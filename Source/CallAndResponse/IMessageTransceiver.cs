using System;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse.Framing;

namespace CallAndResponse
{
    /// <summary>
    /// A message channel. Framing is a property of the link and fixed for its lifetime, so a caller
    /// sends and receives payloads and never sees a delimiter, an escape, or a checksum.
    /// </summary>
    /// <remarks>
    /// A protocol client written against this runs over SLIP, over RFC 1662, or over a plain
    /// terminator codec without modification, because it never expressed an opinion about byte
    /// boundaries. Build one with <see cref="TransceiverExtensions.WithFraming"/>.
    /// </remarks>
    public interface IMessageTransceiver
    {
        /// <summary>Frame <paramref name="payload"/> and send it as one complete message.</summary>
        Task SendMessage(ReadOnlyMemory<byte> payload, CancellationToken token);

        /// <summary>
        /// Receive one message and return its decoded payload.
        /// </summary>
        /// <exception cref="FramingException">The received frame was malformed.</exception>
        /// <exception cref="TransceiverTransportException">The transport closed mid-message.</exception>
        Task<Memory<byte>> ReceiveMessage(CancellationToken token);
    }
}
