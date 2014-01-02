using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using AFNetworking;
using MonoTouch.Foundation;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using MonoTouch.UIKit;

namespace ModernHttpClient
{
    public class AFNetworkHandler : HttpMessageHandler
    {
        public AFHTTPClient SharedClient { get; private set; }

        public AFNetworkHandler(string baseUrl) {
            SharedClient = new AFHTTPClient(NSUrl.FromString(baseUrl));
            AFHTTPRequestOperation.AddAcceptableStatusCodes(NSIndexSet.FromNSRange(new NSRange(100, 599)));
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try {
                return await InternalSendAsync(request, cancellationToken);
            } catch(Exception e) {
                IosExceptionMapper(e);
                throw e;
            }
        }
        private void IosExceptionMapper(Exception e)
        {
            //just map everything to a temporary exception
            throw new WebException("IO Exception", e, WebExceptionStatus.ConnectFailure, null);
        }
        protected async Task<HttpResponseMessage> InternalSendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers as IEnumerable<KeyValuePair<string, IEnumerable<string>>>;
            var ms = new MemoryStream();

            if (request.Content != null) {
                await request.Content.CopyToAsync(ms).ConfigureAwait(false);
                headers = headers.Union(request.Content.Headers);
            }

            var rq = new NSMutableUrlRequest() {
                Body = NSData.FromArray(ms.ToArray()),
                CachePolicy = NSUrlRequestCachePolicy.UseProtocolCachePolicy,
                Headers = headers.Aggregate(new NSMutableDictionary(), (acc, x) => {
                    acc.Add(new NSString(x.Key), new NSString(x.Value.LastOrDefault()));
                    return acc;
                }),
                HttpMethod = request.Method.ToString().ToUpperInvariant(),
                Url = NSUrl.FromString(request.RequestUri.AbsoluteUri),
            };
            if(UIDevice.CurrentDevice.CheckSystemVersion (6, 0))
                rq.AllowsCellularAccess = true;
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            var operation = new AFHTTPRequestOperation(rq);
            AFHttpRequestSuccessCallback completion = (op, response) => {
                try {
                    var resp = (NSHttpUrlResponse)op.Response;
                    var msg = new HttpResponseMessage((HttpStatusCode)resp.StatusCode) {
                        Content = new StreamContent(ToMemoryStream(op.ResponseData)),
                        RequestMessage = request
                    };
                    foreach(var v in resp.AllHeaderFields) {
                        msg.Headers.TryAddWithoutValidation(v.Key.ToString(), v.Value.ToString());
                        msg.Content.Headers.TryAddWithoutValidation(v.Key.ToString(), v.Value.ToString());
                    }
                    tcs.SetResult(msg);
                } catch (Exception e) {
                    tcs.SetException(new WebException(string.Format("Strange request for {0}: {1}", request.RequestUri.AbsoluteUri, e)));
                }
            };
            AFHttpRequestFailureCallback failure = (op, error) => {
                try {
                    if(error.Domain == NSError.NSUrlErrorDomain)
                        tcs.SetException(new WebException (error.LocalizedDescription, WebExceptionStatus.NameResolutionFailure));
                    else
                        tcs.SetException(new WebException (error.LocalizedDescription, WebExceptionStatus.ConnectFailure));
                } catch (Exception e) {
                    tcs.SetException(new WebException(string.Format("Strange request for {0}: {1}", request.RequestUri.AbsoluteUri, e)));
                }
            };

            operation.SetCompletionBlockWithSuccess(completion, failure);

            SharedClient.EnqueueHTTPRequestOperation(operation);

            try {
                var http_response = await tcs.Task;
                return http_response;
            } catch (Exception e) {
                Console.WriteLine("failed response {0}", e);
                throw;
            } finally {
                operation.Dispose();
            }
        }
        static MemoryStream ToMemoryStream (NSData data)
        {
            if(data == null || data.Length == 0 || data.Bytes == IntPtr.Zero)
                return new MemoryStream();
            byte[] bytes = new byte[data.Length];

            System.Runtime.InteropServices.Marshal.Copy(data.Bytes, bytes, 0, Convert.ToInt32(data.Length));

            return new MemoryStream(bytes);
        }
    }
}
