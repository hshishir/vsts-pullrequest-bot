using Microsoft.ServiceFabric.Actors;
using Microsoft.WindowsAzure.Storage.Queue;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullRequestBot.Common
{
    /// <summary>
    /// This interface defines the methods exposed by an actor.
    /// Clients use this interface to interact with the actor that implements it.
    /// </summary>
    public interface IGitEventActor : IActor
    {
        /// <summary>
        /// Processes a pull request from a given account/repository. The pull request Id is a
        /// string ActorId.
        /// </summary>
        Task<bool> ExecuteAsync(string messageId, string accountUrl, string project, string projectId, Guid repositoryId, CancellationToken cancellationToken);
    }
}
