using ComponentsJsonMergeDriver;
using CoreXTConfigMerger.Git;
using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.WindowsAzure.Storage.Queue;
using PullRequestBot.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MergeConflictActor
{
    /// <remarks>
    /// This class represents an actor.
    /// Every ActorID maps to an instance of this class.
    /// The StatePersistence attribute determines persistence and replication of actor state:
    ///  - Persisted: State is written to disk and replicated.
    ///  - Volatile: State is kept in memory only and replicated.
    ///  - None: State is kept in memory only and not replicated.
    /// </remarks>
    [StatePersistence(StatePersistence.None)]
    [ActorService(Name = "MergeConflictActorService")]
    internal class MergeConflictActor : PullRequestActor
    {
        private static Regex ComponentsJsonRegex = new Regex(@".*/.corext/configs/.*components.json", RegexOptions.IgnoreCase);

        /// <summary>
        /// Initializes a new instance of MergeConflictActor
        /// </summary>
        /// <param name="actorService">The Microsoft.ServiceFabric.Actors.Runtime.ActorService that will host this actor instance.</param>
        /// <param name="actorId">The Microsoft.ServiceFabric.Actors.ActorId for this actor instance.</param>
        public MergeConflictActor(ActorService actorService, ActorId actorId)
            : base(actorService, actorId)
        {
        }

        protected override async Task<bool> ExecuteInternalAsync(string messageId, string accountUrl, string project, string projectId, Guid repositoryId, CancellationToken cancellationToken)
        {
            Telemetry?.ResetProperties();

            using (var operation = Telemetry?.StartOperation($"ProcessingPullRequestConflicts"))
            {
                var accountUri = new Uri(accountUrl);
                Telemetry?.SetBaseProperties(messageId, repositoryId, accountUri, GetType().Name, pullRequestIdString: PullRequestIdString);

                var gitClient = new GitHttpClient(accountUri, GitEventService.Credentials);

                var pullRequest = await gitClient.GetPullRequestAsync(repositoryId, PullRequestId, cancellationToken: cancellationToken);
                if (pullRequest.Status != PullRequestStatus.Active)
                {
                    return true;
                }

                // First check if we have conflicts that need resolving. Wait for any in-progress merges to finish before checking.
                var mergeStatus = pullRequest.MergeStatus;
                if (mergeStatus == PullRequestAsyncStatus.Queued)
                {
                    mergeStatus = await WaitForCompletedMergeAsync(gitClient, repositoryId, PullRequestId, cancellationToken);
                }

                if (mergeStatus != PullRequestAsyncStatus.Conflicts)
                {
                    return true;
                }

                // Add a comment stating that we are starting to resolve conflicts. This is an attempt to improve the user experience
                // if the conflict resolution takes a few seconds. This comment should let them know that we are doing some work, so
                // they can hold off on their own manual resolution until we finish.
                var progressMessage = "I am currently attempting to resolve the active conflicts. Please wait a few seconds, and I'll let you know when I'm done. Thanks!";
                var progressComment = await AddCommentAsync(progressMessage, gitClient, repositoryId, PullRequestId, cancellationToken: cancellationToken);

                try
                {
                    await ResolveConflictsAsync(gitClient, repositoryId, PullRequestId, cancellationToken);
                }
                finally
                {
                    await gitClient.DeleteCommentAsync(repositoryId, PullRequestId, progressComment.Id, progressComment.Comments[0].Id, cancellationToken: cancellationToken);
                }
            }

            return true;
        }

        private async Task ResolveConflictsAsync(GitHttpClient gitClient, Guid repositoryId, int pullRequestId, CancellationToken cancellationToken)
        {
            // Hack: The VSTS API currently requires a new merge in between most conflict resolutions. This means that if we resolve multiple conflicts,
            // usually only the first one is accepted after a new merge completes. To work around this issue, we want to keep looping over all of the
            // active conflicts while we continue to make progress on resolving them. Once we stop making progress, we can be done. Limit the number of
            // attempts to 15 (i.e. if they have more than 15 active conflicts, this probably won't solve them all).
            var maxResolved = 0;
            var maxUnresolved = 0;
            var attempts = 0;
            while (attempts++ < 15)
            {
                // We don't want to process conflicts while a merge is in progress since the merge will overwrite any
                // custom resolutions when it completes. Wait for any pending merges to finish and then try to
                // resolve any remaining conflicts
                await WaitForCompletedMergeAsync(gitClient, repositoryId, pullRequestId, cancellationToken);

                var conflicts = await gitClient.GetPullRequestConflictsAsync(repositoryId, pullRequestId);
                var unresolvedConflicts = conflicts.Where(x => x.ResolutionStatus != GitResolutionStatus.Resolved).ToList();
                var totalUnresolved = unresolvedConflicts.Count();

                if (totalUnresolved == 0)
                {
                    break;
                }

                maxUnresolved = Math.Max(totalUnresolved, maxUnresolved);

                // We want to resolve the conflicted files in the order -default.config, components.json, vsversion.json, revision.txt.
                // This is based on the processing required to resolve them.
                // We want this order because, ADO may recomputes merge after each resolution and we may have to redo the resolution for some, 
                // so we want to get the difficult ones done first, so that they are accepted easily and then easier ones which we may have to recompute again
                var orderedList = new List<GitConflictEditEdit>();
                
                var activeDefaultConfigConflicts = unresolvedConflicts
                    .Where(x => x.ConflictType == GitConflictType.EditEdit &&
                        x.ConflictPath.EndsWith("/.corext/Configs/default.config", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x as GitConflictEditEdit);
                orderedList.AddRange(activeDefaultConfigConflicts);                
                
                var activeComponentsJsonConflicts = unresolvedConflicts
                    .Where(x => x.ConflictType == GitConflictType.EditEdit &&
                        ComponentsJsonRegex.IsMatch(x.ConflictPath.ToLowerInvariant()))
                    .Select(x => x as GitConflictEditEdit);
                orderedList.AddRange(activeComponentsJsonConflicts);

                var activeRevisionTextConflicts = unresolvedConflicts
                    .Where(x => x.ConflictType == GitConflictType.EditEdit &&
                        Path.GetFileName(x.ConflictPath).Equals("revision.txt", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x as GitConflictEditEdit);
                orderedList.AddRange(activeRevisionTextConflicts);

                var activeVsVersionConficts = unresolvedConflicts
                    .Where(x => x.ConflictType == GitConflictType.EditEdit &&
                        x.ConflictPath.EndsWith("/.corext/Configs/vsversion.json", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x as GitConflictEditEdit);
                orderedList.AddRange(activeVsVersionConficts);

                var resolvedConflicts = new List<GitConflictEditEdit>();
                foreach (var conflict in orderedList)
                {
                    try
                    {
                        string sourceContent = null;
                        string targetContent = null;
                        string baseContent = null;

                        var sourceStream = await gitClient.GetBlobContentAsync(repositoryId, conflict.SourceBlob.ObjectId);
                        using (var reader = new StreamReader(sourceStream))
                        {
                            sourceContent = await reader.ReadToEndAsync();
                        }

                        var targetStream = await gitClient.GetBlobContentAsync(repositoryId, conflict.TargetBlob.ObjectId);
                        using (var reader = new StreamReader(targetStream))
                        {
                            targetContent = await reader.ReadToEndAsync();
                        }

                        var baseStream = await gitClient.GetBlobContentAsync(repositoryId, conflict.BaseBlob.ObjectId);
                        using (var reader = new StreamReader(baseStream))
                        {
                            baseContent = await reader.ReadToEndAsync();
                        }

                        var mergeBytes = null as byte[];
                        if (conflict.ConflictPath.EndsWith("/.corext/Configs/default.config", StringComparison.OrdinalIgnoreCase))
                        {
                            var sourceFile = Path.GetTempFileName();
                            File.WriteAllText(sourceFile, sourceContent);

                            var targetFile = Path.GetTempFileName();
                            File.WriteAllText(targetFile, targetContent);

                            var baseFile = Path.GetTempFileName();
                            File.WriteAllText(baseFile, baseContent);

                            var returnCode = GitMergeTool.Execute(sourceFile, targetFile, baseFile);
                            if (returnCode == 0)
                            {
                                mergeBytes = File.ReadAllBytes(targetFile);
                            }

                            try
                            {
                                File.Delete(sourceFile);
                                File.Delete(targetFile);
                                File.Delete(baseFile);
                            }
                            catch (Exception ex)
                            {
                                Telemetry?.LogException(ex);
                            }                            
                        }
                        else if (ComponentsJsonRegex.IsMatch(conflict.ConflictPath.ToLowerInvariant()))
                        {
                            try
                            {
                                var result = ComponentsJsonMergeDriver.JsonMerger.ExecuteInMemory(sourceContent, targetContent, baseContent, calledByPRBot: true);
                                if (result.Item1 == 0)
                                {
                                    mergeBytes = Encoding.UTF8.GetBytes(result.Item2);
                                }
                            }
                            catch (MergeConflictInImportsException)
                            {
                                Telemetry?.LogMessage($"PR {pullRequestId}: Merge Conflict in the imports section. Conflict Id: {conflict.ConflictId} at {conflict.ConflictPath}");
                                Telemetry?.LogEvent("MergeConflictInImportsSection");
                            }                                                        
                        }
                        else if (Path.GetFileName(conflict.ConflictPath).Equals("revision.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            mergeBytes = resolveRevisionTxtConflict(sourceContent, targetContent, conflict);
                        }
                        else if (conflict.ConflictPath.EndsWith("/.corext/Configs/vsversion.json", StringComparison.OrdinalIgnoreCase))
                        {
                            var result = VSVersionMerger.JsonMerger.ExecuteInMemory(sourceContent, targetContent, baseContent);
                            if (result.Item1 != 0)
                            {
                                mergeBytes = Encoding.UTF8.GetBytes(result.Item2);
                            }
                        }

                        if(mergeBytes != null)
                        {
                            conflict.Resolution = new GitResolutionMergeContent()
                            {
                                MergeType = GitResolutionMergeType.UserMerged,
                                UserMergedContent = mergeBytes,
                            };
                            conflict.ResolutionStatus = GitResolutionStatus.Resolved;
                            resolvedConflicts.Add(conflict);
                        }                        
                    }
                    catch (Exception e)
                    {
                        // Log the error and continue processing other conflicts. All of the conflict resolution is best-effort.
                        Telemetry?.LogMessage($"PR {pullRequestId}: Failed to resolve conflict {conflict.ConflictId} at {conflict.ConflictPath}: {e}");
                        Telemetry?.LogException(e);
                    }
                }

                // Try to update all of the conflicts as close together in time as possible to help win a race condition on the VSTS side related
                // to when a new merge is kicked off after resolutions are updated.
                var numResolved = 0;
                foreach (var conflict in resolvedConflicts)
                {
                    try
                    {
                        Telemetry?.LogMessage($"PR {pullRequestId}: Auto-resolving conflict {conflict.ConflictId} at {conflict.ConflictPath}");
                        var result = await gitClient.UpdatePullRequestConflictAsync(conflict, repositoryId, pullRequestId, conflict.ConflictId);

                        if (result.ResolutionStatus == GitResolutionStatus.Resolved)
                        {
                            numResolved++;
                            Telemetry?.LogEvent($"{Path.GetFileNameWithoutExtension(conflict.ConflictPath)}ConflictResolved");
                        }
                        else
                        {
                            Telemetry?.LogMessage($"PR {pullRequestId}: Failed to resolve conflict {result.ConflictId} at {result.ConflictPath}. Current status: {result.ResolutionStatus}");
                            Telemetry?.LogEvent($"{Path.GetFileNameWithoutExtension(conflict.ConflictPath)}ResolutionFailed");
                        }
                    }
                    catch (VssServiceException e) when (e.Message.StartsWith("TF401181:"))
                    {
                        // "TF401181: The pull request cannot be edited due to its state."
                        // The PR has been abandoned while we were resolving conflicts. Stop processing, and do not log this as a real error.
                        Telemetry?.LogMessage($"PR {pullRequestId}: Abandoned while conflict resolution was in progress. Aborting.");
                        return;
                    }
                    catch (Exception e)
                    {
                        // Log the error and continue processing other conflicts. All of the conflict resolution is best-effort.
                        Telemetry?.LogMessage($"PR {pullRequestId}: Failed to resolve conflict {conflict.ConflictId} at {conflict.ConflictPath}: {e}");
                        Telemetry?.LogException(e);
                    }
                }

                maxResolved = Math.Max(numResolved, maxResolved);
                if (numResolved == 0)
                {
                    break;
                }
            }

            if (maxUnresolved == 0)
            {
                // We never had any conflicts?
                return;
            }

            try
            {
                if (maxResolved > 0)
                {
                    var remaining = maxUnresolved - maxResolved;
                    var message = remaining > 0 ?
                        $"I have auto-resolved {maxResolved} out of {maxUnresolved} conflicts. You'll need to manually resolve the remaining {remaining} conflicts." :
                        $"I have auto-resolved all of the conflicts. ({maxResolved} out of {maxUnresolved})";
                    var status = remaining > 0 ? CommentThreadStatus.Active : CommentThreadStatus.Fixed;

                    await AddCommentAsync(message, gitClient, repositoryId, pullRequestId, status, allowDuplicates: true, cancellationToken: cancellationToken);
                    Telemetry?.LogEvent("ConflictResolutionCommentAdded");
                }
                else
                {
                    // We want to inform the user that we could not resolve any conflicts. This will prevent users from waiting for the PR bot to resolve
                    // conflicts automatically if they really need to be resolved manually. For this comment, we also want to ensure that we only write
                    // it once.
                    var message = "I am not able to auto-resolve any of the active conflicts. Please manually resolve all remaining conflicts.";
                    await AddCommentAsync(message, gitClient, repositoryId, pullRequestId, cancellationToken: cancellationToken);
                    Telemetry?.LogEvent("ManualConflictResolutionCommentAdded");
                }
            }
            catch (Exception e)
            {
                Telemetry?.LogMessage($"PR {pullRequestId}: Failed to write resolution status comment: {e}");
                Telemetry?.LogException(e);
            }
        }

        private async Task<PullRequestAsyncStatus> WaitForCompletedMergeAsync(GitHttpClient gitClient, Guid repositoryId, int pullRequestId, CancellationToken cancellationToken)
        {
            while (true)
            {
                var pullRequest = await gitClient.GetPullRequestAsync(repositoryId, pullRequestId, cancellationToken: cancellationToken);
                if (pullRequest.MergeStatus != PullRequestAsyncStatus.Queued)
                {
                    // Merge is not currently running. Return merge result
                    return pullRequest.MergeStatus;
                }

                // Merge is in progress. Wait 1 second and poll again
                await Task.Delay(millisecondsDelay: 1000, cancellationToken: cancellationToken);
            }
        }

        private byte[] resolveRevisionTxtConflict(string sourceContent, string targetContent, GitConflictEditEdit conflict)
        {
            var sourceLine = sourceContent.Split('\n').ElementAt(0);
            var targetLine = targetContent.Split('\n').ElementAt(0);

            Telemetry?.LogMessage($"PR {PullRequestId}: Conflict at path {conflict.ConflictPath}: Source content: {sourceLine}");
            Telemetry?.LogMessage($"PR {PullRequestId}: Conflict at path {conflict.ConflictPath}: Target content: {targetLine}");

            var sourceRevision = int.Parse(sourceLine);
            var targetRevision = int.Parse(targetLine);
            var mergeResult = Math.Max(sourceRevision, targetRevision) + 1;

            Telemetry?.LogMessage($"PR {PullRequestId}: Conflict at path {conflict.ConflictPath}: Source: {sourceRevision}, Target: {targetRevision}, Merged: {mergeResult}");

            // Story 586197: Go live with PR bot revision.txt changes
            // The new file format includes a GUID on the next line following the revision.
            // Resolution simply injects a new GUID value every time we increment the revision.
            // (GitResolutionMergeContent replaces the entire file content.)
            // Use \n instead of \r\n because the Git repository stores line endings normalized as unix line endings.
            var mergeBytes = Encoding.UTF8.GetBytes($"{mergeResult}\n{Guid.NewGuid()}\n");

            return mergeBytes;
        }

        private static async Task<GitPullRequestCommentThread> AddCommentAsync(string message, GitHttpClient gitClient, Guid repositoryId, int pullRequestId, CommentThreadStatus status = CommentThreadStatus.Active, bool allowDuplicates = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var comment = new Comment()
            {
                Content = message,
                CommentType = CommentType.Text,
            };

            if (!allowDuplicates)
            {
                var existingThread = await GetThreadWithCommentAsync(comment, gitClient, repositoryId, pullRequestId, cancellationToken);
                if (existingThread != null)
                {
                    // We have already written a comment with this same message before. Return the existing one instead of writing a duplicate message.
                    return existingThread;
                }
            }

            var commentThread = new GitPullRequestCommentThread()
            {
                Comments = new[] { comment },
                Status = status,
            };

            return await gitClient.CreateThreadAsync(commentThread, repositoryId, pullRequestId, cancellationToken: cancellationToken);
        }

        private static async Task<GitPullRequestCommentThread> GetThreadWithCommentAsync(Comment comment, GitHttpClient gitClient, Guid repositoryId, int pullRequestId, CancellationToken cancellationToken)
        {
            var commentThreads = await gitClient.GetThreadsAsync(repositoryId, pullRequestId, cancellationToken: cancellationToken);
            return commentThreads.FirstOrDefault(x => x.Comments?.FirstOrDefault()?.Content == comment.Content);
        }
    }
}
