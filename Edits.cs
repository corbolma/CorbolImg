// Ritocchi dei riquadri della Griglia IA, chiesti all'assistente ("schiarisci B2", "sfoca la targa in D4").
// Niente librerie esterne: GDI+ per rotazioni e specchio, il resto con calcoli sui pixel (32 bit BGRA).
// Ogni ritocco si può limitare a una zona del riquadro (frazioni 0-1), così resta valido anche dopo un
// ingrandimento con Real-ESRGAN e si può riportare dal riquadro aperto a quello del livello superiore.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace CorbolImg
{
    class EditOp
    {
        public string Op;
        public int Amount;
        public string Dir;                                      // flip: horizontal / vertical
        public RectangleF Area = new RectangleF(0, 0, 1, 1);    // zona del riquadro, in frazioni

        public bool Whole { get { return Area.X <= 0.0005f && Area.Y <= 0.0005f && Area.Right >= 0.9995f && Area.Bottom >= 0.9995f; } }
        public bool Geometric { get { return Op == "rotate" || Op == "flip"; } }

        public EditOp WithArea(RectangleF a) { return new EditOp { Op = Op, Amount = Amount, Dir = Dir, Area = a }; }

        static string Signed(int v) { return (v > 0 ? "+" : "") + v.ToString(CultureInfo.InvariantCulture); }

        // Per l'assistente (inglese)
        public string Key()
        {
            string s = Op;
            if (Op == "flip") s += " " + Dir;
            else if (Op == "rotate") s += " " + Amount + "°";
            else if (ImageEdits.HasAmount(Op)) s += " " + (ImageEdits.Signed(Op) ? Signed(Amount) : Amount.ToString(CultureInfo.InvariantCulture));
            if (!Whole) s += string.Format(CultureInfo.InvariantCulture, " area [{0:0.##},{1:0.##},{2:0.##},{3:0.##}]", Area.X, Area.Y, Area.Width, Area.Height);
            return s;
        }

        // Per l'utente
        public string Describe()
        {
            string s;
            switch (Op)
            {
                case "brightness": s = L.T("luminosità", "brightness") + " " + Signed(Amount); break;
                case "contrast": s = L.T("contrasto", "contrast") + " " + Signed(Amount); break;
                case "saturation": s = L.T("saturazione", "saturation") + " " + Signed(Amount); break;
                case "temperature": s = (Amount >= 0 ? L.T("colori più caldi", "warmer colors") : L.T("colori più freddi", "cooler colors")) + " " + Signed(Amount); break;
                case "auto_levels": s = L.T("livelli automatici", "auto levels"); break;
                case "grayscale": s = L.T("bianco e nero", "black and white"); break;
                case "sepia": s = L.T("seppia", "sepia"); break;
                case "sharpen": s = L.T("nitidezza", "sharpness") + " " + Amount; break;
                case "denoise": s = L.T("riduzione rumore", "noise reduction") + " " + Amount; break;
                case "blur": s = L.T("sfocatura", "blur") + " " + Amount; break;
                case "pixelate": s = L.T("pixel grossi", "pixelate") + " " + Amount; break;
                case "rotate": s = L.T("rotazione", "rotation") + " " + Amount + "°"; break;
                case "flip": s = Dir == "vertical" ? L.T("specchio verticale", "vertical flip") : L.T("specchio orizzontale", "horizontal flip"); break;
                default: s = Op; break;
            }
            return Whole ? s : s + " " + L.T("(zona)", "(area)");
        }
    }

    static class ImageEdits
    {
        public static readonly string[] Ops = { "brightness", "contrast", "saturation", "temperature", "auto_levels", "grayscale", "sepia",
                                                "sharpen", "denoise", "blur", "pixelate", "rotate", "flip" };
        public const long MaxPixels = 50000000;   // oltre, i calcoli sui pixel chiedono troppa memoria

        public static bool HasAmount(string op) { return op != "auto_levels" && op != "grayscale" && op != "sepia" && op != "flip"; }
        public static bool Signed(string op) { return op == "brightness" || op == "contrast" || op == "saturation" || op == "temperature"; }

        static double Num(object v, double def)
        {
            if (v == null) return def;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        // Azione dell'assistente → ritocco; null con l'errore in inglese (va anche all'assistente)
        public static EditOp Parse(object a, out string error)
        {
            error = null;
            string op = (AiJson.Str(a, "op") ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            if (op == "levels" || op == "auto_level" || op == "autolevels") op = "auto_levels";
            if (op == "greyscale" || op == "black_and_white") op = "grayscale";
            if (Array.IndexOf(Ops, op) < 0) { error = "unknown edit op '" + op + "'"; return null; }
            var e = new EditOp { Op = op };
            double amt = Num(AiJson.Get(a, "amount"), double.NaN);
            switch (op)
            {
                case "brightness": case "contrast": case "saturation": case "temperature":
                    if (double.IsNaN(amt)) { error = op + " needs an amount from -100 to 100"; return null; }
                    e.Amount = Clamp((int)Math.Round(amt), -100, 100);
                    break;
                case "sharpen": case "denoise": case "blur":
                    e.Amount = Clamp((int)Math.Round(double.IsNaN(amt) ? 40 : amt), 1, 100);
                    break;
                case "pixelate":
                    e.Amount = Clamp((int)Math.Round(double.IsNaN(amt) ? 10 : amt), 2, 100);
                    break;
                case "rotate":
                    if (double.IsNaN(amt)) { error = "rotate needs an amount in degrees"; return null; }
                    int deg = (int)Math.Round(amt) % 360;
                    if (deg > 180) deg -= 360;
                    if (deg <= -180) deg += 360;
                    bool right = deg % 90 == 0;
                    if (!right && Math.Abs(deg) > 45) { error = "rotate: use 90, 180 or 270, or a small angle (-45 to 45) to straighten"; return null; }
                    e.Amount = deg == -90 ? 270 : deg == 180 || deg == -180 ? 180 : deg;
                    if (e.Amount == 0) { error = "rotate: the angle is 0"; return null; }
                    break;
                case "flip":
                    string d = (AiJson.Str(a, "dir") ?? AiJson.Str(a, "direction") ?? "horizontal").ToLowerInvariant();
                    e.Dir = d.StartsWith("v") ? "vertical" : "horizontal";
                    break;
            }
            var area = AiJson.Get(a, "area") as IList;
            if (area != null && !e.Geometric)
            {
                if (area.Count != 4) { error = "area must be [x, y, width, height] as fractions 0-1 of the cell"; return null; }
                double x = Num(area[0], 0), y = Num(area[1], 0), w = Num(area[2], 1), h = Num(area[3], 1);
                x = Math.Max(0, Math.Min(1, x)); y = Math.Max(0, Math.Min(1, y));
                w = Math.Min(1 - x, w); h = Math.Min(1 - y, h);
                if (w <= 0.001 || h <= 0.001) { error = "area is empty"; return null; }
                e.Area = new RectangleF((float)x, (float)y, (float)w, (float)h);
            }
            return e;
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
        static byte B(double v) { return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5); }

        public static Bitmap ApplyAll(Bitmap src, IEnumerable<EditOp> ops)
        {
            var cur = new Bitmap(src);
            foreach (var op in ops)
            {
                var next = Apply(cur, op);
                cur.Dispose();
                cur = next;
            }
            return cur;
        }

        // Nuova bitmap 32 bit con il ritocco applicato (src non cambia)
        public static Bitmap Apply(Bitmap src, EditOp op)
        {
            if (op.Op == "rotate") return Rotate(src, op.Amount);
            if (op.Op == "flip")
            {
                var f = Copy(src);
                f.RotateFlip(op.Dir == "vertical" ? RotateFlipType.RotateNoneFlipY : RotateFlipType.RotateNoneFlipX);
                return f;
            }
            var res = Copy(src);
            Rectangle rc = op.Whole ? new Rectangle(0, 0, res.Width, res.Height) : ToPixels(op.Area, res.Size);
            var data = res.LockBits(rc, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride, w = rc.Width, h = rc.Height;
                var px = new byte[stride * h];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                Pixels(px, w, h, stride, op);
                Marshal.Copy(px, 0, data.Scan0, px.Length);
            }
            finally { res.UnlockBits(data); }
            return res;
        }

        public static Rectangle ToPixels(RectangleF a, Size s)
        {
            int x0 = Clamp((int)Math.Round(a.X * s.Width), 0, s.Width - 1), y0 = Clamp((int)Math.Round(a.Y * s.Height), 0, s.Height - 1);
            int x1 = Clamp((int)Math.Round(a.Right * s.Width), x0 + 1, s.Width), y1 = Clamp((int)Math.Round(a.Bottom * s.Height), y0 + 1, s.Height);
            return new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }

        static Bitmap Copy(Bitmap src)
        {
            var b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            b.SetResolution(src.HorizontalResolution, src.VerticalResolution);
            using (var g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
            }
            return b;
        }

        // ---------- geometria ----------

        // 90/180/270: rotazione esatta; angoli piccoli: raddrizza e taglia i bordi vuoti, stesse dimensioni
        static Bitmap Rotate(Bitmap src, int deg)
        {
            if (deg == 90 || deg == 180 || deg == 270)
            {
                var r = Copy(src);
                r.RotateFlip(deg == 90 ? RotateFlipType.Rotate90FlipNone : deg == 180 ? RotateFlipType.Rotate180FlipNone : RotateFlipType.Rotate270FlipNone);
                return r;
            }
            int w = src.Width, h = src.Height;
            double t = Math.Abs(deg) * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
            double k = Math.Min(w / (w * c + h * s), h / (w * s + h * c));   // rettangolo pieno più grande con le stesse proporzioni
            var res = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            res.SetResolution(src.HorizontalResolution, src.VerticalResolution);
            using (var g = Graphics.FromImage(res))
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);   // niente bordi scuri sui lati
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.TranslateTransform(w / 2f, h / 2f);
                g.RotateTransform(deg);
                g.ScaleTransform((float)(1 / k), (float)(1 / k));
                g.TranslateTransform(-w / 2f, -h / 2f);
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);
            }
            return res;
        }

        // ---------- pixel (BGRA) ----------

        static void Pixels(byte[] px, int w, int h, int stride, EditOp op)
        {
            int a = op.Amount;
            switch (op.Op)
            {
                case "brightness":
                    {
                        // curva gamma: schiarisce o scurisce i toni medi senza bruciare bianchi e neri
                        double g = Math.Pow(2, -a / 50.0);
                        var lut = new byte[256];
                        for (int i = 0; i < 256; i++) lut[i] = B(255 * Math.Pow(i / 255.0, g));
                        Lut(px, w, h, stride, lut, lut, lut);
                        break;
                    }
                case "contrast":
                    {
                        double f = a >= 0 ? 1 + a / 50.0 : 1 + a / 100.0;
                        var lut = new byte[256];
                        for (int i = 0; i < 256; i++) lut[i] = B((i - 128) * f + 128);
                        Lut(px, w, h, stride, lut, lut, lut);
                        break;
                    }
                case "temperature":
                    {
                        double k = a * 0.35;
                        var lr = new byte[256]; var lg = new byte[256]; var lb = new byte[256];
                        for (int i = 0; i < 256; i++) { lr[i] = B(i + k); lg[i] = B(i + k * 0.15); lb[i] = B(i - k); }
                        Lut(px, w, h, stride, lr, lg, lb);
                        break;
                    }
                case "auto_levels": AutoLevels(px, w, h, stride); break;
                case "saturation": Saturate(px, w, h, stride, 1 + a / 100.0); break;
                case "grayscale": Saturate(px, w, h, stride, 0); break;
                case "sepia":
                    for (int y = 0; y < h; y++)
                        for (int x = 0, i = y * stride; x < w; x++, i += 4)
                        {
                            double l = 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2];
                            px[i + 2] = B(l * 1.07 + 20); px[i + 1] = B(l * 0.93 + 8); px[i] = B(l * 0.72);
                        }
                    break;
                case "sharpen":
                    {
                        var blur = (byte[])px.Clone();
                        BoxBlur(blur, w, h, stride, 1, 3);
                        double k = a / 40.0;
                        for (int y = 0; y < h; y++)
                            for (int x = 0, i = y * stride; x < w; x++, i += 4)
                                for (int ch = 0; ch < 3; ch++) px[i + ch] = B(px[i + ch] + (px[i + ch] - blur[i + ch]) * k);
                        break;
                    }
                case "blur":
                    BoxBlur(px, w, h, stride, Math.Max(1, (int)Math.Round(Math.Min(w, h) * a / 400.0)), 3);
                    break;
                case "denoise": Denoise(px, w, h, stride, a / 100.0); break;
                case "pixelate": Pixelate(px, w, h, stride, Math.Max(2, Math.Min(w, h) / Math.Max(2, a))); break;
            }
        }

        static void Lut(byte[] px, int w, int h, int stride, byte[] r, byte[] g, byte[] b)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0, i = y * stride; x < w; x++, i += 4) { px[i] = b[px[i]]; px[i + 1] = g[px[i + 1]]; px[i + 2] = r[px[i + 2]]; }
        }

        static void Saturate(byte[] px, int w, int h, int stride, double s)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0, i = y * stride; x < w; x++, i += 4)
                {
                    double l = 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2];
                    px[i] = B(l + (px[i] - l) * s); px[i + 1] = B(l + (px[i + 1] - l) * s); px[i + 2] = B(l + (px[i + 2] - l) * s);
                }
        }

        // Allarga ogni canale tra lo 0,5% più scuro e lo 0,5% più chiaro: toglie velature e dominanti di colore
        static void AutoLevels(byte[] px, int w, int h, int stride)
        {
            var luts = new byte[3][];
            long n = (long)w * h, cut = n / 200;
            for (int ch = 0; ch < 3; ch++)
            {
                var hist = new long[256];
                for (int y = 0; y < h; y++)
                    for (int x = 0, i = y * stride + ch; x < w; x++, i += 4) hist[px[i]]++;
                int lo = 0, hi = 255;
                for (long acc = 0; lo < 255 && (acc += hist[lo]) <= cut; lo++) { }
                for (long acc = 0; hi > 0 && (acc += hist[hi]) <= cut; hi--) { }
                var lut = new byte[256];
                for (int v = 0; v < 256; v++) lut[v] = hi - lo < 12 ? (byte)v : B((v - lo) * 255.0 / (hi - lo));
                luts[ch] = lut;
            }
            Lut(px, w, h, stride, luts[2], luts[1], luts[0]);
        }

        // Sfocatura a scatola ripetuta (passes volte ≈ gaussiana), separabile, bordi ripetuti; alfa invariato
        static void BoxBlur(byte[] px, int w, int h, int stride, int r, int passes)
        {
            r = Math.Max(1, Math.Min(r, Math.Max(w, h)));
            int n = Math.Max(w, h);
            var line = new int[n];
            var outv = new int[n];
            for (int p = 0; p < passes; p++)
                for (int ch = 0; ch < 3; ch++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++) line[x] = px[y * stride + x * 4 + ch];
                        Blur1(line, outv, w, r);
                        for (int x = 0; x < w; x++) px[y * stride + x * 4 + ch] = (byte)outv[x];
                    }
                    for (int x = 0; x < w; x++)
                    {
                        for (int y = 0; y < h; y++) line[y] = px[y * stride + x * 4 + ch];
                        Blur1(line, outv, h, r);
                        for (int y = 0; y < h; y++) px[y * stride + x * 4 + ch] = (byte)outv[y];
                    }
                }
        }

        static void Blur1(int[] src, int[] dst, int n, int r)
        {
            int win = 2 * r + 1;
            long sum = 0;
            for (int k = -r; k <= r; k++) sum += src[k < 0 ? 0 : k >= n ? n - 1 : k];
            for (int i = 0; i < n; i++)
            {
                dst[i] = (int)((sum + win / 2) / win);
                int add = i + r + 1, rem = i - r;
                sum += src[add >= n ? n - 1 : add] - src[rem < 0 ? 0 : rem];
            }
        }

        // Mediana 3×3 (5×5 oltre metà intensità), mescolata all'originale secondo l'intensità
        static void Denoise(byte[] px, int w, int h, int stride, double strength)
        {
            int r = strength > 0.5 ? 2 : 1, side = 2 * r + 1;
            double mix = Math.Min(1, strength * (r == 1 ? 2 : 1));
            var src = (byte[])px.Clone();
            var win = new byte[side * side];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int k = 0;
                        for (int dy = -r; dy <= r; dy++)
                        {
                            int yy = y + dy < 0 ? 0 : y + dy >= h ? h - 1 : y + dy;
                            for (int dx = -r; dx <= r; dx++)
                            {
                                int xx = x + dx < 0 ? 0 : x + dx >= w ? w - 1 : x + dx;
                                win[k++] = src[yy * stride + xx * 4 + ch];
                            }
                        }
                        Array.Sort(win);
                        int med = win[win.Length / 2];
                        px[i + ch] = B(src[i + ch] + (med - src[i + ch]) * mix);
                    }
                }
        }

        static void Pixelate(byte[] px, int w, int h, int stride, int block)
        {
            for (int by = 0; by < h; by += block)
                for (int bx = 0; bx < w; bx += block)
                {
                    int ey = Math.Min(h, by + block), ex = Math.Min(w, bx + block);
                    long sb = 0, sg = 0, sr = 0, n = 0;
                    for (int y = by; y < ey; y++)
                        for (int x = bx, i = y * stride + bx * 4; x < ex; x++, i += 4) { sb += px[i]; sg += px[i + 1]; sr += px[i + 2]; n++; }
                    byte vb = (byte)(sb / n), vg = (byte)(sg / n), vr = (byte)(sr / n);
                    for (int y = by; y < ey; y++)
                        for (int x = bx, i = y * stride + bx * 4; x < ex; x++, i += 4) { px[i] = vb; px[i + 1] = vg; px[i + 2] = vr; }
                }
        }
    }
}
