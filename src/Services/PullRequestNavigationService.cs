using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;

namespace StartScreen.Services
{
    /// <summary>
    /// Opens supported pull requests in the installed Git experience.
    /// Unsupported providers use the browser; native execution failures are reported.
    /// </summary>
    internal static class PullRequestNavigationService
    {
        private static readonly Guid _gitPackage = new Guid("7FE30A77-37F9-4CF2-83DD-96B207028E1B");
        private static readonly Guid _gitCommandGroup = new Guid("57735D06-C920-4415-A2E0-7D6E6FBDFA99");
        private const int _openPullRequestCommandId = 0x104A;

        public static async Task OpenAsync(string webUrl)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await OpenCoreAsync(webUrl, ExecuteNativeAsync, uri =>
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    return Task.CompletedTask;
                });
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", $"Could not open the pull request. {ex.Message}");
            }
        }

        private static async System.Threading.Tasks.Task<bool> ExecuteNativeAsync(Guid commandGroup, int commandId, string arguments)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            IVsShell shell = await VS.Services.GetShellAsync();
            Guid package = _gitPackage;
            // The start page can be shown before Git has loaded. Name/availability
            // lookup at that point is not evidence that native review is absent.
            ErrorHandler.ThrowOnFailure(shell.LoadPackage(ref package, out _));
            return await VS.Commands.ExecuteAsync(commandGroup, commandId, arguments);
        }

        internal static async Task OpenCoreAsync(string webUrl,
            Func<Guid, int, string, System.Threading.Tasks.Task<bool>> executeNativeAsync, Func<Uri, Task> openBrowserAsync)
        {
            var uri = GetValidatedUri(webUrl);
            if (!IsNativePullRequestUrl(uri))
            {
                await openBrowserAsync(uri);
                return;
            }
            if (!await executeNativeAsync(_gitCommandGroup, _openPullRequestCommandId, GetCommandArguments(uri)))
                throw new InvalidOperationException("Visual Studio's native PR review command is unavailable. Use Open in Browser explicitly if needed.");
        }

        internal static Uri GetValidatedUri(string webUrl)
        {
            if (string.IsNullOrWhiteSpace(webUrl)
                || webUrl.Any(c => char.IsControl(c) || c == '"' || c == '\\')
                || !Uri.TryCreate(webUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || string.IsNullOrEmpty(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new ArgumentException("A valid HTTP or HTTPS pull request URL without credentials is required.", nameof(webUrl));
            }

            return uri;
        }

        internal static string GetCommandArguments(Uri uri)
        {
            return uri.AbsoluteUri;
        }

        internal static bool IsNativePullRequestUrl(Uri uri)
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return segments.Length >= 4
                    && segments[2].Equals("pull", StringComparison.OrdinalIgnoreCase)
                    && IsPullRequestId(segments[3]);
            }

            // The _git route also covers Azure DevOps Server installations.
            var gitIndex = Array.FindIndex(segments, s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
            return gitIndex >= 1 && segments.Length >= gitIndex + 4
                && segments[gitIndex + 2].Equals("pullrequest", StringComparison.OrdinalIgnoreCase)
                && IsPullRequestId(segments[gitIndex + 3]);
        }

        private static bool IsPullRequestId(string value)
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
        }
    }
}