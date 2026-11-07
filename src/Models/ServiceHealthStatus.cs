using System;

namespace StartScreen.Models
{
    internal enum ServiceHealthState
    {
        Unknown,
        Healthy,
        Incident
    }

    /// <summary>
    /// A snapshot of Visual Studio service health, not GitHub public API health.
    /// </summary>
    internal sealed class ServiceHealthStatus
    {
        public ServiceHealthState State { get; }
        public string StatusText
        {
            get
            {
                var text = State switch
                {
                    ServiceHealthState.Healthy => "Service operational",
                    ServiceHealthState.Incident => "Visual Studio service incident",
                    _ => "Visual Studio service status unknown"
                };
                return State != ServiceHealthState.Unknown && IsStale ? "Stale - " + text : text;
            }
        }
        public string Details { get; }
        public DateTimeOffset CheckedAtUtc { get; }
        public DateTimeOffset? LastUpdatedUtc { get; }
        public bool IsStale => !LastUpdatedUtc.HasValue
            || DateTimeOffset.UtcNow - LastUpdatedUtc.Value > TimeSpan.FromMinutes(15);
        public string StatusPageUrl => "https://vsstatus.visualstudio.com/";

        internal ServiceHealthStatus(ServiceHealthState state, string details,
            DateTimeOffset checkedAtUtc, DateTimeOffset? lastUpdatedUtc)
        {
            State = state;
            Details = details;
            CheckedAtUtc = checkedAtUtc;
            LastUpdatedUtc = lastUpdatedUtc;
        }
    }
}