using System;

namespace CallAndResponse
{
    /// <summary>
    /// Thrown when an I/O-level failure occurs during an active communication session.
    /// Examples include the transport closing unexpectedly mid-transfer or a write
    /// failing on the underlying pipe.
    /// </summary>
    public class TransceiverTransportException : Exception
    {
        public TransceiverTransportException() : base() { }
        public TransceiverTransportException(string message) : base(message) { }
        public TransceiverTransportException(string message, Exception innerException) : base(message, innerException) { }
    }
}
