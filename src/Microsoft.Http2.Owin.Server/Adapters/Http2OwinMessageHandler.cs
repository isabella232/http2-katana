﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Http2.Protocol;
using Microsoft.Http2.Protocol.Extensions;
using Microsoft.Http2.Protocol.Framing;
using Microsoft.Http2.Protocol.IO;
using Microsoft.Http2.Protocol.Utils;
using Microsoft.Owin;
using Org.Mentalis.Security.Ssl;

namespace Microsoft.Http2.Owin.Server.Adapters
{
    using AppFunc = Func<IDictionary<string, object>, Task>;

    /// <summary>
    /// This class overrides http2 request/response processing logic as owin requires
    /// </summary>
    public class Http2OwinMessageHandler : Http2MessageHandler
    {
        private readonly AppFunc _next;

        /// <summary>
        /// Initializes a new instance of the <see cref="Http2OwinMessageHandler"/> class.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="end"></param>
        /// <param name="transportInfo">The transport information.</param>
        /// <param name="next">The next layer delegate.</param>
        /// <param name="cancel">The cancellation token.</param>
        public Http2OwinMessageHandler(DuplexStream stream, ConnectionEnd end, TransportInformation transportInfo,
                                AppFunc next, CancellationToken cancel)
            : base(stream, end, stream.IsSecure, transportInfo, cancel)
        {
            _next = next;
            stream.OnClose += delegate { Dispose(); };
        }

        /// <summary>
        /// Adopts protocol terms into owin environment.
        /// </summary>
        /// <param name="headers">The headers.</param>
        /// <returns></returns>
        private IDictionary<string, object> PopulateEnvironment(HeadersList headers)
        {
            var owinContext = new OwinContext();

            var headersAsDict = headers.ToDictionary(header => header.Key, header => new[] {header.Value}, StringComparer.OrdinalIgnoreCase);

            owinContext.Environment["owin.RequestHeaders"] = headersAsDict;
            owinContext.Environment["owin.ResponseHeaders"] = new Dictionary<string, string[]>();

            var owinRequest = owinContext.Request;
            var owinResponse = owinContext.Response;

            owinRequest.Method = headers.GetValue(":method");
            owinRequest.Path = headers.GetValue(":path");
            owinRequest.CallCancelled = CancellationToken.None;

            owinRequest.Host = headers.GetValue(":host");
            owinRequest.PathBase = String.Empty;
            owinRequest.QueryString = String.Empty;
            owinRequest.Body = new MemoryStream();
            owinRequest.Protocol = Protocols.Http2;
            owinRequest.Scheme = headers.GetValue(":scheme") == Uri.UriSchemeHttp ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
            owinRequest.RemoteIpAddress = _transportInfo.RemoteIpAddress;
            owinRequest.RemotePort = Convert.ToInt32(_transportInfo.RemotePort);
            owinRequest.LocalIpAddress = _transportInfo.LocalIpAddress;
            owinRequest.LocalPort = _transportInfo.LocalPort;

            owinResponse.Body = new MemoryStream();

            return owinContext.Environment;
        }

        /// <summary>
        /// Overrides request processing logic.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="frame">The request header frame.</param>
        /// <returns></returns>
        protected override void ProcessRequest(Http2Stream stream, Frame frame)
        {
            //spec 06
            //8.1.2.1.  Request Header Fields
            //HTTP/2.0 defines a number of headers starting with a ':' character
            //that carry information about the request target:
 
            //o  The ":method" header field includes the HTTP method ([HTTP-p2],
            //      Section 4).
 
            //o  The ":scheme" header field includes the scheme portion of the
            //      target URI ([RFC3986], Section 3.1).
 
            //o  The ":host" header field includes the authority portion of the
            //      target URI ([RFC3986], Section 3.2).
 
            //o  The ":path" header field includes the path and query parts of the
                //target URI (the "path-absolute" production from [RFC3986] and
                 //optionally a '?' character followed by the "query" production, see
                 //[RFC3986], Section 3.3 and [RFC3986], Section 3.4).  This field
                 //MUST NOT be empty; URIs that do not contain a path component MUST
                 //include a value of '/', unless the request is an OPTIONS request
                 //for '*', in which case the ":path" header field MUST include '*'.
 
            //All HTTP/2.0 requests MUST include exactly one valid value for all of
            //these header fields.  An intermediary MUST ensure that requests that
            //it forwards are correct.  A server MUST treat the absence of any of
            //these header fields, presence of multiple values, or an invalid value
            //as a stream error (Section 5.4.2) of type PROTOCOL_ERROR.

            if (stream.Headers.GetValue(":method") == null
                || stream.Headers.GetValue(":path") == null
                || stream.Headers.GetValue(":scheme") == null
                || stream.Headers.GetValue(":host") == null)
            {
                stream.WriteRst(ResetStatusCode.ProtocolError);
                return;
            }

            Task.Factory.StartNew(async () =>
            {
                var env = PopulateEnvironment(stream.Headers);

                try
                {
                    await _next(env);
                }
                catch (Exception ex)
                {   
                    EndResponse(stream, ex);
                    return;
                }

                await EndResponse(stream, env);
            });
            
        }

        /// <summary>
        /// Overrides data processing logic.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="frame"></param>
        /// <returns></returns>
        protected override void ProcessIncomingData(Http2Stream stream, Frame frame)
        {
            //Do nothing... handling data is not supported by the server yet
        }

        /// <summary>
        /// Ends the response in case of error.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="ex">The catched exception.</param>
        private void EndResponse(Http2Stream stream, Exception ex)
        {
            WriteStatus(stream, StatusCode.Code500InternalServerError, false);
        }

        /// <summary>
        /// Ends the owin response.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="environment">The environment.</param>
        /// <returns></returns>
        private async Task EndResponse(Http2Stream stream, IDictionary<string, object> environment)
        {
            Stream responseBody = null;
            IDictionary<string, string[]> owinResponseHeaders = null;
            HeadersList responseHeaders = null;

            var response = new OwinResponse(environment);

            if (response.Body != null)
                responseBody = response.Body;

            if (response.Headers != null)
                owinResponseHeaders = response.Headers;

            var responseStatusCode = response.StatusCode;

            if (owinResponseHeaders != null)
                responseHeaders = new HeadersList(owinResponseHeaders);

            var hasDataContent = responseBody != null && responseBody.Position != 0;

            WriteStatus(stream, responseStatusCode, !hasDataContent, responseHeaders);

            //Memory stream contains all response data and can be read by one iteration.
            if (hasDataContent)
            {
                Http2Logger.LogDebug("Transfer begin");
                int contentLen = int.Parse(response.Headers["Content-Length"]);
                int read = 0;

                responseBody.Seek(0, SeekOrigin.Begin);
                while (read < contentLen)
                {
                    var temp = new byte[Constants.MaxFrameContentSize];
                    int tmpRead = await responseBody.ReadAsync(temp, 0, temp.Length);
                    
                    Debug.Assert(tmpRead > 0);
                    
                    var readBytes = new byte[tmpRead];
                    Buffer.BlockCopy(temp, 0, readBytes, 0, tmpRead);

                    read += tmpRead;
                    SendDataTo(stream, readBytes, read == contentLen);
                }

                Http2Logger.LogDebug("Transfer end");
            }
        }

        /// <summary>
        /// Writes the status header to the stream.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="statusCode">The status code.</param>
        /// <param name="final">if set to <c>true</c> then marks headers frame as final.</param>
        /// <param name="additionalHeaders">The additional headers.</param>
        private void WriteStatus(Http2Stream stream, int statusCode, bool final, HeadersList additionalHeaders = null)
        {
            var headers = new HeadersList
                {
                    new KeyValuePair<string, string>(":status", statusCode.ToString()),
                };

            if (additionalHeaders != null)
            {
                headers.AddRange(additionalHeaders);
            }

            stream.WriteHeadersFrame(headers, final, true);
        }

        /// <summary>
        /// Wraps data into data frames and sends it 
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="binaryData">The binary data.</param>
        /// <param name="isLastChunk">if set to <c>true</c> then marks last data frame as final.</param>
        private void SendDataTo(Http2Stream stream, byte[] binaryData, bool isLastChunk)
        {
            int i = 0;
            Debug.Assert(binaryData.Length <= Constants.MaxFrameContentSize);
            do
            {
                int chunkSize = MathEx.Min(binaryData.Length - i, Constants.MaxFrameContentSize);

                var chunk = new byte[chunkSize];
                Buffer.BlockCopy(binaryData, i, chunk, 0, chunk.Length);

                stream.WriteDataFrame(chunk, isLastChunk);

                i += chunkSize;
            } while (binaryData.Length > i);
        }
    }
}