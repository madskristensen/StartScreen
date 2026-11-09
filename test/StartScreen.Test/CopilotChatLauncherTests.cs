using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class CopilotChatLauncherTests
    {
        [TestMethod]
        public async Task StartAsync_CreatesANewPersistentChatOpensItAndSendsThePromptAsync()
        {
            var backend = new FakeBackend();
            var existing = new FakeSession();
            backend.Sessions.Replace(existing);
            var navigator = new FakeNavigator();
            backend.NextSession.RespondAsync = request =>
            {
                Assert.AreSame(backend.NextSession, navigator.OpenedSession);
                return Task.FromResult(new FakeChatResponse());
            };

            var id = await CopilotChatLauncher.StartAsync(new FakeBackends(backend), navigator, "Fix https://github.com/o/r/issues/1", CancellationToken.None);

            Assert.AreEqual(backend.NextSession.SessionId.Guid.ToString("D"), id);

            Assert.HasCount(1, backend.Created);
            Assert.AreNotSame(existing, navigator.OpenedSession);
            Assert.IsTrue(navigator.UserInitiated);
            Assert.IsNotNull(backend.CreateOptions);
            Assert.IsFalse(backend.CreateOptions.IsEphemeral);
            Assert.AreEqual("StartScreen", backend.CreateOptions.Feature.Value);
            var request = backend.NextSession.Request;
            Assert.IsNotNull(request);
            Assert.AreEqual("Fix https://github.com/o/r/issues/1", request.Prompt);
            Assert.AreNotEqual(Guid.Empty, request.CorrelationId.Guid);
            Assert.IsNotNull(request.Attachments);
            Assert.IsEmpty(request.Attachments);
            Assert.IsNotNull(request.Options);
            Assert.AreEqual("StartScreen", request.Feature?.Value);
            Assert.IsNull(existing.Request);
            Assert.AreEqual(0, backend.NextSession.DisposeCount);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task WorkspaceChat_CreatesABlankPersistentSessionAndVerifiesItsDirectoryAsync(bool driveRoot)
        {
            var backend = new FakeBackend();
            var existing = new FakeSession();
            backend.Sessions.Replace(existing);
            var navigator = new FakeNavigator();
            var directory = driveRoot ? Path.GetPathRoot(Path.GetTempPath())! : Path.GetTempPath();
            await CopilotChatLauncher.StartWorkspaceAsync(new FakeBackends(backend), navigator, directory, CancellationToken.None);
            Assert.AreSame(backend.NextSession, navigator.OpenedSession);
            Assert.IsTrue(navigator.UserInitiated);
            Assert.HasCount(1, backend.Created);
            Assert.IsNotNull(backend.CreateOptions);
            Assert.IsFalse(backend.CreateOptions.IsEphemeral);
            Assert.AreEqual("StartScreen", backend.CreateOptions.Feature.Value);
            Assert.AreEqual(Path.GetFullPath(directory), backend.NextSession.ChangedDirectory);
            Assert.IsNull(backend.NextSession.Request, "Starting an empty chat must not send a fabricated prompt.");
            Assert.IsNull(existing.Request);
            Assert.IsEmpty(backend.Deleted);
            Assert.AreEqual(0, backend.NextSession.WorkingDirectory.SubscriberCount);
        }

        [TestMethod]
        public async Task WorkspaceChat_AllowsAnUnchangeableBackendAlreadyInTheRequestedDirectoryAsync()
        {
            var backend = new FakeBackend();
            backend.NextSession.CanChangeWorkingDirectory = false;
            backend.NextSession.WorkingDirectory.Publish(Path.GetTempPath().ToUpperInvariant());
            await CopilotChatLauncher.StartWorkspaceAsync(new FakeBackends(backend), new FakeNavigator(), Path.GetTempPath(), CancellationToken.None);
            Assert.IsNull(backend.NextSession.ChangedDirectory);
            Assert.IsEmpty(backend.Deleted);
        }

        [TestMethod]
        public async Task WorkspaceChat_UnsupportedBackendRemovesOnlyTheNewEmptySessionAsync()
        {
            var backend = new FakeBackend();
            var existing = new FakeSession();
            backend.Sessions.Replace(existing);
            backend.NextSession.CanChangeWorkingDirectory = false;
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => CopilotChatLauncher.StartWorkspaceAsync(
                new FakeBackends(backend), new FakeNavigator(), Path.GetTempPath(), CancellationToken.None));
            CollectionAssert.AreEqual(new[] { backend.NextSession.SessionId }, backend.Deleted);
            Assert.IsNull(backend.NextSession.Request);
            Assert.IsNull(existing.Request);
            Assert.AreEqual(0, backend.NextSession.WorkingDirectory.SubscriberCount);
        }

        [TestMethod]
        public async Task WorkspaceChat_DirectoryChangeMustActuallyBeReportedAndCancellationCleansUpAsync()
        {
            var backend = new FakeBackend();
            using var cancellation = new CancellationTokenSource();
            backend.NextSession.ChangeDirectoryAsync = (_, _) =>
            {
                backend.NextSession.WorkingDirectory.Publish(@"C:\wrong-workspace");
                cancellation.Cancel();
                return Task.CompletedTask;
            };
            await Assert.ThrowsAsync<OperationCanceledException>(() => CopilotChatLauncher.StartWorkspaceAsync(
                new FakeBackends(backend), new FakeNavigator(), Path.GetTempPath(), cancellation.Token));
            CollectionAssert.AreEqual(new[] { backend.NextSession.SessionId }, backend.Deleted);
            Assert.IsNull(backend.NextSession.Request);
            Assert.AreEqual(0, backend.NextSession.WorkingDirectory.SubscriberCount);
        }

        [TestMethod]
        public async Task WorkspaceChat_RejectsMissingPathsAndIncompatibleNavigationBeforeCreationAsync()
        {
            var backend = new FakeBackend();
            await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => CopilotChatLauncher.StartWorkspaceAsync(
                new FakeBackends(backend), new FakeNavigator(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), CancellationToken.None));
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => CopilotChatLauncher.StartWorkspaceAsync(
                new FakeBackends(backend), new object(), Path.GetTempPath(), CancellationToken.None));
            Assert.IsEmpty(backend.Created);
        }

        [TestMethod]
        public async Task WorkspaceChat_UnreadySessionIsRemovedWithoutChangingDirectoryAsync()
        {
            var backend = new FakeBackend();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CopilotChatLauncher.StartWorkspaceAsync(
                new FakeBackends(backend), new FakeNavigator { IsReady = false }, Path.GetTempPath(), CancellationToken.None));
            CollectionAssert.AreEqual(new[] { backend.NextSession.SessionId }, backend.Deleted);
            Assert.IsNull(backend.NextSession.ChangedDirectory);
            Assert.IsNull(backend.NextSession.Request);
        }

        [TestMethod]
        public async Task Resume_VerifiesAndOpensTheExistingSessionWithoutSendingOrCreatingAsync()
        {
            var backend = new FakeBackend();
            var session = new FakeSession();
            backend.Sessions.Replace(session);
            var navigator = new FakeNavigator();
            Assert.IsTrue(await CopilotChatLauncher.TryResumeAsync(new FakeBackends(backend), navigator,
                session.SessionId.Guid.ToString("D"), CancellationToken.None));
            Assert.AreSame(session, navigator.OpenedSession);
            Assert.IsTrue(navigator.UserInitiated);
            Assert.IsEmpty(backend.Created);
            Assert.IsNull(session.Request);
            Assert.AreEqual(0, session.DisposeCount);
            Assert.AreEqual(0, session.Title.SubscriberCount);
        }

        [TestMethod]
        public async Task Resume_MissingAndCancelledSessionsNeverCreateOrSendAsync()
        {
            var backend = new FakeBackend();
            var navigator = new FakeNavigator();
            Assert.IsFalse(await CopilotChatLauncher.TryResumeAsync(new FakeBackends(backend), navigator,
                Guid.NewGuid().ToString("D"), CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => CopilotChatLauncher.TryResumeAsync(
                new FakeBackends(backend), navigator, Guid.NewGuid().ToString("D"), cancellation.Token));
            Assert.IsNull(navigator.OpenedSession);
            Assert.IsEmpty(backend.Created);
        }

        [TestMethod]
        public async Task StartAsync_IncompatibleNavigationIsRejectedBeforeCreatingAChatAsync()
        {
            var backend = new FakeBackend();
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
                CopilotChatLauncher.StartAsync(new FakeBackends(backend), new object(), "Fix issue", CancellationToken.None));
            Assert.IsEmpty(backend.Created);
        }

        [TestMethod]
        public async Task StartAsync_DoesNotSendToAnUnreadyChatAsync()
        {
            var backend = new FakeBackend();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                CopilotChatLauncher.StartAsync(new FakeBackends(backend), new FakeNavigator { IsReady = false }, "Fix issue", CancellationToken.None));
            Assert.IsNull(backend.NextSession.Request);
        }

        [TestMethod]
        [DataRow("Error")]
        [DataRow("Cancelled")]
        public async Task StartAsync_UnsuccessfulResponsesReachTheUiErrorHandlerAsync(string status)
        {
            var backend = new FakeBackend();
            backend.NextSession.RespondAsync = _ => Task.FromResult(new FakeChatResponse
            {
                Status = status, Error = new FakeChatError { Message = "Sign in required" },
            });
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                CopilotChatLauncher.StartAsync(new FakeBackends(backend), new FakeNavigator(), "Fix issue", CancellationToken.None));
            StringAssert.Contains(error.Message, "Sign in required");
        }

        [TestMethod]
        public async Task StartAsync_PropagatesSendFailuresAsync()
        {
            var failure = new InvalidOperationException("Backend failed");
            var backend = new FakeBackend();
            backend.NextSession.RespondAsync = _ => Task.FromException<FakeChatResponse>(failure);
            var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                CopilotChatLauncher.StartAsync(new FakeBackends(backend), new FakeNavigator(), "Fix issue", CancellationToken.None));
            Assert.AreSame(failure, actual);
        }

        [TestMethod]
        public async Task StartAsync_CancelledRequestsNeverCreateAChatAsync()
        {
            var backend = new FakeBackend();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                CopilotChatLauncher.StartAsync(new FakeBackends(backend), new FakeNavigator(), "Fix issue", cancellation.Token));
            Assert.IsEmpty(backend.Created);
        }
    }
}