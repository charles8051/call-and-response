using CallAndResponse;
using CallAndResponse.Protocol.Modbus;
using CallAndResponse.Transport.Serial;
using CallAndResponse.Transport.Ble;
using CallAndResponse.Protocol.Stm32Bootloader;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using System.Reflection;

var logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(theme: SystemConsoleTheme.Literate,
        restrictedToMinimumLevel: LogEventLevel.Information,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] ({SourceContext}.{Method}) {Message}{NewLine}{Exception}")
    .CreateLogger();

//var transceiver = new BleNordicUartTransceiver(anotherLogger);
//var transceiver = new TransceiverProvider().CreateSerialTransceiver("COM10");
//var transceiver = new TransceiverProvider().CreateBleTransceiver();
//var transceiver = new TransceiverFactory().CreateWindowsSerialTransceiver(0x0000, 0x0000);
//var transceiver = new TransceiverFactory().CreateBleTransceiver();
var transceiver = new WindowsSerialPortTransceiver(0x10c4, 0xea60, parity: System.IO.Ports.Parity.Even);
//var client = new ModbusRtuClient(transceiver);
//var client = new Stm32BootloaderClient(transceiver);

using (var cts = new CancellationTokenSource(5000))
{
    try
    {
        await transceiver.Open(cts.Token);
    }
    catch (Exception e)
    {
        Console.WriteLine(e.Message);
    }
}

static string ToReadableByteArray(byte[] bytes)
{
    return string.Join(", ", bytes.Select(b => b.ToString("X2")));
}