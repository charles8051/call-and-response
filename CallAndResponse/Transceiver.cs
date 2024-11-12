using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

namespace CallAndResponse
{
    public abstract class Transceiver : ITransceiver
    {
        public abstract Task Open();
        public abstract Task Close();
        protected abstract Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token);
        protected abstract Task<Memory<byte>> ReceiveUntilMessageDetected(Func<ReadOnlyMemory<byte>, int> detectMessage, CancellationToken token);
        protected abstract Task<Memory<byte>> ReceiveExactly(int numBytesExpected, CancellationToken token);
        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, int numBytesExpected, CancellationToken token)
        {
            await Send(writeBytes, token).ConfigureAwait(false);
            return await ReceiveExactly(numBytesExpected, token).ConfigureAwait(false);
        }
        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, char terminator, CancellationToken token)
        {
            await Send(writeBytes, token).ConfigureAwait(false);
            return await ReceiveUntilTerminator(terminator, token).ConfigureAwait(false);
        }
        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> terminatorPattern, CancellationToken token)
        {
            await Send(writeBytes, token).ConfigureAwait(false);
            return await ReceiveUntilTerminatorPattern(terminatorPattern, token).ConfigureAwait(false);
        }
        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, Func<ReadOnlyMemory<byte>, int> detectMessage, CancellationToken token)
        {
            await Send(writeBytes, token).ConfigureAwait(false);
            return await ReceiveUntilMessageDetected(detectMessage, token).ConfigureAwait(false);
        }
        protected async Task<Memory<byte>> ReceiveUntilTerminatorPattern(ReadOnlyMemory<byte> terminatorPattern, CancellationToken token)
        {
            return await ReceiveUntilMessageDetected((readBytes) =>
            {
                int terminatorIndex = readBytes.ToArray().Locate(terminatorPattern.ToArray()).FirstOrDefault();
                int payloadLength = terminatorIndex < 0 ? 0 : terminatorIndex;
                return payloadLength;
            }, token).ConfigureAwait(false);
        }
        protected async Task<Memory<byte>> ReceiveUntilTerminator(char terminator, CancellationToken token)
        {
            return await ReceiveUntilMessageDetected((readBytes) =>
            {
                int terminatorIndex = readBytes.Span.IndexOf((byte)terminator);
                int payloadLength = terminatorIndex < 0 ? 0 : terminatorIndex;
                return payloadLength;
            }, token).ConfigureAwait(false);
        }
    }
}
