using System;
using System.Collections.Generic;
using System.Text;

namespace CallAndResponse.Transport.Ble
{
    public static class TransceiverBuilderExtensions
    {
        public static ITransceiver CreateBleTransceiver(this TransceiverBuilder factory)
        {
            return new BleNordicUartTransceiver();
        }
    }
}
