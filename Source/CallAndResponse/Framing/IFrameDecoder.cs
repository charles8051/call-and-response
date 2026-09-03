using System;
using System.Buffers;

namespace CallAndResponse.Framing
{
    /// <summary>
    /// Turns received bytes into one frame's payload. A decoder owns the question "where does this
    /// frame end", and — unlike a detection delegate — also produces the payload, so framings whose
    /// payload is not a contiguous slice of the wire are expressible.
    /// </summary>
    public interface IFrameDecoder
    {
        /// <summary>
        /// How long the transport may be silent before <see cref="Decode"/> is called again with
        /// <see cref="FrameContext.IsIdle"/> set. <see langword="null"/> for decoders that frame on
        /// content alone, which never wake on silence.
        /// </summary>
        TimeSpan? IdleTimeout { get; }

        /// <summary>
        /// Inspect the accumulated bytes and decide whether a frame is present.
        /// </summary>
        /// <param name="context">The bytes so far, and the transport's idle and completion state.</param>
        /// <param name="payload">
        /// Where to write the decoded payload. Write to it only when returning
        /// <see cref="FrameDecodeResult.Frame"/>: it is a staging buffer that the receive loop
        /// resets before every call and copies to the caller only on a frame, so writing on any
        /// other path is discarded rather than delivered.
        /// </param>
        /// <remarks>
        /// <para>
        /// Implementations must be a pure function of <paramref name="context"/>. The receive loop
        /// re-invokes the decoder on a buffer that grows but always starts at the same byte, so a
        /// decoder that carries a parse cursor between calls will mis-frame. Caching keyed on
        /// <see cref="FrameContext.Received"/>'s length is fine; remembering where it got to is not.
        /// </para>
        /// <para>
        /// Do not throw. Report a malformed frame as <see cref="FrameDecodeResult.Invalid"/> so the
        /// bad bytes are consumed before the error surfaces; the receive loop turns that into a
        /// <see cref="FramingException"/>.
        /// </para>
        /// </remarks>
        FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload);
    }
}
