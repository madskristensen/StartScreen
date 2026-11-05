using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class AzureDevOpsCredentialProviderTests
    {
        private const string Tenant = "11111111-2222-3333-4444-555555555555";
        private const string OtherTenant = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        [TestMethod]
        [DataRow("https://dev.azure.com/org/project/_git/repo", "https://dev.azure.com/org/")]
        [DataRow("https://devdiv.visualstudio.com/DevDiv/_git/VS", "https://devdiv.visualstudio.com/")]
        [DataRow("https://app.vssps.visualstudio.com/_apis/profile/profiles/me", null)]
        public async Task Credential_UsesOrganizationTenantAndAzureDevOpsScopeAsync(string url, string? organization)
        {
            var handler = new TestHttpMessageHandler((request, _) =>
            {
                Assert.AreEqual(HttpMethod.Head, request.Method);
                Assert.AreEqual(organization, request.RequestUri!.AbsoluteUri);
                Assert.IsNull(request.Headers.Authorization);
                return Challenge(Tenant);
            });
            using var client = new HttpClient(handler);
            var credential = new TestCredential((context, token) =>
            {
                Assert.AreEqual(organization == null ? null : Tenant, context.TenantId);
                CollectionAssert.AreEqual(new[] { AzureDevOpsCredentialProvider.Scope }, context.Scopes);
                return Task.FromResult(new AccessToken("access-token", _now.AddHours(1)));
            });
            var provider = new AzureDevOpsCredentialProvider(credential, client, () => _now);

            var result = await provider.GetCredentialAsync(url, CancellationToken.None);

            Assert.IsNotNull(result);
            Assert.IsTrue(result.IsBearer);
            Assert.AreEqual(_now.AddHours(1), result.ExpiresOn);
            Assert.AreEqual("access-token", result.Token);
            Assert.AreEqual(organization == null ? 0 : 1, handler.RequestCount);
        }

        [TestMethod]
        [DataRow("https://tfs.contoso.com/project/_git/repo")]
        [DataRow("http://dev.azure.com/org")]
        [DataRow("https://dev.azure.com.evil.example/org")]
        [DataRow("https://evilvisualstudio.com/org")]
        [DataRow("https://user@dev.azure.com/org")]
        [DataRow("https://dev.azure.com:8443/org")]
        public async Task Credential_RejectsUntrustedOrOnPremisesUrlsBeforeAcquisitionAsync(string url)
        {
            var handler = new TestHttpMessageHandler((_, _) => Challenge(Tenant));
            using var client = new HttpClient(handler);
            var credential = new TestCredential((_, _) => Task.FromResult(new AccessToken("token", _now.AddHours(1))));
            var provider = new AzureDevOpsCredentialProvider(credential, client, () => _now);

            await Assert.ThrowsAsync<ArgumentException>(() => provider.GetCredentialAsync(url, CancellationToken.None));

            Assert.AreEqual(0, handler.RequestCount);
            Assert.AreEqual(0, credential.RequestCount);
        }

        [TestMethod]
        [DataRow("Bearer", "authorization_uri=\"https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555\"", Tenant)]
        [DataRow("Bearer", "authorization_uri=https://login.windows.net/11111111-2222-3333-4444-555555555555", Tenant)]
        [DataRow("Basic", "authorization_uri=https://login.windows.net/11111111-2222-3333-4444-555555555555", OtherTenant)]
        [DataRow("Bearer", "authorization_uri=https://evil.example/11111111-2222-3333-4444-555555555555", OtherTenant)]
        [DataRow("Bearer", "authorization_uri=https://login.microsoftonline.com/common", OtherTenant)]
        public void TenantDiscovery_UsesTrustedBearerChallengeOrResourceTenant(string scheme, string parameter, string expected)
        {
            using var response = Challenge(OtherTenant);
            response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(scheme, parameter));

            Assert.AreEqual(expected, AzureDevOpsCredentialProvider.GetTenantId(response));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("not-a-tenant")]
        [DataRow("00000000-0000-0000-0000-000000000000")]
        public void TenantDiscovery_AbsentOrMsaTenantUsesConfiguredVsAccount(string? tenant)
        {
            using var response = Challenge(tenant);
            Assert.IsNull(AzureDevOpsCredentialProvider.GetTenantId(response));
        }

        [TestMethod]
        public async Task Credential_CachesByTenantAndRenewsFiveMinutesBeforeExpiryAsync()
        {
            var now = _now;
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
                Challenge(request.RequestUri!.AbsolutePath.Contains("other") ? OtherTenant : Tenant)));
            var credential = new TestCredential((_, _) => Task.FromResult(new AccessToken(Guid.NewGuid().ToString(), now.AddHours(1))));
            var provider = new AzureDevOpsCredentialProvider(credential, client, () => now);

            var first = await provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None);
            var sameTenant = await provider.GetCredentialAsync("https://devdiv.visualstudio.com/DevDiv", CancellationToken.None);
            var other = await provider.GetCredentialAsync("https://dev.azure.com/other", CancellationToken.None);
            Assert.AreSame(first, sameTenant);
            Assert.AreNotSame(first, other);
            Assert.AreEqual(2, credential.RequestCount);

            now = now.AddMinutes(56);
            var renewed = await provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None);
            Assert.AreNotSame(first, renewed);
            Assert.AreEqual(3, credential.RequestCount);
            Assert.AreEqual(now.AddHours(1), renewed!.ExpiresOn);
        }

        [TestMethod]
        public async Task Credential_ConcurrentRequestsShareDiscoveryAndTokenAcquisitionAsync()
        {
            var completion = new TaskCompletionSource<AccessToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new TestHttpMessageHandler((_, _) => Challenge(Tenant));
            using var client = new HttpClient(handler);
            var credential = new TestCredential((_, _) => completion.Task);
            var provider = new AzureDevOpsCredentialProvider(credential, client, () => _now);

            var requests = Enumerable.Range(0, 10).Select(_ => provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None)).ToArray();
            Assert.AreEqual(1, credential.RequestCount);
            completion.SetResult(new AccessToken("token", _now.AddHours(1)));
            var results = await Task.WhenAll(requests);

            Assert.AreEqual(1, credential.RequestCount);
            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsTrue(results.All(result => ReferenceEquals(results[0], result)));
        }

        [TestMethod]
        public async Task Credential_InvalidationDoesNotAllowInFlightResultToRepopulateCacheAsync()
        {
            var completion = new TaskCompletionSource<AccessToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) => Challenge(Tenant)));
            var credential = new TestCredential((_, _) => completion.Task);
            var provider = new AzureDevOpsCredentialProvider(credential, client, () => _now);
            var pending = provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None);
            provider.Invalidate();
            completion.SetResult(new AccessToken("old-token", _now.AddHours(1)));
            await pending;

            await provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None);

            Assert.AreEqual(2, credential.RequestCount);
        }

        [TestMethod]
        public async Task Credential_AuthenticationFailureIsBrieflyCachedAndExplicitRetryClearsItAsync()
        {
            var fail = true;
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) => Challenge(Tenant)));
            var credential = new TestCredential((_, _) => fail
                ? Task.FromException<AccessToken>(new CredentialUnavailableException("VS account needs reauthentication"))
                : Task.FromResult(new AccessToken("token", _now.AddHours(1))));
            var provider = new AzureDevOpsCredentialProvider(credential, client, () => _now);

            Assert.IsNull(await provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None));
            fail = false;
            Assert.IsNull(await provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None));
            Assert.AreEqual(1, credential.RequestCount);
            provider.Invalidate();
            Assert.IsNotNull(await provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None));
            Assert.AreEqual(2, credential.RequestCount);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("expired")]
        public async Task Credential_InvalidTokenIsNotReportedAsAuthenticatedAsync(string token)
        {
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) => Challenge(Tenant)));
            var credential = new TestCredential((_, _) => Task.FromResult(new AccessToken(token, _now.AddMinutes(-1))));
            var provider = new AzureDevOpsCredentialProvider(credential, client, () => _now);

            Assert.IsNull(await provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None));
        }

        [TestMethod]
        public async Task Credential_CancellationPropagatesWithoutCachingFailureAsync()
        {
            using var cancellation = new CancellationTokenSource();
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) => Challenge(Tenant)));
            var credential = new TestCredential((_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new AccessToken("token", _now.AddHours(1)));
            });
            var provider = new AzureDevOpsCredentialProvider(credential, client, () => _now);

            await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetCredentialAsync("https://dev.azure.com/org", cancellation.Token));
            Assert.IsNotNull(await provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None));
            Assert.AreEqual(2, credential.RequestCount);
        }

        [TestMethod]
        public async Task Credential_DiscoveryFailureDoesNotAcquireTokenOrCacheTenantAsync()
        {
            var handler = new TestHttpMessageHandler((_, count) => count == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Challenge(Tenant));
            using var client = new HttpClient(handler);
            var credential = new TestCredential((_, _) => Task.FromResult(new AccessToken("token", _now.AddHours(1))));
            var provider = new AzureDevOpsCredentialProvider(credential, client, () => _now);

            await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None));
            Assert.AreEqual(0, credential.RequestCount);
            Assert.IsNotNull(await provider.GetCredentialAsync("https://dev.azure.com/org", CancellationToken.None));
            Assert.AreEqual(2, handler.RequestCount);
        }

        [TestMethod]
        public async Task Credential_DiscoveryReadsRedirectMetadataWithoutSendingTokenAsync()
        {
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                Assert.IsNull(request.Headers.Authorization);
                var response = Challenge(Tenant);
                response.StatusCode = HttpStatusCode.Redirect;
                response.Headers.Location = new Uri("https://login.microsoftonline.com/");
                return response;
            }));
            var credential = new TestCredential((context, _) =>
            {
                Assert.AreEqual(Tenant, context.TenantId);
                return Task.FromResult(new AccessToken("token", _now.AddHours(1)));
            });
            var provider = new AzureDevOpsCredentialProvider(credential, client, () => _now);

            Assert.IsNotNull(await provider.GetCredentialAsync("https://devdiv.visualstudio.com/DevDiv/_git/VS", CancellationToken.None));
        }

        [TestMethod]
        [DataRow((int)AzureDevOpsAuthenticationMode.Automatic, false, true)]
        [DataRow((int)AzureDevOpsAuthenticationMode.Automatic, true, false)]
        [DataRow((int)AzureDevOpsAuthenticationMode.VisualStudio, true, true)]
        [DataRow((int)AzureDevOpsAuthenticationMode.VisualStudio, false, true)]
        [DataRow((int)AzureDevOpsAuthenticationMode.PersonalAccessToken, true, false)]
        [DataRow((int)AzureDevOpsAuthenticationMode.PersonalAccessToken, false, false)]
        [DataRow((int)AzureDevOpsAuthenticationMode.Disconnected, false, false)]
        [DataRow((int)AzureDevOpsAuthenticationMode.Disconnected, true, false)]
        public void AuthenticationMode_ExplicitChoiceOverridesStoredPat(int mode, bool hasPat, bool useVisualStudio)
        {
            Assert.AreEqual(useVisualStudio, DevHubCredentialHelper.UsesVisualStudio((AzureDevOpsAuthenticationMode)mode, hasPat));
        }

        private static HttpResponseMessage Challenge(string? tenant)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (tenant != null)
                response.Headers.Add("X-VSS-ResourceTenant", tenant);
            return response;
        }

        private sealed class TestCredential(Func<TokenRequestContext, CancellationToken, Task<AccessToken>> acquire) : TokenCredential
        {
            public int RequestCount { get; private set; }

            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                RequestCount++;
                return new ValueTask<AccessToken>(acquire(requestContext, cancellationToken));
            }
        }
    }
}