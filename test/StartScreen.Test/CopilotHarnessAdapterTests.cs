using System.Diagnostics.CodeAnalysis;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;
using Microsoft.VisualStudio.Copilot.Harness;
using Microsoft.VisualStudio.Threading;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test
{
    [TestClass]
    public class CopilotHarnessAdapterTests
    {
        [TestMethod]
        public void QueuedAttachment_ProcessesOneSessionAtATimeAndKeepsAttentionState()
        {
            var backend = new FakeBackend();
            var sessions = Enumerable.Range(0, 100).Select(_ => new FakeSession()).ToArray();
            var permission = new FakePermission();
            sessions[99].ChatEvents.Replace(permission);
            backend.Sessions.Replace(sessions);
            var queue = new Queue<Action>();
            var errors = new List<Exception>();
            using var adapter = new CopilotHarnessAdapter(queue.Enqueue, errors.Add);

            adapter.StartBackend(backend);
            Assert.IsEmpty(adapter.Sessions);
            Assert.IsFalse(adapter.IsReady);
            Assert.IsTrue(sessions.All(session => session.Title.SubscriberCount == 0));
            while (adapter.Sessions.Count == 0)
                queue.Dequeue()();
            Assert.HasCount(1, adapter.Sessions);
            Assert.AreEqual(1, sessions.Count(session => session.Title.SubscriberCount > 0));
            Assert.IsFalse(adapter.IsReady);
            while (queue.Count > 0)
                queue.Dequeue()();

            Assert.HasCount(100, adapter.Sessions);
            Assert.IsTrue(adapter.IsReady);
            Assert.AreEqual(1, adapter.Sessions.Single(session => session.Id == sessions[99].SessionId.Guid.ToString("D")).PendingPermissions);
            Assert.AreEqual(1, permission.State.SubscriberCount);
            Assert.IsEmpty(errors);
        }

        [TestMethod]
        public void QueuedAttachment_RemovedSessionsAndEntriesNeverSubscribe()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            backend.Sessions.Replace(session);
            var queue = new Queue<Action>();
            var errors = new List<Exception>();
            using var adapter = new CopilotHarnessAdapter(queue.Enqueue, errors.Add);
            adapter.StartBackend(backend);
            backend.Sessions.Replace();
            while (queue.Count > 0)
                queue.Dequeue()();
            Assert.IsEmpty(adapter.Sessions);
            Assert.AreEqual(0, session.Title.SubscriberCount);
            Assert.IsTrue(adapter.IsReady);

            var permission = new FakePermission();
            session.ChatEvents.Replace(permission);
            backend.Sessions.Replace(session);
            while (session.ChatEvents.Changes.SubscriberCount == 0)
                queue.Dequeue()();
            session.ChatEvents.Replace();
            while (queue.Count > 0)
                queue.Dequeue()();
            Assert.AreEqual(0, permission.State.SubscriberCount);
            Assert.IsFalse(adapter.Sessions.Single().NeedsAttention);
            Assert.IsTrue(adapter.IsReady);
            Assert.IsEmpty(errors);
        }

        [TestMethod]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "The assertion observes this test's own backend resolution without a UI synchronization context.")]
        public async Task ResolveBackendAsync_CancellationInterruptsWaitDuringSlowActivationAsync()
        {
            using var cancellation = new CancellationTokenSource();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolution = CopilotHarnessAdapter.ResolveBackendAsync(() =>
            {
                try
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("The test did not release backend activation.");
                    return new FakeBackends(new FakeBackend());
                }
                finally
                {
                    exited.TrySetResult(true);
                }
            }, cancellation.Token);
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                cancellation.Cancel();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<OperationCanceledException>(() => resolution.WithCancellation(timeout.Token));
                Assert.IsFalse(exited.Task.IsCompleted);
            }
            finally
            {
                release.Set();
                await exited.Task;
            }
        }
        [STATestMethod]
        public void ResolveBackendAsync_SlowActivationLeavesCallerFreeAndDefersSubscriptions()
        {
            using var context = new JoinableTaskContext();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var callerThread = Environment.CurrentManagedThreadId;
            var activationThread = 0;
            var backend = new FakeBackend();
            backend.Sessions.Replace(new FakeSession());
            var backends = new RecordingBackends(backend);
            JoinableTask<object> resolution = context.Factory.RunAsync(() => CopilotHarnessAdapter.ResolveBackendAsync(() =>
            {
                activationThread = Environment.CurrentManagedThreadId;
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The test did not release backend activation.");
                return backends;
            }, CancellationToken.None));

            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(resolution.IsCompleted, "The caller must return while activation is still blocked.");
                Assert.AreNotEqual(callerThread, activationThread);
                Assert.AreEqual(0, backend.Sessions.Changes.SubscriberCount);
            }
            finally
            {
                release.Set();
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var resolved = resolution.Join(timeout.Token);
            Assert.AreSame(backend, resolved);
            Assert.AreEqual(activationThread, backends.SelectionThread);
            Assert.AreEqual(0, backend.SessionsReady.SubscriberCount);
            using var adapter = CreateAdapter();
            adapter.StartBackend(resolved);
            Assert.IsTrue(adapter.IsReady);
            Assert.HasCount(1, adapter.Sessions);
            Assert.AreEqual(1, backend.Sessions.Changes.SubscriberCount);
        }

        [TestMethod]
        public async Task ResolveBackendAsync_PreCanceledConnectionDoesNotActivateExportsAsync()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var activated = false;

            await Assert.ThrowsAsync<OperationCanceledException>(() => CopilotHarnessAdapter.ResolveBackendAsync(() =>
            {
                activated = true;
                return new FakeBackends(new FakeBackend());
            }, cancellation.Token));

            Assert.IsFalse(activated);
        }

        [TestMethod]
        public async Task ResolveBackendAsync_CancellationDuringActivationDoesNotReturnBackendAsync()
        {
            using var cancellation = new CancellationTokenSource();
            var backend = new FakeBackend();

            await Assert.ThrowsAsync<OperationCanceledException>(() => CopilotHarnessAdapter.ResolveBackendAsync(() =>
            {
                cancellation.Cancel();
                return new FakeBackends(backend);
            }, cancellation.Token));

            Assert.AreEqual(0, backend.Sessions.Changes.SubscriberCount);
            Assert.AreEqual(0, backend.SessionsReady.SubscriberCount);
        }

        [TestMethod]
        public async Task ResolveBackendAsync_PropagatesActivationAndCompatibilityFailuresAsync()
        {
            var failure = new InvalidOperationException("Simulated MEF activation failure");

            var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                CopilotHarnessAdapter.ResolveBackendAsync(() => throw failure, CancellationToken.None));
            Assert.AreSame(failure, actual);
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
                CopilotHarnessAdapter.ResolveBackendAsync(() => new object(), CancellationToken.None));
        }

        [TestMethod]
        public void StartBackend_DisposedConnectionDoesNotSubscribe()
        {
            var backend = new FakeBackend();
            var adapter = CreateAdapter();
            adapter.Dispose();

            Assert.ThrowsExactly<InvalidOperationException>(() => adapter.StartBackend(backend));

            Assert.AreEqual(0, backend.Sessions.Changes.SubscriberCount);
            Assert.AreEqual(0, backend.SessionsReady.SubscriberCount);
        }

        [TestMethod]
        public void Start_DoesNotResolveBackendAgainAfterStartingOrDisposal()
        {
            var backends = new RecordingBackends(new FakeBackend());
            using var adapter = CreateAdapter();
            adapter.Start(backends);

            Assert.ThrowsExactly<InvalidOperationException>(() => adapter.Start(backends));
            adapter.Dispose();
            Assert.ThrowsExactly<InvalidOperationException>(() => adapter.Start(backends));
            Assert.AreEqual(1, backends.SelectionCount);
        }

        private sealed class RecordingBackends(FakeBackend backend) : ICopilotBackends
        {
            internal int SelectionThread { get; private set; }
            internal int SelectionCount { get; private set; }

            public ICopilotBackend GetDefault()
            {
                SelectionThread = Environment.CurrentManagedThreadId;
                SelectionCount++;
                return backend;
            }
        }

        [TestMethod]
        public void Start_DiscoversExistingSessionsWithoutActivatingOrOwningThem()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            backend.Sessions.Replace(session);
            var errors = new List<Exception>();
            using var adapter = CreateAdapter(errors);

            adapter.Start(new FakeBackends(backend));

            Assert.IsTrue(adapter.IsReady);
            Assert.AreEqual(session.SessionId.Guid.ToString("D"), adapter.Sessions.Single().Id);
            Assert.AreEqual("Conversation", adapter.Sessions.Single().Title);
            Assert.AreSame(session, adapter.FindSession(adapter.Sessions.Single().Id));
            Assert.IsEmpty(errors);
        }

        [TestMethod]
        public void Running_RequiresLiveEvidenceNotAnOpenSessionOrTranscript()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            session.IsResponding.Replay = false;
            backend.Sessions.Replace(session);
            using var adapter = CreateAdapter();
            adapter.Start(new FakeBackends(backend));

            Assert.IsNull(adapter.Sessions.Single().IsResponding);
            Assert.IsFalse(adapter.Sessions.Single().IsRunning);
            session.IsResponding.Publish(true);
            Assert.IsTrue(adapter.Sessions.Single().IsRunning);
            session.IsResponding.Publish(false);
            Assert.IsFalse(adapter.Sessions.Single().IsRunning);
            Assert.IsFalse(new CopilotActivitySession { IsResponding = true, IsTranscript = true }.IsRunning);
        }

        [TestMethod]
        public void Requests_TrackResolutionWithoutReadingContentOrAnsweringThem()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            var permission = new FakePermission();
            var question = new FakeQuestion();
            session.ChatEvents.Replace(permission, question);
            backend.Sessions.Replace(session);
            using var adapter = CreateAdapter();
            adapter.Start(new FakeBackends(backend));

            Assert.AreEqual(1, adapter.Sessions.Single().PendingPermissions);
            Assert.AreEqual(1, adapter.Sessions.Single().PendingQuestions);
            Assert.IsTrue(adapter.Sessions.Single().NeedsAttention);
            Assert.AreEqual("Waiting for permission and an answer", adapter.Sessions.Single().Status);
            permission.State.Publish("Accepted");
            Assert.AreEqual(0, adapter.Sessions.Single().PendingPermissions);
            Assert.IsTrue(adapter.Sessions.Single().NeedsAttention);
            question.State.Publish("Answered");
            Assert.IsFalse(adapter.Sessions.Single().NeedsAttention);
        }

        [TestMethod]
        public void SubagentRequests_AppearAndDisappearWithTheSubagent()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            var call = new FakeSubagentCall();
            session.ChatEvents.Replace(call);
            backend.Sessions.Replace(session);
            using var adapter = CreateAdapter();
            adapter.Start(new FakeBackends(backend));
            var child = new FakeSubagent();
            var question = new FakeQuestion();
            child.ChatEvents.Replace(question);

            call.Subagent.Publish(child);

            Assert.AreEqual(1, adapter.Sessions.Single().PendingQuestions);
            call.Subagent.Publish(null);
            Assert.AreEqual(0, adapter.Sessions.Single().PendingQuestions);
            Assert.AreEqual(0, question.State.SubscriberCount);
        }

        [TestMethod]
        public void RemovingEntriesOrSessions_RemovesPendingStateAndSubscriptions()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            var permission = new FakePermission();
            session.ChatEvents.Replace(permission);
            backend.Sessions.Replace(session);
            using var adapter = CreateAdapter();
            adapter.Start(new FakeBackends(backend));
            var id = adapter.Sessions.Single().Id;

            session.ChatEvents.Replace();

            Assert.IsFalse(adapter.Sessions.Single().NeedsAttention);
            Assert.AreEqual(0, permission.State.SubscriberCount);
            backend.Sessions.Replace();
            Assert.IsEmpty(adapter.Sessions);
            Assert.AreEqual(0, session.Title.SubscriberCount);
            Assert.ThrowsExactly<InvalidOperationException>(() => adapter.FindSession(id));
            Assert.AreEqual(0, session.DisposeCount);
        }

        [TestMethod]
        public void Dispose_UnsubscribesWithoutDisposingVsSessions()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            var permission = new FakePermission();
            session.ChatEvents.Replace(permission);
            backend.Sessions.Replace(session);
            var adapter = CreateAdapter();
            adapter.Start(new FakeBackends(backend));

            adapter.Dispose();
            adapter.Dispose();

            Assert.AreEqual(0, backend.Sessions.Changes.SubscriberCount);
            Assert.AreEqual(0, backend.SessionsReady.SubscriberCount);
            Assert.AreEqual(0, session.ChatEvents.Changes.SubscriberCount);
            Assert.AreEqual(0, session.IsResponding.SubscriberCount);
            Assert.AreEqual(0, permission.State.SubscriberCount);
            Assert.AreEqual(0, session.DisposeCount);
        }

        [TestMethod]
        public void Dispose_DropsAlreadyQueuedCallbacks()
        {
            var backend = new FakeBackend();
            backend.Sessions.Replace(new FakeSession());
            var queue = new List<Action>();
            var errors = new List<Exception>();
            var adapter = new CopilotHarnessAdapter(queue.Add, errors.Add);
            adapter.Start(new FakeBackends(backend));

            adapter.Dispose();
            foreach (var callback in queue)
                callback();

            Assert.IsEmpty(adapter.Sessions);
            Assert.IsEmpty(errors);
        }

        [TestMethod]
        public void CompatibilityAndStreamErrors_AreReportedInsteadOfEmptySuccess()
        {
            using var adapter = CreateAdapter();
            Assert.ThrowsExactly<NotSupportedException>(() => adapter.Start(new object()));
            var backend = new FakeBackend();
            var session = new FakeSession();
            var permission = new FakePermission();
            session.ChatEvents.Replace(permission);
            backend.Sessions.Replace(session);
            var errors = new List<Exception>();
            using var connected = CreateAdapter(errors);
            connected.Start(new FakeBackends(backend));

            permission.State.Publish("UnexpectedNewState");

            Assert.IsInstanceOfType<NotSupportedException>(errors.Single());
            var failure = new InvalidOperationException("Backend disconnected");
            backend.SessionsReady.Fail(failure);
            Assert.AreSame(failure, errors.Last());
        }

        [TestMethod]
        public void DiscoveryReadiness_ResetsOnWorkspaceChange()
        {
            var backend = new FakeBackend();
            using var adapter = CreateAdapter();
            adapter.Start(new FakeBackends(backend));

            backend.SessionsReady.Publish(false);
            Assert.IsFalse(adapter.IsReady);
            backend.SessionsReady.Publish(true);
            Assert.IsTrue(adapter.IsReady);
        }

        [TestMethod]
        public void WorkspaceFilter_RequiresKnownDirectoryAndAPathBoundary()
        {
            var session = new CopilotActivitySession { WorkingDirectory = @"C:\Code\App\src" };

            Assert.IsTrue(session.MatchesWorkspace(@"c:\code\app\"));
            Assert.IsFalse(session.MatchesWorkspace(@"C:\Code\App2"));
            session.WorkingDirectory = @"C:\Code\App2";
            Assert.IsFalse(session.MatchesWorkspace(@"C:\Code\App"));
            session.WorkingDirectory = null;
            Assert.IsFalse(session.MatchesWorkspace(@"C:\Code\App"));
        }

        [TestMethod]
        public void WorkItems_SeparateAttentionFromOverviewAndBoundOnlyIdleHistory()
        {
            var now = DateTimeOffset.UtcNow;
            var history = Enumerable.Range(0, 20).Select(index => new CopilotActivitySession
            {
                Id = $"history-{index}",
                Title = $"History {index}",
                LastActivity = now.AddMinutes(-index),
            });
            var running = Enumerable.Range(0, 12).Select(index => new CopilotActivitySession
            {
                Id = $"running-{index}",
                Title = $"Running {index}",
                IsResponding = true,
            });
            var waiting = new CopilotActivitySession
            {
                Id = "waiting",
                Title = "Waiting",
                IsResponding = true,
                PendingPermissions = 1,
            };
            var sessions = history.Concat(running).Append(waiting).ToArray();

            var overview = DevHubWorkItem.GetCopilotItems(sessions, false);
            var needsYou = DevHubWorkItem.GetCopilotItems(sessions, true);

            Assert.HasCount(22, overview);
            Assert.HasCount(1, needsYou);
            Assert.AreEqual("waiting", needsYou[0].CopilotSessionId);
            Assert.IsFalse(overview.Any(item => item.CopilotSessionId == "waiting"));
            Assert.IsTrue(overview.All(item => item.ActionText == "Open in Visual Studio" && item.WebUrl == null));
        }

        private static CopilotHarnessAdapter CreateAdapter(List<Exception>? errors = null)
            => new(callback => callback(), (errors ?? new List<Exception>()).Add);

        [TestMethod]
        public void CatalogBursts_QueueOneRescanAndPreserveRealReadiness()
        {
            var backend = new FakeBackend();
            backend.SessionsReady.Publish(false);
            var sessions = Enumerable.Range(0, 200).Select(_ => new FakeSession()).ToArray();
            var queue = new Queue<Action>();
            var errors = new List<Exception>();
            var queued = false;
            using var adapter = new CopilotHarnessAdapter(action =>
            {
                if (queued)
                    queue.Enqueue(action);
                else
                    action();
            }, errors.Add);
            adapter.Start(new FakeBackends(backend));
            var reads = backend.Sessions.ItemsReadCount;
            queued = true;

            for (var count = 1; count <= sessions.Length; count++)
                backend.Sessions.Replace(sessions.Take(count).ToArray());

            Assert.HasCount(1, queue, "A catalog burst must queue one snapshot rescan, not one per notification.");
            Assert.IsFalse(adapter.IsReady);
            backend.SessionsReady.Publish(true);
            Assert.HasCount(2, queue, "Readiness must remain an independently delivered event.");
            Assert.IsFalse(adapter.IsReady);
            queued = false;
            while (queue.Count > 0)
                queue.Dequeue()();

            Assert.IsTrue(adapter.IsReady);
            Assert.AreEqual(reads + 1, backend.Sessions.ItemsReadCount, "The burst must enumerate the catalog only once.");
            Assert.HasCount(sessions.Length, adapter.Sessions);
            CollectionAssert.AreEquivalent(sessions.Select(session => session.SessionId.Guid.ToString("D")).ToArray(),
                adapter.Sessions.Select(session => session.Id).ToArray());
            Assert.IsEmpty(errors);
        }

        [TestMethod]
        public void ChatBursts_QueueOneRescanWithoutDroppingRequestOrRunningState()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            var permission = new FakePermission();
            var question = new FakeQuestion();
            session.ChatEvents.Replace(permission);
            backend.Sessions.Replace(session);
            var queue = new Queue<Action>();
            var errors = new List<Exception>();
            var queued = false;
            using var adapter = new CopilotHarnessAdapter(action =>
            {
                if (queued)
                    queue.Enqueue(action);
                else
                    action();
            }, errors.Add);
            adapter.Start(new FakeBackends(backend));
            var reads = session.ChatEvents.ItemsReadCount;
            queued = true;

            for (var count = 0; count < 200; count++)
                session.ChatEvents.Replace(permission, question);
            permission.State.Publish("Accepted");
            session.IsResponding.Publish(true);

            Assert.HasCount(3, queue, "Only collection rescans may be coalesced, not permission or running state.");
            queued = false;
            while (queue.Count > 0)
                queue.Dequeue()();

            var current = adapter.Sessions.Single();
            Assert.AreEqual(reads + 1, session.ChatEvents.ItemsReadCount, "The burst must enumerate chat entries only once.");
            Assert.AreEqual(0, current.PendingPermissions);
            Assert.AreEqual(1, current.PendingQuestions);
            Assert.IsTrue(current.IsRunning);
            Assert.IsTrue(current.NeedsAttention);
            Assert.AreEqual(1, permission.State.SubscriberCount);
            Assert.AreEqual(1, question.State.SubscriberCount);
            Assert.IsEmpty(errors);
        }

        [TestMethod]
        public void CollectionBursts_DoNotCoalesceStreamErrors()
        {
            var backend = new FakeBackend();
            var queue = new Queue<Action>();
            var errors = new List<Exception>();
            using var adapter = new CopilotHarnessAdapter(queue.Enqueue, errors.Add);
            adapter.Start(new FakeBackends(backend));
            while (queue.Count > 0)
                queue.Dequeue()();
            var first = new InvalidOperationException("First stream failure");
            var second = new InvalidOperationException("Second stream failure");

            for (var count = 0; count < 200; count++)
                backend.Sessions.Replace();
            backend.Sessions.Changes.Fail(first);
            backend.Sessions.Changes.Fail(second);

            Assert.HasCount(3, queue);
            while (queue.Count > 0)
                queue.Dequeue()();
            CollectionAssert.AreEqual(new[] { first, second }, errors.ToArray());
        }

        [TestMethod]
        public void CollectionChangeDuringRescan_QueuesAnotherSnapshotInsteadOfLosingIt()
        {
            var backend = new FakeBackend();
            var queue = new Queue<Action>();
            var errors = new List<Exception>();
            var replacement = new FakeSession();
            using var adapter = new CopilotHarnessAdapter(queue.Enqueue, errors.Add);
            adapter.Start(new FakeBackends(backend));
            while (queue.Count > 0)
                queue.Dequeue()();
            var replaced = false;
            adapter.Changed += (_, _) =>
            {
                if (!replaced)
                {
                    replaced = true;
                    backend.Sessions.Replace(replacement);
                }
            };

            backend.Sessions.Replace(new FakeSession());
            while (queue.Count > 0)
                queue.Dequeue()();

            Assert.IsTrue(replaced);
            Assert.AreEqual(replacement.SessionId.Guid.ToString("D"), adapter.Sessions.Single().Id);
            Assert.AreSame(replacement, adapter.FindSession(adapter.Sessions.Single().Id));
            Assert.AreEqual(1, replacement.ChatEvents.Changes.SubscriberCount);
            Assert.IsEmpty(errors);
        }

        [TestMethod]
        public void Start_DisplaysExistingSessionsBeforeCatalogCompletionWithoutWaitingForAChange()
        {
            var backend = new FakeBackend();
            backend.SessionsReady.Publish(false);
            backend.Sessions.Changes.Replay = false;
            var session = new FakeSession();
            backend.Sessions.Replace(session);
            using var adapter = CreateAdapter();

            adapter.Start(new FakeBackends(backend));

            Assert.IsFalse(adapter.IsReady);
            Assert.HasCount(1, adapter.Sessions);
            Assert.IsTrue(adapter.HasSession(session.SessionId.Guid.ToString("D")));
            Assert.IsFalse(adapter.HasSession(Guid.NewGuid().ToString("D")));
            Assert.AreSame(session, adapter.FindSession(adapter.Sessions.Single().Id));
        }

        [TestMethod]
        public void ListedSession_CompletedFalseSignalIsNotLiveIdleEvidence()
        {
            var backend = new FakeBackend();
            var listed = new FakeSession();
            listed.IsResponding.CompleteOnSubscribe = true;
            backend.Sessions.Replace(listed);
            using var adapter = CreateAdapter();

            adapter.Start(new FakeBackends(backend));

            Assert.IsNull(adapter.Sessions.Single().IsResponding);
            Assert.AreEqual(string.Empty, adapter.Sessions.Single().StatusBadge);
            Assert.IsFalse(adapter.Sessions.Single().IsRunning);
        }

        [TestMethod]
        public void Hydration_ReplacesListedStateAndTracksRunningWaitingAndIdle()
        {
            var backend = new FakeBackend();
            var listed = new FakeSession();
            listed.IsResponding.CompleteOnSubscribe = true;
            backend.Sessions.Replace(listed);
            using var adapter = CreateAdapter();
            adapter.Start(new FakeBackends(backend));
            var live = new FakeSession();
            live.SessionId.Guid = listed.SessionId.Guid;
            live.IsResponding.Publish(true);

            backend.Sessions.Replace(live);

            Assert.AreEqual("Running", adapter.Sessions.Single().StatusBadge);
            Assert.AreEqual(0, listed.IsResponding.SubscriberCount);
            var permission = new FakePermission();
            live.ChatEvents.Replace(permission);
            Assert.AreEqual("Waiting", adapter.Sessions.Single().StatusBadge);
            permission.State.Publish("Accepted");
            live.IsResponding.Publish(false);
            Assert.AreEqual("Idle", adapter.Sessions.Single().StatusBadge);
            live.IsResponding.Complete();
            Assert.AreEqual(string.Empty, adapter.Sessions.Single().StatusBadge);
        }

        [TestMethod]
        public async Task SavedMetadata_PopulatesCardsBeforeLiveAttachmentAndLiveStateWinsAsync()
        {
            using var directory = new TemporaryDirectory();
            var backend = new FakeBackend();
            backend.SessionsReady.Publish(false);
            var session = new FakeSession();
            session.Title.Publish("Live title");
            var permission = new FakePermission();
            session.ChatEvents.Replace(permission);
            var unsaved = new FakeSession();
            backend.Sessions.Replace(session, unsaved);
            var queue = new Queue<Action>();
            var errors = new List<Exception>();
            using var adapter = new CopilotHarnessAdapter(queue.Enqueue, errors.Add);
            adapter.StartBackend(backend);
            var id = session.SessionId.Guid.ToString("D");
            var path = directory.GetPath(Path.Combine("session-state", id, "workspace.yaml"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"id: {id}\nclient_name: visualstudio-chat\nname: Saved title\ncwd: 'C:\\Code\\App'\nupdated_at: 2026-11-06T19:03:27.900Z\n");

            var metadata = await CopilotLocalSessions.ReadAsync(directory.Path, CancellationToken.None, origin: CopilotSessionOrigin.VisualStudio);
            var cache = new CopilotActivityCache();
            cache.Update(metadata.Sessions, true);
            var early = cache.Update(adapter.Sessions, adapter.IsReady);
            Assert.IsFalse(adapter.IsReady);
            Assert.IsEmpty(adapter.Sessions);
            Assert.HasCount(1, DevHubWorkItem.GetCopilotItems(early, false));
            Assert.AreEqual("Saved title", early.Single().Title);
            Assert.AreEqual(@"C:\Code\App", early.Single().WorkingDirectory);
            Assert.AreEqual(string.Empty, early.Single().StatusBadge);

            while (queue.Count > 0)
                queue.Dequeue()();
            var enriched = cache.Update(adapter.Sessions, adapter.IsReady);
            Assert.IsFalse(adapter.IsReady);
            Assert.HasCount(2, enriched);
            Assert.AreEqual("Live title", enriched.Single(item => item.Id == id).Title);
            Assert.AreEqual("Waiting", enriched.Single(item => item.Id == id).StatusBadge);
            Assert.IsFalse(enriched.Single(item => item.Id == id).IsCached);
            Assert.IsTrue(enriched.Any(item => item.Id == unsaved.SessionId.Guid.ToString("D")));
            Assert.HasCount(1, DevHubWorkItem.GetCopilotItems(enriched, true));
            backend.Sessions.Replace();
            backend.SessionsReady.Publish(true);
            while (queue.Count > 0)
                queue.Dequeue()();
            Assert.IsTrue(adapter.IsReady);
            Assert.IsEmpty(cache.Update(adapter.Sessions, adapter.IsReady));
            Assert.AreEqual(0, permission.State.SubscriberCount);
            Assert.IsEmpty(errors);
        }

        [STATestMethod]
        public void Refresh_KeepsTheConnectedAdapterAndPermissionSubscriptions()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            var permission = new FakePermission();
            session.ChatEvents.Replace(permission);
            backend.Sessions.Replace(session);
            using var adapter = CreateAdapter();
            adapter.StartBackend(backend);
            var panel = CreatePanel();
            var field = typeof(DevHubPanel).GetField("_copilotAdapter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            field.SetValue(panel, adapter);

            panel.RefreshCopilotActivity();

            Assert.AreSame(adapter, field.GetValue(panel));
            Assert.IsTrue(adapter.HasSession(session.SessionId.Guid.ToString("D")));
            Assert.AreEqual(1, session.Title.SubscriberCount);
            Assert.AreEqual(1, permission.State.SubscriberCount);
            permission.State.Publish("Accepted");
            Assert.IsFalse(adapter.Sessions.Single().NeedsAttention);
            Assert.AreEqual("Idle", adapter.Sessions.Single().StatusBadge);
        }

        [STATestMethod]
        public void BusyState_DoesNotWaitForTheLiveIndexButTracksSavedSourceRefresh()
        {
            var backend = new FakeBackend();
            backend.SessionsReady.Publish(false);
            using var adapter = CreateAdapter();
            adapter.StartBackend(backend);
            var panel = CreatePanel();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(DevHubPanel).GetField("_copilotAdapter", flags)!.SetValue(panel, adapter);
            typeof(DevHubPanel).GetField("_copilotConnecting", flags)!.SetValue(panel, true);
            var updateBusy = typeof(DevHubPanel).GetMethod("UpdateBusyState", flags)!;

            updateBusy.Invoke(panel, null);
            Assert.IsFalse(adapter.IsReady);
            Assert.IsFalse(panel.IsBusy);
            var loading = typeof(DevHubPanel).GetField("_copilotSourcesLoading", flags)!;
            loading.SetValue(panel, true);
            updateBusy.Invoke(panel, null);
            Assert.IsTrue(panel.IsBusy);
            loading.SetValue(panel, false);
            updateBusy.Invoke(panel, null);
            Assert.IsFalse(panel.IsBusy);
        }

        [TestMethod]
        public void Cache_RetainsOnlyBoundedMetadataNeverLiveOrPermissionState()
        {
            var now = DateTimeOffset.UtcNow;
            var sessions = Enumerable.Range(0, 120).Select(index => new CopilotActivitySession
            {
                Id = index.ToString(),
                Title = $"Conversation {index}",
                WorkingDirectory = @"C:\Code\App",
                LastActivity = now.AddMinutes(-index),
                IsResponding = true,
                PendingPermissions = 1,
                PendingQuestions = 1,
            }).ToArray();
            var cache = new CopilotActivityCache();

            var current = cache.Update(sessions, true);

            Assert.AreSame(sessions, current);
            Assert.HasCount(100, cache.Sessions);
            Assert.AreEqual("0", cache.Sessions[0].Id);
            Assert.AreEqual("99", cache.Sessions[99].Id);
            Assert.IsTrue(cache.Sessions.All(session => session.IsCached && !session.IsRunning && !session.NeedsAttention
                && session.IsResponding == null && session.PendingPermissions == 0 && session.PendingQuestions == 0
                && session.StatusBadge == string.Empty));
            sessions[0].Title = "Changed after caching";
            Assert.AreEqual("Conversation 0", cache.Sessions[0].Title);
            Assert.IsTrue(cache.Sessions[0].MatchesWorkspace(@"C:\Code\App"));
            Assert.IsFalse(cache.Sessions[0].MatchesWorkspace(@"C:\Code\App2"));
            Assert.IsEmpty(DevHubWorkItem.GetCopilotItems(cache.Sessions, true));
        }

        [TestMethod]
        public void Cache_ProgressiveDiscoveryKeepsHistoryUntilCompletionAndRemovesDeletedSessions()
        {
            var cache = new CopilotActivityCache();
            cache.Update([new CopilotActivitySession { Id = "old", Title = "Old" }], true);
            var live = new CopilotActivitySession { Id = "new", Title = "New", IsResponding = true };

            var discovering = cache.Update([live], false);

            Assert.HasCount(2, discovering);
            Assert.AreSame(live, discovering.Single(session => session.Id == "new"));
            Assert.IsTrue(discovering.Single(session => session.Id == "old").IsCached);
            var ready = cache.Update([live], true);
            Assert.HasCount(1, ready);
            Assert.AreSame(live, ready.Single());
            cache.Update([], true);
            Assert.IsEmpty(cache.Sessions);
        }

        [TestMethod]
        [DataRow(null, false, false, 0, 0, "")]
        [DataRow(true, false, false, 0, 0, "Running")]
        [DataRow(false, false, false, 0, 0, "Idle")]
        [DataRow(null, false, false, 1, 0, "Waiting")]
        [DataRow(null, false, false, 0, 1, "Waiting")]
        [DataRow(null, false, true, 0, 0, "Saved")]
        [DataRow(true, true, false, 1, 1, "")]
        public void WorkItems_OnlyShowConfirmedSessionStates(bool? responding, bool cached, bool transcript,
            int permissions, int questions, string expectedState)
        {
            var session = new CopilotActivitySession
            {
                Id = "session",
                IsResponding = responding,
                IsCached = cached,
                IsTranscript = transcript,
                PendingPermissions = permissions,
                PendingQuestions = questions,
            };

            var item = DevHubWorkItem.GetCopilotItems([session], session.NeedsAttention).Single();

            Assert.AreEqual(expectedState, session.StatusBadge);
            Assert.AreEqual(expectedState, item.CopilotStatus);
        }

        [TestMethod]
        public void WorkItems_ShowNewsStyleDateAndWorkspaceSeparatelyFromLiveStatus()
        {
            var time = new DateTimeOffset(2026, 11, 4, 15, 53, 0, TimeSpan.Zero);
            var item = DevHubWorkItem.GetCopilotItems([new CopilotActivitySession
            {
                Id = "session",
                Title = "Conversation",
                WorkingDirectory = @"C:\Code\App",
                LastActivity = time,
                IsResponding = true,
            }], false).Single();

            Assert.AreEqual($"{time.LocalDateTime:MMM d} in App", item.ActivityText);
            Assert.AreEqual(time.LocalDateTime.ToString("f"), item.ActivityDateToolTip);
            Assert.AreEqual("Running", item.CopilotStatus);
            Assert.AreEqual("App", item.Detail);
        }

        [TestMethod]
        public void OpenSession_NavigatesToTheExistingSessionAsAnExplicitUserAction()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            backend.Sessions.Replace(session);
            using var adapter = CreateAdapter();
            adapter.Start(new FakeBackends(backend));
            var navigator = new FakeNavigator();

            adapter.OpenSession(navigator, session.SessionId.Guid.ToString("D"));

            Assert.AreSame(session, navigator.OpenedSession);
            Assert.IsTrue(navigator.UserInitiated);
            Assert.ThrowsExactly<NotSupportedException>(() => adapter.OpenSession(new object(), adapter.Sessions.Single().Id));
            Assert.ThrowsExactly<InvalidOperationException>(() => adapter.OpenSession(navigator, Guid.NewGuid().ToString("D")));
        }

        [TestMethod]
        public void RemovedSession_QueuedSubagentCallbacksCannotCreateNewSubscriptions()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            var call = new FakeSubagentCall();
            session.ChatEvents.Replace(call);
            backend.Sessions.Replace(session);
            bool queued = false;
            var queue = new List<Action>();
            var errors = new List<Exception>();
            using var adapter = new CopilotHarnessAdapter(action =>
            {
                if (queued)
                    queue.Add(action);
                else
                    action();
            }, errors.Add);
            adapter.Start(new FakeBackends(backend));
            var child = new FakeSubagent();
            var question = new FakeQuestion();
            child.ChatEvents.Replace(question);
            queued = true;
            call.Subagent.Publish(child);
            queued = false;

            backend.Sessions.Replace();
            foreach (var callback in queue)
                callback();

            Assert.IsEmpty(adapter.Sessions);
            Assert.AreEqual(0, child.ChatEvents.Changes.SubscriberCount);
            Assert.AreEqual(0, question.State.SubscriberCount);
            Assert.IsEmpty(errors);
        }

        private static DevHubPanel CreatePanel()
        {
            var application = System.Windows.Application.Current ?? new System.Windows.Application();
            var stylesKey = Microsoft.VisualStudio.Shell.VsResourceKeys.ThemedDialogDefaultStylesKey;
            if (!application.Resources.Contains(stylesKey))
                application.Resources.Add(stylesKey, new System.Windows.ResourceDictionary());
            AddStyle(Microsoft.VisualStudio.Shell.VsResourceKeys.ThemedDialogComboBoxStyleKey, typeof(System.Windows.Controls.ComboBox));
            AddStyle(Microsoft.VisualStudio.Shell.VsResourceKeys.TextBoxStyleKey, typeof(System.Windows.Controls.TextBox));
            return new DevHubPanel();

            void AddStyle(object key, Type targetType)
            {
                if (application.Resources.Contains(key))
                    return;
                var style = new System.Windows.Style(targetType);
                style.Seal();
                application.Resources.Add(key, style);
            }
        }

        [TestMethod]
        public void PartialEntryAttachmentFailure_RemovesEarlierSubscriptions()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            var broken = new FakeBrokenEntry();
            session.ChatEvents.Replace(broken);
            backend.Sessions.Replace(session);
            var errors = new List<Exception>();
            using var adapter = CreateAdapter(errors);

            adapter.Start(new FakeBackends(backend));

            Assert.HasCount(1, errors);
            Assert.AreEqual(0, broken.State.SubscriberCount);
        }
    }

    public interface IFakeObservableCollection
    {
        IEnumerable<object> Items { get; }
        IObservable<int> Connect(Func<object, bool>? predicate = null, bool suppressEmptyChangeSets = true);
    }

    public sealed class FakeCollection : IFakeObservableCollection
    {
        private IEnumerable<object> _items = [];
        public int ItemsReadCount { get; private set; }
        public IEnumerable<object> Items
        {
            get
            {
                ItemsReadCount++;
                return _items;
            }
            private set => _items = value;
        }
        public FakeValue<int> Changes { get; } = new(0);
        public IObservable<int> Connect(Func<object, bool>? predicate = null, bool suppressEmptyChangeSets = true) => Changes;
        public void Replace(params object[] items)
        {
            Items = items;
            Changes.Publish(0);
        }
    }

    public sealed class FakeValue<T>(T initial) : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = [];
        private T _current = initial;
        public bool Replay { get; set; } = true;
        public bool CompleteOnSubscribe { get; set; }
        public int SubscriberCount => _observers.Count;
        public IDisposable Subscribe(IObserver<T> observer)
        {
            _observers.Add(observer);
            if (Replay)
                observer.OnNext(_current);
            if (CompleteOnSubscribe)
                observer.OnCompleted();
            return new RemoveObserver(() => _observers.Remove(observer));
        }

        public void Publish(T value)
        {
            _current = value;
            foreach (var observer in _observers.ToArray())
                observer.OnNext(value);
        }

        public void Fail(Exception error)
        {
            foreach (var observer in _observers.ToArray())
                observer.OnError(error);
        }

        public void Complete()
        {
            foreach (var observer in _observers.ToArray())
                observer.OnCompleted();
        }

        private sealed class RemoveObserver(Action remove) : IDisposable
        {
            public void Dispose() => remove();
        }
    }

    public sealed class FakeBackends(FakeBackend backend) : ICopilotBackends
    {
        public ICopilotBackend GetDefault() => backend;
    }

    public sealed class FakeBackend : ICopilotBackend
    {
        public FakeCollection Sessions { get; } = new();
        public FakeValue<bool> SessionsReady { get; } = new(true);
        public List<FakeSession> Created { get; } = [];
        public FakeCreateOptions? CreateOptions { get; private set; }
        public FakeSession NextSession { get; set; } = new();
        public Task<IBackendSession> CreateNewSessionAsync(FakeCreateOptions options)
        {
            CreateOptions = options;
            Created.Add(NextSession);
            return Task.FromResult<IBackendSession>(NextSession);
        }
    }

    public sealed class FakeSession : IBackendSession, IDisposable
    {
        public FakeSessionId SessionId { get; } = new();
        public FakeValue<string> Title { get; } = new("Conversation");
        public FakeValue<string?> WorkingDirectory { get; } = new(@"C:\Code\App");
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public FakeValue<DateTimeOffset> LastActivity { get; } = new(DateTimeOffset.UtcNow);
        public FakeValue<bool> IsResponding { get; } = new(false);
        public bool IsTranscript => false;
        public FakeCollection ChatEvents { get; } = new();
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
        public FakeChatSendRequest? Request { get; private set; }
        public Func<FakeChatSendRequest, Task<FakeChatResponse>> RespondAsync { get; set; } = _ => Task.FromResult(new FakeChatResponse());
        public Task<FakeChatResponse> SendAsync(FakeChatSendRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return RespondAsync(request);
        }
    }

    public sealed class FakeCreateOptions
    {
        public FakeFeatureId Feature { get; set; }
        public bool IsEphemeral { get; set; }
    }

    public readonly record struct FakeFeatureId(string Value);
    public readonly record struct FakeCorrelationId(Guid Guid);
    public sealed class FakeChatAttachment { }
    public sealed class FakeChatOptions { }
    public sealed class FakeChatSendRequest
    {
        public string? Prompt { get; set; }
        public FakeFeatureId? Feature { get; set; }
        public FakeCorrelationId CorrelationId { get; set; }
        public IReadOnlyList<FakeChatAttachment>? Attachments { get; set; }
        public FakeChatOptions? Options { get; set; }
    }
    public sealed class FakeChatResponse
    {
        public string Status { get; set; } = "Success";
        public FakeChatError? Error { get; set; }
    }
    public sealed class FakeChatError
    {
        public string? Message { get; set; }
    }

    public sealed class FakeSessionId
    {
        public Guid Guid { get; set; } = Guid.NewGuid();
    }

    public sealed class FakePermission : IBackendChatConfirmation
    {
        public FakeValue<string> State { get; } = new("Pending");
    }

    public sealed class FakeQuestion : IBackendChatUserInput
    {
        public FakeValue<string> State { get; } = new("Pending");
    }

    public sealed class FakeSubagentCall : IBackendChatSubagentCall
    {
        public FakeValue<FakeSubagent?> Subagent { get; } = new(null);
    }

    public sealed class FakeSubagent
    {
        public FakeCollection ChatEvents { get; } = new();
    }

    public sealed class FakeBrokenEntry : IBackendChatConfirmation, IBackendChatSubagentCall
    {
        public FakeValue<string> State { get; } = new("Pending");
        public object Subagent => throw new NotSupportedException("Simulated incompatible subagent contract");
    }

    public sealed class FakeNavigator : Microsoft.VisualStudio.Copilot.UI.HarnessUi.IHarnessSessionNavigator
    {
        public IBackendSession? OpenedSession { get; private set; }
        public bool UserInitiated { get; private set; }
        public bool IsReady { get; set; } = true;
        public Task<FakeSessionId?> WaitForSessionReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(IsReady ? OpenedSession?.SessionId : null);
        }
        public void OpenSession(IBackendSession session, bool userInitiated = false)
        {
            OpenedSession = session;
            UserInitiated = userInitiated;
        }
    }
}

namespace Microsoft.VisualStudio.Copilot.Harness
{
    public interface ICopilotBackends
    {
        ICopilotBackend GetDefault();
    }
    public interface ICopilotBackend
    {
        Task<IBackendSession> CreateNewSessionAsync(StartScreen.Test.FakeCreateOptions options);
    }
    public interface IBackendSession
    {
        StartScreen.Test.FakeSessionId SessionId { get; }
        Task<StartScreen.Test.FakeChatResponse> SendAsync(StartScreen.Test.FakeChatSendRequest request, CancellationToken cancellationToken);
    }
    public interface IBackendChatConfirmation { }
    public interface IBackendChatUserInput { }
    public interface IBackendChatSubagentCall { }
}

namespace Microsoft.VisualStudio.Copilot.UI.HarnessUi
{
    public interface IHarnessSessionNavigator
    {
        void OpenSession(IBackendSession session, bool userInitiated = false);
        Task<StartScreen.Test.FakeSessionId?> WaitForSessionReadyAsync(CancellationToken cancellationToken);
    }
}
