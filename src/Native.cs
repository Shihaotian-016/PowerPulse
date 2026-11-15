using System;
using System.Runtime.InteropServices;

namespace PowerPulse
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte BatteryLifeTime;
            public byte BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = false)]
        public static extern ulong GetTickCount64();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint LowDateTime;

            public uint HighDateTime;

            public ulong ToULong()
            {
                return ((ulong)HighDateTime << 32) | (ulong)LowDateTime;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public const int WM_NCLBUTTONDOWN = 0x00A1;

        public const int HTCAPTION = 0x2;
    }
}
