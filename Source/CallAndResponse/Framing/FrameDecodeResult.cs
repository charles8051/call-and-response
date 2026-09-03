using System;

namespace CallAndResponse.Framing
{
    /// <summary>What a <see cref="IFrameDecoder.Decode"/> call concluded about the buffer.</summary>
    public enum FrameDecodeStatus
    {
        /// <summary>No complete frame yet. Nothing is consumed; the decoder is called again when more arrives.</summary>
        NeedMoreData,

        /// <summary>A frame was decoded. Its payload has been written to the staging writer.</summary>
        Frame,

        /// <summary>The leading bytes are not part of any frame. Drop them and keep reading.</summary>
        Discard,

        /// <summary>A frame was found and is malformed. The bytes are consumed and the receive call throws.</summary>
        Invalid,
    }

    /// <summary>
    /// The value returned by <see cref="IFrameDecoder.Decode"/>.
    /// </summary>
    /// <remarks>
    /// Unlike a detection result, this does not describe where the payload sits in the received
    /// bytes — the decoder writes the payload out, so only the consumed extent needs reporting.
    /// That is what lets a decoder produce a payload that is not a contiguous slice of the wire,
    /// which SLIP and RFC 1662 both require.
    /// </remarks>
    public readonly struct FrameDecodeResult
    {
        /// <summary>What the decoder concluded.</summary>
        public FrameDecodeStatus Status { get; }

        /// <summary>
        /// How many bytes to remove from the head of <see cref="FrameContext.Received"/>.
        /// Always zero for <see cref="FrameDecodeStatus.NeedMoreData"/>.
        /// </summary>
        public int ConsumedLength { get; }

        /// <summary>Why the frame was rejected. Non-null only for <see cref="FrameDecodeStatus.Invalid"/>.</summary>
        public string? Reason { get; }

        private FrameDecodeResult(FrameDecodeStatus status, int consumedLength, string? reason)
        {
            Status = status;
            ConsumedLength = consumedLength;
            Reason = reason;
        }

        /// <summary>The buffer does not yet hold a complete frame. Nothing is consumed.</summary>
        public static FrameDecodeResult NeedMoreData => new FrameDecodeResult(FrameDecodeStatus.NeedMoreData, 0, null);

        /// <summary>
        /// A complete frame occupying <paramref name="consumedLength"/> bytes from the head of the
        /// buffer. The payload must already have been written to the staging writer.
        /// </summary>
        public static FrameDecodeResult Frame(int consumedLength)
        {
            if (consumedLength < 0)
                throw new ArgumentOutOfRangeException(nameof(consumedLength), consumedLength, "Consumed length cannot be negative.");

            return new FrameDecodeResult(FrameDecodeStatus.Frame, consumedLength, null);
        }

        /// <summary>
        /// The leading <paramref name="consumedLength"/> bytes belong to no frame. They are dropped
        /// and decoding continues, which is how a decoder recovers from noise without growing the
        /// buffer forever.
        /// </summary>
        public static FrameDecodeResult Discard(int consumedLength)
        {
            if (consumedLength < 0)
                throw new ArgumentOutOfRangeException(nameof(consumedLength), consumedLength, "Consumed length cannot be negative.");

            return new FrameDecodeResult(FrameDecodeStatus.Discard, consumedLength, null);
        }

        /// <summary>
        /// A frame was found and is malformed — a bad checksum, an illegal escape, an over-length
        /// frame. The bytes are consumed before the receive call throws, so the same bad frame
        /// cannot be re-decoded forever.
        /// </summary>
        public static FrameDecodeResult Invalid(int consumedLength, string reason)
        {
            if (consumedLength < 0)
                throw new ArgumentOutOfRangeException(nameof(consumedLength), consumedLength, "Consumed length cannot be negative.");
            if (string.IsNullOrEmpty(reason))
                throw new ArgumentException("An invalid frame must say why.", nameof(reason));

            return new FrameDecodeResult(FrameDecodeStatus.Invalid, consumedLength, reason);
        }
    }
}
