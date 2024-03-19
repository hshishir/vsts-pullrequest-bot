using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using PullRequestBot.Common;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCompleteActor
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
    [ActorService(Name = "AutoCompleteActorService")]
    internal class AutoCompleteActor : PullRequestActor
    {
        /// <summary>
        /// Initializes a new instance of AutoCompleteActor
        /// </summary>
        /// <param name="actorService">The Microsoft.ServiceFabric.Actors.Runtime.ActorService that will host this actor instance.</param>
        /// <param name="actorId">The Microsoft.ServiceFabric.Actors.ActorId for this actor instance.</param>
        public AutoCompleteActor(ActorService actorService, ActorId actorId)
            : base(actorService, actorId)
        {
        }

        protected override async Task<bool> ExecuteInternalAsync(string messageId, string accountUrl, string project, string projectId, Guid repositoryId, CancellationToken cancellationToken)
        {
            Telemetry?.ResetProperties();

            using (var operation = Telemetry?.StartOperation($"ProcessingPullRequestAutoComplete"))
            {
                var accountUri = new Uri(accountUrl);
                Telemetry?.SetBaseProperties(messageId, repositoryId, accountUri, GetType().Name, pullRequestIdString: PullRequestIdString);

                var gitClient = new GitHttpClient(accountUri, GitEventService.Credentials);
                var pullRequest = await gitClient.GetPullRequestAsync(repositoryId, PullRequestId, cancellationToken: cancellationToken);

                if (pullRequest.Status != PullRequestStatus.Active)
                {
                    // We only need to set auto-complete on PRs that are active
                    return true;
                }

                if (pullRequest.AutoCompleteSetBy != null)
                {
                    // Auto-complete is already set, we don't need to try to re-enable it
                    return true;
                }

                var gitRefs = await gitClient.GetBranchRefsAsync(pullRequest.Repository.Id);
                if (gitRefs.Any(x => x.IsLocked && x.Name == pullRequest.TargetRefName))
                {
                    // Branch is locked; can't set auto-complete on a locked branch
                    Telemetry?.LogEvent($"AutoCompleteBranchLocked");
                    return true;
                }

                // Check if auto-complete has already been set in the past, and if it was canceled by VSTS instead of a user
                var threads = await gitClient.GetThreadsAsync(repositoryId, PullRequestId, cancellationToken: cancellationToken);
                var systemComments = threads.Where(x => x.Comments[0].CommentType == CommentType.System)
                                            .Select(x => x.Comments[0].Content)
                                            .ToArray();

                // We want to re-enable auto-complete if a user had previously requested it, but has not yet asked for it to be canceled.
                // VSTS will sometimes cancel auto-complete silently if the merge fails or if the branch is locked, etc.
                // You can differentiate user-cancelation from system-cancelation based on the content of the system comment messages.
                // User cancelation contains a message saying the user is canceling auto-complete. System cancelation does not have this message.
                // The lack of a cancelation message can be used to infer that system cancelation took place.

                // First, check to see whether auto-complete was ever enabled. If not, we don't need to re-enable it.
                var autoCompleteTurnedOnCount = systemComments.Count(x => x.EndsWith("set auto-complete"));
                if (autoCompleteTurnedOnCount == 0)
                {
                    return true;
                }

                // Special case to avoid setting auto-complete over and over if something is broken on the backend where it will always fail.
                // Do not re-enable auto-complete if it has already been turned on more than 20 times in this PR.
                if (autoCompleteTurnedOnCount > 20)
                {
                    Telemetry?.LogEvent($"AutoCompleteFailsafeTriggered");
                    return true;
                }

                // Now, look at the system messages in reverse-chronological order. If the last message relating to auto-complete is about a user
                // turning on auto-complete instead of turning off auto-complete, but we know that auto-complete is not currently set, then it means
                // that VSTS silently canceled auto-complete, and we need to re-enable it.
                for (var i = systemComments.Length - 1; i >= 0; i--)
                {
                    if (systemComments[i].EndsWith("set auto-complete"))
                    {
                        // Create VssConnection object to find VSTS User Id for the credentials we are using
                        var connection = new VssConnection(accountUri, GitEventService.Credentials);

                        // Re-enable auto-complete
                        var autoCompleteSettings = new GitPullRequest
                        {
                            AutoCompleteSetBy = new IdentityRef()
                            {
                                Id = connection.AuthorizedIdentity.Id.ToString(),
                            },
                        };

                        await gitClient.UpdatePullRequestAsync(autoCompleteSettings, repositoryId, PullRequestId, cancellationToken: cancellationToken);

                        Telemetry?.LogEvent($"AutoCompleteReenabled");
                        return true;
                    }
                    else if (systemComments[i].EndsWith("cancelled auto-complete"))
                    {
                        // User asked for auto-complete to be turned off. Do not re-enable it.
                        return true;
                    }
                }

                // We should enver get here. If we see this event, we need to investigate the
                // PR that triggered it and update this code to handle that case.
                Telemetry?.LogException(new InvalidOperationException($"AutoCompleteActor got to an unexpected state for PR {PullRequestId}"));
                return false;
            }
        }
    }
}
