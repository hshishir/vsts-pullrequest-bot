using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using PullRequestBot.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RevisionTextWarningActor
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
    [ActorService(Name = "RevisionTextWarningActorService")]
    internal class RevisionTextWarningActor : PullRequestActor
    {
        /// <summary>
        /// Initializes a new instance of RevisionTextWarningActor
        /// </summary>
        /// <param name="actorService">The Microsoft.ServiceFabric.Actors.Runtime.ActorService that will host this actor instance.</param>
        /// <param name="actorId">The Microsoft.ServiceFabric.Actors.ActorId for this actor instance.</param>
        public RevisionTextWarningActor(ActorService actorService, ActorId actorId)
            : base(actorService, actorId)
        {
        }

        protected override async Task<bool> ExecuteInternalAsync(string messageId, string accountUrl, string project, string projectId, Guid repositoryId, CancellationToken cancellationToken)
        {
            Telemetry?.ResetProperties();

            using (var operation = Telemetry?.StartOperation($"ProcessingRevisionTextWarning"))
            {
                var accountUri = new Uri(accountUrl);
                Telemetry?.SetBaseProperties(messageId, repositoryId, accountUri, GetType().Name, pullRequestIdString: PullRequestIdString);

                var gitClient = new GitHttpClient(accountUri, GitEventService.Credentials);
                var iterations = await gitClient.GetPullRequestIterationsAsync(repositoryId, PullRequestId, cancellationToken: cancellationToken);
                var latestIterationChanges = await gitClient.GetPullRequestIterationChangesAsync(repositoryId, PullRequestId, iterations.Count, top: 100000, cancellationToken: cancellationToken);
                var validPaths = latestIterationChanges
                    .ChangeEntries
                    .Select(x => x?.Item?.Path)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToList();

                try
                {
                    var hasRevisionTextFile = validPaths.Any(x => Path.GetFileName(x).Equals("revision.txt", StringComparison.OrdinalIgnoreCase));

                    var loggingMessage = hasRevisionTextFile ? "Found at least one revision.txt file" : "No revision.txt file found";
                    Telemetry?.LogMessage($"Processed {accountUri.Authority} PR {PullRequestId}: {loggingMessage}");

                    var comment = new Comment()
                    {
                        Content = @"Your PR does not contain an update to any ```revision.txt``` files. If this change affects any shipping Visual Studio product packages, you should __increment the counter__ in the corresponding ```revision.txt``` file(s) as part of this change. If this PR does not affect any VS product, you can disregard this warning.

See [http://aka.ms/revision.txt](http://aka.ms/revision.txt) for more details.",
                        CommentType = CommentType.Text,
                    };

                    var existingThread = await GetThreadWithCommentAsync(comment, gitClient, repositoryId, PullRequestId, cancellationToken);
                    if (hasRevisionTextFile)
                    {
                        if (existingThread == null || existingThread.Status != CommentThreadStatus.Active || existingThread.IsDeleted)
                        {
                            Telemetry?.LogEvent("RevisionTextWarningNotNeeded");
                        }
                        else
                        {
                            // Resolve our previous comment thread now that the issue has been resolved
                            var updatedThread = new GitPullRequestCommentThread()
                            {
                                Status = CommentThreadStatus.Fixed,
                                Comments = new[]
                                {
                                    new Comment()
                                    {
                                        Content = "I noticed that you added some revision.txt files with a recent update. Thanks!",
                                        CommentType = CommentType.Text,
                                    },
                                },
                            };

                            await gitClient.UpdateThreadAsync(updatedThread, repositoryId, PullRequestId, existingThread.Id, cancellationToken: cancellationToken);
                            Telemetry?.LogEvent("RevisionTextWarningFixed");
                        }
                    }
                    else
                    {
                        // Evaluate files as of the latest source commit in the PR
                        var sourceCommit = iterations.Single(x => x.Id == iterations.Count).SourceRefCommit;

                        // We don't have a revision.txt file included in this PR. Do we need one?
                        var needRevisionText = await RequiresRevisionUpdate(validPaths, sourceCommit, repositoryId, gitClient, cancellationToken);
                        if (needRevisionText)
                        {
                            if (existingThread == null)
                            {
                                var newThread = new GitPullRequestCommentThread()
                                {
                                    Comments = new[] { comment },
                                    Status = CommentThreadStatus.Active,
                                };

                                await gitClient.CreateThreadAsync(newThread, repositoryId, PullRequestId, cancellationToken: cancellationToken);
                                Telemetry?.LogEvent("RevisionTextWarningAdded");
                            }
                            else
                            {
                                Telemetry?.LogEvent("RevisionTextWarningAlreadyExists");
                            }
                            
                        }
                        else
                        {
                            if (existingThread == null || existingThread.Status != CommentThreadStatus.Active || existingThread.IsDeleted)
                            {
                                Telemetry?.LogMessage($"Processed {accountUri.Authority} PR {PullRequestId}: No relevant changes that would require a revision.txt file");
                                Telemetry?.LogEvent("RevisionTextWarningNotNeeded");
                            }
                            else
                            {
                                // A comment already exists, but we determined that we don't need it anymore? Go ahead and close it for people
                                var updatedThread = new GitPullRequestCommentThread()
                                {
                                    Status = CommentThreadStatus.Fixed,
                                    Comments = new[]
                                    {
                                        new Comment()
                                        {
                                            Content = "It looks like you no longer need a revision.txt update with your recent changes",
                                            CommentType = CommentType.Text,
                                        },
                                    },
                                };

                                await gitClient.UpdateThreadAsync(updatedThread, repositoryId, PullRequestId, existingThread.Id, cancellationToken: cancellationToken);
                                Telemetry?.LogEvent("RevisionTextWarningFixed");
                            }
                        }
                        
                    }
                }
                catch (Exception e)
                {
                    Telemetry?.LogException(e);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Given a list of changes in a pull request iteration, determine whether the PR should contain a revision.txt update.
        /// </summary>
        /// <param name="paths">The list of file changes in the PR</param>
        /// <param name="commit">The commit to use for looking at files in the tree</param>
        /// <param name="repositoryId">The repository where the commit is located</param>
        /// <param name="gitClient">The VSTS Git client to use for API calls</param>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>True if the PR should contain a revision.txt update, false otherwise.</returns>
        /// <remarks>
        /// The idea is the following:
        /// Assume people are using updateRevisions.txt files to describe when revision.txt needs to be updated.
        /// If part of the tree does not contain an updateRevisions.txt file in the hierarchy, such as when using the
        /// implictRevisions.txt system, then we do not need to write a revision.txt warning.
        /// 
        /// Algorithm:
        /// Take all changes in the PR, and walk up the tree from each one at the commit at the tip of the PR branch, looking for updateRevisions.txt. If we find one,
        /// require the PR to contain a corresponding revision.txt update. Cache directory lookup results along the way to save
        /// time and reduce API calls to VSTS.
        /// </remarks>
        private async Task<bool> RequiresRevisionUpdate(List<string> paths, GitCommitRef commit, Guid repositoryId, GitHttpClient gitClient, CancellationToken cancellationToken)
        {
            // First, special case for default.config which does not use updateRevisions.txt, but still sometimes requires revision.txt updates
            if (paths.Contains("/.corext/Configs/default.config", StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            // Now, perform the scan for updateRevisions.txt files in the part of the tree that has been changed
            foreach (var directory in paths.GetUniqueParentGitDirectories())
            {
                if (await DirectoryContainsUpdateRevisionsFile(directory, commit, repositoryId, gitClient, cancellationToken))
                {
                    // Note: We do not parse updateRevisions.txt or try to understand it, so this doesn't handle the case where we exclude package updates deeper in the tree,
                    // such as by putting an empty updateRevisions.txt file in a directory. We will end up considering that this change should have a revision.txt entry.
                    Telemetry?.LogMessage($"Found updateRevisions.txt file in directory {directory} of repository {repositoryId}. Need to include revision.txt update.");
                    return true;
                }
            }

            return false;
        }

        private static async Task<bool> DirectoryContainsUpdateRevisionsFile(string directory, GitCommitRef commit, Guid repositoryId, GitHttpClient gitClient, CancellationToken cancellationToken)
        {
            var versionDescriptor = new GitVersionDescriptor()
            {
                Version = commit.CommitId,
                VersionType = GitVersionType.Commit
            };
            var files = await gitClient.GetItemsAsync(repositoryId, directory, VersionControlRecursionType.OneLevel, versionDescriptor: versionDescriptor, cancellationToken: cancellationToken);

            return files.Any(x => !x.IsFolder && x.Path != null && x.Path.EndsWith("/updateRevisions.txt", StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<GitPullRequestCommentThread> GetThreadWithCommentAsync(Comment comment, GitHttpClient gitClient, Guid repositoryId, int pullRequestId, CancellationToken cancellationToken)
        {
            var commentThreads = await gitClient.GetThreadsAsync(repositoryId, pullRequestId, cancellationToken: cancellationToken);
            return commentThreads.FirstOrDefault(x => x.Comments?.FirstOrDefault()?.Content == comment.Content);
        }
    }
}
