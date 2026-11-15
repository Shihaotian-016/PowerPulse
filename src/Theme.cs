using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace PowerPulse
{
    internal static class Theme
    {
        // DPI 缩放系数: MainForm 构造时按 DeviceDpi/96 设置; 未设置前按系统 DPI 惰性探测。
        private static float _scale = -1f;

        public static float Scale
        {
            get
            {
                if (_scale < 0f)
                {
                    float s = 1f;
                    try
                    {
                        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                        {
                            if (g.DpiX > 0f)
                            {
                                s = g.DpiX / 96f;
                            }
                        }
                    }
                    catch
                    {
                    }
                    _scale = s;
                }
                return _scale;
            }
            set
            {
                _scale = value;
            }
        }

        public static int S(int v)
        {
            return (int)Math.Round((double)v * (double)Scale, MidpointRounding.AwayFromZero);
        }

        public static readonly Color Bg = Color.FromArgb(9, 10, 12);
        public static readonly Color Nav = Color.FromArgb(6, 7, 9);
        public static readonly Color Card = Color.FromArgb(17, 19, 23);
        public static readonly Color CardHover = Color.FromArgb(23, 27, 33);
        public static readonly Color Inset = Color.FromArgb(7, 8, 10);
        public static readonly Color Stroke = Color.FromArgb(39, 44, 52);
        public static readonly Color StrokeHi = Color.FromArgb(72, 81, 94);
        public static readonly Color Fg = Color.FromArgb(244, 246, 249);
        public static readonly Color Dim = Color.FromArgb(163, 170, 181);
        public static readonly Color Faint = Color.FromArgb(91, 100, 113);
        public static readonly Color Accent = Color.FromArgb(235, 45, 75);
        public static readonly Color Accent2 = Color.FromArgb(150, 18, 40);
        public static readonly Color AccentHi = Color.FromArgb(255, 92, 112);
        public static readonly Color ChartCpu = Color.FromArgb(96, 165, 250);
        public static readonly Color ChartGpu = Color.FromArgb(250, 190, 80);
        public static readonly Color Danger = Color.FromArgb(255, 72, 88);
        public static readonly Color Warning = Color.FromArgb(255, 190, 75);

        public static Color Sel
        {
            get { return Lerp(Card, Accent, 0.2f); }
        }

        private static readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();

        private static readonly object _fontLk = new object();

        // 注意: DPI 感知进程中 GDI(TextRenderer) 已按系统 DPI 自动缩放点数字号,
        // 此处不可再乘 Scale, 否则文字相对布局会双重放大(实测 125% 下截断/过大)。
        public static Font UI(float size, bool bold)
        {
            string key = "u" + size.ToString("0.####", CultureInfo.InvariantCulture) + (bold ? "b" : "r");
            lock (_fontLk)
            {
                Font f;
                if (!_fonts.TryGetValue(key, out f))
                {
                    f = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
                    _fonts[key] = f;
                }
                return f;
            }
        }

        public static Font Mono(float size, bool bold)
        {
            string key = "m" + size.ToString("0.####", CultureInfo.InvariantCulture) + (bold ? "b" : "r");
            lock (_fontLk)
            {
                Font f;
                if (!_fonts.TryGetValue(key, out f))
                {
                    f = new Font("Consolas", size, bold ? FontStyle.Bold : FontStyle.Regular);
                    _fonts[key] = f;
                }
                return f;
            }
        }

        public static Color Lerp(Color a, Color b, float t)
        {
            if (t < 0f)
            {
                t = 0f;
            }
            if (t > 1f)
            {
                t = 1f;
            }
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a)
        {
            if (a < 0)
            {
                a = 0;
            }
            if (a > 255)
            {
                a = 255;
            }
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        public static GraphicsPath TechPath(Rectangle r, int cut)
        {
            GraphicsPath p = new GraphicsPath();
            cut = S(cut);
            cut = Math.Max(1, Math.Min(cut, Math.Min(r.Width, r.Height) / 3));
            p.AddPolygon(new Point[6]
            {
                new Point(r.Left, r.Top),
                new Point(r.Right - cut, r.Top),
                new Point(r.Right, r.Top + cut),
                new Point(r.Right, r.Bottom),
                new Point(r.Left + cut, r.Bottom),
                new Point(r.Left, r.Bottom - cut)
            });
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath Rounded(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            rad = S(rad);
            if (rad <= 0)
            {
                p.AddRectangle(r);
                p.CloseFigure();
                return p;
            }
            int d = rad * 2;
            if (d > r.Width)
            {
                d = r.Width;
            }
            if (d > r.Height)
            {
                d = r.Height;
            }
            if (d <= 0)
            {
                p.AddRectangle(r);
                p.CloseFigure();
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        public static void TechFrame(Graphics g, Rectangle r, int cut, Color fill, Color stroke)
        {
            using (GraphicsPath path = TechPath(r, cut))
            {
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, path);
                }
                if (!stroke.IsEmpty)
                {
                    using (Pen pen = new Pen(stroke))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }
        }

        // 品牌脉冲波形(心电图式), 替代与 Pavise 撞型的闪电
        public static void DrawPulse(Graphics g, Rectangle r, Color c)
        {
            float w = r.Width;
            float h = r.Height;
            PointF[] pts = new PointF[8]
            {
                new PointF(r.X + w * 0.04f, r.Y + h * 0.55f),
                new PointF(r.X + w * 0.24f, r.Y + h * 0.55f),
                new PointF(r.X + w * 0.32f, r.Y + h * 0.40f),
                new PointF(r.X + w * 0.40f, r.Y + h * 0.62f),
                new PointF(r.X + w * 0.50f, r.Y + h * 0.10f),
                new PointF(r.X + w * 0.60f, r.Y + h * 0.80f),
                new PointF(r.X + w * 0.68f, r.Y + h * 0.55f),
                new PointF(r.X + w * 0.96f, r.Y + h * 0.55f)
            };
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(c, Math.Max(1.2f, h * 0.16f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, pts);
            }
            g.SmoothingMode = old;
        }

        public static Rectangle GradientRect(Rectangle r)
        {
            if (r.Width < 1)
            {
                r.Width = 1;
            }
            if (r.Height < 1)
            {
                r.Height = 1;
            }
            return r;
        }
    }
}
