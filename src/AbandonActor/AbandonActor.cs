using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Microsoft.WindowsAzure.Storage.Queue;
using PullRequestBot.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AbandonActor
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
    [ActorService(Name = "AbandonActorService")]
    internal class AbandonActor : PullRequestActor
    {
        /// <summary>
        /// Initializes a new instance of AbandonActor
        /// </summary>
        /// <param name="actorService">The Microsoft.ServiceFabric.Actors.Runtime.ActorService that will host this actor instance.</param>
        /// <param name="actorId">The Microsoft.ServiceFabric.Actors.ActorId for this actor instance.</param>
        public AbandonActor(ActorService actorService, ActorId actorId)
            : base(actorService, actorId)
        {
        }

        protected override async Task<bool> ExecuteInternalAsync(string messageId, string accountUrl, string project, string projectId, Guid repositoryId, CancellationToken cancellationToken)
        {
            Telemetry?.ResetProperties();

            using (var operation = Telemetry.StartOperation("ClosingBugsFromAbandonedPullRequest"))
            {
                var accountUri = new Uri(accountUrl);
                Telemetry?.SetBaseProperties(messageId, repositoryId, accountUri, GetType().Name, pullRequestIdString: PullRequestIdString);
                Telemetry?.LogEvent("PullRequestAbandoned");

                try
                {
                    var bugIdList = await CloseOpenBugsAsync(accountUri, PullRequestId, cancellationToken);
                    if (bugIdList.Count > 0)
                    {
                        await PostClosingBugsCommentAsync(accountUri, repositoryId, PullRequestId, bugIdList, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    Telemetry?.LogException(ex);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Query opened bugs belong to pull request, and close them.
        /// </summary>
        /// <param name="accountUri">The VSTS account</param>
        /// <param name="pullRequestId">The Pull Request Id</param>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>The list of bug Ids that we detect and closed.</returns>
        private async Task<List<int>> CloseOpenBugsAsync(Uri accountUri, int pullRequestId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var witClient = new WorkItemTrackingHttpClient(accountUri, GitEventService.Credentials);

            Telemetry?.LogMessage($"Query and close opened bugs for pull request: {pullRequestId}");

            var openBugs = await QueryOpenBugsForPullRequest(witClient, pullRequestId);
            Telemetry?.LogMessage($"Found {openBugs.Count} bug(s) for pull request: {pullRequestId}");

            if (openBugs.Count > 0)
            {
                Telemetry?.SetProperty("BugsCount", openBugs.Count.ToString());
                Telemetry?.LogMessage($"Bug Ids list: '{string.Join(",", openBugs)}'");

                var resolvedBugs = await ResolveBugs(witClient, pullRequestId, openBugs, cancellationToken);
                if (resolvedBugs.Count < openBugs.Count)
                {
                    Telemetry?.LogError($"Cannot resolve some or all bugs for PullRequest: '{pullRequestId}'.");
                    Telemetry?.LogMessage($"Resolved Bug Ids list: '{string.Join(",", resolvedBugs)}'");
                    Telemetry?.LogMessage($"Not Resolved Bug Ids list: '{string.Join(",", openBugs.Except(resolvedBugs))}'");
                }
                Telemetry?.LogEvent("PullRequestBugsResolved");

                var closedBugs = await CloseBugs(witClient, pullRequestId, resolvedBugs, cancellationToken);
                if (closedBugs.Count < resolvedBugs.Count)
                {
                    Telemetry?.LogError($"Cannot close some or all bugs for PullRequest: '{pullRequestId}'.");
                    Telemetry?.LogMessage($"Closed Bug Ids list: '{string.Join(",", closedBugs)}'");
                    Telemetry?.LogMessage($"Not Closed Bug Ids list: '{string.Join(",", resolvedBugs.Except(closedBugs))}'");
                }
                Telemetry?.LogEvent("PullRequestBugsClosed");

                return closedBugs;
            }

            return new List<int>();
        }

        /// <summary>
        /// Query opened bugs of a pull request by pull request Id.
        /// </summary>
        /// <param name="witClient">WorkItemTrackingHttpClient client</param>
        /// <param name="pullRequestId">The Pull Request Id</param>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>The list of bug Ids for the pull request</returns>
        private async Task<List<int>> QueryOpenBugsForPullRequest(WorkItemTrackingHttpClient witClient, int pullRequestId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var wiql = new Wiql()
            {
                Query = $@"
SELECT [System.Id]
FROM WorkItems 
WHERE [System.WorkItemType] = 'Bug' AND [Build Number] = 'Pull Request {pullRequestId}' AND State != 'Closed' AND [Title] CONTAINS 'Check:'"
            };

            var queryResult = await witClient.QueryByWiqlAsync(wiql, cancellationToken: cancellationToken);
            return queryResult.WorkItems.Select(wid => wid.Id).ToList();
        }

        /// <summary>
        /// Resolve all the bugs by their Ids.
        /// </summary>
        /// <param name="witClient">WorkItemTrackingHttpClient client</param>
        /// <param name="pullRequestId">The Pull Request Id</param>
        /// <param name="bugIds">The list of bug Ids to resolve</param>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>The list of bugIds that were successfully resovled</returns>
        private async Task<List<int>> ResolveBugs(WorkItemTrackingHttpClient witClient, int pullRequestId, List<int> bugIds, CancellationToken cancellationToken = default(CancellationToken))
        {
            var resolveBugTasks = new List<Task<WorkItem>>();
            foreach (var bugId in bugIds)
            {
                var jsonPatch = new JsonPatchDocument()
                {
                    new JsonPatchOperation() { Path = "/fields/System.State", Value = "Resolved", Operation = Operation.Add },
                    new JsonPatchOperation() { Path = "/fields/Microsoft.DevDiv.ResolutionBug", Value = "Not Repro", Operation = Operation.Add },
                    new JsonPatchOperation() { Path = "/fields/System.History", Value = $"PullRequest {pullRequestId} was abandoned.", Operation = Operation.Add }
                };
                resolveBugTasks.Add(witClient.UpdateWorkItemAsync(jsonPatch, bugId, cancellationToken: cancellationToken));
            }

            var resolvedWorkitems = await Task.WhenAll(resolveBugTasks.ToArray());
            return resolvedWorkitems.Where(wit => wit.Id.HasValue).Select(wit => wit.Id.Value).ToList();
        }

        /// <summary>
        /// Close all bugs by their Ids.
        /// </summary>
        /// <param name="witClient">WorkItemTrackingHttpClient client</param>
        /// <param name="pullRequestId">The Pull Request Id</param>
        /// <param name="bugIds">The list of bug Ids to resolve</param>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>The list of bugIds that were successfully closed</returns>
        private async Task<List<int>> CloseBugs(WorkItemTrackingHttpClient witClient, int pullRequestId, List<int> bugIds, CancellationToken cancellationToken = default(CancellationToken))
        {
            var closeBugTasks = new List<Task<WorkItem>>();
            foreach (var bugId in bugIds)
            {
                var jsonPatch = new JsonPatchDocument()
                {
                    new JsonPatchOperation() { Path = "/fields/System.State", Value = "Closed", Operation = Operation.Add }
                };
                closeBugTasks.Add(witClient.UpdateWorkItemAsync(jsonPatch, bugId, cancellationToken: cancellationToken));
            }

            var closedWorkitems = await Task.WhenAll(closeBugTasks.ToArray());
            return closedWorkitems.Where(wit => wit.Id.HasValue).Select(wit => wit.Id.Value).ToList();
        }

        /// <summary>
        /// Post a comment to the pull request to notify users that their bugs are closed.
        /// </summary>
        /// <param name="accountUri">The VSTS account</param>
        /// <param name="repositoryId">The repository Id</param>
        /// <param name="pullRequestId">The Pull Request Id</param>
        /// <param name="bugIdList">The list of all the bugs to notify user</param>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns></returns>
        private async Task PostClosingBugsCommentAsync(Uri accountUri, Guid repositoryId, int pullRequestId, List<int> bugIdList, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (bugIdList.Count > 0)
            {
                var bugListText = string.Join(Environment.NewLine, bugIdList.Select(id => $"#{id}"));
                var comment = new Comment()
                {
                    Content = $"Closing {bugIdList.Count} PRCheck bug(s) because this pull request is abandoned. Bugs:" + Environment.NewLine + bugListText,
                    CommentType = CommentType.Text,
                };

                var commentThread = new GitPullRequestCommentThread()
                {
                    Comments = new[] { comment },
                    Status = CommentThreadStatus.Closed,
                };

                var gitClient = new GitHttpClient(accountUri, GitEventService.Credentials);
                await gitClient.CreateThreadAsync(commentThread, repositoryId, pullRequestId, cancellationToken: cancellationToken);
            }
        }
    }
}
