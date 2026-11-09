using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;

namespace StartScreen.Services
{
    internal sealed partial class EventService
    {
        internal async Task<int> LoadThumbnailsAsync(IReadOnlyList<ConferenceEvent> events, CancellationToken token)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            using var concurrency = new SemaphoreSlim(2);
            var failures = 0;
            await Task.WhenAll(events.Where(item => item.Thumbnail == null).GroupBy(item => item.ThumbnailUrl).Select(async group =>
            {
                var item = group.First();
                await concurrency.WaitAsync(token);
                try
                {
                    var key = ThumbnailKey(item.ThumbnailUrl);
                    var path = Path.Combine(_cacheFolder, "thumbnails", key + ".json");
                    ThumbnailSnapshot cached = null;
                    try
                    {
                        cached = await AtomicJsonCache.ReadAsync<ThumbnailSnapshot>(path, token);
                        if (cached?.Bytes != null && IsFresh(cached.FetchedAtUtc, _utcNow()))
                        {
                            var thumbnail = Decode(cached.Bytes);
                            token.ThrowIfCancellationRequested();
                            foreach (var entry in group)
                                entry.Thumbnail = thumbnail;
                            return;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is System.Text.Json.JsonException || ex is UnauthorizedAccessException
                        || ex is FileFormatException || ex is NotSupportedException || ex is ArgumentException)
                    {
                        await ex.LogAsync();
                    }
                    using var response = await _client.GetAsync(item.ThumbnailUrl, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    var bytes = await HttpResponseReader.ReadBytesAsync(response, token, 2 * 1024 * 1024);
                    var image = Decode(bytes);
                    try
                    {
                        await AtomicJsonCache.WriteAsync(path, new ThumbnailSnapshot { FetchedAtUtc = _utcNow(), Bytes = bytes }, token);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        await ex.LogAsync();
                        Interlocked.Increment(ref failures);
                    }
                    token.ThrowIfCancellationRequested();
                    foreach (var entry in group)
                        entry.Thumbnail = image;
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref failures);
                }
                finally
                {
                    concurrency.Release();
                }
            }));
            return failures;
        }

        private static string ThumbnailKey(string url)
        {
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", string.Empty);
        }

        private static BitmapImage Decode(byte[] bytes)
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes, writable: false);
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 200;
            image.EndInit();
            image.Freeze();
            return image;
        }

        internal sealed class ThumbnailSnapshot
        {
            public DateTimeOffset FetchedAtUtc { get; set; }
            public byte[] Bytes { get; set; }
        }
    }
}
