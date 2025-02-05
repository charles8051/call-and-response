using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Treehopper;
using Treehopper.Desktop;

namespace CallAndResponse.Transport.Treehopper
{
    public class TreehopperTransceiver : Transceiver
    {
        public override bool IsOpen { get; protected set; }

        private TreehopperUsb _board;
        private int _maxRxBufferSize = 1024;

        public TreehopperTransceiver(TreehopperUsb board)
        {
            _board = board;
        }

        public static async Task<TreehopperTransceiver> Create()
        {
            var board = await ConnectionService.Instance.GetFirstDeviceAsync();
            return new TreehopperTransceiver(board);
        }

        public override async Task Open(CancellationToken token = default)
        {
            await _board.ConnectAsync();
            await _board.Uart.StartUartAsync(115200);
        }

        public override async Task Close(CancellationToken token = default)
        {
            _board.Disconnect();
        }

        public override async Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, (int,int)> detectMessage, CancellationToken token = default)
        {
            var data = await _board.Uart.ReceiveAsync();
            data.AsMemory();

            var payloadLength = 0;
            var payloadOffset = 0;
            var numBytesRead = 0;

            var readBytes = new byte[_maxRxBufferSize];

            while (token.IsCancellationRequested == false)
            {
                if (numBytesRead >= _maxRxBufferSize) throw new IOException("buffer overflow");
                token.ThrowIfCancellationRequested();

                var newData = await _board.Uart.ReceiveAsync();
                readBytes.Concat(newData.ToArray());

                (payloadOffset, payloadLength) = detectMessage(data);
                if (payloadLength > 0)
                {
                    break;
                }
            }

            token.ThrowIfCancellationRequested();
            return readBytes.Skip(payloadOffset).Take(payloadLength).ToArray();
        }

        public override async Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token = default)
        {
            var discard = await _board.Uart.ReceiveAsync(); // clear buffer
            await _board.Uart.SendAsync(writeBytes.ToArray());
        }
    }
}
