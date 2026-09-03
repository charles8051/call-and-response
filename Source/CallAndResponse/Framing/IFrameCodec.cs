using System;
using System.Buffers;

namespace CallAndResponse.Framing
{
    /// <summary>
    /// Turns one payload into the bytes that carry it on the wire — delimiters, escapes, checksums.
    /// </summary>
    public interface IFrameEncoder
    {
        /// <summary>Write <paramref name="payload"/> to <paramref name="destination"/> as one complete frame.</summary>
        void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination);
    }

    /// <summary>
    /// Both halves of a framing. A codec is what binds to a link to make a message channel, because
    /// a framing that transforms the payload has to transform it in both directions.
    /// </summary>
    public interface IFrameCodec : IFrameEncoder, IFrameDecoder
    {
    }
}
