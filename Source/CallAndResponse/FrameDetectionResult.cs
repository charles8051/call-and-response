using System;

namespace CallAndResponse
{
    /// <summary>
    /// The result returned by a <c>detectMessage</c> delegate passed to
    /// <see cref="ITransceiver.ReceiveMessage"/>.
    /// </summary>
    /// <remarks>
    /// Use <see cref="Incomplete"/> when the accumulated buffer does not yet contain
    /// a complete frame.  Use <see cref="Complete"/> when a frame boundary has been
    /// identified; supply the byte offset of the payload start and the payload length
    /// within the accumulated buffer.
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

        private FrameDetectionResult(bool isComplete, int payloadOffset, int payloadLength)
        {
            IsComplete = isComplete;
            PayloadOffset = payloadOffset;
            PayloadLength = payloadLength;
        }

        /// <summary>
        /// Returns a <see cref="FrameDetectionResult"/> that signals the frame is not
        /// yet complete.  The transceiver will continue accumulating bytes.
        /// </summary>
        public static FrameDetectionResult Incomplete => new FrameDetectionResult(false, 0, 0);

        /// <summary>
        /// Returns a <see cref="FrameDetectionResult"/> that signals a complete frame
        /// has been detected.
        /// </summary>
        /// <param name="payloadOffset">
        /// Zero-based index of the first payload byte within the accumulated buffer.
        /// </param>
        /// <param name="payloadLength">Number of payload bytes.</param>
        public static FrameDetectionResult Complete(int payloadOffset, int payloadLength)
            => new FrameDetectionResult(true, payloadOffset, payloadLength);
    }
}
