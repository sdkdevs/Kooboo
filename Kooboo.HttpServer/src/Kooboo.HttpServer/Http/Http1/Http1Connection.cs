// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Sequences;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Kooboo.HttpServer;
using System.Text.Encodings.Web.Utf8;
using System.Threading.Tasks;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Kooboo.HttpServer.Http
{
    public partial class Http1Connection : HttpProtocol, IRequestProcessor
    {
        private const byte ByteAsterisk = (byte)'*';
        private const byte ByteForwardSlash = (byte)'/';
        private const string Asterisk = "*";

        private readonly Http1ConnectionContext _context;
        private readonly IHttpParser<Http1ParsingHandler> _parser;
        protected readonly long _keepAliveTicks;
        private readonly long _requestHeadersTimeoutTicks;

        private volatile bool _requestTimedOut;
        private uint _requestCount;

        private HttpRequestTarget _requestTargetForm = HttpRequestTarget.Unknown;
        private Uri _absoluteRequestTarget;

        private int _remainingRequestHeadersBytesAllowed;

        public Http1Connection(Http1ConnectionContext context)
            : base(context)
        {
            _context = context;
            _parser = ServiceContext.HttpParser;
            _keepAliveTicks = ServerOptions.Limits.KeepAliveTimeout.Ticks;
            _requestHeadersTimeoutTicks = ServerOptions.Limits.RequestHeadersTimeout.Ticks;

            Output = new Http1OutputProducer(_context.ToApplication.Input, _context.ToTransport.Output, _context.ConnectionId, context.ServiceContext.Log, _context.TimeoutControl);
        }

        public PipeReader Input => _context.ToTransport.Input;

        public ITimeoutControl TimeoutControl => _context.TimeoutControl;
        public bool RequestTimedOut => _requestTimedOut;

        public override bool IsUpgradableRequest => _upgradeAvailable;

        /// <summary>
        /// Stops the request processing loop between requests.
        /// Called on all active connections when the server wants to initiate a shutdown
        /// and after a keep-alive timeout.
        /// </summary>
        public void StopProcessingNextRequest()
        {
            _keepAlive = false;
            Input.CancelPendingRead();
        }

        public void SendTimeoutResponse()
        {
            _requestTimedOut = true;
            Input.CancelPendingRead();
        }

        public void ParseRequest(ReadOnlyBuffer<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = buffer.Start;
            examined = buffer.End;

            switch (_requestProcessingStatus)
            {
                case RequestProcessingStatus.RequestPending:
                    if (buffer.IsEmpty)
                    {
                        break;
                    }

                    TimeoutControl.ResetTimeout(_requestHeadersTimeoutTicks, TimeoutAction.SendTimeoutResponse);

                    _requestProcessingStatus = RequestProcessingStatus.ParsingRequestLine;
                    goto case RequestProcessingStatus.ParsingRequestLine;
                case RequestProcessingStatus.ParsingRequestLine:
                    if (TakeStartLine(buffer, out consumed, out examined))
                    {
                        buffer = buffer.Slice(consumed, buffer.End);

                        _requestProcessingStatus = RequestProcessingStatus.ParsingHeaders;
                        goto case RequestProcessingStatus.ParsingHeaders;
                    }
                    else
                    {
                        break;
                    }
                case RequestProcessingStatus.ParsingHeaders:
                    if (TakeMessageHeaders(buffer, out consumed, out examined))
                    {
                        _requestProcessingStatus = RequestProcessingStatus.AppStarted;
                    }
                    break;
            }
        }

        public bool TakeStartLine(ReadOnlyBuffer<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            var overLength = false;
            if (buffer.Length >= ServerOptions.Limits.MaxRequestLineSize)
            {
                buffer = buffer.Slice(buffer.Start, ServerOptions.Limits.MaxRequestLineSize);
                overLength = true;
            }

            var result = _parser.ParseRequestLine(new Http1ParsingHandler(this), buffer, out consumed, out examined);
            if (!result && overLength)
            {
                ThrowRequestRejected(RequestRejectionReason.RequestLineTooLong);
            }

            return result;
        }

        public bool TakeMessageHeaders(ReadOnlyBuffer<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            // Make sure the buffer is limited
            bool overLength = false;
            if (buffer.Length >= _remainingRequestHeadersBytesAllowed)
            {
                buffer = buffer.Slice(buffer.Start, _remainingRequestHeadersBytesAllowed);

                // If we sliced it means the current buffer bigger than what we're
                // allowed to look at
                overLength = true;
            }

            var result = _parser.ParseHeaders(new Http1ParsingHandler(this), buffer, out consumed, out examined, out var consumedBytes);
            _remainingRequestHeadersBytesAllowed -= consumedBytes;

            if (!result && overLength)
            {
                ThrowRequestRejected(RequestRejectionReason.HeadersExceedMaxTotalSize);
            }
            if (result)
            {
                TimeoutControl.CancelTimeout();
            }

            return result;
        }

        public void OnStartLine(HttpMethod method, HttpVersion version, Span<byte> target, Span<byte> path, Span<byte> query, Span<byte> customMethod, bool pathEncoded)
        {
            Debug.Assert(target.Length != 0, "Request target must be non-zero length");

            var ch = target[0];
            if (ch == ByteForwardSlash)
            {
                // origin-form.
                // The most common form of request-target.
                // https://tools.ietf.org/html/rfc7230#section-5.3.1
                OnOriginFormTarget(method, version, target, path, query, customMethod, pathEncoded);
            }
            else if (ch == ByteAsterisk && target.Length == 1)
            {
                OnAsteriskFormTarget(method);
            }
            else if (target.GetKnownHttpScheme(out var scheme))
            {
                OnAbsoluteFormTarget(target, query);
            }
            else
            {
                // Assume anything else is considered authority form.
                // FYI: this should be an edge case. This should only happen when
                // a client mistakenly thinks this server is a proxy server.
                OnAuthorityFormTarget(method, target);
            }

            HttpRequestFeature.Method = method != HttpMethod.Custom
                ? HttpUtilities.MethodToString(method) ?? string.Empty
                : customMethod.GetAsciiStringNonNullCharacters();
            _httpVersion = version;

            Debug.Assert(HttpRequestFeature.RawTarget != null, "RawTarget was not set");
            Debug.Assert(HttpRequestFeature.Method != null, "Method was not set");
            Debug.Assert(HttpRequestFeature.Path != null, "Path was not set");
            Debug.Assert(HttpRequestFeature.QueryString != null, "QueryString was not set");
            Debug.Assert(HttpVersion != null, "HttpVersion was not set");
        }

        private void OnOriginFormTarget(HttpMethod method, HttpVersion version, Span<byte> target, Span<byte> path, Span<byte> query, Span<byte> customMethod, bool pathEncoded)
        {
            Debug.Assert(target[0] == ByteForwardSlash, "Should only be called when path starts with /");

            _requestTargetForm = HttpRequestTarget.OriginForm;

            // URIs are always encoded/escaped to ASCII https://tools.ietf.org/html/rfc3986#page-11
            // Multibyte Internationalized Resource Identifiers (IRIs) are first converted to utf8;
            // then encoded/escaped to ASCII  https://www.ietf.org/rfc/rfc3987.txt "Mapping of IRIs to URIs"
            string requestUrlPath = null;
            string rawTarget = null;

            try
            {
                // Read raw target before mutating memory.
                rawTarget = target.GetAsciiStringNonNullCharacters();

                if (pathEncoded)
                {
                    // URI was encoded, unescape and then parse as UTF-8
                    // Disabling warning temporary
#pragma warning disable 618
                    var pathLength =UrlEncoder.Decode(path, path);
#pragma warning restore 618

                    // Removing dot segments must be done after unescaping. From RFC 3986:
                    //
                    // URI producing applications should percent-encode data octets that
                    // correspond to characters in the reserved set unless these characters
                    // are specifically allowed by the URI scheme to represent data in that
                    // component.  If a reserved character is found in a URI component and
                    // no delimiting role is known for that character, then it must be
                    // interpreted as representing the data octet corresponding to that
                    // character's encoding in US-ASCII.
                    //
                    // https://tools.ietf.org/html/rfc3986#section-2.2
                    pathLength = PathNormalizer.RemoveDotSegments(path.Slice(0, pathLength));

                    requestUrlPath = GetUtf8String(path.Slice(0, pathLength));
                }
                else
                {
                    var pathLength = PathNormalizer.RemoveDotSegments(path);

                    if (path.Length == pathLength && query.Length == 0)
                    {
                        // If no decoding was required, no dot segments were removed and
                        // there is no query, the request path is the same as the raw target
                        requestUrlPath = rawTarget;
                    }
                    else
                    {
                        requestUrlPath = path.Slice(0, pathLength).GetAsciiStringNonNullCharacters();
                    }
                }
            }
            catch (InvalidOperationException)
            {
                ThrowRequestTargetRejected(target);
            }

            HttpRequestFeature.QueryString = query.GetAsciiStringNonNullCharacters();
            HttpRequestFeature.RawTarget = rawTarget;
            HttpRequestFeature.Path = requestUrlPath;
        }

        private void OnAuthorityFormTarget(HttpMethod method, Span<byte> target)
        {
            _requestTargetForm = HttpRequestTarget.AuthorityForm;

            // This is not complete validation. It is just a quick scan for invalid characters
            // but doesn't check that the target fully matches the URI spec.
            for (var i = 0; i < target.Length; i++)
            {
                var ch = target[i];
                if (!UriUtilities.IsValidAuthorityCharacter(ch))
                {
                    ThrowRequestTargetRejected(target);
                }
            }

            // The authority-form of request-target is only used for CONNECT
            // requests (https://tools.ietf.org/html/rfc7231#section-4.3.6).
            if (method != HttpMethod.Connect)
            {
                ThrowRequestRejected(RequestRejectionReason.ConnectMethodRequired);
            }

            // When making a CONNECT request to establish a tunnel through one or
            // more proxies, a client MUST send only the target URI's authority
            // component (excluding any userinfo and its "@" delimiter) as the
            // request-target.For example,
            //
            //  CONNECT www.example.com:80 HTTP/1.1
            //
            // Allowed characters in the 'host + port' section of authority.
            // See https://tools.ietf.org/html/rfc3986#section-3.2
            HttpRequestFeature.RawTarget = target.GetAsciiStringNonNullCharacters();
            HttpRequestFeature.Path = string.Empty;
            HttpRequestFeature.QueryString = string.Empty;
        }

        private void OnAsteriskFormTarget(HttpMethod method)
        {
            _requestTargetForm = HttpRequestTarget.AsteriskForm;

            // The asterisk-form of request-target is only used for a server-wide
            // OPTIONS request (https://tools.ietf.org/html/rfc7231#section-4.3.7).
            if (method != HttpMethod.Options)
            {
                ThrowRequestRejected(RequestRejectionReason.OptionsMethodRequired);
            }

            HttpRequestFeature.RawTarget = Asterisk;
            HttpRequestFeature.Path = string.Empty;
            HttpRequestFeature.QueryString = string.Empty;
        }

        private void OnAbsoluteFormTarget(Span<byte> target, Span<byte> query)
        {
            _requestTargetForm = HttpRequestTarget.AbsoluteForm;

            // absolute-form
            // https://tools.ietf.org/html/rfc7230#section-5.3.2

            // This code should be the edge-case.

            // From the spec:
            //    a server MUST accept the absolute-form in requests, even though
            //    HTTP/1.1 clients will only send them in requests to proxies.

            HttpRequestFeature.RawTarget = target.GetAsciiStringNonNullCharacters();

            // Validation of absolute URIs is slow, but clients
            // should not be sending this form anyways, so perf optimization
            // not high priority

            if (!Uri.TryCreate(HttpRequestFeature.RawTarget, UriKind.Absolute, out var uri))
            {
                ThrowRequestTargetRejected(target);
            }

            _absoluteRequestTarget = uri;
            HttpRequestFeature.Path = uri.LocalPath;
            // don't use uri.Query because we need the unescaped version
            HttpRequestFeature.QueryString = query.GetAsciiStringNonNullCharacters();
        }

        private static unsafe string GetUtf8String(Span<byte> path)
        {
            return Encoding.UTF8.GetString(path.ToArray());
            // .NET 451 doesn't have pointer overloads for Encoding.GetString so we
            // copy to an array
            //fixed (byte* pointer = &MemoryMarshal.GetReference(path))
            //{
            //    return Encoding.UTF8.GetString(pointer, path.Length);
            //}
        }

        private void EnsureHostHeaderExists()
        {
            if (_httpVersion == Http.HttpVersion.Http10)
            {
                return;
            }

            // https://tools.ietf.org/html/rfc7230#section-5.4
            // A server MUST respond with a 400 (Bad Request) status code to any
            // HTTP/1.1 request message that lacks a Host header field and to any
            // request message that contains more than one Host header field or a
            // Host header field with an invalid field-value.

            var host = HttpRequestHeaders.HeaderHost;
            if (host.Count <= 0)
            {
                ThrowRequestRejected(RequestRejectionReason.MissingHostHeader);
            }
            else if (host.Count > 1)
            {
                ThrowRequestRejected(RequestRejectionReason.MultipleHostHeaders);
            }
            else if (_requestTargetForm == HttpRequestTarget.AuthorityForm)
            {
                if (!host.Equals(HttpRequestFeature.RawTarget))
                {
                    ThrowRequestRejected(RequestRejectionReason.InvalidHostHeader, host.ToString());
                }
            }
            else if (_requestTargetForm == HttpRequestTarget.AbsoluteForm)
            {
                // If the target URI includes an authority component, then a
                // client MUST send a field - value for Host that is identical to that
                // authority component, excluding any userinfo subcomponent and its "@"
                // delimiter.

                // System.Uri doesn't not tell us if the port was in the original string or not.
                // When IsDefaultPort = true, we will allow Host: with or without the default port
                var authorityAndPort = _absoluteRequestTarget.Authority + ":" + _absoluteRequestTarget.Port;
                if ((host != _absoluteRequestTarget.Authority || !_absoluteRequestTarget.IsDefaultPort)
                    && host != authorityAndPort)
                {
                    ThrowRequestRejected(RequestRejectionReason.InvalidHostHeader, host.ToString());
                }
            }
        }

        protected override void OnReset()
        {
            //FastFeatureSet(typeof(IHttpUpgradeFeature), this);

            _requestTimedOut = false;
            _requestTargetForm = HttpRequestTarget.Unknown;
            _absoluteRequestTarget = null;
            _remainingRequestHeadersBytesAllowed = ServerOptions.Limits.MaxRequestHeadersTotalSize + 2;
            _requestCount++;
        }

        protected override void OnRequestProcessingEnding()
        {
            Input.Complete();
        }

        protected override string CreateRequestId()
            => StringUtilities.ConcatAsHexSuffix(ConnectionId, ':', _requestCount);

        protected override MessageBody CreateMessageBody()
            => Http1MessageBody.For(_httpVersion, HttpRequestHeaders, this);

        protected override void BeginRequestProcessing()
        {
            // Reset the features and timeout.
            Reset();
            TimeoutControl.SetTimeout(_keepAliveTicks, TimeoutAction.StopProcessingNextRequest);
        }

        protected override bool BeginRead(out ValueAwaiter<ReadResult> awaitable)
        {
            awaitable = Input.ReadAsync();
            return true;
        }

        protected override bool TryParseRequest(ReadResult result, out bool endConnection)
        {
            var examined = result.Buffer.End;
            var consumed = result.Buffer.End;

            try
            {
                ParseRequest(result.Buffer, out consumed, out examined);
            }
            catch (InvalidOperationException)
            {
                if (_requestProcessingStatus == RequestProcessingStatus.ParsingHeaders)
                {
                    throw BadHttpRequestException.GetException(RequestRejectionReason
                        .MalformedRequestInvalidHeaders);
                }
                throw;
            }
            finally
            {
                Input.AdvanceTo(consumed, examined);
            }

            if (result.IsCompleted)
            {
                switch (_requestProcessingStatus)
                {
                    case RequestProcessingStatus.RequestPending:
                        endConnection = true;
                        return true;
                    case RequestProcessingStatus.ParsingRequestLine:
                        throw BadHttpRequestException.GetException(
                            RequestRejectionReason.InvalidRequestLine);
                    case RequestProcessingStatus.ParsingHeaders:
                        throw BadHttpRequestException.GetException(
                            RequestRejectionReason.MalformedRequestInvalidHeaders);
                }
            }
            else if (!_keepAlive && _requestProcessingStatus == RequestProcessingStatus.RequestPending)
            {
                // Stop the request processing loop if the server is shutting down or there was a keep-alive timeout
                // and there is no ongoing request.
                endConnection = true;
                return true;
            }
            else if (RequestTimedOut)
            {
                // In this case, there is an ongoing request but the start line/header parsing has timed out, so send
                // a 408 response.
                throw BadHttpRequestException.GetException(RequestRejectionReason.RequestTimeout);
            }

            endConnection = false;
            if (_requestProcessingStatus == RequestProcessingStatus.AppStarted)
            {
                EnsureHostHeaderExists();
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// change http version(no use)
        /// </summary>
        /// <returns></returns>
        public async Task<Stream> UpgradeAsync()
        {
            if (!IsUpgradableRequest)
            {
                throw new InvalidOperationException(CoreStrings.CannotUpgradeNonUpgradableRequest);
            }

            if (IsUpgraded)
            {
                throw new InvalidOperationException(CoreStrings.UpgradeCannotBeCalledMultipleTimes);
            }

            if (!ServiceContext.ConnectionManager.UpgradedConnectionCount.TryLockOne())
            {
                throw new InvalidOperationException(CoreStrings.UpgradedConnectionLimitReached);
            }

            IsUpgraded = true;

            //todo release 
            //ConnectionFeatures.Get<IDecrementConcurrentConnectionCountFeature>()?.ReleaseConnection();
            

            HttpResponseFeature.StatusCode = StatusCodes.Status101SwitchingProtocols;
            HttpResponseFeature.ReasonPhrase = "Switching Protocols";
            HttpResponseFeature.Headers["Connection"] = "Upgrade";
            if (!HttpResponseFeature.Headers.ContainsKey("Upgrade"))
            {
                StringValues values;
                if (HttpRequestFeature.Headers.TryGetValue("Upgrade", out values))
                {
                    HttpRequestFeature.Headers["Upgrade"] = values;
                }
            }

            await FlushAsync(default(CancellationToken));

            return _streams.Upgrade();
        }
    }
}
