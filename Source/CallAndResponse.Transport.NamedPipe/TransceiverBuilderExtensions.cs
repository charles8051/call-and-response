using Serilog;
using System;

namespace CallAndResponse.Transport.NamedPipe
{
    public static class TransceiverBuilderExtensions
    {
        public static TransceiverBuilder UseNamedPipe(this TransceiverBuilder builder, Action<NamedPipeTransceiverOptions> options = null)
        {
            var opts = new NamedPipeTransceiverOptions();
            options?.Invoke(opts);

            if (string.IsNullOrWhiteSpace(opts.PipeName))
                throw new ArgumentException("PipeName must be specified in NamedPipeTransceiverOptions.");

            return builder with { _transceiverFactory = new NamedPipeTransceiverFactory(opts) };
        }
    }

    public class NamedPipeTransceiverOptions
    {
        public string ServerName { get; set; } = ".";
        public string PipeName { get; set; }
        public int ConnectTimeoutMs { get; set; } = 5000;
        public int ReceiveBufferSize { get; set; } = 4096;
    }

    public class NamedPipeTransceiverFactory : ITransceiverFactory
    {
        private readonly NamedPipeTransceiverOptions _options;

        public NamedPipeTransceiverFactory(NamedPipeTransceiverOptions options)
        {
            _options = options;
        }

        public ITransceiver CreateTransceiver(ILogger logger = null)
        {
            return new NamedPipeTransceiver(_options, logger);
        }
    }
}
