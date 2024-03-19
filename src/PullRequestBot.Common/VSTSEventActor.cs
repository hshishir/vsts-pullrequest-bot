using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullRequestBot.Common
{
    /// <summary>
    /// Derive from the default Service Fabric <see cref="Actor"/> class to provide some additional
    /// features that the PR Bot actors can leverage:
    /// 1) Instantiate a unique <see cref="ITelemetry"/> instance for this actor to report all telemetry
    /// through.
    /// 2) Provide access to the underlying <see cref="GitEventActorService"/>, which exposes some
    /// additional features that are useful to the actors. Note that any implementation of this class must
    /// be invoked/run via a <see cref="GitEventActorService"/> rather than a generic <see cref="ActorService"/>
    /// since some of this functionality of this class depends on the <see cref="GitEventActorService"/>
    /// features.
    /// </summary>
    /// <remarks>
    /// This class represents an actor.
    /// Every ActorID maps to an instance of this class.
    /// The StatePersistence attribute determines persistence and replication of actor state:
    ///  - Persisted: State is written to disk and replicated.
    ///  - Volatile: State is kept in memory only and replicated.
    ///  - None: State is kept in memory only and not replicated.
    /// </remarks>
    [StatePersistence(StatePersistence.None)]
    public abstract class VSTSEventActor : Actor, IGitEventActor
    {
        protected ITelemetry Telemetry { get; private set; }

        protected GitEventActorService GitEventService { get; private set; }

        /// <summary>
        /// Initializes a new instance of PullRequestActor
        /// </summary>
        /// <param name="actorService">The Microsoft.ServiceFabric.Actors.Runtime.ActorService that will host this actor instance.</param>
        /// <param name="actorId">The Microsoft.ServiceFabric.Actors.ActorId for this actor instance.</param>
        public VSTSEventActor(ActorService actorService, ActorId actorId)
            : base(actorService, actorId)
        {
        }

        /// <summary>
        /// This method is called whenever an actor is activated.
        /// An actor is activated the first time any of its methods are invoked.
        /// </summary>
        protected override Task OnActivateAsync()
        {
            return Task.Run(() =>
            {
                GitEventService = ActorService as GitEventActorService;
                if (GitEventService == null)
                {
                    throw new InvalidOperationException($"The {nameof(PullRequestActor)} should only be invoked from a {nameof(GitEventActorService)}.");
                }

                var instrumentationKey = GitEventService.SettingStore.GetSetting("TelemetryInstrumentationKey");
                Telemetry = new Telemetry(instrumentationKey);
            });
        }

        public async Task<bool> ExecuteAsync(string messageId, string accountUrl, string project, string projectId, Guid repositoryId, CancellationToken cancellationToken)
        {
            try
            {
                return await ExecuteInternalAsync(messageId, accountUrl, project, projectId, repositoryId, cancellationToken);
            }
            catch (Exception e)
            {
                Telemetry?.LogException(e);
                return false;
            }
        }

        protected abstract Task<bool> ExecuteInternalAsync(string messageId, string accountUrl, string project, string projectId, Guid repositoryId, CancellationToken cancellationToken);
    }
}
