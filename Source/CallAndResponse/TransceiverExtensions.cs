using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse.Framing;
using Microsoft.Extensions.Logging;

namespace CallAndResponse
{
    /// <summary>
    /// Composition helpers over <see cref="ITransceiver"/> and <see cref="IMessageTransceiver"/>.
    /// </summary>
    /// <remarks>
    /// The framing strategies that used to have a method each now live in <see cref="Frame"/> as
    /// decoder values, so one <c>SendReceive</c> covers what a dozen overloads did and the strategies
    /// compose with one another.
    /// </remarks>
    public static class TransceiverExtensions
    {
        /// <summary>Send a frame, then receive one using <paramref name="decoder"/>.</summary>
        public static async Task<Memory<byte>> SendReceive(
            this ITransceiver transceiver,
            ReadOnlyMemory<byte> writeBytes,
            IFrameDecoder decoder,
            CancellationToken token)
        {
            if (transceiver is null) throw new ArgumentNullException(nameof(transceiver));

            await transceiver.Send(writeBytes, token).ConfigureAwait(false);
            return await transceiver.Receive(decoder, token).ConfigureAwait(false);
        }

        /// <summary>Send a payload, then receive one message.</summary>
        public static async Task<Memory<byte>> SendReceiveMessage(
            this IMessageTransceiver channel,
            ReadOnlyMemory<byte> payload,
            CancellationToken token)
        {
            if (channel is null) throw new ArgumentNullException(nameof(channel));

            await channel.SendMessage(payload, token).ConfigureAwait(false);
            return await channel.ReceiveMessage(token).ConfigureAwait(false);
        }

        /// <summary>
        /// Send an ASCII string and receive up to a single-character terminator, returned as ASCII.
        /// </summary>
        public static async Task<string> SendReceiveString(
            this ITransceiver transceiver,
            string writeString,
            char terminator,
            CancellationToken token)
        {
            var payload = await transceiver
                .SendReceive(Encoding.ASCII.GetBytes(writeString), Frame.UntilTerminator((byte)terminator), token)
                .ConfigureAwait(false);

            return Encoding.ASCII.GetString(payload.Span);
        }

        /// <summary>
        /// Send an ASCII string and receive up to a multi-character terminator, returned as ASCII.
        /// </summary>
        public static async Task<string> SendReceiveString(
            this ITransceiver transceiver,
            string writeString,
            string terminator,
            CancellationToken token)
        {
            var payload = await transceiver
                .SendReceive(Encoding.ASCII.GetBytes(writeString), Frame.UntilPattern(Encoding.ASCII.GetBytes(terminator)), token)
                .ConfigureAwait(false);

            return Encoding.ASCII.GetString(payload.Span);
        }

        /// <summary>
        /// Bind a framing to this byte channel, giving a message channel that sends and receives
        /// payloads. The framing is fixed for the life of the returned channel.
        /// </summary>
        public static IMessageTransceiver WithFraming(this ITransceiver transceiver, IFrameCodec codec, ILogger? logger = null)
            => new MessageTransceiver(transceiver, codec, logger);

        /// <summary>
        /// Present a message channel as a byte channel, for a client written against
        /// <see cref="ITransceiver"/>. Lossy in both directions — see <see cref="ByteStreamAdapter"/>
        /// for exactly how.
        /// </summary>
        public static ITransceiver AsByteStream(this IMessageTransceiver channel)
            => new ByteStreamAdapter(channel);
    }
}
