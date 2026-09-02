using System;

namespace CallAndResponse
{
    /// <summary>
    /// The result returned by a <c>detectMessage</c> delegate passed to
    /// <see cref="ITransceiver.ReceiveMessage"/>.
    /// </summary>
    /// <remarks>
    /// Use <see cref="Incomplete"/> when the accumulated buffer does not yet contain
    /// a complete frame.  Use <see cref="Complete(int, int)"/> when a frame boundary has
    /// been identified; supply the byte offset of the payload start and the payload length
    /// within the accumulated buffer.
    /// <para>
    /// When the frame extends beyond the payload — a terminator, a footer, a checksum that
    /// the caller does not want back but that must not be seen again by the next receive —
    /// use <see cref="Complete(int, int, int)"/> to state how many bytes the frame consumed
    /// from the buffer.  Anything past that point stays in the transport for the next call.
    /// </para>
    /// </remarks>
    public readonly struct FrameDetectionResult
    {
        /// <summary>Gets a value indicating whether a complete frame has been detected.</summary>
        public bool IsComplete { get; }

        /// <summary>
        /// Gets the zero-based index of the first payload byte within the accumulated
        /// buffer.  Meaningful only when <see cref="IsComplete"/> is <see langword="true"/>.
        /// </summary>
        public int PayloadOffset { get; }

        /// <summary>
        /// Gets the number of payload bytes.  Meaningful only when
        /// <see cref="IsComplete"/> is <see langword="true"/>.
        /// </summary>
        public int PayloadLength { get; }

        /// <summary>
        /// Gets the number of bytes the detected frame occupies from the start of the
        /// accumulated buffer, including any delimiter that is not part of the payload.
        /// The transceiver consumes exactly this many bytes; the remainder stays in the
        /// transport.  Meaningful only when <see cref="IsComplete"/> is
        /// <see langword="true"/>.
        /// </summary>
        public int ConsumedLength { get; }

        private FrameDetectionResult(bool isComplete, int payloadOffset, int payloadLength, int consumedLength)
        {
            IsComplete = isComplete;
            PayloadOffset = payloadOffset;
            PayloadLength = payloadLength;
            ConsumedLength = consumedLength;
        }

        /// <summary>
        /// Returns a <see cref="FrameDetectionResult"/> that signals the frame is not
        /// yet complete.  The transceiver will continue accumulating bytes.
        /// </summary>
        public static FrameDetectionResult Incomplete => new FrameDetectionResult(false, 0, 0, 0);

        /// <summary>
        /// Returns a <see cref="FrameDetectionResult"/> that signals a complete frame
        /// has been detected.  The frame is taken to end where the payload ends, so
        /// <see cref="ConsumedLength"/> is <paramref name="payloadOffset"/> +
        /// <paramref name="payloadLength"/>.
        /// </summary>
        /// <param name="payloadOffset">
        /// Zero-based index of the first payload byte within the accumulated buffer.
        /// </param>
        /// <param name="payloadLength">Number of payload bytes.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="payloadOffset"/> + <paramref name="payloadLength"/> does not fit in
        /// an <see cref="int"/>, which would yield a frame extent that cannot address the buffer.
        /// </exception>
        public static FrameDetectionResult Complete(int payloadOffset, int payloadLength)
        {
            // Widened deliberately: an int sum would wrap in either direction and publish a
            // frame extent unrelated to the arguments.
            long payloadEnd = (long)payloadOffset + payloadLength;
            if (payloadEnd > int.MaxValue || payloadEnd < int.MinValue)
                throw new ArgumentOutOfRangeException(nameof(payloadLength), payloadLength, "Payload offset plus payload length does not fit in an Int32.");

            return new FrameDetectionResult(true, payloadOffset, payloadLength, (int)payloadEnd);
        }

        /// <summary>
        /// Returns a <see cref="FrameDetectionResult"/> that signals a complete frame
        /// has been detected and whose frame extends past the payload — for example a
        /// terminator or footer that the caller does not want returned but that must be
        /// removed from the transport.
        /// </summary>
        /// <param name="payloadOffset">
        /// Zero-based index of the first payload byte within the accumulated buffer.
        /// </param>
        /// <param name="payloadLength">Number of payload bytes.</param>
        /// <param name="consumedLength">
        /// Number of bytes the frame occupies from the start of the accumulated buffer.
        /// Must be at least <paramref name="payloadOffset"/> + <paramref name="payloadLength"/>;
        /// a shorter frame would hand the caller bytes that are also left in the transport.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="payloadOffset"/> or <paramref name="payloadLength"/> is negative,
        /// their sum overflows <see cref="int"/>, or <paramref name="consumedLength"/> is less
        /// than <paramref name="payloadOffset"/> + <paramref name="payloadLength"/>.
        /// </exception>
        public static FrameDetectionResult Complete(int payloadOffset, int payloadLength, int consumedLength)
        {
            if (payloadOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(payloadOffset), payloadOffset, "Payload offset cannot be negative.");
            if (payloadLength < 0)
                throw new ArgumentOutOfRangeException(nameof(payloadLength), payloadLength, "Payload length cannot be negative.");

            // Widened deliberately: an int sum would wrap and let a consumedLength shorter
            // than the payload pass the check below.
            long payloadEnd = (long)payloadOffset + payloadLength;
            if (payloadEnd > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(payloadLength), payloadLength, "Payload offset plus payload length does not fit in an Int32.");
            if (consumedLength < payloadEnd)
                throw new ArgumentOutOfRangeException(nameof(consumedLength), consumedLength, "Consumed length cannot be less than the end of the payload.");

            return new FrameDetectionResult(true, payloadOffset, payloadLength, consumedLength);
        }
    }
}
