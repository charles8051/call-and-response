using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using CallAndResponse;

namespace CallAndResponse.Serial
{
    internal class SerialPortTransceiver : ITransceiver
    {
        private SerialPort _serialPort;

        private readonly int _maxRxBufferSize = 1024;

        public SerialPortTransceiver(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
        {
            _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
            _serialPort.WriteTimeout = SerialPort.InfiniteTimeout;
            _serialPort.ReadTimeout = SerialPort.InfiniteTimeout;
        }

        public async Task Open()
        {
            await Task.Run(() => _serialPort.Open());
        }

        public async Task Close()
        {
            await Task.Run(() => _serialPort.Close());
        }

        public async Task<byte[]> SendReceive(byte[] writeBytes, int numBytesExpected, CancellationToken token)
        {
            await Send(writeBytes, token).ConfigureAwait(false);    
            return await ReceiveExactly(numBytesExpected, token).ConfigureAwait(false);
        }

        public async Task<byte[]> SendReceive(byte[] writeBytes, char terminator, CancellationToken token)
        {
            await Send(writeBytes, token).ConfigureAwait(false);
            return await ReceiveUntilTerminator(terminator, token).ConfigureAwait(false);
        }

        public async Task<byte[]> SendReceive(byte[] writeBytes, byte[] terminatorPattern, CancellationToken token)
        {
            await Send(writeBytes, token).ConfigureAwait(false);
            return await ReceiveUntilTerminatorPattern(terminatorPattern, token).ConfigureAwait(false);
        }

        public async Task<byte[]> SendReceive(byte[] writeBytes, Func<byte[], int> detectMessage, CancellationToken token)
        {
            await Send(writeBytes, token).ConfigureAwait(false);
            return await ReceiveUntilMessageDetected(detectMessage, token).ConfigureAwait(false);
        }

        private async Task Send(byte[] writeBytes, CancellationToken token)
        {
            if (_serialPort.IsOpen is false)
            {
                _serialPort.Open();
            }

            await _serialPort.BaseStream.FlushAsync();
            await _serialPort.BaseStream.WriteAsync(writeBytes, 0, writeBytes.Length, token).ConfigureAwait(false);
        }

        private async Task<byte[]> ReceiveUntilMessageDetected(Func<byte[], int> detectMessage, CancellationToken token)
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
                        if (numBytesRead == _maxRxBufferSize) throw new IOException("buffer overflow");
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

        private async Task<byte[]> ReceiveUntilTerminatorPattern(byte[] terminatorPattern, CancellationToken token)
        {
            return await ReceiveUntilMessageDetected((readBytes) =>
            {
                int terminatorIndex = readBytes.Locate(terminatorPattern).FirstOrDefault();
                int payloadLength = terminatorIndex < 0 ? 0 : terminatorIndex;
                return payloadLength;
            }, token).ConfigureAwait(false);
        }
        private async Task<byte[]> ReceiveUntilTerminator(char terminator, CancellationToken token)
        {
            return await ReceiveUntilMessageDetected((readBytes) =>
            {
                int terminatorIndex = readBytes.ToList().IndexOf((byte)terminator);
                int payloadLength = terminatorIndex < 0 ? 0 : terminatorIndex;
                return payloadLength;
            }, token).ConfigureAwait(false);
        }

        private async Task<byte[]> ReceiveExactly(int numBytesExpected, CancellationToken token)
        {
            if (_serialPort.IsOpen is false)
            {
                _serialPort.Open();
            }

            using (token.Register(() => _serialPort.Close()))
            {
                try
                {
                    byte[] readBytes = new byte[numBytesExpected];
                    int numBytesRead = 0;

                    while (token.IsCancellationRequested == false)
                    {
                        if (_serialPort.BytesToRead == 0)
                        {
                            continue;
                        }

                        numBytesRead += await _serialPort.BaseStream.ReadAsync(readBytes, numBytesRead, numBytesExpected - numBytesRead).ConfigureAwait(false);

                        if (numBytesRead == numBytesExpected)
                        {
                            break;
                        }

                        token.ThrowIfCancellationRequested();
                    }

                    token.ThrowIfCancellationRequested();
                    return readBytes.Take(numBytesRead).ToArray();
                }
                catch (Exception e)
                {
                    throw;
                }
            }
        }

    }

}
