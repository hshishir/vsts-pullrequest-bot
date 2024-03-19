using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AzureDevOpsAPI;
using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.WindowsAzure.Storage.Queue;
using PullRequestBot.Common;

namespace BatmonUrlActor
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
    [ActorService(Name = "BatmonUrlActorService")]
    internal class BatmonUrlActor : PullRequestActor
    {
        private const int CloudBuildPRDefinitionId = 9186;
        private const int CloudBuildPRYamlDefinitionId = 10310;
        private static Regex CloudBuildTaskNamePattern = new Regex(@"CloudBuild-[a-z0-9]{8}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{12}");

        /// <summary>
        /// Initializes a new instance of BatmonUrlActor
        /// </summary>
        /// <param name="actorService">The Microsoft.ServiceFabric.Actors.Runtime.ActorService that will host this actor instance.</param>
        /// <param name="actorId">The Microsoft.ServiceFabric.Actors.ActorId for this actor instance.</param>
        public BatmonUrlActor(ActorService actorService, ActorId actorId)
            : base(actorService, actorId)
        {
        }

        protected override async Task<bool> ExecuteInternalAsync(string messageId, string accountUrl, string project, string projectId, Guid repositoryId, CancellationToken cancellationToken)
        {
            Telemetry?.ResetProperties();
            using (var operation = Telemetry?.StartOperation($"ProcessingToAddBatmonUrl"))
            {
                var accountUri = new Uri(accountUrl);

                Telemetry?.SetBaseProperties(messageId, repositoryId, accountUri, GetType().Name, pullRequestIdString: PullRequestIdString);
                Telemetry?.SetProperty("Project", project);
                Telemetry?.SetProperty("ProjectId", projectId);

                try
                {
                    var account = accountUrl.ExtractAccountName();
                    if (account == null)
                    {
                        Telemetry?.LogEvent($"UnableToParseAccountUrl");
                        return true;
                    }

                    GlobalSettings.CredentialType = CredentialType.VssPatCredential;
                    GlobalSettings.PersonalAccessToken = GitEventService.AccessToken;

                    var evaluations = await PullRequestAPI.GetEvaluationsAsync(account, project, projectId, PullRequestId);

                    var success = AddBatmonUrlCommentForDefinition(account, project, repositoryId, evaluations, CloudBuildPRDefinitionId);
                    success &= AddBatmonUrlCommentForDefinition(account, project, repositoryId, evaluations, CloudBuildPRYamlDefinitionId);

                    return success;
                }
                catch (Exception ex)
                {
                    Telemetry?.LogException(ex);
                    return false;
                }
            }
        }

        private bool AddBatmonUrlCommentForDefinition(string account, string project, Guid repositoryId, IEnumerable<PullRequestEvaluation> evaluations, int definitionId)
        {
            var definitionName = definitionId == CloudBuildPRDefinitionId ? "CloudBuildPRDefinition" : "CloudBuildPRYamlDefinition";
            var evaluation = evaluations.Where(x => x.configuration.settings.buildDefinitionId == definitionId).FirstOrDefault();
            if (evaluation == null)
            {
                Telemetry?.LogEvent($"{definitionName}NotTriggered");
                return true; // The PR does not trigger Cloudbuild-PR that would queue the Cloudbuild
            }

            if (!evaluation.status.Equals("running") && !evaluation.status.Equals("approved"))
            {
                Telemetry?.LogEvent($"{definitionName}BuildNotRun");
                return true; //The policy may be optional and the build related to the policy is not running. 
            }

            var cloudBuildTaskName = GetCloudBuildTaskName(account, project, evaluation.context.buildId);
            if (cloudBuildTaskName == null)
            {
                Telemetry?.LogEvent($"{definitionName}FailedToQueue");
                return false; //The Cloudbuild-PR build failed before reaching CloudBuild Agentless task.
            }

            var policyName = evaluation.configuration.settings.displayName;

            var cloudBuildId = cloudBuildTaskName.Replace("CloudBuild-", "");
            var batmonUrl = $"https://b/build?id={cloudBuildId}";
            var batmonUrlComment = $"[CloudBuild Batmon Link]({batmonUrl})";

            var updateComment = GetUpdateComment(account, project, repositoryId, evaluation.context.buildId);

            var comment = $"Use {batmonUrlComment} to view the build automatically queued by the **{policyName}** policy for {updateComment}";
            
            var thread = PullRequestAPI.GetThreadWithComment(account, repositoryId, PullRequestId, comment);
            if (thread == null)
            {
                PullRequestAPI.CreateNewThread(account, repositoryId, PullRequestId, comment, GitEventService.AccessToken, status: CommentThreadStatus.Closed);
                Telemetry?.LogEvent($"BatmonUrlAddedFor{definitionName}");
            }

            return true;
        }

        private string GetUpdateComment(string account, string project, Guid repositoryId, int buildId)
        {
            var build = BuildAPI.GetBuild(account, project, buildId);
            var sourceCommitId = build.ParametersDictionary["system.pullRequest.sourceCommitId"];
            var iterations = PullRequestAPI.GetIterations(account, repositoryId, PullRequestId);
            var iteration = iterations.Where(x => x.SourceRefCommit.CommitId.Equals(sourceCommitId)).First();
            var updateLink = $"https://dev.azure.com/devdiv/DevDiv/_git/VS/pullrequest/{PullRequestId}?_a=files&iteration={iteration.Id}&base={iteration.Id - 1}";
            return $"[Update {iteration.Id}]({updateLink})";
        }

        private string GetCloudBuildTaskName(string account, string project, int buildId, int retryCount = 5)
        {
            var timeline = BuildAPI.GetBuildTimeline(account, project, buildId);
            var cloudbuildTaskRecord = timeline.Where(x => CloudBuildTaskNamePattern.Match(x.Name).Success).FirstOrDefault();
            if (cloudbuildTaskRecord != null)
            {
                return cloudbuildTaskRecord.Name;
            }

            return null;
        }
    }
}
