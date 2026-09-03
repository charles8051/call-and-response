using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse.Framing;

namespace CallAndResponse
{
    /// <summary>
    /// A byte channel over an active transport. Sends go out verbatim, and each receive is directed
    /// by the caller: you supply the decoder that says where the frame ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the contract for protocols that decide their own boundaries — a fixed reply length, a
    /// terminator, a length field. For a link whose framing is fixed and self-delimiting, such as
    /// SLIP or RFC 1662, use <see cref="IMessageTransceiver"/>: there the framing chooses the
    /// boundary and a caller-supplied decoder would have nothing to decide.
    /// </para>
    /// <para>
    /// There are no lifecycle members. The caller owns the transport and everything under it.
    /// </para>
    /// </remarks>
    public interface ITransceiver
    {
        /// <summary>Write bytes to the transport exactly as given.</summary>
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken token);

        /// <summary>
        /// Accumulate bytes until <paramref name="decoder"/> reports a frame, and return its payload.
        /// </summary>
        /// <exception cref="FramingException">The decoder rejected a frame as malformed.</exception>
        /// <exception cref="TransceiverTransportException">
        /// The transport closed while the decoder still needed more data.
        /// </exception>
        Task<Memory<byte>> Receive(IFrameDecoder decoder, CancellationToken token);

        /// <summary>
        /// As <see cref="Receive(IFrameDecoder, CancellationToken)"/>, but writes the payload to
        /// <paramref name="destination"/> instead of allocating one. The payload is written only
        /// once a frame is complete.
        /// </summary>
        Task Receive(IFrameDecoder decoder, IBufferWriter<byte> destination, CancellationToken token);
    }
}
