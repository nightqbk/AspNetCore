// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3.QPack;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3
{
    internal abstract class Http3Stream : HttpProtocol, IHttpHeadersHandler, IThreadPoolWorkItem
    {
        private Http3FrameWriter _frameWriter;
        private Http3OutputProducer _http3Output;
        private int _isClosed;
        private int _gracefulCloseInitiator;
        private readonly HttpConnectionContext _context;
        private readonly Http3Frame _incomingFrame = new Http3Frame();

        private readonly Http3Connection _http3Connection;
        private bool _receivedHeaders;
        public Pipe RequestBodyPipe { get; }

        public Http3Stream(Http3Connection http3Connection, HttpConnectionContext context) : base(context)
        {
            // First, determine how we know if an Http3stream is unidirectional or bidirectional
            var httpLimits = context.ServiceContext.ServerOptions.Limits;
            var http3Limits = httpLimits.Http3;
            _http3Connection = http3Connection;
            _context = context;

            _frameWriter = new Http3FrameWriter(
                context.Transport.Output,
                context.ConnectionContext,
                context.TimeoutControl,
                httpLimits.MinResponseDataRate,
                context.ConnectionId,
                context.MemoryPool,
                context.ServiceContext.Log);

            // ResponseHeaders aren't set, kind of ugly that we need to reset.
            Reset();

            _http3Output = new Http3OutputProducer(
                0, // TODO streamid
                _frameWriter,
                context.MemoryPool,
                this,
                context.ServiceContext.Log);
            RequestBodyPipe = CreateRequestBodyPipe(64 * 1024); // windowSize?
            Output = _http3Output;
        }

        public QPackDecoder QPackDecoder { get; set; } = new QPackDecoder(10000, 10000);

        public PipeReader Input => _context.Transport.Input;

        public ISystemClock SystemClock => _context.ServiceContext.SystemClock;
        public KestrelServerLimits Limits => _context.ServiceContext.ServerOptions.Limits;

        public void Abort(ConnectionAbortedException ex)
        {
            Abort(ex, Http3ErrorCode.InternalError);
        }

        public void Abort(ConnectionAbortedException ex, Http3ErrorCode errorCode)
        {
        }

        public void OnHeadersComplete(bool endStream)
        {
            OnHeadersComplete();
        }

        public void HandleReadDataRateTimeout()
        {
            Log.RequestBodyMinimumDataRateNotSatisfied(ConnectionId, null, Limits.MinRequestBodyDataRate.BytesPerSecond);
            Abort(new ConnectionAbortedException(CoreStrings.BadRequest_RequestBodyTimeout), Http3ErrorCode.RequestRejected);
        }

        public void HandleRequestHeadersTimeout()
        {
            Log.ConnectionBadRequest(ConnectionId, BadHttpRequestException.GetException(RequestRejectionReason.RequestHeadersTimeout));
            Abort(new ConnectionAbortedException(CoreStrings.BadRequest_RequestHeadersTimeout), Http3ErrorCode.RequestRejected);
        }

        public void OnInputOrOutputCompleted()
        {
            TryClose();
            _frameWriter.Abort(new ConnectionAbortedException(CoreStrings.ConnectionAbortedByClient));
        }

        private bool TryClose()
        {
            if (Interlocked.Exchange(ref _isClosed, 1) == 0)
            {
                return true;
            }

            // TODO make this actually close the Http3Stream by telling msquic to close the stream.
            return false;
        }

        public async Task ProcessRequestAsync<TContext>(IHttpApplication<TContext> application)
        {
            try
            {

                while (_isClosed == 0)
                {
                    var result = await Input.ReadAsync();
                    var readableBuffer = result.Buffer;
                    var consumed = readableBuffer.Start;
                    var examined = readableBuffer.End;

                    try
                    {
                        if (!readableBuffer.IsEmpty)
                        {
                            while (Http3FrameReader.TryReadFrame(ref readableBuffer, _incomingFrame, 16 * 1024, out var framePayload))
                            {
                                consumed = examined = framePayload.End;
                                await ProcessHttp3Stream(application, framePayload);
                            }
                        }

                        if (result.IsCompleted)
                        {
                            return;
                        }
                    }
                    catch (Http3StreamErrorException)
                    {
                        // TODO 
                    }
                    finally
                    {
                        Input.AdvanceTo(consumed, examined);
                    }
                }
            }
            catch (Exception)
            {
                // TODO 
            }
            finally
            {
                await RequestBodyPipe.Writer.CompleteAsync();
            }
        }


        private Task ProcessHttp3Stream<TContext>(IHttpApplication<TContext> application, in ReadOnlySequence<byte> payload)
        {
            switch (_incomingFrame.Type)
            {
                case Http3FrameType.Data:
                    return ProcessDataFrameAsync(payload);
                case Http3FrameType.Headers:
                    return ProcessHeadersFrameAsync(application, payload);
                // need to be on control stream
                case Http3FrameType.DuplicatePush:
                case Http3FrameType.PushPromise:
                case Http3FrameType.Settings:
                case Http3FrameType.GoAway:
                case Http3FrameType.CancelPush:
                case Http3FrameType.MaxPushId:
                    throw new Http3ConnectionException("HTTP_FRAME_UNEXPECTED");
                default:
                    return ProcessUnknownFrameAsync();
            }
        }

        private Task ProcessUnknownFrameAsync()
        {
            // Unknown frames must be explicitly ignored.
            return Task.CompletedTask;
        }

        private Task ProcessHeadersFrameAsync<TContext>(IHttpApplication<TContext> application, ReadOnlySequence<byte> payload)
        {
            QPackDecoder.Decode(payload, handler: this);

            // start off a request once qpack has decoded
            // Make sure to await this task.
            if (_receivedHeaders)
            {
                // trailers
                // TODO figure out if there is anything else to do here.
                return Task.CompletedTask;
            }

            _receivedHeaders = true;

            Task.Run(() => base.ProcessRequestsAsync(application));
            return Task.CompletedTask;
        }

        private Task ProcessDataFrameAsync(in ReadOnlySequence<byte> payload)
        {
            foreach (var segment in payload)
            {
                RequestBodyPipe.Writer.Write(segment.Span);
            }

            // TODO this can be better.
            return RequestBodyPipe.Writer.FlushAsync().AsTask();
        }

        public void StopProcessingNextRequest()
            => StopProcessingNextRequest(serverInitiated: true);

        public void StopProcessingNextRequest(bool serverInitiated)
        {
            var initiator = serverInitiated ? GracefulCloseInitiator.Server : GracefulCloseInitiator.Client;

            if (Interlocked.CompareExchange(ref _gracefulCloseInitiator, initiator, GracefulCloseInitiator.None) == GracefulCloseInitiator.None)
            {
                Input.CancelPendingRead();
            }
        }

        public void Tick(DateTimeOffset now)
        {
        }

        protected override void OnReset()
        {
        }

        protected override void ApplicationAbort()
        {
        }

        protected override string CreateRequestId()
        {
            // TODO include stream id.
            return ConnectionId;
        }

        protected override MessageBody CreateMessageBody()
            => Http3MessageBody.For(this);


        protected override bool TryParseRequest(ReadResult result, out bool endConnection)
        {
            endConnection = !TryValidatePseudoHeaders();
            return true;
        }

        private bool TryValidatePseudoHeaders()
        {
            _httpVersion = Http.HttpVersion.Http3;

            if (!TryValidateMethod())
            {
                return false;
            }

            if (!TryValidateAuthorityAndHost(out var hostText))
            {
                return false;
            }

            // CONNECT - :scheme and :path must be excluded
            if (Method == Http.HttpMethod.Connect)
            {
                if (!string.IsNullOrEmpty(RequestHeaders[HeaderNames.Scheme]) || !string.IsNullOrEmpty(RequestHeaders[HeaderNames.Path]))
                {
                    //ResetAndAbort(new ConnectionAbortedException(CoreStrings.Http2ErrorConnectMustNotSendSchemeOrPath), Http2ErrorCode.PROTOCOL_ERROR);
                    return false;
                }

                RawTarget = hostText;

                return true;
            }

            // :scheme https://tools.ietf.org/html/rfc7540#section-8.1.2.3
            // ":scheme" is not restricted to "http" and "https" schemed URIs.  A
            // proxy or gateway can translate requests for non - HTTP schemes,
            // enabling the use of HTTP to interact with non - HTTP services.

            // - That said, we shouldn't allow arbitrary values or use them to populate Request.Scheme, right?
            // - For now we'll restrict it to http/s and require it match the transport.
            // - We'll need to find some concrete scenarios to warrant unblocking this.
            if (!string.Equals(RequestHeaders[HeaderNames.Scheme], Scheme, StringComparison.OrdinalIgnoreCase))
            {
                //ResetAndAbort(new ConnectionAbortedException(
                //    CoreStrings.FormatHttp2StreamErrorSchemeMismatch(RequestHeaders[HeaderNames.Scheme], Scheme)), Http2ErrorCode.PROTOCOL_ERROR);
                return false;
            }

            // :path (and query) - Required
            // Must start with / except may be * for OPTIONS
            var path = RequestHeaders[HeaderNames.Path].ToString();
            RawTarget = path;

            // OPTIONS - https://tools.ietf.org/html/rfc7540#section-8.1.2.3
            // This pseudo-header field MUST NOT be empty for "http" or "https"
            // URIs; "http" or "https" URIs that do not contain a path component
            // MUST include a value of '/'.  The exception to this rule is an
            // OPTIONS request for an "http" or "https" URI that does not include
            // a path component; these MUST include a ":path" pseudo-header field
            // with a value of '*'.
            if (Method == Http.HttpMethod.Options && path.Length == 1 && path[0] == '*')
            {
                // * is stored in RawTarget only since HttpRequest expects Path to be empty or start with a /.
                Path = string.Empty;
                QueryString = string.Empty;
                return true;
            }

            // Approximate MaxRequestLineSize by totaling the required pseudo header field lengths.
            var requestLineLength = _methodText.Length + Scheme.Length + hostText.Length + path.Length;
            if (requestLineLength > ServerOptions.Limits.MaxRequestLineSize)
            {
                //ResetAndAbort(new ConnectionAbortedException(CoreStrings.BadRequest_RequestLineTooLong), Http2ErrorCode.PROTOCOL_ERROR);
                return false;
            }

            var queryIndex = path.IndexOf('?');
            QueryString = queryIndex == -1 ? string.Empty : path.Substring(queryIndex);

            var pathSegment = queryIndex == -1 ? path.AsSpan() : path.AsSpan(0, queryIndex);

            return TryValidatePath(pathSegment);
        }


        private bool TryValidateMethod()
        {
            // :method
            _methodText = RequestHeaders[HeaderNames.Method].ToString();
            Method = HttpUtilities.GetKnownMethod(_methodText);

            if (Method == Http.HttpMethod.None)
            {
                // TODO
                //ResetAndAbort(new ConnectionAbortedException(CoreStrings.FormatHttp2ErrorMethodInvalid(_methodText)), Http2ErrorCode.PROTOCOL_ERROR);
                return false;
            }

            if (Method == Http.HttpMethod.Custom)
            {
                if (HttpCharacters.IndexOfInvalidTokenChar(_methodText) >= 0)
                {
                    //ResetAndAbort(new ConnectionAbortedException(CoreStrings.FormatHttp2ErrorMethodInvalid(_methodText)), Http2ErrorCode.PROTOCOL_ERROR);
                    return false;
                }
            }

            return true;
        }

        private bool TryValidateAuthorityAndHost(out string hostText)
        {
            // :authority (optional)
            // Prefer this over Host

            var authority = RequestHeaders[HeaderNames.Authority];
            var host = HttpRequestHeaders.HeaderHost;
            if (!StringValues.IsNullOrEmpty(authority))
            {
                // https://tools.ietf.org/html/rfc7540#section-8.1.2.3
                // Clients that generate HTTP/2 requests directly SHOULD use the ":authority"
                // pseudo - header field instead of the Host header field.
                // An intermediary that converts an HTTP/2 request to HTTP/1.1 MUST
                // create a Host header field if one is not present in a request by
                // copying the value of the ":authority" pseudo - header field.

                // We take this one step further, we don't want mismatched :authority
                // and Host headers, replace Host if :authority is defined. The application
                // will operate on the Host header.
                HttpRequestHeaders.HeaderHost = authority;
                host = authority;
            }

            // https://tools.ietf.org/html/rfc7230#section-5.4
            // A server MUST respond with a 400 (Bad Request) status code to any
            // HTTP/1.1 request message that lacks a Host header field and to any
            // request message that contains more than one Host header field or a
            // Host header field with an invalid field-value.
            hostText = host.ToString();
            if (host.Count > 1 || !HttpUtilities.IsHostHeaderValid(hostText))
            {
                // RST replaces 400
                //ResetAndAbort(new ConnectionAbortedException(CoreStrings.FormatBadRequest_InvalidHostHeader_Detail(hostText)), Http2ErrorCode.PROTOCOL_ERROR);
                return false;
            }

            return true;
        }

        private bool TryValidatePath(ReadOnlySpan<char> pathSegment)
        {
            // Must start with a leading slash
            if (pathSegment.Length == 0 || pathSegment[0] != '/')
            {
                //ResetAndAbort(new ConnectionAbortedException(CoreStrings.FormatHttp2StreamErrorPathInvalid(RawTarget)), Http2ErrorCode.PROTOCOL_ERROR);
                return false;
            }

            var pathEncoded = pathSegment.Contains('%');

            // Compare with Http1Connection.OnOriginFormTarget

            // URIs are always encoded/escaped to ASCII https://tools.ietf.org/html/rfc3986#page-11
            // Multibyte Internationalized Resource Identifiers (IRIs) are first converted to utf8;
            // then encoded/escaped to ASCII  https://www.ietf.org/rfc/rfc3987.txt "Mapping of IRIs to URIs"

            try
            {
                // The decoder operates only on raw bytes
                var pathBuffer = new byte[pathSegment.Length].AsSpan();
                for (int i = 0; i < pathSegment.Length; i++)
                {
                    var ch = pathSegment[i];
                    // The header parser should already be checking this
                    Debug.Assert(32 < ch && ch < 127);
                    pathBuffer[i] = (byte)ch;
                }

                Path = PathNormalizer.DecodePath(pathBuffer, pathEncoded, RawTarget, QueryString.Length);

                return true;
            }
            catch (InvalidOperationException)
            {
                //ResetAndAbort(new ConnectionAbortedException(CoreStrings.FormatHttp2StreamErrorPathInvalid(RawTarget)), Http2ErrorCode.PROTOCOL_ERROR);
                return false;
            }
        }

        private Pipe CreateRequestBodyPipe(uint windowSize)
            => new Pipe(new PipeOptions
            (
                pool: _context.MemoryPool,
                readerScheduler: ServiceContext.Scheduler,
                writerScheduler: PipeScheduler.Inline,
                // Never pause within the window range. Flow control will prevent more data from being added.
                // See the assert in OnDataAsync.
                pauseWriterThreshold: windowSize + 1,
                resumeWriterThreshold: windowSize + 1,
                useSynchronizationContext: false,
                minimumSegmentSize: _context.MemoryPool.GetMinimumSegmentSize()
            ));

        /// <summary>
        /// Used to kick off the request processing loop by derived classes.
        /// </summary>
        public abstract void Execute();

        private static class GracefulCloseInitiator
        {
            public const int None = 0;
            public const int Server = 1;
            public const int Client = 2;
        }
    }
}
