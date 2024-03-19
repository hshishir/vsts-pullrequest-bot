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
    public abstract class PullRequestActor : VSTSEventActor
    {
        protected string PullRequestIdString => Id.GetStringId();

        protected int PullRequestId => int.Parse(PullRequestIdString);

        /// <summary>
        /// Initializes a new instance of PullRequestActor
        /// </summary>
        /// <param name="actorService">The Microsoft.ServiceFabric.Actors.Runtime.ActorService that will host this actor instance.</param>
        /// <param name="actorId">The Microsoft.ServiceFabric.Actors.ActorId for this actor instance.</param>
        public PullRequestActor(ActorService actorService, ActorId actorId)
            : base(actorService, actorId)
        {
        }        
    }
}
