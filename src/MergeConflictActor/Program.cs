using Microsoft.ServiceFabric.Actors.Runtime;
using PullRequestBot.Common;
using System.Threading;

namespace MergeConflictActor
{
    internal static class Program
    {
        /// <summary>
        /// This is the entry point of the service host process.
        /// </summary>
        private static void Main()
        {
            // This line registers an Actor Service to host your actor class with the Service Fabric runtime.
            // The contents of your ServiceManifest.xml and ApplicationManifest.xml files
            // are automatically populated when you build this project.
            // For more information, see https://aka.ms/servicefabricactorsplatform
            ActorRuntime.RegisterActorAsync<MergeConflictActor>(
                (context, actorType) => new GitEventActorService(context, actorType)).GetAwaiter().GetResult();

            Thread.Sleep(Timeout.Infinite);
        }
    }
}
