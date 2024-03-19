using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Client;
using Microsoft.ServiceFabric.Services.Runtime;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Queue;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PullRequestBot.Common;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NotificationProcessor
{
    public class NotificationProcessorService : StatelessService, IDisposable
    {
        protected ITelemetry Telemetry { get; private set; }

        public SettingStore SettingStore { get; private set; }

        private static readonly TimeSpan MessageProcessingTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan QueuePollingInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MessageVisibilityTimeout = TimeSpan.FromMinutes(3);

        private static List<string> targetBranches = new List<string>()
                    {
                        "refs/heads/master",
                        "refs/heads/lab/",
                        "refs/heads/rel/",
                        "refs/heads/svc/"
                    };

        public NotificationProcessorService(StatelessServiceContext serviceContext)
            : base(serviceContext)
        {
            SettingStore = new SettingStore(serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Settings.Sections["Settings"]);
            Telemetry = new Telemetry(SettingStore.GetSetting("TelemetryInstrumentationKey"));
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            var queue = await GetQueueAsync(cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Telemetry?.ResetProperties();
                Telemetry?.SetProperty("Queue", queue.Name);

                try
                {
                    var message = await queue.GetMessageAsync(MessageProcessingTimeout, options: null, operationContext: null, cancellationToken: cancellationToken);
                    if (message == null)
                    {
                        try
                        {
                            await Task.Delay(QueuePollingInterval, cancellationToken);
                        }
                        catch (TaskCanceledException ex)
                        {
                            if (ex.InnerException != null)
                            {
                                Telemetry?.LogException(ex.InnerException);
                            }
                        }
                        continue;
                    }
                    queue.UpdateMessage(message, MessageVisibilityTimeout, MessageUpdateFields.Visibility);

                    Telemetry?.SetProperty("CloudQueueMessageId", message.Id);
                    Telemetry?.SetProperty("MessageContents", message.AsString);
                    Telemetry?.SetProperty("Actor", GetType().Name);

                    using (var operation = Telemetry?.StartOperation($"{GetType().Name}-ProcessingMessage"))
                    {
                        Telemetry?.LogMessage($"Processing notification {message.Id} from the {queue.Name} queue");

                        if (message.DequeueCount > 5)
                        {
                            Telemetry?.LogMessage($"Notification failed to be processed after 5 attempts, deleting notification");
                            Telemetry?.LogEvent("UnprocessedMessageDeleted");
                            await queue.DeleteMessageAsync(message, cancellationToken);
                            continue;
                        }

                        var success = false;
                        if (success = await ProcessNotificationAsync(queue, message, cancellationToken))
                        {
                            await queue.DeleteMessageAsync(message, cancellationToken);
                        }
                        else
                        {
                            Telemetry?.LogMessage($"Failed to process notification {message.Id} (attempt {message.DequeueCount})");
                        }

                        Telemetry?.LogEvent(success ? "MessageProcessingSucceeded" : "MessageProcessingFailed");
                    }
                }
                catch (Exception e)
                {
                    Telemetry?.LogException(e);
                    await Task.Delay(QueuePollingInterval, cancellationToken);
                }
            }
        }

        private async Task<bool> ProcessNotificationAsync(CloudQueue queue, CloudQueueMessage message, CancellationToken cancellationToken)
        {
            try
            {
                var parsedNotification = JsonConvert.DeserializeObject<ParsedNotification>(message.AsString);

                Telemetry?.SetProperty("EventType", parsedNotification.EventType);
                
                var accountUrl = parsedNotification.Resource.Repository.Url.GetLeftPart(UriPartial.Authority);
                var repositoryId = parsedNotification.Resource.Repository.Id;
                var project = parsedNotification.Resource.Repository.Project.Name;
                var projectId = parsedNotification.Resource.Repository.Project.Id;
                var tasks = new List<Task<bool>>();
                var jsonObject = JObject.Parse(message.AsString);
                var deleteMessage = true;

                if (parsedNotification.EventType.Equals("git.push"))
                {
                    var targetBranchName = parsedNotification.Resource.RefUpdates.First().Name;
                    if (targetBranches.Any(x => targetBranchName.StartsWith(x)))
                    {
                        var actorId = new ActorId(targetBranchName);
                        var targetBranchUpdateActor = ActorProxy.Create<IGitEventActor>(actorId, new Uri("fabric:/PullRequestBot/TargetBranchUpdateActorService"));
                        tasks.Add(targetBranchUpdateActor.ExecuteAsync(message.Id, accountUrl, project, projectId, repositoryId, cancellationToken));
                    }
                }
                else 
                {
                    Telemetry?.SetProperty("PullRequestStatus", parsedNotification.Resource.Status);

                    if (parsedNotification.Resource.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                    {
                        // No work to do for completed PRs
                        return true;
                    }

                    var pullRequestId = parsedNotification.Resource.PullRequestId.ToString();
                    var actorId = new ActorId(pullRequestId);

                    if (parsedNotification.Resource.Status.Equals("abandoned", StringComparison.OrdinalIgnoreCase))
                    {
                        var abandonedActor = ActorProxy.Create<IGitEventActor>(actorId, new Uri("fabric:/PullRequestBot/AbandonActorService"));
                        tasks.Add(abandonedActor.ExecuteAsync(message.Id, accountUrl, project, projectId, repositoryId, cancellationToken));
                    }
                    else
                    {
                        if (parsedNotification.RunAllActors)
                        {
                            var revisionTextWarningActor = ActorProxy.Create<IGitEventActor>(actorId, new Uri("fabric:/PullRequestBot/RevisionTextWarningActorService"));
                            tasks.Add(revisionTextWarningActor.ExecuteAsync(message.Id, accountUrl, project, projectId, repositoryId, cancellationToken));

                            var mergeConflictActor = ActorProxy.Create<IGitEventActor>(actorId, new Uri("fabric:/PullRequestBot/MergeConflictActorService"));
                            tasks.Add(mergeConflictActor.ExecuteAsync(message.Id, accountUrl, project, projectId, repositoryId, cancellationToken));

                            var autoCompleteActor = ActorProxy.Create<IGitEventActor>(actorId, new Uri("fabric:/PullRequestBot/AutoCompleteActorService"));
                            tasks.Add(autoCompleteActor.ExecuteAsync(message.Id, accountUrl, project, projectId, repositoryId, cancellationToken));

                            var generalTasksActor = ActorProxy.Create<IGitEventActor>(actorId, new Uri("fabric:/PullRequestBot/GeneralTasksActorService"));
                            tasks.Add(generalTasksActor.ExecuteAsync(message.Id, accountUrl, project, projectId, repositoryId, cancellationToken));
                            
                            // Disabling the actor, till we design a better experience around Optprof
                            //var optprofWarningActor = ActorProxy.Create<IGitEventActor>(actorId, new Uri("fabric:/PullRequestBot/OptprofWarningActorService"));
                            //tasks.Add(optprofWarningActor.ExecuteAsync(message.Id, accountUrl, project, projectId, repositoryId, cancellationToken));
                        }

                        if (parsedNotification.RunBatmonUrlActor)
                        {
                            var batmonUrlActor = ActorProxy.Create<IGitEventActor>(actorId, new Uri("fabric:/PullRequestBot/BatmonUrlActorService"));
                            tasks.Add(batmonUrlActor.ExecuteAsync(message.Id, accountUrl, project, projectId, repositoryId, cancellationToken));
                        }
                        else
                        {
                            //Adding "InvokeBatmonUrlActor" field to the json message along with preserving all other fields. 
                            jsonObject.Add(new JProperty("RunBatmonUrlActor", true));
                            message.SetMessageContent(JsonConvert.SerializeObject(jsonObject));
                            queue.UpdateMessage(message, MessageVisibilityTimeout, MessageUpdateFields.Content | MessageUpdateFields.Visibility);
                            deleteMessage = false;
                        }
                    }
                }

                // Wait for all independent tasks to finish
                var results = await Task.WhenAll(tasks.ToArray());

                //If all actors ran successfully don't run all actors again in the next run
                if (results.All(x => x))
                {
                    if (!jsonObject.ContainsKey("RunAllActors"))
                    { 
                        jsonObject.Add(new JProperty("RunAllActors", false));
                    }
                    message.SetMessageContent(JsonConvert.SerializeObject(jsonObject));
                    queue.UpdateMessage(message, MessageVisibilityTimeout, MessageUpdateFields.Content | MessageUpdateFields.Visibility);
                }

                return deleteMessage ? results.All(x => x) : false;
            }
            catch (Exception e)
            {
                Telemetry?.LogException(e);
                return false;
            }
        }

        private async Task<CloudQueue> GetQueueAsync(CancellationToken cancellationToken)
        {
            var storageAccessKey = await SettingStore.GetSecretAsync("KeyVaultStorageSecretId", cancellationToken);

            var storageAccountName = SettingStore.GetSetting("StorageAccountName");
            var queueName = SettingStore.GetSetting("QueueName");

            var credentials = new StorageCredentials(storageAccountName, storageAccessKey);
            var storageAccount = new CloudStorageAccount(credentials, useHttps: true);
            var queueClient = storageAccount.CreateCloudQueueClient();
            var queue = queueClient.GetQueueReference(queueName);

            await queue.CreateIfNotExistsAsync(cancellationToken);

            Telemetry?.LogMessage($"Listening for notifications in the {queueName} queue");
            return queue;
        }

        protected override async Task OnCloseAsync(CancellationToken cancellationToken)
        {
            Dispose();
            await base.OnCloseAsync(cancellationToken);
        }

        protected override void OnAbort()
        {
            Dispose();
            base.OnAbort();
        }

        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    SettingStore?.Dispose();
                    SettingStore = null;
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
        #endregion
    }
}
