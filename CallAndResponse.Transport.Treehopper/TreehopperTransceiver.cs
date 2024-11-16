using System;
using System.Threading;
using System.Threading.Tasks;
using Treehopper;
using Treehopper.Desktop;

namespace CallAndResponse.Transport.Treehopper
{
    public class TreehopperTransceiver : Transceiver
    {
        public override bool IsOpen => throw new NotImplementedException();

        private TreehopperUsb _board;

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

        public override async Task<Memory<byte>> ReceiveExactly(int numBytesExpected, CancellationToken token = default)
        {
            var bytes = await _board.Uart.ReceiveAsync(numBytesExpected);
            return bytes.AsMemory();
        }

        public override async Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, int> detectMessage, CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public override async Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token = default)
        {
            throw new NotImplementedException();
        }
    }
}
