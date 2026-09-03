using System;

namespace CallAndResponse.Framing
{
    /// <summary>
    /// Thrown when a healthy transport delivered a malformed frame. This is neither a
    /// <see cref="TransceiverTransportException"/> — the link is fine — nor a protocol error, since
    /// framing fails before any protocol is reached.
    /// </summary>
    public class FramingException : Exception
    {
        public FramingException() : base() { }
        public FramingException(string message) : base(message) { }
        public FramingException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Thrown when a frame's checksum did not match its contents, such as an RFC 1662 FCS mismatch.
    /// </summary>
    public class FrameIntegrityException : FramingException
    {
        public FrameIntegrityException() : base() { }
        public FrameIntegrityException(string message) : base(message) { }
        public FrameIntegrityException(string message, Exception innerException) : base(message, innerException) { }
    }
}
