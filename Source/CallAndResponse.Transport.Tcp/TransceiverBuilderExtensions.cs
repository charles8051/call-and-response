using Serilog;
using System;

namespace CallAndResponse.Transport.Tcp
{
    public static class TransceiverBuilderExtensions
    {
        public static TransceiverBuilder UseTcp(this TransceiverBuilder builder, Action<TcpTransceiverOptions> options = null)
        {
            var opts = new TcpTransceiverOptions();
            options?.Invoke(opts);

            if (string.IsNullOrWhiteSpace(opts.Host))
                throw new ArgumentException("Host must be specified in TcpTransceiverOptions.");
            if (opts.Port <= 0 || opts.Port > 65535)
                throw new ArgumentException("Port must be a valid port number (1-65535) in TcpTransceiverOptions.");

            return builder with { _transceiverFactory = new TcpTransceiverFactory(opts) };
        }
    }

    public class TcpTransceiverOptions
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public int ReceiveBufferSize { get; set; } = 4096;
    }

    public class TcpTransceiverFactory : ITransceiverFactory
    {
        private readonly TcpTransceiverOptions _options;

        public TcpTransceiverFactory(TcpTransceiverOptions options)
        {
            _options = options;
        }

        public ITransceiver CreateTransceiver(ILogger logger = null)
        {
            return new TcpTransceiver(_options, logger);
        }
    }
}
