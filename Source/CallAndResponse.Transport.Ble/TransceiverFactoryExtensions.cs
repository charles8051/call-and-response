using System;
using System.Collections.Generic;
using System.Text;

namespace CallAndResponse.Transport.Ble
{
    public static class TransceiverFactoryExtensions
    {
        public static ITransceiver CreateBleTransceiver(this TransceiverFactory factory)
        {
            return new BleNordicUartTransceiver();
        }
    }
}
