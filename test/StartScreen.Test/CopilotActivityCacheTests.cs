using System.Text.Json;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class CopilotActivityCacheTests
    {
        [TestMethod]
        public async Task DiskCache_RestoresMetadataIntoANewCacheWithoutLiveStateAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("copilot.json");
            var session = new CopilotActivitySession
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Original conversation\nwith a full title",
                WorkingDirectory = @"C:\Repos\Example",
                LastActivity = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero),
                IsTranscript = true,
                IsResponding = true,
                PendingPermissions = 2,
                PendingQuestions = 1,
            };

            await CopilotActivityCache.WriteAsync(path, [session], CancellationToken.None);
            var restored = new CopilotActivityCache();
            restored.Update(await CopilotActivityCache.ReadAsync(path, CancellationToken.None), false);

            Assert.HasCount(1, restored.Sessions);
            var actual = restored.Sessions[0];
            Assert.AreEqual(session.Id, actual.Id);
            Assert.AreEqual(session.Title, actual.Title);
            Assert.AreEqual(session.WorkingDirectory, actual.WorkingDirectory);
            Assert.AreEqual(session.LastActivity, actual.LastActivity);
            Assert.IsTrue(actual.IsTranscript);
            Assert.IsTrue(actual.IsCached);
            Assert.IsNull(actual.IsResponding);
            Assert.AreEqual(0, actual.PendingPermissions);
            Assert.AreEqual(0, actual.PendingQuestions);
            Assert.IsFalse(actual.IsRunning);
            Assert.IsFalse(actual.NeedsAttention);
            Assert.AreEqual(string.Empty, actual.StatusBadge);

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            CollectionAssert.AreEquivalent(
                new[] { "Id", "Title", "WorkingDirectory", "LastActivity", "IsTranscript" },
                json.RootElement[0].EnumerateObject().Select(property => property.Name).ToArray());
        }

        [TestMethod]
        public async Task DiskCache_EmptyCompletedDiscoveryRemovesPersistedHistoryAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("copilot.json");
            await CopilotActivityCache.WriteAsync(path,
                [new CopilotActivitySession { Id = "old", Title = "Deleted conversation" }], CancellationToken.None);
            var cache = new CopilotActivityCache();
            cache.Update(await CopilotActivityCache.ReadAsync(path, CancellationToken.None), false);
            Assert.HasCount(1, cache.Sessions);

            cache.Update(Array.Empty<CopilotActivitySession>(), true);
            await CopilotActivityCache.WriteAsync(path, cache.Sessions, CancellationToken.None);

            Assert.HasCount(0, await CopilotActivityCache.ReadAsync(path, CancellationToken.None));
            Assert.HasCount(1, Directory.GetFiles(directory.Path), "Atomic replacement must not leave temporary files.");
        }

        [TestMethod]
        public async Task DiskCache_PersistsAtMost100SessionsAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("copilot.json");
            var sessions = Enumerable.Range(0, 130).Select(index => new CopilotActivitySession
            {
                Id = index.ToString(),
                Title = "Conversation " + index,
            }).ToArray();

            await CopilotActivityCache.WriteAsync(path, sessions, CancellationToken.None);
            var restored = await CopilotActivityCache.ReadAsync(path, CancellationToken.None);

            Assert.HasCount(100, restored);
            CollectionAssert.AreEqual(sessions.Take(100).Select(session => session.Id).ToArray(),
                restored.Select(session => session.Id).ToArray());
        }

        [TestMethod]
        public async Task DiskCache_MissingFileIsAnEmptyCacheAsync()
        {
            using var directory = new TemporaryDirectory();
            Assert.HasCount(0, await CopilotActivityCache.ReadAsync(directory.GetPath("missing.json"), CancellationToken.None));
        }

        [TestMethod]
        public async Task DiskCache_MalformedJsonReportsFailureAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("copilot.json");
            File.WriteAllText(path, "not json");

            await Assert.ThrowsAsync<JsonException>(() => CopilotActivityCache.ReadAsync(path, CancellationToken.None));
        }

        [TestMethod]
        [DataRow("null")]
        [DataRow("[null]")]
        [DataRow("[{}]")]
        public async Task DiskCache_InvalidMetadataReportsFailureAsync(string json)
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("copilot.json");
            File.WriteAllText(path, json);

            await Assert.ThrowsAsync<InvalidDataException>(() => CopilotActivityCache.ReadAsync(path, CancellationToken.None));
        }

        [TestMethod]
        public async Task DiskCache_OversizedFileReportsFailureAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("copilot.json");
            using (var stream = File.Create(path))
                stream.SetLength(4 * 1024 * 1024 + 1);

            await Assert.ThrowsAsync<InvalidDataException>(() => CopilotActivityCache.ReadAsync(path, CancellationToken.None));
        }

        [TestMethod]
        public async Task DiskCache_ConcurrentReplacementRemainsReadableAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("copilot.json");
            await CopilotActivityCache.WriteAsync(path,
                [new CopilotActivitySession { Id = "original" }], CancellationToken.None);

            await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
            {
                await CopilotActivityCache.WriteAsync(path,
                    [new CopilotActivitySession { Id = index.ToString(), Title = new string('x', 100000) }], CancellationToken.None);
                var restored = await CopilotActivityCache.ReadAsync(path, CancellationToken.None);
                Assert.HasCount(1, restored);
                Assert.AreEqual(100000, restored[0].Title.Length);
                Assert.IsTrue(restored[0].IsCached);
            }));

            Assert.HasCount(1, Directory.GetFiles(directory.Path));
        }

        [TestMethod]
        public async Task DiskCache_OversizedWritePreservesExistingCacheAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("copilot.json");
            await CopilotActivityCache.WriteAsync(path,
                [new CopilotActivitySession { Id = "original" }], CancellationToken.None);

            await Assert.ThrowsAsync<InvalidDataException>(() => CopilotActivityCache.WriteAsync(path,
                [new CopilotActivitySession { Id = "oversized", Title = new string('x', 4 * 1024 * 1024) }], CancellationToken.None));

            var restored = await CopilotActivityCache.ReadAsync(path, CancellationToken.None);
            Assert.HasCount(1, restored);
            Assert.AreEqual("original", restored[0].Id);
            Assert.HasCount(1, Directory.GetFiles(directory.Path));
        }

        [TestMethod]
        public async Task DiskCache_CanceledWritePreservesExistingCacheAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("copilot.json");
            await CopilotActivityCache.WriteAsync(path,
                [new CopilotActivitySession { Id = "original" }], CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => CopilotActivityCache.WriteAsync(path,
                Array.Empty<CopilotActivitySession>(), cancellation.Token));

            var restored = await CopilotActivityCache.ReadAsync(path, CancellationToken.None);
            Assert.HasCount(1, restored);
            Assert.AreEqual("original", restored[0].Id);
            Assert.HasCount(1, Directory.GetFiles(directory.Path));
        }
    }
}