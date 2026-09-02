using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CallAndResponse
{
    /// <summary>
    /// Convenience extension methods for <see cref="ITransceiver"/>.
    /// Each method composes <see cref="ITransceiver.Send"/> and/or
    /// <see cref="ITransceiver.ReceiveMessage"/> with a built-in frame detection
    /// strategy, covering the most common framing patterns without requiring the
    /// caller to write a detection delegate.
    /// </summary>
    public static class TransceiverExtensions
    {
        /// <summary>
        /// Send an ASCII string and receive until a single-character terminator is found.
        /// Returns the payload as an ASCII string, excluding the terminator.
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="writeString">The ASCII string to send.</param>
        /// <param name="terminator">The character that marks the end of the response.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The response as an ASCII string, excluding the terminator.</returns>
        public static async Task<string> SendReceiveString(this ITransceiver transceiver, string writeString, char terminator, CancellationToken token)
        {
            await transceiver.Send(Encoding.ASCII.GetBytes(writeString), token).ConfigureAwait(false);
            var payloadBytes = await transceiver.ReceiveUntilTerminator(terminator, token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(payloadBytes.ToArray());
        }

        /// <summary>
        /// Send an ASCII string and receive until a multi-character terminator pattern is found.
        /// Returns the payload as an ASCII string, excluding the terminator pattern.
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="writeString">The ASCII string to send.</param>
        /// <param name="terminatorString">The string pattern that marks the end of the response (e.g., "\r\n").</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The response as an ASCII string, excluding the terminator pattern.</returns>
        public static async Task<string> SendReceiveString(this ITransceiver transceiver, string writeString, string terminatorString, CancellationToken token)
        {
            await transceiver.Send(Encoding.ASCII.GetBytes(writeString), token).ConfigureAwait(false);
            var payloadBytes = await transceiver.ReceiveUntilTerminatorPattern(Encoding.ASCII.GetBytes(terminatorString), token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(payloadBytes.ToArray());
        }

        /// <summary>
        /// Send a frame and receive exactly <paramref name="numBytesExpected"/> bytes in response.
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="writeBytes">The bytes to send.</param>
        /// <param name="numBytesExpected">The exact number of response bytes to wait for.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Exactly <paramref name="numBytesExpected"/> bytes.</returns>
        public static async Task<Memory<byte>> SendReceiveExactly(this ITransceiver transceiver, ReadOnlyMemory<byte> writeBytes, int numBytesExpected, CancellationToken token)
        {
            await transceiver.Send(writeBytes, token).ConfigureAwait(false);
            return await transceiver.ReceiveExactly(numBytesExpected, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Send a frame and receive until an exact byte sequence is found in the response.
        /// Returns the matched bytes.
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="writeBytes">The bytes to send.</param>
        /// <param name="matchBytes">The exact byte sequence to scan for.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The matched bytes (same content as <paramref name="matchBytes"/>).</returns>
        public static async Task<Memory<byte>> SendReceivePerfectMatch(this ITransceiver transceiver, ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> matchBytes, CancellationToken token)
        {
            await transceiver.Send(writeBytes, token).ConfigureAwait(false);
            return await transceiver.ReceiveUntilPerfectMatch(matchBytes, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Send a frame and receive until a footer byte pattern is found.
        /// Returns the bytes before the footer, excluding the footer itself.
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="writeBytes">The bytes to send.</param>
        /// <param name="terminatorPattern">The byte pattern that marks the end of the response.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>All bytes received before the footer pattern.</returns>
        public static async Task<Memory<byte>> SendReceiveFooter(this ITransceiver transceiver, ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> terminatorPattern, CancellationToken token)
        {
            await transceiver.Send(writeBytes, token).ConfigureAwait(false);
            return await transceiver.ReceiveUntilTerminatorPattern(terminatorPattern, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Send a frame and receive using a custom frame detection function.
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="writeBytes">The bytes to send.</param>
        /// <param name="detectMessage">
        /// A function that inspects the accumulated buffer and returns
        /// <see cref="FrameDetectionResult.Incomplete"/> or
        /// <see cref="FrameDetectionResult.Complete(int, int)"/>.
        /// </param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The detected payload bytes.</returns>
        public static async Task<Memory<byte>> SendReceive(this ITransceiver transceiver, ReadOnlyMemory<byte> writeBytes, Func<ReadOnlyMemory<byte>, FrameDetectionResult> detectMessage, CancellationToken token)
        {
            await transceiver.Send(writeBytes, token).ConfigureAwait(false);
            return await transceiver.ReceiveMessage(detectMessage, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Send a frame and receive until both a header and footer byte pattern are found.
        /// Returns the bytes between the header and footer, excluding both.
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="writeBytes">The bytes to send.</param>
        /// <param name="header">The byte pattern marking the start of the payload.</param>
        /// <param name="footer">The byte pattern marking the end of the payload.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The payload bytes between header and footer.</returns>
        public static async Task<Memory<byte>> SendReceiveHeaderFooter(this ITransceiver transceiver, ReadOnlyMemory<byte> writeBytes, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> footer, CancellationToken token)
        {
            await transceiver.Send(writeBytes, token).ConfigureAwait(false);
            return await transceiver.ReceiveUntilHeaderFooterMatch(header, footer, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Receive bytes until a multi-byte terminator pattern is found.
        /// Returns all bytes before the pattern, excluding the pattern itself.
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="terminatorPattern">The byte sequence that marks the end of the message.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>All bytes received before the terminator pattern.</returns>
        public static Task<Memory<byte>> ReceiveUntilTerminatorPattern(this ITransceiver transceiver, ReadOnlyMemory<byte> terminatorPattern, CancellationToken token)
        {
            return transceiver.ReceiveMessage((readBytes) =>
            {
                int terminatorIndex = readBytes.Span.IndexOf(terminatorPattern.Span);
                return terminatorIndex < 0
                    ? FrameDetectionResult.Incomplete
                    : FrameDetectionResult.Complete(0, terminatorIndex, terminatorIndex + terminatorPattern.Length);
            }, token);
        }

        /// <summary>
        /// Receive bytes until both a header and footer pattern are found.
        /// Returns the bytes between them, excluding both header and footer.
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="header">The byte pattern marking the start of the payload.</param>
        /// <param name="footer">The byte pattern marking the end of the payload.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The payload bytes between header and footer.</returns>
        public static Task<Memory<byte>> ReceiveUntilHeaderFooterMatch(this ITransceiver transceiver, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> footer, CancellationToken token)
        {
            return transceiver.ReceiveMessage((readBytes) =>
            {
                int headerIndex = readBytes.Span.IndexOf(header.Span);
                int footerIndex = -1;

                if (headerIndex >= 0)
                {
                    var afterHeader = readBytes.Slice(headerIndex + header.Length);
                    int footerRelativeIndex = afterHeader.Span.IndexOf(footer.Span);
                    if (footerRelativeIndex >= 0)
                    {
                        footerIndex = headerIndex + header.Length + footerRelativeIndex;
                    }
                }

                if (headerIndex < 0 || footerIndex < 0)
                {
                    return FrameDetectionResult.Incomplete;
                }
                else
                {
                    var payloadLength = footerIndex - headerIndex - header.Length;
                    return FrameDetectionResult.Complete(headerIndex + header.Length, payloadLength, footerIndex + footer.Length);
                }
            }, token);
        }

        /// <summary>
        /// Receive bytes until an exact byte sequence is found in the accumulated buffer.
        /// Returns the matched bytes (same content as <paramref name="matchBytes"/>).
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="matchBytes">The exact byte sequence to scan for.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The matched bytes.</returns>
        public static Task<Memory<byte>> ReceiveUntilPerfectMatch(this ITransceiver transceiver, ReadOnlyMemory<byte> matchBytes, CancellationToken token)
        {
            return transceiver.ReceiveMessage((readBytes) =>
            {
                int matchIndex = readBytes.Span.IndexOf(matchBytes.Span);
                return matchIndex >= 0
                    ? FrameDetectionResult.Complete(matchIndex, matchBytes.Length)
                    : FrameDetectionResult.Incomplete;
            }, token);
        }

        /// <summary>
        /// Receive bytes until a single-character terminator is found.
        /// Returns all bytes before the terminator, excluding the terminator itself.
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="terminator">The ASCII character that marks the end of the message.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>All bytes received before the terminator.</returns>
        public static Task<Memory<byte>> ReceiveUntilTerminator(this ITransceiver transceiver, char terminator, CancellationToken token)
        {
            return transceiver.ReceiveMessage((readBytes) =>
            {
                int terminatorIndex = readBytes.Span.IndexOf((byte)terminator);
                return terminatorIndex < 0
                    ? FrameDetectionResult.Incomplete
                    : FrameDetectionResult.Complete(0, terminatorIndex, terminatorIndex + 1);
            }, token);
        }

        /// <summary>
        /// Receive exactly <paramref name="numBytesExpected"/> bytes from the transport.
        /// </summary>
        /// <param name="transceiver">The transceiver to use.</param>
        /// <param name="numBytesExpected">The exact number of bytes to wait for.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Exactly <paramref name="numBytesExpected"/> bytes.</returns>
        public static Task<Memory<byte>> ReceiveExactly(this ITransceiver transceiver, int numBytesExpected, CancellationToken token)
        {
            return transceiver.ReceiveMessage((readBytes) =>
            {
                return readBytes.Length >= numBytesExpected
                    ? FrameDetectionResult.Complete(0, numBytesExpected)
                    : FrameDetectionResult.Incomplete;
            }, token);
        }
    }
}
