using Serilog;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.Text;

namespace CallAndResponse
{
    public interface ITransceiverFactory
    {
        ITransceiver CreateTransceiver(ILogger logger);
    }
}
