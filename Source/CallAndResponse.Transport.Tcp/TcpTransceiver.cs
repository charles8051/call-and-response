using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace CallAndResponse.Transport.Tcp
{
    public class TcpTransceiver : Transceiver
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;

        private readonly int _maxRxBufferSize = 4096;

        private bool _isOpen;
        public override bool IsOpen
        {
            get { return _isOpen; }
            protected set { _isOpen = value; }
        }

        private readonly string _host;
        private readonly int _port;

        public TcpTransceiver(TcpTransceiverOptions options, ILogger logger = null) : base(logger)
        {
            _host = options.Host;
            _port = options.Port;
            if (options.ReceiveBufferSize > 0)
                _maxRxBufferSize = options.ReceiveBufferSize;
        }

        public override async Task Open(CancellationToken token)
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(_host, _port).ConfigureAwait(false);
            _stream = _tcpClient.GetStream();
            _isOpen = true;
        }

        public override async Task Close(CancellationToken token)
        {
            _isOpen = false;
            _stream?.Dispose();
            _stream = null;
            _tcpClient?.Close();
            _tcpClient?.Dispose();
            _tcpClient = null;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public override async Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token)
        {
            if (_stream is null) throw new TransceiverConnectionException("TCP stream is null");
            await _stream.WriteAsync(writeBytes.ToArray(), 0, writeBytes.Length, token).ConfigureAwait(false);
            await _stream.FlushAsync(token).ConfigureAwait(false);
        }

        public override async Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, (int, int)> detectMessage, CancellationToken token)
        {
            if (_stream is null) throw new TransceiverConnectionException("TCP stream is null");

            int payloadLength = 0;
            int payloadOffset = 0;
            int numBytesRead = 0;
            var readBytes = new byte[_maxRxBufferSize];

            while (token.IsCancellationRequested == false)
            {
                if (numBytesRead >= _maxRxBufferSize) throw new IOException("buffer overflow");

                int bytesRead = await _stream.ReadAsync(readBytes, numBytesRead, _maxRxBufferSize - numBytesRead, token).ConfigureAwait(false);
                if (bytesRead == 0) throw new TransceiverTransportException("Connection closed by remote host");
                numBytesRead += bytesRead;

                (payloadOffset, payloadLength) = detectMessage(new ReadOnlyMemory<byte>(readBytes, 0, numBytesRead));
                if (payloadLength > 0)
                {
                    break;
                }

                token.ThrowIfCancellationRequested();
            }

            token.ThrowIfCancellationRequested();
            return new Memory<byte>(readBytes, payloadOffset, payloadLength);
        }
    }
}
