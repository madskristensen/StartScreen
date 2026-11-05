using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class PullRequestNavigationServiceTests
    {
        [TestMethod]
        [DataRow("https://github.com/owner/repo/pull/42")]
        [DataRow("https://github.com/owner/repo/pull/42/files?diff=split#file")]
        [DataRow("https://dev.azure.com/org/project/_git/repo/pullrequest/42")]
        [DataRow("https://org.visualstudio.com/project/_git/repo/pullrequest/42")]
        [DataRow("http://tfs.contoso.com/tfs/collection/project/_git/repo/pullrequest/42")]
        public void IsNativePullRequestUrl_SupportedProviders_ReturnsTrue(string url)
        {
            Assert.IsTrue(PullRequestNavigationService.IsNativePullRequestUrl(
                PullRequestNavigationService.GetValidatedUri(url)));
        }

        [TestMethod]
        [DataRow("https://bitbucket.org/owner/repo/pull-requests/42")]
        [DataRow("https://gitlab.com/owner/repo/-/merge_requests/42")]
        [DataRow("https://github.com.evil.example/owner/repo/pull/42")]
        [DataRow("https://github.com/owner/repo/issues/42")]
        [DataRow("https://github.com/owner/repo/pull/0")]
        [DataRow("https://github.com/owner/repo/pull/not-a-number")]
        public void IsNativePullRequestUrl_UnsupportedUrls_ReturnsFalse(string url)
        {
            Assert.IsFalse(PullRequestNavigationService.IsNativePullRequestUrl(
                PullRequestNavigationService.GetValidatedUri(url)));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("not a url")]
        [DataRow("javascript:alert(1)")]
        [DataRow("file:///C:/repo/request.html")]
        [DataRow("https://user:password@github.com/owner/repo/pull/42")]
        [DataRow("https://github.com/owner/repo/pull/42\r\nFile.OpenFile")]
        [DataRow("https://github.com/owner/repo/pull/42\" extra")]
        [DataRow("https://github.com\\owner\\repo\\pull\\42")]
        public void GetValidatedUri_UnsafeUrls_Throws(string? url)
        {
            Assert.ThrowsExactly<ArgumentException>(() => PullRequestNavigationService.GetValidatedUri(url!));
        }

        [TestMethod]
        public void GetCommandArguments_PreservesWholeUrlWithoutQuotes()
        {
            const string url = "https://dev.azure.com/org/My%20Project/_git/repo/pullrequest/42?view=files&path=%2Fa#discussion";

            var args = PullRequestNavigationService.GetCommandArguments(PullRequestNavigationService.GetValidatedUri(url));

            Assert.AreEqual(url, args);
        }

        [TestMethod]
        public async Task OpenCoreAsync_SupportedPr_ExecutesNativeCommandWithoutBrowser()
        {
            const string url = "https://github.com/owner/repo/pull/42";
            Guid? actualGroup = null;
            int actualId = 0;
            string? actualArguments = null;
            int browserCalls = 0;

            await PullRequestNavigationService.OpenCoreAsync(url, (group, id, arguments) =>
            {
                actualGroup = group;
                actualId = id;
                actualArguments = arguments;
                return Task.FromResult(true);
            }, _ =>
            {
                browserCalls++;
                return Task.CompletedTask;
            });

            Assert.AreEqual(new Guid("57735D06-C920-4415-A2E0-7D6E6FBDFA99"), actualGroup);
            Assert.AreEqual(0x104A, actualId);
            Assert.AreEqual(url, actualArguments);
            Assert.AreEqual(0, browserCalls);
        }

        [TestMethod]
        public async Task OpenCoreAsync_NativeUnavailable_DoesNotSilentlyOpenBrowser()
        {
            int browserCalls = 0;

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PullRequestNavigationService.OpenCoreAsync(
                "https://github.com/owner/repo/pull/42", (_, _, _) => Task.FromResult(false), _ =>
                {
                    browserCalls++;
                    return Task.CompletedTask;
                }));

            Assert.AreEqual(0, browserCalls);
        }

        [TestMethod]
        public async Task OpenCoreAsync_UnsupportedProvider_UsesBrowserWithoutNativeCommand()
        {
            const string url = "https://bitbucket.org/owner/repo/pull-requests/42";
            int nativeCalls = 0;
            string? browserUrl = null;

            await PullRequestNavigationService.OpenCoreAsync(url, (_, _, _) =>
            {
                nativeCalls++;
                return Task.FromResult(true);
            }, uri =>
            {
                browserUrl = uri.AbsoluteUri;
                return Task.CompletedTask;
            });

            Assert.AreEqual(0, nativeCalls);
            Assert.AreEqual(url, browserUrl);
        }
    }
}