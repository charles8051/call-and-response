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
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace CallAndResponse.Ble
{
    public class BleNordicUartTransceiver : Transceiver
    {
        //readonly string UartDeviceGuid = "00000000-0000-0000-0000-8065999482c6";
        //readonly string DeviceName = "Datafeel El Jefe";
        //readonly string UartSeviceGuidString = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
        //readonly string UartRxGuidString = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";
        //readonly string UartTxGuidString = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";

        private readonly Guid UartServiceGuid = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
        private readonly Guid UartRxGuid = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
        private readonly Guid UartTxGuid = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");

        private readonly IBluetoothLE bluetoothLE = CrossBluetoothLE.Current;
        private readonly IAdapter adapter = CrossBluetoothLE.Current.Adapter;

        private IDevice? uartDevice = null;
        private IService? uartService = null;
        private ICharacteristic? uartRx = null;
        private ICharacteristic? uartTx = null;

        private readonly int MtuRequest = 300;

        private Guid _id = Guid.Empty;

        // TODO never: investigate performance implications
        private static readonly int _rxBufferSize = 1024;
        private Channel<byte> rxChannel = Channel.CreateBounded<byte>(_rxBufferSize);

        public bool _isConnected = false;
        public override bool IsConnected => _isConnected;

        //public int ReadTimeout { get; set; } = 500;
        //public int WriteTimeout { get; set; } = 500;

        public BleNordicUartTransceiver()
        {
            CreateDefaultLogger();
        }

        public BleNordicUartTransceiver(Guid id)
        {
            _id = id;
            CreateDefaultLogger();
        }

        public override async Task Open(CancellationToken token = default)
        {
            //if (_id == Guid.Empty) throw new Exception("No GUID. Scan or provide a GUID");
            try
            {
                await CrossBluetoothLE.Current.TrySetStateAsync(true);

                if(_id == Guid.Empty)
                {
                    LogInformation("Searching for device");
                    uartDevice = await Scan(token);
                    if (uartDevice is null) throw new TransceiverConnectionException("Device not found");
                    LogInformation("Device found with UART Service");
                } else
                {
                    uartDevice = await adapter.ConnectToKnownDeviceAsync(_id, default, token);
                }

                var services = await uartDevice.GetServicesAsync();
                uartService = services.Where(s => s.Id == UartServiceGuid).FirstOrDefault();
                if (uartService is null) throw new TransceiverConnectionException("Service not found");

                // Double check the charactersistics inside our service
                var characteristics = await uartService.GetCharacteristicsAsync();
                uartRx = characteristics.Where(c => c.Id == UartRxGuid).FirstOrDefault();
                uartTx = characteristics.Where(c => c.Id == UartTxGuid).FirstOrDefault();
                if (uartRx is null || uartTx is null) throw new TransceiverConnectionException("Characteristics not found");

                uartDevice.UpdateConnectionInterval(ConnectionInterval.High);

                LogInformation("Request MTU: {@MtuRequest}", MtuRequest);
                var mtuActual = await uartDevice.RequestMtuAsync(MtuRequest);
                LogInformation("Actual MTU: {@MtuActual}", mtuActual);

                uartTx.WriteType = CharacteristicWriteType.WithoutResponse;

                _isConnected = true;
                CrossBluetoothLE.Current.Adapter.DeviceDisconnected += DeviceDisconnectedHandler;

                uartRx.ValueUpdated += RxNotificationHandler;
                await uartRx.StartUpdatesAsync();
            }
            catch (Exception e)
            {
                LogError("Open(): " + e.Message, e);
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
                    uartDevice.Dispose();
                }
            }
            catch (Exception e)
            {
                LogError(e.Message);
            }
            finally
            {
                _isConnected = false;
            }
        }

        public override async Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token = default)
        {
            if (IsConnected == false)
            {
                throw new TransceiverTransportException("Cannot read while disconnected");
            }

            try
            {
                if (uartTx != null)
                {
                    var result = await uartTx.WriteAsync(writeBytes.ToArray(), token);
                    if (result == 0)
                    {
                        LogVerbose("Sent {@ByteLength} bytes to BLE", writeBytes.Length);
                        await Task.Delay(3); // Add some deadtime to avoid overloading our garbage esp32 arduino code
                    } else
                    {
                        throw new TransceiverTransportException("Failed to write to BLE");
                    }
                }
            }
            catch (OperationCanceledException e) when (token.IsCancellationRequested)
            {
                LogError("Send(): " + e.Message, e);
                throw;
            }
            catch (Exception e)
            {
                LogError("Send(): " + e.Message, e);
                throw;
            }
        }

        public override async Task<Memory<byte>> ReceiveExactly(int numBytesExpected, CancellationToken token = default)
        {
            if (IsConnected == false)
            {
                LogError("Cannot read while disconnected");
                throw new TransceiverTransportException("Cannot read while disconnected");
            }
            if (numBytesExpected > _rxBufferSize)
            {
                LogError($"numBytesExpected cannot be greater than {_rxBufferSize}");
                throw new ArgumentException($"numBytesExpected cannot be greater than {_rxBufferSize}");
            }

            await Clear();

            var numBytesRead = 0;
            var data = new List<byte>();
            while (numBytesRead < numBytesExpected && token.IsCancellationRequested is false)
            {
                token.ThrowIfCancellationRequested();
                data.Add(await rxChannel.Reader.ReadAsync(token));
                numBytesRead++;
            }
            token.ThrowIfCancellationRequested();
            return data.ToArray().AsMemory();
        }

        public override async Task<Memory<byte>> ReceiveUntilMessageDetected(Func<ReadOnlyMemory<byte>, int> detectMessage, CancellationToken token = default)
        {
            if (IsConnected == false)
            {
                LogError("Cannot read while disconnected");
                throw new TransceiverTransportException("Cannot read while disconnected");
            }

            await Clear();

            int payloadLength = 0;
            int numBytesRead = 0;
            var data = new List<byte>();
            while (token.IsCancellationRequested is false)
            {
                token.ThrowIfCancellationRequested();
                if(numBytesRead >= _rxBufferSize) throw new TransceiverTransportException("Buffer overflow");

                data.Add(await rxChannel.Reader.ReadAsync(token));
                numBytesRead++;

                payloadLength = detectMessage(data.ToArray());
                if (payloadLength > 0) break;
            }
            token.ThrowIfCancellationRequested();
            return data.Take(payloadLength).ToArray().AsMemory();
        }

        private async Task<IDevice> ScanConnect(Guid deviceGuid, CancellationToken token = default)
        {
            IDevice? device = null;

            void deviceDiscovered(object sender, DeviceEventArgs a)
            {
                if(a.Device.Id.Equals(deviceGuid))
                //if (a.Device.Name.Equals("Datafeel El Jefe"))
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

            using var timeoutCancelSource = new CancellationTokenSource();
            using var scanCancel = CancellationTokenSource.CreateLinkedTokenSource(timeoutCancelSource.Token, token);

            async void deviceDiscovered(object sender, DeviceEventArgs a)
            {
                //a.Device.State == DeviceState.Connected;
                Console.WriteLine(a.Device.Name);
                var records = a.Device.AdvertisementRecords;
                foreach(var record in records)
                {
                    Console.WriteLine($"    {record.Type}");
                    
                    //if (record.Type == AdvertisementRecordType.ServiceData)
                    //{

                    //}
                }
                //var uartService = await a.Device.GetServiceAsync(UartServiceGuid);

                    //if (!(uartService is null))
                    //{
                    //    LogInformation("Found Service: {@ServiceId}", uartService.Id);

                    //    adapter.DeviceDiscovered -= deviceDiscovered;

                    //    _id = a.Device.Id;
                    //    scanCancel.Cancel();
                    //    tcs.TrySetResult(a.Device);
                    //}
            }

            timeoutCancelSource.Token.Register(() =>
            {
                adapter.DeviceDiscovered -= deviceDiscovered;
                tcs.TrySetException(new OperationCanceledException());
            });

            adapter.DeviceDiscovered += deviceDiscovered;
            adapter.ScanMode = ScanMode.LowLatency;
            LogInformation("Scanning for device with Nordic UART Service");
            await adapter.StartScanningForDevicesAsync(scanCancel.Token);

            return await tcs.Task;
        }

        protected async void RxNotificationHandler(object source, CharacteristicUpdatedEventArgs args)
        {

            var data = uartRx.Value;
            foreach (byte b in data)
            {
                await rxChannel.Writer.WriteAsync(b);
            }

            LogVerbose("Received notification of {ByteLength} bytes from BLE server: ", data.Length);
        }

        protected void DeviceDisconnectedHandler(object source, DeviceEventArgs args)
        {
            if (args.Device.Id == uartDevice.Id)
            {
                _isConnected = false;
                CrossBluetoothLE.Current.Adapter.DeviceDisconnected -= DeviceDisconnectedHandler;
                LogInformation("Device disconnected");
            }
        }

        private async Task Clear()
        {
            var discard = rxChannel.Reader.ReadAllAsync();
            await foreach (var item in discard) { }
        }




    }
}
