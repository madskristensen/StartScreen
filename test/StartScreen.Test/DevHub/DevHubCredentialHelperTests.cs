using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    [DoNotParallelize]
    public class DevHubCredentialHelperTests
    {
        [TestInitialize]
        public void Initialize()
        {
            DevHubCredentialHelper.ClearCachedCredentials();
        }

        [TestCleanup]
        public void Cleanup()
        {
            DevHubCredentialHelper.ClearCachedCredentials();
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GetCredential_InvalidationDuringAcquisition_DoesNotReturnOrCacheOldCredentialAsync(bool clearAll)
        {
            var pending = new TaskCompletionSource<DevHubCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<DevHubCredential> request = DevHubCredentialHelper.GetCredentialAsync("github.com", null!, CancellationToken.None,
                (_, _, _) => pending.Task, _ => null!, () => "account-a");

            if (clearAll)
                DevHubCredentialHelper.ClearCachedCredentials();
            else
                DevHubCredentialHelper.InvalidateCachedCredential("github.com");
            pending.SetResult(new DevHubCredential("account-a", "old-token"));

            Assert.IsNull(await request);
            var acquisitions = 0;
            var fresh = new DevHubCredential("account-b", "new-token");
            var next = await DevHubCredentialHelper.GetCredentialAsync("github.com", null!, CancellationToken.None,
                (_, _, _) => { acquisitions++; return Task.FromResult(fresh); }, _ => null!, () => "account-a");
            Assert.AreSame(fresh, next);
            Assert.AreEqual(1, acquisitions, "A subsequent refresh must not reuse the stale credential.");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GetCredential_OtherProviderRetryDoesNotDiscardOrEvictInFlightCredentialAsync(bool azureDevOps)
        {
            var pending = new TaskCompletionSource<DevHubCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            var expected = new DevHubCredential("account-a", "current-token");
            var request = DevHubCredentialHelper.GetCredentialAsync("github.com", null!, CancellationToken.None,
                (_, _, _) => pending.Task, _ => null!, () => string.Empty);
            if (azureDevOps)
                DevHubCredentialHelper.InvalidateAzureDevOpsCredential(new DevHubCredential("test-token", DateTimeOffset.UtcNow.AddHours(1)));
            else
                DevHubCredentialHelper.InvalidateCachedCredential("bitbucket.org");
            pending.SetResult(expected);

            Assert.AreSame(expected, await request);
            var cached = await DevHubCredentialHelper.GetCredentialAsync("github.com", null!, CancellationToken.None,
                (_, _, _) => throw new AssertFailedException("Another provider must not evict GitHub credentials."), _ => null!, () => string.Empty);
            Assert.AreSame(expected, cached);
        }

        [TestMethod]
        public async Task GetCredential_AccountChangesDuringAcquisition_DiscardsResultAsync()
        {
            var pending = new TaskCompletionSource<DevHubCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            var account = "account-a";
            Task<DevHubCredential> request = DevHubCredentialHelper.GetCredentialAsync("github.com", null!, CancellationToken.None,
                (_, _, _) => pending.Task, _ => null!, () => account);

            account = "account-b";
            pending.SetResult(new DevHubCredential("account-a", "old-token"));

            Assert.IsNull(await request);
        }

        [TestMethod]
        public async Task GetCredential_DefaultCacheFromAnotherSelectedAccount_IsNotReusedAsync()
        {
            var account = "account-a";
            var acquisitions = 0;
            Task<DevHubCredential> Acquire(string host, string username, CancellationToken token)
            {
                acquisitions++;
                return Task.FromResult(new DevHubCredential(account, account + "-token"));
            }

            var first = await DevHubCredentialHelper.GetCredentialAsync("github.com", null!, CancellationToken.None,
                Acquire, _ => null!, () => account);
            account = "account-b";
            var second = await DevHubCredentialHelper.GetCredentialAsync("github.com", null!, CancellationToken.None,
                Acquire, _ => null!, () => account);

            Assert.AreEqual("account-a", first.Username);
            Assert.AreEqual("account-b", second.Username);
            Assert.AreEqual(2, acquisitions);
        }

        [TestMethod]
        public async Task GetCredential_CanceledAcquisitionIgnoringToken_DoesNotCacheResultAsync()
        {
            using var cancellation = new CancellationTokenSource();
            var pending = new TaskCompletionSource<DevHubCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            var assertion = Assert.ThrowsAsync<OperationCanceledException>(() => DevHubCredentialHelper
                .GetCredentialAsync("github.com", null!, cancellation.Token,
                    (_, _, _) => pending.Task, _ => null!, () => string.Empty));

            cancellation.Cancel();
            pending.SetResult(new DevHubCredential("account-a", "old-token"));
            await assertion;

            var next = await DevHubCredentialHelper.GetCredentialAsync("github.com", null!, CancellationToken.None,
                (_, _, _) => Task.FromResult<DevHubCredential>(null!), _ => null!, () => string.Empty);
            Assert.IsNull(next);
        }

        [TestMethod]
        public async Task GetCredential_InvalidationDuringStoredFallback_DiscardsResultAsync()
        {
            var result = await DevHubCredentialHelper.GetCredentialAsync("github.com", null!, CancellationToken.None,
                (_, _, _) => Task.FromResult<DevHubCredential>(null!),
                _ =>
                {
                    DevHubCredentialHelper.InvalidateCachedCredential("github.com");
                    return new DevHubCredential("old-account", "old-token");
                }, () => string.Empty);

            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task GetCredential_ExplicitAccountsAndDefault_UseSeparateCacheKeysAsync()
        {
            var acquisitions = 0;
            Task<DevHubCredential> Acquire(string host, string username, CancellationToken token)
            {
                acquisitions++;
                return Task.FromResult(new DevHubCredential(username ?? "default", username ?? "default-token"));
            }

            foreach (var username in new[] { "account-a", "account-b", null! })
            {
                var first = await DevHubCredentialHelper.GetCredentialAsync("github.com", username, CancellationToken.None,
                    Acquire, _ => null!, () => string.Empty);
                var cached = await DevHubCredentialHelper.GetCredentialAsync("github.com", username, CancellationToken.None,
                    Acquire, _ => null!, () => string.Empty);
                Assert.AreSame(first, cached);
                Assert.AreEqual(username ?? "default", cached.Username);
            }

            Assert.AreEqual(3, acquisitions);
        }

        [TestMethod]
        public async Task GetCredential_GcmReturnsDifferentExplicitAccount_RejectsCredentialAsync()
        {
            var result = await DevHubCredentialHelper.GetCredentialAsync("github.com", "account-b", CancellationToken.None,
                (_, _, _) => Task.FromResult(new DevHubCredential("account-a", "token")), _ => null!, () => string.Empty);

            Assert.IsNull(result);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ConnectInteractive_InvalidationDuringLogin_DoesNotPersistOrPublishAsync(bool clearAll)
        {
            var pending = new TaskCompletionSource<DevHubCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            var writes = 0;
            Task<bool> request = DevHubCredentialHelper.ConnectInteractiveAsync("github.com", CancellationToken.None,
                (_, _) => pending.Task, (_, _, _) => writes++, () => string.Empty);

            if (clearAll)
                DevHubCredentialHelper.ClearCachedCredentials();
            else
                DevHubCredentialHelper.InvalidateCachedCredential("github.com");
            pending.SetResult(new DevHubCredential("account-a", "old-token"));

            Assert.IsFalse(await request);
            Assert.AreEqual(0, writes);
            var cached = await DevHubCredentialHelper.GetCredentialAsync("github.com", null!, CancellationToken.None,
                (_, _, _) => Task.FromResult<DevHubCredential>(null!), _ => null!, () => string.Empty);
            Assert.IsNull(cached);
        }

        [TestMethod]
        public async Task ConnectInteractive_AccountChangesDuringLogin_DoesNotPersistAsync()
        {
            var pending = new TaskCompletionSource<DevHubCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            var account = "account-a";
            var writes = 0;
            Task<bool> request = DevHubCredentialHelper.ConnectInteractiveAsync("github.com", CancellationToken.None,
                (_, _) => pending.Task, (_, _, _) => writes++, () => account);

            account = "account-b";
            pending.SetResult(new DevHubCredential("account-a", "old-token"));

            Assert.IsFalse(await request);
            Assert.AreEqual(0, writes);
        }

        [TestMethod]
        public async Task ConnectInteractive_CanceledLoginIgnoringToken_DoesNotPersistAsync()
        {
            using var cancellation = new CancellationTokenSource();
            var pending = new TaskCompletionSource<DevHubCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            var writes = 0;
            var assertion = Assert.ThrowsAsync<OperationCanceledException>(() => DevHubCredentialHelper
                .ConnectInteractiveAsync("github.com", cancellation.Token,
                    (_, _) => pending.Task, (_, _, _) => writes++, () => string.Empty));

            cancellation.Cancel();
            pending.SetResult(new DevHubCredential("account-a", "old-token"));

            await assertion;
            Assert.AreEqual(0, writes);
        }

        [TestMethod]
        public async Task ConnectInteractive_CurrentLogin_PersistsAndCachesCredentialAsync()
        {
            var credential = new DevHubCredential("account-a", "current-token");
            var writes = 0;
            var accountGeneration = DevHubCredentialHelper.AccountGeneration;
            var connected = await DevHubCredentialHelper.ConnectInteractiveAsync("github.com", CancellationToken.None,
                (_, _) => Task.FromResult(credential), (host, username, token) =>
                {
                    Assert.AreEqual("github.com", host);
                    Assert.AreEqual(credential.Username, username);
                    Assert.AreEqual(credential.Token, token);
                    writes++;
                }, () => string.Empty);
            var cached = await DevHubCredentialHelper.GetCredentialAsync("github.com", null!, CancellationToken.None,
                (_, _, _) => Task.FromResult<DevHubCredential>(null!), _ => null!, () => string.Empty);

            Assert.IsTrue(connected);
            Assert.AreEqual(1, writes);
            Assert.IsGreaterThan(accountGeneration, DevHubCredentialHelper.AccountGeneration,
                "An explicit sign-in must invalidate the previous dashboard account context without evicting the new credential.");
            Assert.AreSame(credential, cached);
        }

        [TestMethod]
        [DataRow("git:https://bitbucket.org", true)]
        [DataRow("git:https://user@bitbucket.org", true)]
        [DataRow("git:https://bitbucket.org/user", true)]
        [DataRow("git:https://BITBUCKET.ORG", true)]
        [DataRow("git:https://github.com/bitbucket.org", false)]
        [DataRow("git:https://bitbucket.org.example.com", false)]
        [DataRow("git:https://bitbucket.org@github.com", false)]
        [DataRow("git:not-a-uri", false)]
        [DataRow("git:ftp://bitbucket.org", false)]
        [DataRow("StartScreen:bitbucket.org", false)]
        [DataRow(null, false)]
        public void GitCredentialTarget_MatchesActualHostOnly(string? target, bool expected)
        {
            Assert.AreEqual(expected, NativeCredentialManager.IsGitCredentialTargetForHost(target!, "bitbucket.org"));
        }

        [TestMethod]
        public void ParseGitCredentialOutput_ValidOutput_ParsesCredential()
        {
            var output = "protocol=https\nhost=github.com\nusername=madskristensen\npassword=ghp_abc123\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNotNull(result);
            Assert.AreEqual("madskristensen", result.Username);
            Assert.AreEqual("ghp_abc123", result.Token);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_WindowsLineEndings_ParsesCorrectly()
        {
            var output = "protocol=https\r\nhost=github.com\r\nusername=user\r\npassword=token123\r\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNotNull(result);
            Assert.AreEqual("user", result.Username);
            Assert.AreEqual("token123", result.Token);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_NoPassword_ReturnsNull()
        {
            var output = "protocol=https\nhost=github.com\nusername=user\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_EmptyPassword_ReturnsNull()
        {
            var output = "protocol=https\nhost=github.com\nusername=user\npassword=\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_NullInput_ReturnsNull()
        {
            Assert.IsNull(DevHubCredentialHelper.ParseGitCredentialOutput(null));
        }

        [TestMethod]
        public void ParseGitCredentialOutput_EmptyInput_ReturnsNull()
        {
            Assert.IsNull(DevHubCredentialHelper.ParseGitCredentialOutput(""));
        }

        [TestMethod]
        public void ParseGitCredentialOutput_WhitespaceOnly_ReturnsNull()
        {
            Assert.IsNull(DevHubCredentialHelper.ParseGitCredentialOutput("   \n  \n  "));
        }

        [TestMethod]
        public void ParseGitCredentialOutput_NoUsername_StillReturnsCredentialWithNullUsername()
        {
            var output = "protocol=https\nhost=github.com\npassword=my_token\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNotNull(result);
            Assert.IsNull(result.Username);
            Assert.AreEqual("my_token", result.Token);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_ExtraFields_IgnoresThem()
        {
            var output = "protocol=https\nhost=github.com\nusername=user\npassword=token\npath=/some/path\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNotNull(result);
            Assert.AreEqual("user", result.Username);
            Assert.AreEqual("token", result.Token);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_PasswordWithEquals_ParsesCorrectly()
        {
            // Tokens can contain equals signs
            var output = "protocol=https\nhost=github.com\nusername=user\npassword=abc=def=ghi\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNotNull(result);
            Assert.AreEqual("abc=def=ghi", result.Token);
        }

        [TestMethod]
        public void InvalidateCachedCredential_NullHost_DoesNotThrow()
        {
            DevHubCredentialHelper.InvalidateCachedCredential(null);
        }

        [TestMethod]
        public void InvalidateCachedCredential_EmptyHost_DoesNotThrow()
        {
            DevHubCredentialHelper.InvalidateCachedCredential("");
        }

        [TestMethod]
        public void InvalidateCachedCredential_WhitespaceHost_DoesNotThrow()
        {
            DevHubCredentialHelper.InvalidateCachedCredential("   ");
        }

        [TestMethod]
        public void InvalidateCachedCredential_UnknownHost_DoesNotThrow()
        {
            // Invalidating a host that was never cached should be a safe no-op.
            DevHubCredentialHelper.ClearCachedCredentials();
            DevHubCredentialHelper.InvalidateCachedCredential("unknown.example.com");
        }

        [TestMethod]
        public void ParseGitHubAccountListOutput_ValidLines_ReturnsAccounts()
        {
            var output = "madskristensen\nwork-account\n";

            var result = DevHubCredentialHelper.ParseGitHubAccountListOutput(output);

            CollectionAssert.AreEqual(new[] { "madskristensen", "work-account" }, result.ToArray());
        }

        [TestMethod]
        public void ParseGitHubAccountListOutput_DuplicateAndWarningLines_FiltersOutput()
        {
            var output = "madskristensen\nwarning: skipped helper\ntestuser\nmadskristensen\n";

            var result = DevHubCredentialHelper.ParseGitHubAccountListOutput(output);

            CollectionAssert.AreEqual(new[] { "madskristensen", "testuser" }, result.ToArray());
        }

        [TestMethod]
        public void ParseGitHubAccountListOutput_EmptyInput_ReturnsEmpty()
        {
            var result = DevHubCredentialHelper.ParseGitHubAccountListOutput(" \n \r\n ");

            Assert.IsEmpty(result);
        }
    }
}
