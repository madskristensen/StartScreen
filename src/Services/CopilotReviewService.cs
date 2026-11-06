using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using StartScreen.Services.DevHub;

namespace StartScreen.Services
{
    internal static class CopilotReviewService
    {
        private static readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        internal static bool Supports(string webUrl)
        {
            if (!Uri.TryCreate(webUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !uri.IsDefaultPort || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(uri.UserInfo))
                return false;
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            return parts.Length == 4 && !string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1])
                && parts[2] == "pull" && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
        }

        internal static async Task RequestAsync(string webUrl, CancellationToken cancellationToken)
        {
            if (!Supports(webUrl))
                throw new ArgumentException("Copilot review requests are supported for GitHub.com pull requests only.", nameof(webUrl));
            var credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", Options.Instance.DevHubGitHubAccount, cancellationToken);
            await RequestAsync(_client, credential, webUrl, cancellationToken);
        }

        internal static async Task RequestAsync(HttpClient client, DevHubCredential credential, string webUrl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Supports(webUrl))
                throw new ArgumentException("A GitHub.com pull request URL is required.", nameof(webUrl));
            if (string.IsNullOrWhiteSpace(credential?.Token))
                throw new InvalidOperationException("Connect your GitHub account in Dev Hub settings before requesting a review.");
            var uri = PullRequestNavigationService.GetValidatedUri(webUrl);
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            var url = $"https://api.github.com/repos/{parts[0]}/{parts[1]}/pulls/{parts[3]}/requested_reviewers";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
            request.Headers.UserAgent.ParseAdd("StartScreen-VS-Extension");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                reviewers = new[] { "copilot-pull-request-reviewer[bot]" },
            }), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Created)
                throw new HttpRequestException($"GitHub did not accept the Copilot review request (HTTP {(int)response.StatusCode}). "
                    + "Check your sign-in, pull request write permissions, and Copilot code review availability. "
                    + "Use Review Online to inspect or request the review there.");
        }
    }
}