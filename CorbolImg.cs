// CorbolImg - visualizzatore di immagini per Windows, con convertitore di formato.
// Apre tutto ciò che Windows sa leggere (JPEG, PNG, HEIC, WebP, AVIF, TIFF, GIF animate, BMP, ICO, RAW...)
// e salva in PNG, JPEG, WebP, HEIC, TIFF, BMP, GIF, JPEG XR, ICO o PDF, anche molti file insieme.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CorbolImg
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        // Deve coincidere con quello scritto dall'installer nei collegamenti
        public const string AppUserModelId = "AppImg.Viewer"; // come prima del cambio di nome: le icone già fissate alla barra restano della stessa app

        static ApplicationContext context;
        static int openForms;
        static ConverterForm converter;

        // CorbolImg.exe [file o cartella]          -> visualizzatore (con le altre immagini della cartella)
        // CorbolImg.exe file1 file2 ...            -> visualizzatore, solo con quei file
        // CorbolImg.exe /convert [file o cartelle] -> convertitore (usato da "Invia a > CorbolImg")
        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            OldApp.MoveLeftovers(); // dati rimasti nelle cartelle col nome vecchio (AppImg)
            ClipboardImages.Cleanup(); // immagini incollate dagli appunti più vecchie di una settimana
            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Un errore imprevisto nell'interfaccia mostra un messaggio invece di chiudere l'app
            Application.ThreadException += (s, e) =>
                MessageBox.Show(L.T("Si è verificato un errore:", "An error occurred:") + "\n" + e.Exception.Message, "CorbolImg", MessageBoxButtons.OK, MessageBoxIcon.Error);

            Settings.Load();
            L.Set(Settings.Get("lang", "auto"));

            bool convert = args.Length > 0 && (args[0].Equals("/convert", StringComparison.OrdinalIgnoreCase) || args[0].Equals("-convert", StringComparison.OrdinalIgnoreCase));
            var paths = args.Skip(convert ? 1 : 0).Where(a => !a.StartsWith("/")).ToArray();

            context = new ApplicationContext();
            StartLink();
            if (convert) OpenConverter(paths);
            else OpenViewer(paths);
            Application.Run(context);
        }

        // Comandi dalle altre app Corbol (CorbolLink.cs): open apre un visualizzatore, convert il convertitore
        static void StartLink()
        {
            Corbol.Link.Start("CorbolImg", L.T);
            Corbol.Link.Handle("open", delegate (List<string> a)
            {
                var v = OpenViewer(a.Where(x => x.Trim().Length > 0).ToList());
                v.Activate();
                return "ok";
            });
            Corbol.Link.Handle("convert", delegate (List<string> a)
            {
                OpenConverter(a.Where(x => x.Trim().Length > 0).ToList());
                if (converter != null) converter.Activate();
                return "ok";
            });
            Corbol.Link.Ready();
        }

        // L'app resta aperta finché c'è almeno una finestra (visualizzatore o convertitore).
        static void Track(Form f)
        {
            openForms++;
            f.FormClosed += delegate
            {
                if (--openForms == 0) context.ExitThread();
            };
        }

        public static ViewerForm OpenViewer(IList<string> paths)
        {
            var v = new ViewerForm(paths);
            Track(v);
            v.Show();
            return v;
        }

        public static void OpenGrid(string path, int quarterTurns)
        {
            var g = new GridForm(path, quarterTurns);
            Track(g);
            g.Show();
        }

        public static void OpenConverter(IEnumerable<string> paths)
        {
            if (converter == null || converter.IsDisposed)
            {
                converter = new ConverterForm(paths ?? new string[0]);
                Track(converter);
                converter.Show();
            }
            else if (paths != null) converter.AddFiles(paths);
            else converter.Activate();
        }

        // Cambio lingua: aggiorna tutte le finestre aperte.
        public static void LanguageChanged()
        {
            foreach (Form f in Application.OpenForms.Cast<Form>().ToList())
            {
                var v = f as ViewerForm;
                if (v != null) v.ApplyTexts();
                var c = f as ConverterForm;
                if (c != null) c.ApplyTexts();
                var g = f as GridForm;
                if (g != null) g.ApplyTexts();
            }
        }

        // Le operazioni WIC/WPF girano su thread STA dedicati, così la finestra resta reattiva.
        public static void RunSta(ThreadStart work)
        {
            var t = new Thread(work) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    static class Util
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);

        // Ordinamento dei nomi come in Esplora file (foto2 prima di foto10)
        public static int CompareNames(string a, string b)
        {
            try { return StrCmpLogicalW(a, b); } catch { return string.Compare(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        public static string FormatBytes(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString(b < 10 * 1024 ? "0.0" : "0") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.0") + " MB";
            return (b / 1073741824.0).ToString("0.00") + " GB";
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        public static void SelectInExplorer(string file)
        {
            try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { }
        }
    }

    // Impostazioni in %APPDATA%\CorbolImg\settings.txt, righe "chiave=valore".
    static class Settings
    {
        static readonly string Path_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CorbolImg", "settings.txt");
        static readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public static void Load()
        {
            try
            {
                if (!File.Exists(Path_)) return;
                foreach (var ln in File.ReadAllLines(Path_, Encoding.UTF8))
                {
                    int i = ln.IndexOf('=');
                    if (i > 0) values[ln.Substring(0, i).Trim()] = ln.Substring(i + 1).Trim();
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path_));
                File.WriteAllLines(Path_, values.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(false));
            }
            catch { }
        }

        public static string Get(string key, string def) { string v; return values.TryGetValue(key, out v) ? v : def; }
        public static int Get(string key, int def) { int v; return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def; }
        public static bool Get(string key, bool def) { string v = Get(key, ""); return v == "1" ? true : v == "0" ? false : def; }
        public static void Set(string key, string v) { values[key] = v ?? ""; }
        public static void Set(string key, int v) { values[key] = v.ToString(CultureInfo.InvariantCulture); }
        public static void Set(string key, bool v) { values[key] = v ? "1" : "0"; }
    }

    // Controllo aggiornamenti: legge l'ultima release da GitHub e propone solo l'installer pubblicato nel repository.
    static class Updater
    {
        static int checking;
        public static string Version, Url, Sha;
        public static event Action Available;

        public static void CheckIfDue(Control ui)
        {
            if (!Settings.Get("autoUpdate", false)) return;
            DateTime last;
            if (DateTime.TryParse(Settings.Get("lastUpdCheck", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out last) &&
                DateTime.Now - last < TimeSpan.FromHours(24)) return;
            Check(ui, false);
        }

        public static void Check(Control ui, bool manual)
        {
            if (Interlocked.Exchange(ref checking, 1) == 1) return;
            Settings.Set("lastUpdCheck", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
            Settings.Save();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string tag = null, url = null, sha = null;
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    var req = (HttpWebRequest)WebRequest.Create(AppVersion.RepoUrl.Replace("https://github.com/", "https://api.github.com/repos/") + "/releases/latest");
                    req.Timeout = 10000;
                    req.UserAgent = "CorbolImg/" + AppVersion.Text;
                    req.Accept = "application/vnd.github+json";
                    string json;
                    using (var resp = req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream())) json = sr.ReadToEnd();
                    var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(\\.[0-9]+){1,3})\"");
                    if (m.Success) tag = m.Groups[1].Value;
                    m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(" + Regex.Escape(AppVersion.RepoUrl + "/releases/download/") + "[^\"]+\\.exe)\"");
                    if (m.Success) url = m.Groups[1].Value;
                    m = Regex.Match(json, "SHA-256:\\s*`?([0-9a-fA-F]{64})");
                    if (m.Success) sha = m.Groups[1].Value.ToLowerInvariant();
                }
                catch { }
                checking = 0;
                try { ui.BeginInvoke(new Action(delegate { OnResult(ui, tag, url, sha, manual); })); } catch { }
            });
        }

        static void OnResult(Control ui, string tag, string url, string sha, bool manual)
        {
            System.Version latest;
            if (tag == null || !System.Version.TryParse(tag, out latest))
            {
                if (manual) Msg(ui, L.T("Impossibile controllare gli aggiornamenti. Verifica la connessione a Internet e riprova.",
                                        "Could not check for updates. Check your Internet connection and try again."), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (latest > new System.Version(AppVersion.Text))
            {
                Version = tag; Url = url; Sha = sha;
                if (Available != null) Available();
                if (manual) Offer(ui);
            }
            else if (manual)
                Msg(ui, L.F("Hai già l'ultima versione di CorbolImg ({0}).", "You already have the latest version of CorbolImg ({0}).", AppVersion.Text),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void Offer(Control ui)
        {
            if (Version == null) return;
            string page = AppVersion.RepoUrl + "/releases/tag/v" + Version;
            if (Url == null) { Util.OpenUrl(page); return; }
            var r = Msg(ui, L.F("È disponibile CorbolImg {0} (installata: {1}).\n\nScaricare e installare ora? L'installer chiuderà CorbolImg durante l'aggiornamento; le impostazioni restano.\n\nNovità: {2}",
                                "CorbolImg {0} is available (installed: {1}).\n\nDownload and install it now? The installer will close CorbolImg during the update; your settings are kept.\n\nWhat's new: {2}",
                                Version, AppVersion.Text, page), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) Download(ui, Url, Sha, Version, page);
        }

        static void Download(Control ui, string url, string sha, string ver, string page)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string file = Path.Combine(Path.GetTempPath(), "CorbolImg-Setup-" + ver + ".exe");
                string err = null;
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "CorbolImg/" + AppVersion.Text;
                        wc.DownloadFile(url, file);
                    }
                    if (sha != null)
                    {
                        // Confronta con lo SHA-256 scritto nelle note della release
                        string got;
                        using (var st = File.OpenRead(file))
                        using (var h = SHA256.Create())
                            got = BitConverter.ToString(h.ComputeHash(st)).Replace("-", "").ToLowerInvariant();
                        if (got != sha)
                        {
                            File.Delete(file);
                            err = L.T("il file scaricato non corrisponde a quello pubblicato (SHA-256 diverso).", "the downloaded file does not match the published one (SHA-256 mismatch).");
                        }
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    ui.BeginInvoke(new Action(delegate
                    {
                        if (err == null)
                        {
                            try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); return; }
                            catch (Exception ex) { err = ex.Message; }
                        }
                        if (Msg(ui, L.F("Aggiornamento non riuscito: {0}\n\nAprire la pagina di download?", "Update failed: {0}\n\nOpen the download page?", err),
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            Util.OpenUrl(page);
                    }));
                }
                catch { }
            });
        }

        static DialogResult Msg(Control owner, string text, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBox.Show(owner, text, "CorbolImg", buttons, icon);
        }
    }

    class AboutForm : Form
    {
        static AboutForm open;

        public static void ShowOnce(Icon icon)
        {
            if (open == null || open.IsDisposed) open = new AboutForm(icon);
            open.Show();
            open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        AboutForm(Icon icon)
        {
            Text = L.T("Informazioni su CorbolImg", "About CorbolImg");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;
            ClientSize = new Size(380, 200);
            if (icon != null) Icon = icon;

            var accent = Color.FromArgb(124, 58, 237);
            var pic = new PictureBox { Location = new Point(20, 20), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom };
            try
            {
                // Le immagini in corbolimg.ico sono PNG: si legge direttamente quella da 64 px
                using (var st = typeof(AboutForm).Assembly.GetManifestResourceStream("CorbolImg.corbolimg.ico"))
                using (var ms = new MemoryStream())
                {
                    st.CopyTo(ms);
                    byte[] d = ms.ToArray();
                    int n = BitConverter.ToUInt16(d, 4);
                    for (int i = 0; i < n; i++)
                    {
                        int e = 6 + 16 * i;
                        if (d[e] != 64) continue;
                        int size = BitConverter.ToInt32(d, e + 8), off = BitConverter.ToInt32(d, e + 12);
                        using (var img = Image.FromStream(new MemoryStream(d, off, size))) pic.Image = new Bitmap(img);
                        break;
                    }
                }
            }
            catch { }
            var title = new Label { Text = "CorbolImg " + AppVersion.Text, AutoSize = true, Location = new Point(82, 18), Font = new Font("Segoe UI Semibold", 14f) };
            var desc = new Label
            {
                Text = L.T("Visualizzatore di immagini con convertitore: JPEG, PNG, HEIC, WebP, TIFF, GIF, BMP, ICO, PDF e altri",
                           "Image viewer and converter: JPEG, PNG, HEIC, WebP, TIFF, GIF, BMP, ICO, PDF and more"),
                AutoSize = true, MaximumSize = new Size(280, 0), Location = new Point(84, 50), ForeColor = Color.DimGray
            };
            var author = new Label { Text = "© 2026 " + AppVersion.Author, AutoSize = true, Location = new Point(84, 90) };

            string lic = L.T("Licenza MIT", "MIT License");
            var links = new LinkLabel
            {
                Text = lic + "  ·  GitHub", AutoSize = true, Location = new Point(84, 112),
                LinkColor = accent, ActiveLinkColor = accent, LinkBehavior = LinkBehavior.HoverUnderline
            };
            links.Links.Clear();
            links.Links.Add(0, lic.Length, AppVersion.RepoUrl + "/blob/main/LICENSE");
            links.Links.Add(lic.Length + 5, 6, AppVersion.RepoUrl);
            links.LinkClicked += (s, e) => Util.OpenUrl((string)e.Link.LinkData);

            var ok = new Button { Text = "OK", Size = new Size(90, 30), DialogResult = DialogResult.OK };
            ok.Click += delegate { Close(); };
            AcceptButton = CancelButton = ok;
            Controls.AddRange(new Control[] { pic, title, desc, author, links, ok });

            Load += delegate
            {
                // Testi sotto la descrizione, qualunque sia la sua altezza (lingua, DPI)
                author.Top = desc.Bottom + 14;
                links.Top = author.Bottom + 4;
                ok.Location = new Point(ClientSize.Width - 16 - ok.Width, links.Bottom + 16);
                ClientSize = new Size(ClientSize.Width, ok.Bottom + 14);
            };
        }
    }
}
