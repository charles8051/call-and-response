using CallAndResponse;
using CallAndResponse.Serial;
using CallAndResponse.Modbus;
using System.IO.Ports;
using System.Text;

var transceiver = new SerialPortTransceiver("COM38", 115200, Parity.None, 8, StopBits.One);
//await transceiver.Open();

var modbussy = new ModbusRtuClient(transceiver);
await modbussy.Open();

while (true)
{
    var delayTask = Task.Delay(250);
    using (var cts = new CancellationTokenSource(250))
    {
        try
        {
            //var receivedBytes = await transceiver.SendReceive(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x84, 0x0A }, 15, cts.Token).ConfigureAwait(true);
            //var receivedBytes = await transceiver.SendReceive(new byte[] { 0x01, 0x03, (byte)'\n' }, '\n', cts.Token).ConfigureAwait(true);
            //var receivedBytes = await transceiver.SendReceive(new ReadOnlyMemory<byte>([0x01, 0x03, 0x04, 0x07, 0xda, 0xba, 0xd0, 0x00]), new ReadOnlyMemory<byte>([0xda, 0xba, 0xd0, 0x00]), cts.Token).ConfigureAwait(true);
            //Console.WriteLine(ToReadableByteArray(receivedBytes.ToArray()));

            var data = await modbussy.ReadHoldingRegistersAsync(4, 0, 96, cts.Token);
            //Console.WriteLine(ToReadableByteArray(data.ToArray()));
            Console.WriteLine(Encoding.ASCII.GetString(data.ToArray()));
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }
    }
    await delayTask;
}

static string ToReadableByteArray(byte[] bytes)
{
    return string.Join(", ", bytes);
}