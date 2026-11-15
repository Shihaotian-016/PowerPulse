using System;
using System.Runtime.InteropServices;

namespace PowerPulse
{
    // NVAPI 用户态读取 NVIDIA GPU 真实功率(瓦)。无需管理员权限。
    // 主路径: NvAPI_GPU_ClientPowerTopologyGetStatus (现代驱动/Blackwell 唯一可用,
    //        官方 nvapi.h 已移除旧 Pstates 功耗字段, 布局参考 LibreHardwareMonitor)。
    // 回退路径: 旧版 NvAPI_GPU_GetPstates20 / NvAPI_GPU_GetPstatesInfoEx 结构布局。
    internal sealed class NvApi : IDisposable
    {
        private const uint ID_INITIALIZE = 0x0150E828;
        private const uint ID_UNLOAD = 0xD22BDD7E;
        private const uint ID_ENUM_PHYSICAL_GPUS = 0xE5AC921F;
        private const uint ID_GET_PSTATES20 = 0x6FF81213;
        private const uint ID_GET_PSTATES_INFO_EX = 0x843C0256;
        private const uint ID_POWER_TOPOLOGY = 0xEDCF624E;

        private const int MAX_PHYSICAL_GPUS = 64;
        private const int MAX_POWER_TOPOLOGIES = 4;
        private const int MAX_CONTROLLERS = 8;
        private const int MAX_SENSORS = 32;
        private const double MAX_PLAUSIBLE_WATTS = 1200.0;

        private const int MODE_NONE = 0;
        private const int MODE_POWER_TOPOLOGY = 1;
        private const int MODE_PSTATES20 = 2;
        private const int MODE_PSTATES_INFO_EX = 3;

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QueryInterface64(uint interfaceId);

        [DllImport("nvapi.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QueryInterface32(uint interfaceId);

        private static IntPtr Query(uint interfaceId)
        {
            return Environment.Is64BitProcess ? QueryInterface64(interfaceId) : QueryInterface32(interfaceId);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvVoidDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EnumGpusDelegate([Out] IntPtr[] gpuHandles, out uint gpuCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PowerTopologyDelegate(IntPtr gpuHandle, IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Pstates20Delegate(IntPtr gpuHandle, IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PstatesInfoExDelegate(IntPtr gpuHandle, IntPtr info, uint inputFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerTopologyEntry
        {
            public uint Domain; // 0=Gpu 1=Board
            public uint Reserved;
            public uint PowerUsage; // 毫瓦
            public uint Reserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerTopologyInfo
        {
            public uint Version;
            public uint Count;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_POWER_TOPOLOGIES)]
            public PowerTopologyEntry[] Entries;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LegacyControllerInfo
        {
            public uint util;
            public uint memory;
            public uint vid;
            public uint subText;
            public uint perfId;
            public int power; // 毫瓦
            public uint unknown0;
            public uint unknown1;
            public uint unknown2;
            public uint unknown3;
            public uint unknown4;
            public uint unknown5;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LegacySensorInfo
        {
            public uint type; // 2=power
            public uint indicator;
            public int currentValue; // 毫瓦
            public int maximumValue;
            public uint unknown0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LegacySensorData
        {
            public uint version;
            public uint sensorCount;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_SENSORS)]
            public LegacySensorInfo[] sensors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LegacyPstates20Info
        {
            public uint version;
            public uint controllerCount;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_CONTROLLERS)]
            public LegacyControllerInfo[] controller;

            public LegacySensorData sensors;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public uint[] unknown0;

            public uint unknown1;
            public uint basePStateIndex;
            public uint basePStateTransitions;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LegacyPerfController
        {
            public uint isIdle;
            public uint ebcIntensity;
            public uint power; // 毫瓦

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public uint[] unknown0;

            public uint utilization;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public uint[] unknown1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LegacyPstatesInfo
        {
            public uint version;
            public uint flags;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_CONTROLLERS)]
            public LegacyPerfController[] controller;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public uint[] unknown2;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public uint[] unknown3;
        }

        private int _mode;
        private IntPtr _gpu;
        private bool _initialized;
        private NvVoidDelegate _unload;
        private PowerTopologyDelegate _powerTopology;
        private Pstates20Delegate _pstates20;
        private PstatesInfoExDelegate _pstatesInfoEx;

        public string ModeName { get; private set; }

        private NvApi()
        {
            ModeName = "";
        }

        // 探测可用接口; 完全不可用返回 null。
        public static NvApi TryCreate()
        {
            NvApi api = null;
            try
            {
                api = new NvApi();
                api.Initialize();
            }
            catch
            {
                if (api != null)
                {
                    api.Dispose();
                }
                return null;
            }
            if (api._mode == MODE_NONE)
            {
                api.Dispose();
                return null;
            }
            return api;
        }

        private void Initialize()
        {
            IntPtr pInit = Query(ID_INITIALIZE);
            if (pInit == IntPtr.Zero)
            {
                return;
            }
            NvVoidDelegate init = (NvVoidDelegate)Marshal.GetDelegateForFunctionPointer(pInit, typeof(NvVoidDelegate));
            if (init() != 0)
            {
                return;
            }
            _initialized = true;
            IntPtr pUnload = Query(ID_UNLOAD);
            if (pUnload != IntPtr.Zero)
            {
                _unload = (NvVoidDelegate)Marshal.GetDelegateForFunctionPointer(pUnload, typeof(NvVoidDelegate));
            }
            IntPtr pEnum = Query(ID_ENUM_PHYSICAL_GPUS);
            if (pEnum == IntPtr.Zero)
            {
                return;
            }
            EnumGpusDelegate enumGpus = (EnumGpusDelegate)Marshal.GetDelegateForFunctionPointer(pEnum, typeof(EnumGpusDelegate));
            IntPtr[] handles = new IntPtr[MAX_PHYSICAL_GPUS];
            uint count;
            if (enumGpus(handles, out count) != 0 || count == 0)
            {
                return;
            }
            _gpu = handles[0];

            // 主路径: ClientPowerTopology (现代驱动)
            IntPtr pPt = Query(ID_POWER_TOPOLOGY);
            if (pPt != IntPtr.Zero)
            {
                _powerTopology = (PowerTopologyDelegate)Marshal.GetDelegateForFunctionPointer(pPt, typeof(PowerTopologyDelegate));
                if (ReadPowerTopology() >= 0.0)
                {
                    _mode = MODE_POWER_TOPOLOGY;
                    ModeName = "NVAPI PowerTopology";
                    return;
                }
            }

            // 回退 1: 旧版 Pstates20 (传感器/控制器功耗)
            IntPtr p20 = Query(ID_GET_PSTATES20);
            if (p20 != IntPtr.Zero)
            {
                _pstates20 = (Pstates20Delegate)Marshal.GetDelegateForFunctionPointer(p20, typeof(Pstates20Delegate));
                if (ReadPstates20() >= 0.0)
                {
                    _mode = MODE_PSTATES20;
                    ModeName = "NVAPI Pstates20";
                    return;
                }
            }

            // 回退 2: 旧版 PstatesInfoEx (8 控制器功耗求和)
            IntPtr pEx = Query(ID_GET_PSTATES_INFO_EX);
            if (pEx != IntPtr.Zero)
            {
                _pstatesInfoEx = (PstatesInfoExDelegate)Marshal.GetDelegateForFunctionPointer(pEx, typeof(PstatesInfoExDelegate));
                if (ReadPstatesInfoEx() >= 0.0)
                {
                    _mode = MODE_PSTATES_INFO_EX;
                    ModeName = "NVAPI PstatesInfoEx";
                }
            }
        }

        public double ReadWatts()
        {
            switch (_mode)
            {
                case MODE_POWER_TOPOLOGY:
                    return ReadPowerTopology();
                case MODE_PSTATES20:
                    return ReadPstates20();
                case MODE_PSTATES_INFO_EX:
                    return ReadPstatesInfoEx();
                default:
                    return -1.0;
            }
        }

        private double ReadPowerTopology()
        {
            if (_powerTopology == null || _gpu == IntPtr.Zero)
            {
                return -1.0;
            }
            int size = Marshal.SizeOf(typeof(PowerTopologyInfo));
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                PowerTopologyInfo info = new PowerTopologyInfo();
                info.Version = (uint)(size | (1 << 16));
                info.Entries = new PowerTopologyEntry[MAX_POWER_TOPOLOGIES];
                Marshal.StructureToPtr(info, buf, false);
                if (_powerTopology(_gpu, buf) != 0)
                {
                    return -1.0;
                }
                PowerTopologyInfo r = (PowerTopologyInfo)Marshal.PtrToStructure(buf, typeof(PowerTopologyInfo));
                if (r.Entries == null)
                {
                    return -1.0;
                }
                uint n = r.Count;
                if (n > (uint)r.Entries.Length)
                {
                    n = (uint)r.Entries.Length;
                }
                double board = -1.0;
                double gpu = -1.0;
                for (int i = 0; i < (int)n; i++)
                {
                    double w = (double)r.Entries[i].PowerUsage / 1000.0;
                    if (w < 0.0 || w > MAX_PLAUSIBLE_WATTS)
                    {
                        continue;
                    }
                    if (r.Entries[i].Domain == 1u)
                    {
                        board = w;
                    }
                    else if (r.Entries[i].Domain == 0u)
                    {
                        gpu = w;
                    }
                }
                if (board >= 0.0)
                {
                    return board;
                }
                return gpu;
            }
            catch
            {
                return -1.0;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private double ReadPstates20()
        {
            if (_pstates20 == null || _gpu == IntPtr.Zero)
            {
                return -1.0;
            }
            int size = Marshal.SizeOf(typeof(LegacyPstates20Info));
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                LegacyPstates20Info info = new LegacyPstates20Info();
                info.controller = new LegacyControllerInfo[MAX_CONTROLLERS];
                info.sensors = new LegacySensorData();
                info.sensors.version = (uint)(Marshal.SizeOf(typeof(LegacySensorData)) | (1 << 16));
                info.sensors.sensors = new LegacySensorInfo[MAX_SENSORS];
                info.unknown0 = new uint[8];
                // 先试 v1 布局(无 basePState 字段), 再试 v2
                info.version = (uint)(1076 | (1 << 16));
                Marshal.StructureToPtr(info, buf, false);
                int st = _pstates20(_gpu, buf);
                if (st != 0)
                {
                    info.version = (uint)(size | (2 << 16));
                    Marshal.StructureToPtr(info, buf, false);
                    st = _pstates20(_gpu, buf);
                }
                if (st != 0)
                {
                    return -1.0;
                }
                LegacyPstates20Info r = (LegacyPstates20Info)Marshal.PtrToStructure(buf, typeof(LegacyPstates20Info));
                double mw = 0.0;
                bool found = false;
                if (r.sensors.sensors != null)
                {
                    uint n = r.sensors.sensorCount;
                    if (n > (uint)r.sensors.sensors.Length)
                    {
                        n = (uint)r.sensors.sensors.Length;
                    }
                    for (int i = 0; i < (int)n; i++)
                    {
                        LegacySensorInfo s = r.sensors.sensors[i];
                        if (s.type == 2u && s.currentValue > 0)
                        {
                            mw += (double)s.currentValue;
                            found = true;
                        }
                    }
                }
                if (!found && r.controller != null)
                {
                    for (int i = 0; i < r.controller.Length; i++)
                    {
                        if (r.controller[i].power > 0)
                        {
                            mw += (double)r.controller[i].power;
                            found = true;
                        }
                    }
                }
                if (!found)
                {
                    return -1.0;
                }
                double w = mw / 1000.0;
                if (w < 0.0 || w > MAX_PLAUSIBLE_WATTS)
                {
                    return -1.0;
                }
                return w;
            }
            catch
            {
                return -1.0;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private double ReadPstatesInfoEx()
        {
            if (_pstatesInfoEx == null || _gpu == IntPtr.Zero)
            {
                return -1.0;
            }
            int size = Marshal.SizeOf(typeof(LegacyPstatesInfo));
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                LegacyPstatesInfo info = new LegacyPstatesInfo();
                info.version = (uint)(size | (1 << 16));
                info.controller = new LegacyPerfController[MAX_CONTROLLERS];
                info.unknown2 = new uint[8];
                info.unknown3 = new uint[8];
                Marshal.StructureToPtr(info, buf, false);
                if (_pstatesInfoEx(_gpu, buf, 0u) != 0)
                {
                    return -1.0;
                }
                LegacyPstatesInfo r = (LegacyPstatesInfo)Marshal.PtrToStructure(buf, typeof(LegacyPstatesInfo));
                if (r.controller == null)
                {
                    return -1.0;
                }
                double mw = 0.0;
                bool found = false;
                for (int i = 0; i < r.controller.Length; i++)
                {
                    if (r.controller[i].power > 0u)
                    {
                        mw += (double)r.controller[i].power;
                        found = true;
                    }
                }
                if (!found)
                {
                    return -1.0;
                }
                double w = mw / 1000.0;
                if (w < 0.0 || w > MAX_PLAUSIBLE_WATTS)
                {
                    return -1.0;
                }
                return w;
            }
            catch
            {
                return -1.0;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        public void Dispose()
        {
            if (_initialized && _unload != null)
            {
                try
                {
                    _unload();
                }
                catch
                {
                }
            }
            _initialized = false;
            _mode = MODE_NONE;
            _gpu = IntPtr.Zero;
        }
    }
}
