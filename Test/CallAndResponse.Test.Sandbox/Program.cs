﻿#define LOG_TRANSCEIVER_INFO
using CallAndResponse;
using CallAndResponse.Protocol.Modbus;
using CallAndResponse.Transport.Serial;

var transceiver = WindowsSerialPortTransceiver.CreateCP210xTransceiver();
using (var cts = new CancellationTokenSource(5000))
{
    try
    {
        await transceiver.Open(cts.Token);
    } catch(Exception e)
    {
        Console.WriteLine(e.Message);
    }
}

var modbusClient = new ModbusRtuClient(transceiver);
while (true)
{
    var delayTask = Task.Delay(1000);
    using (var cts = new CancellationTokenSource(250))
    {
        try
        {
            var receivedBytes = await transceiver.SendReceive(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 }, 6, cts.Token);
            Console.WriteLine(ToReadableByteArray(receivedBytes.ToArray()));
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