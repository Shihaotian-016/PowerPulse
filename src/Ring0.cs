using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PowerPulse
{
    // WinRing0 内核驱动装载与 MSR 读取(用于 Intel RAPL)。
    // IOCTL 与缓冲区布局复刻自 Open Hardware Monitor 官方 Ring0.cs:
    //   OLS_TYPE=40000, READ_MSR function=0x821, Buffered/AnyAccess
    //   => CTL_CODE = (40000<<16)|(0<<14)|(0x821<<2)|0 = 0x9C402084
    //   输入: uint MSR 索引(4 字节), 输出: ulong 值(8 字节)。
    // 装载/打开失败一律返回 false 并写 data\error.log, 绝不抛异常。
    internal sealed class Ring0
    {
        private const string ServiceName = "PowerPulseRing0";

        private const uint SC_MANAGER_CONNECT = 0x00000001;
        private const uint SC_MANAGER_CREATE_SERVICE = 0x00000002;
        private const uint SERVICE_ALL_ACCESS = 0x000F01FF;
        private const uint SERVICE_KERNEL_DRIVER = 0x00000010;
        private const uint SERVICE_DEMAND_START = 0x00000003;
        private const uint SERVICE_ERROR_IGNORE = 0x00000001;
        private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;

        private const uint IOCTL_OLS_READ_MSR = 0x9C402084;

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ_WRITE = 0x00000003;
        private const uint OPEN_EXISTING = 3;

        // OHM 构建的设备名在前, 原版 WinRing0 设备名兜底
        private static readonly string[] DevicePaths = new string[]
        {
            @"\\.\WinRing0_1_2_0",
            @"\\.\Ring0"
        };

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
            CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
            CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr CreateService(IntPtr scManager, string serviceName, string displayName,
            uint desiredAccess, uint serviceType, uint startType, uint errorControl,
            string binaryPathName, string loadOrderGroup, string tagId,
            string dependencies, string serviceStartName, string password);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
            CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint access);

        [DllImport("advapi32.dll", SetLastError = true,
            CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StartService(IntPtr service, uint argc, string[] argv);

        [DllImport("advapi32.dll", SetLastError = true,
            CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
            CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr CreateFile(string fileName, uint access, uint share,
            IntPtr securityAttributes, uint disposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true,
            CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(IntPtr device, uint ioControlCode,
            ref uint inBuffer, uint inBufferSize, out ulong outBuffer, uint outBufferSize,
            out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true,
            CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private IntPtr _device = IntPtr.Zero;

        public bool IsOpen { get { return _device != IntPtr.Zero; } }

        // 尝试装载驱动并打开设备; 成功 true。任何失败(含 24H2 驱动阻止列表)静默 false。
        public bool TryLoad()
        {
            try
            {
                string sysPath = Path.Combine(Application.StartupPath, "driver");
                sysPath = Path.Combine(sysPath, "WinRing0x64.sys");
                if (!File.Exists(sysPath))
                {
                    return false;
                }
                IntPtr sc = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
                if (sc == IntPtr.Zero)
                {
                    Log("OpenSCManager failed err=" + Marshal.GetLastWin32Error());
                    return false;
                }
                IntPtr svc = IntPtr.Zero;
                try
                {
                    svc = OpenService(sc, ServiceName, SERVICE_ALL_ACCESS);
                    if (svc == IntPtr.Zero)
                    {
                        svc = CreateService(sc, ServiceName, ServiceName, SERVICE_ALL_ACCESS,
                            SERVICE_KERNEL_DRIVER, SERVICE_DEMAND_START, SERVICE_ERROR_IGNORE,
                            sysPath, null, null, null, null, null);
                        if (svc == IntPtr.Zero)
                        {
                            Log("CreateService failed err=" + Marshal.GetLastWin32Error());
                            return false;
                        }
                    }
                    if (!StartService(svc, 0, null))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != ERROR_SERVICE_ALREADY_RUNNING)
                        {
                            // 常见: 1275/0xC000036/577 = 易受攻击驱动阻止列表或签名策略拒绝
                            Log("StartService failed err=" + err + " (0x" +
                                err.ToString("X8", CultureInfo.InvariantCulture) + ")");
                            return false;
                        }
                    }
                }
                finally
                {
                    if (svc != IntPtr.Zero)
                    {
                        CloseServiceHandle(svc);
                    }
                    CloseServiceHandle(sc);
                }
                for (int i = 0; i < DevicePaths.Length; i++)
                {
                    IntPtr h = CreateFile(DevicePaths[i], GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h != IntPtr.Zero && (long)h != -1L)
                    {
                        _device = h;
                        return true;
                    }
                }
                Log("CreateFile device failed err=" + Marshal.GetLastWin32Error());
                return false;
            }
            catch (Exception e)
            {
                Log("TryLoad exception: " + e.Message);
                return false;
            }
        }

        public bool ReadMsr(uint index, out ulong value)
        {
            value = 0UL;
            if (_device == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                uint inBuf = index;
                ulong outBuf;
                uint returned;
                if (!DeviceIoControl(_device, IOCTL_OLS_READ_MSR, ref inBuf, 4u, out outBuf, 8u,
                    out returned, IntPtr.Zero))
                {
                    return false;
                }
                if (returned < 8u)
                {
                    return false;
                }
                value = outBuf;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 仅关闭设备句柄; 保留服务以便下次快速启动。
        public void Unload()
        {
            if (_device != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(_device);
                }
                catch
                {
                }
                _device = IntPtr.Zero;
            }
        }

        private static void Log(string text)
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "data");
                path = Path.Combine(path, "error.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " Ring0: " + text + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
