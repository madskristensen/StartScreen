using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Primitives;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell.Interop;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.ToolWindows.Controls
{
    public partial class DevHubPanel
    {
        public static readonly DependencyProperty CopilotActivityStatusProperty = DependencyProperty.Register(
            nameof(CopilotActivityStatus), typeof(string), typeof(DevHubPanel),
            new PropertyMetadata("Local Copilot activity not connected."));

        public string CopilotActivityStatus
        {
            get => (string)GetValue(CopilotActivityStatusProperty);
            private set => SetValue(CopilotActivityStatusProperty, value);
        }

        private CopilotHarnessAdapter _copilotAdapter;
        private readonly CopilotActivityCache _copilotCache = new();
        private IComponentModel _copilotComponentModel;
        private DispatcherTimer _copilotRenderTimer;
        private string[] _copilotWorkspaceRoots = Array.Empty<string>();
        private bool _copilotScoped;
        private bool _copilotLoaded;
        private bool _copilotConnecting;
        private bool _copilotDiskCacheLoaded;
        private bool _copilotCacheSavedForConnection;
        private string _copilotCacheError;
        private int _copilotGeneration;

        internal void SetCopilotWorkspace(IEnumerable<string> roots, bool scoped)
        {
            _copilotWorkspaceRoots = roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _copilotScoped = scoped;
            RenderCopilotActivity();
        }

        internal void RefreshCopilotActivity()
        {
            StopCopilotActivity();
            StartCopilotActivity();
        }

        private void CopilotVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
                StartCopilotActivity();
            else
                StopCopilotActivity();
        }

        private void CopilotUnloaded(object sender, RoutedEventArgs e)
        {
            _copilotLoaded = false;
            StopCopilotActivity();
        }

        private void StartCopilotActivity()
        {
            if (_isDisposed || StartScreenPackage.ShutdownToken.IsCancellationRequested
                || !_copilotLoaded || !IsVisible || _copilotAdapter != null || _copilotConnecting)
                return;
            ConnectCopilotActivityAsync().FileAndForget(nameof(DevHubPanel));
        }

        private async Task ConnectCopilotActivityAsync()
        {
            int generation = ++_copilotGeneration;
            _copilotConnecting = true;
            CopilotActivityStatus = "Connecting to local Copilot activity...";
            RenderCopilotActivity();
            try
            {
                if (!_copilotDiskCacheLoaded)
                {
                    try
                    {
                        var cached = await CopilotActivityCache.ReadAsync(CopilotActivityCache.DefaultFile, StartScreenPackage.ShutdownToken);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                        if (generation != _copilotGeneration || !_copilotLoaded || !IsVisible)
                            return;
                        _copilotCache.Update(cached, false);
                        _copilotDiskCacheLoaded = true;
                        RenderCopilotActivity();
                        if (cached.Count > 0)
                            StartupDiagnostics.Mark("Copilot cached cards bound");
                    }
                    catch (OperationCanceledException) when (StartScreenPackage.ShutdownToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await ex.LogAsync();
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                        if (generation != _copilotGeneration || !_copilotLoaded || !IsVisible)
                            return;
                        _copilotCacheError = "Copilot history cache could not be loaded. Connecting to Copilot...";
                        CopilotActivityStatus = _copilotCacheError;
                    }
                }
                await StartScreenPackage.BackgroundTaskFactory.YieldAsync(DispatcherPriority.Background, StartScreenPackage.ShutdownToken);
                if (generation != _copilotGeneration || !_copilotLoaded || !IsVisible)
                    return;
                StartupDiagnostics.Mark("Copilot service connection started");
                var componentModel = await VS.GetServiceAsync<SComponentModel, IComponentModel>();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                if (generation != _copilotGeneration || !_copilotLoaded || !IsVisible)
                    return;
                _copilotComponentModel = componentModel ?? throw new NotSupportedException("Visual Studio's component model is unavailable.");
                var backends = GetCopilotExport(CopilotHarnessAdapter.BackendsContract);
                _copilotAdapter = new CopilotHarnessAdapter(callback =>
                {
                    if (Dispatcher.CheckAccess())
                        callback();
                    else if (!Dispatcher.HasShutdownStarted)
                        StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                            callback();
                        }).FileAndForget(nameof(DevHubPanel));
                }, ReportCopilotFailure);
                _copilotAdapter.Changed += CopilotActivityChanged;
                _copilotAdapter.Start(backends);
                StartupDiagnostics.Mark("Copilot harness attached");
                RenderCopilotActivity();
            }
            catch (OperationCanceledException ex) when (StartScreenPackage.ShutdownToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                StopCopilotActivity();
            }
            catch (Exception ex)
            {
                if (generation == _copilotGeneration)
                    ReportCopilotFailure(ex);
                else
                    await ex.LogAsync();
            }
            finally
            {
                if (generation == _copilotGeneration)
                    _copilotConnecting = false;
            }
        }

        private object GetCopilotExport(string contract)
        {
            var definition = new ContractBasedImportDefinition(contract, null, null,
                ImportCardinality.ZeroOrOne, false, false, CreationPolicy.Any);
            return _copilotComponentModel.DefaultExportProvider.GetExports(definition).SingleOrDefault()?.Value
                ?? throw new NotSupportedException($"The installed Copilot does not export {contract}.");
        }

        private void CopilotActivityChanged(object sender, EventArgs e)
        {
            if (_isDisposed)
                return;
            if (_copilotRenderTimer == null)
            {
                _copilotRenderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(150),
                };
                _copilotRenderTimer.Tick += CopilotRenderTimer_Tick;
            }
            _copilotRenderTimer.Start();
        }

        private void CopilotRenderTimer_Tick(object sender, EventArgs e)
        {
            _copilotRenderTimer?.Stop();
            if (!_isDisposed)
                RenderCopilotActivity();
        }

        private IReadOnlyList<CopilotActivitySession> GetVisibleCopilotSessions()
        {
            var sessions = _copilotAdapter == null ? _copilotCache.Sessions
                : _copilotCache.Update(_copilotAdapter.Sessions, _copilotAdapter.IsReady);
            return sessions.Where(session => !_copilotScoped
                || _copilotWorkspaceRoots.Any(session.MatchesWorkspace)).ToArray();
        }

        private void RenderCopilotActivity()
        {
            if (_isDisposed)
                return;
            if (_copilotAdapter != null)
            {
                CopilotActivityStatus = _copilotCacheError ?? (_copilotAdapter.IsReady ? string.Empty : "Discovering local Copilot sessions...");
            }
            RefreshWorkspaceActivity();
            if (_copilotAdapter?.IsReady == true && !_copilotCacheSavedForConnection)
            {
                _copilotCacheSavedForConnection = true;
                PersistCopilotCache();
            }
        }

        private void PersistCopilotCache()
        {
            var snapshot = _copilotCache.Sessions;
            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await CopilotActivityCache.WriteAsync(CopilotActivityCache.DefaultFile, snapshot, StartScreenPackage.ShutdownToken);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                    if (_isDisposed)
                        return;
                    _copilotCacheError = null;
                    if (_copilotAdapter != null)
                        CopilotActivityStatus = _copilotAdapter.IsReady ? string.Empty : "Discovering local Copilot sessions...";
                }
                catch (OperationCanceledException ex) when (StartScreenPackage.ShutdownToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                    _copilotCacheError = "Copilot history cache could not be saved.";
                    CopilotActivityStatus = _copilotCacheError;
                }
            }).FileAndForget(nameof(DevHubPanel));
        }

        private void ReportCopilotFailure(Exception ex)
        {
            ex.LogAsync().FileAndForget(nameof(DevHubPanel));
            StopCopilotActivity();
            CopilotActivityStatus = "Local Copilot activity unavailable. Open Copilot Chat and refresh. This experimental integration requires compatible VS harness contracts.";
        }

        private void StopCopilotActivity()
        {
            _copilotGeneration++;
            _copilotConnecting = false;
            if (_copilotRenderTimer != null)
            {
                _copilotRenderTimer.Stop();
                _copilotRenderTimer.Tick -= CopilotRenderTimer_Tick;
                _copilotRenderTimer = null;
            }
            var adapter = _copilotAdapter;
            if (adapter != null)
            {
                _copilotCache.Update(adapter.Sessions, adapter.IsReady);
                if (!StartScreenPackage.ShutdownToken.IsCancellationRequested
                    && (adapter.IsReady || _copilotCache.Sessions.Count > 0))
                    PersistCopilotCache();
            }
            _copilotCacheSavedForConnection = false;
            _copilotAdapter = null;
            if (adapter != null)
            {
                adapter.Changed -= CopilotActivityChanged;
                adapter.Dispose();
            }
            _copilotComponentModel = null;
            if (_isDisposed)
                return;
            CopilotActivityStatus = "Local Copilot activity is disconnected.";
            RefreshWorkspaceActivity();
        }

        private async Task OpenCopilotSessionAsync(string sessionId)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(StartScreenPackage.ShutdownToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(timeout.Token);
                while (_copilotConnecting || (_copilotAdapter != null && !_copilotAdapter.IsReady && !_copilotAdapter.HasSession(sessionId)))
                    await System.Threading.Tasks.Task.Delay(100, timeout.Token);
                var adapter = _copilotAdapter ?? throw new InvalidOperationException("Copilot activity is disconnected. Refresh and try again.");
                adapter.FindSession(sessionId);
                var shell = await VS.GetServiceAsync<SVsUIShell, IVsUIShell>();
                var chatWindow = new Guid("40ea2e6b-2121-4bb8-a43e-c83c04b51041");
                ErrorHandler.ThrowOnFailure(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref chatWindow, out IVsWindowFrame frame));
                ErrorHandler.ThrowOnFailure(frame.Show());

                var registry = GetCopilotExport(CopilotHarnessAdapter.NavigatorRegistryContract);
                object navigator;
                while ((navigator = CopilotHarnessAdapter.Read(registry, "Current")) == null)
                    await System.Threading.Tasks.Task.Delay(100, timeout.Token);

                if (!ReferenceEquals(adapter, _copilotAdapter))
                    throw new InvalidOperationException("Copilot activity changed while opening this session. Try again.");
                adapter.OpenSession(navigator, sessionId);
            }
            catch (OperationCanceledException ex) when (!StartScreenPackage.ShutdownToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", "Copilot Chat did not become ready in time. Open Copilot Chat and try again.");
            }
            catch (OperationCanceledException ex) when (StartScreenPackage.ShutdownToken.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not open this Copilot conversation. Refresh the Start Screen and try again. Details are in the Activity Log.");
            }
        }
    }
}
