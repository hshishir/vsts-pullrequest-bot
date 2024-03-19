using System;

namespace PullRequestBot.Common
{
    public static class TelemetryExtensions
    {
        public static void SetBaseProperties(this ITelemetry telemetry, string messageId, Guid repoId, Uri accountUri, string actor, string pullRequestIdString = null, string pushToBranch = null)
        {
            telemetry.SetProperty("CloudQueueMessageId", messageId);
            telemetry.SetProperty("RepositoryId", repoId.ToString());
            telemetry.SetProperty("Account", accountUri.Authority);
            telemetry.SetProperty("Actor", actor);

            if (pullRequestIdString != null)
            {
                telemetry.SetProperty("PullRequestId", pullRequestIdString);
            }
            if (pushToBranch != null)
            {
                telemetry.SetProperty("PushToBranch", pushToBranch);
            }
        }
    }
}
