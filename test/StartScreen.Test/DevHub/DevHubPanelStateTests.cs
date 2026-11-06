using System.Windows;
using System.Windows.Controls;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.ToolWindows;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubPanelStateTests
    {
        [TestMethod]
        [DataRow(DevHubActivityStatus.Partial, "partial")]
        [DataRow(DevHubActivityStatus.Unavailable, "unavailable")]
        [DataRow(DevHubActivityStatus.Unauthorized, "unauthorized")]
        public void ActivityRefreshStatus_FailuresNeverClaimFreshSuccess(DevHubActivityStatus status, string text)
        {
            var dashboard = new DevHubDashboard { Sources = { ["source"] = new DevHubActivitySnapshot { Status = status } } };
            StringAssert.Contains(DevHubPanel.GetActivityRefreshStatus(dashboard), text);
            StringAssert.Contains(DevHubPanel.GetActivityRefreshStatus(dashboard), "stale");
            dashboard.IsRefreshing = true;
            Assert.AreEqual("Refreshing activity...", DevHubPanel.GetActivityRefreshStatus(dashboard));
            Assert.IsNull(DevHubPanel.GetActivityRefreshStatus(new DevHubDashboard()));
        }

        [STATestMethod]
        [DataRow(Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed)]
        [DataRow(Visibility.Collapsed, Visibility.Visible, Visibility.Collapsed)]
        [DataRow(Visibility.Collapsed, Visibility.Collapsed, Visibility.Visible)]
        public void WorkspacePanelReset_ClearsPreviousErrorLoadingAndConnectionStates(Visibility errorState, Visibility loadingState, Visibility connectionState)
        {
            var error = new StackPanel { Visibility = errorState };
            var loading = new StackPanel { Visibility = loadingState };
            var connection = new StackPanel { Visibility = connectionState };

            DevHubPanel.ResetStatusPanels(connection, loading, error);

            Assert.AreEqual(Visibility.Collapsed, error.Visibility);
            Assert.AreEqual(Visibility.Collapsed, loading.Visibility);
            Assert.AreEqual(Visibility.Collapsed, connection.Visibility);
        }

        [TestMethod]
        public void SelectingLocalWorkspaces_RefreshesScopeEvenWhenBothHaveNoRemote()
        {
            var repository = new MruItem { Name = "Repo", RemoteUrl = "https://dev.azure.com/org/project/_git/repo" };
            var local = new MruItem { Name = "Local", Path = @"C:\Local\App.sln" };
            var otherLocal = new MruItem { Name = "Other local", Path = @"C:\Other\App.sln" };
            var model = new StartScreenViewModel([repository, local, otherLocal]) { SelectedMruItem = repository };
            var scopes = new List<RemoteRepoIdentifier?>();
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StartScreenViewModel.SelectedRepository))
                    scopes.Add(model.SelectedRepository);
            };

            model.SelectedMruItem = local;
            model.SelectedMruItem = otherLocal;

            Assert.HasCount(2, scopes);
            Assert.IsTrue(scopes.All(scope => scope == null));
            Assert.AreEqual("Other local", model.WorkSectionTitle);
        }
    }
}