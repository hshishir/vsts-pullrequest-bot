using System.Threading;
using System.Threading.Tasks;

namespace PullRequestBot.Common
{
    public interface INotificationProcessor
    {
        Task<bool> ExecuteAsync(string notification, CancellationToken cancellationToken);

        void InitializeTelemetry(ITelemetry telemetry);
    }
}