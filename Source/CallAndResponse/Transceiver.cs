using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CallAndResponse
{
    /// <summary>
    /// The core <see cref="ITransceiver"/> implementation backed by
    /// <see cref="PipeReader"/> and <see cref="PipeWriter"/>.
    /// <para>
    /// Reads chunks from the pipe (not byte-at-a-time) and applies caller-supplied
    /// frame detection to identify complete messages. Any bytes beyond the detected
    /// frame remain in the pipe for the next receive call.
    /// </para>
    /// </summary>
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

        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;
        private readonly ILogger<Transceiver> _logger;

        /// <summary>
        /// Create a transceiver from an <see cref="IDuplexPipe"/>.
        /// The pipe must already be active for the duration of use.
        /// </summary>
        /// <param name="pipe">A duplex pipe providing the transport's read and write ends.</param>
        /// <param name="logger">Optional logger. Falls back to <see cref="NullLogger{T}"/> when <see langword="null"/>.</param>
        public Transceiver(IDuplexPipe pipe, ILogger<Transceiver>? logger = null)
        {
            if (pipe is null) throw new ArgumentNullException(nameof(pipe));
            _reader = pipe.Input;
            _writer = pipe.Output;
            _logger = logger ?? NullLogger<Transceiver>.Instance;
        }

        /// <summary>
        /// Create a transceiver from separate <see cref="PipeReader"/> and <see cref="PipeWriter"/>.
        /// Both must already be active for the duration of use.
        /// </summary>
        /// <param name="input">The read side of the transport.</param>
        /// <param name="output">The write side of the transport.</param>
        /// <param name="logger">Optional logger. Falls back to <see cref="NullLogger{T}"/> when <see langword="null"/>.</param>
        public Transceiver(PipeReader input, PipeWriter output, ILogger<Transceiver>? logger = null)
        {
            _reader = input ?? throw new ArgumentNullException(nameof(input));
            _writer = output ?? throw new ArgumentNullException(nameof(output));
            _logger = logger ?? NullLogger<Transceiver>.Instance;
        }

        /// <inheritdoc />
        public async Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token)
        {
            LogSending(_logger, writeBytes.Length);
            await _writer.WriteAsync(writeBytes, token).ConfigureAwait(false);
            BytesSentCounter.Add(writeBytes.Length);
        }

        /// <inheritdoc />
        public async Task<Memory<byte>> ReceiveMessage(
            Func<ReadOnlyMemory<byte>, FrameDetectionResult> detectMessage,
            CancellationToken token)
        {
            while (true)
            {
                var readResult = await _reader.ReadAsync(token).ConfigureAwait(false);
                var buffer = readResult.Buffer;

                LogPipeRead(_logger, buffer.Length, buffer.Length);

                var contiguous = buffer.IsSingleSegment
                    ? buffer.First
                    : (ReadOnlyMemory<byte>)buffer.ToArray();

                var result = detectMessage(contiguous);
                if (result.IsComplete)
                {
                    LogFrameDetected(_logger, result.PayloadOffset, result.PayloadLength);

                    var payload = contiguous
                        .Slice(result.PayloadOffset, result.PayloadLength)
                        .ToArray();

                    _reader.AdvanceTo(
                        buffer.GetPosition(result.PayloadOffset + result.PayloadLength));

                    BytesReceivedCounter.Add(result.PayloadLength);
                    FramesReceivedCounter.Add(1);
                    return payload;
                }

                // Examined everything, consumed nothing — wait for more data.
                _reader.AdvanceTo(buffer.Start, buffer.End);

                if (readResult.IsCompleted)
                {
                    LogTransportClosedBeforeFrameComplete(_logger);
                    throw new TransceiverTransportException(
                        "Transport closed before frame was complete");
                }
            }
        }

        /// <inheritdoc />
        public async Task<Memory<byte>> ReceiveUntilIdle(
            TimeSpan idleTimeout,
            CancellationToken token = default)
        {
            var accumulated = new List<byte>();

            while (true)
            {
                // Arm a one-shot timer that calls CancelPendingRead when the idle window
                // elapses. CancelPendingRead causes ReadAsync to return ReadResult.IsCanceled
                // = true instead of throwing OperationCanceledException, eliminating both
                // the exception overhead and the debug-output spam on every idle timeout.
                using var idleTimer = new Timer(
                    _ => _reader.CancelPendingRead(), null, idleTimeout, Timeout.InfiniteTimeSpan);

                var readResult = await _reader.ReadAsync(token).ConfigureAwait(false);

                // Disarm the timer the moment ReadAsync returns so a stale callback cannot
                // call CancelPendingRead and corrupt the very next ReadAsync call.
                idleTimer.Change(Timeout.Infinite, Timeout.Infinite);

                var buffer = readResult.Buffer;
                foreach (var segment in buffer)
                {
                    accumulated.AddRange(segment.ToArray());
                }
                _reader.AdvanceTo(buffer.End);

                if (readResult.IsCanceled)
                {
                    // Idle timeout fired — no exception, just a flag on the ReadResult.
                    if (accumulated.Count == 0)
                        continue; // No bytes at all yet — keep waiting for the first byte.

                    LogIdleTimeoutFired(_logger, accumulated.Count);
                    BytesReceivedCounter.Add(accumulated.Count);
                    FramesReceivedCounter.Add(1);
                    return accumulated.ToArray();
                }

                LogIdleReceiveRead(_logger, (int)buffer.Length, accumulated.Count);

                if (readResult.IsCompleted)
                {
                    if (accumulated.Count > 0)
                    {
                        BytesReceivedCounter.Add(accumulated.Count);
                        FramesReceivedCounter.Add(1);
                        return accumulated.ToArray();
                    }

                    LogTransportClosedDuringIdleReceive(_logger);
                    throw new TransceiverTransportException("Transport closed");
                }
            }
        }

        // ── Source-generated log methods ─────────────────────────────────────

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Sending {ByteCount} bytes")]
        private static partial void LogSending(ILogger logger, int byteCount);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Read {ByteCount} bytes from pipe, buffer total {BufferLength} bytes")]
        private static partial void LogPipeRead(ILogger logger, long byteCount, long bufferLength);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Frame detected at offset {PayloadOffset} with length {PayloadLength}")]
        private static partial void LogFrameDetected(ILogger logger, int payloadOffset, int payloadLength);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Transport closed before frame was complete")]
        private static partial void LogTransportClosedBeforeFrameComplete(ILogger logger);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Idle receive read {ByteCount} bytes, accumulated {AccumulatedBytes} bytes total")]
        private static partial void LogIdleReceiveRead(ILogger logger, int byteCount, int accumulatedBytes);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Idle timeout fired with {AccumulatedBytes} accumulated bytes")]
        private static partial void LogIdleTimeoutFired(ILogger logger, int accumulatedBytes);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Transport closed during idle receive")]
        private static partial void LogTransportClosedDuringIdleReceive(ILogger logger);
    }
}
