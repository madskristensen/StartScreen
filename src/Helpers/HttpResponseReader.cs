using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StartScreen.Helpers
{
    /// <summary>
    /// Bounds body reads separately from HttpClient's headers-only request timeout.
    /// </summary>
    internal static class HttpResponseReader
    {
        internal static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, CancellationToken cancellationToken,
            int maxBytes = 8 * 1024 * 1024, TimeSpan? timeout = null)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));
            if (maxBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            var duration = timeout ?? TimeSpan.FromSeconds(30);
            if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            using var deadline = new CancellationTokenSource(duration);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var interrupted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = linked.Token.Register(() =>
            {
                // Stream.ReadAsync on .NET Framework can ignore cancellation until the socket closes.
                try
                {
                    response.Dispose();
                }
                catch (Exception ex)
                {
                    _ = ex.LogAsync();
                }
                finally
                {
                    interrupted.TrySetResult(true);
                }
            });

            Task<byte[]> read = null;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                if (response.Content?.Headers.ContentLength > maxBytes)
                    throw new HttpRequestException("The response body exceeded the configured size limit.");
                read = ReadCoreAsync(response, linked.Token, maxBytes);
                await Task.WhenAny(read, interrupted.Task);
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                    throw new TimeoutException("The response body read timed out.");
                return await read;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                response.Dispose();
                if (read != null)
                    _ = ObserveAsync(read);
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                    throw new TimeoutException("The response body read timed out.", ex);
                throw;
            }
        }

        internal static async Task<string> ReadStringAsync(HttpResponseMessage response, CancellationToken cancellationToken,
            int maxBytes = 8 * 1024 * 1024, TimeSpan? timeout = null)
        {
            var charset = response?.Content?.Headers.ContentType?.CharSet;
            var bytes = await ReadBytesAsync(response, cancellationToken, maxBytes, timeout);
            var encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'));
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: string.IsNullOrWhiteSpace(charset));
            cancellationToken.ThrowIfCancellationRequested();
            return await reader.ReadToEndAsync();
        }

        private static async Task<byte[]> ReadCoreAsync(HttpResponseMessage response, CancellationToken cancellationToken, int maxBytes)
        {
            if (response.Content == null)
                return Array.Empty<byte>();
            using var stream = await response.Content.ReadAsStreamAsync();
            cancellationToken.ThrowIfCancellationRequested();
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                // Read one extra byte to reject oversized bodies even without Content-Length.
                var count = (int)Math.Min(buffer.Length, (long)maxBytes - output.Length + 1);
                var read = await stream.ReadAsync(buffer, 0, count, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0)
                    return output.ToArray();
                if (output.Length + read > maxBytes)
                    throw new HttpRequestException("The response body exceeded the configured size limit.");
                await output.WriteAsync(buffer, 0, read, cancellationToken);
            }
        }

        private static Task ObserveAsync(Task<byte[]> read)
        {
            return read.ContinueWith(completed => completed.IsFaulted
                ? completed.Exception.LogAsync() : Task.CompletedTask, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        }
    }
}