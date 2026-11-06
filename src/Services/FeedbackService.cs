using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.Services.DevHub;

namespace StartScreen.Services
{
    internal sealed class FeedbackService
    {
        private const string _baseUrl = "https://sendvsfeedback2.azurewebsites.net/api/";
        private const int _pageSize = 500;
        private const int _concurrency = 4;
        private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        private static readonly Regex _htmlTags = new Regex("<[^>]+>", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        internal static FeedbackService Instance { get; } = new FeedbackService();
        private readonly HttpClient _client;
        private readonly Func<CancellationToken, Task<AccessToken>> _getTokenAsync;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private FeedbackSnapshot _cached;
        private string _cachedCredentialHash;

        internal FeedbackService(HttpClient client = null,
            Func<CancellationToken, Task<AccessToken>> getTokenAsync = null, Func<DateTime> utcNow = null)
        {
            _client = client ?? _httpClient;
            _getTokenAsync = getTokenAsync ?? GetVisualStudioTokenAsync;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal async Task<FeedbackSnapshot> GetTicketsAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (forceRefresh)
                    _cached = null;
                AccessToken token = await _getTokenAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(token.Token) || token.ExpiresOn <= new DateTimeOffset(_utcNow()))
                    throw new UnauthorizedAccessException("Visual Studio did not return a valid Developer Community credential.");

                string credentialHash;
                using (var hash = SHA256.Create())
                    credentialHash = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(token.Token)));
                // The current credential must match before a cache hit can skip identity HTTP requests.
                if (HasFreshCache() && _cachedCredentialHash == credentialHash)
                    return _cached;

                using JsonDocument metadata = await SendAsync("userProperties/metadata", token.Token, null, cancellationToken);
                long principalId = ReadId(metadata.RootElement.GetProperty("Id"));
                // Confirm identity before reusing data, including after a VS account switch.
                if (_cached?.PrincipalId != principalId)
                    _cached = null;
                if (HasFreshCache())
                {
                    _cachedCredentialHash = credentialHash;
                    return _cached;
                }

                var ids = new HashSet<long>();
                long read = 0;
                long total;
                var page = 1;
                do
                {
                    using JsonDocument activity = await SendAsync("userProperties/getActivity", token.Token,
                        new { page, pageSize = _pageSize }, cancellationToken);
                    total = activity.RootElement.GetProperty("totalCount").GetInt64();
                    JsonElement activities = activity.RootElement.GetProperty("activities");
                    if (total < 0 || (activities.GetArrayLength() == 0 && read < total))
                        throw new InvalidDataException("Developer Community returned incomplete activity paging data.");

                    foreach (JsonElement action in activities.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var kind = action.GetProperty("activity").GetString();
                        if (kind == "posted" || kind == "voted")
                            ids.Add(ReadId(action.GetProperty("rootPostId")));
                    }
                    read += activities.GetArrayLength();
                    page++;
                }
                while (read < total);

                var tickets = new List<FeedbackTicket>();
                var candidates = ids.ToArray();
                for (var offset = 0; offset < candidates.Length; offset += _concurrency)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FeedbackTicket[] batch = await Task.WhenAll(candidates.Skip(offset).Take(_concurrency)
                        .Select(id => GetTicketAsync(id, principalId, token.Token, cancellationToken)));
                    tickets.AddRange(batch.Where(ticket => ticket != null));
                }

                cancellationToken.ThrowIfCancellationRequested();
                _cached = new FeedbackSnapshot(principalId,
                    tickets.OrderByDescending(ticket => ticket.UpdatedUtc).ThenByDescending(ticket => ticket.Id).ToArray(), _utcNow());
                _cachedCredentialHash = credentialHash;
                return _cached;
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool HasFreshCache() => _cached != null && _utcNow() >= _cached.UpdatedUtc
            && _utcNow() - _cached.UpdatedUtc < TimeSpan.FromHours(1);

        private async Task<FeedbackTicket> GetTicketAsync(long id, long principalId, string token, CancellationToken cancellationToken)
        {
            var body = new
            {
                version = 3,
                searchFor = new { version = 20220110, title = id.ToString(CultureInfo.InvariantCulture), tags = Array.Empty<object>() },
                skip = 0,
                take = 1,
                filter = "_item",
                typesFilterFlags = 3,
                clientVersion = "1.0.121",
                capabilities = new { canDisplayTypedMarkdown = true, rawTextSupport = true },
            };
            using JsonDocument response = await SendAsync("searchquery/item", token, body, cancellationToken);
            JsonElement results = response.RootElement.GetProperty("results");
            // Deleted or no-longer-accessible tickets are intentionally omitted by search.
            if (results.GetArrayLength() == 0)
                return null;
            if (results.GetArrayLength() != 1)
                throw new InvalidDataException("Developer Community returned multiple results for one ticket.");

            JsonElement item = results[0];
            if (ReadId(item.GetProperty("answerHubId")) != id)
                throw new InvalidDataException("Developer Community returned a different ticket than requested.");
            var created = ReadId(item.GetProperty("createdByPrincipalId")) == principalId;
            var voted = item.GetProperty("hasVoted").GetBoolean();
            // Activity includes canceled votes and comment reactions, not just current root-post upvotes.
            if (!created && !voted)
                return null;

            var url = item.GetProperty("communityUrl").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps
                || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
                || !(uri.Host.Equals("developercommunity.visualstudio.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("developercommunity.microsoft.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Developer Community returned an invalid ticket link.");

            var title = WebUtility.HtmlDecode(_htmlTags.Replace(item.GetProperty("title").GetString() ?? string.Empty, string.Empty)).Trim();
            if (string.IsNullOrEmpty(title))
                throw new InvalidDataException("Developer Community returned an empty ticket title.");
            var state = item.GetProperty("state").GetString();
            return new FeedbackTicket
            {
                Id = id,
                Title = title,
                Url = uri.AbsoluteUri,
                State = state == null ? "No status" : state.Replace('_', ' '),
                Votes = item.GetProperty("totalVotes").GetInt32(),
                IsCreatedByUser = created,
                IsUpvotedByUser = voted,
                UpdatedUtc = item.GetProperty("lastModifiedUtc").GetDateTime().ToUniversalTime(),
            };
        }

        private async Task<JsonDocument> SendAsync(string path, string token, object body, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, _baseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("StartScreen/1.0");
            if (body != null)
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("Developer Community needs a signed-in Visual Studio account.");
            response.EnsureSuccessStatusCode();
            using Stream stream = await response.Content.ReadAsStreamAsync();
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        private static long ReadId(JsonElement value)
        {
            var id = value.ValueKind == JsonValueKind.Number ? value.GetInt64()
                : long.Parse(value.GetString(), CultureInfo.InvariantCulture);
            if (id <= 0)
                throw new InvalidDataException("Developer Community returned an invalid ID.");
            return id;
        }

        private static async Task<AccessToken> GetVisualStudioTokenAsync(CancellationToken cancellationToken)
        {
            var credential = new VisualStudioCredential(new VisualStudioCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(15) });
            return await credential.GetTokenAsync(new TokenRequestContext(new[] { AzureDevOpsCredentialProvider.Scope }), cancellationToken);
        }
    }
}