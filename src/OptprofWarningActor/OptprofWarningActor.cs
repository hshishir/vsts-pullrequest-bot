using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using PullRequestBot.Common;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OptprofWarningActor
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
    [ActorService(Name = "OptprofWarningActorService")]
    internal class OptprofWarningActor : PullRequestActor
    {
        /// <summary>
        /// Initializes a new instance of OptprofWarningActor
        /// </summary>
        /// <param name="actorService">The Microsoft.ServiceFabric.Actors.Runtime.ActorService that will host this actor instance.</param>
        /// <param name="actorId">The Microsoft.ServiceFabric.Actors.ActorId for this actor instance.</param>
        public OptprofWarningActor(ActorService actorService, ActorId actorId)
            : base(actorService, actorId)
        {
        }

        protected override async Task<bool> ExecuteInternalAsync(string messageId, string accountUrl, string project, string projectId, Guid repositoryId, CancellationToken cancellationToken)
        {
            Telemetry?.ResetProperties();

            using (var operation = Telemetry?.StartOperation($"ProcessingOptprofWarning"))
            {
                var accountUri = new Uri(accountUrl);
                Telemetry?.SetBaseProperties(messageId, repositoryId, accountUri, GetType().Name, pullRequestIdString: PullRequestIdString);

                try { 
                    // Get the list of proj files changed in the PR, Check if any of them have <Optprof in them, if yes add a comment asking the user to manually run optprof tests
                    var gitClient = new GitHttpClient(accountUri, GitEventService.Credentials);
                    var iterations = await gitClient.GetPullRequestIterationsAsync(repositoryId, PullRequestId, cancellationToken: cancellationToken);
                    var latestIterationChanges = await gitClient.GetPullRequestIterationChangesAsync(repositoryId, PullRequestId, iterations.Count, top: 100000, compareTo: iterations.Count - 1, cancellationToken: cancellationToken);
                    var changedObjectIds = latestIterationChanges
                        .ChangeEntries
                        .Where(x => x?.Item?.Path?.EndsWith("proj") ?? false)
                        .Select(x => x?.Item?.ObjectId)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();

                    if (!changedObjectIds.Any())
                    {
                        Telemetry?.LogEvent("NoProjFilesChanged");
                        return true;
                    }

                    var needOptprofWarning = false;
                    foreach (var changedObjectId in changedObjectIds)
                    {
                        var objectStreamBlob = await gitClient.GetBlobContentAsync(repositoryId, changedObjectId);
                        using (var reader = new StreamReader(objectStreamBlob))
                        {
                            var changedContent = reader.ReadToEnd();
                            if (changedContent.ToLower().Contains("<optprof"))
                            {
                                needOptprofWarning = true;
                                break;
                            }
                        }
                    }

                    if (!needOptprofWarning)
                    {
                        Telemetry?.LogEvent("DoesNotNeedOptprofWarning");
                        return true;
                    }

                    var account = accountUrl.ExtractAccountName();
                    if (account == null)
                    {
                        Telemetry?.LogError("UnableToParseAccountUrl");
                        return false;
                    }

                    var latestIteration = iterations.Last();
                    var updateLink = $"https://dev.azure.com/{account}/{project}/_git/VS/pullrequest/{PullRequestId}?_a=files&iteration={latestIteration.Id}&base={latestIteration.Id - 1}";
                    var updateComment = $"[Update {latestIteration.Id}]({updateLink})";

                    var stepsLink = $"http://aka.ms/optprof/runlocally";
                    var stepsComment = $"[here]({stepsLink})";

                    var content = $"{updateComment} contains changes to one or more project files that use OptProf. Please review manual testing steps {stepsComment}, if your change impacts OptProf.";
                    await AddCommentAsync(content, gitClient, repositoryId, PullRequestId, CommentThreadStatus.Active, allowDuplicates: false, cancellationToken: cancellationToken);
                    Telemetry?.LogEvent("RunOptprofCommentAdded");

                    // Closing older comments on the PR about running Optprof, if we add a new comment to run optprof
                    var iteration = null as GitPullRequestIteration;
                    var comment = null as Comment;
                    var existingThread = null as GitPullRequestCommentThread;
                    var threadUpdate = new GitPullRequestCommentThread
                    {
                        Status = CommentThreadStatus.Closed
                    };

                    for (var i=1; i< iterations.Count; i++)
                    {
                        iteration = iterations.ElementAt(i);
                        updateLink = $"https://dev.azure.com/{account}/{project}/_git/VS/pullrequest/{PullRequestId}?_a=files&iteration={iteration.Id}&base={iteration.Id - 1}";
                        updateComment = $"[Update {iteration.Id}]({updateLink})";
                        content = $"{updateComment} contains changes to one or more project files that use OptProf. Please review manual testing steps {stepsComment}, if your change impacts OptProf.";

                        comment = new Comment()
                        {
                            Content = content,
                            CommentType = CommentType.Text,
                        };
                        existingThread = await GetThreadWithCommentAsync(comment, gitClient, repositoryId, PullRequestId, cancellationToken);
                        if (existingThread != null)
                        {
                            await gitClient.UpdateThreadAsync(threadUpdate, repositoryId, PullRequestId, existingThread.Id);
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

        private static async Task<GitPullRequestCommentThread> AddCommentAsync(string content, GitHttpClient gitClient, Guid repositoryId, int pullRequestId, CommentThreadStatus status = CommentThreadStatus.Active, bool allowDuplicates = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var comment = new Comment()
            {
                Content = content,
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
