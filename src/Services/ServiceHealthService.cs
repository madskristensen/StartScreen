using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models;

namespace StartScreen.Services
{
    /// <summary>
    /// Reads the Visual Studio status feed. Unknown results must not replace a UI's
    /// last known snapshot without also displaying that snapshot as stale.
    /// </summary>
    internal static class ServiceHealthService
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        internal const string StatusApiUrl = "https://status.visualstudio.microsoft.com/api/status";

        public static Task<ServiceHealthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return GetStatusAsync(_httpClient, cancellationToken);
        }

        internal static async Task<ServiceHealthStatus> GetStatusAsync(HttpClient client, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(StatusApiUrl, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return ParseStatus(json, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;

                return new ServiceHealthStatus(ServiceHealthState.Unknown,
                    "Unable to refresh Visual Studio service status. Any previously displayed status is stale. Open the status page for current information.",
                    DateTimeOffset.UtcNow, null);
            }
        }

        internal static ServiceHealthStatus ParseStatus(string json, DateTimeOffset checkedAtUtc)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("services", out var services)
                || services.ValueKind != JsonValueKind.Array || services.GetArrayLength() == 0
                || !root.TryGetProperty("queriedTimestamp", out var timestamp)
                || timestamp.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var updatedAtUtc)
                || updatedAtUtc > checkedAtUtc.AddMinutes(5))
            {
                throw new FormatException("The Visual Studio status feed is missing valid services or a timestamp.");
            }

            var incidents = new List<string>();
            foreach (var service in services.EnumerateArray())
            {
                var name = GetRequiredString(service, "name");
                var incident = HasIncident(service);
                if (service.TryGetProperty("geographies", out var geographies))
                {
                    if (geographies.ValueKind != JsonValueKind.Array)
                        throw new FormatException("The Visual Studio status feed has invalid geographies.");

                    foreach (var geography in geographies.EnumerateArray())
                        incident |= HasIncident(geography);
                }

                if (incident)
                    incidents.Add(name);
            }

            var state = incidents.Count > 0 ? ServiceHealthState.Incident : ServiceHealthState.Healthy;
            var details = incidents.Count > 0
                ? "Affected Visual Studio services: " + string.Join(", ", incidents) + "."
                : "No incidents reported for Visual Studio services. This does not report GitHub public API health.";
            if (checkedAtUtc - updatedAtUtc > TimeSpan.FromMinutes(15))
                details = "Stale status - " + details;

            return new ServiceHealthStatus(state, details, checkedAtUtc, updatedAtUtc.ToUniversalTime());
        }

        private static bool HasIncident(JsonElement item)
        {
            var state = GetRequiredString(item, "state");
            bool incident;
            switch (state.ToLowerInvariant())
            {
                case "resolved":
                case "healthy":
                case "operational":
                case "available":
                    incident = false;
                    break;
                case "active":
                case "investigating":
                case "identified":
                case "monitoring":
                case "degraded":
                case "unhealthy":
                case "outage":
                    incident = true;
                    break;
                default:
                    throw new FormatException("The Visual Studio status feed contains an unrecognized service state.");
            }

            // Healthy entries in the live feed omit severity entirely.
            if (item.TryGetProperty("severity", out var severity) && severity.ValueKind != JsonValueKind.Null)
            {
                if (severity.ValueKind == JsonValueKind.Number && severity.TryGetInt32(out var level) && level >= 0)
                    incident |= level > 0;
                else if (severity.ValueKind == JsonValueKind.String)
                {
                    switch (severity.GetString().ToLowerInvariant())
                    {
                        case "none":
                        case "healthy":
                        case "0":
                            break;
                        case "warning":
                        case "error":
                        case "critical":
                        case "advisory":
                        case "1":
                        case "2":
                        case "3":
                        case "4":
                            incident = true;
                            break;
                        default:
                            throw new FormatException("The Visual Studio status feed contains an unrecognized severity.");
                    }
                }
                else
                    throw new FormatException("The Visual Studio status feed contains an invalid severity.");
            }

            return incident;
        }

        private static string GetRequiredString(JsonElement item, string propertyName)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new FormatException("The Visual Studio status feed is missing " + propertyName + ".");
            }

            return value.GetString();
        }
    }
}