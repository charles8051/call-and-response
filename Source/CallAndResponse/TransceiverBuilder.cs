using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using Serilog;

namespace CallAndResponse
{
    public record class TransceiverBuilder // Empty non-static class to tack extensions onto. Typically from CallAndResponse.Transport._______ namespaces
    {
        public ILogger _logger { get; init; } = new LoggerConfiguration().CreateLogger();

        public ITransceiverFactory _transceiverFactory { get; init;} = null;

        public ITransceiver Build()
        {
            if (_transceiverFactory == null)
                throw new InvalidOperationException("TransceiverFactory not configured.");

            return _transceiverFactory.CreateTransceiver(_logger);
        }
    }

    public static class TransceiverBuilderExtensions
    {
        public static TransceiverBuilder UseLogger(this TransceiverBuilder builder, ILogger logger)
        {
            return builder with { _logger = logger };
        }
    }
}
