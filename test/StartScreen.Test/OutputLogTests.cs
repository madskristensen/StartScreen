using System.IO;

namespace StartScreen.Test
{
    [TestClass]
    public class OutputLogTests
    {
        [TestMethod]
        public async Task DiagnosticsBeforeInitialization_DoNotRequireVisualStudioAsync()
        {
            OutputLog.Info("Test", "Logging before the Output pane exists.");
            Task operation = new InvalidOperationException("Handled test failure.").LogAsync();

            Assert.AreEqual(TaskStatus.RanToCompletion, operation.Status,
                "Diagnostics must buffer without waiting for Visual Studio services.");
            await operation;
        }

        [TestMethod]
        public async Task MessagesBeforeInitialization_AreFlushedOnceInOrderAsync()
        {
            var log = new OutputLogBuffer();
            var lines = new List<string>();
            await log.WriteAsync("Feedback", "Using disk cache.");
            await log.WriteAsync("GitHub", "SAML restriction.");

            await log.InitializeAsync(line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            });
            await log.WriteAsync("News", "Updated.");

            Assert.HasCount(3, lines);
            StringAssert.Contains(lines[0], "[Feedback] Using disk cache.");
            StringAssert.Contains(lines[1], "[GitHub] SAML restriction.");
            StringAssert.Contains(lines[2], "[News] Updated.");
            StringAssert.Matches(lines[0], new System.Text.RegularExpressions.Regex(@"^\[\d{2}:\d{2}:\d{2}\] "));
        }

        [TestMethod]
        public async Task ConcurrentWrites_AreSerializedWithoutLosingMessagesAsync()
        {
            var log = new OutputLogBuffer();
            var lines = new List<string>();
            var active = 0;
            await log.InitializeAsync(async line =>
            {
                Assert.AreEqual(1, Interlocked.Increment(ref active));
                try
                {
                    await Task.Yield();
                    lines.Add(line);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

            await Task.WhenAll(Enumerable.Range(0, 64).Select(index => log.WriteAsync("Refresh", $"Message {index}.")));

            Assert.HasCount(64, lines);
            for (var index = 0; index < lines.Count; index++)
                StringAssert.Contains(lines[index], $"[Refresh] Message {index}.");
            Assert.AreEqual(0, active);
        }

        [TestMethod]
        public async Task FailedWrite_PreservesTheMessageForRetryAsync()
        {
            var log = new OutputLogBuffer();
            await log.InitializeAsync(_ => Task.FromException(new IOException("Test output failure")));
            await Assert.ThrowsAsync<IOException>(() => log.WriteAsync("Feedback", "Cache failed."));
            var lines = new List<string>();

            await log.InitializeAsync(line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            });

            Assert.HasCount(1, lines);
            StringAssert.Contains(lines.Single(), "[Feedback] Cache failed.");
        }
    }
}