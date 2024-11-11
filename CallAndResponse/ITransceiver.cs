using System;
using System.Threading.Tasks;
using System.Threading;

namespace CallAndResponse
{
    public interface ITransceiver
    {
        Task Open();
        Task Close();
        Task<byte[]> SendReceive(byte[] writeBytes, int numBytesExpected, CancellationToken token);
        Task<byte[]> SendReceive(byte[] writeBytes, char terminator, CancellationToken token);
        Task<byte[]> SendReceive(byte[] writeBytes, byte[] pattern, CancellationToken token);
        Task<byte[]> SendReceive(byte[] writeBytes, Func<byte[], int> detectMessage, CancellationToken token);
    }
}
