using System;
using System.Collections.Generic;
using System.Text;

namespace CallAndResponse.Transport.Serial
{
    public static class TransceiverFactoryExtensions
    {
        public static ITransceiver CreateSerialTransceiver(this TransceiverFactory factory, string portName)
        {
            return new SerialPortTransceiver(portName);
        }

        public static ITransceiver CreateWindowsSerialTransceiver(this TransceiverFactory factory, ushort vid, ushort pid)
        {
            return new WindowsSerialPortTransceiver(vid, pid);
        }
    }
}
