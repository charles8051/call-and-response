using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System.Diagnostics;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using Serilog.Events;

namespace CallAndResponse
{
    public abstract class Transceiver : ITransceiver
    {
        public abstract bool IsOpen { get; }
        public abstract Task Open(CancellationToken token);
        public abstract Task Close(CancellationToken token);
        public abstract Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token);
        public abstract Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, int> detectMessage, CancellationToken token);

        protected ILogger _logger;

        public Transceiver()
        {
            _logger = CreateDefaultLogger();
        }
        public Transceiver(ILogger logger)
        {
            _logger = logger;
        }

        #region Default Implementations

        public async Task<string> SendReceive(string writeString, char terminator, CancellationToken token)
        {
            LogInformation("Sending [{@writeBytes}]", writeString);
            await Send(Encoding.ASCII.GetBytes(writeString), token).ConfigureAwait(false);
            var payloadBytes = await ReceiveUntilTerminator(terminator, token).ConfigureAwait(false);
            var payloadString = Encoding.ASCII.GetString(payloadBytes.ToArray());
            LogInformation("Received [{@Payload}]", payloadString);
            return payloadString;
        }
        public async Task<string> SendReceive(string writeString, string terminatorString, CancellationToken token)
        {
            LogInformation("Sending [{@writeBytes}]", writeString);
            await Send(Encoding.ASCII.GetBytes(writeString), token).ConfigureAwait(false);
            var payloadBytes = await ReceiveUntilTerminatorPattern(Encoding.ASCII.GetBytes(terminatorString), token).ConfigureAwait(false);
            var payloadString = Encoding.ASCII.GetString(payloadBytes.ToArray());
            LogInformation("Received [{@Payload}]", payloadString);
            return payloadString;
        }
        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, int numBytesExpected, CancellationToken token)
        {
            var byteStrings = writeBytes.ToArray().Select(b => $"{b:X2}").ToArray();
            var readable = string.Join(',', byteStrings);
            LogInformation("Sending [{@WriteBytes}]", readable);
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveExactly(numBytesExpected, token).ConfigureAwait(false);
            LogInformation("Received [{@Payload}]", string.Join(',', payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }
        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> terminatorPattern, CancellationToken token)
        {
            var readable = writeBytes.ToArray().Select(b => $"{b:X2}").ToArray();
            LogInformation("Sending [{@writeBytes}]", string.Join(',', readable));
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveUntilTerminatorPattern(terminatorPattern, token).ConfigureAwait(false);
            LogInformation("Received [{@Payload}]", string.Join(',', payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }
        public async Task<Memory<byte>> SendReceive(ReadOnlyMemory<byte> writeBytes, Func<ReadOnlyMemory<byte>, int> detectMessage, CancellationToken token)
        {
            LogInformation("Sending [{@writeBytes}]", string.Join(',', writeBytes));
            await Send(writeBytes, token).ConfigureAwait(false);
            var payload = await ReceiveMessage(detectMessage, token).ConfigureAwait(false);
            LogInformation("Received [{@Payload}]", string.Join(',', payload.ToArray().Select(b => $"{b:X}").ToArray()));
            return payload;
        }
        protected async Task<Memory<byte>> ReceiveUntilTerminatorPattern(ReadOnlyMemory<byte> terminatorPattern, CancellationToken token)
        {
            LogInformation("Receiving until [{@terminatorPattern}]", string.Join(',', terminatorPattern));
            var message = await ReceiveMessage((readBytes) =>
            {
                int terminatorIndex = readBytes.ToArray().Locate(terminatorPattern.ToArray()).FirstOrDefault();
                int payloadLength = terminatorIndex < 0 ? 0 : terminatorIndex;
                return payloadLength;
            }, token).ConfigureAwait(false);
            LogInformation("Received [{@message}]", string.Join(',', message));
            return message;
        }
        protected async Task<Memory<byte>> ReceiveUntilTerminator(char terminator, CancellationToken token)
        {
            return await ReceiveMessage((readBytes) =>
            {
                int terminatorIndex = readBytes.Span.IndexOf((byte)terminator);
                int payloadLength = terminatorIndex < 0 ? 0 : terminatorIndex;
                return payloadLength;
            }, token).ConfigureAwait(false);
        }
        protected async Task<Memory<byte>> ReceiveExactly(int numBytesExpected, CancellationToken token)
        {
            return await ReceiveMessage((readBytes) =>
            {
                return readBytes.Length == numBytesExpected ? numBytesExpected : 0;
            }, token).ConfigureAwait(false);
        }
        protected ILogger CreateDefaultLogger()
        {
            return new LoggerConfiguration()
                .Enrich.FromLogContext()
                .WriteTo.Console(theme: SystemConsoleTheme.Literate,
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] ({SourceContext}.{Method}) {Message}{NewLine}{Exception}")
                .CreateLogger();
        }

        [Conditional("TRACE_TRANSCEIVER")]
        protected void LogInformation(string message, params object[] args)
        {
            //_logger.Information(message, args);
        }

        [Conditional("TRACE_TRANSCEIVER")]
        protected void LogTrace(string message, params object[] args)
        {
            //_logger.Verbose(message, args);
        }

        [Conditional("TRACE_TRANSCEIVER")]
        protected void LogError(string message, params object[] args)
        {
            //_logger.Error(message, args);
        }

        #endregion
    }
}
