// Logo üretici: assets/logo.ico (16-256 px, PNG gömülü) ve assets/logo.png (512 px)
// Derleme ve çalıştırma: build.cmd logo
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class LogoGen
{
    static Bitmap Draw(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float s = size / 100f;
            bool tiny = size <= 24;

            // zemin: yuvarlatılmış kare, koyu lacivert degrade
            float r = 22 * s, inset = tiny ? 0 : 2 * s;
            RectangleF bg = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
            using (GraphicsPath p = RoundRect(bg, r))
            using (LinearGradientBrush b = new LinearGradientBrush(bg, Color.FromArgb(0x1E, 0x29, 0x3B), Color.FromArgb(0x0B, 0x11, 0x20), 45f))
                g.FillPath(b, p);

            // sayaç kadranı: 135°'den başlayan 270°'lik iz + yeşil→sarı→turuncu değer yayı
            float aw = (tiny ? 11 : 8) * s;
            RectangleF arc = new RectangleF(17 * s, 17 * s, 66 * s, 66 * s);
            if (!tiny)
                using (Pen track = new Pen(Color.FromArgb(0x33, 0x41, 0x55), aw))
                {
                    track.StartCap = track.EndCap = LineCap.Round;
                    g.DrawArc(track, arc, 135, 270);
                }
            using (LinearGradientBrush gb = new LinearGradientBrush(new RectangleF(arc.X - aw, arc.Y, arc.Width + 2 * aw, arc.Height), Color.Lime, Color.Red, 0f))
            {
                ColorBlend cb = new ColorBlend();
                cb.Colors = new Color[] { Color.FromArgb(0x22, 0xC5, 0x5E), Color.FromArgb(0xFA, 0xCC, 0x15), Color.FromArgb(0xF9, 0x73, 0x16) };
                cb.Positions = new float[] { 0f, 0.55f, 1f };
                gb.InterpolationColors = cb;
                using (Pen val = new Pen(gb, aw))
                {
                    val.StartCap = val.EndCap = LineCap.Round;
                    g.DrawArc(val, arc, 135, tiny ? 270 : 205);
                }
            }

            // şimşek
            PointF[] bolt = {
                P(55, 28, s), P(36, 56, s), P(48.5f, 56, s), P(44, 77, s), P(64, 46, s), P(52.5f, 46, s), P(59.5f, 28, s) };
            if (!tiny)
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddPolygon(bolt);
                    using (Pen glow = new Pen(Color.FromArgb(70, 0xFA, 0xCC, 0x15), 5 * s)) { glow.LineJoin = LineJoin.Round; g.DrawPath(glow, gp); }
                }
            using (LinearGradientBrush bb = new LinearGradientBrush(new RectangleF(36 * s, 28 * s, 28 * s, 49 * s),
                Color.FromArgb(0xFE, 0xF0, 0x8A), Color.FromArgb(0xFA, 0xCC, 0x15), 90f))
                g.FillPolygon(bb, bolt);
        }
        return bmp;
    }

    static PointF P(float x, float y, float s) { return new PointF(x * s, y * s); }

    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        GraphicsPath p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static byte[] Png(Bitmap b)
    {
        using (MemoryStream ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    // Klasik ICO girdisi: BITMAPINFOHEADER + 32 bpp BGRA (alttan üste) + boş AND maskesi.
    // .NET Icon sınıfı küçük boyutlarda PNG girdisini çözemediği için gerekli.
    static byte[] Dib(Bitmap b)
    {
        int w = b.Width, h = b.Height, maskRow = ((w + 31) / 32) * 4;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter o = new BinaryWriter(ms))
        {
            o.Write(40); o.Write(w); o.Write(h * 2); o.Write((short)1); o.Write((short)32);
            o.Write(0); o.Write(w * h * 4 + maskRow * h); o.Write(0); o.Write(0); o.Write(0); o.Write(0);
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    Color c = b.GetPixel(x, y);
                    o.Write(c.B); o.Write(c.G); o.Write(c.R); o.Write(c.A);
                }
            o.Write(new byte[maskRow * h]);
            return ms.ToArray();
        }
    }

    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "assets";
        Directory.CreateDirectory(dir);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var images = new List<byte[]>();
        foreach (int sz in sizes) using (Bitmap b = Draw(sz)) images.Add(sz >= 256 ? Png(b) : Dib(b));

        using (FileStream fs = File.Create(Path.Combine(dir, "logo.ico")))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] img in images) w.Write(img);
        }
        using (Bitmap b = Draw(512)) b.Save(Path.Combine(dir, "logo.png"), ImageFormat.Png);
        Console.WriteLine("logo.ico + logo.png -> " + dir);
    }
}
