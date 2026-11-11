// Real-ESRGAN (ncnn-vulkan, licenza BSD-3-Clause): ingrandisce un'immagine x2/x3/x4 ricostruendo i dettagli
// con una rete neurale, sul PC (scheda video Vulkan: anche le grafiche integrate Intel/AMD).
// Non è incluso nell'installer: si scarica al primo uso (~45 MB) dalla release ufficiale su GitHub,
// si controlla lo SHA-256 e si estrae in %LOCALAPPDATA%\AppImg\realesrgan.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AppImg
{
    class EsrganModel
    {
        public string Id;        // nome nel parametro -n, o prefisso per i modelli con più scale
        public string Key;       // nome usato dall'assistente: photo, drawing, fast
        public bool PerScale;    // realesr-animevideov3-x2/-x3/-x4: un file per scala
        public string Name { get { return Key == "photo" ? L.T("Foto (più dettagli)", "Photo (more detail)") : Key == "drawing" ? L.T("Disegni e grafica", "Drawings and graphics") : L.T("Veloce", "Fast"); } }
        public override string ToString() { return Name; }
    }

    static class Esrgan
    {
        public const string Url = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip";
        const string Sha256 = "abc02804e17982a3be33675e4d471e91ea374e65b70167abc09e31acb412802d";
        public const long DownloadBytes = 45474481;
        public const string ProjectUrl = "https://github.com/xinntao/Real-ESRGAN";

        public static readonly EsrganModel[] Models =
        {
            new EsrganModel { Id = "realesrgan-x4plus", Key = "photo" },
            new EsrganModel { Id = "realesrgan-x4plus-anime", Key = "drawing" },
            new EsrganModel { Id = "realesr-animevideov3", Key = "fast", PerScale = true },
        };

        public static EsrganModel Find(string key)
        {
            foreach (var m in Models) if (string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase) || m.Id == key) return m;
            return Models[0];
        }

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppImg", "realesrgan"); } }
        static string Exe { get { return Path.Combine(Dir, "realesrgan-ncnn-vulkan.exe"); } }
        public static bool Installed { get { return File.Exists(Exe) && File.Exists(Path.Combine(Dir, "models", "realesrgan-x4plus.bin")); } }

        // Scarica ed estrae. Bloccante; progress riceve 0..100. Restituisce null o il messaggio d'errore.
        public static string Install(Action<int> progress, CancellationToken cancel)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "AppImg-realesrgan-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                var req = (HttpWebRequest)WebRequest.Create(Url);
                req.UserAgent = "AppImg/" + AppVersion.Text;
                req.Timeout = 30000;
                req.ReadWriteTimeout = 60000;
                using (cancel.Register(delegate { try { req.Abort(); } catch { } }))
                using (var resp = req.GetResponse())
                using (var st = resp.GetResponseStream())
                using (var fs = File.Create(tmp))
                {
                    long total = resp.ContentLength > 0 ? resp.ContentLength : DownloadBytes, done = 0;
                    var buf = new byte[81920];
                    int n, last = -1;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        done += n;
                        int pct = (int)(done * 100 / total);
                        if (pct != last && progress != null) progress(last = Math.Min(100, pct));
                    }
                }
                return InstallZip(tmp);
            }
            catch (Exception ex)
            {
                if (cancel.IsCancellationRequested) return L.T("annullato.", "cancelled.");
                return ex.Message;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // Controlla lo SHA-256 dello zip ed estrae solo il programma, le sue librerie e i modelli
        // (lo zip contiene anche un video e due immagini di prova).
        public static string InstallZip(string zipPath)
        {
            try
            {
                using (var st = File.OpenRead(zipPath))
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(st)).Replace("-", "").ToLowerInvariant() != Sha256)
                        return L.T("il file scaricato non corrisponde a quello pubblicato (SHA-256 diverso).", "the downloaded file does not match the published one (SHA-256 mismatch).");
                string dir = Dir, stage = dir + ".new";
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(Path.Combine(stage, "models"));
                using (var zip = ZipFile.OpenRead(zipPath))
                    foreach (var e in zip.Entries)
                    {
                        string name = e.FullName.Replace('\\', '/');
                        bool want = name.EndsWith(".exe") || name.EndsWith(".dll") ||
                                    (name.StartsWith("models/") && (name.EndsWith(".bin") || name.EndsWith(".param")));
                        if (!want || name.Contains("..")) continue;
                        e.ExtractToFile(Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar)), true);
                    }
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.Move(stage, dir);
                return Installed ? null : L.T("l'archivio non contiene i file attesi.", "the archive does not contain the expected files.");
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Pixel massimi del risultato: oltre, la memoria non basta (bitmap a 32 bit)
        public const long MaxOutputPixels = 80L * 1000 * 1000;

        // Ingrandisce src di "scale" volte (2, 3 o 4). Bloccante. I modelli x4 lavorano sempre a 4x:
        // per x2/x3 il risultato viene poi ridotto, così resta la ricostruzione dei dettagli.
        public static Bitmap Upscale(Bitmap src, EsrganModel model, int scale, Action<int> progress, CancellationToken cancel, out string error)
        {
            error = null;
            scale = Math.Max(2, Math.Min(4, scale));
            if ((long)src.Width * src.Height * scale * scale > MaxOutputPixels)
            {
                error = L.F("Riquadro troppo grande per x{0} ({1}×{2} px): suddividilo o scegli una scala minore.",
                            "Tile too large for x{0} ({1}×{2} px): subdivide it or pick a smaller scale.", scale, src.Width, src.Height);
                return null;
            }
            string work = Path.Combine(Path.GetTempPath(), "AppImg-esrgan-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Process p = null;
            var log = new StringBuilder();
            try
            {
                Directory.CreateDirectory(work);
                string inFile = Path.Combine(work, "in.png"), outFile = Path.Combine(work, "out.png");
                src.Save(inFile, System.Drawing.Imaging.ImageFormat.Png);
                int run = model.PerScale ? scale : 4;
                string name = model.PerScale ? model.Id + "-x" + run : model.Id;
                var psi = new ProcessStartInfo(Exe, "-i \"" + inFile + "\" -o \"" + outFile + "\" -n " + name + " -s " + run.ToString(CultureInfo.InvariantCulture)
                                                    + " -m \"" + Path.Combine(Dir, "models") + "\"")
                {
                    WorkingDirectory = Dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                p = Process.Start(psi);
                var pct = new Regex(@"(\d+)[.,]\d+%");
                DataReceivedEventHandler onLine = (s, e) =>
                {
                    if (e.Data == null) return;
                    var m = pct.Match(e.Data);
                    if (m.Success) { if (progress != null) progress(int.Parse(m.Groups[1].Value)); }
                    else lock (log) log.AppendLine(e.Data);
                };
                p.ErrorDataReceived += onLine;
                p.OutputDataReceived += onLine;
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                var proc = p;
                using (cancel.Register(delegate { try { if (!proc.HasExited) proc.Kill(); } catch { } }))
                    p.WaitForExit();
                if (cancel.IsCancellationRequested) { error = L.T("Annullato.", "Cancelled."); return null; }
                if (!File.Exists(outFile))
                {
                    string tail;
                    lock (log) tail = log.ToString().Trim();
                    if (tail.Length > 400) tail = tail.Substring(tail.Length - 400);
                    error = L.F("Real-ESRGAN non ha prodotto il risultato (codice {0}). Serve una scheda video con driver Vulkan aggiornati.",
                                "Real-ESRGAN produced no result (code {0}). It needs a graphics card with up-to-date Vulkan drivers.", p.ExitCode)
                          + (tail.Length > 0 ? "\n" + tail : "");
                    return null;
                }
                Bitmap result;
                using (var ms = new MemoryStream(File.ReadAllBytes(outFile)))
                using (var img = Image.FromStream(ms))
                    result = new Bitmap(img);
                int w = src.Width * scale, h = src.Height * scale;
                if (result.Width != w || result.Height != h)
                    using (var big = result) result = Imaging.Scale(big, new Size(w, h));
                return result;
            }
            catch (Exception ex)
            {
                error = cancel.IsCancellationRequested ? L.T("Annullato.", "Cancelled.") : ex.Message;
                return null;
            }
            finally
            {
                try { if (p != null && !p.HasExited) p.Kill(); } catch { }
                try { Directory.Delete(work, true); } catch { }
            }
        }
    }
}
