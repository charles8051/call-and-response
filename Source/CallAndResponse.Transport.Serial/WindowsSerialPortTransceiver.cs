using System;
using System.Collections.Generic;
using System.Text;
using System.IO.Ports;
using System.Threading.Tasks;
using System.Threading;

namespace CallAndResponse.Transport.Serial
{
    //public class WindowsSerialPortTransceiver : SerialPortTransceiver
    //{
    //    public ushort Vid { get; }
    //    public ushort Pid { get; }

    //    public static WindowsSerialPortTransceiver CreateCP210xTransceiver(int baudRate = 115200, Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One)
    //    {
    //        return new WindowsSerialPortTransceiver(0x10c4, 0xea60, baudRate, parity, dataBits, stopBits);
    //    }
        
    //    public WindowsSerialPortTransceiver(ushort vid, ushort pid, int baudRate = 115200, Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One)
    //        : base("", baudRate, parity, dataBits, stopBits)
    //    {
    //        Vid = vid;
    //        Pid = pid;
    //    }

    //    public override async Task Open(CancellationToken token)
    //    {
    //        if(_serialPort != null)
    //        {
    //            _serialPort.Close();
    //        }

    //        await Task.Run(() =>
    //        {
    //            string? portName = SerialPortUtils.FindPortNameById(Vid, Pid);
    //            if (portName is null)
    //            {
    //                throw new SystemException("Device not found");
    //            }
    //            _serialPort = new SerialPort(portName, _baudRate, _parity, _dataBits, _stopBits);
    //            _serialPort.PortName = portName;
    //        });

    //        await base.Open(token);
    //    }
    //}
}
