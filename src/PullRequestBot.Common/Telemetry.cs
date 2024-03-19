using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using System;
using System.Collections.Generic;

namespace PullRequestBot.Common
{
    public class Telemetry : ITelemetry
    {
        /// <summary>
        /// The set of key-value pairs to send along with any telemetry for a given notification.
        /// </summary>
        protected IDictionary<string, string> TelemetryProperties { get; private set; }

        private TelemetryClient Client { get; set; }

        public Telemetry(string instrumentationKey = null)
        {
            if (instrumentationKey == null)
            {
                Client = new TelemetryClient();
            }
            else
            {
                var configuration = TelemetryConfiguration.CreateDefault();
                configuration.InstrumentationKey = instrumentationKey;
                Client = new TelemetryClient(configuration);
            }

            TelemetryProperties = new Dictionary<string, string>();
        }

        public void SetProperty(string key, string value)
        {
            TelemetryProperties[key] = value;
        }

        public void ResetProperties()
        {
            TelemetryProperties.Clear();
        }

        public IDisposable StartOperation(string operationName)
        {
            return Client.StartOperation<RequestTelemetry>(operationName);
        }

        public void LogMessage(string message)
        {
            Client.TrackTrace(message, TelemetryProperties);
        }

        public void LogError(string errorMessage)
        {
            Client.TrackTrace(errorMessage, SeverityLevel.Error, TelemetryProperties);
        }

        public void LogEvent(string eventName)
        {
            Client.TrackEvent(eventName, TelemetryProperties);
        }

        public void LogException(Exception exception)
        {
            Client.TrackException(exception, TelemetryProperties);
        }
    }
}
