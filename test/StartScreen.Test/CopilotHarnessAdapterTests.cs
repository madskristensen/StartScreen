using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;
using Microsoft.VisualStudio.Copilot.Harness;

namespace StartScreen.Test
{
    [TestClass]
    public class CopilotHarnessAdapterTests
    {
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
            Assert.IsTrue(overview.All(item => item.ActionText == "Open in Copilot" && item.WebUrl == null));
        }

        private static CopilotHarnessAdapter CreateAdapter(List<Exception>? errors = null)
            => new(callback => callback(), (errors ?? new List<Exception>()).Add);

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
        public IEnumerable<object> Items { get; private set; } = [];
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
    public interface ICopilotBackend { }
    public interface IBackendSession { }
    public interface IBackendChatConfirmation { }
    public interface IBackendChatUserInput { }
    public interface IBackendChatSubagentCall { }
}

namespace Microsoft.VisualStudio.Copilot.UI.HarnessUi
{
    public interface IHarnessSessionNavigator
    {
        void OpenSession(IBackendSession session, bool userInitiated = false);
    }
}
