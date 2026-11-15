using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PowerPulse
{
    // Intel XTU 遥测读取 CPU 真实包功率(瓦)。
    //
    // 研究结论(2026-11, 两个独立仓库在真实 XTU 7.14.2.135 上联机验证, 结论一致):
    //   现代 XTU 7.x 不再以 xtuapi.dll 暴露遥测, 而是由 Windows 服务 XTU3SERVICE
    //   (显示名 "Intel(R) Extreme Tuning Utility Service", 安装时设为自动启动) 提供
    //   WCF 命名管道:
    //     net.pipe://localhost/ExtremeTuningUtility/Monitoring  (仅管理员进程可连)
    //   接口 IronCity.Core.IXtuMonitoring: Connect(pid)->bool, Start(pid),
    //   Stop(pid), GetValues()->Dictionary<uint,decimal>。
    //   监控 ID 1000 = CPU 包功率(Package TDP), 单位瓦(decimal); 1001=核心功率,
    //   1002=核显功率。来源:
    //     https://github.com/minhdevtree/xtu-profile-switcher  (Monitor.cs,
    //       .claude/skills/xtu-switcher/references/xtu-service.md, 联机验证)
    //     https://github.com/rigbay-app/module-xtu  (Monitoring.cs, Intel/Contracts.cs,
    //       Intel/IntelPorts.cs, 对真实服务录制过线格式并测试通过)
    //   安装目录(用于旧版探测): 注册表 HKLM Uninstall DisplayName 含 "Extreme Tuning"
    //   的 InstallLocation; 常见为 C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\
    //   与 C:\Program Files (x86)\Intel\Intel(R) Extreme Tuning Utility\Client\
    //   (来源: https://github.com/twitchbionx/autotune autotune/xtu_sdk.py)。
    //   WCF 配额必须调大(64MB, MaxItemsInObjectGraph=int.MaxValue), 否则 GetValues
    //   回复超默认限制失败(同上来源)。
    //
    // 回退路径(旧版 XTU 6.x 的 xtuapi.dll, XtuGetTelemetryFieldData):
    //   未能在多个独立仓库间交叉验证(2026-11 检索不到权威 xtuapi.h), 且 XTU 7.x 安装
    //   包已不含该 DLL, 故仅作尽力而为的兼容: 找到 DLL 时按任务给定的签名
    //   XtuInitialize()/XtuTerminate()/XtuGetTelemetryFieldData(id, instance, buf, size)
    //   以 stdcall 调用, 遥测 ID 沿用 1000, 返回码 0=成功; 数值经 0~1000W 合理性
    //   校验(超 1000 时尝试按 mW 除以 1000)。任何异常/校验失败一律返回 -1 并置
    //   Available=false, 绝不崩溃(本进程 anycpu 在 x64 下加载 32 位 DLL 会直接失败,
    //   属预期静默降级)。
    //
    // Start(pid) 刚返回时服务给出的是上次运行的陈旧值(来源同上 xtu-service.md),
    // 本应用 15 秒采样间隔远大于此, 不做额外预热丢弃。
    internal sealed class XtuTelemetry
    {
        private const uint MonitorPkgPower = 1000;
        private const string MonitoringPipe = "net.pipe://localhost/ExtremeTuningUtility/Monitoring";
        private const double MaxPlausibleWatts = 1000.0;

        // XTU 7.x 遥测由 Windows 服务 XTU3SERVICE 承载, 进程需保证其在运行(必要时提权拉起)。
        private const string XtuServiceName = "XTU3SERVICE";
        private const uint SC_MANAGER_CONNECT = 0x00000001;
        private const uint SERVICE_QUERY_STATUS = 0x00000004;
        private const uint SERVICE_START = 0x00000010;
        private const uint SERVICE_STOPPED = 0x00000001;
        private const uint SERVICE_RUNNING = 0x00000004;
        private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
        private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;

        private const int ModeNone = 0;
        private const int ModeWcf = 1;
        private const int ModeLegacyDll = 2;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int XtuInitFn();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int XtuTermFn();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int XtuGetTelemetryFn(uint telemetryId, uint instanceIndex, IntPtr buffer, uint bufferSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType;

            public uint dwCurrentState;

            public uint dwControlsAccepted;

            public uint dwWin32ExitCode;

            public uint dwServiceSpecificExitCode;

            public uint dwCheckPoint;

            public uint dwWaitHint;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
            CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint access);

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
        private static extern bool QueryServiceStatus(IntPtr service, ref SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true,
            CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        private int _mode;
        private bool _available;
        private string _lastLoggedStatus;

        private ChannelFactory<IronCity.Core.IXtuMonitoring> _factory;
        private IronCity.Core.IXtuMonitoring _channel;
        private bool _started;
        private int _pid;

        private IntPtr _dll;
        private XtuInitFn _init;
        private XtuTermFn _term;
        private XtuGetTelemetryFn _getTelemetry;
        private bool _legacyInitDone;

        public bool Available { get { return _available; } }

        public string SourceName { get { return _available ? "Intel XTU" : null; } }

        private XtuTelemetry()
        {
        }

        // 先试 XTU 7.x WCF 监控管道(必要时提权拉起 XTU3SERVICE), 再试旧版 xtuapi.dll; 全失败返回 Available=false 的实例。
        public static XtuTelemetry TryCreate()
        {
            XtuTelemetry t = new XtuTelemetry();
            t.TryOpenWcf();
            if (!t._available)
            {
                t.TryOpenLegacyDll();
            }
            if (!t._available)
            {
                t.LogStatus("no XTU source available");
            }
            return t;
        }

        // CPU 数据源不可用时由 BatteryMonitor 周期性调用: 重新探测服务并尝试重连管道。
        public bool TryReconnect()
        {
            if (_available)
            {
                return true;
            }
            TryOpenWcf();
            if (!_available)
            {
                TryOpenLegacyDll();
            }
            if (!_available)
            {
                LogStatus("reconnect failed, no XTU source");
            }
            return _available;
        }

        public double ReadPackageWatts()
        {
            try
            {
                if (_mode == ModeWcf)
                {
                    return ReadWcf();
                }
                if (_mode == ModeLegacyDll)
                {
                    return ReadLegacy();
                }
            }
            catch (Exception e)
            {
                LogStatus("read exception: " + e.GetType().Name + ": " + e.Message);
                if (_mode == ModeWcf)
                {
                    // 管道已坏(如服务重启), 断开并交给周期重连
                    CloseWcf(false);
                }
            }
            return -1.0;
        }

        public void Shutdown()
        {
            try
            {
                if (_channel != null)
                {
                    if (_started)
                    {
                        _channel.Stop(_pid);
                    }
                    ((ICommunicationObject)_channel).Abort();
                }
            }
            catch
            {
            }
            _channel = null;
            try
            {
                if (_factory != null)
                {
                    _factory.Abort();
                }
            }
            catch
            {
            }
            _factory = null;
            _started = false;
            try
            {
                if (_legacyInitDone && _term != null)
                {
                    _term();
                }
            }
            catch
            {
            }
            _legacyInitDone = false;
            try
            {
                if (_dll != IntPtr.Zero)
                {
                    FreeLibrary(_dll);
                }
            }
            catch
            {
            }
            _dll = IntPtr.Zero;
            _init = null;
            _term = null;
            _getTelemetry = null;
            _mode = ModeNone;
            _available = false;
        }

        private void TryOpenWcf()
        {
            if (EnsureServiceRunning() < 0)
            {
                LogStatus("XTU3SERVICE not installed, skip WCF path");
                return;
            }
            try
            {
                _pid = Process.GetCurrentProcess().Id;
                NetNamedPipeBinding b = new NetNamedPipeBinding();
                b.OpenTimeout = TimeSpan.FromSeconds(5);
                b.SendTimeout = TimeSpan.FromSeconds(10);
                b.ReceiveTimeout = TimeSpan.FromSeconds(10);
                b.MaxReceivedMessageSize = 67108864;
                b.MaxBufferSize = 67108864;
                b.ReaderQuotas.MaxArrayLength = 67108864;
                b.ReaderQuotas.MaxStringContentLength = 67108864;
                b.ReaderQuotas.MaxDepth = 64;
                _factory = new ChannelFactory<IronCity.Core.IXtuMonitoring>(b, new EndpointAddress(MonitoringPipe));
                foreach (OperationDescription op in _factory.Endpoint.Contract.Operations)
                {
                    DataContractSerializerOperationBehavior dc =
                        op.Behaviors.Find<DataContractSerializerOperationBehavior>();
                    if (dc != null)
                    {
                        dc.MaxItemsInObjectGraph = int.MaxValue;
                    }
                }
                _channel = _factory.CreateChannel();
                if (!_channel.Connect(_pid))
                {
                    LogStatus("Connect(pid) refused by service");
                    CloseWcf(false);
                    return;
                }
                _channel.Start(_pid);
                _started = true;
                _mode = ModeWcf;
                _available = true;
                LogStatus("connected via WCF monitoring pipe");
            }
            catch (Exception e)
            {
                LogStatus("WCF open failed: " + e.GetType().Name + ": " + e.Message);
                CloseWcf(false);
            }
        }

        // 保证 XTU3SERVICE 在运行。返回 1=RUNNING, 0=存在但没跑起来/无法管理, -1=服务未安装。
        private int EnsureServiceRunning()
        {
            try
            {
                IntPtr sc = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (sc == IntPtr.Zero)
                {
                    LogStatus("OpenSCManager failed err=" + Marshal.GetLastWin32Error());
                    return 0;
                }
                try
                {
                    IntPtr svc = OpenService(sc, XtuServiceName, SERVICE_QUERY_STATUS | SERVICE_START);
                    if (svc == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == ERROR_SERVICE_DOES_NOT_EXIST)
                        {
                            return -1;
                        }
                        LogStatus("OpenService XTU3SERVICE failed err=" + err);
                        return 0;
                    }
                    try
                    {
                        SERVICE_STATUS st = new SERVICE_STATUS();
                        if (QueryServiceStatus(svc, ref st) && st.dwCurrentState == SERVICE_RUNNING)
                        {
                            return 1;
                        }
                        if (!IsElevated())
                        {
                            LogStatus("XTU3SERVICE stopped and process not elevated");
                            return 0;
                        }
                        if (!StartService(svc, 0, null))
                        {
                            int err = Marshal.GetLastWin32Error();
                            if (err != ERROR_SERVICE_ALREADY_RUNNING)
                            {
                                LogStatus("StartService XTU3SERVICE failed err=" + err);
                                return 0;
                            }
                        }
                        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                        while (DateTime.UtcNow < deadline)
                        {
                            st = new SERVICE_STATUS();
                            if (QueryServiceStatus(svc, ref st))
                            {
                                if (st.dwCurrentState == SERVICE_RUNNING)
                                {
                                    LogStatus("XTU3SERVICE started by PowerPulse");
                                    return 1;
                                }
                                if (st.dwCurrentState == SERVICE_STOPPED)
                                {
                                    break;
                                }
                            }
                            Thread.Sleep(250);
                        }
                        LogStatus("XTU3SERVICE not RUNNING after start (state=" + st.dwCurrentState + ")");
                        return 0;
                    }
                    finally
                    {
                        CloseServiceHandle(svc);
                    }
                }
                finally
                {
                    CloseServiceHandle(sc);
                }
            }
            catch (Exception e)
            {
                LogStatus("EnsureServiceRunning exception: " + e.GetType().Name + ": " + e.Message);
                return 0;
            }
        }

        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        // 状态变化时写一行 data\error.log, 相同内容不重复写, 避免刷屏。
        private void LogStatus(string text)
        {
            if (text == _lastLoggedStatus)
            {
                return;
            }
            _lastLoggedStatus = text;
            try
            {
                string dir = Path.Combine(Application.StartupPath, "data");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " XTU: " + text + Environment.NewLine);
            }
            catch
            {
            }
        }

        private double ReadWcf()
        {
            Dictionary<uint, decimal> values = _channel.GetValues();
            if (values == null)
            {
                LogStatus("GetValues returned null");
                return -1.0;
            }
            decimal v;
            if (!values.TryGetValue(MonitorPkgPower, out v))
            {
                LogStatus("GetValues missing monitor id 1000");
                return -1.0;
            }
            double w = (double)v;
            if (double.IsNaN(w) || double.IsInfinity(w) || w < 0.0 || w > MaxPlausibleWatts)
            {
                LogStatus("implausible package power (outside 0-1000W)");
                return -1.0;
            }
            LogStatus("reading OK");
            return w;
        }

        private void CloseWcf(bool stop)
        {
            try
            {
                if (_channel != null)
                {
                    if (stop && _started)
                    {
                        _channel.Stop(_pid);
                    }
                    ((ICommunicationObject)_channel).Abort();
                }
            }
            catch
            {
            }
            _channel = null;
            try
            {
                if (_factory != null)
                {
                    _factory.Abort();
                }
            }
            catch
            {
            }
            _factory = null;
            _started = false;
            if (_mode == ModeWcf)
            {
                _mode = ModeNone;
                _available = false;
            }
        }

        private void TryOpenLegacyDll()
        {
            string path = FindXtuApiDll();
            if (path == null)
            {
                return;
            }
            try
            {
                _dll = LoadLibrary(path);
                if (_dll == IntPtr.Zero)
                {
                    return;
                }
                IntPtr pInit = GetProcAddress(_dll, "XtuInitialize");
                IntPtr pTerm = GetProcAddress(_dll, "XtuTerminate");
                IntPtr pGet = GetProcAddress(_dll, "XtuGetTelemetryFieldData");
                if (pInit == IntPtr.Zero || pTerm == IntPtr.Zero || pGet == IntPtr.Zero)
                {
                    FreeLibrary(_dll);
                    _dll = IntPtr.Zero;
                    return;
                }
                _init = (XtuInitFn)Marshal.GetDelegateForFunctionPointer(pInit, typeof(XtuInitFn));
                _term = (XtuTermFn)Marshal.GetDelegateForFunctionPointer(pTerm, typeof(XtuTermFn));
                _getTelemetry = (XtuGetTelemetryFn)Marshal.GetDelegateForFunctionPointer(pGet, typeof(XtuGetTelemetryFn));
                _mode = ModeLegacyDll;
                _available = true;
            }
            catch
            {
                _mode = ModeNone;
                _available = false;
            }
        }

        private double ReadLegacy()
        {
            if (!_legacyInitDone)
            {
                if (_init() != 0)
                {
                    _available = false;
                    return -1.0;
                }
                _legacyInitDone = true;
            }
            IntPtr buf = Marshal.AllocHGlobal(64);
            try
            {
                byte[] zero = new byte[64];
                Marshal.Copy(zero, 0, buf, 64);
                if (_getTelemetry(MonitorPkgPower, 0u, buf, 64u) != 0)
                {
                    return -1.0;
                }
                byte[] raw = new byte[8];
                Marshal.Copy(buf, raw, 0, 8);
                double d = BitConverter.ToDouble(raw, 0);
                if (!double.IsNaN(d) && !double.IsInfinity(d))
                {
                    if (d >= 0.0 && d <= MaxPlausibleWatts)
                    {
                        return d;
                    }
                    if (d > MaxPlausibleWatts && d <= MaxPlausibleWatts * 1000.0)
                    {
                        // 合理性启发: 旧版若以 mW 返回, 换算后仍须在 0~1000W
                        double w = d / 1000.0;
                        if (w >= 0.0 && w <= MaxPlausibleWatts)
                        {
                            return w;
                        }
                    }
                }
                float f = BitConverter.ToSingle(raw, 0);
                if (!float.IsNaN(f) && !float.IsInfinity(f) && f >= 0.0f && f <= (float)MaxPlausibleWatts)
                {
                    return (double)f;
                }
                return -1.0;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // 依次: 注册表 Uninstall(64/32 视图) InstallLocation -> 标准安装目录; 深度 3 内找 xtuapi.dll。
        private static string FindXtuApiDll()
        {
            List<string> roots = new List<string>();
            string reg = FindInstallLocationFromRegistry();
            if (reg != null)
            {
                roots.Add(reg);
            }
            string pf64 = Environment.GetEnvironmentVariable("ProgramFiles");
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (pf86 != null)
            {
                roots.Add(Path.Combine(Path.Combine(pf86, "Intel"), @"Intel(R) Extreme Tuning Utility"));
                roots.Add(Path.Combine(Path.Combine(pf86, "Intel"), "XTU"));
            }
            if (pf64 != null)
            {
                roots.Add(Path.Combine(Path.Combine(pf64, "Intel"), @"Intel(R) Extreme Tuning Utility"));
                roots.Add(Path.Combine(Path.Combine(pf64, "Intel"), "XTU"));
            }
            for (int i = 0; i < roots.Count; i++)
            {
                string hit = SearchDll(roots[i], 3);
                if (hit != null)
                {
                    return hit;
                }
            }
            return null;
        }

        private static string FindInstallLocationFromRegistry()
        {
            RegistryView[] views = new RegistryView[2] { RegistryView.Registry64, RegistryView.Registry32 };
            for (int v = 0; v < views.Length; v++)
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, views[v]))
                    using (RegistryKey uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null)
                        {
                            continue;
                        }
                        string[] names = uninstall.GetSubKeyNames();
                        for (int i = 0; i < names.Length; i++)
                        {
                            try
                            {
                                using (RegistryKey k = uninstall.OpenSubKey(names[i]))
                                {
                                    object dn = k.GetValue("DisplayName");
                                    if (dn == null || dn.ToString().IndexOf("Extreme Tuning", StringComparison.OrdinalIgnoreCase) < 0)
                                    {
                                        continue;
                                    }
                                    object il = k.GetValue("InstallLocation");
                                    if (il != null)
                                    {
                                        string s = il.ToString().Trim().TrimEnd(Path.DirectorySeparatorChar);
                                        if (s.Length > 0 && Directory.Exists(s))
                                        {
                                            return s;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private static string SearchDll(string dir, int depth)
        {
            if (dir == null || dir.Length == 0 || depth < 0)
            {
                return null;
            }
            try
            {
                if (!Directory.Exists(dir))
                {
                    return null;
                }
                string hit = Path.Combine(dir, "xtuapi.dll");
                if (File.Exists(hit))
                {
                    return hit;
                }
                if (depth == 0)
                {
                    return null;
                }
                string[] subs = Directory.GetDirectories(dir);
                for (int i = 0; i < subs.Length; i++)
                {
                    string r = SearchDll(subs[i], depth - 1);
                    if (r != null)
                    {
                        return r;
                    }
                }
            }
            catch
            {
            }
            return null;
        }
    }
}

namespace IronCity.Core
{
    // 本文件自带的 Intel XTU 监控服务契约"形状副本"(非 Intel 文件, 仅接口/方法/参数名)。
    // WCF 线格式由 CLR 命名空间+类型名+成员名决定, 以下名称不可改动
    // (来源: https://github.com/rigbay-app/module-xtu Intel/Contracts.cs 的联机验证结论)。
    [System.ServiceModel.ServiceContract]
    internal interface IXtuMonitoring
    {
        [System.ServiceModel.OperationContract]
        bool Connect(int processId);

        [System.ServiceModel.OperationContract]
        void Start(int processId);

        [System.ServiceModel.OperationContract]
        void Stop(int processId);

        [System.ServiceModel.OperationContract]
        System.Collections.Generic.Dictionary<uint, decimal> GetValues();
    }
}
