using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PowerPulse
{
    internal sealed class MainForm : Form
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private const string RunValueName = "PowerPulse";

        private const string TaskName = "PowerPulse";

        private readonly BatteryMonitor _mon = new BatteryMonitor();

        private readonly TitleBar _bar = new TitleBar();

        private readonly NavPanel _nav = new NavPanel();

        private readonly Panel _content = new Panel();

        private readonly Panel _pageOverview = new Panel();

        private readonly Panel _pageRecords = new Panel();

        private readonly Panel _pageDaily = new Panel();

        private readonly Panel _pageSettings = new Panel();

        private readonly StatTile _tileTotal = new StatTile();

        private readonly StatTile _tileCpu = new StatTile();

        private readonly StatTile _tileGpu = new StatTile();

        private readonly StatTile _tileEnergy = new StatTile();

        private readonly StatTile _tileAvg = new StatTile();

        private readonly StatTile _tilePeak = new StatTile();

        private readonly StatTile _tileToday = new StatTile();

        private readonly StatTile _tileMonth = new StatTile();

        private readonly StatTile _tileWeekAvg = new StatTile();

        private readonly DailyBars _dailyBars = new DailyBars();

        private readonly ChartCard _chartCard = new ChartCard();

        private readonly LineChart _chart = new LineChart();

        private readonly RecordsView _records = new RecordsView();

        private readonly PrimaryButton _btnOpenRecords = new PrimaryButton("打开数据文件夹");

        private readonly Label _lblSessionFile = new Label();

        private readonly TechCheck _chkAuto = new TechCheck("开机自动启动 PowerPulse");

        private readonly Label _lblDataPath = new Label();

        private readonly PrimaryButton _btnOpenData = new PrimaryButton("打开数据文件夹");

        private readonly Label _lblSessionInfo = new Label();

        private readonly Label _lblBootInfo = new Label();

        private readonly Label _lblDriver = new Label();

        private readonly Label _lblSource = new Label();

        private readonly NotifyIcon _tray = new NotifyIcon();

        // I1: 二次启动通过命名事件唤起本实例
        private readonly System.Threading.EventWaitHandle _showEvt;

        private readonly Timer _showTimer;

        private bool _exiting;

        public MainForm()
        {
            // DPI 感知: 取 DeviceDpi/96 与系统 DPI 惰性探测值中的较大者作为缩放系数
            float sysScale = Theme.Scale;
            float devScale = DeviceDpi / 96f;
            Theme.Scale = devScale > sysScale ? devScale : sysScale;

            Text = "PowerPulse";
            Icon = LoadAppIcon(32);
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(Theme.S(920), Theme.S(600));
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Padding = new Padding(1);

            _content.Dock = DockStyle.Fill;
            _content.BackColor = Theme.Bg;
            _content.Padding = new Padding(Theme.S(16));

            _nav.Dock = DockStyle.Left;
            _bar.Dock = DockStyle.Top;

            Controls.Add(_content);
            Controls.Add(_nav);
            Controls.Add(_bar);

            BuildOverview();
            BuildRecords();
            BuildDaily();
            BuildSettings();
            BuildNav();
            BuildTray();

            foreach (Control c in new Control[4] { _pageOverview, _pageRecords, _pageDaily, _pageSettings })
            {
                c.Dock = DockStyle.Fill;
                c.BackColor = Theme.Bg;
                c.Visible = false;
                _content.Controls.Add(c);
            }
            _pageOverview.Visible = true;

            bool evtOwned;
            _showEvt = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset,
                @"Local\PowerPulse.ShowEvent", out evtOwned);
            _showTimer = new Timer();
            _showTimer.Interval = 500;
            _showTimer.Tick += delegate
            {
                if (_showEvt.WaitOne(0))
                {
                    _showEvt.Reset();
                    RestoreFromTray();
                }
            };
            _showTimer.Start();

            EnsureFirstRunAutoStart();
            _chkAuto.Checked = IsAutoStartEnabled();
            _chkAuto.CheckedChanged += delegate
            {
                SetAutoStart(_chkAuto.Checked);
            };

            _btnOpenRecords.Click += delegate { OpenDataFolder(); };
            _btnOpenData.Click += delegate { OpenDataFolder(); };

            _mon.Changed += delegate { RefreshAll(); };
            _mon.FastChanged += delegate { RefreshPower(); };
            _mon.Start();
            RefreshAll();
        }

        private void BuildNav()
        {
            NavButton b1 = new NavButton("总览");
            NavButton b2 = new NavButton("记录");
            NavButton b3 = new NavButton("每日");
            NavButton b4 = new NavButton("设置");
            b1.SetBounds(Theme.S(12), Theme.S(74), Theme.S(156), Theme.S(40));
            b2.SetBounds(Theme.S(12), Theme.S(122), Theme.S(156), Theme.S(40));
            b3.SetBounds(Theme.S(12), Theme.S(170), Theme.S(156), Theme.S(40));
            b4.SetBounds(Theme.S(12), Theme.S(218), Theme.S(156), Theme.S(40));
            b1.Selected = true;
            b1.Activated += delegate { ShowPage(0); };
            b2.Activated += delegate { ShowPage(1); };
            b3.Activated += delegate { ShowPage(2); };
            b4.Activated += delegate { ShowPage(3); };
            _nav.Controls.Add(b1);
            _nav.Controls.Add(b2);
            _nav.Controls.Add(b3);
            _nav.Controls.Add(b4);
            _nav.Tag = new Control[4] { b1, b2, b3, b4 };
        }

        private void ShowPage(int index)
        {
            _pageOverview.Visible = index == 0;
            _pageRecords.Visible = index == 1;
            _pageDaily.Visible = index == 2;
            _pageSettings.Visible = index == 3;
            Control[] buttons = (Control[])_nav.Tag;
            for (int i = 0; i < buttons.Length; i++)
            {
                NavButton nb = (NavButton)buttons[i];
                nb.Selected = i == index;
                nb.Invalidate();
            }
        }

        private void BuildOverview()
        {
            PlaceTile(_tileTotal, 0, 0, "01", "当前总功率");
            PlaceTile(_tileCpu, 239, 0, "02", "CPU 功率");
            PlaceTile(_tileGpu, 478, 0, "03", "GPU 功率");
            PlaceTile(_tileEnergy, 0, 104, "04", "开机累计耗电");
            PlaceTile(_tileAvg, 239, 104, "05", "平均功率");
            PlaceTile(_tilePeak, 478, 104, "06", "峰值功率");
            _pageOverview.Controls.Add(_tileTotal);
            _pageOverview.Controls.Add(_tileCpu);
            _pageOverview.Controls.Add(_tileGpu);
            _pageOverview.Controls.Add(_tileEnergy);
            _pageOverview.Controls.Add(_tileAvg);
            _pageOverview.Controls.Add(_tilePeak);
            _lblSource.Font = Theme.UI(8.5f, false);
            _lblSource.ForeColor = Theme.Faint;
            _lblSource.BackColor = Theme.Bg;
            _lblSource.SetBounds(0, Theme.S(200), Theme.S(706), Theme.S(16));
            _pageOverview.Controls.Add(_lblSource);
            _chartCard.SetBounds(0, Theme.S(222), Theme.S(706), Theme.S(286));
            _chart.SetBounds(Theme.S(12), Theme.S(36), Theme.S(682), Theme.S(238));
            _chartCard.Controls.Add(_chart);
            _pageOverview.Controls.Add(_chartCard);
        }

        private static void PlaceTile(StatTile t, int x, int y, string index, string title)
        {
            t.SetBounds(Theme.S(x), Theme.S(y), Theme.S(227), Theme.S(92));
            t.Index = index;
            t.Title = title;
        }

        private void BuildRecords()
        {
            Label head = new Label();
            head.Text = "历次开机记录";
            head.Font = Theme.UI(9f, true);
            head.ForeColor = Theme.Dim;
            head.BackColor = Theme.Bg;
            head.SetBounds(0, 0, Theme.S(300), Theme.S(20));
            _records.SetBounds(0, Theme.S(26), Theme.S(706), Theme.S(430));
            _btnOpenRecords.SetBounds(0, Theme.S(472), Theme.S(160), Theme.S(36));
            _lblSessionFile.Font = Theme.Mono(8f, false);
            _lblSessionFile.ForeColor = Theme.Faint;
            _lblSessionFile.BackColor = Theme.Bg;
            _lblSessionFile.SetBounds(Theme.S(176), Theme.S(482), Theme.S(520), Theme.S(18));
            _pageRecords.Controls.Add(head);
            _pageRecords.Controls.Add(_records);
            _pageRecords.Controls.Add(_btnOpenRecords);
            _pageRecords.Controls.Add(_lblSessionFile);
        }

        private void BuildDaily()
        {
            Label head = new Label();
            head.Text = "每日耗电统计";
            head.Font = Theme.UI(9f, true);
            head.ForeColor = Theme.Dim;
            head.BackColor = Theme.Bg;
            head.SetBounds(0, 0, Theme.S(300), Theme.S(20));
            PlaceTile(_tileToday, 0, 26, "07", "今日累计");
            PlaceTile(_tileMonth, 239, 26, "08", "本月累计");
            PlaceTile(_tileWeekAvg, 478, 26, "09", "近7日日均");
            Label barsHead = new Label();
            barsHead.Text = "近 14 日耗电(度)";
            barsHead.Font = Theme.UI(9f, true);
            barsHead.ForeColor = Theme.Fg;
            barsHead.BackColor = Theme.Bg;
            barsHead.SetBounds(0, Theme.S(134), Theme.S(300), Theme.S(18));
            _dailyBars.SetBounds(0, Theme.S(156), Theme.S(706), Theme.S(344));
            _pageDaily.Controls.Add(head);
            _pageDaily.Controls.Add(_tileToday);
            _pageDaily.Controls.Add(_tileMonth);
            _pageDaily.Controls.Add(_tileWeekAvg);
            _pageDaily.Controls.Add(barsHead);
            _pageDaily.Controls.Add(_dailyBars);
        }

        private void BuildSettings()
        {
            CardPanel c1 = new CardPanel("启动");
            c1.SetBounds(0, 0, Theme.S(706), Theme.S(84));
            _chkAuto.SetBounds(Theme.S(14), Theme.S(44), Theme.S(360), Theme.S(24));
            c1.Controls.Add(_chkAuto);

            CardPanel c2 = new CardPanel("数据");
            c2.SetBounds(0, Theme.S(96), Theme.S(706), Theme.S(130));
            _lblDataPath.Font = Theme.Mono(8f, false);
            _lblDataPath.ForeColor = Theme.Dim;
            _lblDataPath.BackColor = Theme.Card;
            _lblDataPath.SetBounds(Theme.S(14), Theme.S(40), Theme.S(676), Theme.S(16));
            _btnOpenData.SetBounds(Theme.S(14), Theme.S(70), Theme.S(160), Theme.S(36));
            _btnOpenData.BackColor = Theme.Card;
            _lblSessionInfo.Font = Theme.UI(8f, false);
            _lblSessionInfo.ForeColor = Theme.Faint;
            _lblSessionInfo.BackColor = Theme.Card;
            _lblSessionInfo.SetBounds(Theme.S(190), Theme.S(78), Theme.S(500), Theme.S(20));
            c2.Controls.Add(_lblDataPath);
            c2.Controls.Add(_btnOpenData);
            c2.Controls.Add(_lblSessionInfo);

            CardPanel c3 = new CardPanel("本机会话");
            c3.SetBounds(0, Theme.S(238), Theme.S(706), Theme.S(130));
            _lblBootInfo.Name = "uptimeInfo";
            _lblBootInfo.Font = Theme.UI(8.5f, false);
            _lblBootInfo.ForeColor = Theme.Dim;
            _lblBootInfo.BackColor = Theme.Card;
            _lblBootInfo.SetBounds(Theme.S(14), Theme.S(40), Theme.S(676), Theme.S(44));
            _lblDriver.Font = Theme.UI(8.5f, false);
            _lblDriver.ForeColor = Theme.Dim;
            _lblDriver.BackColor = Theme.Card;
            _lblDriver.SetBounds(Theme.S(14), Theme.S(90), Theme.S(676), Theme.S(30));
            c3.Controls.Add(_lblBootInfo);
            c3.Controls.Add(_lblDriver);

            _pageSettings.Controls.Add(c1);
            _pageSettings.Controls.Add(c2);
            _pageSettings.Controls.Add(c3);
        }

        // 优先从嵌入资源按目标像素选帧, 失败时退回 exe 自带图标
        private static Icon LoadAppIcon(int px)
        {
            try
            {
                Stream s = typeof(MainForm).Assembly.GetManifestResourceStream("PowerPulse.app.ico");
                if (s != null)
                {
                    using (s)
                    {
                        return new Icon(s, px, px);
                    }
                }
            }
            catch
            {
            }
            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private void BuildTray()
        {
            _tray.Icon = LoadAppIcon(Math.Max(16, SystemInformation.SmallIconSize.Width));
            _tray.Text = "PowerPulse · 耗电追踪";
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.BackColor = Theme.Card;
            menu.ForeColor = Theme.Fg;
            menu.Renderer = new DarkMenuRenderer();
            ToolStripItem show = menu.Items.Add("显示");
            ToolStripItem exit = menu.Items.Add("退出");
            show.Click += delegate { RestoreFromTray(); };
            exit.Click += delegate
            {
                _exiting = true;
                Close();
            };
            _tray.ContextMenuStrip = menu;
            _tray.MouseDoubleClick += delegate { RestoreFromTray(); };
            _tray.Visible = true;
        }

        public void MinimizeToTray()
        {
            Hide();
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void OpenDataFolder()
        {
            try
            {
                Process.Start("explorer.exe", _mon.DataDir);
            }
            catch
            {
            }
        }

        // 2 秒快速刷新：只更新瞬时功率磁贴，不重绘曲线/表格
        private void RefreshPower()
        {
            double cpuW = _mon.CpuRealWatts;
            double gpuW = _mon.GpuRealWatts;
            double cur = -1.0;
            if (cpuW >= 0.0 || gpuW >= 0.0)
            {
                cur = 0.0;
                if (cpuW >= 0.0)
                {
                    cur += cpuW;
                }
                if (gpuW >= 0.0)
                {
                    cur += gpuW;
                }
            }
            if (cur >= 0.0)
            {
                _tileTotal.SetValue(cur, "0", " W", Theme.Accent);
                _tray.Text = "PowerPulse · 当前 " + cur.ToString("0", CultureInfo.InvariantCulture) + " W";
            }
            else
            {
                _tileTotal.SetText("--", Theme.Fg);
                _tray.Text = "PowerPulse · 耗电追踪";
            }

            if (cpuW >= 0.0)
            {
                _tileCpu.SetValue(cpuW, "0.0", " W", Theme.Fg);
            }
            else
            {
                _tileCpu.SetText("不可用(需装XTU)", Theme.Warning);
            }

            if (gpuW >= 0.0)
            {
                _tileGpu.SetValue(gpuW, "0.0", " W", Theme.Fg);
            }
            else
            {
                _tileGpu.SetText("不可用", Theme.Warning);
            }
        }

        private void RefreshAll()
        {
            RefreshPower();

            double wh = _mon.CumWh;
            _tileEnergy.SetValue(wh / 1000.0, "0.000", " 度", Theme.Fg);

            double avg = _mon.AvgWatts;
            if (avg >= 0.0)
            {
                _tileAvg.SetValue(avg, "0", " W", Theme.Fg);
            }
            else
            {
                _tileAvg.SetText("--", Theme.Fg);
            }

            double peak = _mon.PeakWatts;
            if (peak >= 0.0)
            {
                _tilePeak.SetValue(peak, "0", " W", Theme.Warning);
            }
            else
            {
                _tilePeak.SetText("--", Theme.Warning);
            }

            RefreshDaily();

            _lblSource.Text = "数据源: GPU " + (_mon.GpuRealAvailable ? "真实(NVAPI)" : "不可用")
                + " · CPU " + (_mon.CpuRealMode ? "真实(" + _mon.CpuSourceName + ")" : "不可用（需安装 Intel XTU）");

            _chart.SetData(_mon.Samples, _mon.BootTime);
            _records.SetData(_mon.Sessions);
            _lblSessionFile.Text = Path.GetFileName(_mon.SessionsPath);
            _lblDataPath.Text = _mon.DataDir;
            _lblSessionInfo.Text = "开机时刻: " + _mon.BootTime.ToString("yyyy-MM-dd HH:mm:ss")
                + " · 样本: " + _mon.Samples.Count;

            _lblBootInfo.Text = "开机时刻: " + _mon.BootTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + " · 已采样 " + _mon.Samples.Count.ToString(CultureInfo.InvariantCulture) + " 次 · 功率 2 秒刷新 · 记录每 15 秒"
                + Environment.NewLine
                + "会话文件: " + Path.GetFileName(_mon.SessionPath)
                + " · 历史记录: " + _mon.SessionsPath;
            _lblDriver.Text = _mon.CpuRealMode
                ? "CPU 数据源: 真实(" + _mon.CpuSourceName + ")"
                : "CPU 数据源: 不可用（需安装 Intel XTU）";

            Invalidate();
        }

        // F1: 每日页 — 今日/本月/近7日日均 + 近14日柱状图
        private void RefreshDaily()
        {
            List<DailyStat> daily = _mon.GetDailyStats();
            DateTime today = DateTime.Now.Date;
            DateTime monthStart = new DateTime(today.Year, today.Month, 1);
            DateTime weekStart = today.AddDays(-6.0);
            double todayWh = 0.0;
            double monthWh = 0.0;
            double weekWh = 0.0;
            for (int i = 0; i < daily.Count; i++)
            {
                DailyStat d = daily[i];
                if (d.Day == today)
                {
                    todayWh = d.Wh;
                }
                if (d.Day >= monthStart)
                {
                    monthWh += d.Wh;
                }
                if (d.Day >= weekStart)
                {
                    weekWh += d.Wh;
                }
            }
            _tileToday.SetValue(todayWh / 1000.0, "0.000", " 度", Theme.Accent);
            _tileMonth.SetValue(monthWh / 1000.0, "0.000", " 度", Theme.Fg);
            _tileWeekAvg.SetValue(weekWh / 7.0 / 1000.0, "0.000", " 度", Theme.Fg);
            int start = Math.Max(0, daily.Count - 14);
            _dailyBars.SetData(daily.GetRange(start, daily.Count - start));
        }

        private static bool IsRunKeyEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    return key.GetValue(RunValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsElevated()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static bool RunSchtasks(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "schtasks.exe";
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process pr = Process.Start(psi))
                {
                    if (!pr.WaitForExit(15000))
                    {
                        return false;
                    }
                    return pr.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTaskInstalled()
        {
            return RunSchtasks("/Query /TN " + TaskName + " /FO LIST");
        }

        private static bool IsAutoStartEnabled()
        {
            return IsTaskInstalled() || IsRunKeyEnabled();
        }

        private static void RemoveRunKey()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
            }
            catch
            {
            }
        }

        private static void SetRunKey()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null)
                    {
                        key.SetValue(RunValueName, Application.ExecutablePath, RegistryValueKind.String);
                    }
                }
            }
            catch
            {
            }
        }

        private static void SetAutoStart(bool on)
        {
            if (on)
            {
                if (IsElevated())
                {
                    string tr = "\"" + Application.ExecutablePath + "\"";
                    if (RunSchtasks("/Create /TN " + TaskName + " /TR " + tr + " /SC ONLOGON /RL HIGHEST /F"))
                    {
                        RemoveRunKey();
                        return;
                    }
                }
                SetRunKey();
            }
            else
            {
                RunSchtasks("/Delete /TN " + TaskName + " /F");
                RemoveRunKey();
            }
        }

        private void EnsureFirstRunAutoStart()
        {
            bool task = IsTaskInstalled();
            bool run = IsRunKeyEnabled();
            if (IsElevated() && !task)
            {
                // 提权运行时把旧的注册表自启升级为最高权限计划任务
                SetAutoStart(true);
                return;
            }
            if (!task && !run)
            {
                SetAutoStart(true);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Theme.Stroke))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                base.OnFormClosing(e);
                return;
            }
            _mon.CaptureNow();
            _mon.Dispose();
            _showTimer.Stop();
            _showTimer.Dispose();
            _showEvt.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            base.OnFormClosing(e);
        }

        private sealed class ChartCard : Panel
        {
            public ChartCard()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Bg;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (SolidBrush bg = new SolidBrush(Theme.Bg))
                {
                    g.FillRectangle(bg, ClientRectangle);
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
                Theme.TechFrame(g, r, 10, Theme.Card, Theme.Stroke);
                TextRenderer.DrawText(g, "功率曲线 · 开机以来 (W)", Theme.UI(9f, true),
                    new Rectangle(Theme.S(14), Theme.S(10), Theme.S(300), Theme.S(20)), Theme.Fg,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
                Rectangle line = new Rectangle(Theme.S(14), Height - Theme.S(9), Math.Max(2, (Width - Theme.S(28)) / 3), Theme.S(2));
                using (LinearGradientBrush b = new LinearGradientBrush(
                    Theme.GradientRect(line), Theme.Accent, Theme.Alpha(Theme.Accent, 0), LinearGradientMode.Horizontal))
                {
                    g.FillRectangle(b, line);
                }
            }
        }

        private sealed class CardPanel : Panel
        {
            private readonly string _title;

            public CardPanel(string title)
            {
                _title = title;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Bg;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (SolidBrush bg = new SolidBrush(Theme.Bg))
                {
                    g.FillRectangle(bg, ClientRectangle);
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
                Theme.TechFrame(g, r, 10, Theme.Card, Theme.Stroke);
                TextRenderer.DrawText(g, _title, Theme.UI(9f, true),
                    new Rectangle(Theme.S(14), Theme.S(10), Theme.S(200), Theme.S(20)), Theme.Fg,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            }
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected
            {
                get { return Theme.CardHover; }
            }

            public override Color MenuItemBorder
            {
                get { return Theme.StrokeHi; }
            }

            public override Color MenuBorder
            {
                get { return Theme.Stroke; }
            }

            public override Color MenuItemPressedGradientBegin
            {
                get { return Theme.Card; }
            }

            public override Color MenuItemPressedGradientEnd
            {
                get { return Theme.Card; }
            }

            public override Color MenuStripGradientBegin
            {
                get { return Theme.Card; }
            }

            public override Color MenuStripGradientEnd
            {
                get { return Theme.Card; }
            }

            public override Color ToolStripBorder
            {
                get { return Theme.Stroke; }
            }
        }

        private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkColorTable())
            {
            }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Selected ? Theme.Fg : Theme.Dim;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Theme.Dim;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(Theme.Card))
                {
                    e.Graphics.FillRectangle(b, e.Item.Bounds);
                }
                using (Pen pen = new Pen(Theme.Stroke))
                {
                    e.Graphics.DrawLine(pen, 4, e.Item.Height / 2, e.Item.Width - 4, e.Item.Height / 2);
                }
            }
        }
    }
}
