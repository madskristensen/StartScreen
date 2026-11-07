using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;

namespace StartScreen.Services.DevHub
{
    internal sealed class DevHubReadRequestSharing
    {
        internal static DevHubReadRequestSharing Instance { get; } = new DevHubReadRequestSharing(() => StartScreenPackage.ShutdownToken);
        private readonly ConditionalWeakTable<HttpClient, Transport> _transports = new ConditionalWeakTable<HttpClient, Transport>();
        private readonly Func<CancellationToken> _getShutdownToken;

        internal DevHubReadRequestSharing(Func<CancellationToken> getShutdownToken = null)
        {
            _getShutdownToken = getShutdownToken ?? (() => CancellationToken.None);
        }

        internal async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request,
            DevHubCredential credential, CancellationToken cancellationToken, bool readOnlyPost = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationToken shutdown = _getShutdownToken();
            shutdown.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get && !(request.Method == HttpMethod.Post && readOnlyPost))
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            byte[] body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (body?.Length > 1024 * 1024)
                throw new HttpRequestException("The read-only request body exceeded the configured size limit.");
            var headers = Capture(request.Headers);
            var contentHeaders = request.Content == null ? null : Capture(request.Content.Headers);
            HttpMethod method = request.Method;
            Uri uri = request.RequestUri;
            Version version = request.Version;
            using var hash = SHA256.Create();
            var fingerprint = string.Concat(new[]
            {
                method.Method, uri.AbsoluteUri, version.ToString(), Format(headers),
                Format(Capture(client.DefaultRequestHeaders)), Format(contentHeaders),
                credential.Username ?? string.Empty, credential.Token ?? string.Empty, credential.IsBearer.ToString(),
                credential.ExpiresOn.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                body == null ? "absent" : Convert.ToBase64String(hash.ComputeHash(body))
            }.Select(value => value.Length + ":" + value));
            var key = DevHubMetadataCache.GetKey(credential, Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(fingerprint))));
            Transport transport = _transports.GetValue(client, _ => new Transport());
            Entry entry;
            lock (transport.Gate)
            {
                if (!transport.Pending.TryGetValue(key, out entry))
                {
                    entry = new Entry(shutdown);
                    Entry current = entry;
                    entry.Task = Task.Run(async () =>
                    {
                        try
                        {
                            using var copy = new HttpRequestMessage(method, uri) { Version = version };
                            using var cancellation = current.Cancellation.Token.Register(() =>
                            {
                                try
                                {
                                    copy.Dispose();
                                }
                                catch (Exception ex)
                                {
                                    _ = ex.LogAsync();
                                }
                            });
                            foreach (var header in headers)
                                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            if (body != null)
                            {
                                copy.Content = new ByteArrayContent(body);
                                foreach (var header in contentHeaders)
                                    copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            }
                            using var response = await client.SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, current.Cancellation.Token);
                            var bytes = await HttpResponseReader.ReadBytesAsync(response, current.Cancellation.Token);
                            return new BufferedResponse(response, bytes);
                        }
                        catch (Exception ex)
                        {
                            await ex.LogAsync();
                            throw;
                        }
                        finally
                        {
                            lock (transport.Gate)
                            {
                                if (transport.Pending.TryGetValue(key, out Entry pending) && ReferenceEquals(pending, current))
                                    transport.Pending.Remove(key);
                            }
                        }
                    });
                    // Bound coalescing bookkeeping; overflow requests still have independent ownership and deadlines.
                    if (transport.Pending.Count < 256)
                        transport.Pending.Add(key, entry);
                }
                entry.Waiters++;
            }

            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown);
                BufferedResponse response = await entry.Task.WithCancellation(wait.Token);
                cancellationToken.ThrowIfCancellationRequested();
                shutdown.ThrowIfCancellationRequested();
                return response.CreateResponse(request);
            }
            finally
            {
                var release = false;
                lock (transport.Gate)
                {
                    if (--entry.Waiters == 0)
                    {
                        release = true;
                        if (transport.Pending.TryGetValue(key, out Entry pending) && ReferenceEquals(pending, entry))
                            transport.Pending.Remove(key);
                    }
                }
                if (release)
                {
                    if (!entry.Task.IsCompleted)
                        entry.Cancellation.Cancel();
                    _ = DisposeWhenCompletedAsync(entry);
                }
            }
        }

        private static async Task DisposeWhenCompletedAsync(Entry entry)
        {
            try
            {
                await entry.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
            finally
            {
                entry.Cancellation.Dispose();
            }
        }

        private static Dictionary<string, string[]> Capture(System.Net.Http.Headers.HttpHeaders headers) =>
            headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

        private static string Format(Dictionary<string, string[]> headers) => headers == null ? string.Empty
            : string.Concat(headers.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                .Select(header => header.Key.ToLowerInvariant().Length + ":" + header.Key.ToLowerInvariant()
                    + header.Value.Length + ":" + string.Concat(header.Value.Select(value => value.Length + ":" + value))));

        private sealed class Transport
        {
            internal object Gate { get; } = new object();
            internal Dictionary<string, Entry> Pending { get; } = new Dictionary<string, Entry>();
        }

        private sealed class Entry
        {
            internal Entry(CancellationToken shutdown)
            {
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                Cancellation.CancelAfter(TimeSpan.FromSeconds(60));
            }

            internal CancellationTokenSource Cancellation { get; }
            internal Task<BufferedResponse> Task { get; set; }
            internal int Waiters { get; set; }
        }

        private sealed class BufferedResponse
        {
            private readonly HttpStatusCode _status;
            private readonly string _reason;
            private readonly Version _version;
            private readonly Dictionary<string, string[]> _headers;
            private readonly Dictionary<string, string[]> _contentHeaders;
            private readonly byte[] _bytes;

            internal BufferedResponse(HttpResponseMessage response, byte[] bytes)
            {
                _status = response.StatusCode;
                _reason = response.ReasonPhrase;
                _version = response.Version;
                _headers = Capture(response.Headers);
                _contentHeaders = response.Content == null ? null : Capture(response.Content.Headers);
                _bytes = bytes;
            }

            internal HttpResponseMessage CreateResponse(HttpRequestMessage request)
            {
                var response = new HttpResponseMessage(_status) { ReasonPhrase = _reason, Version = _version, RequestMessage = request };
                foreach (var header in _headers)
                    response.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (_contentHeaders != null)
                {
                    response.Content = new ByteArrayContent((byte[])_bytes.Clone());
                    foreach (var header in _contentHeaders)
                        response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                return response;
            }
        }
    }
}
