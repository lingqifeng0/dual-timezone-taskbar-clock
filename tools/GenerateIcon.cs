using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;

internal static class GenerateIcon
{
    private static GraphicsPath Rounded(float x, float y, float width, float height, float radius)
    {
        GraphicsPath p = new GraphicsPath(); float d = radius * 2;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + width - d, y, d, d, 270, 90);
        p.AddArc(x + width - d, y + height - d, d, d, 0, 90); p.AddArc(x, y + height - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    private static byte[] Render(int size)
    {
        using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bitmap)) {
            g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality; g.ScaleTransform(size / 256f, size / 256f);
            RectangleF tile = new RectangleF(4, 4, 248, 248);
            using (GraphicsPath shape = Rounded(tile.X, tile.Y, tile.Width, tile.Height, 56))
            using (LinearGradientBrush fill = new LinearGradientBrush(tile, Color.FromArgb(24, 143, 196), Color.FromArgb(27, 58, 116), 50f))
                g.FillPath(fill, shape);
            using (SolidBrush shade = new SolidBrush(Color.FromArgb(40, 0, 15, 38))) g.FillEllipse(shade, 38, 47, 156, 156);
            using (Pen outline = new Pen(Color.FromArgb(246, 251, 255), 11)) g.DrawEllipse(outline, 37, 38, 150, 150);
            using (Pen marks = new Pen(Color.FromArgb(161, 225, 249), 5)) {
                marks.StartCap = LineCap.Round; marks.EndCap = LineCap.Round;
                for (int i = 0; i < 12; ++i) {
                    double angle = Math.PI * 2 * i / 12.0;
                    float sx = (float)Math.Sin(angle), sy = (float)-Math.Cos(angle);
                    g.DrawLine(marks, 112 + sx * 59, 113 + sy * 59, 112 + sx * 64, 113 + sy * 64);
                }
            }
            using (Pen hand = new Pen(Color.White, 9)) {
                hand.StartCap = LineCap.Round; hand.EndCap = LineCap.Round;
                g.DrawLine(hand, 112, 113, 112, 72); g.DrawLine(hand, 112, 113, 139, 127);
            }
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(55, 4, 20, 41))) g.FillEllipse(shadow, 130, 133, 84, 84);
            using (SolidBrush face = new SolidBrush(Color.FromArgb(15, 46, 77))) g.FillEllipse(face, 128, 128, 84, 84);
            using (Pen ring = new Pen(Color.FromArgb(119, 220, 236), 7)) g.DrawEllipse(ring, 128, 128, 84, 84);
            using (Pen hand = new Pen(Color.FromArgb(235, 252, 255), 6)) {
                hand.StartCap = LineCap.Round; hand.EndCap = LineCap.Round;
                g.DrawLine(hand, 170, 170, 170, 148); g.DrawLine(hand, 170, 170, 185, 178);
            }
            using (MemoryStream png = new MemoryStream()) { bitmap.Save(png, ImageFormat.Png); return png.ToArray(); }
        }
    }
    public static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Supply the output .ico path.");
        int[] sizes = { 256, 128, 64, 48, 32, 24, 20, 16 };
        List<byte[]> frames = new List<byte[]>(); foreach (int size in sizes) frames.Add(Render(size));
        using (FileStream file = new FileStream(args[0], FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(file)) {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; ++i) {
                w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
                w.Write(frames[i].Length); w.Write(offset); offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) w.Write(frame);
        }
    }
}
