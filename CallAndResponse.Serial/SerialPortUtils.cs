using System;
using System.Collections.Generic;
using System.Management;

namespace CallAndResponse.Serial
{
    public class SerialPortUtils
    {
        private static List<CIMSerialControllerInfo> GetSerialPorts()
        {
            List<CIMSerialControllerInfo> devices = new List<CIMSerialControllerInfo>();

            using var searcher = new ManagementObjectSearcher(
                @"Select * From CIM_SerialController");
            using ManagementObjectCollection collection = searcher.Get();

            foreach (var device in collection)
            {
                try
                {
                    devices.Add(new CIMSerialControllerInfo()
                    {
                        SerialPort = (string)device.GetPropertyValue("DeviceID"),
                        Description = (string)device.GetPropertyValue("Description"),
                        PNPDeviceID = (string)device.GetPropertyValue("PNPDeviceID")
                    });
                }
                catch { }
            }
            return devices;
        }

        public static string? FindPortNameById(ushort vid, ushort pid)
        {
            var serialPortInfos = GetSerialPorts();

            string pnpString = $"VID_{vid:X4}&PID_{pid:X4}";

            var device = serialPortInfos.Find(device => device.PNPDeviceID == pnpString);
            if (device != null)
            {
                return device.SerialPort;
            }
            return null;
        }

        public static string? GetCp210xComPort()
        {
            return FindPortNameById(0x10C4, 0xEA60);
        }
    }
    internal class CIMSerialControllerInfo
    {
        public string? SerialPort { get; set; }
        public string? Description { get; set; }
        public string? PNPDeviceID { get; set; }
    }
}
