using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PowerPulse
{
    internal struct PowerSample
    {
        public DateTime Time;

        // 总功率 = CPU 真实功率 + GPU 真实功率(仅可用部分求和); -1 表示两者均不可用
        public double Watts;

        public double CpuW;

        public double GpuW;

        public double CumWh;

        public PowerSample(DateTime time, double watts, double cpuW, double gpuW, double cumWh)
        {
            Time = time;
            Watts = watts;
            CpuW = cpuW;
            GpuW = gpuW;
            CumWh = cumWh;
        }
    }

    // sessions.csv 中的一行: 一次开机一条
    internal sealed class SessionRecord
    {
        public string BootTime = "";

        public string LastRecord = "";

        public string Duration = "";

        public double AvgW = -1.0;

        public double PeakW = -1.0;

        public double CumWh = 0.0;
    }

    // 按自然日聚合的耗电统计(F1), 由 session_*.csv 明细积分而来
    internal sealed class DailyStat
    {
        public DateTime Day;

        public double Wh;

        public double PeakW = -1.0;
    }

    internal sealed class BatteryMonitor : IDisposable
    {
        private const int SampleIntervalMs = 15000;

        private const string SessionHeader = "时间,CPU功率W,GPU功率W,总功率W,累计耗电Wh";

        private const string SessionsHeader = "开机时刻,最近记录,运行时长,平均功率W,峰值功率W,累计耗电Wh";

        private readonly List<PowerSample> _samples = new List<PowerSample>();

        private readonly List<SessionRecord> _sessions = new List<SessionRecord>();

        // F1: session_*.csv 解析缓存(按文件修改时间失效)
        private readonly Dictionary<string, DateTime> _dailyFileMtimes = new Dictionary<string, DateTime>();

        private readonly Dictionary<string, List<DateTime>> _dailyFileTimes = new Dictionary<string, List<DateTime>>();

        private readonly Dictionary<string, List<double>> _dailyFileWatts = new Dictionary<string, List<double>>();

        private readonly Timer _timer;

        private readonly Timer _fastTimer;

        private readonly string _dataDir;

        private readonly string _sessionPath;

        private readonly string _sessionsPath;

        public event EventHandler Changed;

        public event EventHandler FastChanged;

        private NvApi _nvapi;

        private Ring0 _ring0;

        private Rapl _rapl;

        private XtuTelemetry _xtu;

        private double _gpuRealWatts = -1.0;

        private double _cpuRealWatts = -1.0;

        private int _xtuRetryTicks;

        public DateTime BootTime { get; private set; }

        // NVAPI 真实 GPU 板卡功率(瓦); -1 表示不可用
        public double GpuRealWatts { get { return _gpuRealWatts; } }

        public bool GpuRealAvailable { get { return _nvapi != null; } }

        public string GpuRealMode { get { return _nvapi != null ? _nvapi.ModeName : ""; } }

        // WinRing0+RAPL 或 Intel XTU 真实 CPU 包功率(瓦); 两者均不可用则 CPU 数据源缺失
        public bool CpuRealMode { get { return (_rapl != null && _rapl.Available) || (_xtu != null && _xtu.Available); } }

        public string CpuSourceName
        {
            get
            {
                if (_rapl != null && _rapl.Available)
                {
                    return "RAPL";
                }
                if (_xtu != null && _xtu.Available)
                {
                    return "Intel XTU";
                }
                return null;
            }
        }

        public double CpuRealWatts { get { return _cpuRealWatts; } }

        public string SessionPath { get { return _sessionPath; } }

        public string SessionsPath { get { return _sessionsPath; } }

        public string DataDir { get { return _dataDir; } }

        public IReadOnlyList<PowerSample> Samples { get { return _samples; } }

        public IReadOnlyList<SessionRecord> Sessions { get { return _sessions; } }

        public BatteryMonitor()
        {
            ulong tick = 0;
            try
            {
                tick = Native.GetTickCount64();
            }
            catch
            {
                tick = 0;
            }
            BootTime = DateTime.Now.AddMilliseconds(-(double)tick);
            _dataDir = Path.Combine(Application.StartupPath, "data");
            try
            {
                Directory.CreateDirectory(_dataDir);
            }
            catch
            {
            }
            _sessionPath = Path.Combine(_dataDir, "session_" + BootTime.ToString("yyyyMMdd_HHmmss") + ".csv");
            _sessionsPath = Path.Combine(_dataDir, "sessions.csv");
            LoadSession();
            LoadOrMigrateSessions();
            _nvapi = NvApi.TryCreate();
            try
            {
                _ring0 = new Ring0();
                if (_ring0.TryLoad())
                {
                    _rapl = new Rapl(_ring0);
                }
            }
            catch
            {
                _rapl = null;
            }
            _xtu = XtuTelemetry.TryCreate();
            _timer = new Timer();
            _timer.Interval = SampleIntervalMs;
            _timer.Tick += OnTick;
            _fastTimer = new Timer();
            _fastTimer.Interval = 2000;
            _fastTimer.Tick += OnFastTick;
        }

        public void Start()
        {
            CaptureNow();
            _timer.Start();
            _fastTimer.Start();
        }

        // 2 秒快速刷新：只读瞬时功率供界面磁贴使用，不写 CSV、不参与积分
        private void OnFastTick(object sender, EventArgs e)
        {
            ReadSensors();
            EventHandler h = FastChanged;
            if (h != null)
            {
                h(this, EventArgs.Empty);
            }
        }

        private void ReadSensors()
        {
            if (_nvapi != null)
            {
                double realW = _nvapi.ReadWatts();
                if (realW >= 0.0)
                {
                    _gpuRealWatts = realW;
                }
            }
            if (_rapl != null && _rapl.Available)
            {
                double cpuRealW = _rapl.ReadPackageWatts();
                if (cpuRealW >= 0.0)
                {
                    _cpuRealWatts = cpuRealW;
                }
            }
            else if (_xtu != null && _xtu.Available)
            {
                double cpuRealW = _xtu.ReadPackageWatts();
                if (cpuRealW >= 0.0)
                {
                    _cpuRealWatts = cpuRealW;
                }
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            CaptureNow();
        }

        public void CaptureNow()
        {
            // CPU 数据源不可用时每 4 个采样周期(约 60 秒)重试一次 XTU(内含服务拉起)
            if (_xtu != null && !_xtu.Available && (_rapl == null || !_rapl.Available))
            {
                _xtuRetryTicks++;
                if (_xtuRetryTicks >= 4)
                {
                    _xtuRetryTicks = 0;
                    _xtu.TryReconnect();
                }
            }
            ReadSensors();
            double watts = -1.0;
            if (_cpuRealWatts >= 0.0 || _gpuRealWatts >= 0.0)
            {
                watts = 0.0;
                if (_cpuRealWatts >= 0.0)
                {
                    watts += _cpuRealWatts;
                }
                if (_gpuRealWatts >= 0.0)
                {
                    watts += _gpuRealWatts;
                }
            }
            DateTime now = DateTime.Now;
            double cumWh = CumWhFromSamples();
            if (_samples.Count > 0 && watts >= 0.0)
            {
                PowerSample prev = _samples[_samples.Count - 1];
                double dtH = (now - prev.Time).TotalHours;
                if (dtH > 0.0 && dtH < 24.0)
                {
                    if (prev.Watts >= 0.0)
                    {
                        cumWh += (prev.Watts + watts) * 0.5 * dtH;
                    }
                    else
                    {
                        cumWh += watts * dtH;
                    }
                }
            }
            PowerSample s = new PowerSample(now, watts, _cpuRealWatts, _gpuRealWatts, cumWh);
            _samples.Add(s);
            AppendCsv(s);
            UpdateSessionRow();
            Raise();
        }

        private double CumWhFromSamples()
        {
            if (_samples.Count > 0)
            {
                return _samples[_samples.Count - 1].CumWh;
            }
            return 0.0;
        }

        private void Raise()
        {
            EventHandler h = Changed;
            if (h != null)
            {
                h(this, EventArgs.Empty);
            }
        }

        private void AppendCsv(PowerSample s)
        {
            try
            {
                bool fresh = !File.Exists(_sessionPath);
                using (StreamWriter w = new StreamWriter(_sessionPath, true, new UTF8Encoding(false)))
                {
                    if (fresh)
                    {
                        w.WriteLine(SessionHeader);
                    }
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss},{1:0.0},{2:0.0},{3:0.0},{4:0.000}",
                        s.Time, s.CpuW, s.GpuW, s.Watts, s.CumWh));
                }
            }
            catch
            {
            }
        }

        // 仅接受新格式(表头含"总功率W"), 解析不了则空列表重来
        private void LoadSession()
        {
            try
            {
                if (!File.Exists(_sessionPath))
                {
                    return;
                }
                string[] lines = File.ReadAllLines(_sessionPath, Encoding.UTF8);
                if (lines.Length == 0 || lines[0].IndexOf("总功率W") < 0)
                {
                    return;
                }
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }
                    string[] parts = line.Split(',');
                    if (parts.Length < 5)
                    {
                        continue;
                    }
                    DateTime t;
                    double cpuW;
                    double gpuW;
                    double watts;
                    double cum;
                    if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                    {
                        continue;
                    }
                    if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out cpuW))
                    {
                        cpuW = -1.0;
                    }
                    if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out gpuW))
                    {
                        gpuW = -1.0;
                    }
                    if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out watts))
                    {
                        continue;
                    }
                    if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out cum))
                    {
                        continue;
                    }
                    _samples.Add(new PowerSample(t, watts, cpuW, gpuW, cum));
                }
            }
            catch
            {
                _samples.Clear();
            }
        }

        private void LoadOrMigrateSessions()
        {
            try
            {
                if (File.Exists(_sessionsPath))
                {
                    string[] lines = File.ReadAllLines(_sessionsPath, Encoding.UTF8);
                    int start = 0;
                    if (lines.Length > 0 && lines[0].StartsWith("开机时刻"))
                    {
                        start = 1;
                    }
                    for (int i = start; i < lines.Length; i++)
                    {
                        string[] parts = lines[i].Split(',');
                        if (parts.Length < 6)
                        {
                            continue;
                        }
                        SessionRecord r = new SessionRecord();
                        r.BootTime = parts[0].Trim();
                        r.LastRecord = parts[1].Trim();
                        r.Duration = parts[2].Trim();
                        double v;
                        r.AvgW = double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : -1.0;
                        r.PeakW = double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : -1.0;
                        r.CumWh = double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0.0;
                        _sessions.Add(r);
                    }
                }
                else
                {
                    MigrateSessions();
                }
            }
            catch
            {
            }
            UpdateSessionRow();
        }

        // 把 data\session_*.csv 逐个解析成一条历史会话行(原始文件保留不动)
        private void MigrateSessions()
        {
            try
            {
                string[] files = Directory.GetFiles(_dataDir, "session_*.csv");
                Array.Sort(files, StringComparer.Ordinal);
                for (int i = 0; i < files.Length; i++)
                {
                    SessionRecord r = ParseSessionFile(files[i]);
                    if (r != null)
                    {
                        _sessions.Add(r);
                    }
                }
            }
            catch
            {
            }
        }

        private static SessionRecord ParseSessionFile(string path)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(path);
                int us = name.IndexOf('_');
                if (us < 0)
                {
                    return null;
                }
                DateTime boot;
                if (!DateTime.TryParseExact(name.Substring(us + 1), "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out boot))
                {
                    return null;
                }
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length < 2)
                {
                    return null;
                }
                bool realFormat = lines[0].IndexOf("总功率W") >= 0;
                int wattsIdx = realFormat ? 3 : 1;
                DateTime last = DateTime.MinValue;
                double peak = -1.0;
                double sum = 0.0;
                int cnt = 0;
                double cum = 0.0;
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split(',');
                    if (parts.Length < 5)
                    {
                        continue;
                    }
                    DateTime t;
                    double watts;
                    if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                    {
                        continue;
                    }
                    if (!double.TryParse(parts[wattsIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out watts))
                    {
                        continue;
                    }
                    double cumV;
                    if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out cumV))
                    {
                        cumV = 0.0;
                    }
                    last = t;
                    cum = cumV;
                    if (watts >= 0.0)
                    {
                        if (watts > peak)
                        {
                            peak = watts;
                        }
                        sum += watts;
                        cnt++;
                    }
                }
                if (last == DateTime.MinValue)
                {
                    return null;
                }
                SessionRecord r = new SessionRecord();
                r.BootTime = boot.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                r.LastRecord = last.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                TimeSpan dur = last - boot;
                if (dur < TimeSpan.Zero)
                {
                    dur = TimeSpan.Zero;
                }
                r.Duration = FormatDuration(dur);
                double hours = dur.TotalHours;
                if (hours > 0.0 && cum > 0.0)
                {
                    r.AvgW = cum / hours;
                }
                else if (cnt > 0)
                {
                    r.AvgW = sum / (double)cnt;
                }
                r.PeakW = peak;
                r.CumWh = cum;
                return r;
            }
            catch
            {
                return null;
            }
        }

        // 重写本机会话行并原子写回 sessions.csv
        private void UpdateSessionRow()
        {
            string key = BootTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            DateTime last = _samples.Count > 0 ? _samples[_samples.Count - 1].Time : BootTime;
            SessionRecord cur = new SessionRecord();
            cur.BootTime = key;
            cur.LastRecord = last.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            TimeSpan dur = last - BootTime;
            if (dur < TimeSpan.Zero)
            {
                dur = TimeSpan.Zero;
            }
            cur.Duration = FormatDuration(dur);
            cur.AvgW = AvgWatts;
            cur.PeakW = PeakWatts;
            cur.CumWh = CumWh;
            bool found = false;
            for (int i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].BootTime == key)
                {
                    _sessions[i] = cur;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                _sessions.Add(cur);
            }
            WriteSessions();
        }

        private void WriteSessions()
        {
            try
            {
                string tmp = _sessionsPath + ".tmp";
                using (StreamWriter w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                {
                    w.WriteLine(SessionsHeader);
                    for (int i = 0; i < _sessions.Count; i++)
                    {
                        SessionRecord r = _sessions[i];
                        w.WriteLine(string.Join(",", new string[6]
                        {
                            r.BootTime,
                            r.LastRecord,
                            r.Duration,
                            (r.AvgW >= 0.0 ? r.AvgW.ToString("0.0", CultureInfo.InvariantCulture) : "-1"),
                            (r.PeakW >= 0.0 ? r.PeakW.ToString("0.0", CultureInfo.InvariantCulture) : "-1"),
                            r.CumWh.ToString("0.000", CultureInfo.InvariantCulture)
                        }));
                    }
                }
                if (File.Exists(_sessionsPath))
                {
                    File.Delete(_sessionsPath);
                }
                File.Move(tmp, _sessionsPath);
            }
            catch
            {
            }
        }

        public static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalDays >= 1.0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}天{1:00}时{2:00}分",
                    (int)ts.TotalDays, ts.Hours, ts.Minutes);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
                (int)ts.TotalHours, ts.Minutes, ts.Seconds);
        }

        public double CurrentWatts
        {
            get
            {
                if (_samples.Count == 0)
                {
                    return -1.0;
                }
                return _samples[_samples.Count - 1].Watts;
            }
        }

        public double CumWh
        {
            get
            {
                return CumWhFromSamples();
            }
        }

        public double PeakWatts
        {
            get
            {
                double p = -1.0;
                for (int i = 0; i < _samples.Count; i++)
                {
                    if (_samples[i].Watts > p)
                    {
                        p = _samples[i].Watts;
                    }
                }
                return p;
            }
        }

        public double AvgWatts
        {
            get
            {
                if (_samples.Count < 2)
                {
                    return -1.0;
                }
                double cum = CumWh;
                if (cum <= 0.0)
                {
                    return -1.0;
                }
                double h = (_samples[_samples.Count - 1].Time - _samples[0].Time).TotalHours;
                if (h <= 0.0)
                {
                    return -1.0;
                }
                return cum / h;
            }
        }

        public TimeSpan Uptime
        {
            get
            {
                TimeSpan ts = DateTime.Now - BootTime;
                if (ts < TimeSpan.Zero)
                {
                    ts = TimeSpan.Zero;
                }
                return ts;
            }
        }

        // F1: 按自然日聚合全部 session_*.csv 明细; 跨零点区间在午夜处线性切分到两侧日期
        public List<DailyStat> GetDailyStats()
        {
            Dictionary<DateTime, DailyStat> map = new Dictionary<DateTime, DailyStat>();
            try
            {
                string[] files = Directory.GetFiles(_dataDir, "session_*.csv");
                Array.Sort(files, StringComparer.Ordinal);
                for (int fi = 0; fi < files.Length; fi++)
                {
                    string path = files[fi];
                    DateTime mtime;
                    try
                    {
                        mtime = File.GetLastWriteTimeUtc(path);
                    }
                    catch
                    {
                        continue;
                    }
                    List<DateTime> times;
                    List<double> watts;
                    DateTime cached;
                    bool hit = _dailyFileMtimes.TryGetValue(path, out cached);
                    if (hit && cached == mtime)
                    {
                        times = _dailyFileTimes[path];
                        watts = _dailyFileWatts[path];
                    }
                    else
                    {
                        times = new List<DateTime>();
                        watts = new List<double>();
                        ParseDailyPoints(path, times, watts);
                        _dailyFileMtimes[path] = mtime;
                        _dailyFileTimes[path] = times;
                        _dailyFileWatts[path] = watts;
                    }
                    for (int i = 0; i < times.Count; i++)
                    {
                        double w = watts[i];
                        if (w >= 0.0)
                        {
                            DailyStat d = GetDay(map, times[i].Date);
                            if (w > d.PeakW)
                            {
                                d.PeakW = w;
                            }
                        }
                        if (i > 0)
                        {
                            IntegrateSpan(map, times[i - 1], watts[i - 1], times[i], w);
                        }
                    }
                }
            }
            catch
            {
            }
            List<DailyStat> list = new List<DailyStat>(map.Values);
            list.Sort(delegate(DailyStat a, DailyStat b) { return a.Day.CompareTo(b.Day); });
            return list;
        }

        private static DailyStat GetDay(Dictionary<DateTime, DailyStat> map, DateTime day)
        {
            DailyStat d;
            if (!map.TryGetValue(day, out d))
            {
                d = new DailyStat();
                d.Day = day;
                map[day] = d;
            }
            return d;
        }

        private static void IntegrateSpan(Dictionary<DateTime, DailyStat> map, DateTime t0, double w0, DateTime t1, double w1)
        {
            if (t1 <= t0 || w0 < 0.0 || w1 < 0.0)
            {
                return;
            }
            while (t1 > t0)
            {
                DateTime mid = t0.Date.AddDays(1.0);
                DateTime end = t1 < mid ? t1 : mid;
                double frac = (end - t0).TotalMilliseconds / (t1 - t0).TotalMilliseconds;
                double wEnd = w0 + (w1 - w0) * frac;
                double hours = (end - t0).TotalHours;
                GetDay(map, t0.Date).Wh += (w0 + wEnd) * 0.5 * hours;
                t0 = end;
                w0 = wEnd;
            }
        }

        private static void ParseDailyPoints(string path, List<DateTime> times, List<double> watts)
        {
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length < 2)
                {
                    return;
                }
                int wattsIdx = lines[0].IndexOf("总功率W") >= 0 ? 3 : 1;
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split(',');
                    if (parts.Length < wattsIdx + 1)
                    {
                        continue;
                    }
                    DateTime t;
                    double w;
                    if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                    {
                        continue;
                    }
                    if (!double.TryParse(parts[wattsIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out w))
                    {
                        continue;
                    }
                    times.Add(t);
                    watts.Add(w);
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            _fastTimer.Stop();
            _fastTimer.Dispose();
            if (_nvapi != null)
            {
                _nvapi.Dispose();
                _nvapi = null;
            }
            _rapl = null;
            if (_ring0 != null)
            {
                _ring0.Unload();
                _ring0 = null;
            }
            if (_xtu != null)
            {
                _xtu.Shutdown();
                _xtu = null;
            }
        }
    }
}
