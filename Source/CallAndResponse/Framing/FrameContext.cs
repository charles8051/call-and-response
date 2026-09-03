using System;
using System.Buffers;

namespace CallAndResponse.Framing
{
    /// <summary>
    /// The input handed to <see cref="IFrameDecoder.Decode"/>: the bytes accumulated so far,
    /// plus the two facts about the transport a decoder may need to reach a decision.
    /// </summary>
    /// <remarks>
    /// <see cref="Received"/> always starts at the first unconsumed byte, and grows across calls
    /// as more data arrives. A decoder is re-invoked on that growing buffer, so it must reach the
    /// same conclusion about the same prefix every time; see <see cref="IFrameDecoder.Decode"/>.
    /// </remarks>
    public readonly struct FrameContext
    {
        /// <summary>
        /// Everything received and not yet consumed, always beginning at the first byte of the
        /// frame under construction.
        /// </summary>
        public ReadOnlySequence<byte> Received { get; }

        /// <summary>
        /// Whether the decoder's <see cref="IFrameDecoder.IdleTimeout"/> elapsed with no new bytes.
        /// Always <see langword="false"/> for a decoder that declares no idle timeout.
        /// </summary>
        public bool IsIdle { get; }

        /// <summary>
        /// Whether the transport has completed. No further bytes will ever arrive, so a decoder
        /// that still needs more data cannot get it.
        /// </summary>
        public bool IsTransportComplete { get; }

        /// <summary>Create a context. Normally constructed by the receive loop, not by callers.</summary>
        public FrameContext(ReadOnlySequence<byte> received, bool isIdle, bool isTransportComplete)
        {
            Received = received;
            IsIdle = isIdle;
            IsTransportComplete = isTransportComplete;
        }
    }
}
