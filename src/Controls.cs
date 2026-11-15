using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace PowerPulse
{
    internal class FxControl : Control
    {
        protected bool Hover;

        public FxControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            Hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            Hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }
    }

    internal sealed class CaptionButton : FxControl
    {
        private readonly bool _close;

        public CaptionButton(bool isClose)
        {
            _close = isClose;
            BackColor = Theme.Bg;
            Cursor = Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(Theme.Bg))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = Width / 2;
            int cy = Height / 2;
            if (Hover)
            {
                int r = Theme.S(14);
                Color c = _close ? Theme.Alpha(Theme.Danger, 210) : Theme.Alpha(Theme.Fg, 22);
                using (SolidBrush b = new SolidBrush(c))
                {
                    g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                }
            }
            Color ink = _close
                ? Theme.Lerp(Theme.Dim, Color.White, Hover ? 1f : 0f)
                : (Hover ? Theme.Fg : Theme.Dim);
            int d = Theme.S(5);
            using (Pen pen = new Pen(ink, 1.2f * Theme.Scale))
            {
                if (_close)
                {
                    g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                    g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
                }
                else
                {
                    g.DrawLine(pen, cx - d, cy, cx + d, cy);
                }
            }
        }
    }

    internal sealed class NavButton : FxControl
    {
        public string Text2 = "";

        public bool Selected;

        public event EventHandler Activated;

        public NavButton(string text)
        {
            Text2 = text;
            AccessibleName = text;
            AccessibleRole = AccessibleRole.PushButton;
            BackColor = Theme.Nav;
            Cursor = Cursors.Hand;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            EventHandler h = Activated;
            if (h != null)
            {
                h(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Theme.Nav))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = Theme.Card;
            Color stroke = Theme.Stroke;
            Color ink = Theme.Dim;
            if (Selected)
            {
                fill = Theme.Sel;
                stroke = Theme.Accent;
                ink = Theme.Fg;
            }
            else if (Hover)
            {
                fill = Theme.CardHover;
                stroke = Theme.StrokeHi;
                ink = Theme.Fg;
            }
            Theme.TechFrame(g, r, 6, fill, stroke);
            if (Selected)
            {
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                {
                    g.FillRectangle(b, 0, Theme.S(8), Theme.S(3), Height - Theme.S(16));
                }
            }
            TextRenderer.DrawText(g, Text2, Theme.UI(9f, Selected),
                new Rectangle(Theme.S(14), 0, Width - Theme.S(18), Height), ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class StatTile : Control
    {
        public string Index = "01";

        public string Title = "";

        public Color ValueColor = Theme.Fg;

        // U3: 数值模式带平滑过渡; 文本模式(占位符/提示)直接显示
        private string _text = "--";

        private double _cur;

        private double _target;

        private bool _numeric;

        private string _fmt = "0";

        private string _suffix = "";

        private readonly Timer _anim;

        public StatTile()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            BackColor = Theme.Bg;
            _anim = new Timer();
            _anim.Interval = 40;
            _anim.Tick += delegate { Step(); };
        }

        public void SetText(string text, Color color)
        {
            _numeric = false;
            _text = text;
            ValueColor = color;
            _anim.Stop();
            Invalidate();
        }

        public void SetValue(double v, string fmt, string suffix, Color color)
        {
            ValueColor = color;
            if (!_numeric || _fmt != fmt || _suffix != suffix)
            {
                // 格式切换时直接落位, 避免动画中途出现单位错乱
                _numeric = true;
                _fmt = fmt;
                _suffix = suffix;
                _cur = v;
                _target = v;
                _anim.Stop();
                Invalidate();
                return;
            }
            _target = v;
            if (Math.Abs(_cur - _target) > 0.0005)
            {
                _anim.Start();
            }
        }

        private void Step()
        {
            double d = _target - _cur;
            double eps = Math.Max(0.0005, Math.Abs(_target) * 0.002);
            if (Math.Abs(d) <= eps)
            {
                _cur = _target;
                _anim.Stop();
            }
            else
            {
                _cur += d * 0.35;
            }
            Invalidate();
        }

        private string Display
        {
            get
            {
                if (!_numeric)
                {
                    return _text;
                }
                return _cur.ToString(_fmt, CultureInfo.InvariantCulture) + _suffix;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _anim.Dispose();
            }
            base.Dispose(disposing);
        }

        private static bool IsAscii(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] > 127)
                {
                    return false;
                }
            }
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (SolidBrush bg = new SolidBrush(Theme.Bg))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            string disp = Display;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.TechFrame(g, r, 10, Theme.Card, Theme.Stroke);
            TextRenderer.DrawText(g, Index, Theme.Mono(11f, false),
                new Rectangle(Width - Theme.S(44), Theme.S(10), Theme.S(32), Theme.S(18)), Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Title, Theme.UI(9f, false),
                new Rectangle(Theme.S(14), Theme.S(12), Width - Theme.S(60), Theme.S(18)), Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            Font vf = IsAscii(disp) ? Theme.Mono(16f, true) : Theme.UI(11f, true);
            TextRenderer.DrawText(g, disp, vf,
                new Rectangle(Theme.S(14), Theme.S(38), Width - Theme.S(28), Height - Theme.S(52)), ValueColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            Rectangle line = new Rectangle(Theme.S(14), Height - Theme.S(9), Math.Max(2, (Width - Theme.S(28)) / 3), Theme.S(2));
            using (LinearGradientBrush b = new LinearGradientBrush(
                Theme.GradientRect(line), Theme.Accent, Theme.Alpha(Theme.Accent, 0), LinearGradientMode.Horizontal))
            {
                g.FillRectangle(b, line);
            }
        }
    }

    internal sealed class PrimaryButton : FxControl
    {
        public string Text2 = "";

        public PrimaryButton(string text)
        {
            Text2 = text;
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color face = Parent != null ? Parent.BackColor : Theme.Bg;
            using (SolidBrush bg = new SolidBrush(face))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.TechPath(r, 6))
            {
                Color c1 = Hover ? Theme.Lerp(Theme.Accent, Color.White, 0.12f) : Theme.Accent;
                Color c2 = Hover ? Theme.Lerp(Theme.Accent2, Color.White, 0.12f) : Theme.Accent2;
                using (LinearGradientBrush b = new LinearGradientBrush(
                    Theme.GradientRect(r), c1, c2, LinearGradientMode.Horizontal))
                {
                    g.FillPath(b, path);
                }
                using (Pen pen = new Pen(Theme.Alpha(Color.White, 72)))
                {
                    g.DrawLine(pen, Theme.S(2), Theme.S(1), Width - Theme.S(8), Theme.S(1));
                }
            }
            TextRenderer.DrawText(g, Text2, Theme.UI(9f, true), r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class TechCheck : CheckBox
    {
        private bool _hover;

        public TechCheck(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            ForeColor = Theme.Fg;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(BackColor))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            Rectangle box = new Rectangle(Theme.S(2), (Height - Theme.S(16)) / 2, Theme.S(16), Theme.S(16));
            Color stroke = Checked ? Theme.Accent : (_hover ? Theme.StrokeHi : Theme.Stroke);
            Color fill = Checked ? Theme.Sel : Theme.Inset;
            Theme.TechFrame(g, box, 4, fill, stroke);
            if (Checked)
            {
                using (Pen pen = new Pen(Theme.Accent, 1.8f * Theme.Scale))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new Point[3]
                    {
                        new Point(box.X + Theme.S(4), box.Y + Theme.S(8)),
                        new Point(box.X + Theme.S(7), box.Y + Theme.S(12)),
                        new Point(box.X + Theme.S(13), box.Y + Theme.S(4))
                    });
                }
            }
            Color ink = ForeColor;
            TextRenderer.DrawText(g, Text, Theme.UI(9f, false),
                new Rectangle(box.Right + Theme.S(10), 0, Width - box.Right - Theme.S(12), Height), ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class LineChart : Control
    {
        private static readonly string[] SeriesNames = new string[3] { "总功率", "CPU", "GPU" };

        private IReadOnlyList<PowerSample> _samples;

        private DateTime _boot;

        // 三条曲线显隐: 总功率 / CPU / GPU
        private readonly bool[] _show = new bool[3] { true, true, true };

        private readonly Rectangle[] _legend = new Rectangle[3];

        private Point _mouse;

        public LineChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            BackColor = Theme.Card;
        }

        public void SetData(IReadOnlyList<PowerSample> samples, DateTime boot)
        {
            _samples = samples;
            _boot = boot;
            Invalidate();
        }

        private static Color SeriesColor(int i)
        {
            return i == 0 ? Theme.Accent : (i == 1 ? Theme.ChartCpu : Theme.ChartGpu);
        }

        private static double SeriesValue(PowerSample s, int i)
        {
            return i == 0 ? s.Watts : (i == 1 ? s.CpuW : s.GpuW);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.Location;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (_legend[i].Contains(e.Location))
                    {
                        _show[i] = !_show[i];
                        Invalidate();
                        return;
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(Theme.Inset))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (Pen pen = new Pen(Theme.Stroke))
            {
                g.DrawRectangle(pen, frame);
            }
            Rectangle plot = new Rectangle(Theme.S(46), Theme.S(14), Width - Theme.S(62), Height - Theme.S(40));
            if (plot.Width < 20 || plot.Height < 20)
            {
                return;
            }
            DrawLegend(g, plot);

            DateTime t0 = _boot;
            DateTime t1 = DateTime.Now;
            if (_samples != null && _samples.Count > 0)
            {
                DateTime last = _samples[_samples.Count - 1].Time;
                if (last > t1)
                {
                    t1 = last;
                }
            }
            double span = Math.Max(1.0, (t1 - t0).TotalMinutes);

            double yMax = 100.0;
            if (_samples != null)
            {
                for (int i = 0; i < _samples.Count; i++)
                {
                    for (int s = 0; s < 3; s++)
                    {
                        if (!_show[s])
                        {
                            continue;
                        }
                        double v = SeriesValue(_samples[i], s);
                        if (v > yMax)
                        {
                            yMax = v;
                        }
                    }
                }
            }
            yMax = Math.Ceiling(yMax * 1.1 / 50.0) * 50.0;
            using (Pen grid = new Pen(Theme.Stroke))
            {
                for (int step = 0; step <= 4; step++)
                {
                    int y = plot.Bottom - step * plot.Height / 4;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    int label = (int)Math.Round(yMax * step / 4.0);
                    TextRenderer.DrawText(g, label.ToString(CultureInfo.InvariantCulture), Theme.Mono(7.8f, false),
                        new Rectangle(Theme.S(2), y - Theme.S(8), Theme.S(40), Theme.S(16)), Theme.Faint,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
            using (Pen grid = new Pen(Theme.Stroke))
            {
                for (int i = 0; i <= 6; i++)
                {
                    int x = plot.Left + (int)Math.Round(plot.Width * i / 6.0);
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    DateTime t = t0.AddMinutes(span * i / 6.0);
                    TextRenderer.DrawText(g, t.ToString("HH:mm"), Theme.Mono(7.8f, false),
                        new Rectangle(x - Theme.S(20), plot.Bottom + Theme.S(6), Theme.S(40), Theme.S(14)), Theme.Faint,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
                }
            }

            if (_samples == null || _samples.Count == 0)
            {
                TextRenderer.DrawText(g, "等待功率样本…", Theme.UI(9f, false), plot, Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            PointF[][] series = new PointF[3][];
            bool any = false;
            for (int s = 0; s < 3; s++)
            {
                if (!_show[s])
                {
                    series[s] = new PointF[0];
                    continue;
                }
                List<PointF> pts = new List<PointF>();
                for (int i = 0; i < _samples.Count; i++)
                {
                    double v = SeriesValue(_samples[i], s);
                    if (v < 0.0)
                    {
                        continue;
                    }
                    float x = plot.Left + (float)((_samples[i].Time - t0).TotalMinutes / span) * plot.Width;
                    float y = plot.Bottom - (float)(v / yMax) * plot.Height;
                    pts.Add(new PointF(x, y));
                }
                series[s] = pts.ToArray();
                if (series[s].Length > 0)
                {
                    any = true;
                }
            }
            if (!any)
            {
                TextRenderer.DrawText(g, "等待功率样本…", Theme.UI(9f, false), plot, Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            PointF[] total = series[0];
            if (total.Length >= 2)
            {
                PointF[] area = new PointF[total.Length + 2];
                Array.Copy(total, area, total.Length);
                area[total.Length] = new PointF(total[total.Length - 1].X, plot.Bottom);
                area[total.Length + 1] = new PointF(total[0].X, plot.Bottom);
                using (GraphicsPath ap = new GraphicsPath())
                {
                    ap.AddPolygon(area);
                    using (SolidBrush b = new SolidBrush(Theme.Alpha(Theme.Accent, 26)))
                    {
                        g.FillPath(b, ap);
                    }
                }
            }
            for (int s = 0; s < 3; s++)
            {
                if (series[s].Length < 2)
                {
                    continue;
                }
                using (Pen line = new Pen(SeriesColor(s), (s == 0 ? 1.6f : 1.2f) * Theme.Scale))
                {
                    line.LineJoin = LineJoin.Round;
                    g.DrawLines(line, series[s]);
                }
            }
            if (total.Length > 0)
            {
                PointF lastPt = total[total.Length - 1];
                float hs = Theme.S(5);
                using (SolidBrush halo = new SolidBrush(Theme.Alpha(Theme.Accent, 60)))
                {
                    g.FillEllipse(halo, lastPt.X - hs, lastPt.Y - hs, hs * 2f, hs * 2f);
                }
                using (SolidBrush dot = new SolidBrush(Theme.AccentHi))
                {
                    g.FillEllipse(dot, lastPt.X - hs / 2f, lastPt.Y - hs / 2f, hs, hs);
                }
            }
            DrawHover(g, plot, t0, span, yMax);
        }

        private void DrawLegend(Graphics g, Rectangle plot)
        {
            Font f = Theme.UI(8f, false);
            int x = plot.Right - Theme.S(10);
            for (int i = 2; i >= 0; i--)
            {
                Size ts = TextRenderer.MeasureText(g, SeriesNames[i], f,
                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                int itemW = Theme.S(16) + ts.Width + Theme.S(10);
                Rectangle hit = new Rectangle(x - itemW, plot.Top + Theme.S(2), itemW, Theme.S(18));
                _legend[i] = hit;
                Color c = _show[i] ? SeriesColor(i) : Theme.Faint;
                using (Pen pen = new Pen(c, 2f * Theme.Scale))
                {
                    int ly = hit.Y + hit.Height / 2;
                    g.DrawLine(pen, hit.X, ly, hit.X + Theme.S(12), ly);
                }
                TextRenderer.DrawText(g, SeriesNames[i], f,
                    new Rectangle(hit.X + Theme.S(15), hit.Y, ts.Width + 2, hit.Height),
                    _show[i] ? Theme.Dim : Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x = hit.X - Theme.S(4);
            }
        }

        // U2/U5: 悬停十字线 + 自绘深色数值面板(不走系统白底 ToolTip)
        private void DrawHover(Graphics g, Rectangle plot, DateTime t0, double span, double yMax)
        {
            if (_samples == null || _samples.Count == 0)
            {
                return;
            }
            if (_mouse.X < plot.Left || _mouse.X > plot.Right || _mouse.Y < plot.Top || _mouse.Y > plot.Bottom)
            {
                return;
            }
            double frac = (_mouse.X - plot.Left) / (double)plot.Width;
            DateTime tAt = t0.AddMinutes(span * frac);
            int idx = 0;
            double best = double.MaxValue;
            for (int i = 0; i < _samples.Count; i++)
            {
                double d = Math.Abs((_samples[i].Time - tAt).TotalMinutes);
                if (d < best)
                {
                    best = d;
                    idx = i;
                }
            }
            PowerSample s = _samples[idx];
            float x = plot.Left + (float)((s.Time - t0).TotalMinutes / span) * plot.Width;
            using (Pen vpen = new Pen(Theme.StrokeHi, 1f))
            {
                vpen.DashStyle = DashStyle.Dot;
                g.DrawLine(vpen, x, plot.Top, x, plot.Bottom);
            }
            List<string> lines = new List<string>();
            lines.Add(s.Time.ToString("HH:mm:ss"));
            for (int ser = 0; ser < 3; ser++)
            {
                if (!_show[ser])
                {
                    continue;
                }
                double v = SeriesValue(s, ser);
                lines.Add(SeriesNames[ser] + ": " + (v >= 0.0 ? v.ToString("0.0", CultureInfo.InvariantCulture) + " W" : "--"));
                if (v >= 0.0)
                {
                    float y = plot.Bottom - (float)(v / yMax) * plot.Height;
                    using (SolidBrush b = new SolidBrush(SeriesColor(ser)))
                    {
                        g.FillEllipse(b, x - Theme.S(3), y - Theme.S(3), Theme.S(6), Theme.S(6));
                    }
                }
            }
            Font f = Theme.Mono(8f, false);
            int bw = Theme.S(120);
            for (int i = 0; i < lines.Count; i++)
            {
                Size ts = TextRenderer.MeasureText(g, lines[i], f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                if (ts.Width + Theme.S(20) > bw)
                {
                    bw = ts.Width + Theme.S(20);
                }
            }
            int bh = lines.Count * Theme.S(16) + Theme.S(12);
            int bx = (int)x + Theme.S(10);
            if (bx + bw > plot.Right)
            {
                bx = (int)x - bw - Theme.S(10);
            }
            int by = _mouse.Y - bh / 2;
            if (by < plot.Top)
            {
                by = plot.Top;
            }
            if (by + bh > plot.Bottom)
            {
                by = plot.Bottom - bh;
            }
            Rectangle box = new Rectangle(bx, by, bw, bh);
            using (GraphicsPath path = Theme.Rounded(box, 4))
            {
                using (SolidBrush b = new SolidBrush(Theme.Alpha(Theme.Card, 242)))
                {
                    g.FillPath(b, path);
                }
                using (Pen pen = new Pen(Theme.StrokeHi))
                {
                    g.DrawPath(pen, path);
                }
            }
            for (int i = 0; i < lines.Count; i++)
            {
                Rectangle lr = new Rectangle(box.X + Theme.S(8), box.Y + Theme.S(6) + i * Theme.S(16), bw - Theme.S(16), Theme.S(16));
                TextRenderer.DrawText(g, lines[i], f, lr, i == 0 ? Theme.Dim : Theme.Fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    internal sealed class RecordsView : Control
    {
        private const int MaxRows = 12;

        private IReadOnlyList<SessionRecord> _rows;

        public RecordsView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            BackColor = Theme.Card;
        }

        public void SetData(IReadOnlyList<SessionRecord> rows)
        {
            _rows = rows;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(Theme.Card))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            Rectangle frame = new Rectangle(0, 0, Width - 1, Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Theme.Stroke))
            {
                g.DrawRectangle(pen, frame);
            }
            int rowH = Theme.S(26);
            int[] colX = new int[6] { Theme.S(14), Theme.S(164), Theme.S(314), Theme.S(424), Theme.S(484), Theme.S(544) };
            string[] heads = new string[6] { "开机时刻", "最近记录", "运行时长", "平均W", "峰值W", "累计(度)" };
            Font hf = Theme.UI(8f, true);
            for (int i = 0; i < 6; i++)
            {
                int cw = (i < 5 ? colX[i + 1] : Width - Theme.S(10)) - colX[i] - Theme.S(8);
                TextRenderer.DrawText(g, heads[i], hf, new Rectangle(colX[i], Theme.S(6), cw, Theme.S(16)), Theme.Dim,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            }
            using (Pen sep = new Pen(Theme.Stroke))
            {
                g.DrawLine(sep, Theme.S(10), Theme.S(26), Width - Theme.S(10), Theme.S(26));
            }
            if (_rows == null || _rows.Count == 0)
            {
                TextRenderer.DrawText(g, "暂无开机记录", Theme.UI(9f, false),
                    new Rectangle(0, Theme.S(40), Width, Theme.S(30)), Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
                return;
            }
            int n = Math.Min(MaxRows, _rows.Count);
            Font f = Theme.Mono(8.5f, false);
            for (int i = 0; i < n; i++)
            {
                SessionRecord r = _rows[_rows.Count - 1 - i];
                int y = Theme.S(30) + i * rowH;
                if (y + rowH > Height - Theme.S(4))
                {
                    break;
                }
                if (i % 2 == 0)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Inset))
                    {
                        g.FillRectangle(b, new Rectangle(1, y, Width - 2, rowH));
                    }
                }
                TextFormatFlags cellFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
                Rectangle cell0 = new Rectangle(colX[0], y, colX[1] - colX[0] - Theme.S(8), rowH);
                TextRenderer.DrawText(g, r.BootTime, f, cell0, Theme.Fg, cellFlags);
                Rectangle cell1 = new Rectangle(colX[1], y, colX[2] - colX[1] - Theme.S(8), rowH);
                TextRenderer.DrawText(g, r.LastRecord, f, cell1, Theme.Dim, cellFlags);
                Rectangle cell2 = new Rectangle(colX[2], y, colX[3] - colX[2] - Theme.S(8), rowH);
                TextRenderer.DrawText(g, r.Duration, f, cell2, Theme.Dim, cellFlags);
                Rectangle cell3 = new Rectangle(colX[3], y, colX[4] - colX[3] - Theme.S(8), rowH);
                TextRenderer.DrawText(g, (r.AvgW >= 0.0 ? r.AvgW.ToString("0.0", CultureInfo.InvariantCulture) : "--"),
                    f, cell3, Theme.Accent, cellFlags);
                Rectangle cell4 = new Rectangle(colX[4], y, colX[5] - colX[4] - Theme.S(8), rowH);
                TextRenderer.DrawText(g, (r.PeakW >= 0.0 ? r.PeakW.ToString("0.0", CultureInfo.InvariantCulture) : "--"),
                    f, cell4, Theme.Warning, cellFlags);
                Rectangle cell5 = new Rectangle(colX[5], y, Width - colX[5] - Theme.S(10), rowH);
                TextRenderer.DrawText(g, (r.CumWh / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " 度",
                    f, cell5, Theme.Fg, cellFlags);
            }
        }
    }

    internal sealed class DailyBars : Control
    {
        private IReadOnlyList<DailyStat> _days;

        public DailyBars()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            BackColor = Theme.Inset;
        }

        public void SetData(IReadOnlyList<DailyStat> days)
        {
            _days = days;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(Theme.Inset))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            Rectangle frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (Pen pen = new Pen(Theme.Stroke))
            {
                g.DrawRectangle(pen, frame);
            }
            if (_days == null || _days.Count == 0)
            {
                TextRenderer.DrawText(g, "暂无每日数据", Theme.UI(9f, false), ClientRectangle, Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle plot = new Rectangle(Theme.S(46), Theme.S(14), Width - Theme.S(62), Height - Theme.S(44));
            double maxWh = 0.05;
            for (int i = 0; i < _days.Count; i++)
            {
                if (_days[i].Wh > maxWh)
                {
                    maxWh = _days[i].Wh;
                }
            }
            double top = Math.Ceiling(maxWh / 100.0) * 100.0;
            if (top < 50.0)
            {
                top = 50.0;
            }
            using (Pen grid = new Pen(Theme.Stroke))
            {
                for (int step = 0; step <= 4; step++)
                {
                    int y = plot.Bottom - step * plot.Height / 4;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    double wh = top * step / 4.0;
                    TextRenderer.DrawText(g, (wh / 1000.0).ToString("0.0", CultureInfo.InvariantCulture), Theme.Mono(7.8f, false),
                        new Rectangle(Theme.S(2), y - Theme.S(8), Theme.S(40), Theme.S(16)), Theme.Faint,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                using (Font yf = Theme.UI(7.5f, false))
                {
                    TextRenderer.DrawText(g, "度", yf, new Rectangle(Theme.S(2), plot.Top - Theme.S(14), Theme.S(30), Theme.S(12)),
                        Theme.Faint, TextFormatFlags.Right | TextFormatFlags.NoPadding);
                }
            }
            DateTime today = DateTime.Now.Date;
            int n = _days.Count;
            float slot = plot.Width / (float)n;
            float barW = Math.Max(Theme.S(4), slot * 0.55f);
            for (int i = 0; i < n; i++)
            {
                DailyStat d = _days[i];
                float cx = plot.Left + slot * i + slot / 2f;
                float hgt = (float)(d.Wh / top) * plot.Height;
                if (hgt < 1f && d.Wh > 0.0)
                {
                    hgt = 1f;
                }
                RectangleF bar = new RectangleF(cx - barW / 2f, plot.Bottom - hgt, barW, hgt);
                bool isToday = d.Day == today;
                if (bar.Height > 0.5f)
                {
                    using (LinearGradientBrush lb = new LinearGradientBrush(
                        new Rectangle((int)bar.X, (int)bar.Y, Math.Max(1, (int)bar.Width), Math.Max(1, (int)bar.Height)),
                        isToday ? Theme.AccentHi : Theme.Accent,
                        Theme.Alpha(isToday ? Theme.Accent : Theme.Accent2, 160),
                        LinearGradientMode.Vertical))
                    {
                        g.FillRectangle(lb, bar);
                    }
                }
                TextRenderer.DrawText(g, d.Day.ToString("MM-dd"), Theme.Mono(7f, false),
                    new Rectangle((int)cx - Theme.S(20), plot.Bottom + Theme.S(4), Theme.S(40), Theme.S(12)),
                    isToday ? Theme.Fg : Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            }
        }
    }

    internal sealed class TitleBar : Panel
    {
        private readonly CaptionButton _min;

        private readonly CaptionButton _close;

        public TitleBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Height = Theme.S(44);
            _min = new CaptionButton(false);
            _min.Size = new Size(Theme.S(40), Theme.S(36));
            _min.Click += delegate
            {
                Form f = FindForm();
                if (f != null)
                {
                    f.WindowState = FormWindowState.Minimized;
                }
            };
            _close = new CaptionButton(true);
            _close.Size = new Size(Theme.S(40), Theme.S(36));
            _close.Click += delegate
            {
                Form f = FindForm();
                MainForm mp = f as MainForm;
                if (mp != null)
                {
                    mp.MinimizeToTray();
                }
            };
            Controls.Add(_min);
            Controls.Add(_close);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_min == null || _close == null)
            {
                return;
            }
            _min.Location = new Point(Width - Theme.S(82), Theme.S(4));
            _close.Location = new Point(Width - Theme.S(42), Theme.S(4));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                Form f = FindForm();
                if (f != null)
                {
                    Native.ReleaseCapture();
                    Native.SendMessage(f.Handle, Native.WM_NCLBUTTONDOWN,
                        new IntPtr(Native.HTCAPTION), IntPtr.Zero);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(Theme.Bg))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle badge = new Rectangle(Theme.S(16), Theme.S(14), Theme.S(16), Theme.S(16));
            Theme.TechFrame(g, badge, 4, Theme.Sel, Theme.Accent);
            Theme.DrawPulse(g, new Rectangle(Theme.S(19), Theme.S(17), Theme.S(10), Theme.S(9)), Theme.Accent);
            TextRenderer.DrawText(g, "PowerPulse · 耗电追踪", Theme.UI(10.5f, true),
                new Rectangle(Theme.S(40), 0, Width - Theme.S(170), Height), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (Pen pen = new Pen(Theme.Stroke))
            {
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }
        }
    }

    internal sealed class NavPanel : Panel
    {
        public NavPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Nav;
            Width = Theme.S(180);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(Theme.Nav))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle badge = new Rectangle(Theme.S(16), Theme.S(16), Theme.S(26), Theme.S(26));
            Theme.TechFrame(g, badge, 6, Theme.Sel, Theme.Accent);
            Theme.DrawPulse(g, new Rectangle(badge.X + Theme.S(6), badge.Y + Theme.S(8), Theme.S(14), Theme.S(10)), Theme.Accent);
            TextRenderer.DrawText(g, "PowerPulse", Theme.UI(10.5f, true),
                new Rectangle(Theme.S(50), Theme.S(14), Width - Theme.S(58), Theme.S(18)), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "POWER · SESSION", Theme.Mono(7.8f, false),
                new Rectangle(Theme.S(50), Theme.S(33), Width - Theme.S(58), Theme.S(14)), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.NoPadding);
            using (Pen pen = new Pen(Theme.Stroke))
            {
                g.DrawLine(pen, Theme.S(16), Theme.S(58), Width - Theme.S(16), Theme.S(58));
            }
        }
    }
}
