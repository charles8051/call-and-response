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

    // TODO: refactor to return headers and footers with the payload??
    // TODO: provide an API to receive spontaneous messages. Will need to provide some events. Primary use case in mind is a COM port barcode scanner that can spit out data at any moment
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
            if (logger is not null) Logger = logger;
        }

        #region Default Implementations

        public async Task<string> SendReceiveString(string writeString, char terminator, CancellationToken token)
        {
            Logger.Debug("Sending [{@writeBytes}]", writeString);
            await Send(Encoding.ASCII.GetBytes(writeString), token).ConfigureAwait(false);
            var payloadBytes = await ReceiveUntilTerminator(terminator, token).ConfigureAwait(false);
            var payloadString = Encoding.ASCII.GetString(payloadBytes.ToArray());
            Logger.Debug("Received payload [{@Payload}]", payloadString);
            return payloadString;
        }
        public async Task<string> SendReceiveString(string writeString, string terminatorString, CancellationToken token)
        {
            Logger.Debug("Sending [{@writeBytes}]", writeString);
            await Send(Encoding.ASCII.GetBytes(writeString), token).ConfigureAwait(false);
            var payloadBytes = await ReceiveUntilTerminatorPattern(Encoding.ASCII.GetBytes(terminatorString), token).ConfigureAwait(false);
            var payloadString = Encoding.ASCII.GetString(payloadBytes.ToArray());
            Logger.Debug("Received payload [{@Payload}]", payloadString);
            return payloadString;
        }
        public async Task<Memory<byte>> SendReceiveExactly(ReadOnlyMemory<byte> writeBytes, int numBytesExpected, CancellationToken token)
        {
            var byteStrings = writeBytes.ToArray().Select(b => $"{b:X2}").ToArray();
            var readable = string.Join(",", byteStrings);
            Logger.Debug("Sending [{@WriteBytes}]", readable);
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveExactly(numBytesExpected, token).ConfigureAwait(false);
            Logger.Debug("Received payload [{@Payload}]", string.Join(",", payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }

        // TODO: Add overload to match a string response
        public async Task<Memory<byte>> SendReceivePerfectMatch(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> matchBytes, CancellationToken token)
        {
            Logger.Debug("Sending [{@writeBytes}]", string.Join(",", writeBytes.ToArray().Select(b => $"{b:X}").ToArray()));
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveUntilPerfectMatch(matchBytes, token).ConfigureAwait(false);
            Logger.Debug("Received payload [{@Payload}]", string.Join(",", payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }

        public async Task<Memory<byte>> SendReceiveFooter(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> terminatorPattern, CancellationToken token)
        {
            var readable = writeBytes.ToArray().Select(b => $"{b:X2}").ToArray();
            Logger.Debug("Sending [{@writeBytes}]", string.Join(",", readable));
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveUntilTerminatorPattern(terminatorPattern, token).ConfigureAwait(false);
            Logger.Debug("Received payload [{@Payload}]", string.Join(",", payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }
        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, Func<ReadOnlyMemory<byte>, (int,int)> detectMessage, CancellationToken token)
        {
            Logger.Debug("Sending [{@writeBytes}]", string.Join(",", writeBytes));
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveMessage(detectMessage, token).ConfigureAwait(false);
            Logger.Debug("Received payload [{@Payload}]", string.Join(",", payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }

        public async Task<Memory<byte>> SendReceiveHeaderFooter(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> footer, CancellationToken token)
        {
            Logger.Debug("Sending [{@writeBytes}]", string.Join(",", writeBytes.ToArray().Select(b => $"{b:X}").ToArray()));
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveUntilHeaderFooterMatch(header, footer, token).ConfigureAwait(false);
            Logger.Debug("Received payload [{@Payload}]", string.Join(",", payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }

        public async Task<Memory<byte>> ReceiveUntilTerminatorPattern(ReadOnlyMemory<byte> terminatorPattern, CancellationToken token)
        {
            Logger.Verbose("Receiving until [{@terminatorPattern}]", string.Join(",", terminatorPattern));
            var message = await ReceiveMessage((readBytes) =>
            {
                int terminatorIndex = readBytes.Span.IndexOf(terminatorPattern.Span);
                int payloadLength = terminatorIndex < 0 ? 0 : terminatorIndex;
                return (0, payloadLength);
            }, token).ConfigureAwait(false);
            Logger.Verbose("Received raw [{@message}]", string.Join(",", message.ToArray().Select(b => $"{b:X}").ToArray()));
            return message;
        }
        public async Task<Memory<byte>> ReceiveUntilHeaderFooterMatch(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> footer, CancellationToken token)
        {
            Logger.Verbose("Receiving until [{@header}] and [{@footer}]", string.Join(",", header), string.Join(",", footer));
            var message = await ReceiveMessage((readBytes) =>
            {
                int headerIndex = -1;
                int footerIndex = -1;
                int offsetFooterIndex = -1;

                headerIndex = readBytes.Span.IndexOf(header.Span);
                offsetFooterIndex = readBytes.Span.IndexOf(footer.Span);
                footerIndex = offsetFooterIndex < 0 ? -1 : headerIndex + header.Length + offsetFooterIndex;

                if (headerIndex < 0 || footerIndex < 0)
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
            Logger.Verbose("Received raw [{@message}]", string.Join(",", message.ToArray().Select(b => $"{b:X}").ToArray()));
            return message;
        }

        public async Task<Memory<byte>> ReceiveUntilPerfectMatch(ReadOnlyMemory<byte> matchBytes, CancellationToken token)
        {
            Logger.Verbose("Receiving until match [{@matchBytes}]", string.Join(",", matchBytes.ToArray().Select(b => $"{b:X}").ToArray()));
            var message = await ReceiveMessage((readBytes) =>
            {
                int matchIndex = -1;
                matchIndex = readBytes.Span.IndexOf(matchBytes.Span);
                return matchIndex >= 0 ? (matchIndex, matchBytes.Length) : (matchIndex, -1);
            }, token).ConfigureAwait(false);
            Logger.Verbose("Received raw [{@message}]", string.Join(",", message.ToArray().Select(b => $"{b:X}").ToArray()));
            return message;
        }

        public async Task<Memory<byte>> ReceiveUntilTerminator(char terminator, CancellationToken token)
        {
            Logger.Verbose("Receiving until terminator [{@matchBytes}]", terminator.ToString());
            var message = await ReceiveMessage((readBytes) =>
            {
                int terminatorIndex = readBytes.Span.IndexOf((byte)terminator);
                int payloadLength = terminatorIndex < 0 ? 0 : terminatorIndex;
                return (0, payloadLength);
            }, token).ConfigureAwait(false);
            Logger.Verbose("Received raw [{@message}]", string.Join(",", message.ToArray().Select(b => $"{b:X}").ToArray()));
            return message;
        }
        public async Task<Memory<byte>> ReceiveExactly(int numBytesExpected, CancellationToken token)
        {
            Logger.Verbose("Receiving until [{@matchBytes}] bytes received", numBytesExpected);
            var message = await ReceiveMessage((readBytes) =>
            {
                if (readBytes.Length >= numBytesExpected)
                {
                    return (0, numBytesExpected);
                }
                return (0, 0);
            }, token).ConfigureAwait(false);
            Logger.Verbose("Received raw [{@message}]", string.Join(",", message.ToArray().Select(b => $"{b:X}").ToArray()));
            return message;
        }
        #endregion
    }
}
