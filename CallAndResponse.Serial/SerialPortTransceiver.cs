using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace CallAndResponse.Serial
{
    public class SerialPortTransceiver : Transceiver
    {
        private SerialPort _serialPort;

        private readonly int _maxRxBufferSize = 1024;

        private bool _isConnected;
        public override bool IsConnected => _serialPort.IsOpen;


        public static SerialPortTransceiver CreateFromId(ushort vid, ushort pid, int baudRate, Parity parity, int dataBits, StopBits stopBits)
        {
            string? portName = SerialPortUtils.FindPortNameById(vid, pid);
            if (portName is null)
            {
                throw new ArgumentException("Device not found");
            }
            return new SerialPortTransceiver(portName, baudRate, parity, dataBits, stopBits);
        }
        public SerialPortTransceiver(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
        {
            // TODO: defer instantiation of SerialPort to Open method?
            _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
            _serialPort.WriteTimeout = SerialPort.InfiniteTimeout;
            _serialPort.ReadTimeout = SerialPort.InfiniteTimeout;
        }

        public override async Task Open(CancellationToken token)
        {
            await Task.Run(() => _serialPort.Open());
        }

        public override async Task Close(CancellationToken token)
        {
            await Task.Run(() => _serialPort.Close());
        }

        public override async Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token)
        {
            if (_serialPort.IsOpen is false)
            {
                _serialPort.Open();
            }

            await _serialPort.BaseStream.FlushAsync();
            await _serialPort.BaseStream.WriteAsync(writeBytes.ToArray(), 0, writeBytes.Length, token).ConfigureAwait(false);
        }

        public override async Task<Memory<byte>> ReceiveUntilMessageDetected(Func<ReadOnlyMemory<byte>, int> detectMessage, CancellationToken token)
        {
            if (_serialPort.IsOpen is false)
            {
                _serialPort.Open();
            }

            int payloadLength = 0;
            int numBytesRead = 0;
            using (token.Register(() => _serialPort.Close()))
            {
                try
                {
                    var readBytes = new byte[_maxRxBufferSize];

                    while (token.IsCancellationRequested == false)
                    {
                        if (numBytesRead >= _maxRxBufferSize) throw new IOException("buffer overflow");
                        if (_serialPort.BytesToRead == 0) continue;

                        numBytesRead += await _serialPort.BaseStream.ReadAsync(readBytes, numBytesRead, _maxRxBufferSize - numBytesRead , token);

                        payloadLength = detectMessage(readBytes);
                        if (payloadLength > 0)
                        {
                            break;
                        }

                        token.ThrowIfCancellationRequested();
                    }

                    token.ThrowIfCancellationRequested();
                    return readBytes.Take(payloadLength).ToArray();
                }
                catch (Exception e)
                {
                    throw;
                }
            }
        }

        public override async Task<Memory<byte>> ReceiveExactly(int numBytesExpected, CancellationToken token)
        {
            if (_serialPort.IsOpen is false)
            {
                _serialPort.Open();
            }

            using (token.Register(() => _serialPort.Close()))
            {
                try
                {
                    var readData = new Memory<byte>(new byte[numBytesExpected]);
                    //byte[] readBytes = new byte[numBytesExpected];
                    int numBytesRead = 0;

                    while (token.IsCancellationRequested == false)
                    {
                        if (_serialPort.BytesToRead == 0)
                        {
                            continue;
                        }
                        numBytesRead += await _serialPort.BaseStream.ReadAsync(readData.Slice(numBytesRead), token).ConfigureAwait(false);
                        //numBytesRead += await _serialPort.BaseStream.ReadAsync(readBytes, numBytesRead, numBytesExpected - numBytesRead).ConfigureAwait(false);

                        if (numBytesRead == numBytesExpected)
                        {
                            break;
                        }

                        token.ThrowIfCancellationRequested();
                    }

                    token.ThrowIfCancellationRequested();
                    //return readBytes.Take(numBytesRead).ToArray();
                    return readData.Slice(0, numBytesRead);
                }
                catch (Exception e)
                {
                    throw;
                }
            }
        }

    }

}
