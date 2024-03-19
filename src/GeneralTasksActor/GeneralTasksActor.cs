using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AzureDevOpsAPI;
using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using PullRequestBot.Common;

namespace GeneralTasksActor
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
    [ActorService(Name = "GeneralTasksActorService")]
    internal class GeneralTasksActor : PullRequestActor
    {
        private static List<Regex> TargetBranches = new List<Regex>
                                {
                                    new Regex(@"refs/heads/lab/.+stg"),
                                    new Regex(@"refs/heads/lab/vscore"),
                                    new Regex(@"refs/heads/lab/vseng"),
                                    new Regex(@"refs/heads/rel/.+"),
                                    new Regex(@"refs/heads/master")
                                };
        private static string RPSPolicyName = "**CloudBuild - Request RPS**";
        /// <summary>
        /// Initializes a new instance of GeneralTasksActor
        /// </summary>
        /// <param name="actorService">The Microsoft.ServiceFabric.Actors.Runtime.ActorService that will host this actor instance.</param>
        /// <param name="actorId">The Microsoft.ServiceFabric.Actors.ActorId for this actor instance.</param>
        public GeneralTasksActor(ActorService actorService, ActorId actorId)
            : base(actorService, actorId)
        {
        }

        protected override async Task<bool> ExecuteInternalAsync(string messageId, string accountUrl, string project, string projectId, Guid repositoryId, CancellationToken cancellationToken)
        {
            Telemetry?.ResetProperties();
            using (var operation = Telemetry?.StartOperation($"ProcessingToAddStartRPSComment"))
            {
                var accountUri = new Uri(accountUrl);
                Telemetry?.SetBaseProperties(messageId, repositoryId, accountUri, GetType().Name, pullRequestIdString: PullRequestIdString);

                try
                {
                    var account = accountUrl.ExtractAccountName();
                    if (account == null)
                    {
                        Telemetry?.LogError("UnableToParseAccountUrl");
                        return false;
                    }
                    GlobalSettings.CredentialType = CredentialType.VssPatCredential;
                    GlobalSettings.PersonalAccessToken = GitEventService.AccessToken;
                    var evaluations = await PullRequestAPI.GetEvaluationsAsync(account, project, projectId, PullRequestId);
                    if (!evaluations.Any(x => x.configuration?.settings?.displayName?.ToLower().Contains("rps") == true))
                    {
                        Telemetry?.LogEvent("NoRPSPolicyForPR");
                        return true;
                    }

                    var pullRequest = PullRequestAPI.GetPullRequestInformation(account, repositoryId, PullRequestId);
                    if (TargetBranches.Any(x => x.IsMatch(pullRequest.TargetRefName)))
                    {
                        var latestIteration = PullRequestAPI.GetLatestIteration(account, repositoryId, PullRequestId);
                        var manualResolutionComment = $"If you decide to resolve this comment manually without running the policy, you must provide strong justification in the comment below and get a sign-off from your team’s designated performance champion.";
                        var firstComment = $"Please validate performance impact of your changes by queuing {RPSPolicyName} policy. {manualResolutionComment}";
                        var thread = null as GitPullRequestCommentThread;
                        if (latestIteration.Id == 1)
                        {
                            thread = PullRequestAPI.GetThreadWithComment(account, repositoryId, PullRequestId, firstComment);
                            if (thread == null)
                            {
                                PullRequestAPI.CreateNewThread(account, repositoryId, PullRequestId, firstComment, GitEventService.AccessToken);
                                Telemetry?.LogEvent($"TriggerRPSCommentAdded");
                            }
                        }
                        else
                        {
                            var changeList = PullRequestAPI.GetChangesInIteration(account, repositoryId, PullRequestId, latestIteration.Id.Value);
                            var changedPaths = changeList.ChangeEntries.Select(x => x?.Item?.Path).Where(x => !string.IsNullOrEmpty(x)).ToList();
                            if (changedPaths.Any(x => x.Contains("/.corext/Configs/")))
                            {
                                var updateLink = $"https://dev.azure.com/{account}/{project}/_git/VS/pullrequest/{PullRequestId}?_a=files&iteration={latestIteration.Id}&base={latestIteration.Id - 1}";
                                var updateComment =  $"[Update {latestIteration.Id}]({updateLink})";
                                var comment = $"Change under .corext/Configs discovered in {updateComment}.  Please run the {RPSPolicyName} policy. {manualResolutionComment}";
                                thread = PullRequestAPI.GetThreadWithComment(account, repositoryId, PullRequestId, comment);
                                if (thread == null)
                                {
                                    PullRequestAPI.CreateNewThread(account, repositoryId, PullRequestId, comment, GitEventService.AccessToken);
                                    Telemetry?.LogEvent($"ReTriggerRPSCommentAdded");
                                }

                                // Close Older comments, if present
                                thread = PullRequestAPI.GetThreadWithComment(account, repositoryId, PullRequestId, firstComment);
                                if (thread != null)
                                {
                                    PullRequestAPI.CloseCommentThread(account, project, repositoryId, PullRequestId, thread.Id);
                                }
                                for (var i = 2; i < latestIteration.Id; i++)
                                {
                                    updateLink = $"https://dev.azure.com/{account}/{project}/_git/VS/pullrequest/{PullRequestId}?_a=files&iteration={i}&base={i - 1}";
                                    updateComment = $"[Update {i}]({updateLink})";
                                    comment = $"Change under .corext/Configs discovered in {updateComment}.  Please run the {RPSPolicyName} policy. {manualResolutionComment}";
                                    thread = PullRequestAPI.GetThreadWithComment(account, repositoryId, PullRequestId, comment);
                                    if (thread != null)
                                    {
                                        PullRequestAPI.CloseCommentThread(account, project, repositoryId, PullRequestId, thread.Id);
                                    }
                                }
                            }
                        }
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
    }
}
