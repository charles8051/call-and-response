#define LOG_TRANSCEIVER_INFO
using CallAndResponse;
using CallAndResponse.Transport.Serial;
using CallAndResponse.Protocol.Modbus;
using CallAndResponse.Transport.Ble;
using System.IO.Ports;
using System.Text;

var transceiver = new SerialPortTransceiver("COM38", 115200, Parity.None, 8, StopBits.One);
//var transceiver = new BleNordicUartTransceiver();
//await transceiver.Open(CancellationToken.None);

var modbusClient = new ModbusRtuClient(transceiver);
//await modbusClient.Open();
var dummyData = new byte[230];
var terminator = new byte[] { 0xda, 0xba, 0xd0, 0x00 };
dummyData = dummyData.Concat(terminator.ToList()).ToArray();
while (true)
{
    var delayTask = Task.Delay(250);
    using (var cts = new CancellationTokenSource(250))
    {
        try
        {
            //var receivedBytes = await transceiver.SendReceive(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x84, 0x0A }, 15, cts.Token).ConfigureAwait(true);
            //var responseString = await transceiver.SendReceive("Loopback\n", '\n', cts.Token).ConfigureAwait(true);
            
            //var receivedBytes = await transceiver.SendReceive(dummyData, terminator, cts.Token).ConfigureAwait(true);
            //Console.WriteLine(ToReadableByteArray(receivedBytes.ToArray()));

            var data = await modbusClient.ReadHoldingRegisters(1, 0, numBytes: 96, cts.Token);
            //Console.WriteLine(ToReadableByteArray(receivedBytes.ToArray()));
            //Console.WriteLine($"received: {responseString})");
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