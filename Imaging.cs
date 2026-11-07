// Lettura e scrittura delle immagini tramite WIC (Windows Imaging Component, via WPF).
// Legge tutti i formati per cui Windows ha un decoder: quelli di base (BMP, PNG, JPEG, GIF, TIFF,
// ICO, JPEG XR) più quelli aggiunti dalle estensioni del Microsoft Store (HEIC/HEIF, WebP, AVIF,
// RAW delle fotocamere...). Scrive PNG, JPEG, TIFF, BMP, GIF, JPEG XR, ICO e PDF; WebP e HEIC
// solo se sul PC è installato il relativo encoder.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Gdi = System.Drawing;
using GdiImaging = System.Drawing.Imaging;
using GdiD2 = System.Drawing.Drawing2D;

namespace AppImg
{
    class OutputFormat
    {
        public string Id, Name, Ext;
        public bool Quality;      // la qualità (compressione con perdita) è regolabile
        public bool Alpha;        // conserva la trasparenza
        public Guid Container;    // per i formati scritti da un encoder WIC generico (WebP, HEIC)
        public override string ToString() { return Name + "  (" + Ext + ")"; }
    }

    class ImageInfo
    {
        public string Format;
        public int Width, Height, Frames;
    }

    static class Imaging
    {
        static readonly Guid WebpContainer = new Guid("e094b0e2-67f2-45b3-b0ea-115337ca7cf3");
        static readonly Guid HeifContainer = new Guid("e1e62521-6787-405b-a339-500715b5763f");
        // Categoria dei decoder WIC nel registro: ogni decoder installato elenca le estensioni che legge
        const string DecoderCategory = @"CLSID\{7ED96837-96F0-4812-B211-F13C24117ED3}\Instance";

        static List<OutputFormat> outputs;
        static string[] inputExts;

        // ---------- formati disponibili ----------

        public static List<OutputFormat> Outputs()
        {
            if (outputs != null) return outputs;
            var l = new List<OutputFormat>
            {
                new OutputFormat { Id = "png", Name = "PNG", Ext = ".png", Alpha = true },
                new OutputFormat { Id = "jpg", Name = "JPEG", Ext = ".jpg", Quality = true },
            };
            if (CanEncode(WebpContainer)) l.Add(new OutputFormat { Id = "webp", Name = "WebP", Ext = ".webp", Alpha = true, Container = WebpContainer });
            if (CanEncode(HeifContainer)) l.Add(new OutputFormat { Id = "heic", Name = "HEIC", Ext = ".heic", Container = HeifContainer });
            l.Add(new OutputFormat { Id = "tif", Name = "TIFF", Ext = ".tif", Alpha = true });
            l.Add(new OutputFormat { Id = "bmp", Name = "BMP", Ext = ".bmp" });
            l.Add(new OutputFormat { Id = "gif", Name = "GIF", Ext = ".gif", Alpha = true });
            l.Add(new OutputFormat { Id = "jxr", Name = "JPEG XR", Ext = ".jxr", Quality = true, Alpha = true });
            l.Add(new OutputFormat { Id = "ico", Name = "ICO", Ext = ".ico", Alpha = true });
            l.Add(new OutputFormat { Id = "pdf", Name = "PDF", Ext = ".pdf", Quality = true });
            outputs = l;
            return l;
        }

        // Prova a scrivere una piccola immagine: dice se l'encoder è installato e funziona.
        static bool CanEncode(Guid container)
        {
            try
            {
                var enc = BitmapEncoder.Create(container);
                var bs = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, new byte[64 * 64 * 4], 64 * 4);
                enc.Frames.Add(BitmapFrame.Create(bs));
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    return ms.Length > 0;
                }
            }
            catch { return false; }
        }

        // Estensioni leggibili: quelle dei decoder WIC installati (più quelle di base, per sicurezza).
        public static string[] InputExtensions()
        {
            if (inputExts != null) return inputExts;
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".bmp", ".dib", ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".tif", ".tiff", ".ico", ".jxr", ".wdp", ".hdp"
            };
            try
            {
                using (var inst = Registry.ClassesRoot.OpenSubKey(DecoderCategory))
                {
                    if (inst != null)
                        foreach (var clsid in inst.GetSubKeyNames())
                            using (var k = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid))
                            {
                                string exts = k != null ? k.GetValue("FileExtensions") as string : null;
                                if (exts == null) continue;
                                foreach (var e in exts.Split(',', ';'))
                                {
                                    string t = e.Trim().ToLowerInvariant();
                                    if (t.StartsWith(".") && t.Length > 1) set.Add(t);
                                }
                            }
                }
            }
            catch { }
            inputExts = set.ToArray();
            return inputExts;
        }

        public static bool IsImageExt(string path)
        {
            string ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && InputExtensions().Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        // ---------- lettura ----------

        static BitmapDecoder Open(string path, BitmapCacheOption cache)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (cache == BitmapCacheOption.OnLoad)
                    return BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                // Solo intestazione: il file viene copiato in memoria, così si può chiudere subito
                var ms = new MemoryStream();
                fs.CopyTo(ms);
                ms.Position = 0;
                return BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            }
        }

        // Le icone contengono più dimensioni: si usa la più grande. Negli altri formati la prima pagina/fotogramma.
        static BitmapFrame MainFrame(BitmapDecoder d)
        {
            if (d is IconBitmapDecoder && d.Frames.Count > 1)
                return d.Frames.OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).ThenByDescending(f => f.Format.BitsPerPixel).First();
            return d.Frames[0];
        }

        static int Orientation(BitmapFrame f)
        {
            try
            {
                var md = f.Metadata as BitmapMetadata;
                if (md == null) return 1;
                object o = md.GetQuery("System.Photo.Orientation");
                if (o == null) return 1;
                int v = System.Convert.ToInt32(o, CultureInfo.InvariantCulture);
                return v >= 1 && v <= 8 ? v : 1;
            }
            catch { return 1; }
        }

        static string FormatName(BitmapDecoder d)
        {
            if (d is JpegBitmapDecoder) return "JPEG";
            if (d is PngBitmapDecoder) return "PNG";
            if (d is GifBitmapDecoder) return "GIF";
            if (d is BmpBitmapDecoder) return "BMP";
            if (d is TiffBitmapDecoder) return "TIFF";
            if (d is IconBitmapDecoder) return "ICO";
            if (d is WmpBitmapDecoder) return "JPEG XR";
            try
            {
                string n = d.CodecInfo.FriendlyName ?? "";
                n = Regex.Replace(n, @"(?i)^(microsoft|windows)\s+", "");
                n = Regex.Replace(n, @"(?i)\s*(image|bitmap)?\s*decoder$", "").Trim();
                if (n.Length > 0) return n;
            }
            catch { }
            return "?";
        }

        // Formato e dimensioni (già ruotate secondo l'EXIF) senza decodificare tutta l'immagine.
        public static ImageInfo GetInfo(string path)
        {
            var d = Open(path, BitmapCacheOption.None);
            var f = MainFrame(d);
            int o = Orientation(f);
            bool swap = o >= 5;
            return new ImageInfo
            {
                Format = FormatName(d),
                Width = swap ? f.PixelHeight : f.PixelWidth,
                Height = swap ? f.PixelWidth : f.PixelHeight,
                Frames = d.Frames.Count
            };
        }

        // Immagine completa, già raddrizzata secondo l'orientamento EXIF.
        public static BitmapSource Load(string path)
        {
            ImageInfo info;
            return Load(path, out info);
        }

        public static BitmapSource Load(string path, out ImageInfo info)
        {
            var d = Open(path, BitmapCacheOption.OnLoad);
            var f = MainFrame(d);
            info = new ImageInfo { Format = FormatName(d), Frames = d.Frames.Count };
            BitmapSource s = f;
            switch (Orientation(f))
            {
                case 2: s = Flip(s); break;
                case 3: s = Rotate(s, 180); break;
                case 4: s = Rotate(Flip(s), 180); break;
                case 5: s = Flip(Rotate(s, 90)); break;
                case 6: s = Rotate(s, 90); break;
                case 7: s = Flip(Rotate(s, 270)); break;
                case 8: s = Rotate(s, 270); break;
            }
            s.Freeze();
            info.Width = s.PixelWidth;
            info.Height = s.PixelHeight;
            return s;
        }

        static BitmapSource Rotate(BitmapSource s, double angle) { return new TransformedBitmap(s, new RotateTransform(angle)); }
        static BitmapSource Flip(BitmapSource s) { return new TransformedBitmap(s, new ScaleTransform(-1, 1)); }

        // Anteprima ridotta come bitmap GDI+ per la finestra.
        public static Gdi.Bitmap Preview(string path, int maxSide)
        {
            var s = Load(path);
            var big = ToGdi(s);
            int w = s.PixelWidth, h = s.PixelHeight;
            if (Math.Max(w, h) <= maxSide) return big;
            using (big) return Scale(big, Fit(w, h, maxSide));
        }

        // ---------- visualizzazione ----------

        // Immagine pronta per lo schermo: bitmap GDI+ (o GIF animata) più le informazioni sul file.
        public class Picture : IDisposable
        {
            public Gdi.Image Image;
            public bool Animated, HasAlpha;
            public ImageInfo Info;
            public long Bytes;
            Stream keep; // le GIF animate leggono i fotogrammi dallo stream: deve restare aperto

            public void Dispose()
            {
                if (Image != null) { Image.Dispose(); Image = null; }
                if (keep != null) { keep.Dispose(); keep = null; }
            }

            // Carica per lo schermo; le immagini enormi vengono ridotte a maxSide (in Info restano le dimensioni vere).
            public static Picture Load(string path, int maxSide)
            {
                var p = new Picture();
                try { p.Bytes = new FileInfo(path).Length; } catch { }
                if (string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
                {
                    MemoryStream ms = null;
                    try
                    {
                        ms = new MemoryStream(File.ReadAllBytes(path));
                        var img = Gdi.Image.FromStream(ms);
                        var dim = new GdiImaging.FrameDimension(img.FrameDimensionsList[0]);
                        int n = img.GetFrameCount(dim);
                        if (n > 1)
                        {
                            p.Image = img; p.Animated = true; p.HasAlpha = true; p.keep = ms;
                            p.Info = new ImageInfo { Format = "GIF", Width = img.Width, Height = img.Height, Frames = n };
                            return p;
                        }
                        img.Dispose();
                    }
                    catch { }
                    if (ms != null && p.keep == null) ms.Dispose();
                }
                ImageInfo info;
                var s = Imaging.Load(path, out info);
                p.Info = info;
                var bmp = ToGdi(s);
                p.HasAlpha = AnyTransparent(bmp);
                if (Math.Max(bmp.Width, bmp.Height) > maxSide)
                    using (var big = bmp) bmp = Scale(big, Fit(big.Width, big.Height, maxSide));
                p.Image = bmp;
                return p;
            }
        }

        static bool AnyTransparent(Gdi.Bitmap bmp)
        {
            var data = bmp.LockBits(new Gdi.Rectangle(0, 0, bmp.Width, bmp.Height), GdiImaging.ImageLockMode.ReadOnly, GdiImaging.PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bmp.Width * 4];
                for (int y = 0; y < bmp.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (int i = 3; i < row.Length; i += 4) if (row[i] != 255) return true;
                }
                return false;
            }
            finally { bmp.UnlockBits(data); }
        }

        // Dati dello scatto (EXIF) per il pannello informazioni. Chiavi in inglese, tradotte dall'interfaccia.
        public static List<KeyValuePair<string, string>> Details(string path)
        {
            var r = new List<KeyValuePair<string, string>>();
            BitmapMetadata md = null;
            try { md = MainFrame(Open(path, BitmapCacheOption.None)).Metadata as BitmapMetadata; } catch { }
            if (md == null) return r;
            var ci = CultureInfo.CurrentCulture;
            Func<string, object> q = k => { try { return md.GetQuery(k); } catch { return null; } };
            Action<string, string> add = (k, val) => { if (!string.IsNullOrWhiteSpace(val)) r.Add(new KeyValuePair<string, string>(k, val.Trim())); };
            Func<object, double> num = o => { try { return o == null ? double.NaN : System.Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return double.NaN; } };

            try
            {
                DateTime dt;
                string taken = md.DateTaken;
                if (taken != null && DateTime.TryParse(taken, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) add("Date taken", dt.ToString("g", ci));
                else add("Date taken", taken);
            }
            catch { }
            string make = null, model = null;
            try { make = md.CameraManufacturer; } catch { }
            try { model = md.CameraModel; } catch { }
            if (!string.IsNullOrWhiteSpace(model))
                add("Camera", !string.IsNullOrWhiteSpace(make) && !model.Trim().StartsWith(make.Trim(), StringComparison.OrdinalIgnoreCase) ? make.Trim() + " " + model.Trim() : model);
            else add("Camera", make);

            var parts = new List<string>();
            double v = num(q("System.Photo.FNumber"));
            if (v > 0) parts.Add("f/" + v.ToString("0.#", ci));
            v = num(q("System.Photo.ExposureTime"));
            if (v > 0) parts.Add(v < 1 ? "1/" + Math.Round(1 / v) + " s" : v.ToString("0.#", ci) + " s");
            v = num(q("System.Photo.ISOSpeed"));
            if (v > 0) parts.Add("ISO " + v.ToString("0", ci));
            v = num(q("System.Photo.FocalLength"));
            if (v > 0) parts.Add(v.ToString("0.#", ci) + " mm");
            add("Exposure", string.Join("  ·  ", parts));
            add("Lens", q("System.Photo.LensModel") as string);
            try { add("Software", md.ApplicationName); } catch { }
            try { add("Author", md.Author != null ? string.Join(", ", md.Author) : null); } catch { }
            try { add("Title", md.Title); } catch { }

            double[] lat = Gps(q, 2, "System.GPS.Latitude"), lon = Gps(q, 4, "System.GPS.Longitude");
            if (lat != null && lon != null)
            {
                double la = lat[0] + lat[1] / 60 + lat[2] / 3600, lo = lon[0] + lon[1] / 60 + lon[2] / 3600;
                string latRef = q("System.GPS.LatitudeRef") as string ?? q("/app1/ifd/gps/{ushort=1}") as string ?? q("/ifd/gps/{ushort=1}") as string ?? "N";
                string lonRef = q("System.GPS.LongitudeRef") as string ?? q("/app1/ifd/gps/{ushort=3}") as string ?? q("/ifd/gps/{ushort=3}") as string ?? "E";
                if (latRef.StartsWith("S")) la = -la;
                if (lonRef.StartsWith("W")) lo = -lo;
                add("Location", la.ToString("0.00000", CultureInfo.InvariantCulture) + ", " + lo.ToString("0.00000", CultureInfo.InvariantCulture));
            }
            return r;
        }

        // Gradi, minuti, secondi del GPS: dal nome del sistema o, se WPF non lo risolve, dai razionali EXIF grezzi.
        static double[] Gps(Func<string, object> q, int tag, string name)
        {
            var d = q(name) as double[];
            if (d != null && d.Length == 3) return d;
            foreach (var path in new[] { "/app1/ifd/gps/{ushort=" + tag + "}", "/ifd/gps/{ushort=" + tag + "}" })
            {
                var raw = q(path) as ulong[];
                if (raw == null || raw.Length != 3) continue;
                var r = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    uint num = (uint)(raw[i] & 0xFFFFFFFF), den = (uint)(raw[i] >> 32); // razionale: numeratore nei 32 bit bassi
                    r[i] = den == 0 ? 0 : (double)num / den;
                }
                return r;
            }
            return null;
        }

        // ---------- conversione ----------

        // Converte un file. maxSide > 0 riduce l'immagine (mai ingrandisce) perché il lato lungo non superi maxSide.
        // quarterTurns: rotazione in senso orario a passi di 90° (quella scelta nel visualizzatore).
        public static void Convert(string src, string dest, OutputFormat f, int quality, int maxSide, int quarterTurns = 0)
        {
            Convert(src, dest, f, quality, ResizeSpec.LongSide(maxSide), quarterTurns);
        }

        public static void Convert(string src, string dest, OutputFormat f, int quality, ResizeSpec resize, int quarterTurns = 0)
        {
            Save(Prepare(src, resize, quarterTurns), dest, f, quality);
        }

        public static BitmapSource Prepare(string src, int maxSide, int quarterTurns = 0)
        {
            return Prepare(src, ResizeSpec.LongSide(maxSide), quarterTurns);
        }

        // Immagine pronta da salvare: letta, ruotata e ridotta come richiesto.
        public static BitmapSource Prepare(string src, ResizeSpec resize, int quarterTurns = 0)
        {
            var s = Load(src);
            quarterTurns = ((quarterTurns % 4) + 4) % 4;
            if (quarterTurns != 0)
            {
                s = Rotate(s, 90 * quarterTurns);
                s.Freeze();
            }
            var size = resize == null ? null : resize.Target(s.PixelWidth, s.PixelHeight);
            if (size != null)
            {
                using (var g = ToGdi(s))
                using (var r = Scale(g, size.Value))
                    s = FromGdi(r, s.DpiX, s.DpiY);
            }
            return s;
        }

        // Peso che avrebbe il file: l'immagine viene codificata davvero, ma i byte vengono solo contati.
        public static long EstimateSize(BitmapSource s, OutputFormat f, int quality)
        {
            using (var cs = new CountingStream())
            {
                Write(s, cs, f, Math.Max(1, Math.Min(100, quality)));
                return cs.Length;
            }
        }

        // Stream che non conserva nulla: conta soltanto i byte scritti.
        class CountingStream : Stream
        {
            long len, pos;
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return len; } }
            public override long Position { get { return pos; } set { pos = value; } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin)
            {
                pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? pos + offset : len + offset;
                return pos;
            }
            public override void SetLength(long value) { len = value; }
            public override void Write(byte[] buffer, int offset, int count) { pos += count; if (pos > len) len = pos; }
        }

        static void Write(BitmapSource s, Stream output, OutputFormat f, int quality)
        {
            switch (f.Id)
            {
                case "ico": WriteIco(s, output); break;
                case "pdf": WritePdf(s, output, quality); break;
                default: Encoder(s, f, quality).Save(output); break;
            }
        }

        public static void Save(BitmapSource s, string dest, OutputFormat f, int quality)
        {
            quality = Math.Max(1, Math.Min(100, quality));
            string tmp = dest + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 6);
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
                    Write(s, fs, f, quality);
                // Il file finale viene sostituito solo a scrittura completata
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        static BitmapEncoder Encoder(BitmapSource s, OutputFormat f, int quality)
        {
            BitmapEncoder enc;
            switch (f.Id)
            {
                case "png": enc = new PngBitmapEncoder(); s = Normalize(s); break;
                case "jpg": enc = new JpegBitmapEncoder { QualityLevel = quality }; s = Flatten(s); break;
                case "bmp": enc = new BmpBitmapEncoder(); s = Flatten(s); break;
                case "tif": enc = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw }; s = Normalize(s); break;
                case "gif": enc = new GifBitmapEncoder(); s = ToIndexed(s); break;
                case "jxr":
                    var w = new WmpBitmapEncoder();
                    if (quality >= 100) w.Lossless = true;
                    else w.ImageQualityLevel = quality / 100f;
                    enc = w;
                    s = ToBgra(s);
                    break;
                case "heic": enc = BitmapEncoder.Create(f.Container); s = Flatten(s); break;
                default: enc = BitmapEncoder.Create(f.Container); s = ToBgra(s); break;
            }
            enc.Frames.Add(BitmapFrame.Create(s));
            return enc;
        }

        // ---------- formati dei pixel ----------

        static readonly PixelFormat[] Plain =
        {
            PixelFormats.Bgra32, PixelFormats.Bgr32, PixelFormats.Bgr24, PixelFormats.Pbgra32, PixelFormats.Rgb24,
            PixelFormats.Gray8, PixelFormats.Gray16, PixelFormats.Rgb48, PixelFormats.Rgba64, PixelFormats.BlackWhite,
            PixelFormats.Indexed1, PixelFormats.Indexed2, PixelFormats.Indexed4, PixelFormats.Indexed8
        };

        // Formati particolari (CMYK, virgola mobile...) diventano BGRA a 8 bit; gli altri restano come sono.
        static BitmapSource Normalize(BitmapSource s)
        {
            return Plain.Contains(s.Format) ? s : ToBgra(s);
        }

        static BitmapSource ToBgra(BitmapSource s)
        {
            if (s.Format == PixelFormats.Bgra32) return s;
            var c = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
            c.Freeze();
            return c;
        }

        static byte[] Pixels(BitmapSource bgra)
        {
            var px = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(px, bgra.PixelWidth * 4, 0);
            return px;
        }

        // Toglie la trasparenza appoggiando l'immagine su fondo bianco (per JPEG, BMP, PDF, HEIC).
        static BitmapSource Flatten(BitmapSource s)
        {
            if (s.Format == PixelFormats.Bgr24 || s.Format == PixelFormats.Bgr32 || s.Format == PixelFormats.Gray8) return s;
            var b = ToBgra(s);
            int w = b.PixelWidth, h = b.PixelHeight;
            var px = Pixels(b);
            var rgb = new byte[w * h * 3];
            for (int i = 0, j = 0; i < px.Length; i += 4, j += 3)
            {
                int a = px[i + 3];
                if (a == 255) { rgb[j] = px[i]; rgb[j + 1] = px[i + 1]; rgb[j + 2] = px[i + 2]; }
                else
                {
                    int bg = 255 * (255 - a);
                    rgb[j] = (byte)((px[i] * a + bg + 127) / 255);
                    rgb[j + 1] = (byte)((px[i + 1] * a + bg + 127) / 255);
                    rgb[j + 2] = (byte)((px[i + 2] * a + bg + 127) / 255);
                }
            }
            var r = BitmapSource.Create(w, h, b.DpiX, b.DpiY, PixelFormats.Bgr24, null, rgb, w * 3);
            r.Freeze();
            return r;
        }

        // GIF: tavolozza ottimizzata di 256 colori; i pixel quasi trasparenti usano un colore trasparente.
        static BitmapSource ToIndexed(BitmapSource s)
        {
            if (s.Format == PixelFormats.Indexed8 || s.Format == PixelFormats.Indexed4 ||
                s.Format == PixelFormats.Indexed2 || s.Format == PixelFormats.Indexed1) return s;
            var b = ToBgra(s);
            int w = b.PixelWidth, h = b.PixelHeight;
            var px = Pixels(b);
            bool transparent = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] < 128) { transparent = true; break; }

            var opaque = Flatten(b);
            var palette = new BitmapPalette(opaque, transparent ? 255 : 256);
            var idx = new FormatConvertedBitmap(opaque, PixelFormats.Indexed8, palette, 0);
            int stride = (w + 3) & ~3;
            var data = new byte[stride * h];
            idx.CopyPixels(data, stride, 0);
            if (!transparent) { var r0 = BitmapSource.Create(w, h, b.DpiX, b.DpiY, PixelFormats.Indexed8, palette, data, stride); r0.Freeze(); return r0; }

            var colors = new List<Color>(palette.Colors);
            while (colors.Count < 255) colors.Add(Colors.Black);
            byte tIndex = (byte)colors.Count;
            colors.Add(Color.FromArgb(0, 0, 0, 0));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[(y * w + x) * 4 + 3] < 128) data[y * stride + x] = tIndex;
            var r = BitmapSource.Create(w, h, b.DpiX, b.DpiY, PixelFormats.Indexed8, new BitmapPalette(colors), data, stride);
            r.Freeze();
            return r;
        }

        // ---------- GDI+ (ridimensionamento di qualità) ----------

        static Gdi.Size Fit(int w, int h, int maxSide)
        {
            double k = (double)maxSide / Math.Max(w, h);
            return new Gdi.Size(Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));
        }

        static Gdi.Bitmap ToGdi(BitmapSource s)
        {
            var b = ToBgra(s);
            int w = b.PixelWidth, h = b.PixelHeight;
            var bmp = new Gdi.Bitmap(w, h, GdiImaging.PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Gdi.Rectangle(0, 0, w, h), GdiImaging.ImageLockMode.WriteOnly, GdiImaging.PixelFormat.Format32bppArgb);
            try { b.CopyPixels(Int32Rect.Empty, data.Scan0, data.Stride * h, data.Stride); }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        static BitmapSource FromGdi(Gdi.Bitmap bmp, double dpiX, double dpiY)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Gdi.Rectangle(0, 0, w, h), GdiImaging.ImageLockMode.ReadOnly, GdiImaging.PixelFormat.Format32bppArgb);
            try
            {
                var s = BitmapSource.Create(w, h, dpiX > 0 ? dpiX : 96, dpiY > 0 ? dpiY : 96, PixelFormats.Bgra32, null, data.Scan0, data.Stride * h, data.Stride);
                s.Freeze();
                return s;
            }
            finally { bmp.UnlockBits(data); }
        }

        static Gdi.Bitmap Scale(Gdi.Image src, Gdi.Size size)
        {
            var dst = new Gdi.Bitmap(size.Width, size.Height, GdiImaging.PixelFormat.Format32bppArgb);
            Draw(src, dst, new Gdi.Rectangle(0, 0, size.Width, size.Height));
            return dst;
        }

        static void Draw(Gdi.Image src, Gdi.Bitmap dst, Gdi.Rectangle area)
        {
            using (var g = Gdi.Graphics.FromImage(dst))
            using (var ia = new GdiImaging.ImageAttributes())
            {
                g.CompositingMode = GdiD2.CompositingMode.SourceCopy;
                g.CompositingQuality = GdiD2.CompositingQuality.HighQuality;
                g.InterpolationMode = GdiD2.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = GdiD2.PixelOffsetMode.HighQuality;
                g.SmoothingMode = GdiD2.SmoothingMode.HighQuality;
                ia.SetWrapMode(GdiD2.WrapMode.TileFlipXY); // niente bordi scuri ai margini
                g.DrawImage(src, area, 0, 0, src.Width, src.Height, Gdi.GraphicsUnit.Pixel, ia);
            }
        }

        // ---------- ICO ----------

        // Icona con più dimensioni (256...16 px), ognuna salvata come PNG; le immagini non quadrate sono centrate.
        static void WriteIco(BitmapSource s, Stream output)
        {
            int[] sizes = { 256, 128, 64, 48, 32, 24, 16 };
            var pngs = new List<byte[]>();
            using (var src = ToGdi(s))
            {
                foreach (int size in sizes)
                {
                    var fit = Fit(src.Width, src.Height, size);
                    using (var canvas = new Gdi.Bitmap(size, size, GdiImaging.PixelFormat.Format32bppArgb))
                    using (var ms = new MemoryStream())
                    {
                        Draw(src, canvas, new Gdi.Rectangle((size - fit.Width) / 2, (size - fit.Height) / 2, fit.Width, fit.Height));
                        canvas.Save(ms, GdiImaging.ImageFormat.Png);
                        pngs.Add(ms.ToArray());
                    }
                }
            }
            var bw = new BinaryWriter(output);
            bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)(sizes[i] % 256)); bw.Write((byte)(sizes[i] % 256));
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write(pngs[i].Length); bw.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) bw.Write(p);
            bw.Flush();
        }

        // ---------- PDF ----------

        // PDF di una pagina con l'immagine in JPEG; la pagina ha le dimensioni dell'immagine secondo i suoi DPI.
        static void WritePdf(BitmapSource s, Stream output, int quality)
        {
            var flat = Flatten(s);
            byte[] jpg;
            using (var ms = new MemoryStream())
            {
                var enc = new JpegBitmapEncoder { QualityLevel = quality };
                enc.Frames.Add(BitmapFrame.Create(flat));
                enc.Save(ms);
                jpg = ms.ToArray();
            }
            bool gray = flat.Format == PixelFormats.Gray8;
            double dpiX = s.DpiX >= 10 && s.DpiX <= 10000 ? s.DpiX : 96, dpiY = s.DpiY >= 10 && s.DpiY <= 10000 ? s.DpiY : 96;
            double pw = flat.PixelWidth * 72.0 / dpiX, ph = flat.PixelHeight * 72.0 / dpiY;
            double k = Math.Min(1.0, 14400.0 / Math.Max(pw, ph)); // limite delle pagine PDF: 200 pollici
            pw *= k; ph *= k;
            var ci = CultureInfo.InvariantCulture;
            string W = pw.ToString("0.###", ci), H = ph.ToString("0.###", ci);

            var offsets = new List<long>();
            var latin = Encoding.GetEncoding("ISO-8859-1");
            long pos = 0;
            Action<string> put = t => { var b = latin.GetBytes(t); output.Write(b, 0, b.Length); pos += b.Length; };
            Action<byte[]> putBytes = b => { output.Write(b, 0, b.Length); pos += b.Length; };

            put("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
            offsets.Add(pos); put("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            offsets.Add(pos); put("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            offsets.Add(pos); put("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + W + " " + H + "] " +
                                  "/Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
            offsets.Add(pos); put("4 0 obj\n<< /Type /XObject /Subtype /Image /Width " + flat.PixelWidth + " /Height " + flat.PixelHeight +
                                  " /ColorSpace /" + (gray ? "DeviceGray" : "DeviceRGB") + " /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpg.Length + " >>\nstream\n");
            putBytes(jpg);
            put("\nendstream\nendobj\n");
            string content = "q " + W + " 0 0 " + H + " 0 0 cm /Im0 Do Q\n";
            offsets.Add(pos); put("5 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "endstream\nendobj\n");
            offsets.Add(pos); put("6 0 obj\n<< /Producer (AppImg " + AppVersion.Text + ") /CreationDate (D:" + DateTime.Now.ToString("yyyyMMddHHmmss", ci) + ") >>\nendobj\n");
            long xref = pos;
            var sb = new StringBuilder();
            sb.Append("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
            foreach (var o in offsets) sb.Append(o.ToString("0000000000", ci) + " 00000 n \n");
            sb.Append("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R /Info 6 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            put(sb.ToString());
        }
    }
    // Come ridurre le immagini nel convertitore. Non ingrandisce mai: se l'immagine è già abbastanza piccola resta com'è.
    enum ResizeMode { None, LongSide, Box, Percent }

    class ResizeSpec
    {
        public ResizeMode Mode;
        public int A, B;   // LongSide: A = lato lungo massimo; Box: A × B massimi; Percent: A = percentuale (1-99)
        public bool Stretch;   // Box senza proporzioni: ogni lato viene portato a A e B (senza ingrandire), anche deformando

        public static ResizeSpec LongSide(int maxSide) { return new ResizeSpec { Mode = maxSide > 0 ? ResizeMode.LongSide : ResizeMode.None, A = maxSide }; }

        // Chiave per la cache delle immagini già preparate
        public string Key { get { return Mode == ResizeMode.None ? "none" : Mode + ":" + A + "x" + B + (Stretch ? "s" : ""); } }

        // Nuove dimensioni, oppure null se l'immagine non va toccata.
        public System.Drawing.Size? Target(int w, int h)
        {
            if (Mode == ResizeMode.Box && Stretch)
            {
                if (A <= 0 || B <= 0) return null;
                var d = new System.Drawing.Size(Math.Min(w, A), Math.Min(h, B));
                if (d.Width == w && d.Height == h) return null;
                return d;
            }
            double k;
            switch (Mode)
            {
                case ResizeMode.LongSide: k = A > 0 ? (double)A / Math.Max(w, h) : 1; break;
                case ResizeMode.Box: k = A > 0 && B > 0 ? Math.Min((double)A / w, (double)B / h) : 1; break;
                case ResizeMode.Percent: k = A > 0 ? A / 100.0 : 1; break;
                default: return null;
            }
            if (k >= 1) return null;
            var r = new System.Drawing.Size(Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));
            if (r.Width == w && r.Height == h) return null;
            return r;
        }
    }
}
