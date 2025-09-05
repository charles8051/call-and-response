using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Runtime.CompilerServices;
using Serilog;

using System.IO.Ports;

namespace CallAndResponse.Transport.Serial
{
    public class SerialPortTransceiver : Transceiver
    {
        protected SerialPort? _serialPort;

        private readonly int _maxRxBufferSize = 1024;

        private bool _isOpen;
        public override bool IsOpen
        {
            get
            {
                return _isOpen;
            }
            protected set
            {
                _isOpen = value;
            }
        }

        protected string _portName;
        protected int _baudRate;
        protected Parity _parity;
        protected int _dataBits;
        protected StopBits _stopBits;

        protected SerialPortTransceiver(SerialTransceiverOptions options)
        {
            _portName = options.PortName;
            _baudRate = options.BaudRate;
            _parity = options.Parity;
            _dataBits = options.DataBits;
            _stopBits = options.StopBits;
        }


        public SerialPortTransceiver(ILogger logger) : base(logger)
        {
            
        }

        public override async Task Open(CancellationToken token)
        {
            if(_serialPort is null)
            {
                _serialPort = new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits);
                _serialPort.WriteTimeout = SerialPort.InfiniteTimeout;
                _serialPort.ReadTimeout = SerialPort.InfiniteTimeout;
            }

            //if (_serialPort.IsOpen is false) { return; }

            await Task.Run(() => _serialPort.Open());
        }

        public override async Task Close(CancellationToken token)
        {
            if(_serialPort is null) { return; }
            await Task.Run(() => {
                _serialPort.Close();
                _serialPort.Dispose();
            });
        }

        public override async Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token)
        {
            if (_serialPort is null) { throw new TransceiverConnectionException("Serial Port is null"); }
            if (_serialPort.IsOpen is false)
            {
                _serialPort.Open();
            }

            await _serialPort.BaseStream.FlushAsync();
            await _serialPort.BaseStream.WriteAsync(writeBytes.ToArray(), 0, writeBytes.Length, token).ConfigureAwait(false);
        }

        public override async Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, (int,int)> detectMessage, CancellationToken token)
        {
            if (_serialPort is null) { throw new TransceiverConnectionException("Serial Port is null"); }
            if (_serialPort.IsOpen is false)
            {
                _serialPort.Open();
            }

            int payloadLength = 0;
            int payloadOffset = 0;
            int numBytesRead = 0;


            void Close()
            {
                _serialPort.Close();
                _serialPort.Open();
            }

            try
            {
                var readBytes = new byte[_maxRxBufferSize];

                while (token.IsCancellationRequested == false)
                {
                    if (numBytesRead >= _maxRxBufferSize) throw new IOException("buffer overflow");
                    if (_serialPort.BytesToRead == 0) continue;

                    using (var cts = new CancellationTokenSource(10))
                    using (var registration = cts.Token.Register(Close))
                    {
                        try
                        {
                            numBytesRead += await _serialPort.BaseStream.ReadAsync(readBytes, numBytesRead, _maxRxBufferSize - numBytesRead, token);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                        }
                    }
                    //numBytesRead += await _serialPort.BaseStream.ReadAsync(readBytes, numBytesRead, _maxRxBufferSize - numBytesRead, token);

                    // TODO: return index of payload start also
                    (payloadOffset, payloadLength) = detectMessage(readBytes.Take(numBytesRead).ToArray());
                    if (payloadLength > 0)
                    {
                        break;
                    }

                    token.ThrowIfCancellationRequested();
                }

                token.ThrowIfCancellationRequested();
                return readBytes.Skip(payloadOffset).Take(payloadLength).ToArray();
            }
            catch (Exception e)
            {
                throw;
            }
        }
    }

}
