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