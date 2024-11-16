using System;
using System.Threading.Tasks;
using System.Threading;

namespace CallAndResponse
{
    public interface ITransceiver
    {
        bool IsConnected { get; }
        Task Open(CancellationToken token);
        Task Close(CancellationToken token);
        Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token);
        Task<Memory<byte>> ReceiveUntilMessageDetected(Func<ReadOnlyMemory<byte>, int> detectMessage, CancellationToken token);
        Task<Memory<byte>> ReceiveExactly(int numBytesExpected, CancellationToken token);
        Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, int numBytesExpected, CancellationToken token);
        Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, char terminator, CancellationToken token);
        Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> pattern, CancellationToken token);
        Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, Func<ReadOnlyMemory<byte>, int> detectMessage, CancellationToken token);
    }
}
