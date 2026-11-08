using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.Services;
using StartScreen.Services.DevHub;

namespace StartScreen.ToolWindows.Controls
{
    public partial class DevHubPanel
    {
        private IReadOnlyList<CopilotActivitySession> _localCopilotSessions = Array.Empty<CopilotActivitySession>();
        private readonly CopilotLocalSessions.DiscoveryCache _localCopilotCache = new();
        private readonly CopilotLocalSessions.DiscoveryCache _visualStudioCopilotCache = new();
        private JoinableTask _visualStudioCopilotRefresh;
        private bool _visualStudioCopilotMetadataLoaded;
        private string _visualStudioCopilotStatus;
        private IReadOnlyList<CopilotActivitySession> _cloudCopilotSessions = Array.Empty<CopilotActivitySession>();
        private CancellationTokenSource _copilotSourcesCancellation;
        private DispatcherTimer _copilotSourcesTimer;
        private string _localCopilotStatus;
        private string _cloudCopilotStatus;
        private string _copilotConnectionError;
        private string _lastCopilotStatus;
        private string _cloudCopilotAccount;
        private int _cloudCopilotAccountGeneration;
        private bool _copilotSourcesLoading;

        private void StartCopilotSources(bool forceRefresh = false)
        {
            if (_copilotSourcesCancellation != null)
                return;
            _copilotSourcesCancellation = CancellationTokenSource.CreateLinkedTokenSource(StartScreenPackage.ShutdownToken);
            _copilotSourcesTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMinutes(1),
            };
            _copilotSourcesTimer.Tick += CopilotSourcesTimer_Tick;
            _copilotSourcesTimer.Start();
            RefreshCopilotSourcesAsync(_copilotSourcesCancellation.Token, forceRefresh).FileAndForget(nameof(DevHubPanel));
        }

        private void CopilotSourcesTimer_Tick(object sender, EventArgs e)
        {
            if (!_copilotSourcesLoading && _copilotSourcesCancellation != null)
                RefreshCopilotSourcesAsync(_copilotSourcesCancellation.Token).FileAndForget(nameof(DevHubPanel));
        }

        private async Task RefreshCopilotSourcesAsync(CancellationToken token, bool forceRefresh = false)
        {
            _copilotSourcesLoading = true;
            UpdateBusyState();
            try
            {
                _visualStudioCopilotRefresh = StartScreenPackage.BackgroundTaskFactory.RunAsync(() => RefreshVisualStudioCopilotMetadataAsync(token, forceRefresh));
                await System.Threading.Tasks.Task.WhenAll(_visualStudioCopilotRefresh.Task,
                    RefreshLocalCopilotAsync(token, forceRefresh), RefreshCloudCopilotAsync(token, forceRefresh));
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_copilotSourcesCancellation?.Token == token)
                {
                    _copilotSourcesLoading = false;
                    UpdateBusyState();
                }
            }
        }

        private async Task RefreshVisualStudioCopilotMetadataAsync(CancellationToken token, bool forceRefresh)
        {
            using var progress = Progress?.Start(step: CopilotLoadingStep);
            progress?.Report((0, 1));
            var elapsed = Stopwatch.StartNew();
            try
            {
                var result = await CopilotLocalSessions.ReadAsync(CopilotLocalSessions.VisualStudioHome, token,
                    _visualStudioCopilotCache, forceRefresh, CopilotSessionOrigin.VisualStudio);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (token.IsCancellationRequested || _isDisposed)
                    return;
                _copilotCache.Update(result.Sessions, true);
                _visualStudioCopilotMetadataLoaded = true;
                _visualStudioCopilotStatus = result.Status;
                OutputLog.Info("Copilot", $"VS saved metadata discovery finished in {elapsed.ElapsedMilliseconds} ms ({result.Sessions.Count} sessions).");
                progress?.Complete();
                RenderCopilotActivity();
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!token.IsCancellationRequested && !_isDisposed)
                {
                    _visualStudioCopilotStatus = "VS saved session discovery failed. Refresh to retry. Details are in the Activity Log.";
                    RenderCopilotActivity();
                }
            }
        }

        private async Task RefreshLocalCopilotAsync(CancellationToken token, bool forceRefresh)
        {
            using var progress = Progress?.Start(step: "Reading CLI Copilot sessions...");
            progress?.Report((0, 1));
            var elapsed = Stopwatch.StartNew();
            try
            {
                var result = await CopilotLocalSessions.ReadAsync(CopilotLocalSessions.DefaultHome, token,
                    _localCopilotCache, forceRefresh);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (token.IsCancellationRequested || _isDisposed)
                    return;
                _localCopilotSessions = result.Sessions;
                _localCopilotStatus = result.Status;
                OutputLog.Info("Copilot", $"CLI discovery finished in {elapsed.ElapsedMilliseconds} ms ({result.Sessions.Count} sessions).");
                progress?.Complete();
                RenderCopilotActivity();
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!token.IsCancellationRequested && !_isDisposed)
                {
                    _localCopilotStatus = "CLI session discovery failed. Refresh to retry. Details are in the Activity Log.";
                    RenderCopilotActivity();
                }
            }
        }

        private async Task RefreshCloudCopilotAsync(CancellationToken token, bool forceRefresh = false)
        {
            var enabled = Options.Instance.DevHubGitHubEnabled;
            var account = Options.Instance.DevHubGitHubAccount;
            var generation = DevHubCredentialHelper.AccountGeneration;
            using var progress = enabled ? Progress?.Start(step: "Loading GitHub cloud Copilot sessions...") : null;
            progress?.Report((0, 1));
            var elapsed = Stopwatch.StartNew();
            try
            {
                var result = enabled ? await CopilotCloudSessions.ReadAsync(account, token, forceRefresh) : new CopilotSessionDiscovery();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (!IsCloudCopilotRequestCurrent(account, generation, token))
                    return;
                _cloudCopilotSessions = result.Sessions;
                _cloudCopilotStatus = result.Status;
                _cloudCopilotAccount = account;
                _cloudCopilotAccountGeneration = generation;
                if (enabled)
                    OutputLog.Info("Copilot", $"GitHub cloud discovery finished in {elapsed.ElapsedMilliseconds} ms ({result.Sessions.Count} sessions).");
                progress?.Complete();
                RenderCopilotActivity();
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (IsCloudCopilotRequestCurrent(account, generation, token))
                {
                    _cloudCopilotSessions = _cloudCopilotSessions.Select(session => session.AsCached()).ToArray();
                    _cloudCopilotStatus = ex is UnauthorizedAccessException || ex is NotSupportedException ? ex.Message
                        : "GitHub cloud sessions could not be loaded. Refresh to retry. Details are in the Activity Log.";
                    RenderCopilotActivity();
                }
            }
        }

        private bool IsCloudCopilotRequestCurrent(string account, int generation, CancellationToken token) => !token.IsCancellationRequested
            && !_isDisposed && generation == DevHubCredentialHelper.AccountGeneration
            && string.Equals(account, Options.Instance.DevHubGitHubAccount, StringComparison.OrdinalIgnoreCase);

        private IEnumerable<CopilotActivitySession> CurrentCloudCopilotSessions => Options.Instance.DevHubGitHubEnabled
            && IsCloudCopilotRequestCurrent(_cloudCopilotAccount, _cloudCopilotAccountGeneration, CancellationToken.None)
            ? _cloudCopilotSessions : Array.Empty<CopilotActivitySession>();

        private void UpdateCopilotStatus()
        {
            var status = string.Join(" ", new[] { _copilotConnectionError, _copilotCacheError, _visualStudioCopilotStatus, _localCopilotStatus,
                Options.Instance.DevHubGitHubEnabled ? _cloudCopilotStatus : null }.Where(message => !string.IsNullOrWhiteSpace(message)));
            if (status == _lastCopilotStatus)
                return;
            _lastCopilotStatus = status;
            if (!string.IsNullOrEmpty(status))
                OutputLog.Info("Copilot", status);
        }

        private void StopCopilotSources()
        {
            if (_copilotSourcesTimer != null)
            {
                _copilotSourcesTimer.Stop();
                _copilotSourcesTimer.Tick -= CopilotSourcesTimer_Tick;
                _copilotSourcesTimer = null;
            }
            _copilotSourcesCancellation?.Cancel();
            _copilotSourcesCancellation?.Dispose();
            _copilotSourcesCancellation = null;
            _visualStudioCopilotRefresh = null;
            _copilotSourcesLoading = false;
            UpdateBusyState();
            _cloudCopilotSessions = _cloudCopilotSessions.Select(session => session.AsCached()).ToArray();
        }
    }
}