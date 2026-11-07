using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
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
        [DataRow("github.com", "GitHub/pulls")]
        [DataRow("dev.azure.com", "Azure DevOps/pulls")]
        public void AccountRecovery_IsHiddenDuringRefreshAndAfterSuccessfulAuthentication(string host, string source)
        {
            var dashboard = new DevHubDashboard { IsRefreshing = true };
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(dashboard, host, true));
            dashboard.IsRefreshing = false;
            Assert.IsTrue(DevHubPanel.NeedsAccountRecovery(dashboard, host, true));
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(dashboard, host, false));
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(null, host, true));
            dashboard.Users.Add(new DevHubUser { Host = host });
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(dashboard, host, true));
            dashboard.Sources[source] = new DevHubActivitySnapshot { Status = DevHubActivityStatus.Unavailable };
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(dashboard, host, true), "Network failures are not rejected credentials.");
            dashboard.Sources[source].Status = DevHubActivityStatus.Unauthorized;
            Assert.IsTrue(DevHubPanel.NeedsAccountRecovery(dashboard, host, true), "Cached identities can have rejected credentials.");
            dashboard.Sources[source].Status = DevHubActivityStatus.Success;
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(dashboard, host, true));
            dashboard.Sources["repository"] = new DevHubActivitySnapshot
            {
                RepoIdentifier = new RemoteRepoIdentifier(host, "owner", "repo"),
                Status = DevHubActivityStatus.Unauthorized,
            };
            Assert.IsTrue(DevHubPanel.NeedsAccountRecovery(dashboard, host, true));
        }

        [TestMethod]
        public void ActivityFailures_HaveNoDashboardBannerOrErrorPanel()
        {
            using var stream = typeof(DevHubPanelStateTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            var document = XDocument.Load(stream!);
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

            Assert.IsEmpty(document.Descendants().Where(element => new[] { "ActivityWarningText", "ErrorPanel", "ErrorMessageText" }
                .Contains(element.Attribute(x + "Name")?.Value)));
            Assert.IsFalse(document.Descendants().Any(element => element.Attribute("Text")?.Value == "Something went wrong"));
            Assert.IsTrue(document.Descendants().Any(element => element.Attribute(x + "Name")?.Value == "AccountRecoveryPanel"),
                "Output-only errors must not remove account connection actions.");
        }

        [TestMethod]
        public void OrganizationAuthorization_IsOptionalAndOnlyShownInAccountSettings()
        {
            using var stream = typeof(DevHubPanelStateTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var link = document.Descendants(p + "Hyperlink").Single(item =>
                item.Attribute("Click")?.Value == "OpenGitHubOrganizationAuthorization_Click");

            Assert.IsTrue(link.Ancestors().Any(item => item.Attribute(x + "Name")?.Value == "ConnectedGitHubStatus"));
            Assert.IsTrue(link.Ancestors().Any(item => item.Attribute(x + "Name")?.Value == "SettingsPanel"));
            Assert.IsFalse(link.Ancestors().Any(item => item.Attribute(x + "Name")?.Value == "DashboardPanel"));
            StringAssert.Contains(link.Attribute("ToolTip")!.Value, "Optional");
            StringAssert.Contains(link.Attribute("ToolTip")!.Value, "organization permissions vary");
            Assert.IsNull(link.Attribute("Focusable"));
        }

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