using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;

namespace StartScreen.Services.DevHub
{
    /// <summary>
    /// Acquires short-lived Azure DevOps tokens through Visual Studio, without storing them on disk.
    /// </summary>
    internal sealed class AzureDevOpsCredentialProvider
    {
        internal const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
        internal const string SignInMessage = "Azure DevOps needs a Visual Studio account. Sign in or reauthenticate in Visual Studio, select the account under Tools > Options > Azure Service Authentication, then retry. PAT is optional in Dev Hub settings.";

        private readonly TokenCredential _credential;
        private readonly HttpClient _httpClient;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly SemaphoreSlim _acquisition = new SemaphoreSlim(1, 1);
        private readonly object _gate = new object();
        private readonly Dictionary<string, (DevHubCredential Credential, DateTimeOffset Until)> _tokens = new Dictionary<string, (DevHubCredential, DateTimeOffset)>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Tenant, DateTimeOffset Until)> _tenants = new Dictionary<string, (string, DateTimeOffset)>(StringComparer.OrdinalIgnoreCase);
        private int _generation;

        internal AzureDevOpsCredentialProvider(TokenCredential credential, HttpClient httpClient, Func<DateTimeOffset> utcNow = null)
        {
            _credential = credential ?? throw new ArgumentNullException(nameof(credential));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        internal async Task<DevHubCredential> GetCredentialAsync(string requestUrl, CancellationToken cancellationToken)
        {
            var organization = GetOrganizationUri(requestUrl);
            await _acquisition.WaitAsync(cancellationToken);
            try
            {
                int generation;
                lock (_gate)
                    generation = _generation;

                var tenant = await GetTenantAsync(organization, generation, cancellationToken);
                var key = tenant ?? string.Empty;
                lock (_gate)
                {
                    if (_tokens.TryGetValue(key, out var cached) && cached.Until > _utcNow())
                        return cached.Credential;
                }

                DevHubCredential result;
                DateTimeOffset until;
                try
                {
                    var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { Scope }, tenantId: tenant), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(token.Token) || token.ExpiresOn <= _utcNow())
                        throw new AuthenticationFailedException("Visual Studio did not return a valid Azure DevOps access token.");

                    result = new DevHubCredential(token.Token, token.ExpiresOn);
                    until = token.ExpiresOn - TimeSpan.FromMinutes(5);
                }
                catch (Exception ex) when (ex is CredentialUnavailableException || ex is AuthenticationFailedException)
                {
                    await ex.LogAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    OutputLog.Info("Azure DevOps", SignInMessage);
                    result = null;
                    until = _utcNow().AddSeconds(30);
                }

                lock (_gate)
                {
                    if (generation == _generation)
                        _tokens[key] = (result, until);
                }
                return result;
            }
            finally
            {
                _acquisition.Release();
            }
        }

        internal void Invalidate(DevHubCredential credential = null)
        {
            lock (_gate)
            {
                _generation++;
                if (credential == null)
                {
                    _tokens.Clear();
                    _tenants.Clear();
                }
                else
                {
                    foreach (var key in _tokens.Where(pair => ReferenceEquals(pair.Value.Credential, credential)).Select(pair => pair.Key).ToArray())
                        _tokens.Remove(key);
                }
            }
        }

        private async Task<string> GetTenantAsync(Uri organization, int generation, CancellationToken cancellationToken)
        {
            if (organization == null)
                return null;

            var key = organization.AbsoluteUri;
            lock (_gate)
            {
                if (_tenants.TryGetValue(key, out var cached) && cached.Until > _utcNow())
                    return cached.Tenant;
            }

            using (var request = new HttpRequestMessage(HttpMethod.Head, organization))
            using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Challenges and sign-in redirects can carry tenant metadata. Never follow the redirect.
                var redirect = (int)response.StatusCode >= 300 && (int)response.StatusCode < 400;
                if (!response.IsSuccessStatusCode && !redirect
                    && response.StatusCode != HttpStatusCode.Unauthorized && response.StatusCode != HttpStatusCode.Forbidden)
                    response.EnsureSuccessStatusCode();

                var tenant = GetTenantId(response);
                lock (_gate)
                {
                    if (generation == _generation)
                        _tenants[key] = (tenant, _utcNow().AddHours(1));
                }
                return tenant;
            }
        }

        internal static string GetTenantId(HttpResponseMessage response)
        {
            foreach (var header in response.Headers.WwwAuthenticate)
            {
                if (!header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || header.Parameter == null)
                    continue;

                foreach (var parameter in header.Parameter.Split(','))
                {
                    var parts = parameter.Split(new[] { '=' }, 2);
                    if (parts.Length != 2 || !parts[0].Trim().Equals("authorization_uri", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (Uri.TryCreate(parts[1].Trim().Trim('"'), UriKind.Absolute, out var authority)
                        && authority.Scheme == Uri.UriSchemeHttps
                        && (authority.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
                            || authority.Host.Equals("login.windows.net", StringComparison.OrdinalIgnoreCase))
                        && Guid.TryParse(authority.AbsolutePath.Trim('/'), out var tenant) && tenant != Guid.Empty)
                    {
                        return tenant.ToString();
                    }
                }
            }

            if (response.Headers.TryGetValues("X-VSS-ResourceTenant", out var values))
            {
                foreach (var value in values)
                {
                    if (Guid.TryParse(value, out var tenant) && tenant != Guid.Empty)
                        return tenant.ToString();
                }
            }

            // MSA organizations and endpoints without tenant metadata use VS's configured account tenant.
            return null;
        }

        internal static bool IsCloudUrl(Uri uri) => uri != null && uri.IsAbsoluteUri
            && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
            && (uri.Host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase));

        internal static Uri GetOrganizationUri(string requestUrl)
        {
            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) || !IsCloudUrl(uri))
                throw new ArgumentException("Visual Studio authentication requires an Azure DevOps Services HTTPS URL.", nameof(requestUrl));

            if (uri.Host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase))
            {
                var organization = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return string.IsNullOrEmpty(organization) || organization.StartsWith("_", StringComparison.Ordinal)
                    ? null : new Uri($"https://dev.azure.com/{organization}/");
            }

            return uri.Host.EndsWith(".vssps.visualstudio.com", StringComparison.OrdinalIgnoreCase)
                ? null : new Uri($"https://{uri.Host}/");
        }
    }
}