using System;
using System.Threading.Tasks;
using System.Threading;
using Serilog.Core;
using System.Linq;

namespace CallAndResponse
{
    public interface ITransceiver
    {
        bool IsOpen { get; }
        Task Open(CancellationToken token);
        Task Close(CancellationToken token);
        Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token);
        Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, (int, int)> detectMessage, CancellationToken token);
        Task<string> SendReceiveString(string writeString, char terminator, CancellationToken token);
        Task<string> SendReceiveString(string writeString, string terminator, CancellationToken token);
        Task<Memory<byte>> SendReceiveExactly(ReadOnlyMemory<byte> writeBytes, int numBytesExpected, CancellationToken token);
        Task<Memory<byte>> SendReceiveFooter(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> footer, CancellationToken token);
        Task<Memory<byte>> SendReceiveHeaderFooter(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> footer, CancellationToken token);
        Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, Func<ReadOnlyMemory<byte>, (int,int)> detectMessage, CancellationToken token);
        Task<Memory<byte>> SendReceivePerfectMatch(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> matchBytes, CancellationToken token);
        Task<Memory<byte>> ReceiveUntilPerfectMatch(ReadOnlyMemory<byte> matchBytes, CancellationToken token);
        Task<Memory<byte>> ReceiveUntilTerminatorPattern(ReadOnlyMemory<byte> terminatorPattern, CancellationToken token);
        Task<Memory<byte>> ReceiveUntilHeaderFooterMatch(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> footer, CancellationToken token);
        Task<Memory<byte>> ReceiveUntilTerminator(char terminator, CancellationToken token);
        Task<Memory<byte>> ReceiveExactly(int numBytesExpected, CancellationToken token);
    }
}
