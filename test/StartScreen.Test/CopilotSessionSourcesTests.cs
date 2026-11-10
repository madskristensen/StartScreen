using System.Collections.Concurrent;
using System.ComponentModel.Design;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using StartScreen.Commands;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;
using StartScreen.Services.DevHub;

namespace StartScreen.Test
{
    [TestClass]
    public class CopilotSessionSourcesTests
    {
        private const string RuntimeId = "96c12378-f593-4c38-982f-d16b25f68bc3";
        private const string NativeId = "a304b9be-c94c-4585-81f8-570cae2025db";

        [TestMethod]
        [DataRow("github/cli", "vscode-agent-host", true)]
        [DataRow("vscode-agent-host", "github/cli", false)]
        [DataRow("unknown", "github/cli", false)]
        [DataRow("visualstudio-chat", "github/cli", false)]
        [DataRow("microsoft/visualstudio-chat", "github/cli", false)]
        public void Metadata_UsesCreatorNotLastClient(string creator, string lastClient, bool supported)
        {
            var session = CopilotLocalSessions.ParseMetadata(Metadata(creator) + "last_client_name: " + lastClient + "\n", RuntimeId, "home");
            if (!supported)
            {
                Assert.IsNull(session);
                return;
            }
            Assert.IsNotNull(session);
            Assert.AreEqual(CopilotSessionOrigin.Cli, session.Origin);
            Assert.AreEqual(RuntimeId, session.Id);
            Assert.AreEqual("A title: with punctuation", session.Title);
            Assert.AreEqual(@"C:\Repos\Example", session.WorkingDirectory);
            Assert.IsFalse(session.IsRunning);
            Assert.IsFalse(session.NeedsAttention);
            Assert.AreEqual(string.Empty, session.StatusBadge);
        }

        [TestMethod]
        [DataRow("visualstudio-chat", true)]
        [DataRow("microsoft/visualstudio-chat", true)]
        [DataRow("github/cli", false)]
        [DataRow("vscode-agent-host", false)]
        [DataRow("unknown", false)]
        [DataRow("", false)]
        public void Metadata_VisualStudioRequiresItsCreatorAndNeverInventsLiveState(string creator, bool supported)
        {
            var session = CopilotLocalSessions.ParseMetadata(Metadata(creator) + "last_client_name: github/cli\n",
                RuntimeId, "vs-home", CopilotSessionOrigin.VisualStudio);
            if (!supported)
            {
                Assert.IsNull(session);
                return;
            }
            Assert.IsNotNull(session);
            Assert.AreEqual(CopilotSessionOrigin.VisualStudio, session.Origin);
            Assert.AreEqual(RuntimeId, session.Id);
            Assert.AreEqual("vs-home", session.SessionHome);
            Assert.AreEqual("A title: with punctuation", session.Title);
            Assert.AreEqual(@"C:\Repos\Example", session.WorkingDirectory);
            Assert.IsTrue(session.IsCached);
            Assert.IsNull(session.IsResponding);
            Assert.IsFalse(session.IsRunning);
            Assert.IsFalse(session.NeedsAttention);
            Assert.AreEqual(string.Empty, session.StatusBadge);
            Assert.IsEmpty(DevHubWorkItem.GetCopilotItems([session], true));
        }

        [TestMethod]
        public void Metadata_RejectsInvalidIdsAndDoesNotGuessAnUnknownCreator()
        {
            Assert.Throws<InvalidDataException>(() => CopilotLocalSessions.ParseMetadata(Metadata("github/cli"), NativeId, "home"));
            Assert.Throws<InvalidDataException>(() => CopilotLocalSessions.ParseMetadata(new string('a', 65537), RuntimeId, "home"));
            Assert.Throws<InvalidDataException>(() => CopilotLocalSessions.ParseMetadata(Metadata("github/cli") + "alias: &a [*a]\n", RuntimeId, "home"));
            Assert.IsNull(CopilotLocalSessions.ParseMetadata(Metadata("unknown"), RuntimeId, "home"));
        }

        [TestMethod]
        public void Metadata_ReadIsBounded()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("workspace.yaml");
            File.WriteAllText(path, new string('a', 65537));
            Assert.Throws<InvalidDataException>(() => CopilotLocalSessions.ReadBoundedText(path));
        }

        [TestMethod]
        [DataRow(false, 2)]
        [DataRow(true, 2)]
        [DataRow(false, 0)]
        [DataRow(true, 0)]
        public async Task Discovery_ExcludesCodeAndRetainsSupportedCreatorsWithoutReadingDatabasesOrTranscriptsAsync(bool forceRefresh, int originValue)
        {
            var origin = (CopilotSessionOrigin)originValue;
            using var directory = new TemporaryDirectory();
            var home = directory.GetPath("home");
            SaveMetadata(home, RuntimeId, "vscode-agent-host");
            SaveMetadata(home, NativeId, origin == CopilotSessionOrigin.Cli ? "github/cli" : "visualstudio-chat");
            var database = Path.Combine(home, "session-state", RuntimeId, "session.db");
            var transcript = Path.Combine(home, "session-state", NativeId, "events.jsonl");
            File.WriteAllText(database, "Not a database");
            File.WriteAllText(transcript, "Not a transcript");
            using var lockedDatabase = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None);
            using var lockedTranscript = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.None);
            var cache = new CopilotLocalSessions.DiscoveryCache();
            await CopilotLocalSessions.ReadAsync(home, CancellationToken.None, cache, origin: origin);
            var result = await CopilotLocalSessions.ReadAsync(home, CancellationToken.None, cache, forceRefresh, origin);
            Assert.HasCount(1, result.Sessions);
            var cli = result.Sessions.Single();
            Assert.AreEqual(origin, cli.Origin);
            Assert.AreEqual(NativeId, cli.Id);
            Assert.AreEqual(home, cli.SessionHome);
            Assert.AreEqual(string.Empty, result.Status);
            Assert.IsFalse(cli.IsRunning);
            Assert.IsFalse(cli.NeedsAttention);
        }

        [TestMethod]
        [DataRow(2)]
        [DataRow(0)]
        public async Task DiscoveryCache_ReusesOnlyUnchangedMetadataAndClonesSnapshotsAsync(int originValue)
        {
            var origin = (CopilotSessionOrigin)originValue;
            using var directory = new TemporaryDirectory();
            var creator = origin == CopilotSessionOrigin.Cli ? "github/cli" : "microsoft/visualstudio-chat";
            SaveMetadata(directory.Path, RuntimeId, creator);
            var reads = 0;
            var cache = new CopilotLocalSessions.DiscoveryCache(path =>
            {
                Interlocked.Increment(ref reads);
                return CopilotLocalSessions.ReadBoundedText(path);
            });
            var first = await CopilotLocalSessions.ReadAsync(directory.Path, CancellationToken.None, cache, origin: origin);
            first.Sessions.Single().Title = "Caller mutation";
            first.Sessions.Single().IsResponding = true;
            var second = await CopilotLocalSessions.ReadAsync(directory.Path, CancellationToken.None, cache, origin: origin);
            Assert.AreEqual(1, reads);
            Assert.AreEqual("A title: with punctuation", second.Sessions.Single().Title);
            Assert.IsFalse(second.Sessions.Single().IsRunning);
            Assert.AreNotSame(first.Sessions.Single(), second.Sessions.Single());

            await CopilotLocalSessions.ReadAsync(directory.Path, CancellationToken.None, cache, forceRefresh: true, origin: origin);
            Assert.AreEqual(2, reads);
            var path = Path.Combine(directory.Path, "session-state", RuntimeId, "workspace.yaml");
            File.WriteAllText(path, Metadata(creator).Replace("A title: with punctuation", "Changed title"));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            var changed = await CopilotLocalSessions.ReadAsync(directory.Path, CancellationToken.None, cache, origin: origin);
            Assert.AreEqual(3, reads);
            Assert.AreEqual("Changed title", changed.Sessions.Single().Title);
            File.Delete(path);
            Assert.IsEmpty((await CopilotLocalSessions.ReadAsync(directory.Path, CancellationToken.None, cache, origin: origin)).Sessions);
            SaveMetadata(directory.Path, RuntimeId, creator);
            Assert.HasCount(1, (await CopilotLocalSessions.ReadAsync(directory.Path, CancellationToken.None, cache, origin: origin)).Sessions);
            Assert.AreEqual(4, reads);
        }

        [TestMethod]
        public void Discovery_DoesNotReferenceSqlite()
        {
            var references = typeof(CopilotLocalSessions).Assembly.GetReferencedAssemblies();
            Assert.IsFalse(references.Any(reference => reference.Name!.StartsWith("Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase)
                || reference.Name.StartsWith("SQLitePCLRaw", StringComparison.OrdinalIgnoreCase)));
        }

        [TestMethod]
        public async Task DiscoveryCache_ReportsMetadataFailuresAndRecoversWithoutStaleFallbackAsync()
        {
            using var directory = new TemporaryDirectory();
            SaveMetadata(directory.Path, RuntimeId, "github/cli");
            var cache = new CopilotLocalSessions.DiscoveryCache();
            Assert.HasCount(1, (await CopilotLocalSessions.ReadAsync(directory.Path, CancellationToken.None, cache)).Sessions);
            var path = Path.Combine(directory.Path, "session-state", RuntimeId, "workspace.yaml");
            File.WriteAllText(path, "id: [invalid YAML");
            var failed = await CopilotLocalSessions.ReadAsync(directory.Path, CancellationToken.None, cache, forceRefresh: true);
            Assert.IsEmpty(failed.Sessions);
            StringAssert.Contains(failed.Status, "local Copilot session metadata could not be read");
            SaveMetadata(directory.Path, RuntimeId, "github/cli");
            var recovered = await CopilotLocalSessions.ReadAsync(directory.Path, CancellationToken.None, cache);
            Assert.HasCount(1, recovered.Sessions);
            Assert.AreEqual(string.Empty, recovered.Status);
        }

        [TestMethod]
        [DataRow(2)]
        [DataRow(0)]
        public async Task Discovery_HonorsCancellationAsync(int originValue)
        {
            var origin = (CopilotSessionOrigin)originValue;
            using var directory = new TemporaryDirectory();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => CopilotLocalSessions.ReadAsync(directory.Path, cancellation.Token, origin: origin));
        }

        [TestMethod]
        [DataRow(2)]
        [DataRow(0)]
        public async Task Discovery_IsBoundedTo100RecentMetadataDirectoriesAsync(int originValue)
        {
            var origin = (CopilotSessionOrigin)originValue;
            using var directory = new TemporaryDirectory();
            for (var index = 0; index < 105; index++)
                SaveMetadata(directory.Path, Guid.NewGuid().ToString("D"), origin == CopilotSessionOrigin.Cli ? "github/cli" : "visualstudio-chat");
            var result = await CopilotLocalSessions.ReadAsync(directory.Path, CancellationToken.None, origin: origin);
            Assert.HasCount(100, result.Sessions);
            Assert.AreEqual(string.Empty, result.Status);
        }

        [TestMethod]
        [DataRow(0, 0, true)]
        [DataRow(0, 1, false)]
        [DataRow(0, 2, true)]
        [DataRow(0, 3, false)]
        [DataRow(1, 0, false)]
        [DataRow(1, 1, false)]
        [DataRow(1, 2, false)]
        [DataRow(1, 3, false)]
        [DataRow(2, 0, false)]
        [DataRow(2, 1, false)]
        [DataRow(2, 2, true)]
        [DataRow(2, 3, false)]
        [DataRow(3, 0, false)]
        [DataRow(3, 1, false)]
        [DataRow(3, 2, false)]
        [DataRow(3, 3, true)]
        [DataRow(99, 99, false)]
        public void Destinations_OfferOnlyTestedCombinations(int source, int destination, bool expected)
        {
            Assert.AreEqual(expected, CopilotSessionLauncher.Supports((CopilotSessionOrigin)source, (CopilotSessionOrigin)destination));
        }

        [TestMethod]
        public void CliLaunch_UsesOriginalHomeAndDoesNotSubmitAPromptOrTakeOver()
        {
            var session = new CopilotActivitySession { Id = RuntimeId, Origin = CopilotSessionOrigin.VisualStudio };
            var launch = CopilotSessionLauncher.CreateStartInfo(session, CopilotSessionOrigin.Cli, @"C:\Tools\copilot.exe");
            Assert.AreEqual("--resume=" + RuntimeId, launch.Arguments);
            Assert.AreEqual(@"C:\Tools\copilot.exe", launch.FileName);
            Assert.AreEqual(CopilotLocalSessions.VisualStudioHome, launch.EnvironmentVariables["COPILOT_HOME"]);
            Assert.IsFalse(launch.EnvironmentVariables.ContainsKey("COPILOT_AGENT_SESSION_ID"));
            Assert.IsFalse(launch.EnvironmentVariables.ContainsKey("COPILOT_CLI_RESOLVED_DIST_DIR"));
            Assert.IsFalse(launch.UseShellExecute);
            Assert.IsFalse(launch.CreateNoWindow);
            Assert.IsFalse(launch.RedirectStandardInput);
            Assert.IsFalse(launch.RedirectStandardOutput);
            session.Id = "bad & echo injection";
            Assert.Throws<InvalidDataException>(() => CopilotSessionLauncher.CreateStartInfo(session, CopilotSessionOrigin.Cli, "copilot.exe"));
        }

        [TestMethod]
        public void CliLaunch_UsesDefaultHomeAndRejectsRemovedCodeDestinations()
        {
            var session = new CopilotActivitySession { Id = RuntimeId, Origin = CopilotSessionOrigin.Cli };
            var launch = CopilotSessionLauncher.CreateStartInfo(session, CopilotSessionOrigin.Cli, "copilot.exe");
            Assert.AreEqual("--resume=" + RuntimeId, launch.Arguments);
            Assert.AreEqual(CopilotLocalSessions.DefaultHome, launch.EnvironmentVariables["COPILOT_HOME"]);
            Assert.Throws<InvalidOperationException>(() => CopilotSessionLauncher.CreateStartInfo(session, (CopilotSessionOrigin)1, "Code.exe"));
            Assert.Throws<InvalidOperationException>(() => CopilotSessionLauncher.CreateStartInfo(session, CopilotSessionOrigin.VisualStudio, "devenv.exe"));
        }

        [TestMethod]
        public void Ownership_RequiresPositiveReleaseAndRechecksNewMarkers()
        {
            using var directory = new TemporaryDirectory();
            Assert.IsNotNull(CopilotSessionLauncher.SavedSessionUnavailableReason(directory.Path));
            var events = directory.GetPath("events.jsonl");
            File.WriteAllText(events, "{\"type\":\"session.idle\"}\n");
            Assert.IsNotNull(CopilotSessionLauncher.SavedSessionUnavailableReason(directory.Path));
            File.WriteAllText(events, new string('x', 100000) + "\n{\"type\":\"session.shutdown\"}\n");
            Assert.IsNull(CopilotSessionLauncher.SavedSessionUnavailableReason(directory.Path));
            var marker = directory.GetPath("inuse.123.hold");
            File.WriteAllText(marker, "");
            Assert.IsNotNull(CopilotSessionLauncher.SavedSessionUnavailableReason(directory.Path));
            Assert.IsTrue(File.Exists(marker), "Ownership checks must never remove markers.");
            File.Delete(marker);
            File.AppendAllText(events, "{\"type\":\"session.resume\"}\n");
            Assert.IsNotNull(CopilotSessionLauncher.SavedSessionUnavailableReason(directory.Path));
        }

        [TestMethod]
        [DoNotParallelize]
        [DataRow(0)]
        [DataRow(2)]
        public void CliMenu_ReleasedSessionsWithAnUnheldForkLockAreEnabled(int origin)
        {
            using var directory = new TemporaryDirectory();
            var events = directory.GetPath("events.jsonl");
            var forkLock = directory.GetPath(".workspace-fork.lock");
            File.WriteAllText(events, "{\"type\":\"session.shutdown\"}\n");
            File.WriteAllText(forkLock, "");
            var session = new CopilotActivitySession { Id = RuntimeId, Origin = (CopilotSessionOrigin)origin };
            var command = new OleMenuCommand(null, new CommandID(PackageGuids.StartScreen, PackageIds.OpenCopilotInCli));
            void UpdateMenu()
            {
                CopilotSessionMenu.Current = new CopilotSessionMenu
                {
                    Session = session,
                    CliUnavailableReason = CopilotSessionLauncher.CliUnavailableReason(session,
                        _ => CopilotSessionLauncher.SavedSessionUnavailableReason(directory.Path)),
                };
                CopilotSessionMenu.Update(command, CopilotSessionOrigin.Cli);
            }

            try
            {
                UpdateMenu();
                Assert.IsTrue(command.Visible);
                Assert.IsTrue(command.Enabled, CopilotSessionMenu.Current.CliUnavailableReason);
                Assert.AreEqual("Open in CLI", command.Text);
                Assert.IsTrue(File.Exists(forkLock), "A released lock file must not be deleted.");

                var marker = directory.GetPath("inuse.123.lock");
                File.WriteAllText(marker, "");
                UpdateMenu();
                Assert.IsFalse(command.Enabled);
                Assert.IsTrue(File.Exists(marker));

                File.Delete(marker);
                File.AppendAllText(events, "{\"type\":\"session.resume\"}\n");
                UpdateMenu();
                Assert.IsFalse(command.Enabled, "An unheld fork lock alone must not qualify a session for resuming.");
            }
            finally
            {
                CopilotSessionMenu.Current = null;
            }
        }

        [TestMethod]
        public void Ownership_HeldForkLockBlocksEvenAfterASavedShutdown()
        {
            using var directory = new TemporaryDirectory();
            File.WriteAllText(directory.GetPath("events.jsonl"), "{\"type\":\"session.shutdown\"}\n");
            var forkLock = directory.GetPath(".workspace-fork.lock");
            File.WriteAllText(forkLock, "");
            using (var owner = new FileStream(forkLock, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                Assert.Throws<IOException>(() => CopilotSessionLauncher.SavedSessionUnavailableReason(directory.Path));

            Assert.IsNull(CopilotSessionLauncher.SavedSessionUnavailableReason(directory.Path));
            Assert.IsTrue(File.Exists(forkLock));
            Assert.AreEqual(0L, new FileInfo(forkLock).Length);
        }

        [TestMethod]
        public void Ownership_DoesNotTreatIdleCachedOrLiveSessionsAsReleased()
        {
            var session = new CopilotActivitySession { Id = RuntimeId, Origin = CopilotSessionOrigin.VisualStudio, IsResponding = false };
            Assert.IsNotNull(CopilotSessionLauncher.CliUnavailableReason(session));
            session.IsTranscript = true;
            session.IsCached = true;
            Assert.IsNotNull(CopilotSessionLauncher.CliUnavailableReason(session));
            session.IsCached = false;
            session.IsTranscript = false;
            session.PendingPermissions = 1;
            Assert.IsNotNull(CopilotSessionLauncher.CliUnavailableReason(session));
            session.PendingPermissions = 0;
            session.Origin = (CopilotSessionOrigin)1;
            StringAssert.Contains(CopilotSessionLauncher.CliUnavailableReason(session), "cannot be opened in CLI");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Ownership_ReleasedVisualStudioSessionsDoNotRequireTranscriptFormat(bool transcript)
        {
            using var directory = new TemporaryDirectory();
            var events = directory.GetPath("events.jsonl");
            File.WriteAllText(events, "{\"type\":\"session.shutdown\"}\n");
            var session = new CopilotActivitySession
            {
                Id = RuntimeId,
                Origin = CopilotSessionOrigin.VisualStudio,
                IsTranscript = transcript,
                IsResponding = null,
            };
            string? checkedPath = null;
            string ReadSavedState(string path)
            {
                checkedPath = path;
                return CopilotSessionLauncher.SavedSessionUnavailableReason(directory.Path);
            }

            Assert.IsNull(CopilotSessionLauncher.CliUnavailableReason(session, ReadSavedState));
            Assert.AreEqual(Path.Combine(CopilotLocalSessions.VisualStudioHome, "session-state", RuntimeId), checkedPath);
            File.WriteAllText(directory.GetPath("inuse.123.lock"), "");
            Assert.IsNotNull(CopilotSessionLauncher.CliUnavailableReason(session, ReadSavedState));
            File.Delete(directory.GetPath("inuse.123.lock"));
            File.AppendAllText(events, "{\"type\":\"session.resume\"}\n");
            Assert.IsNotNull(CopilotSessionLauncher.CliUnavailableReason(session, ReadSavedState));
        }

        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void Ownership_LoadedVisualStudioSessionsStayBlockedRegardlessOfFormat(bool responding, bool transcript)
        {
            var session = new CopilotActivitySession
            {
                Id = RuntimeId,
                Origin = CopilotSessionOrigin.VisualStudio,
                IsTranscript = transcript,
                IsResponding = responding,
            };
            var checkedDisk = false;
            var reason = CopilotSessionLauncher.CliUnavailableReason(session, _ =>
            {
                checkedDisk = true;
                return null;
            });
            Assert.IsNotNull(reason);
            Assert.IsFalse(checkedDisk, "A stale saved shutdown must not override VS's live ownership signal.");
        }

        [TestMethod]
        [DataRow("copilot-developer", true)]
        [DataRow("copilot-swe-agent", true)]
        [DataRow("copilot-developer-cli", false)]
        [DataRow("vscode-chat", false)]
        [DataRow("jetbrains-chat", false)]
        public void CloudClassification_DoesNotMistakeSyncedLocalTasksForCloud(string slug, bool expected)
        {
            using var json = JsonDocument.Parse("{\"agent_collaborators\":[{\"slug\":\"" + slug + "\"}]}");
            Assert.AreEqual(expected, CopilotCloudSessions.IsCloudTask(json.RootElement));
        }

        [TestMethod]
        [DataRow("managed-sandbox")]
        [DataRow("user-local")]
        public void CloudClassification_ExcludesLocalCompute(string kind)
        {
            using var json = JsonDocument.Parse("{\"agent_collaborators\":[{\"slug\":\"copilot-developer\"}],\"current_environment\":{\"kind\":\"" + kind + "\"}}");
            Assert.IsFalse(CopilotCloudSessions.IsCloudTask(json.RootElement));
        }

        [TestMethod]
        public async Task CloudDiscovery_ReportsUnidentifiedTasksInsteadOfInventingCloudSessionsAsync()
        {
            using var client = new HttpClient(new FakeApi("{\"tasks\":[{\"id\":\"" + RuntimeId + "\",\"name\":\"Mirrored CLI\"}]}"));
            var result = await CopilotCloudSessions.ReadAsync(client, new DevHubCredential("user", "test-token"), CancellationToken.None);
            Assert.HasCount(0, result.Sessions);
            StringAssert.Contains(result.Status, "omitted origin metadata");
        }

        [TestMethod]
        public async Task CloudDiscovery_AuthenticatesAndProjectsVerifiedCloudMetadataAsync()
        {
            using var handler = new FakeApi("{\"tasks\":[{\"id\":\"" + RuntimeId + "\",\"name\":\"Cloud work\","
                + "\"agent_collaborators\":[{\"slug\":\"copilot-swe-agent\"}],\"state\":\"in_progress\","
                + "\"html_url\":\"https://github.com/owner/repo/agents/tasks/" + RuntimeId + "\",\"updated_at\":\"2026-11-06T19:00:00Z\"}]}" );
            using var client = new HttpClient(handler);
            var result = await CopilotCloudSessions.ReadAsync(client, new DevHubCredential("user", "test-token"), CancellationToken.None);
            Assert.HasCount(1, result.Sessions);
            Assert.AreEqual(CopilotSessionOrigin.GitHub, result.Sessions[0].Origin);
            Assert.AreEqual("owner/repo", result.Sessions[0].Repository);
            Assert.IsTrue(result.Sessions[0].IsRunning);
            Assert.AreEqual("Bearer test-token", handler.Authorization);
            var cached = result.Sessions[0].AsCached();
            Assert.IsFalse(cached.IsRunning);
            Assert.IsFalse(cached.NeedsAttention);
            Assert.IsNull(cached.RemoteState);
        }

        [TestMethod]
        public async Task CloudDiscovery_RequestsAndProcessesOnlyThirtyRecentTasksAsync()
        {
            var tasks = Enumerable.Range(1, 40).Select(index =>
            {
                var id = Guid.NewGuid().ToString("D");
                return new
                {
                    id,
                    name = "Cloud work " + index,
                    agent_collaborators = new[] { new { slug = "copilot-swe-agent" } },
                    state = "in_progress",
                    html_url = "https://github.com/owner/repo/agents/tasks/" + id,
                    updated_at = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc).AddMinutes(-index),
                };
            }).ToArray();
            using var handler = new FakeApi(JsonSerializer.Serialize(new { tasks }));
            using var client = new HttpClient(handler);

            var result = await CopilotCloudSessions.ReadAsync(client, new DevHubCredential("user", "test-token"), CancellationToken.None);

            Assert.AreEqual("?per_page=30&sort=updated_at&direction=desc", handler.RequestUri!.Query);
            Assert.HasCount(30, result.Sessions);
            CollectionAssert.AreEqual(tasks.Take(30).Select(task => task.id).ToArray(), result.Sessions.Select(session => session.Id).ToArray());
        }

        [TestMethod]
        public async Task CloudDiscovery_SurfacesAuthorizationFailureAsync()
        {
            using var client = new HttpClient(new FakeApi("{}", HttpStatusCode.Forbidden));
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CopilotCloudSessions.ReadAsync(client,
                new DevHubCredential("user", "test-token"), CancellationToken.None));
            StringAssert.Contains(error.Message, "Agent tasks read");
        }

        [TestMethod]
        public async Task CloudDiscovery_ResolvesRepositoriesConcurrentlyOnceEachAndPreservesTaskOrderAsync()
        {
            var tasks = Enumerable.Range(0, 12).Select(index => new
            {
                id = Guid.NewGuid().ToString("D"),
                name = "Task " + index,
                repository = new { id = index % 6 + 1 },
                agent_collaborators = new[] { new { slug = "copilot-swe-agent" } },
                state = "completed",
                updated_at = "2026-11-07T19:00:00Z",
            }).ToArray();
            var active = 0;
            var observedConcurrency = new ConcurrentBag<int>();
            var requests = new ConcurrentDictionary<int, int>();
            var firstBatch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new AsyncHandler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath == "/agents/tasks")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { tasks })) };
                var number = int.Parse(request.RequestUri.Segments.Last());
                requests.AddOrUpdate(number, 1, (_, count) => count + 1);
                var concurrent = Interlocked.Increment(ref active);
                observedConcurrency.Add(concurrent);
                if (concurrent == CopilotCloudSessions.MaximumRepositoryConcurrency)
                    firstBatch.TrySetResult(true);
                try
                {
                    await release.Task.WithCancellation(token);
                    await Task.Delay((6 - number) * 5, token);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { full_name = "owner/repo" + number })),
                    };
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }));
            var discovery = CopilotCloudSessions.ReadAsync(client, new DevHubCredential("user", "test-token"), timeout.Token);
            try
            {
                await firstBatch.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(discovery.IsCompleted, "Repository lookups must overlap before any response is released.");
                Assert.AreEqual(CopilotCloudSessions.MaximumRepositoryConcurrency, active);
            }
            finally
            {
                release.TrySetResult(true);
            }
            var result = await discovery;

            Assert.AreEqual(CopilotCloudSessions.MaximumRepositoryConcurrency, observedConcurrency.Max());
            Assert.HasCount(6, requests);
            Assert.IsTrue(requests.Values.All(count => count == 1));
            CollectionAssert.AreEqual(tasks.Select(task => task.id).ToArray(), result.Sessions.Select(session => session.Id).ToArray());
            CollectionAssert.AreEqual(tasks.Select(task => "owner/repo" + task.repository.id).ToArray(),
                result.Sessions.Select(session => session.Repository).ToArray());
            Assert.AreEqual(0, active);
        }

        [TestMethod]
        public async Task CloudDiscovery_ReusesRepositoryNamesButAlwaysReloadsTasksAndHonorsForcedRefreshAsync()
        {
            var taskRequests = 0;
            var repositoryRequests = 0;
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/agents/tasks")
                    return CloudTaskResponse(++taskRequests == 1 ? "in_progress" : "completed");
                repositoryRequests++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { full_name = "owner/revision" + repositoryRequests })),
                };
            }));
            var credential = new DevHubCredential("user", "test-token");
            var first = await CopilotCloudSessions.ReadAsync(client, credential, CancellationToken.None);
            var cached = await CopilotCloudSessions.ReadAsync(client, credential, CancellationToken.None);

            Assert.AreEqual(2, taskRequests);
            Assert.AreEqual(1, repositoryRequests);
            Assert.AreEqual("owner/revision1", cached.Sessions.Single().Repository);
            Assert.IsTrue(first.Sessions.Single().IsRunning);
            Assert.IsFalse(cached.Sessions.Single().IsRunning, "Task state must remain live even when its repository mapping is cached.");
            var forced = await CopilotCloudSessions.ReadAsync(client, credential, CancellationToken.None, forceRefresh: true);
            Assert.AreEqual("owner/revision2", forced.Sessions.Single().Repository);
            Assert.AreEqual(2, repositoryRequests);
            var changed = await CopilotCloudSessions.ReadAsync(client, new DevHubCredential("user", "different-token"), CancellationToken.None);
            Assert.AreEqual("owner/revision3", changed.Sessions.Single().Repository);
            Assert.AreEqual(3, repositoryRequests);
        }

        [TestMethod]
        [DataRow(3599, 1)]
        [DataRow(3600, 2)]
        public async Task CloudDiscovery_RepositoryNamesExpireAfterOneHourAsync(int seconds, int expectedRepositoryRequests)
        {
            var now = DateTimeOffset.UtcNow;
            var cache = new DevHubCredentialCache<string>(TimeSpan.FromHours(1), () => now, () => 0);
            var repositories = 0;
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/agents/tasks")
                    return CloudTaskResponse("completed");
                repositories++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"full_name\":\"owner/repo\"}") };
            }));
            var credential = new DevHubCredential("user", "test-token");
            await CopilotCloudSessions.ReadAsync(client, credential, CancellationToken.None, repositoryCache: cache);
            now = now.AddSeconds(seconds);

            await CopilotCloudSessions.ReadAsync(client, credential, CancellationToken.None, repositoryCache: cache);

            Assert.AreEqual(expectedRepositoryRequests, repositories);
        }

        [TestMethod]
        public async Task CloudDiscovery_CachedMappingCannotHideTaskAuthorizationFailureAsync()
        {
            var denied = false;
            var repositories = 0;
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/agents/tasks")
                    return denied ? new HttpResponseMessage(HttpStatusCode.Forbidden) : CloudTaskResponse("completed");
                repositories++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"full_name\":\"owner/repo\"}") };
            }));
            var credential = new DevHubCredential("user", "test-token");
            await CopilotCloudSessions.ReadAsync(client, credential, CancellationToken.None);
            denied = true;

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CopilotCloudSessions.ReadAsync(client, credential, CancellationToken.None));
            Assert.AreEqual(1, repositories);
        }

        [TestMethod]
        public async Task CloudDiscovery_InvalidatedLookupCannotPopulateRepositoryCacheAsync()
        {
            var generation = 0;
            var invalidate = true;
            var repositories = 0;
            var cache = new DevHubCredentialCache<string>(TimeSpan.FromHours(1), getGeneration: () => generation);
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/agents/tasks")
                    return CloudTaskResponse("completed");
                repositories++;
                if (invalidate)
                    generation++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"full_name\":\"owner/repo\"}") };
            }));
            var credential = new DevHubCredential("user", "test-token");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CopilotCloudSessions.ReadAsync(client, credential,
                CancellationToken.None, repositoryCache: cache));
            invalidate = false;

            var current = await CopilotCloudSessions.ReadAsync(client, credential, CancellationToken.None, repositoryCache: cache);

            Assert.AreEqual(2, repositories);
            Assert.AreEqual("owner/repo", current.Sessions.Single().Repository);
        }

        private static HttpResponseMessage CloudTaskResponse(string state) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                tasks = new[] { new
                {
                    id = RuntimeId,
                    repository = new { id = 123 },
                    agent_collaborators = new[] { new { slug = "copilot-swe-agent" } },
                    state,
                    updated_at = "2026-11-07T19:00:00Z",
                } },
            })),
        };

        [TestMethod]
        [DataRow(8)]
        [DataRow(30)]
        public async Task CloudDiscovery_CancellationStopsActiveAndQueuedRepositoryLookupsAsync(int repositoryCount)
        {
            var tasks = Enumerable.Range(1, repositoryCount).Select(index => new
            {
                id = Guid.NewGuid().ToString("D"),
                repository = new { id = index },
                agent_collaborators = new[] { new { slug = "copilot-swe-agent" } },
            }).ToArray();
            var requests = 0;
            var stopped = 0;
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new AsyncHandler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath == "/agents/tasks")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { tasks })) };
                if (Interlocked.Increment(ref requests) == CopilotCloudSessions.MaximumRepositoryConcurrency)
                    started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                    throw new AssertFailedException("Canceled repository requests must not return a response.");
                }
                finally
                {
                    Interlocked.Increment(ref stopped);
                }
            }));
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                var discovery = CopilotCloudSessions.ReadAsync(client, new DevHubCredential("user", "test-token"), cancellation.Token);
                await started.Task.WithCancellation(cancellation.Token);
                cancellation.Cancel();
                await discovery;
            });
            Assert.AreEqual(CopilotCloudSessions.MaximumRepositoryConcurrency, requests);
            Assert.AreEqual(requests, stopped, "Discovery must await cancellation of every active lookup.");
        }

        [TestMethod]
        public async Task CloudDiscovery_RepositoryAuthorizationFailureDoesNotInventSessionMetadataAsync()
        {
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
                request.RequestUri!.AbsolutePath == "/agents/tasks"
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"tasks\":[{\"id\":\"" + RuntimeId
                            + "\",\"repository\":{\"id\":1},\"agent_collaborators\":[{\"slug\":\"copilot-swe-agent\"}]}]}"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.Forbidden)));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                CopilotCloudSessions.ReadAsync(client, new DevHubCredential("user", "test-token"), CancellationToken.None));
        }

        [TestMethod]
        [DataRow("https://github.com/owner/repo/agents/tasks/96c12378-f593-4c38-982f-d16b25f68bc3", true)]
        [DataRow("http://github.com/owner/repo/agents/tasks/96c12378-f593-4c38-982f-d16b25f68bc3", false)]
        [DataRow("https://evil.example/owner/repo/agents/tasks/96c12378-f593-4c38-982f-d16b25f68bc3", false)]
        [DataRow("https://github.com@evil.example/owner/repo/agents/tasks/96c12378-f593-4c38-982f-d16b25f68bc3", false)]
        [DataRow("https://github.com/owner/repo/agents/tasks/a304b9be-c94c-4585-81f8-570cae2025db", false)]
        public void CloudUrls_RequireTheExactTrustedTask(string url, bool expected)
        {
            Assert.AreEqual(expected, CopilotCloudSessions.IsTaskUrl(url, Guid.Parse(RuntimeId)));
        }

        [TestMethod]
        public void Cards_ExposeOriginSpecificIconsAndDefaultActions()
        {
            var sessions = Enum.GetValues(typeof(CopilotSessionOrigin)).Cast<CopilotSessionOrigin>()
                .Select(origin => new CopilotActivitySession { Id = RuntimeId, Origin = origin, Repository = "owner/repo" }).ToArray();
            var cards = DevHubWorkItem.GetCopilotItems(sessions, false);
            Assert.HasCount(3, cards);
            foreach (var card in cards)
            {
                Assert.AreEqual("Open in " + card.CopilotSession.OriginName, card.ActionText);
                Assert.AreEqual(CopilotMonikers.ForOrigin(card.CopilotSession.Origin).Id, card.CopilotMoniker.Id);
            }
            var agentProperty = typeof(KnownMonikers).GetProperty("Agent");
            var agent = agentProperty?.GetValue(null) is Microsoft.VisualStudio.Imaging.Interop.ImageMoniker agentMoniker
                ? agentMoniker : CopilotMonikers.GitHubCopilot;
            Assert.AreEqual(agent.Id, CopilotMonikers.ForOrigin(CopilotSessionOrigin.VisualStudio).Id);
            Assert.AreEqual(KnownMonikers.Console.Id, CopilotMonikers.ForOrigin(CopilotSessionOrigin.Cli).Id);
            Assert.AreEqual(KnownMonikers.GitHub.Id, CopilotMonikers.ForOrigin(CopilotSessionOrigin.GitHub).Id);
            Assert.HasCount(3, sessions.Select(session => session.Key).Distinct().ToArray());
            Assert.AreEqual("owner/repo", cards.Single(card => card.CopilotSession.Origin == CopilotSessionOrigin.GitHub).Detail);
        }

        [TestMethod]
        [DataRow(0, "Visual Studio")]
        [DataRow(2, "Terminal")]
        [DataRow(3, "GitHub")]
        public void Cards_SourceTooltipsIdentifyTheApp(int origin, string expected)
        {
            var session = new CopilotActivitySession { Id = RuntimeId, Origin = (CopilotSessionOrigin)origin };
            var card = DevHubWorkItem.GetCopilotItems([session], false).Single();
            Assert.AreEqual(expected, card.CopilotSourceName);
        }

        [TestMethod]
        public void Cards_UseNativeMouseAndKeyboardMenuHandlersWithoutDestinationButtons()
        {
            using var stream = typeof(CopilotSessionSourcesTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            var document = XDocument.Load(stream!);
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var card = document.Descendants().Single(element => element.Attribute(x + "Name")?.Value == "WorkItemBorder");
            var icon = card.Descendants().Single(element => element.Attribute(x + "Name")?.Value == "CopilotActivityIcon");
            Assert.AreEqual("{Binding CopilotSourceName}", icon.Attribute("ToolTip")?.Value);
            Assert.AreEqual("CopilotItem_RightButtonDown", card.Attribute("PreviewMouseRightButtonDown")?.Value);
            Assert.AreEqual("CopilotItem_RightButtonUp", card.Attribute("PreviewMouseRightButtonUp")?.Value);
            Assert.AreEqual("CopilotItem_PreviewKeyDown", card.Attribute("PreviewKeyDown")?.Value);
            Assert.IsFalse(card.Descendants().Any(element => element.Name.LocalName == "Button"));
        }

        private static string Metadata(string client) => "id: " + RuntimeId + "\nclient_name: " + client
            + "\nname: 'A title: with punctuation'\ncwd: 'C:\\Repos\\Example'\nupdated_at: 2026-11-06T19:03:27.900Z\n";

        private static void SaveMetadata(string home, string id, string client, string? workingDirectory = null)
        {
            var directory = Path.Combine(home, "session-state", id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "workspace.yaml"), Metadata(client).Replace(RuntimeId, id)
                .Replace(@"C:\Repos\Example", workingDirectory ?? @"C:\Repos\Example"));
        }

        private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => send(request, cancellationToken);
        }

        private sealed class FakeApi(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
        {
            internal string? Authorization { get; private set; }
            internal Uri? RequestUri { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Authorization = request.Headers.Authorization?.ToString();
                RequestUri = request.RequestUri;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
            }
        }
    }
}