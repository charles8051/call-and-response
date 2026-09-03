using System;
using System.Buffers;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse.Framing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CallAndResponse
{
    /// <summary>
    /// The core <see cref="ITransceiver"/>, backed by a <see cref="PipeReader"/> and a
    /// <see cref="PipeWriter"/>.
    /// </summary>
    /// <remarks>
    /// Reads chunks from the pipe and applies a caller-supplied decoder until it reports a frame.
    /// Bytes beyond the frame stay in the pipe for the next receive. The caller owns the pipe; this
    /// type never completes either end of it.
    /// </remarks>
    public sealed partial class Transceiver : ITransceiver
    {
        private static readonly Meter TransceiverMeter = new("CallAndResponse.Transceiver", "1.0.0");
        private static readonly Counter<long> BytesSentCounter =
            TransceiverMeter.CreateCounter<long>(
                "callresponse.transceiver.bytes_sent",
                description: "Total bytes sent through the transceiver.");
        private static readonly Counter<long> BytesReceivedCounter =
            TransceiverMeter.CreateCounter<long>(
                "callresponse.transceiver.bytes_received",
                description: "Total payload bytes received in complete frames.");
        private static readonly Counter<long> FramesReceivedCounter =
            TransceiverMeter.CreateCounter<long>(
                "callresponse.transceiver.frames_received",
                description: "Total complete frames received.");
        private static readonly Counter<long> BytesDiscardedCounter =
            TransceiverMeter.CreateCounter<long>(
                "callresponse.transceiver.bytes_discarded",
                description: "Total bytes a decoder dropped as belonging to no frame.");

        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;
        private readonly ILogger<Transceiver> _logger;

        /// <summary>
        /// Create a transceiver from an <see cref="IDuplexPipe"/>. The pipe must already be active
        /// for the duration of use.
        /// </summary>
        public Transceiver(IDuplexPipe pipe, ILogger<Transceiver>? logger = null)
        {
            if (pipe is null) throw new ArgumentNullException(nameof(pipe));
            _reader = pipe.Input;
            _writer = pipe.Output;
            _logger = logger ?? NullLogger<Transceiver>.Instance;
        }

        /// <summary>
        /// Create a transceiver from separate pipe ends. Both must already be active for the
        /// duration of use.
        /// </summary>
        public Transceiver(PipeReader input, PipeWriter output, ILogger<Transceiver>? logger = null)
        {
            _reader = input ?? throw new ArgumentNullException(nameof(input));
            _writer = output ?? throw new ArgumentNullException(nameof(output));
            _logger = logger ?? NullLogger<Transceiver>.Instance;
        }

        /// <inheritdoc />
        public async Task Send(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            LogSending(_logger, bytes.Length);
            await _writer.WriteAsync(bytes, token).ConfigureAwait(false);
            BytesSentCounter.Add(bytes.Length);
        }

        /// <inheritdoc />
        public async Task<Memory<byte>> Receive(IFrameDecoder decoder, CancellationToken token)
        {
            var destination = new ArrayBufferWriter<byte>();
            await Receive(decoder, destination, token).ConfigureAwait(false);
            return destination.WrittenMemory.ToArray();
        }

        /// <inheritdoc />
        public async Task Receive(IFrameDecoder decoder, IBufferWriter<byte> destination, CancellationToken token)
        {
            if (decoder is null) throw new ArgumentNullException(nameof(decoder));
            if (destination is null) throw new ArgumentNullException(nameof(destination));

            // The decoder writes here, not into the caller's destination. A decoder that writes and
            // then asks for more data would otherwise duplicate its output on the next read, and one
            // that writes and then rejects the frame would leave those bytes with the caller —
            // IBufferWriter cannot take them back. Staging makes both harmless.
            var staging = new ArrayBufferWriter<byte>();

            TimeSpan? idleTimeout = decoder.IdleTimeout;

            while (true)
            {
                var arming = new IdleArming();
                using var idleTimer = idleTimeout is null
                    ? null
                    : new Timer(_ => arming.Fire(_reader), null, idleTimeout.Value, Timeout.InfiniteTimeSpan);

                var readResult = await _reader.ReadAsync(token).ConfigureAwait(false);
                var buffer = readResult.Buffer;

                // Only a cancellation this iteration's own timer caused is an idle window. A
                // callback that won the race to fire while we were disarming it cancels a later
                // read instead, and that read must not be reported as idle — which is the whole
                // reason the arming state is a CAS rather than a Timer.Change.
                bool idleFired = !arming.Disarm() && readResult.IsCanceled;

                if (readResult.IsCanceled && !idleFired)
                {
                    // A stray from an earlier arming, or a CancelPendingRead from outside. Neither
                    // ends a frame, so read again.
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    continue;
                }

                LogPipeRead(_logger, buffer.Length, idleFired);

                staging.Clear();
                FrameDecodeResult result;
                bool advanced = false;
                try
                {
                    var context = new FrameContext(buffer, idleFired, readResult.IsCompleted);
                    result = decoder.Decode(context, staging);
                }
                catch
                {
                    // A decoder is caller-supplied code. Leaving without advancing would break the
                    // read-then-advance contract and wedge the pipe for every later call.
                    _reader.AdvanceTo(buffer.Start);
                    throw;
                }

                try
                {
                    switch (result.Status)
                    {
                        case FrameDecodeStatus.Frame:
                            RequireFits(result, buffer.Length, nameof(decoder));
                            destination.Write(staging.WrittenSpan);
                            _reader.AdvanceTo(buffer.GetPosition(result.ConsumedLength));
                            advanced = true;

                            LogFrameDecoded(_logger, staging.WrittenCount, result.ConsumedLength);
                            BytesReceivedCounter.Add(staging.WrittenCount);
                            FramesReceivedCounter.Add(1);
                            return;

                        case FrameDecodeStatus.Discard:
                            RequireFits(result, buffer.Length, nameof(decoder));
                            if (result.ConsumedLength == 0)
                            {
                                throw new ArgumentException(
                                    "The decoder discarded zero bytes, which would loop forever. A discard must make progress.",
                                    nameof(decoder));
                            }

                            LogBytesDiscarded(_logger, result.ConsumedLength);
                            BytesDiscardedCounter.Add(result.ConsumedLength);
                            _reader.AdvanceTo(buffer.GetPosition(result.ConsumedLength));
                            advanced = true;
                            continue;

                        case FrameDecodeStatus.Invalid:
                            RequireFits(result, buffer.Length, nameof(decoder));

                            // Consume the bad frame before throwing, so the same bytes cannot be
                            // decoded into the same failure on every later call.
                            _reader.AdvanceTo(buffer.GetPosition(result.ConsumedLength));
                            advanced = true;

                            LogFrameRejected(_logger, result.Reason ?? "unspecified", result.ConsumedLength);
                            throw new FramingException(result.Reason ?? "The decoder rejected the frame.");

                        default:
                            if (readResult.IsCompleted)
                            {
                                // The decoder has now seen the final bytes and still wants more,
                                // which it will never get. Say what was left rather than spinning
                                // on a completed pipe or dropping the remainder silently.
                                _reader.AdvanceTo(buffer.Start, buffer.End);
                                advanced = true;

                                LogTransportClosedBeforeFrameComplete(_logger, buffer.Length);
                                throw new TransceiverTransportException(
                                    $"Transport closed with {buffer.Length} byte(s) left unframed.");
                            }

                            // Examined everything, consumed nothing — wait for more data.
                            _reader.AdvanceTo(buffer.Start, buffer.End);
                            advanced = true;
                            continue;
                    }
                }
                finally
                {
                    if (!advanced) _reader.AdvanceTo(buffer.Start);
                }
            }
        }

        private static void RequireFits(in FrameDecodeResult result, long bufferLength, string parameterName)
        {
            if (result.ConsumedLength > bufferLength)
            {
                throw new ArgumentException(
                    $"The decoder consumed {result.ConsumedLength} bytes from a {bufferLength}-byte buffer.",
                    parameterName);
            }
        }

        /// <summary>
        /// One arming of the idle timer. The CAS is what makes disarming reliable:
        /// <see cref="Timer.Change(int, int)"/> does not recall a callback already queued to the
        /// thread pool, so without this a late callback cancels a read it was never armed for.
        /// </summary>
        private sealed class IdleArming
        {
            private const int Armed = 0;
            private const int Fired = 1;
            private const int Disarmed = 2;

            private int _state = Armed;

            internal void Fire(PipeReader reader)
            {
                if (Interlocked.CompareExchange(ref _state, Fired, Armed) == Armed)
                {
                    reader.CancelPendingRead();
                }
            }

            /// <summary>Returns whether the timer was disarmed before it could fire.</summary>
            internal bool Disarm() => Interlocked.CompareExchange(ref _state, Disarmed, Armed) == Armed;
        }

        // ── Source-generated log methods ─────────────────────────────────────

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Sending {ByteCount} bytes")]
        private static partial void LogSending(ILogger logger, int byteCount);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Read from pipe: {BufferLength} bytes accumulated (idle: {IsIdle})")]
        private static partial void LogPipeRead(ILogger logger, long bufferLength, bool isIdle);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Frame decoded: {PayloadLength} byte payload, {ConsumedLength} bytes consumed")]
        private static partial void LogFrameDecoded(ILogger logger, int payloadLength, int consumedLength);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Discarded {ByteCount} bytes belonging to no frame")]
        private static partial void LogBytesDiscarded(ILogger logger, int byteCount);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Frame rejected ({Reason}); consumed {ConsumedLength} bytes")]
        private static partial void LogFrameRejected(ILogger logger, string reason, int consumedLength);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Transport closed with {ByteCount} bytes left unframed")]
        private static partial void LogTransportClosedBeforeFrameComplete(ILogger logger, long byteCount);
    }
}
