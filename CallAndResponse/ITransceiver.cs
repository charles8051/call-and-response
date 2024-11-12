using System;
using System.Threading.Tasks;
using System.Threading;

namespace CallAndResponse
{
    public interface ITransceiver
    {
        Task Open();
        Task Close();
        Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, int numBytesExpected, CancellationToken token);
        Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, char terminator, CancellationToken token);
        Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> pattern, CancellationToken token);
        Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, Func<ReadOnlyMemory<byte>, int> detectMessage, CancellationToken token);
    }
}
