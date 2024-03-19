using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.VisualStudio.Services.Common;
using System;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;

namespace PullRequestBot.Common
{
    /// <summary>
    /// This service overrides the default Service Fabric <see cref="ActorService"/> to provide two main features
    /// for our actor implementations:
    /// 1) Instantiates and provides access to the VSTS <see cref="Credentials"/> for use in all VSTS API calls
    /// 2) Provides access to a <see cref="SettingStore"/> which can load configuration settings and secrets from
    /// KeyVault for use by the actor at runtime.
    /// </summary>
    public class GitEventActorService : ActorService, IDisposable
    {
        public VssCredentials Credentials { get; private set; }

        public SettingStore SettingStore { get; private set; }

        public string AccessToken { get; private set; }
        public GitEventActorService(StatefulServiceContext context, ActorTypeInformation actorTypeInfo)
            : base(context, actorTypeInfo)
        {
            SettingStore = new SettingStore(context.CodePackageActivationContext.GetConfigurationPackageObject("Config").Settings.Sections["Settings"]);
        }

        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            AccessToken = await SettingStore.GetSecretAsync("KeyVaultPersonalAccessTokenSecretId");
            Credentials = new VssBasicCredential(string.Empty, AccessToken);

            await base.RunAsync(cancellationToken);
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
