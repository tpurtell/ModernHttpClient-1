using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using OkHttp;
using Java.Net;
using Java.IO;

namespace ModernHttpClient
{
    public class OkHttpNetworkHandler : HttpMessageHandler
    {
        static readonly object xamarinLock = new object();
        readonly OkHttpClient client = new OkHttpClient();
        readonly bool throwOnCaptiveNetwork;

        public OkHttpNetworkHandler() : this(false) {}

        public void CloseConnections() {
            ConnectionPool.Default.EvictAll();
        }

        public OkHttpNetworkHandler(bool throwOnCaptiveNetwork)
        {
            this.throwOnCaptiveNetwork = throwOnCaptiveNetwork;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try {
                return await InternalSendAsync(request, cancellationToken);
            } catch(Exception e) {
                JavaExceptionMapper(e);
                throw e;
            }
        }
        private void JavaExceptionMapper(Exception e)
        {
            if (e is Java.Net.UnknownHostException)
                throw new WebException("Name resolution failure", e, WebExceptionStatus.NameResolutionFailure, null);
            if (e is Java.IO.IOException) {
                var msg = e.ToString();
                if(msg.Contains("Hostname") && msg.Contains("was not verified"))
                    CloseConnections();
                throw new WebException("IO Exception", e, WebExceptionStatus.ConnectFailure, null);
            }
        }

        static void CopyHeaders (HttpResponseMessage ret, HttpURLConnection rq)
        {
            var headers = Xamarin.Bug.MapToArray.GetHeaderFields (rq);
            foreach (var e in headers) {
                ret.Headers.TryAddWithoutValidation (e.Key, e.Value);
                ret.Content.Headers.TryAddWithoutValidation (e.Key, e.Value);
            }
        }

        protected async Task<HttpResponseMessage> InternalSendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var java_uri = request.RequestUri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
            var url = new Java.Net.URL(java_uri);
            var rq = client.Open(url);
            rq.RequestMethod = request.Method.Method.ToUpperInvariant();

            foreach (var kvp in request.Headers) { rq.SetRequestProperty(kvp.Key, kvp.Value.FirstOrDefault()); }

            if (request.Content != null) {
                foreach (var kvp in request.Content.Headers) { rq.SetRequestProperty (kvp.Key, kvp.Value.FirstOrDefault ()); }

                await Task.Run(async () => {
                    var contentStream = await request.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await copyToAsync(contentStream, rq.OutputStream, cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);

                rq.OutputStream.Close();
            }

            return await Task.Run<HttpResponseMessage> (() => {
                try {
                    // NB: This is the line that blocks until we have headers
                    var ret = new HttpResponseMessage((HttpStatusCode)rq.ResponseCode);
                    // Test to see if we're being redirected (i.e. in a captive network)
                    if (throwOnCaptiveNetwork && (url.Host != rq.URL.Host)) {
                        throw new WebException("Hostnames don't match, you are probably on a captive network");
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    ret.Content = new StreamContent(new ConcatenatingStream(new Func<Stream>[] {
                        () => {
                            try {
                                return rq.InputStream;
                            } catch(Java.IO.FileNotFoundException e) {
                                return new MemoryStream();
                            }
                        },
                        () => rq.ErrorStream ?? new MemoryStream (),
                    }, true, JavaExceptionMapper));

                    //the implicit handling of Java.Lang.String => string conversion or iterators are very broken
                    CopyHeaders (ret, rq);
                    cancellationToken.Register (ret.Content.Dispose);

                    ret.RequestMessage = request;
                    return ret;
                } catch(Exception e) {
                    JavaExceptionMapper(e);
                    throw e;
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        async Task copyToAsync(Stream source, Stream target, CancellationToken ct)
        {
            await Task.Run(async () => {
                var buf = new byte[4096];
                var read = 0;

                do {
                    read = await source.ReadAsync(buf, 0, 4096).ConfigureAwait(false);

                    if (read > 0) {
                        target.Write(buf, 0, read);
                    }
                } while (!ct.IsCancellationRequested && read > 0);

                ct.ThrowIfCancellationRequested();
            }, ct).ConfigureAwait(false);
        }
    }
}
