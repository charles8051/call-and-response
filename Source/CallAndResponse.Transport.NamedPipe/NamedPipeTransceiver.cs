using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace CallAndResponse.Transport.NamedPipe
{
    public class NamedPipeTransceiver : Transceiver
    {
        private NamedPipeClientStream? _pipeClient;

        private readonly int _maxRxBufferSize = 4096;

        private bool _isOpen;
        public override bool IsOpen
        {
            get { return _isOpen; }
            protected set { _isOpen = value; }
        }

        private readonly string _serverName;
        private readonly string _pipeName;
        private readonly int _connectTimeoutMs;

        public NamedPipeTransceiver(NamedPipeTransceiverOptions options, ILogger logger = null) : base(logger)
        {
            _serverName = options.ServerName;
            _pipeName = options.PipeName;
            _connectTimeoutMs = options.ConnectTimeoutMs;
            if (options.ReceiveBufferSize > 0)
                _maxRxBufferSize = options.ReceiveBufferSize;
        }

        public override async Task Open(CancellationToken token)
        {
            _pipeClient = new NamedPipeClientStream(_serverName, _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.Run(() => _pipeClient.Connect(_connectTimeoutMs), token).ConfigureAwait(false);
            _isOpen = true;
        }

        public override async Task Close(CancellationToken token)
        {
            _isOpen = false;
            _pipeClient?.Dispose();
            _pipeClient = null;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public override async Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token)
        {
            if (_pipeClient is null) throw new TransceiverConnectionException("Named pipe is null");
            await _pipeClient.WriteAsync(writeBytes.ToArray(), 0, writeBytes.Length, token).ConfigureAwait(false);
            await _pipeClient.FlushAsync(token).ConfigureAwait(false);
        }

        public override async Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, (int, int)> detectMessage, CancellationToken token)
        {
            if (_pipeClient is null) throw new TransceiverConnectionException("Named pipe is null");

            int payloadLength = 0;
            int payloadOffset = 0;
            int numBytesRead = 0;
            var readBytes = new byte[_maxRxBufferSize];

            while (token.IsCancellationRequested == false)
            {
                if (numBytesRead >= _maxRxBufferSize) throw new IOException("buffer overflow");

                int bytesRead = await _pipeClient.ReadAsync(readBytes, numBytesRead, _maxRxBufferSize - numBytesRead, token).ConfigureAwait(false);
                if (bytesRead == 0) throw new TransceiverTransportException("Pipe closed by server");
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
