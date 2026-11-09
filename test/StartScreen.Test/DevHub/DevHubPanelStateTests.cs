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
        [DataRow("github.com", DevHubActivityStatus.Unauthorized)]
        [DataRow("dev.azure.com", DevHubActivityStatus.Unauthorized)]
        [DataRow("github.com", DevHubActivityStatus.Unavailable)]
        [DataRow("dev.azure.com", DevHubActivityStatus.Unavailable)]
        public void AccountRecovery_CompletedSnapshotsStayHiddenWhileActivityIsLoading(string host, DevHubActivityStatus status)
        {
            var dashboard = new DevHubDashboard();
            var repo = new RemoteRepoIdentifier(host, "owner", "repo");
            dashboard.Sources["repository"] = new DevHubActivitySnapshot { RepoIdentifier = repo, Status = status };
            Assert.IsFalse(dashboard.IsRefreshing);
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(dashboard, host, true, repo, isActivityLoading: true));
            Assert.IsFalse(DevHubPanel.ShouldShowAccountRecovery(dashboard, host, true, repo, isActivityLoading: true));
            Assert.IsFalse(DevHubPanel.HasUnavailableAccountVerification(dashboard, host, repo, isActivityLoading: true));

            Assert.AreEqual(status == DevHubActivityStatus.Unauthorized,
                DevHubPanel.ShouldShowAccountRecovery(dashboard, host, true, repo, isActivityLoading: false),
                "A genuine final rejection must become actionable when all activity requests finish.");
            Assert.AreEqual(status == DevHubActivityStatus.Unavailable,
                DevHubPanel.HasUnavailableAccountVerification(dashboard, host, repo, isActivityLoading: false));
        }

        [TestMethod]
        [DataRow("github.com")]
        [DataRow("dev.azure.com")]
        public void AccountRecovery_SuccessfulRefreshNeverExposesAnIntermediateReauthenticationMessage(string host)
        {
            var repo = new RemoteRepoIdentifier(host, "owner", "repo");
            var intermediate = new DevHubDashboard();
            intermediate.Sources["repository"] = new DevHubActivitySnapshot { RepoIdentifier = repo, Status = DevHubActivityStatus.Unauthorized };
            Assert.IsFalse(DevHubPanel.ShouldShowAccountRecovery(intermediate, host, true, repo, isActivityLoading: true));

            var authenticated = new DevHubDashboard();
            authenticated.Users.Add(new DevHubUser { Host = host });
            Assert.IsFalse(DevHubPanel.ShouldShowAccountRecovery(authenticated, host, true, repo, isActivityLoading: true));
            Assert.IsFalse(DevHubPanel.ShouldShowAccountRecovery(authenticated, host, true, repo, isActivityLoading: false));
            Assert.IsFalse(DevHubPanel.HasUnavailableAccountVerification(authenticated, host, repo, isActivityLoading: false));
        }

        [TestMethod]
        [DataRow("github.com", "GitHub/authentication")]
        [DataRow("dev.azure.com", "Azure DevOps/authentication")]
        [DataRow("dev.azure.com", "scoped repository")]
        public void AccountVerification_NetworkFailuresWithoutIdentityDoNotSuggestReauthentication(string host, string source)
        {
            var dashboard = new DevHubDashboard();
            dashboard.Sources[source] = new DevHubActivitySnapshot
            {
                RepoIdentifier = new RemoteRepoIdentifier(host, "owner", "repo"),
                Status = DevHubActivityStatus.Unavailable,
            };
            Assert.IsTrue(DevHubPanel.HasUnavailableAccountVerification(dashboard, host));
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(dashboard, host, true));
            Assert.IsFalse(DevHubPanel.HasUnavailableAccountVerification(null, host));
            dashboard.IsRefreshing = true;
            Assert.IsFalse(DevHubPanel.HasUnavailableAccountVerification(dashboard, host));
            dashboard.IsRefreshing = false;
            dashboard.Sources[source].Status = DevHubActivityStatus.Unauthorized;
            Assert.IsFalse(DevHubPanel.HasUnavailableAccountVerification(dashboard, host));
            Assert.IsTrue(DevHubPanel.NeedsAccountRecovery(dashboard, host, true));
            dashboard.Sources[source].Status = DevHubActivityStatus.Unavailable;
            dashboard.Users.Add(new DevHubUser { Host = host });
            Assert.IsFalse(DevHubPanel.HasUnavailableAccountVerification(dashboard, host));
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(dashboard, host, true));
        }

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
        [DataRow("dev.azure.com", "https://github.com/pr0g/cmake-examples", false)]
        [DataRow("github.com", "https://github.com/pr0g/cmake-examples", true)]
        [DataRow("github.com", "https://dev.azure.com/org/project/_git/repo", false)]
        [DataRow("dev.azure.com", "https://dev.azure.com/org/project/_git/repo", true)]
        [DataRow("dev.azure.com", "https://org.visualstudio.com/project/_git/repo", true)]
        [DataRow("dev.azure.com", "https://tfs.contoso.com/collection/project/_git/repo", false)]
        [DataRow("github.com", "https://gitlab.com/owner/repo", false)]
        [DataRow("dev.azure.com", "https://gitlab.com/owner/repo", false)]
        public void AccountWarnings_ApplyOnlyToTheSelectedRepositoryProvider(string host, string remoteUrl, bool relevant)
        {
            var repo = RemoteRepoIdentifier.TryParse(remoteUrl);
            Assert.IsNotNull(repo);
            var dashboard = new DevHubDashboard();
            Assert.AreEqual(relevant, DevHubPanel.IsAccountProviderInScope(host, repo));
            Assert.AreEqual(relevant, DevHubPanel.NeedsAccountRecovery(dashboard, host, true, repo));
            dashboard.Sources["authentication"] = new DevHubActivitySnapshot
            {
                RepoIdentifier = new RemoteRepoIdentifier(host, "owner", "repo"),
                Status = DevHubActivityStatus.Unavailable,
            };
            Assert.AreEqual(relevant, DevHubPanel.HasUnavailableAccountVerification(dashboard, host, repo));
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(dashboard, host, true, repo));
            dashboard.Sources["authentication"].Status = DevHubActivityStatus.Unauthorized;
            Assert.AreEqual(relevant, DevHubPanel.NeedsAccountRecovery(dashboard, host, true, repo));
            Assert.IsFalse(DevHubPanel.HasUnavailableAccountVerification(dashboard, host, repo));
            Assert.IsTrue(DevHubPanel.NeedsAccountRecovery(dashboard, host, true), "All repositories must retain provider account recovery.");
        }

        [TestMethod]
        public void AccountRecovery_ReturnsWhenLeavingAnUnrelatedRepositoryScope()
        {
            var dashboard = new DevHubDashboard();
            dashboard.Users.Add(new DevHubUser { Host = "github.com" });
            var github = RemoteRepoIdentifier.TryParse("https://github.com/pr0g/cmake-examples");
            var ado = RemoteRepoIdentifier.TryParse("https://dev.azure.com/org/project/_git/repo");

            Assert.IsTrue(DevHubPanel.NeedsAccountRecovery(dashboard, "dev.azure.com", true));
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(dashboard, "dev.azure.com", true, github));
            Assert.IsTrue(DevHubPanel.NeedsAccountRecovery(dashboard, "dev.azure.com", true, ado));
            Assert.IsTrue(DevHubPanel.NeedsAccountRecovery(dashboard, "dev.azure.com", true));
            Assert.IsFalse(DevHubPanel.NeedsAccountRecovery(dashboard, "dev.azure.com", true, github));
        }

        [TestMethod]
        [DataRow("github.com", "GitHub/authentication")]
        [DataRow("dev.azure.com", "Azure DevOps/authentication")]
        public void AccountPrompts_SeparateInitialConnectionFromRejectedCredentials(string host, string source)
        {
            var dashboard = new DevHubDashboard();
            Assert.IsFalse(DevHubPanel.ShouldShowAccountRecovery(dashboard, host, true), "Initial connection must not also show a recovery action.");
            dashboard.Sources[source] = new DevHubActivitySnapshot { Status = DevHubActivityStatus.Unauthorized };
            Assert.IsTrue(DevHubPanel.ShouldShowAccountRecovery(dashboard, host, true));
            Assert.IsFalse(DevHubPanel.ShouldShowAccountRecovery(dashboard, host, false));
            dashboard.Sources[source].Status = DevHubActivityStatus.Unavailable;
            Assert.IsFalse(DevHubPanel.ShouldShowAccountRecovery(dashboard, host, true), "A network failure is not rejected credentials.");
            dashboard.Users.Add(new DevHubUser { Host = host == "github.com" ? "dev.azure.com" : "github.com" });
            dashboard.Sources.Clear();
            Assert.IsTrue(DevHubPanel.ShouldShowAccountRecovery(dashboard, host, true), "A missing provider still needs an action when the dashboard is already connected elsewhere.");
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