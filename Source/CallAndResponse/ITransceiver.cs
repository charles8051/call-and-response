using System;
using System.Threading;
using System.Threading.Tasks;

namespace CallAndResponse
{
    /// <summary>
    /// Protocol-facing communication contract over an active byte transport.
    /// <para>
    /// Provides two operations: <see cref="Send"/> to transmit a frame, and
    /// <see cref="ReceiveMessage"/> to accumulate incoming bytes until a caller-supplied
    /// detection function identifies a complete response frame.
    /// </para>
    /// <para>
    /// For unsolicited or streaming data where no structural delimiter exists, use
    /// <see cref="ReceiveUntilIdle"/> which returns accumulated bytes after a period
    /// of silence on the transport.
    /// </para>
    /// </summary>
    public interface ITransceiver
    {
        /// <summary>
        /// Transmit a frame to the transport.
        /// </summary>
        /// <param name="writeBytes">The bytes to send.</param>
        /// <param name="token">Cancellation token.</param>
        Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token);

        /// <summary>
        /// Accumulate bytes from the transport and invoke <paramref name="detectMessage"/>
        /// after each chunk until it signals that a complete frame has been received.
        /// <para>
        /// The detection function receives the full accumulated buffer and returns a
        /// <see cref="FrameDetectionResult"/> indicating whether the frame is complete.
        /// When complete, the result specifies the payload offset and length within the
        /// buffer; only those bytes are returned to the caller.
        /// </para>
        /// <para>
        /// The implementation consumes <see cref="FrameDetectionResult.ConsumedLength"/>
        /// bytes from the transport, which is the payload end unless the detector reported
        /// a frame that extends further — a terminator or footer, for instance. Bytes
        /// beyond the frame stay in the transport for the next call.
        /// </para>
        /// </summary>
        /// <param name="detectMessage">
        /// A function that inspects the accumulated buffer and returns
        /// <see cref="FrameDetectionResult.Incomplete"/> to continue reading, or
        /// <see cref="FrameDetectionResult.Complete(int, int)"/> — or
        /// <see cref="FrameDetectionResult.Complete(int, int, int)"/> when the frame
        /// extends past the payload — to extract the payload and stop.
        /// </param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The detected payload bytes.</returns>
        Task<Memory<byte>> ReceiveMessage(
            Func<ReadOnlyMemory<byte>, FrameDetectionResult> detectMessage,
            CancellationToken token);

        /// <summary>
        /// Accumulate bytes using temporal framing: reads until no new data arrives
        /// within <paramref name="idleTimeout"/>, then returns the accumulated buffer.
        /// <para>
        /// This is designed for unsolicited or streaming data (e.g., barcode scanners,
        /// GPS NMEA sentences) where the gap between bytes is the frame boundary and
        /// no structural delimiter exists.
        /// </para>
        /// <para>
        /// Waits indefinitely for the first byte. Once at least one byte has been
        /// received, returns the full buffer after <paramref name="idleTimeout"/>
        /// elapses with no additional data.
        /// </para>
        /// </summary>
        /// <param name="idleTimeout">
        /// Maximum time to wait between consecutive bytes before considering the
        /// message complete. Does not apply to the initial wait for the first byte.
        /// </param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>All bytes accumulated before the idle timeout fired.</returns>
        Task<Memory<byte>> ReceiveUntilIdle(TimeSpan idleTimeout, CancellationToken token);
    }
}
