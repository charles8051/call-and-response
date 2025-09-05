using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using System.IO.Ports;

namespace CallAndResponse.Transport.Serial
{
    public static class TransceiverBuilderExtensions
    {
        // TODO: Figure out how to accommodate VID PID pairs on all platforms. CIM queries will only work on Windows.
        // Need to allow a list of VID/PID pairs to be specified in case we want to support multiple devices.

        public static TransceiverBuilder UseSerial(this TransceiverBuilder builder, Action<SerialTransceiverOptions> options = null)
        {
            var opts = new SerialTransceiverOptions();
            options?.Invoke(opts);

            // TODO: add and validate remaining serial options: Baud rate, parity, stop bits, data bits
            // Either a VID/PID pair or a port name, but not both, must be specified
            // We need to decide when to check which platform we're on to determine if we can even use VID/PID
            if (string.IsNullOrWhiteSpace(opts.PortName))
                throw new ArgumentException("PortName must be specified in SerialTransceiverOptions.");

            return builder with { _transceiverFactory = new SerialTransceiverFactory(opts) };
        }

    }

    public class SerialTransceiverOptions
    {
        public string PortName { get; set; }
        public int BaudRate { get; set; } = 115200;
        public Parity Parity { get; set; } = Parity.None;
        public StopBits StopBits { get; set; } = StopBits.One;
        public int DataBits { get; set; } = 8;
    }

    public class SerialTransceiverFactory : ITransceiverFactory
    {
        private readonly SerialTransceiverOptions _options;

        public SerialTransceiverFactory(SerialTransceiverOptions options)
        {
            _options = options;
        }

        public ITransceiver CreateTransceiver(ILogger logger = null)
        {
            return new SerialPortTransceiver(_options, logger);
        }
    }
}
