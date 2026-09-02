using System;

namespace CallAndResponse.Protocol.Stm32Bootloader
{
    /// <summary>
    /// Thrown when the device answers a bootloader command in a way the protocol does not allow.
    /// Examples include a reply to the sync byte that is neither ACK nor NACK — typically because
    /// the port is not an STM32 bootloader at all.
    /// </summary>
    public class Stm32BootloaderException : Exception
    {
        public Stm32BootloaderException() : base() { }
        public Stm32BootloaderException(string message) : base(message) { }
        public Stm32BootloaderException(string message, Exception innerException) : base(message, innerException) { }
    }
}
