using System;

namespace PullRequestBot.Common
{
    public interface ITelemetry
    {
        void LogEvent(string eventName);
        void LogException(Exception exception);
        void LogMessage(string message);
        void LogError(string errorMessage);
        void ResetProperties();
        void SetProperty(string key, string value);
        IDisposable StartOperation(string operationName);
    }
}