// 本地图标生成器：绘制 PowerPulse 品牌图标（深色圆角方块 + 绿色闪电），
// 输出多尺寸 32bpp ICO（16/24/32/48/64/128/256），全部使用 BMP 编码以保证
// .NET Framework Icon 类与托盘/任务栏在各 DPI 下均可正确加载。
// 编译: csc /r:System.Drawing.dll IconGen.cs   运行: IconGen.exe <输出路径>
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IconGen
{
    // 与 Theme.cs 一致的品牌色(电竞红)
    private static readonly Color TileTop = Color.FromArgb(30, 35, 42);
    private static readonly Color TileBottom = Color.FromArgb(9, 11, 13);
    private static readonly Color TileStroke = Color.FromArgb(74, 84, 97);
    private static readonly Color Pulse = Color.FromArgb(255, 92, 112);
    private static readonly Color PulseShade = Color.FromArgb(150, 18, 40);

    private static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        int[] sizes = new int[7] { 16, 24, 32, 48, 64, 128, 256 };
        byte[][] images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            images[i] = RenderBmpEntry(sizes[i]);
        }
        using (FileStream fs = File.Create(outPath))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);
            w.Write((ushort)1); // ICONTYPE_ICON
            w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write(images[i]);
            }
        }
        Console.WriteLine("written: " + outPath);
        return 0;
    }

    // 生成一个 BMP 编码的 ICO 目录项：BITMAPINFOHEADER + 自底向上 BGRA + 1bpp AND 掩码
    private static byte[] RenderBmpEntry(int size)
    {
        using (Bitmap bmp = Draw(size))
        {
            Rectangle rect = new Rectangle(0, 0, size, size);
            BitmapData data = bmp.LockBits(
                rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int maskRow = ((size + 31) / 32) * 4;
            byte[] px = new byte[size * size * 4];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(40);            // biSize
                w.Write(size);          // biWidth
                w.Write(size * 2);      // biHeight (XOR + AND)
                w.Write((ushort)1);     // biPlanes
                w.Write((ushort)32);    // biBitCount
                w.Write(0);             // biCompression BI_RGB
                w.Write(px.Length + maskRow * size);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                // 自底向上写像素
                for (int y = size - 1; y >= 0; y--)
                {
                    w.Write(px, y * size * 4, size * 4);
                }
                // AND 掩码全 0（透明由 alpha 通道决定）
                w.Write(new byte[maskRow * size]);
                return ms.ToArray();
            }
        }
    }

    private static Bitmap Draw(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingMode = CompositingMode.SourceOver;
            float u = size;

            // 圆角深色底块
            float pad = u * 0.02f;
            RectangleF tile = new RectangleF(pad, pad, u - pad * 2f, u - pad * 2f);
            float rad = Math.Max(2f, u * 0.22f);
            using (GraphicsPath path = Round(tile, rad))
            {
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new RectangleF(0, 0, u, u), TileTop, TileBottom, 90f))
                {
                    g.FillPath(br, path);
                }
                if (size >= 24)
                {
                    using (Pen pen = new Pen(TileStroke, Math.Max(1f, u * 0.03f)))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            // 脉冲波形(心电图), 与 Pavise 的闪电区分
            RectangleF pulse = new RectangleF(u * 0.10f, u * 0.26f, u * 0.80f, u * 0.48f);
            float penW = Math.Max(1.5f, u * 0.09f);
            PointF[] pts = PulsePoints(pulse);
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (size >= 48)
            {
                using (LinearGradientBrush gb = new LinearGradientBrush(
                    pulse, Pulse, PulseShade, 90f))
                {
                    using (Pen pen = new Pen(gb, penW))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;
                        g.DrawLines(pen, pts);
                    }
                }
            }
            else
            {
                using (Pen pen = new Pen(Pulse, penW))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, pts);
                }
            }
            g.SmoothingMode = old;
        }
        return bmp;
    }

    private static GraphicsPath Round(RectangleF r, float rad)
    {
        GraphicsPath p = new GraphicsPath();
        float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180f, 90f);
        p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
        p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
        p.CloseFigure();
        return p;
    }

    private static PointF[] PulsePoints(RectangleF r)
    {
        float w = r.Width, h = r.Height;
        return new PointF[8]
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
    }
}
