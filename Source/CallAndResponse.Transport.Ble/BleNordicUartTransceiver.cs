using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions.Extensions;
using Plugin.BLE.Abstractions;
using System.Diagnostics;
using System.Threading;
using Serilog;
using System.Threading.Channels;
using System.Runtime.InteropServices;


namespace CallAndResponse.Transport.Ble
{
    // TODO: Add option to connect non-bonded device
    public class BleNordicUartTransceiver : Transceiver
    {
        private readonly Guid UartServiceGuid = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
        private readonly Guid UartRxGuid = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
        private readonly Guid UartTxGuid = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");

        private readonly IBluetoothLE bluetoothLE = CrossBluetoothLE.Current;
        private readonly IAdapter adapter = CrossBluetoothLE.Current.Adapter;

        private IDevice? uartDevice = null;
        private IService? uartService = null;
        private ICharacteristic? uartRx = null;
        private ICharacteristic? uartTx = null;

        private readonly int MtuRequest = 517;

        private Guid Id = Guid.Empty;
        //public Guid Id { get; private set; } = Guid.Parse("00000000-0000-0000-0000-e4b32306888e");

        // TODO never: investigate performance implications
        private static readonly int _rxBufferSize = 1024;
        private Channel<byte> rxChannel = Channel.CreateBounded<byte>(_rxBufferSize);

        public bool _isConnected = false;
        public override bool IsOpen { get { return _isConnected; } protected set { _isConnected = value; } }

        //public int ReadTimeout { get; set; } = 500;
        //public int WriteTimeout { get; set; } = 500;
        // TODO: Implement default timeouts at this layer because different transports have different timing characteristics

        public BleNordicUartTransceiver(ILogger logger)
        {
            Logger = logger;
        }
        public BleNordicUartTransceiver(Guid id) : base()
        {
            Id = id;
        }
        public BleNordicUartTransceiver()
        {
        }

        public override async Task Open(CancellationToken token = default)
        {
            try
            {
                await CrossBluetoothLE.Current.TrySetStateAsync(true);

                // If a GUID is specified, connect directly without scanning or bonding
                // Will not work for devices using RPA addresses
                if (Id != Guid.Empty)
                {
                    uartDevice = await ScanConnectDevice(Id, token);
                }
                else // then check bonded devices. Then check all devices
                {
                    var devices = adapter.BondedDevices;
                    foreach (var device in devices)
                    {
                        Logger.Information("Bonded device: {@DeviceName}", device.Name);

                        var bondedDeviceServices = await device.GetServicesAsync();
                        foreach (var service in bondedDeviceServices)
                        {
                            Logger.Information("Service: {@DeviceName}: {@ServiceId}", device.Name, service.Id);
                            if (service.Id.Equals(UartServiceGuid))
                            {
                                Logger.Information("Found device with Nordic UART Service. Id: {@DeviceId}", device.Id);
                                uartDevice = device;
                                break;
                            }
                        }
                    }
                }

                if (uartDevice is null)
                {
                    throw new TransceiverConnectionException("Device not found");    
                }
                await adapter.ConnectToKnownDeviceAsync(uartDevice.Id);
                CrossBluetoothLE.Current.Adapter.DeviceDisconnected += DeviceDisconnectedHandler;
                await Task.Delay(2000); // Let the server respond to the connection event

                var services = await uartDevice.GetServicesAsync();
                uartService = services.Where(s => s.Id == UartServiceGuid).FirstOrDefault();
                if (uartService is null) throw new TransceiverConnectionException("Service not found");

                // Double check the charactersistics inside our service
                var characteristics = await uartService.GetCharacteristicsAsync();
                uartRx = characteristics.Where(c => c.Id == UartRxGuid).FirstOrDefault();
                uartTx = characteristics.Where(c => c.Id == UartTxGuid).FirstOrDefault();
                if (uartRx is null || uartTx is null) throw new TransceiverConnectionException("Characteristics not found");

                // TODO: If android, do these things
                //if (DeviceInfo.Current.DeviceType == DeviceType.Android)
                //{
                //    uartDevice.UpdateConnectionInterval(ConnectionInterval.High);
                //    var mtuActual = await uartDevice.RequestMtuAsync(MtuRequest);
                //}

                uartTx.WriteType = CharacteristicWriteType.WithoutResponse;

                uartRx.ValueUpdated += RxNotificationHandler;
                await uartRx.StartUpdatesAsync(token);

                _isConnected = true;
            }
            catch (Exception e)
            {
                Logger.Error("Open(): " + e.Message, e);
                _isConnected = false;
                if (uartRx != null)
                {
                    uartRx.ValueUpdated -= RxNotificationHandler;
                }
                throw;
            }
            finally
            {

            }
        }

        public override async Task Close(CancellationToken token = default)
        {
            try
            {
                if (uartDevice != null)
                {
                    // use compiler directive to check if windows. If not windows, call adapter.DisconnectAsync(uartDevice)
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) is false)
                    {
                        await adapter.DisconnectDeviceAsync(uartDevice);
                    }
                    uartDevice.Dispose();
                }
            }
            catch (Exception e)
            {
                Logger.Error(e.Message);
            }
            finally
            {
                _isConnected = false;
            }
        }

        public override async Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token = default)
        {
            if (IsOpen == false)
            {
                throw new TransceiverTransportException("Cannot write while disconnected");
            }

            try
            {
                if (uartTx != null)
                {
                    var result = await uartTx.WriteAsync(writeBytes.ToArray(), token);
                    if (result == 0)
                    {
                        //await Task.Delay(3); // Add some deadtime to avoid overloading our garbage esp32 arduino code
                    } else
                    {
                        throw new TransceiverTransportException("Failed to write to BLE");
                    }
                }
            }
            catch (OperationCanceledException e) when (token.IsCancellationRequested)
            {
                Logger.Error("Send(): " + e.Message, e);
                throw;
            }
            catch (Exception e)
            {
                Logger.Error("Send(): " + e.Message, e);
                //throw;
            }
        }

        public override async Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, (int,int)> detectMessage, CancellationToken token = default)
        {
            if (IsOpen == false)
            {
                Logger.Error("Cannot read while disconnected");
                throw new TransceiverTransportException("Cannot read while disconnected");
            }

            await Clear();

            int payloadLength = 0;
            int payloadOffset = 0;
            int numBytesRead = 0;
            var data = new List<byte>();
            while (token.IsCancellationRequested is false)
            {
                token.ThrowIfCancellationRequested();
                if(numBytesRead >= _rxBufferSize) throw new TransceiverTransportException("Buffer overflow");

                data.Add(await rxChannel.Reader.ReadAsync(token));
                numBytesRead++;

                (payloadOffset, payloadLength) = detectMessage(data.ToArray());
                if (payloadLength > 0) break;
            }
            token.ThrowIfCancellationRequested();
            return data.Take(payloadLength).ToArray().AsMemory();
        }

        private async Task<IDevice> ScanConnectService()
        {
            throw new NotImplementedException();
        }

        private async Task<IDevice> ScanConnectDevice(Guid deviceGuid, CancellationToken token = default)
        {
            IDevice? device = null;

            void deviceDiscovered(object sender, DeviceEventArgs a)
            {
                Logger.Information("Device discovered: {@DeviceName}", a.Device.Name);
                if (a.Device.Id.Equals(deviceGuid))
                //if (a.Device.Name.Contains("DataFeel"))
                {
                    adapter.DeviceDiscovered -= deviceDiscovered;
                    device = a.Device;
                }
            }
            adapter.DeviceDiscovered += deviceDiscovered;
            await adapter.StartScanningForDevicesAsync(token);

            if (device is null)
            {
                throw new Exception("Device not found");
            }
            else
            {
                return device;
            }
        }

        private async Task<IDevice?> Scan(CancellationToken token = default)
        {
            var tcs = new TaskCompletionSource<IDevice?>();

            using var internalCancelSource = new CancellationTokenSource();
            using var linkedCancelSource = CancellationTokenSource.CreateLinkedTokenSource(internalCancelSource.Token, token);

            void deviceDiscovered(object sender, DeviceEventArgs args)
            {
                Logger.Information("Device Found: {@DeviceName}", args.Device.Name);
                Logger.Information("Device Availability: {DeviceAvailability}", args.Device.State);
                
                var records = args.Device.AdvertisementRecords;
                foreach (var record in records)
                {
                    if (record.Type == AdvertisementRecordType.UuidsIncomplete128Bit || record.Type == AdvertisementRecordType.UuidsComplete128Bit)
                    {
                        var hexData = record.Data.ToList().Select(x => $"{x:X}").ToArray().Reverse();


                        var guidData = record.Data.AsMemory();
                        var a = guidData.Slice(12, 4).ToArray();
                        var b = guidData.Slice(10, 2).ToArray();
                        var c = guidData.Slice(8, 2).ToArray();
                        var d0 = guidData.Slice(0, 1).ToArray().FirstOrDefault();
                        var d1 = guidData.Slice(1, 1).ToArray().FirstOrDefault();
                        var d2 = guidData.Slice(2, 1).ToArray().FirstOrDefault();
                        var d3 = guidData.Slice(3, 1).ToArray().FirstOrDefault();
                        var d4 = guidData.Slice(4, 1).ToArray().FirstOrDefault();
                        var d5 = guidData.Slice(5, 1).ToArray().FirstOrDefault();
                        var d6 = guidData.Slice(6, 1).ToArray().FirstOrDefault();
                        var d7 = guidData.Slice(7, 1).ToArray().FirstOrDefault();

                        var uuid = new Guid(BitConverter.ToUInt32(a, 0), BitConverter.ToUInt16(b, 0), BitConverter.ToUInt16(c, 0), d7, d6, d5, d4, d3, d2, d1, d0);

                        if (uuid.Equals(UartServiceGuid))
                        {
                            Logger.Information("Found device with Nordic UART Service. Id: {@DeviceId}", args.Device.Id);
                        }
                        adapter.DeviceDiscovered -= deviceDiscovered;
                        linkedCancelSource.Cancel();
                        tcs.TrySetResult(args.Device);
                    }
                }
            }

            token.Register(() =>
            {
                Logger.Information("Scan cancelled by caller");
                adapter.DeviceDiscovered -= deviceDiscovered;
                tcs.TrySetException(new OperationCanceledException());
            });

            adapter.DeviceDiscovered += deviceDiscovered;
            adapter.ScanMode = ScanMode.LowLatency;
            Logger.Information("Scanning for device with Nordic UART Service");
            await adapter.StartScanningForDevicesAsync(linkedCancelSource.Token);

            return await tcs.Task;
        }

        protected async void RxNotificationHandler(object source, CharacteristicUpdatedEventArgs args)
        {

            var data = uartRx.Value;
            foreach (byte b in data)
            {
                await rxChannel.Writer.WriteAsync(b);
            }

            Logger.Information("Received notification of {ByteLength} bytes from BLE server: ", data.Length);
        }

        protected void DeviceDisconnectedHandler(object source, DeviceEventArgs args)
        {
            if (args.Device.Id == uartDevice.Id)
            {
                _isConnected = false;
                CrossBluetoothLE.Current.Adapter.DeviceDisconnected -= DeviceDisconnectedHandler;
                Logger.Information("Device disconnected");
            }
        }
        private async Task Clear()
        {
            while (rxChannel.Reader.Count > 0)
            {
                var _ = await rxChannel.Reader.ReadAsync();
            }
            //var discard = rxChannel.Reader.ReadAllAsync();
            //await foreach (var item in discard) { }
            // TODO fix this
        }
    }
}
