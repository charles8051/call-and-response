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
        public abstract bool IsConnected { get; }
        public abstract Task Open(CancellationToken token = default);
        public abstract Task Close(CancellationToken token = default);
        public abstract Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token = default);
        public abstract Task<Memory<byte>> ReceiveExactly(int numBytesExpected, CancellationToken token = default);
        public abstract Task<Memory<byte>> ReceiveUntilMessageDetected(Func<ReadOnlyMemory<byte>, int> detectMessage, CancellationToken token = default);
        

        #region Default Implementations
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

        protected ILogger logger;
        protected void CreateDefaultLogger()
        {
            logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .WriteTo.Console(theme: SystemConsoleTheme.Literate,
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] ({SourceContext}.{Method}) {Message}{NewLine}{Exception}")
                .CreateLogger();
        }

        [Conditional("LOG_TRANSCEIVER_INFO")]
        protected void LogInformation(string message, params object[] args)
        {
            logger.Information(message, args);
        }

        [Conditional("LOG_TRANSCEIVER_VERBOSE")]
        protected void LogVerbose(string message, params object[] args)
        {
            logger.Verbose(message, args);
        }

        [Conditional("LOG_TRANSCEIVER_ERROR")]
        protected void LogError(string message, params object[] args)
        {
            logger.Error(message, args);
        }

        #endregion
    }
}
