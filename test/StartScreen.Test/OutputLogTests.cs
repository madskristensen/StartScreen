using System.IO;
using System.Linq.Expressions;
using StartScreen.Models;

namespace StartScreen.Test
{
    [TestClass]
    public class OutputLogTests
    {
        [TestMethod]
        public void ExceptionLogging_ResolvesToTheToolkitInsteadOfTheStatusPane()
        {
            Expression<Func<Exception, Task>> log = exception => exception.LogAsync();
            var method = ((MethodCallExpression)log.Body).Method;

            Assert.AreEqual("System.ExceptionExtensions", method.DeclaringType!.FullName);
            Assert.AreEqual("Community.VisualStudio.Toolkit", method.DeclaringType.Assembly.GetName().Name);
        }

        [TestMethod]
        [DataRow(false, false, false, false, "Activity updated")]
        [DataRow(true, false, false, false, "Activity updated (cached content)")]
        [DataRow(false, true, false, false, "Activity updated (may be out of date)")]
        [DataRow(false, false, true, false, "Activity updated (some sources unavailable)")]
        [DataRow(false, false, false, true, "Activity updated (unavailable)")]
        [DataRow(true, true, true, true, "Activity updated (cached content, may be out of date, some sources unavailable, unavailable)")]
        public void RefreshMessages_DescribeOnlyRelevantStates(bool cached, bool stale, bool partial, bool unavailable, string expected)
        {
            var state = new SectionRefreshState { IsCached = cached, IsStale = stale, IsPartial = partial, IsUnavailable = unavailable };

            Assert.AreEqual(expected, OutputLog.FormatRefreshMessage("Activity updated", state));
        }

        [TestMethod]
        public async Task RepeatedStatuses_AreSuppressedPerSourceWithoutHidingTransitionsOrDiagnosticsAsync()
        {
            var log = new OutputLogBuffer();
            var lines = new List<string>();
            Task initial = log.WriteAsync("Activity", "Refreshing", suppressRepeated: true);
            Assert.AreEqual(TaskStatus.RanToCompletion, initial.Status);
            await initial;
            await log.WriteAsync("Activity", "Refreshing", suppressRepeated: true);
            await log.WriteAsync("Feedback", "Refreshing", suppressRepeated: true);
            await log.InitializeAsync(line => { lines.Add(line); return Task.CompletedTask; });
            await log.WriteAsync("Activity", "Source finished");
            await log.WriteAsync("Activity", "Refreshing", suppressRepeated: true);
            await log.WriteAsync("Activity", "Updated", suppressRepeated: true);
            await log.WriteAsync("Activity", "Updated", suppressRepeated: true);
            await log.WriteAsync("Activity", "Refreshing", suppressRepeated: true);
            await log.WriteAsync("Activity", "Source finished");

            Assert.HasCount(6, lines);
            StringAssert.Contains(lines[0], "[Activity] Refreshing");
            StringAssert.Contains(lines[1], "[Feedback] Refreshing");
            StringAssert.Contains(lines[2], "[Activity] Source finished");
            StringAssert.Contains(lines[3], "[Activity] Updated");
            StringAssert.Contains(lines[4], "[Activity] Refreshing");
            StringAssert.Contains(lines[5], "[Activity] Source finished");
        }

        [TestMethod]
        public async Task ConcurrentIdenticalStatuses_AreWrittenOnceAsync()
        {
            var log = new OutputLogBuffer();
            var lines = new List<string>();
            await log.InitializeAsync(line => { lines.Add(line); return Task.CompletedTask; });

            await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => log.WriteAsync("Activity", "Updated", suppressRepeated: true))));

            Assert.HasCount(1, lines);
            StringAssert.Contains(lines[0], "[Activity] Updated");
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