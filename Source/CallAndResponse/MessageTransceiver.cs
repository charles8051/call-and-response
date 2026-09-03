using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse.Framing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CallAndResponse
{
    /// <summary>
    /// A message channel made by binding a codec to a byte channel. Encodes on the way out, decodes
    /// on the way in, so the caller only ever handles payloads.
    /// </summary>
    public sealed class MessageTransceiver : IMessageTransceiver
    {
        private readonly ITransceiver _inner;
        private readonly IFrameCodec _codec;
        private readonly ILogger _logger;

        /// <summary>Bind <paramref name="codec"/> to <paramref name="inner"/>.</summary>
        public MessageTransceiver(ITransceiver inner, IFrameCodec codec, ILogger? logger = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc />
        public async Task SendMessage(ReadOnlyMemory<byte> payload, CancellationToken token)
        {
            var encoded = new ArrayBufferWriter<byte>(payload.Length + 8);
            _codec.Encode(payload.Span, encoded);
            await _inner.Send(encoded.WrittenMemory, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<Memory<byte>> ReceiveMessage(CancellationToken token)
            => _inner.Receive(_codec, token);
    }
}
