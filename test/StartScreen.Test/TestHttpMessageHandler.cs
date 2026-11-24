using System.Net.Http;
using System.Threading;

namespace StartScreen.Test
{
    internal sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responseFactory;
        private int _requestCount;
        private HttpRequestMessage? _lastRequest;

        public TestHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public HttpRequestMessage? LastRequest => Volatile.Read(ref _lastRequest);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestCount = Interlocked.Increment(ref _requestCount);
            Volatile.Write(ref _lastRequest, request);
            return Task.FromResult(_responseFactory(request, requestCount));
        }
    }
}
