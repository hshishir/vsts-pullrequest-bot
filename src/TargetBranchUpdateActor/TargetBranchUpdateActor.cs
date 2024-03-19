using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Client;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.WindowsAzure.Storage.Queue;
using PullRequestBot.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TargetBranchUpdateActor
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
    [ActorService(Name = "TargetBranchUpdateActorService")]
    internal class TargetBranchUpdateActor : PushEventActor
    {
        /// <summary>
        /// Initializes a new instance of TargetBranchUpdateActor
        /// </summary>
        /// <param name="actorService">The Microsoft.ServiceFabric.Actors.Runtime.ActorService that will host this actor instance.</param>
        /// <param name="actorId">The Microsoft.ServiceFabric.Actors.ActorId for this actor instance.</param>
        public TargetBranchUpdateActor(ActorService actorService, ActorId actorId)
            : base(actorService, actorId)
        {
        }
        
        protected override async Task<bool> ExecuteInternalAsync(string messageId, string accountUrl, string project, string projectId, Guid repositoryId, CancellationToken cancellationToken)
        {
            Telemetry?.ResetProperties();
            using (var operation = Telemetry?.StartOperation($"ProcessingGitPushEvent"))
            {
                var accountUri = new Uri(accountUrl);
                Telemetry?.SetBaseProperties(messageId, repositoryId, accountUri, GetType().Name, pushToBranch: BranchName);

                var gitClient = new GitHttpClient(accountUri, GitEventService.Credentials);
                var pullRequestSearchCriteria = new GitPullRequestSearchCriteria();
                pullRequestSearchCriteria.TargetRefName = BranchName;
                var pullRequestsToBranch = await gitClient.GetPullRequestsAsync(repositoryId, pullRequestSearchCriteria, cancellationToken: cancellationToken);

                if (!pullRequestsToBranch.Any())
                {
                    Telemetry?.LogEvent($"NoPullRequestsToBranch");
                    return true;
                }

                var tasks = new List<Task<bool>>();
                foreach (var pullRequest in pullRequestsToBranch)
                {
                    var pullRequestId = pullRequest.PullRequestId.ToString();
                    var actorId = new ActorId(pullRequestId);

                    var mergeConflictActor = ActorProxy.Create<IGitEventActor>(actorId, new Uri("fabric:/PullRequestBot/MergeConflictActorService"));
                    tasks.Add(mergeConflictActor.ExecuteAsync(messageId, accountUrl, project: null, projectId: null, repositoryId: repositoryId, cancellationToken: cancellationToken));

                    var autoCompleteActor = ActorProxy.Create<IGitEventActor>(actorId, new Uri("fabric:/PullRequestBot/AutoCompleteActorService"));
                    tasks.Add(autoCompleteActor.ExecuteAsync(messageId, accountUrl, project: null, projectId: null, repositoryId: repositoryId, cancellationToken: cancellationToken));
                }

                Telemetry?.LogEvent($"TargetBranchPRsProcessed");

                var results = await Task.WhenAll(tasks);

                return results.All(x => x);
            }            
        }
    }
}