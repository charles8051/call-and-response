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
    public class BleTransceiver : Transceiver
    {
        //readonly string UartDeviceGuid = "00000000-0000-0000-0000-8065999482c6";
        readonly string DeviceName = "Datafeel El Jefe";
        readonly string UartSeviceGuid = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
        readonly string UartRxGuid = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";
        readonly string UartTxGuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";

        private readonly IBluetoothLE bluetoothLE = CrossBluetoothLE.Current;
        private readonly IAdapter adapter = CrossBluetoothLE.Current.Adapter;

        private IDevice? uartDevice = null;
        private IService? uartService = null;
        private ICharacteristic? uartRx = null;
        private ICharacteristic? uartTx = null;

        private readonly int MtuRequest = 300;

        public Guid Id { get; set; } = Guid.Empty;

        private CancellationTokenSource cts = new CancellationTokenSource();

        // TODO never: investigate performance implications
        private Channel<byte> rxChannel = Channel.CreateBounded<byte>(1024);

        private bool isRunning = false;
        public bool IsConnected { get; private set; } = false;

        public int ReadTimeout { get; set; } = 500;
        public int WriteTimeout { get; set; } = 500;

        public BleTransceiver()
        {
            InitLogger();
        }


        public async Task<bool> Scan(CancellationToken token)
        {
            LogInformation("Scanning for Datafeel El Jefe");
            var tcs = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource();
            var scanCancel = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, token);

            void deviceDiscovered(object sender, DeviceEventArgs a)
            {
                if (a.Device.Name.Equals("Datafeel El Jefe"))
                {
                    adapter.DeviceDiscovered -= deviceDiscovered;
                    LogInformation("Found Datafeel El Jefe with Device GUID: {@DeviceId}", a.Device.Id);
                    Id = a.Device.Id;
                    scanCancel.Cancel();
                    tcs.TrySetResult(true);
                }
            }

            token.Register(() =>
            {
                adapter.DeviceDiscovered -= deviceDiscovered;
                //tcs.TrySetException(new TimeoutException());
                tcs.TrySetResult(false);
            });

            adapter.DeviceDiscovered += deviceDiscovered;
            adapter.ScanMode = ScanMode.LowLatency;
            await adapter.StartScanningForDevicesAsync(scanCancel.Token);

            return await tcs.Task;
        }


        public async Task Open(CancellationToken token)
        {
            if (Id == Guid.Empty) throw new Exception("No GUID. Scan or provide a GUID");
            try
            {
                await CrossBluetoothLE.Current.TrySetStateAsync(true);

                LogInformation("Connecting to El Jefe...");
                uartDevice = await adapter.ConnectToKnownDeviceAsync(Id, default, token);
                if (uartDevice is null) throw new Exception("Device not found");
                LogInformation("Connected to El Jefe!");

                //uartDevice.NativeDevice
                LogInformation("Request MTU: {@MtuRequest}", MtuRequest);
                var mtuActual = await uartDevice.RequestMtuAsync(MtuRequest);
                LogInformation("Actual MTU: {@MtuActual}", mtuActual);

                uartDevice.UpdateConnectionInterval(ConnectionInterval.High);

                var services = await uartDevice.GetServicesAsync();
                uartService = services.Where(s => s.Id == Guid.Parse(UartSeviceGuid)).FirstOrDefault();
                if (uartService is null) throw new Exception("Service not found");

                var characteristics = await uartService.GetCharacteristicsAsync();
                uartRx = characteristics.Where(c => c.Id == Guid.Parse(UartRxGuid)).FirstOrDefault();
                uartTx = characteristics.Where(c => c.Id == Guid.Parse(UartTxGuid)).FirstOrDefault();
                if (uartRx is null || uartTx is null) throw new Exception("Characteristics not found");
                uartTx.WriteType = CharacteristicWriteType.WithoutResponse;
                LogInformation("El Jefe GATT service found");

                IsConnected = true;
                CrossBluetoothLE.Current.Adapter.DeviceDisconnected += DeviceDisconnectedHandler;

                uartRx.ValueUpdated += RxNotificationHandler;
                await uartRx.StartUpdatesAsync();
            }
            catch (Exception e)
            {
                //Serilog.Log.Logger.Error(e, "Error opening BLE serial port"); //TODO use the logger instance in this class
                LogError("Open" + e.Message, e);
                IsConnected = false;
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

        public async Task<byte[]> ReadExact(int count, CancellationToken token = default)
        {
            //var data = new byte[numBytes];
            var data = new List<byte>();
            while (count > 0 && token.IsCancellationRequested == false)
            {
                try
                {
                    data.Add(await rxChannel.Reader.ReadAsync(token));
                    count--;
                    if (rxChannel.Reader.Count is 0)
                    {
                        break;
                    }
                }
                catch (Exception e)
                {
                    LogError("ReadExact" + e.Message, e);
                    //throw;
                }
            }

            // TODO: add timeout
            return data.ToArray();
        }


        // needs to timeout
        public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (IsConnected == false)
            {
                throw new TransceiverException();
            }

            //var timeoutCts = new CancellationTokenSource(WriteTimeout);

            var data = await TakeUpTo(count, token);
            data.ToArray().CopyTo(buffer, offset);
            return data.Length;
        }

        public async Task<byte[]> TakeAll()
        {
            if (rxChannel.Reader.Count is 0)
            {
                return Array.Empty<byte>();
            }
            List<byte> data = new List<byte>();
            while (rxChannel.Reader.Count > 0)
            {
                data.Add(await rxChannel.Reader.ReadAsync());
            }
            return data.ToArray();
        }

        public async Task<byte[]> TakeUpTo(int numBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (rxChannel.Reader.Count is 0)
            {
                return Array.Empty<byte>();
            }

            List<byte> data = new List<byte>();
            while (rxChannel.Reader.Count > 0 && numBytes > 0)
            {
                token.ThrowIfCancellationRequested();
                data.Add(await rxChannel.Reader.ReadAsync());
                numBytes--;
            }
            return data.ToArray();
        }

        public async Task WriteAsync(byte[] data, CancellationToken token)
        {
            if (IsConnected == false)
            {
                throw new TransceiverException();
            }

            //var timeoutCts = new CancellationTokenSource(WriteTimeout);
            try
            {
                if (uartTx != null)
                {
                    await uartTx.WriteAsync(data, token);
                    await Task.Delay(3); // Add some deadtime to avoid overloading our garbage esp32 arduino code

                    LogVerbose("Sent {@ByteLength} bytes to BLE", data.Length);
                }
            }
            catch (OperationCanceledException e) when (token.IsCancellationRequested)
            {
                LogError("Write Async" + e.Message, e);
                throw;
            }
            //catch(OperationCanceledException e) when (timeoutCts.IsCancellationRequested)
            //{
            //    LogError(e.Message, e);
            //    throw new TimeoutException("The asynchronous write operation timed out.");
            //}
            catch (Exception e) // transport exceptions should bubble up above us
            {
                LogError("BleSerialPort " + e.Message, e);
                throw;
            }
        }

        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token = default)
        {
            var data = buffer
                .Skip(offset)
                .Take(count)
                .ToArray();
            return WriteAsync(data, token);
        }

        public async Task Close()
        {
            //cts.Cancel();
            try
            {
                if (uartDevice != null)
                {
                    uartDevice.Dispose();
                }
            }
            catch (Exception e)
            {
                Serilog.Log.Logger.Error(e.Message);
            }
            finally
            {
                IsConnected = false;
            }
        }

        private async void RxNotificationHandler(object source, CharacteristicUpdatedEventArgs args)
        {

            var data = uartRx.Value;
            foreach (byte b in data)
            {
                await rxChannel.Writer.WriteAsync(b);
            }

            //string logString = $"Received {data.Length} bytes from BLE server: ";
            //for (int i = 0; i < data.Length; i++)
            //{
            //    logString += data[i].ToString("X2") + " ";
            //}
            //logger.LogInformation(logString);
            LogVerbose("Received notification of {ByteLength} bytes from BLE server: ", data.Length);
        }

        protected void DeviceDisconnectedHandler(object source, DeviceEventArgs args)
        {
            if (args.Device.Id == uartDevice.Id)
            {
                IsConnected = false;
                CrossBluetoothLE.Current.Adapter.DeviceDisconnected -= DeviceDisconnectedHandler;
                LogInformation("Device disconnected");
            }
        }

        public async Task Clear()
        {
            var discard = rxChannel.Reader.ReadAllAsync();
        }


        //private async Task<bool> ForRxNotification(TimeSpan timeout)
        //{
        //    var tcs = new TaskCompletionSource<bool>();
        //    var cancel = new CancellationTokenSource(timeout);
        //    var timeoutTask = Task.Delay(timeout);

        //    void Handler(object sender, CharacteristicUpdatedEventArgs args)
        //    {
        //        tcs.TrySetResult(true);
        //    }

        //    uartRx.ValueUpdated += Handler;

        //    cancel.Token.Register(() =>
        //    {
        //        tcs.TrySetException(new TimeoutException());
        //    });

        //    try
        //    {
        //        return await tcs.Task;
        //    }
        //    finally
        //    {
        //        uartRx.ValueUpdated -= Handler;
        //    }
        //}

        private async Task<IDevice> ScanConnect(Guid deviceGuid, CancellationToken token = default)
        {
            IDevice? device = null;

            void deviceDiscovered(object sender, DeviceEventArgs a)
            {
                //if(a.Device.Id.Equals(deviceGuid))
                if (a.Device.Name.Equals("Datafeel El Jefe"))
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

        [Conditional("TRACE_BLE_SERIAL_PORT")]
        private async void Measure(Func<Task> action, string actionName)
        {
            var watch = new Stopwatch();
            watch.Start();
            await action();
            watch.Stop();
            LogInformation("Action:{@ActionName} took {Milliseconds}ms", actionName, watch.ElapsedMilliseconds);
        }



        private ILogger logger;
        [Conditional("LOG_BLE_SERIAL_PORT")]
        private void InitLogger()
        {
            logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .WriteTo.Console(theme: SystemConsoleTheme.Literate,
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] ({SourceContext}.{Method}) {Message}{NewLine}{Exception}")
                .CreateLogger();
        }

        [Conditional("LOG_BLE_SERIAL_PORT")]
        private void LogInformation(string message, params object[] args)
        {
            logger.Information(message, args);
        }

        [Conditional("LOG_BLE_SERIAL_PORT")]
        private void LogVerbose(string message, params object[] args)
        {
            logger.Verbose(message, args);
        }

        [Conditional("LOG_BLE_SERIAL_PORT")]
        private void LogError(string message, params object[] args)
        {
            logger.Error(message, args);
        }
    }
}
