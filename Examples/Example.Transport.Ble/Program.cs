using CallAndResponse;
using CallAndResponse.Framing;
using CallAndResponse.Transport.BleNordicUart;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;

var uartServiceGuid = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
var uartRxGuid = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
var uartTxGuid = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");

var adapter = CrossBluetoothLE.Current.Adapter;

// -- 1. Scan for a device advertising the Nordic UART Service --
IDevice? device = null;
adapter.DeviceDiscovered += (s, e) =>
{
    Console.WriteLine($"Discovered: {e.Device.Name} ({e.Device.Id})");
    device = e.Device;
};

Console.WriteLine("Scanning for BLE devices...");
using var scanCts = new CancellationTokenSource(10000);
await adapter.StartScanningForDevicesAsync(
    serviceUuids: new[] { uartServiceGuid },
    cancellationToken: scanCts.Token
);

if (device is null)
{
    Console.WriteLine("No Nordic UART device found.");
    return;
}

// -- 2. Connect and discover characteristics --
await adapter.ConnectToDeviceAsync(device);
Console.WriteLine($"Connected to {device.Name}");

var service = await device.GetServiceAsync(uartServiceGuid);
var uartTx = await service.GetCharacteristicAsync(uartTxGuid);
var uartRx = await service.GetCharacteristicAsync(uartRxGuid);

// -- 3. Set up the pipe and bridging --
var pipe = new BleNordicUartPipe();

// Bridge RX notifications into the pipe
uartRx.ValueUpdated += async (s, e) =>
{
    var data = e.Characteristic.Value;
    if (data is { Length: > 0 })
    {
        await pipe.RxWriter.WriteAsync(data);
    }
};
await uartRx.StartUpdatesAsync();

// Bridge TX pipe out to the characteristic
var txCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (!txCts.IsCancellationRequested)
    {
        var result = await pipe.TxReader.ReadAsync(txCts.Token);
        foreach (var segment in result.Buffer)
        {
            await uartTx.WriteAsync(segment.ToArray());
        }
        pipe.TxReader.AdvanceTo(result.Buffer.End);
        if (result.IsCompleted)
            break;
    }
});

// -- 4. Use the transceiver --
var transceiver = new Transceiver(pipe);

using var cts = new CancellationTokenSource(5000);
try
{
    var response = await transceiver.SendReceive(
        new byte[] { 0x01, 0x02, 0x03 },
        Frame.Exactly(3),
        cts.Token
    );

    Console.WriteLine($"Response: {BitConverter.ToString(response.ToArray())}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Timed out waiting for response.");
}
finally
{
    // -- 5. Caller owns cleanup --
    txCts.Cancel();
    await uartRx.StopUpdatesAsync();
    await adapter.DisconnectDeviceAsync(device);
    Console.WriteLine("Disconnected.");
}
