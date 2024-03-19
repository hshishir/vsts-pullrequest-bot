# Background Pull Request Bot for VSTS
This repo contains the code used to run a background service that processes
Pull Request event notifications from VSTS via Service Hooks. The service can
then perform actions, such as writing a comment back to the Pull Request.

The PR Bot is implemented as a set of Azure Service Fabric stateless services.

# Setup
## Requirement
* Visual Studio with Azure Development
* [Azure Fabric SDK]()

## How to start
* Always open Visual Studio in Administrator mode

# Azure resources
* [PullRequestBot-Telemetry]()
* [VSeng-servicefabric]()
* [StorageAccountQueue]()

# Running locally
* Install the PullRequestBot certificate to your Local Machine cert store. You also need to grant
permissions for Network Service to have access to this cert, which is used when you run the service
locally on the Service Fabric Local Cluster.
  * A base64-encoded version of the certificate is stored in the [VSEng-PullRequestBot]() Key Vault.
  * To grant Network Service full permissions to the cert, first install the cert to your Local Machine
  cert store. Then go to `Control Panel > Manage Computer Certificates > Personal > Certificates`. You
  should see a certificate called `VSEng-PullRequestBot Application`. Right-click on the certificate,
  then go to `All Tasks > Manage Private Keys...`. Add `Network Service` with full control and read
  permissions allowed.
* Set the `PullRequestBot` project as the startup project in Visual Studio. `F5` will run the PR bot
locally on the Service Fabric Local Cluster. By default, it will process messages from the
"revisiontextbot-dev" queue, which has Service Hook subscriptions set up for PRs on the
xyz account.

# Deploying
* The production cluster uses the "revisiontextbot" queue for VS PR notifications, and that setting gets
applied from the `xyz` parameter overrides file at
deployment time.
* You can deploy in VS by right-clicking on the `PullRequestBot` application and selecting `Publish`.
Then, follow the wizard and use the Cloud publish profile at
`src\PullRequestBot\PublishProfiles\Cloud.xml`. This publish profile references the Cloud parameters
file mentioned above, and also contains information about how to connect to the service fabric cluster,
which requires both the cert to be installed to the machine as well as the user to be in the
xyz@microsoft.com security group via AAD.
* The default deployment is a monitored rolling upgrade, which takes about 15-20 minutes to slowly roll
out the update over the 5 nodes in the cluser.

# Manual rollback
If you want to roll back to a previously deployed version after an upgrade has already finished, you can
do so using PowerShell cmdlets.

1) Connect to the cluster:
```
Connect-ServiceFabricCluster -ConnectionEndpoint xyz -AzureActiveDirectory -ServerCertThumbprint xyz
```

2) Find out which versions of the PullRequestBot are available. This can also be seen in the
[Service Fabric Explorer]().
If you use the Service Fabric Explorer, it will warn you that the certificate is not valid. This is by
design, since it is using a self-signed cert. You can proceed to the site anyway (usually
under some advanced menu in your browser on the warning page).
```
Get-ServiceFabricApplicationType -ApplicationTypeName PullRequestBotType
```

2) "Upgrade" the application to a different version (this could be a downgrade as well):
```
Start-ServiceFabricApplicationUpgrade -ApplicationName xyz  -ApplicationTypeVersion <versionNumber> -UnmonitoredAuto
```

Instead of `-UnmonitoredAuto`, you can also do `-Monitored`, which is safer, but much slower.