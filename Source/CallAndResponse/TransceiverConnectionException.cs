using System;
using System.Collections.Generic;
using System.Text;

namespace CallAndResponse
{
    public class TransceiverConnectionException : Exception
    {
        public TransceiverConnectionException(string message) : base(message)
        {
        }
    }
}
