using System;
using System.Collections.Generic;
using System.Text;

namespace CallAndResponse
{
    public class TransceiverTransportException: Exception
    {
        public TransceiverTransportException(string message) : base(message)
        {

        }
    }
}
