using CallAndResponse;
using CallAndResponse.Protocol.Modbus;
using CallAndResponse.Transport.Serial;
using CallAndResponse.Transport.Ble;
using CallAndResponse.Protocol.Stm32Bootloader;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

var logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(theme: SystemConsoleTheme.Literate,
        restrictedToMinimumLevel: LogEventLevel.Information,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] ({SourceContext}.{Method}) {Message}{NewLine}{Exception}")
    .CreateLogger();

var anotherLogger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(theme: SystemConsoleTheme.Colored,
        restrictedToMinimumLevel: LogEventLevel.Information,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] ({SourceContext}.{Method}) {Message}{NewLine}{Exception}")
    .CreateLogger();

//var transceiver = new BleNordicUartTransceiver(anotherLogger);
//var transceiver = new TransceiverProvider().CreateSerialTransceiver("COM10");
//var transceiver = new TransceiverProvider().CreateBleTransceiver();
//var transceiver = new TransceiverFactory().CreateWindowsSerialTransceiver(0x0000, 0x0000);
//var transceiver = new TransceiverFactory().CreateBleTransceiver();
//var client = new ModbusRtuClient(transceiver);
var transceiver = new WindowsSerialPortTransceiver(0x10c4, 0xea60, parity: System.IO.Ports.Parity.Even);
var client = new Stm32BootloaderClient(transceiver);

using (var cts = new CancellationTokenSource(5000))
{
    try
    {
        //await transceiver.Open(cts.Token);
        await client.Open(cts.Token);
    } catch(Exception e)
    {
        Console.WriteLine(e.Message);
    }
}


//while (true)
//{
//    await Task.Delay(500);
//    try
//    {
//        using (var cts = new CancellationTokenSource(250))
//        {
//            var result = await transceiver.SendReceivePerfectMatch(new byte[] { 0xAA }, new byte[] { 0xAA }, cts.Token);
//            Console.WriteLine(ToReadableByteArray(result.ToArray()));
//        }
//    }
//    catch (Exception e)
//    {
//        Console.WriteLine(e.Message);
//    }
//}

using (var cts = new CancellationTokenSource(5000))
{
    try
    {
        await client.Ping(cts.Token);
        await Task.Delay(500);
        var id = await client.GetId(cts.Token);
        Console.WriteLine($"Id: {id}");
        await Task.Delay(500);
        var data = await client.ReadMemory(0x08000000, 16, cts.Token);
        Console.WriteLine(ToReadableByteArray(data.ToArray()));
    }
    catch (Exception e)
    {
        Console.WriteLine(e.Message);
    }
}

//var supportedCommands = await client.GetSupportedCommands();
//await client.WriteFlash(0x08000000, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });
await Task.Delay(100);
//while (true)
//{
//    var delayTask = Task.Delay(1000);
//    using (var cts = new CancellationTokenSource(1000))
//    {
//        try
//        {
//            //var receivedBytes = await transceiver.SendReceive(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 }, 6, cts.Token);
//            //await client.ReadHoldingRegisters(0x01, 1000, numRegisters: 4, cts.Token);
//            //Console.WriteLine(ToReadableByteArray(receivedBytes.ToArray()));
//        }
//        catch (Exception e)
//        {
//            Console.WriteLine(e.Message);
//        }
//    }
//    await delayTask;
//}

//static string ToReadableByteArray(byte[] bytes)
//{
//    // Do this except display hex

//    return string.Join(", ", bytes);
//}

static string ToReadableByteArray(byte[] bytes)
{
    return string.Join(", ", bytes.Select(b => b.ToString("X2")));
}