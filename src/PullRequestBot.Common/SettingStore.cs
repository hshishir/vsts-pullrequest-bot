using Microsoft.Azure.KeyVault;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using System;
using System.Fabric.Description;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace PullRequestBot.Common
{
    public class SettingStore
    {
        private ConfigurationSection ConfigurationSettings { get; set; }

        private KeyVaultClient keyVaultClient;

        private KeyVaultClient KeyVaultClient
        {
            get
            {
                if (keyVaultClient == null)
                {
                    var clientId = GetSetting("KeyVaultClientId");
                    var thumbprint = GetSetting("KeyVaultCertThumbprint");
                    var certificate = FindCertificateByThumbprint(thumbprint);
                    var assertionCert = new ClientAssertionCertificate(clientId, certificate);

                    keyVaultClient = new KeyVaultClient((authority, resource, scope) => GetAccessTokenAsync(authority, resource, scope, assertionCert));
                }

                return keyVaultClient;
            }
        }

        public SettingStore(ConfigurationSection settings)
        {
            ConfigurationSettings = settings;
        }

        public string GetSetting(string key)
        {
            return ConfigurationSettings.Parameters[key].Value;
        }

        public async Task<string> GetSecretAsync(string settingName, CancellationToken cancellationToken = default(CancellationToken))
        {
            var secretId = GetSetting(settingName);
            var result = await KeyVaultClient.GetSecretAsync(secretId, cancellationToken);
            return result.Value;
        }

        /// <summary>
        /// Gets the access token
        /// </summary>
        /// <param name="authority"> Authority </param>
        /// <param name="resource"> Resource </param>
        /// <param name="scope"> scope </param>
        /// <returns> token </returns>
        private static async Task<string> GetAccessTokenAsync(string authority, string resource, string scope, ClientAssertionCertificate assertionCert)
        {
            var context = new AuthenticationContext(authority, TokenCache.DefaultShared);
            var result = await context.AcquireTokenAsync(resource, assertionCert);
            return result.AccessToken;
        }

        /// <summary>
        /// Helper function to load an X509 certificate
        /// </summary>
        /// <param name="certificateThumbprint">Thumbprint of the certificate to be loaded</param>
        /// <returns>X509 Certificate</returns>
        private static X509Certificate2 FindCertificateByThumbprint(string certificateThumbprint)
        {
            if (certificateThumbprint == null)
            {
                throw new ArgumentNullException(nameof(certificateThumbprint));
            }

            var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            try
            {
                store.Open(OpenFlags.ReadOnly);
                var collection = store.Certificates.Find(X509FindType.FindByThumbprint, certificateThumbprint, false); // Don't validate certs, since the test root isn't installed.
                if (collection == null || collection.Count == 0)
                {
                    throw new Exception($"Could not find the certificate with thumbprint {certificateThumbprint} in the Local Machine's Personal certificate store.");
                }

                return collection[0];
            }
            finally
            {
                store.Close();
            }
        }

        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    keyVaultClient?.Dispose();
                    keyVaultClient = null;
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
