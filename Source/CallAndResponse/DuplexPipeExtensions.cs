using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

namespace CallAndResponse
{
    /// <summary>
    /// Extension methods for creating an <see cref="ITransceiver"/> from pipes.
    /// </summary>
    public static class DuplexPipeExtensions
    {
        /// <summary>
        /// Wraps this <see cref="IDuplexPipe"/> in an <see cref="ITransceiver"/>.
        /// The pipe must already be active and usable for the duration of the
        /// wrapped communication session.
        /// </summary>
        /// <param name="pipe">The duplex pipe to wrap.</param>
        /// <param name="logger">Optional logger forwarded to the <see cref="Transceiver"/>.</param>
        public static ITransceiver AsTransceiver(
            this IDuplexPipe pipe,
            ILogger<Transceiver>? logger = null
        ) => new Transceiver(pipe, logger);
    }
}
