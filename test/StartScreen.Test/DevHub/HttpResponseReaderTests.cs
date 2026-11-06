using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using StartScreen.Helpers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class HttpResponseReaderTests
    {
        [TestMethod]
        public async Task CancellationDisposesStreamEvenWhenReadIgnoresTokenAsync()
        {
            using var stream = new BlockingStream();
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            using var cancellation = new CancellationTokenSource();
            var assertion = Assert.ThrowsAsync<OperationCanceledException>(() => HttpResponseReader.ReadBytesAsync(response, cancellation.Token));
            await MustCompleteAsync(stream.ReadStarted.Task);

            cancellation.Cancel();

            await MustCompleteAsync(assertion);
            var exception = await assertion;
            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.IsTrue(stream.IsDisposed);
            Assert.IsTrue(stream.PendingRead.Task.IsFaulted, "Disposal must abort the underlying read, not only abandon the waiter.");
        }

        [TestMethod]
        public async Task BodyDeadlineDisposesStalledStreamAndNeverReturnsEmptySuccessAsync()
        {
            using var stream = new BlockingStream();
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            var assertion = Assert.ThrowsAsync<TimeoutException>(() => HttpResponseReader
                .ReadStringAsync(response, CancellationToken.None, timeout: TimeSpan.FromMilliseconds(200)));

            await MustCompleteAsync(assertion);

            await assertion;
            Assert.IsTrue(stream.IsDisposed);
            Assert.IsTrue(stream.PendingRead.Task.IsFaulted);
        }

        [TestMethod]
        public async Task CancellationDuringStreamAcquisitionDisposesResponseAndLateStreamAsync()
        {
            using var content = new DelayedStreamContent();
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            using var cancellation = new CancellationTokenSource();
            var assertion = Assert.ThrowsAsync<OperationCanceledException>(() => HttpResponseReader.ReadBytesAsync(response, cancellation.Token));
            await MustCompleteAsync(content.Started.Task);
            cancellation.Cancel();
            await MustCompleteAsync(assertion);
            await assertion;
            Assert.IsTrue(content.IsDisposed);

            using var lateStream = new BlockingStream();
            content.Stream.SetResult(lateStream);
            await MustCompleteAsync(lateStream.Disposed.Task);
            Assert.IsTrue(lateStream.IsDisposed, "An acquisition completing after cancellation must not leak its stream.");
        }

        [TestMethod]
        public async Task AdvertisedOversizeIsRejectedBeforeReadingAndDisposesContentAsync()
        {
            using var stream = new BlockingStream();
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentLength = 100;

            await Assert.ThrowsAsync<HttpRequestException>(() => HttpResponseReader.ReadBytesAsync(response, CancellationToken.None, 10));

            Assert.IsTrue(stream.IsDisposed);
            Assert.IsFalse(stream.ReadStarted.Task.IsCompleted);
        }

        [TestMethod]
        public async Task TransportBodyFailureIsPropagatedAndDisposedRatherThanReturnedAsEmptyAsync()
        {
            using var stream = new BlockingStream();
            stream.PendingRead.SetException(new IOException("The body transport failed."));
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };

            await Assert.ThrowsAsync<IOException>(() => HttpResponseReader.ReadBytesAsync(response, CancellationToken.None));

            Assert.IsTrue(stream.IsDisposed);
        }

        [TestMethod]
        public async Task UnadvertisedOversizeIsRejectedButExactLimitSucceedsAsync()
        {
            using var oversized = new NonSeekableStream([1, 2, 3, 4]);
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(oversized) };
            Assert.IsNull(response.Content.Headers.ContentLength);

            await Assert.ThrowsAsync<HttpRequestException>(() => HttpResponseReader.ReadBytesAsync(response, CancellationToken.None, 3));
            Assert.IsTrue(oversized.IsDisposed);

            using var exact = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await HttpResponseReader.ReadBytesAsync(exact, CancellationToken.None, 3));
        }

        [TestMethod]
        [DataRow("utf-16", false)]
        [DataRow("utf-32", false)]
        [DataRow("utf-8", true)]
        [DataRow("utf-16", true)]
        public async Task StringReadPreservesCharsetAndBomAsync(string charset, bool bomOnly)
        {
            var encoding = Encoding.GetEncoding(charset);
            const string text = "caf\u00e9 \u6771\u4eac";
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            if (!bomOnly)
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "\"" + charset + "\"" };

            Assert.AreEqual(text, await HttpResponseReader.ReadStringAsync(response, CancellationToken.None));
        }

        [TestMethod]
        public async Task AlreadyCanceledReadDisposesBodyAndPreservesCallerTokenAsync()
        {
            using var stream = new BlockingStream();
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => HttpResponseReader.ReadBytesAsync(response, cancellation.Token));

            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.IsTrue(stream.IsDisposed);
            Assert.IsFalse(stream.ReadStarted.Task.IsCompleted);
        }

        internal static async Task MustCompleteAsync(Task task)
        {
            var completion = task.ContinueWith(_ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Assert.AreSame(completion, await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(5))), "The operation must not hang.");
        }

        internal sealed class BlockingStream : Stream
        {
            internal TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<int> PendingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool IsDisposed { get; private set; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ReadStarted.TrySetResult(true);
                return PendingRead.Task;
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    IsDisposed = true;
                    if (ReadStarted.Task.IsCompleted)
                        PendingRead.TrySetException(new ObjectDisposedException(nameof(BlockingStream)));
                    else
                        PendingRead.TrySetCanceled();
                    Disposed.TrySetResult(true);
                }
                base.Dispose(disposing);
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
        {
            public override bool CanSeek => false;
            internal bool IsDisposed { get; private set; }
            protected override void Dispose(bool disposing)
            {
                IsDisposed |= disposing;
                base.Dispose(disposing);
            }
        }

        private sealed class DelayedStreamContent : HttpContent
        {
            internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<Stream> Stream { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool IsDisposed { get; private set; }
            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                Started.TrySetResult(true);
                return Stream.Task;
            }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
            protected override void Dispose(bool disposing)
            {
                IsDisposed |= disposing;
                base.Dispose(disposing);
            }
        }
    }
}