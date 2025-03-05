using CallAndResponse;
using CallAndResponse.Protocol.Modbus;
using CallAndResponse.Transport.Serial;
using CallAndResponse.Transport.Ble;
using CallAndResponse.Protocol.Stm32Bootloader;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using System.Reflection;
using System.Diagnostics;

var logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(theme: SystemConsoleTheme.Literate,
        restrictedToMinimumLevel: LogEventLevel.Information,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] ({SourceContext}.{Method}) {Message}{NewLine}{Exception}")
    .CreateLogger();

var transceiver = new BleNordicUartTransceiver(logger);
//var transceiver = new TransceiverProvider().CreateSerialTransceiver("COM10");
//var transceiver = new TransceiverProvider().CreateBleTransceiver();
//var transceiver = new TransceiverFactory().CreateWindowsSerialTransceiver(0x0000, 0x0000);
//var transceiver = new TransceiverFactory().CreateBleTransceiver();
//var transceiver = new WindowsSerialPortTransceiver(0x10c4, 0xea60, parity: System.IO.Ports.Parity.Even);
//var client = new ModbusRtuClient(transceiver);
//var client = new Stm32BootloaderClient(transceiver);

using (var cts = new CancellationTokenSource(15000))
{
    try
    {
        await transceiver.Open(cts.Token);
        Console.WriteLine("Connected");
        //await client.Open();
        //await client.Ping();
        //await Task.Delay(2000);
        //await client.ExtendedEraseMemoryPages(0x2c, cts.Token);

        for(byte i = 0; i < 100; i++)
        {
            var sw = new Stopwatch();
            sw.Start();
            try
            {
                var dummyData = new byte[512];
                //for (byte j = 0; j < dummyData.Length; j++)
                //{
                //    dummyData[j] = j;
                //}
                //dummyData[63] = 0xff;
                //dummyData[0] = i;
                // set every byte of dummydata to i
                //for (byte j = 1; j < dummyData.Length; j++)
                //{
                //    dummyData[j] = i;
                //}
                using var sendCts = new CancellationTokenSource(120);
                var data = await transceiver.SendReceiveExactly(dummyData, dummyData.Length, sendCts.Token);
                //var waitWatch = Stopwatch.StartNew();
                //await transceiver.Send(dummyData, sendCts.Token);

                //await Task.Delay(1);
                //while (waitWatch.ElapsedMilliseconds < 10)
                //{
                //}

                //Console.WriteLine("Received" + ToReadableByteArray(data.ToArray()) + " in " + sw.ElapsedMilliseconds + "ms");
            }
            catch (Exception e)
            {
                Console.WriteLine("failed: " + e.Message);
            }
            sw.Stop();
            Console.WriteLine("Sent " + i + " in " + sw.ElapsedMilliseconds + "ms");
        }

        await Task.Delay(500);
        await transceiver.Close();
        //while (true)
        //{
        //    try
        //    {
        //        var data = await transceiver.SendReceiveExactly(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x84, 0x0A }, 8, cts.Token);
        //        Console.WriteLine("Received" + ToReadableByteArray(data.ToArray()));
        //    } catch( Exception e)
        //    {
        //        Console.WriteLine("failed: " + e.Message);
        //    }
        //    await Task.Delay(500);
        //}
        
        //await Task.Delay(10000);
    }
    catch (Exception e)
    {
        Console.WriteLine("ahh: " + e.Message);
    }
}

static string ToReadableByteArray(byte[] bytes)
{
    return string.Join(", ", bytes.Select(b => b.ToString("X2")));
}