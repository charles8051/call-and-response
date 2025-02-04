using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System.Diagnostics;
using Serilog;
using Serilog.Events;

namespace CallAndResponse
{
    public abstract class Transceiver : ITransceiver
    {
        public abstract bool IsOpen { get; protected set; }
        public abstract Task Open(CancellationToken token);
        public abstract Task Close(CancellationToken token);
        public abstract Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token);
        public abstract Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, (int, int)> detectMessage, CancellationToken token);

        protected ILogger Logger { get; set; } = new LoggerConfiguration().CreateLogger();

        public Transceiver()
        {
        }
        public Transceiver(ILogger logger)
        {
            Logger = logger;
        }

        #region Default Implementations

        public async Task<string> SendReceive(string writeString, char terminator, CancellationToken token)
        {
            Logger.Verbose("Sending [{@writeBytes}]", writeString);
            await Send(Encoding.ASCII.GetBytes(writeString), token).ConfigureAwait(false);
            var payloadBytes = await ReceiveUntilTerminator(terminator, token).ConfigureAwait(false);
            var payloadString = Encoding.ASCII.GetString(payloadBytes.ToArray());
            Logger.Verbose("Received [{@Payload}]", payloadString);
            return payloadString;
        }
        public async Task<string> SendReceive(string writeString, string terminatorString, CancellationToken token)
        {
            Logger.Verbose("Sending [{@writeBytes}]", writeString);
            await Send(Encoding.ASCII.GetBytes(writeString), token).ConfigureAwait(false);
            var payloadBytes = await ReceiveUntilTerminatorPattern(Encoding.ASCII.GetBytes(terminatorString), token).ConfigureAwait(false);
            var payloadString = Encoding.ASCII.GetString(payloadBytes.ToArray());
            Logger.Verbose("Received [{@Payload}]", payloadString);
            return payloadString;
        }
        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, int numBytesExpected, CancellationToken token)
        {
            var byteStrings = writeBytes.ToArray().Select(b => $"{b:X2}").ToArray();
            var readable = string.Join(",", byteStrings);
            Logger.Verbose("Sending [{@WriteBytes}]", readable);
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveExactly(numBytesExpected, token).ConfigureAwait(false);
            Logger.Verbose("Received [{@Payload}]", string.Join(",", payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }
        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> terminatorPattern, CancellationToken token)
        {
            var readable = writeBytes.ToArray().Select(b => $"{b:X2}").ToArray();
            Logger.Verbose("Sending [{@writeBytes}]", string.Join(",", readable));
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveUntilTerminatorPattern(terminatorPattern, token).ConfigureAwait(false);
            Logger.Verbose("Received [{@Payload}]", string.Join(",", payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }
        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, Func<ReadOnlyMemory<byte>, (int,int)> detectMessage, CancellationToken token)
        {
            Logger.Verbose("Sending [{@writeBytes}]", string.Join(",", writeBytes));
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveMessage(detectMessage, token).ConfigureAwait(false);
            Logger.Verbose("Received [{@Payload}]", string.Join(",", payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }

        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> footer, CancellationToken token)
        {
            Logger.Verbose("Sending [{@writeBytes}]", string.Join(",", writeBytes.ToArray().Select(b => $"{b:X}").ToArray()));
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveUntilHeaderFooterMatch(header, footer, token).ConfigureAwait(false);
            Logger.Verbose("Received [{@Payload}]", string.Join(",", payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }

        public async Task<Memory<byte>> ReceiveUntilTerminatorPattern(ReadOnlyMemory<byte> terminatorPattern, CancellationToken token)
        {
            Logger.Verbose("Receiving until [{@terminatorPattern}]", string.Join(",", terminatorPattern));
            var message = await ReceiveMessage((readBytes) =>
            {
                int terminatorIndex = readBytes.ToArray().Locate(terminatorPattern.ToArray()).FirstOrDefault();
                int payloadLength = terminatorIndex < 0 ? 0 : terminatorIndex;
                return (0, payloadLength);
            }, token).ConfigureAwait(false);
            Logger.Verbose("Received [{@message}]", string.Join(",", message));
            return message;
        }
        public async Task<Memory<byte>> ReceiveUntilHeaderFooterMatch(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> footer, CancellationToken token)
        {
            Logger.Verbose("Receiving until [{@header}] and [{@footer}]", string.Join(",", header), string.Join(",", footer));
            var message = await ReceiveMessage((readBytes) =>
            {
                int headerIndex = -1;
                int footerIndex = -1;   

                headerIndex = readBytes.ToArray().Locate(header.ToArray()).FirstOrDefault();
                footerIndex = readBytes.ToArray().Locate(footer.ToArray()).FirstOrDefault();

                if(headerIndex < 0 || footerIndex < 0)
                {
                    return (0, 0);
                } else
                {
                    //var payloadLength = readBytes.Length - header.Length - footer.Length;
                    // Use indices to calculate payload length instead
                    var payloadLength = footerIndex - headerIndex - header.Length;
                    return (headerIndex + header.Length, payloadLength);
                }

                
            }, token).ConfigureAwait(false);
            Logger.Verbose("Received [{@message}]", string.Join(",", message));
            return message;
        }
        public async Task<Memory<byte>> ReceiveUntilTerminator(char terminator, CancellationToken token)
        {
            return await ReceiveMessage((readBytes) =>
            {
                int terminatorIndex = readBytes.Span.IndexOf((byte)terminator);
                int payloadLength = terminatorIndex < 0 ? 0 : terminatorIndex;
                return (0, payloadLength);
            }, token).ConfigureAwait(false);
        }
        public async Task<Memory<byte>> ReceiveExactly(int numBytesExpected, CancellationToken token)
        {
            return await ReceiveMessage((readBytes) =>
            {
                if (readBytes.Length >= numBytesExpected)
                {
                    return (0, numBytesExpected);
                }
                return (0, 0);
            }, token).ConfigureAwait(false);
        }


        #endregion
    }
}
