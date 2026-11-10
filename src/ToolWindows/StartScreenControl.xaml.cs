using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;
using StartScreen.Services.DevHub;
using StartScreen.Helpers;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenControl : UserControl, IDisposable
    {
        private readonly StartScreenViewModel _viewModel;
        private readonly JoinableTask _loadTask;
        private DropIndicatorAdorner _dropAdorner;
        private readonly DevHubService _devHubService;
        private readonly DevHubStartupLoad _devHubStartup;
        private JoinableTask _controlLoadTask;
        private bool _isInitialized;
        private bool _isSynchronizingContentHorizontalScroll;
        private readonly CancellationToken _shutdownToken = StartScreenPackage.ShutdownToken;
        private CancellationTokenSource _lifetimeCancellation;
        private CancellationTokenSource _workspaceCancellation;
        private DevHubDashboard _workspaceDashboard;
        private string _workspaceError;
        private bool _isDisposed;
        private int _devHubRefreshGeneration;
        private bool _isGlobalDevHubRefreshing;
        private bool _isWorkspaceDevHubRefreshing;
        private string _dragPath;
        private bool _isDragPathOpenable;

        private StartScreenViewModel ViewModel => DataContext as StartScreenViewModel;

        internal StartScreenControl(StartScreenViewModel viewModel, JoinableTask loadTask, Options options, DevHubStartupLoad devHubStartup)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _viewModel = viewModel;
            _loadTask = loadTask;
            _devHubStartup = devHubStartup;
            _devHubService = devHubStartup?.Service ?? new DevHubService();
            _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            InitializeComponent();
            DevHubPanelControl.Progress = viewModel.BackgroundProgress;
            ApplyInitialMruWidth(LeftColumn, options.SplitterPosition);

            // Bind the ViewModel up front so the MRU list pops in as soon as
            // LoadMruAsync raises PropertyChanged on GroupedMruItems, instead of
            // waiting until the Loaded handler awaits _loadTask before binding.
            DataContext = _viewModel;
            _viewModel.LoadWorkspaceTagsAsync(options.WorkspaceTagsJson).FileAndForget(nameof(StartScreenControl));
            ApplyDevHubVisibility(options.DevHubEnabled);
            if (options.DevHubEnabled && _devHubService.CurrentDashboard != null)
                ApplyStartupDevHubDashboard(_devHubService.CurrentDashboard);
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.ActivityRefreshRequested += ActivityRefreshRequested;
            _viewModel.EnableMruViewportTracking();
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution += WorkspaceSolutionOpened;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenFolder += WorkspaceFolderOpened;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenProject += WorkspaceProjectOpened;
            StartupDiagnostics.Mark("control created and search ready");
        }

        private void WorkspaceSolutionOpened(object sender, Microsoft.VisualStudio.Shell.Events.OpenSolutionEventArgs e)
        {
            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var solution = await VS.Solutions.GetCurrentSolutionAsync();
                    if (solution != null)
                        await _viewModel.RecordWorkspaceVisitAsync(solution.FullPath);
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    OutputLog.Info("Workspace", "Could not identify the opened workspace for its visit checkpoint.");
                }
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void WorkspaceFolderOpened(object sender, Microsoft.VisualStudio.Shell.Events.FolderEventArgs e)
        {
            StartScreenPackage.BackgroundTaskFactory.RunAsync(() => _viewModel.RecordWorkspaceVisitAsync(e.FolderPath))
                .FileAndForget(nameof(StartScreenControl));
        }

        private void WorkspaceProjectOpened(object sender, Microsoft.VisualStudio.Shell.Events.OpenProjectEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(e.Hierarchy.GetCanonicalName(Microsoft.VisualStudio.VSConstants.VSITEMID_ROOT, out var path));
                StartScreenPackage.BackgroundTaskFactory.RunAsync(() => _viewModel.RecordWorkspaceVisitAsync(path))
                    .FileAndForget(nameof(StartScreenControl));
            }
            catch (Exception ex)
            {
                ex.LogAsync().FileAndForget(nameof(StartScreenControl));
                OutputLog.Info("Workspace", "Could not identify the opened project for its visit checkpoint.");
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isDisposed || _shutdownToken.IsCancellationRequested)
                return;

            StartupDiagnostics.Mark("control loaded");
            if (_lifetimeCancellation.IsCancellationRequested)
            {
                _lifetimeCancellation.Dispose();
                _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            }

            _viewModel.ActivateBackgroundWork();
            if (ReferenceEquals(NewsTabs.SelectedItem, VideosTab))
                ActivateVideosTab();
            else if (ReferenceEquals(NewsTabs.SelectedItem, EventsTab))
                ActivateEventsTab();
            CancellationToken cancellationToken = _lifetimeCancellation.Token;
            JoinableTask previousLoad = _controlLoadTask;
            _controlLoadTask = StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                if (previousLoad != null)
                {
                    try
                    {
                        await previousLoad.JoinAsync(cancellationToken);
                    }
                    catch (OperationCanceledException ex)
                    {
                        await ex.LogAsync();
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                    await LoadControlAsync(cancellationToken);
            });
            _controlLoadTask.FileAndForget(nameof(StartScreenControl));
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_isDisposed)
                return;
            _dragPath = null;
            _isDragPathOpenable = false;
            _lifetimeCancellation.Cancel();
            _devHubStartup?.Cancel();
            _workspaceCancellation?.Cancel();
            _devHubRefreshGeneration++;
            _isGlobalDevHubRefreshing = false;
            _isWorkspaceDevHubRefreshing = false;
            _viewModel.CancelBackgroundWork();
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_isDisposed)
                return;
            _isDisposed = true;
            _lifetimeCancellation.Cancel();
            _devHubStartup?.Dispose();
            _workspaceCancellation?.Cancel();
            _workspaceCancellation?.Dispose();
            _workspaceCancellation = null;
            _lifetimeCancellation.Dispose();
            Loaded -= UserControl_Loaded;
            Unloaded -= UserControl_Unloaded;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.ActivityRefreshRequested -= ActivityRefreshRequested;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution -= WorkspaceSolutionOpened;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenFolder -= WorkspaceFolderOpened;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenProject -= WorkspaceProjectOpened;
            DevHubPanelControl.Dispose();
            _viewModel.Dispose();
        }

        private async Task LoadControlAsync(CancellationToken cancellationToken)
        {
            try
            {
                // If already initialized, just refresh in background
                if (_isInitialized)
                {
                    _viewModel.EnsureDeferredContentInitialized();
                    RefreshWorkspaceSignalsAsync(false).FileAndForget(nameof(StartScreenControl));
                    await Task.WhenAll(LoadDevHubAsync(), ViewModel.RefreshInBackgroundAsync());
                    cancellationToken.ThrowIfCancellationRequested();
                    return;
                }

                await ApplyDevHubEnabledAsync();

                UpdateResponsiveColumns();

                JoinableTask devHubTask = StartScreenPackage.BackgroundTaskFactory.RunAsync(LoadDevHubAsync);
                devHubTask.FileAndForget(nameof(StartScreenControl));

                await LoadInitialMruAsync(_loadTask, _viewModel.LoadMruAsync, cancellationToken);

                // Let rendering and pending user input run before secondary sections start.
                await StartScreenPackage.BackgroundTaskFactory.YieldAsync(
                    System.Windows.Threading.DispatcherPriority.ContextIdle, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                StartupDiagnostics.Mark("MRU render yielded to input");

                _viewModel.EnsureDeferredContentInitialized();

                Task newsTask = _viewModel.RefreshInBackgroundAsync(refreshMru: false);
                RefreshWorkspaceSignalsAsync(false).FileAndForget(nameof(StartScreenControl));

                _isInitialized = true;
                await Task.WhenAll(newsTask, devHubTask.Task);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void ReleaseNotesLink_Click(object sender, RoutedEventArgs e)
        {
            VS.Commands.ExecuteAsync("Help.ReleaseNotes").FileAndForget(nameof(StartScreenControl));
        }

        internal static async Task LoadInitialMruAsync(JoinableTask loadTask, Func<Task> reload, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (loadTask.Task.IsCanceled || loadTask.Task.IsFaulted)
                    await reload();
                else
                    await loadTask.JoinAsync(cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
        }

        internal static void ApplyInitialMruWidth(ColumnDefinition column, double splitterPosition)
        {
            if (splitterPosition >= 280 && splitterPosition <= 480)
            {
                column.Width = new GridLength(splitterPosition);
            }
        }

        private void GridSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            Options.Instance.SplitterPosition = LeftColumn.ActualWidth;
            Options.Instance.SaveAsync().FileAndForget(nameof(StartScreenControl));
        }

        private void RightPaneBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateResponsiveColumns();
        }

        private void UpdateResponsiveColumns()
        {
            if (ViewModel == null || RightPaneBorder.ActualWidth == 0)
                return;

            bool showWork = DevHubPanelControl.Visibility == Visibility.Visible;
            double contentWidth = Math.Max(0, RightPaneBorder.ActualWidth - NewsPanel.Margin.Left - NewsPanel.Margin.Right);
            var layout = CalculateContentLayout(contentWidth, showWork);
            ContentLayout.Width = Math.Max(contentWidth, showWork ? 1400 : 440);
            DevHubColumn.Width = new GridLength(layout.WorkWidth);
            NewsColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(NewsColumnPanel, showWork ? 1 : 0);
            Grid.SetColumnSpan(NewsColumnPanel, showWork ? 1 : 2);
            ViewModel.UpdateColumnCounts(layout.NewsColumns, layout.VideoColumns);
        }

        internal static (double WorkWidth, bool Stacked, int NewsColumns, int VideoColumns) CalculateContentLayout(double width, bool showWork)
        {
            double workWidth = showWork ? Math.Min(1000, Math.Max(1400, width) - 440) : 0;
            double newsWidth = showWork ? Math.Max(1400, width) - workWidth : Math.Max(440, width);
            return (workWidth, false, Math.Max(1, (int)(newsWidth / 332)), Math.Max(1, (int)(newsWidth / 292)));
        }

        private void NewProjectButton_Click(object sender, RoutedEventArgs e)
        {
            VsCommandService.NewProjectAsync().FileAndForget(nameof(StartScreenControl));
        }

        private void OpenProjectButton_Click(object sender, RoutedEventArgs e)
        {
            VsCommandService.OpenProjectAsync().FileAndForget(nameof(StartScreenControl));
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            VsCommandService.OpenFolderAsync().FileAndForget(nameof(StartScreenControl));
        }

        private void CloneRepoButton_Click(object sender, RoutedEventArgs e)
        {
            VsCommandService.CloneRepositoryAsync().FileAndForget(nameof(StartScreenControl));
        }

        private void AttachToProcessButton_Click(object sender, RoutedEventArgs e)
        {
            VsCommandService.AttachToProcessAsync().FileAndForget(nameof(StartScreenControl));
        }

        private void AttachToProcessButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Button button))
                return;

            Point position = e.GetPosition(button);
            const double dropDownWidth = 25;

            if (position.X >= button.ActualWidth - dropDownWidth)
            {
                e.Handled = true;
                ShowAttachToProcessMenu(button);
            }
        }

        private void ReattachToProcessMenuItem_Click(object sender, RoutedEventArgs e)
        {
            VsCommandService.ReattachToProcessAsync().FileAndForget(nameof(StartScreenControl));
        }

        private static void ShowAttachToProcessMenu(Button button)
        {
            var menu = button.ContextMenu;
            ThemedContextMenuHelper.ApplyVsTheme(menu);
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.SearchFilter = SearchBox.Text;
            }
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Clear();
                }
                else
                {
                    FocusFirstMruItem();
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                MruItemControl firstMru = MruItemControl.GetItemControl(MruPanel, 0);
                if (firstMru != null)
                {
                    firstMru.FocusItem();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Up)
            {
                if (ActionBar.Children.Count > 0 && ActionBar.Children[0] is Button btn)
                {
                    btn.Focus();
                }
                e.Handled = true;
            }
        }

        private void MruItemControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is MruItemControl control && control.DataContext is MruItem item)
            {
                ViewModel?.SetMruItemRealized(item, true);
                if (!_isDisposed && ViewModel != null)
                    StartScreenPackage.BackgroundTaskFactory.RunAsync(() =>
                        ViewModel.RefreshSelectedRepositorySummaryAsync(item, _lifetimeCancellation.Token))
                        .FileAndForget(nameof(StartScreenControl));
            }
        }

        private void MruItemControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is MruItemControl control && control.DataContext is MruItem item)
                ViewModel?.SetMruItemRealized(item, false);
        }

        private void MruItemControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is MruItem previous)
                ViewModel?.SetMruItemRealized(previous, false);
            if (sender is MruItemControl control && control.IsLoaded)
                MruItemControl_Loaded(sender, new RoutedEventArgs());
        }

        private void MruItemControl_PinToggleRequested(object sender, MruItem item)
        {
            if (_isDisposed || ViewModel == null)
                return;

            var hadFocus = sender is MruItemControl ctrl && ctrl.IsItemFocused();
            CancellationToken cancellationToken = _lifetimeCancellation.Token;

            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                await ViewModel.TogglePinAsync(item);

                if (hadFocus && !cancellationToken.IsCancellationRequested)
                {
                    // Allow layout to rebuild after collection change
                    await StartScreenPackage.BackgroundTaskFactory.YieldAsync(
                        System.Windows.Threading.DispatcherPriority.Loaded, cancellationToken);
                    FocusMruItemByPath(item.Path);
                }
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void MruItemControl_RemoveRequested(object sender, MruItem item)
        {
            if (ViewModel != null)
            {
                StartScreenPackage.BackgroundTaskFactory.RunAsync(
                    () => ViewModel.RemoveMruItemAsync(item)).FileAndForget(nameof(StartScreenControl));
            }
        }

        private void ClearRecentButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var shell = ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell;
            if (shell == null)
                return;

            shell.UpdateCommandUI(1);

            Point screenPoint = ClearRecentButton.PointToScreen(new Point(0, ClearRecentButton.ActualHeight));
            Guid commandSet = PackageGuids.StartScreen;
            var points = new[]
            {
                new POINTS
                {
                    x = (short)screenPoint.X,
                    y = (short)screenPoint.Y
                }
            };

            shell.ShowContextMenu(0, ref commandSet, PackageIds.ClearRecentMenu, points, null);
        }

        private void MruItemControl_FocusSearchBoxRequested(object sender, EventArgs e)
        {
            SearchBox.Focus();
        }

        private void MruItemControl_FocusDevHubRequested(object sender, EventArgs e)
        {
            if (!DevHubPanelControl.FocusFirstItem(DevHubDefaultTab.MyWork))
                FocusFirstNewsItem();
        }

        private void DevHubPanel_FocusMruRequested(object sender, EventArgs e)
        {
            // Focus the currently selected MRU item, or the first one
            MruItemControl mru = ViewModel.SelectedMruItem != null
                ? FindMruItemControlByPath(MruPanel, ViewModel.SelectedMruItem.Path) : null;
            mru ??= MruItemControl.GetItemControl(MruPanel, 0);
            if (mru != null)
            {
                mru.RootBorder.Focus();
            }
            else
            {
                SearchBox.Focus();
            }
        }

        private void DevHubPanel_FocusNewsRequested(object sender, EventArgs e)
        {
            FocusFirstNewsItem();
        }

        private void FocusFirstNewsItem()
        {
            NewsKeyboardNavigation.FocusFirstItem(NewsTabs);
        }

        private void NewsTabs_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.None)
                e.Handled = NewsKeyboardNavigation.HandleKey(NewsTabs, Keyboard.FocusedElement as DependencyObject, e.Key, () =>
                {
                    if (!DevHubPanelControl.FocusFirstItem(DevHubDefaultTab.Incoming))
                        DevHubPanel_FocusMruRequested(this, EventArgs.Empty);
                });
        }

        private void DevHubPanel_RefreshRequested(object sender, EventArgs e)
        {
            if (_isDisposed || _lifetimeCancellation.IsCancellationRequested)
                return;
            DevHubPanelControl.RefreshCopilotActivity();
            // Don't fetch Dev Hub data when the section is hidden. The unified
            // refresh button routes through here, so this keeps a refresh from
            // hitting GitHub/Azure DevOps while the panel is collapsed.
            if (!Options.Instance.DevHubEnabled)
            {
                return;
            }

            CancellationToken cancellationToken = _lifetimeCancellation.Token;
            if (_devHubService.CurrentDashboard == null)
            {
                _workspaceDashboard = null;
                _workspaceError = null;
                _workspaceCancellation?.Cancel();
                DevHubPanelControl.UpdateView(null, ViewModel.SelectedRepository);
            }
            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                try
                {
                    // Only show loading spinner if we have no data to display
                    if (_devHubService.CurrentDashboard == null || !_devHubService.CurrentDashboard.HasAuthentication)
                    {
                        DevHubPanelControl.ShowLoading();
                    }

                    await Task.WhenAll(RefreshDevHubInBackgroundAsync(cancellationToken, forceRefresh: true),
                        RefreshWorkspaceSignalsAsync(true));
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // Only show error if we have no cached data to fall back to
                    if (!cancellationToken.IsCancellationRequested
                        && (_devHubService.CurrentDashboard == null || !_devHubService.CurrentDashboard.HasAuthentication))
                    {
                        DevHubPanelControl.ShowError("Failed to refresh. Check your connection and try again.");
                    }
                }
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void DevHubPanel_ConnectAccountRequested(object sender, string host)
        {
            if (_isDisposed || _lifetimeCancellation.IsCancellationRequested)
                return;
            CancellationToken cancellationToken = _lifetimeCancellation.Token;
            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                using var work = _viewModel.BackgroundProgress.Start(step: $"Connecting to {host}...");
                var loadingStarted = false;
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    DevHubPanelControl.SetActivityLoading(true);
                    loadingStarted = true;
                    DevHubPanelControl.ShowLoading($"Connecting to {host}...");

                    await TaskScheduler.Default;
                    if (host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase))
                    {
                        var options = await Options.GetLiveInstanceAsync();
                        options.DevHubAdoAuthentication = AzureDevOpsAuthenticationMode.VisualStudio;
                        await options.SaveAsync();
                        Services.DevHub.DevHubCredentialHelper.ClearCachedCredentials();
                        await RefreshDevHubInBackgroundAsync(cancellationToken, forceRefresh: true);

                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                        if (_devHubService.CurrentDashboard?.HasProvider(AzureDevOpsServerHelper.CloudHost) != true)
                            DevHubPanelControl.ShowAzureDevOpsSignInRequired();
                        RefreshWorkspaceSignalsAsync(true).FileAndForget(nameof(StartScreenControl));
                        return;
                    }

                    var connected = await Services.DevHub.DevHubCredentialHelper.ConnectInteractiveAsync(
                        host, cancellationToken);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (connected)
                    {
                        // Note: do NOT call ClearCachedCredentials here. ConnectInteractiveAsync
                        // already stored the freshly acquired credential in both the in-memory
                        // cache and Windows Credential Manager.
                        await RefreshDevHubInBackgroundAsync(cancellationToken, forceRefresh: true);
                    }
                    else if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                    {
                        DevHubPanelControl.ShowGitHubCredentialManagerUnavailable();
                    }
                    else
                    {
                        // GCM not available or user cancelled - fall back to showing panel state
                        UpdateDevHubPanel();
                    }
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    if (host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase))
                        DevHubPanelControl.ShowAzureDevOpsSignInRequired();
                    else
                        DevHubPanelControl.ShowError("Could not connect the account. Check your sign-in and retry.");
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (loadingStarted && !_isDisposed)
                        DevHubPanelControl.SetActivityLoading(false);
                }
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void OpenAccountSettings_Click(object sender, EventArgs e)
        {
            if (_isDisposed || _lifetimeCancellation.IsCancellationRequested)
                return;
            StartScreenPackage.BackgroundTaskFactory.RunAsync(VsCommandService.OpenAccountSettingsAsync)
                .FileAndForget(nameof(StartScreenControl));
        }

        private void DevHubPanel_ScopeToRepoRequested(object sender, RemoteRepoIdentifier repo)
        {
            ViewModel.SelectedMruItem = null;
            ViewModel.SetRepositoryScope(repo);
        }

        private void RefreshWorkspaceScope(bool forceRefresh = false)
        {
            if (_isDisposed || _lifetimeCancellation.IsCancellationRequested)
                return;
            _workspaceDashboard = null;
            _workspaceError = null;
            _workspaceCancellation?.Cancel();
            _workspaceCancellation?.Dispose();
            _workspaceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            CancellationToken cancellationToken = _workspaceCancellation.Token;
            _isWorkspaceDevHubRefreshing = false;
            UpdateDevHubLoadingState();
            RemoteRepoIdentifier repo = ViewModel.SelectedRepository;
            DevHubPanelControl.SetWorkspaceFilter(repo);
            UpdateDevHubPanel();
            if (repo == null || !Options.Instance.DevHubEnabled)
                return;

            var activityOptions = DevHubPanelControl.ActivityOptions;
            DevHubPanelControl.SetActivityLoading(true);
            _isWorkspaceDevHubRefreshing = true;
            UpdateDevHubLoadingState();
            var scopeProgress = new Progress<DevHubDashboard>(dashboard =>
            {
                if (_isDisposed || !_isWorkspaceDevHubRefreshing || cancellationToken.IsCancellationRequested
                    || !Equals(repo, ViewModel.SelectedRepository)
                    || dashboard.CredentialGeneration != DevHubCredentialHelper.AccountGeneration
                    || dashboard.QueryContext != (Options.Instance.DevHubSearchQuery ?? string.Empty))
                    return;
                _workspaceDashboard = dashboard;
                UpdateDevHubPanel(dashboard);
            });
            ViewModel.RefreshSelectedRepositorySummaryAsync(ViewModel.SelectedMruItem, cancellationToken)
                .FileAndForget(nameof(StartScreenControl));
            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                using var work = _viewModel.BackgroundProgress.Start(step: $"Refreshing {repo.DisplayName}...");
                try
                {
                    await TaskScheduler.Default;
                    var dashboard = await _devHubService.ScopeToRepoAsync(repo, cancellationToken, activityOptions, forceRefresh, scopeProgress);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Equals(repo, ViewModel.SelectedRepository))
                    {
                        if (dashboard == null)
                        {
                            _workspaceError = "Repository activity is unavailable. Check your connection and account access, then refresh.";
                            UpdateDevHubPanel();
                        }
                        else
                        {
                            _workspaceDashboard = dashboard;
                            UpdateDevHubPanel(dashboard);
                        }
                    }
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    if (!_isDisposed)
                    {
                        await ThreadHelper.JoinableTaskFactory.RunOnMainThreadForCleanupAsync(_shutdownToken, () =>
                        {
                            if (_isDisposed || cancellationToken.IsCancellationRequested)
                                return;
                            _workspaceError = "Could not load repository activity. Refresh to try again.";
                            UpdateDevHubPanel();
                        });
                    }
                }
                finally
                {
                    if (!_isDisposed)
                    {
                        await ThreadHelper.JoinableTaskFactory.RunOnMainThreadForCleanupAsync(_shutdownToken, () =>
                        {
                            if (_isDisposed)
                                return;
                            DevHubPanelControl.SetActivityLoading(false);
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                _isWorkspaceDevHubRefreshing = false;
                                UpdateDevHubLoadingState();
                            }
                        });
                    }
                }
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_isDisposed)
                return;
            if (e.PropertyName == nameof(StartScreenViewModel.SelectedRepository))
            {
                RefreshWorkspaceScope();
            }
            else if (e.PropertyName == nameof(StartScreenViewModel.ShowSolutionsOnly))
            {
                SaveSolutionsOnlyAsync().FileAndForget(nameof(StartScreenControl));
            }
        }

        private async Task SaveSolutionsOnlyAsync()
        {
            try
            {
                Options options = await Options.GetLiveInstanceAsync();
                options.ShowSolutionsOnly = ViewModel.ShowSolutionsOnly;
                await options.SaveAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void MruItemControl_SelectionRequested(object sender, MruItem item)
        {
            ViewModel.SetMruSelection(MruPanel.SelectedItems.Cast<MruItem>().ToArray());
        }

        private void MruPanel_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_isDisposed && ReferenceEquals(e.OriginalSource, MruPanel))
                ViewModel.SetMruSelection(MruPanel.SelectedItems.Cast<MruItem>().ToArray());
        }

        private void MruItemControl_RepositorySummaryRequested(object sender, MruItem item)
        {
            MruItemControl.SelectMruItem(MruPanel, item, false, false);
            ViewModel.SelectedMruItem = item;
            DevHubPanelControl.ShowPullRequests();
        }

        private void MruItemControl_RepositoryReviewsRequested(object sender, MruItem item)
        {
            MruItemControl.SelectMruItem(MruPanel, item, false, false);
            ViewModel.SelectedMruItem = item;
            DevHubPanelControl.ShowRequestedReviews();
        }

        private void ServiceStatusLink_Click(object sender, RoutedEventArgs e)
        {
            OpenServiceStatusAsync().FileAndForget(nameof(StartScreenControl));
        }

        private static async Task OpenServiceStatusAsync()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://vsstatus.visualstudio.com/")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not open the service status page.");
            }
        }

        internal void ApplyStartupDevHubDashboard(DevHubDashboard dashboard)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_isDisposed && !_lifetimeCancellation.IsCancellationRequested && Options.Instance.DevHubEnabled
                && ReferenceEquals(dashboard, _devHubService.CurrentDashboard))
            {
                UpdateDevHubPanel(dashboard, cached: true);
            }
        }

        private void ActivityRefreshRequested(object sender, EventArgs e)
        {
            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                if (_isDisposed || _lifetimeCancellation.IsCancellationRequested || !Options.Instance.DevHubEnabled)
                    return;
                var cancellationToken = _lifetimeCancellation.Token;
                await TaskScheduler.Default;
                if (_devHubService.IsCacheStale())
                    await RefreshDevHubInBackgroundAsync(cancellationToken);
            }).FileAndForget(nameof(StartScreenControl));
        }

        private async Task LoadDevHubAsync()
        {
            if (_isDisposed || _lifetimeCancellation.IsCancellationRequested)
                return;
            CancellationToken cancellationToken = _lifetimeCancellation.Token;
            var loadingStarted = false;

            try
            {
                if (!Options.Instance.DevHubEnabled)
                    return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                DevHubPanelControl.SetActivityLoading(true);
                loadingStarted = true;
                var activityOptions = DevHubPanelControl.ActivityOptions;
                var progress = new Progress<DevHubDashboard>(dashboard =>
                {
                    if (!cancellationToken.IsCancellationRequested && ReferenceEquals(dashboard, _devHubService.CurrentDashboard))
                        UpdateDevHubPanel(dashboard, cached: true);
                });
                await TaskScheduler.Default;
                async Task<DevHubDashboard> ReloadCacheAsync()
                {
                    using var work = _viewModel.BackgroundProgress.Start(step: "Reading cached activity...");
                    return await _devHubService.LoadFromCacheAsync(cancellationToken, activityOptions, progress, work, work);
                }
                var cached = _devHubStartup != null
                    ? await _devHubStartup.LoadAsync(cancellationToken, ReloadCacheAsync)
                    : await ReloadCacheAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (cached != null && cached.HasAuthentication)
                {
                    OutputLog.Info("Activity", $"Restored {cached.PullRequests.Count} PRs, {cached.Issues.Count} issues, and {cached.CiRuns.Count} builds from disk after verifying the current accounts.");
                    // Show cached data immediately (same pattern as News/MRU)
                    UpdateDevHubPanel(cached, cached: true);
                    StartupDiagnostics.Mark("Dev Hub cache displayed");

                    // Refresh in background if stale - don't block the UI
                    await TaskScheduler.Default;
                    var isStale = _devHubService.IsCacheStale();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (isStale)
                    {
                        await StartScreenPackage.BackgroundTaskFactory.YieldAsync(
                            System.Windows.Threading.DispatcherPriority.ContextIdle, cancellationToken);
                        StartScreenPackage.BackgroundTaskFactory.RunAsync(
                            () => RefreshDevHubInBackgroundAsync(cancellationToken))
                            .FileAndForget(nameof(StartScreenControl));
                    }
                }
                else
                {
                    // No usable cache - must do a full load
                    DevHubPanelControl.ShowLoading("Signing in to GitHub and Azure DevOps...");
                    await RefreshDevHubInBackgroundAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                if (!_isDisposed)
                    await ThreadHelper.JoinableTaskFactory.RunOnMainThreadForCleanupAsync(_shutdownToken, () =>
                    {
                        if (!_isDisposed && !cancellationToken.IsCancellationRequested)
                            UpdateDevHubPanel(_devHubService.CurrentDashboard, "Could not load Dev Hub data.");
                    });
            }
            finally
            {
                if (loadingStarted && !_isDisposed)
                    await ThreadHelper.JoinableTaskFactory.RunOnMainThreadForCleanupAsync(_shutdownToken, () =>
                    {
                        if (!_isDisposed)
                            DevHubPanelControl.SetActivityLoading(false);
                    });
            }
        }

        private async Task RefreshDevHubInBackgroundAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_isGlobalDevHubRefreshing && !forceRefresh)
                return;
            using var work = _viewModel.BackgroundProgress.Start(step: "Refreshing activity...");
            var activityOptions = DevHubPanelControl.ActivityOptions;
            DevHubPanelControl.SetActivityLoading(true);
            int generation = ++_devHubRefreshGeneration;
            _isGlobalDevHubRefreshing = true;
            UpdateDevHubLoadingState();
            try
            {
                // Construct Progress<T> while still on the UI thread so it captures
                // the UI SynchronizationContext and marshals UpdateDevHubPanel back
                // to the UI thread.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var progress = new Progress<DevHubDashboard>(dashboard =>
                {
                    if (!cancellationToken.IsCancellationRequested && ReferenceEquals(dashboard, _devHubService.CurrentDashboard))
                        UpdateDevHubPanel(dashboard);
                });

                // Now hop to the thread pool BEFORE doing any DevHub work. The
                // provider auth path eventually calls Process.Start("git", "credential
                // fill") and process.WaitForExit(...), both of which are synchronous
                // and would block the UI thread for several seconds per provider if
                // we stayed on the captured UI SynchronizationContext.
                await TaskScheduler.Default;

                await _devHubService.RefreshAsync(cancellationToken, progress, forceRefresh, activityOptions, work, work);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                UpdateDevHubPanel();
                if (ViewModel.SelectedRepository != null)
                    RefreshWorkspaceScope(forceRefresh);
                StartupDiagnostics.Mark("Dev Hub remote refresh completed");
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                // Only show error if we have no cached data to fall back to
                if (_isDisposed)
                    return;
                await ThreadHelper.JoinableTaskFactory.RunOnMainThreadForCleanupAsync(_shutdownToken, () =>
                {
                    if (_isDisposed || cancellationToken.IsCancellationRequested)
                        return;
                    if (_devHubService.CurrentDashboard == null || !_devHubService.CurrentDashboard.HasAuthentication)
                    {
                        UpdateDevHubPanel(_devHubService.CurrentDashboard, "Could not load Dev Hub data.");
                    }
                    else
                    {
                        DevHubPanelControl.ShowActivityError("Activity could not be refreshed. Showing loaded items. Refresh to retry.");
                        ViewModel.SetDevHubRefreshState(_devHubService.CurrentDashboard,
                            cached: true, error: "Activity could not be refreshed. Showing loaded items. Refresh to retry.");
                    }
                });
            }
            finally
            {
                if (!_isDisposed)
                {
                    await ThreadHelper.JoinableTaskFactory.RunOnMainThreadForCleanupAsync(_shutdownToken, () =>
                    {
                        if (_isDisposed)
                            return;
                        DevHubPanelControl.SetActivityLoading(false);
                        if (!cancellationToken.IsCancellationRequested && generation == _devHubRefreshGeneration)
                        {
                            _isGlobalDevHubRefreshing = false;
                            UpdateDevHubLoadingState();
                        }
                    });
                }
            }
        }

        private void UpdateDevHubLoadingState()
        {
            if (!_isDisposed)
                ViewModel.IsRefreshingDevHub = _isGlobalDevHubRefreshing || _isWorkspaceDevHubRefreshing;
        }

        private void UpdateDevHubPanel()
        {
            var dashboard = _devHubService.CurrentDashboard;
            UpdateDevHubPanel(dashboard);
        }

        private void UpdateDevHubPanel(DevHubDashboard dashboard, string error = null, bool cached = false)
        {
            if (_isDisposed)
                return;
            if (_workspaceDashboard != null && (_workspaceDashboard.CredentialGeneration != DevHubCredentialHelper.AccountGeneration
                || _workspaceDashboard.QueryContext != (Options.Instance.DevHubSearchQuery ?? string.Empty)
                || DevHubService.HasScopedIdentityChanged(dashboard, _workspaceDashboard)))
            {
                _workspaceDashboard = null;
                _workspaceError = null;
                _workspaceCancellation?.Cancel();
            }
            ViewModel.SetDevHubRefreshState(_workspaceDashboard ?? dashboard, cached, error ?? _workspaceError);
            ViewModel.UpdateWorkspaceActivity(Options.Instance.DevHubEnabled ? dashboard : null,
                Options.Instance.DevHubEnabled ? _workspaceDashboard : null, cached);
            DevHubPanelControl.SetCopilotWorkspace(ViewModel.GetCopilotWorkspaceRoots(),
                ViewModel.SelectedMruItem != null || ViewModel.SelectedRepository != null);
            if (ViewModel.HasSelectedMruItem && ViewModel.SelectedRepository == null)
            {
                DevHubPanelControl.ShowLocalWorkspace();
                return;
            }
            if (_workspaceError != null || error != null)
            {
                DevHubPanelControl.ShowError(_workspaceError ?? error);
                return;
            }
            DevHubPanelControl.UpdateView(_workspaceDashboard ?? dashboard, ViewModel.SelectedRepository);
        }

        private void ActionBar_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!(e.OriginalSource is Button currentButton))
                return;

            var index = ActionBar.Children.IndexOf(currentButton);
            if (index < 0)
                return;

            if (e.Key == Key.Right)
            {
                var next = index + 1;
                if (next < ActionBar.Children.Count && ActionBar.Children[next] is Button nextBtn)
                {
                    nextBtn.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                var prev = index - 1;
                if (prev >= 0 && ActionBar.Children[prev] is Button prevBtn)
                {
                    prevBtn.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        }

        private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Oem3 && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        }

        private void PageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift && ContentScroll != null && ContentScroll.ScrollableWidth > 0)
            {
                ContentScroll.ScrollToHorizontalOffset(ContentScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void ContentScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift && ContentScroll != null && ContentScroll.ScrollableWidth > 0)
            {
                ContentScroll.ScrollToHorizontalOffset(ContentScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }
        }

        private void ContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (ContentHorizontalScrollbar == null)
            {
                return;
            }

            _isSynchronizingContentHorizontalScroll = true;

            try
            {
                var maximum = Math.Max(0, e.ExtentWidth - e.ViewportWidth);

                ContentHorizontalScrollbar.ViewportSize = e.ViewportWidth;
                ContentHorizontalScrollbar.Maximum = maximum;
                ContentHorizontalScrollbar.IsEnabled = maximum > 0;
                ContentHorizontalScrollbar.Visibility = maximum > 0 ? Visibility.Visible : Visibility.Collapsed;
                ContentHorizontalScrollbar.Value = Math.Max(0, Math.Min(maximum, e.HorizontalOffset));
            }
            finally
            {
                _isSynchronizingContentHorizontalScroll = false;
            }
        }

        private void ContentHorizontalScrollbar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSynchronizingContentHorizontalScroll || ContentScroll == null)
            {
                return;
            }

            ContentScroll.ScrollToHorizontalOffset(e.NewValue);
        }

        private void UserControl_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Only redirect focus to MRU when the outer control itself or a non-interactive
            // container gets focus (not when an inner control like Dev Hub items get focus)
            if (e.NewFocus == this || e.NewFocus == PageScroll)
            {
                FocusFirstMruItem();
            }
        }

        private void FocusFirstMruItem()
        {
            MruItemControl firstMru = MruItemControl.GetItemControl(MruPanel, 0);
            firstMru?.FocusItem();
        }

        private void FocusMruItemByPath(string path)
        {
            MruItemControl mruControl = FindMruItemControlByPath(MruPanel, path);
            mruControl?.FocusItem();
        }

        private static MruItemControl FindMruItemControlByPath(ListBox list, string path)
        {
            for (var index = 0; index < list.Items.Count; index++)
            {
                if (list.Items[index] is MruItem item && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                    return MruItemControl.GetItemControl(list, index);
            }
            return null;
        }

        private void PinnedItems_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            MruItemControl mruControl = FindAncestor<MruItemControl>(e.OriginalSource as DependencyObject);
            if (mruControl?.DataContext is MruItem item && FindAncestor<Button>(e.OriginalSource as DependencyObject) == null)
            {
                bool toggle = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                MruItemControl.SelectMruItem(MruPanel, item, toggle, false);
                if (toggle)
                {
                    mruControl.FocusItem(true);
                    e.Handled = true;
                }
            }
            mruControl?.HandlePreviewMouseLeftButtonDown(e);
        }

        private void PinnedItems_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            MruItemControl mruControl = FindAncestor<MruItemControl>(e.OriginalSource as DependencyObject);
            mruControl?.HandlePreviewMouseMove(e);
        }

        private void PinnedItems_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("PinnedMruItem"))
            {
                return;
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // Find the MruItemControl under the cursor to show drop indicator
            MruItemControl target = FindAncestor<MruItemControl>(e.OriginalSource as DependencyObject);
            if (target == null)
            {
                RemoveDropAdorner();
                return;
            }

            // Determine if dropping above or below the target
            Point pos = e.GetPosition(target);
            var insertAfter = pos.Y > target.ActualHeight / 2;

            ShowDropAdorner(target, insertAfter);
        }

        private void PinnedItems_DragLeave(object sender, DragEventArgs e)
        {
            RemoveDropAdorner();
        }

        private void PinnedItems_Drop(object sender, DragEventArgs e)
        {
            RemoveDropAdorner();

            if (!e.Data.GetDataPresent("PinnedMruItem") || ViewModel == null)
                return;

            var draggedItem = e.Data.GetData("PinnedMruItem") as MruItem;
            if (draggedItem == null)
                return;

            // Find the target MruItemControl
            MruItemControl target = FindAncestor<MruItemControl>(e.OriginalSource as DependencyObject);
            if (target == null || !(target.DataContext is MruItem targetItem) || !targetItem.IsPinned)
                return;

            // Determine insert index
            var targetIndex = ViewModel.PinnedItems.IndexOf(targetItem);
            if (targetIndex < 0)
                return;

            Point pos = e.GetPosition(target);
            if (pos.Y > target.ActualHeight / 2)
            {
                targetIndex++;
            }

            // Adjust if dragging from before the target position
            var currentIndex = ViewModel.PinnedItems.IndexOf(draggedItem);
            if (currentIndex >= 0 && currentIndex < targetIndex)
            {
                targetIndex--;
            }

            e.Handled = true;

            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                await ViewModel.MovePinnedItemAsync(draggedItem, targetIndex);
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void ShowDropAdorner(MruItemControl target, bool below)
        {
            RemoveDropAdorner();

            var layer = AdornerLayer.GetAdornerLayer(target);
            if (layer == null)
                return;

            _dropAdorner = new DropIndicatorAdorner(target, below);
            layer.Add(_dropAdorner);
        }

        private void RemoveDropAdorner()
        {
            if (_dropAdorner != null)
            {
                var layer = AdornerLayer.GetAdornerLayer(_dropAdorner.AdornedElement);
                layer?.Remove(_dropAdorner);
                _dropAdorner = null;
            }
        }

        private static T FindAncestor<T>(DependencyObject element) where T : DependencyObject
        {
            while (element != null)
            {
                if (element is T match)
                    return match;

                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private void NewsItemControl_PinToggleRequested(object sender, NewsPost post)
        {
            if (ViewModel != null)
            {
                StartScreenPackage.BackgroundTaskFactory.RunAsync(
                    () => ViewModel.ToggleNewsPinAsync(post)).FileAndForget(nameof(StartScreenControl));
            }
        }

        private void NewsFeedsSettings_Click(object sender, RoutedEventArgs e)
        {
            OpenNewsFeedsSettingsAsync().FileAndForget(nameof(StartScreenControl));
        }

        private async Task OpenNewsFeedsSettingsAsync()
        {
            try
            {
                await TaskScheduler.Default;
                var filePath = FeedStore.EnsureNewsFeedsFileAndGetPath();
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    // Start watching for changes
                    FeedStore.StartWatching();

                    // Open in VS
                    await VS.Documents.OpenAsync(filePath);
                }
                else
                {
                    await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not create the news feeds configuration file.");
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void StartScreenSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowStartScreenSettingsAsync().FileAndForget(nameof(StartScreenControl));
        }

        private async Task ShowStartScreenSettingsAsync()
        {
            try
            {
                Options options = await Options.GetLiveInstanceAsync();
                ShowDevHubCheckBox.IsChecked = options.DevHubEnabled;
                StartScreenSettingsPopup.IsOpen = true;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }



        private void ShowDevHubCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            SaveShowDevHubAsync().FileAndForget(nameof(StartScreenControl));
        }

        private async Task SaveShowDevHubAsync()
        {
            try
            {
                bool show = ShowDevHubCheckBox.IsChecked == true;
                Options options = await Options.GetLiveInstanceAsync();
                bool wasEnabled = options.DevHubEnabled;
                options.DevHubEnabled = show;
                await options.SaveAsync();

                ApplyDevHubVisibility(show);

                // When the user turns Dev Hub back on, load its data now since the
                // initial UserControl_Loaded pass skipped it while it was disabled.
                if (show && !wasEnabled)
                {
                    await LoadDevHubAsync();
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private async Task ApplyDevHubEnabledAsync()
        {
            try
            {
                Options options = await Options.GetLiveInstanceAsync();
                ApplyDevHubVisibility(options.DevHubEnabled);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void ApplyDevHubVisibility(bool show)
        {
            DevHubPanelControl.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DevHubSettingsLinkText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            UpdateResponsiveColumns();
        }

        private void DevHubPanel_LastRefreshChanged(object sender, string text)
        {
            if (ViewModel != null)
            {
                ViewModel.LastDevHubRefreshText = text ?? string.Empty;
            }
        }

        private void UnifiedRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.ForceRefreshNews();
                ViewModel.ForceRefreshYouTube();
                ViewModel.ForceRefreshFeedback();
                if (ReferenceEquals(NewsTabs.SelectedItem, EventsTab))
                    ActivateEventsTab(force: true);
            }

            DevHubPanel_RefreshRequested(this, System.EventArgs.Empty);
            if (!Options.Instance.DevHubEnabled)
                RefreshWorkspaceSignalsAsync(true).FileAndForget(nameof(StartScreenControl));
        }

        private async Task RefreshWorkspaceSignalsAsync(bool force)
        {
            if (_isDisposed || _lifetimeCancellation.IsCancellationRequested)
                return;
            CancellationToken cancellationToken = _lifetimeCancellation.Token;
            try
            {
                await ViewModel.RefreshWorkspaceSignalsAsync(force, cancellationToken);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!cancellationToken.IsCancellationRequested)
                {
                    ViewModel.SetServiceHealth("Service status unavailable", "Refresh to try checking service health again.", false);
                    ViewModel.SetRefreshState(ViewModel.WorkspaceRefreshState,
                        "Some workspace signals could not be refreshed. Refresh to retry.", partial: true);
                }
            }
        }

        private void DevHubSettingsLink_Click(object sender, RoutedEventArgs e)
        {
            StartScreenSettingsPopup.IsOpen = false;
            DevHubPanelControl?.ToggleSettings();
        }

        private void NewsFeedsSettingsLink_Click(object sender, RoutedEventArgs e)
        {
            StartScreenSettingsPopup.IsOpen = false;
            NewsFeedsSettings_Click(sender, e);
        }

        private void NewsTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Ignore selection changes that bubble up from nested ItemsControls.
            if (!ReferenceEquals(e.OriginalSource, NewsTabs))
            {
                return;
            }

            if (ViewModel == null)
            {
                return;
            }

            object selected = NewsTabs.SelectedItem;
            if (ReferenceEquals(selected, BlogsTab))
            {
                StartScreenPackage.BackgroundTaskFactory.RunAsync(
                    () => ViewModel.MarkBlogsAsSeenAsync())
                    .FileAndForget(nameof(StartScreenControl));
            }
            else if (ReferenceEquals(selected, VideosTab))
            {
                ActivateVideosTab();
            }
            else if (ReferenceEquals(selected, EventsTab))
            {
                ActivateEventsTab();
            }
            else if (ReferenceEquals(selected, FeedbackTab))
            {
                ViewModel.EnsureFeedbackLoaded();
            }
            else if (ReferenceEquals(selected, WhatsNewTab))
            {
                ViewModel.EnsureWhatsNewLoadedAsync(factory: StartScreenPackage.BackgroundTaskFactory)
                    .FileAndForget(nameof(StartScreenControl));
            }
        }

        private void ActivateVideosTab()
        {
            var videos = ViewModel.VideoSection;
            StartScreenPackage.BackgroundTaskFactory.RunAsync(videos.ActivateTabAsync)
                .FileAndForget(nameof(StartScreenControl));
        }

        private void FeedbackTicket_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            e.Handled = true;
            if (sender is Hyperlink link && link.DataContext is FeedbackTicket ticket)
            {
                StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ticket.Url) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        await ex.LogAsync();
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        ViewModel?.ReportFeedbackNavigationFailure();
                    }
                }).FileAndForget(nameof(StartScreenControl));
            }
        }

        private void UpdateNowButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateVisualStudioAsync().FileAndForget(nameof(StartScreenControl));
        }

        private async Task UpdateVisualStudioAsync()
        {
            try
            {
                if (ViewModel != null)
                {
                    await ViewModel.UpdateVisualStudioAsync();
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void NextTipButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.NextTip();
        }

        private void PrevTipButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.PreviousTip();
        }

        private void NextExtensionButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.NextExtension();
        }

        private void PrevExtensionButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.PreviousExtension();
        }

        private void InstallExtension_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ViewModel?.CurrentSuggestedExtension != null &&
                    !string.IsNullOrWhiteSpace(ViewModel.CurrentSuggestedExtension.MarketplaceUrl))
                {
                    System.Diagnostics.Process.Start(ViewModel.CurrentSuggestedExtension.MarketplaceUrl);
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        private static readonly string[] _openableExtensions = { ".sln", ".slnx", ".csproj", ".vbproj", ".fsproj", ".vcxproj" };

        private void UserControl_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);

                if (files != null && files.Length > 0)
                {
                    var path = files[0];
                    if (!string.Equals(_dragPath, path, StringComparison.Ordinal))
                    {
                        _dragPath = path;
                        _isDragPathOpenable = false;
                        UpdateDragPathAsync(path).FileAndForget(nameof(StartScreenControl));
                    }
                    e.Effects = _isDragPathOpenable ? DragDropEffects.Copy : DragDropEffects.None;
                    e.Handled = true;
                    return;
                }
            }

            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void UserControl_Drop(object sender, DragEventArgs e)
        {
            _dragPath = null;
            _isDragPathOpenable = false;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0)
                return;

            var path = files[0];
            OpenDroppedPathAsync(path).FileAndForget(nameof(StartScreenControl));
        }

        private async Task UpdateDragPathAsync(string path)
        {
            try
            {
                var openable = await IsOpenablePathAsync(path);
                if (!_isDisposed && string.Equals(_dragPath, path, StringComparison.Ordinal))
                    _isDragPathOpenable = openable;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private static async Task OpenDroppedPathAsync(string path)
        {
            try
            {
                if (await IsOpenablePathAsync(path))
                    await VsCommandService.OpenPathAsync(path);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        internal static Task<bool> IsOpenablePathAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(false);

            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            if (ext != null && _openableExtensions.Contains(ext))
                return Task.FromResult(true);

            return Task.Run(() => Directory.Exists(path));
        }
    }

    /// <summary>
    /// Converter that returns Visible if false, Collapsed if true (inverse of BooleanToVisibilityConverter).
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b)
            {
                return Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter that returns Visible if true, Hidden (not Collapsed) if false.
    /// Use this for elements that should stay in layout when inactive, preventing layout shifts.
    /// </summary>
    public class BoolToHiddenVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && b ? Visibility.Visible : Visibility.Hidden;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter that returns a reduced opacity when true (refreshing), full opacity when false.
    /// </summary>
    public class RefreshingToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && b ? 0.4 : 1.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter that returns Visible if count > 0, Collapsed otherwise.
    /// </summary>
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                return count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a column count into a pixel width by multiplying by the per-column
    /// width passed in via ConverterParameter (item width plus its right margin).
    /// Used to bound a WrapPanel so it wraps after the desired number of columns
    /// instead of stretching across the whole available width.
    /// </summary>
    public class ColumnCountToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count && count > 0)
            {
                double perColumn = 292;
                if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                {
                    perColumn = parsed;
                }

                return count * perColumn;
            }

            return double.PositiveInfinity;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Adorner that draws a horizontal line to indicate where a dragged pinned item will be dropped.
    /// </summary>
    internal sealed class DropIndicatorAdorner : Adorner
    {
        private readonly bool _below;
        private static readonly Pen _indicatorPen = CreateIndicatorPen();

        public DropIndicatorAdorner(UIElement adornedElement, bool below)
            : base(adornedElement)
        {
            _below = below;
            IsHitTestVisible = false;
        }

        private static Pen CreateIndicatorPen()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0, 122, 204)), 2);
            pen.Freeze();
            return pen;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            Size size = AdornedElement.RenderSize;
            var y = _below ? size.Height : 0;
            drawingContext.DrawLine(_indicatorPen, new Point(0, y), new Point(size.Width, y));
        }
    }
}
