// AppImg - converte immagini tra formati diversi: legge tutto ciò che Windows sa aprire
// (JPEG, PNG, HEIC, WebP, AVIF, TIFF, BMP, GIF, ICO, RAW...) e salva in PNG, JPEG, WebP, HEIC,
// TIFF, BMP, GIF, JPEG XR, ICO o PDF, anche molti file insieme.
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

namespace AppImg
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        // Deve coincidere con quello scritto dall'installer nel collegamento del menu Start
        public const string AppUserModelId = "AppImg.Converter";

        // AppImg.exe [file o cartelle...]: apre la finestra con quei file già in elenco
        // (è così che funziona anche "Invia a > AppImg" di Esplora file).
        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Un errore imprevisto nell'interfaccia mostra un messaggio invece di chiudere l'app
            Application.ThreadException += (s, e) =>
                MessageBox.Show(L.T("Si è verificato un errore:", "An error occurred:") + "\n" + e.Exception.Message, "AppImg", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new MainForm(args));
        }

        // Le operazioni WIC/WPF girano su thread STA dedicati, così la finestra resta reattiva.
        public static void RunSta(ThreadStart work)
        {
            var t = new Thread(work) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    // Impostazioni in %APPDATA%\AppImg\settings.txt, righe "chiave=valore".
    static class Settings
    {
        static readonly string Path_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppImg", "settings.txt");
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

    class Item
    {
        public string Path;
        public ImageInfo Info;
        public bool Unsupported;
        public ListViewItem Row;
    }

    class MainForm : Form
    {
        // Menu
        readonly MenuStrip menu = new MenuStrip();
        readonly ToolStripMenuItem mFile = new ToolStripMenuItem(), mAddFiles = new ToolStripMenuItem(), mAddFolder = new ToolStripMenuItem(),
            mExit = new ToolStripMenuItem(), mLang = new ToolStripMenuItem(), mHelp = new ToolStripMenuItem(), mCheckUpd = new ToolStripMenuItem(),
            mAutoUpd = new ToolStripMenuItem(), mUpdAvail = new ToolStripMenuItem(), mAbout = new ToolStripMenuItem(), mSite = new ToolStripMenuItem();
        readonly List<ToolStripMenuItem> miLang = new List<ToolStripMenuItem>();
        // Barra strumenti
        readonly ToolStrip tools = new ToolStrip();
        readonly ToolStripButton tAddFiles = new ToolStripButton(), tAddFolder = new ToolStripButton(), tRemove = new ToolStripButton(), tClear = new ToolStripButton();
        // Elenco e anteprima
        readonly SplitContainer split = new SplitContainer();
        readonly ListView list = new ListView();
        readonly ColumnHeader chFile = new ColumnHeader(), chFmt = new ColumnHeader(), chDim = new ColumnHeader(), chBytes = new ColumnHeader(), chState = new ColumnHeader();
        readonly Label dropHint = new Label();
        readonly PictureBox preview = new PictureBox();
        readonly Label previewInfo = new Label();
        readonly ContextMenuStrip listMenu = new ContextMenuStrip();
        readonly ToolStripMenuItem cmOpen = new ToolStripMenuItem(), cmFolder = new ToolStripMenuItem(), cmRemove = new ToolStripMenuItem();
        // Opzioni
        readonly Label lFormat = new Label(), lQuality = new Label(), lQualityVal = new Label(), lSize = new Label(), lPx = new Label(), lDest = new Label(), lOptions = new Label();
        readonly ComboBox cbFormat = new ComboBox();
        readonly TrackBar tbQuality = new TrackBar();
        readonly CheckBox chkResize = new CheckBox(), chkOverwrite = new CheckBox(), chkOpenFolder = new CheckBox();
        readonly NumericUpDown numMax = new NumericUpDown();
        readonly RadioButton rbSame = new RadioButton(), rbFolder = new RadioButton();
        readonly TextBox txtFolder = new TextBox();
        readonly Button btnBrowse = new Button(), btnConvert = new Button();
        readonly ProgressBar progress = new ProgressBar();
        readonly Label status = new Label();
        readonly ToolTip tips = new ToolTip();

        readonly List<Item> items = new List<Item>();
        readonly Queue<Item> infoQueue = new Queue<Item>();
        bool infoRunning;
        int previewSeq;
        volatile bool converting, cancel;
        float scale = 1f;

        // Aggiornamenti
        int checkingUpd;
        string updVersion, updUrl, updSha;

        public MainForm(string[] args)
        {
            Settings.Load();
            L.Set(Settings.Get("lang", "auto"));

            // Dimensioni scalate a mano con S(): il font in punti segue già i DPI dello schermo
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9f);
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(Settings.Get("width", 940)), S(Settings.Get("height", 620)));
            MinimumSize = new Size(S(700), S(480));
            if (Settings.Get("maximized", false)) WindowState = FormWindowState.Maximized;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildMenu();
            BuildTools();
            BuildList();
            var opts = BuildOptions();
            var bottom = BuildBottomBar();

            Controls.Add(split);
            Controls.Add(opts);
            Controls.Add(bottom);
            Controls.Add(tools);
            Controls.Add(menu);
            MainMenuStrip = menu;

            LoadOptions();
            ApplyTexts();

            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            KeyPreview = true;
            KeyDown += OnKeyDown;
            FormClosing += delegate { cancel = true; SaveOptions(); };
            Load += delegate
            {
                // I minimi si impostano qui: prima la finestra non ha ancora la sua larghezza
                try
                {
                    split.SplitterDistance = Math.Max(S(200), (int)(split.Width * 0.62));
                    split.Panel1MinSize = S(200);
                    split.Panel2MinSize = S(160);
                }
                catch { }
                AddPaths(args);
                if (Settings.Get("autoUpdate", false))
                {
                    DateTime last;
                    if (!DateTime.TryParse(Settings.Get("lastUpdCheck", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out last) ||
                        DateTime.Now - last > TimeSpan.FromHours(24))
                        CheckUpdates(false);
                }
            };
        }

        // ===================== Costruzione dell'interfaccia =====================

        int S(int v) { return (int)Math.Round(v * scale); }

        ToolStripMenuItem Mi(ToolStripMenuItem m, EventHandler click)
        {
            if (click != null) m.Click += click;
            return m;
        }

        void BuildMenu()
        {
            mAddFiles.ShortcutKeys = Keys.Control | Keys.O;
            mFile.DropDownItems.AddRange(new ToolStripItem[]
            {
                Mi(mAddFiles, delegate { AddFilesDialog(); }),
                Mi(mAddFolder, delegate { AddFolderDialog(); }),
                new ToolStripSeparator(),
                Mi(mExit, delegate { Close(); })
            });

            var auto = new ToolStripMenuItem { Tag = "auto" };
            auto.Click += delegate { SetLang("auto"); };
            miLang.Add(auto);
            mLang.DropDownItems.Add(auto);
            mLang.DropDownItems.Add(new ToolStripSeparator());
            foreach (var lang in L.All())
            {
                var m = new ToolStripMenuItem(lang.Name) { Tag = lang.Code };
                string code = lang.Code;
                m.Click += delegate { SetLang(code); };
                miLang.Add(m);
                mLang.DropDownItems.Add(m);
            }

            mAutoUpd.Click += delegate
            {
                bool v = !Settings.Get("autoUpdate", false);
                Settings.Set("autoUpdate", v);
                Settings.Save();
                mAutoUpd.Checked = v;
                if (v) CheckUpdates(false);
            };
            mUpdAvail.Visible = false;
            mUpdAvail.Font = new Font(mUpdAvail.Font, FontStyle.Bold);
            mHelp.DropDownItems.AddRange(new ToolStripItem[]
            {
                Mi(mUpdAvail, delegate { OfferUpdate(); }),
                Mi(mCheckUpd, delegate { CheckUpdates(true); }),
                mAutoUpd,
                new ToolStripSeparator(),
                Mi(mSite, delegate { OpenUrl(AppVersion.RepoUrl); }),
                Mi(mAbout, delegate { AboutForm.ShowOnce(Icon); })
            });
            mHelp.DropDownOpening += delegate { mAutoUpd.Checked = Settings.Get("autoUpdate", false); };
            menu.Items.AddRange(new ToolStripItem[] { mFile, mLang, mHelp });
        }

        void BuildTools()
        {
            tools.GripStyle = ToolStripGripStyle.Hidden;
            tools.Padding = new Padding(S(6), S(2), S(6), S(2));
            tools.ImageScalingSize = new Size(S(16), S(16));
            tAddFiles.Click += delegate { AddFilesDialog(); };
            tAddFolder.Click += delegate { AddFolderDialog(); };
            tRemove.Click += delegate { RemoveSelected(); };
            tClear.Click += delegate { ClearAll(); };
            foreach (var b in new[] { tAddFiles, tAddFolder, tRemove, tClear }) b.DisplayStyle = ToolStripItemDisplayStyle.Text;
            tools.Items.AddRange(new ToolStripItem[] { tAddFiles, tAddFolder, new ToolStripSeparator(), tRemove, tClear });
        }

        void BuildList()
        {
            split.Dock = DockStyle.Fill;
            split.Panel1.Padding = new Padding(S(10), S(6), 0, 0);
            split.Panel2.Padding = new Padding(0, S(6), S(10), 0);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.AllowDrop = true;
            list.Columns.AddRange(new[] { chFile, chFmt, chDim, chBytes, chState });
            chFile.Width = S(220); chFmt.Width = S(70); chDim.Width = S(90); chBytes.Width = S(70); chState.Width = S(160);
            chBytes.TextAlign = HorizontalAlignment.Right;
            list.DragEnter += OnDragEnter;
            list.DragDrop += OnDragDrop;
            list.SelectedIndexChanged += delegate { ShowPreview(); };
            list.DoubleClick += delegate { OpenSelected(); };
            list.ContextMenuStrip = listMenu;
            listMenu.Items.AddRange(new ToolStripItem[]
            {
                Mi(cmOpen, delegate { OpenSelected(); }),
                Mi(cmFolder, delegate { OpenSelectedFolder(); }),
                new ToolStripSeparator(),
                Mi(cmRemove, delegate { RemoveSelected(); })
            });
            listMenu.Opening += (s, e) => { if (list.SelectedItems.Count == 0) e.Cancel = true; };

            // Suggerimento al centro dell'elenco vuoto
            dropHint.AutoSize = false;
            dropHint.TextAlign = ContentAlignment.MiddleCenter;
            dropHint.ForeColor = Color.Gray;
            dropHint.BackColor = SystemColors.Window;
            dropHint.Font = new Font(Font.FontFamily, 11f);
            dropHint.AllowDrop = true;
            dropHint.Cursor = Cursors.Hand;
            dropHint.DragEnter += OnDragEnter;
            dropHint.DragDrop += OnDragDrop;
            dropHint.Click += delegate { AddFilesDialog(); };
            list.Controls.Add(dropHint);
            list.Resize += delegate { PlaceHint(); FitLastColumn(); };

            preview.Dock = DockStyle.Fill;
            preview.SizeMode = PictureBoxSizeMode.Zoom;
            preview.BackColor = Color.FromArgb(236, 238, 242);
            preview.BorderStyle = BorderStyle.FixedSingle;
            previewInfo.Dock = DockStyle.Bottom;
            previewInfo.AutoSize = false;
            previewInfo.Height = S(40);
            previewInfo.TextAlign = ContentAlignment.MiddleLeft;
            previewInfo.ForeColor = Color.DimGray;
            previewInfo.AutoEllipsis = true;

            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(preview);
            split.Panel2.Controls.Add(previewInfo);
        }

        static FlowLayoutPanel Row(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
            f.Controls.AddRange(cs);
            return f;
        }

        Control BuildOptions()
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, Padding = new Padding(S(10), S(8), S(10), S(4))
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            foreach (var l in new[] { lFormat, lSize, lDest, lOptions })
            {
                l.AutoSize = true;
                l.Anchor = AnchorStyles.Left;
                l.Margin = new Padding(0, S(4), S(10), S(4));
            }
            foreach (var l in new[] { lQuality, lQualityVal, lPx })
            {
                l.AutoSize = true;
                l.Margin = new Padding(S(4), S(7), S(4), 0);
            }
            lQuality.Margin = new Padding(S(18), S(7), 0, 0);
            lQualityVal.MinimumSize = new Size(S(30), 0);

            cbFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            cbFormat.Width = S(170);
            cbFormat.Margin = new Padding(0, S(3), 0, S(3));
            foreach (var f in Imaging.Outputs()) cbFormat.Items.Add(f);
            cbFormat.SelectedIndexChanged += delegate { UpdateQualityState(); };

            tbQuality.Minimum = 1;
            tbQuality.Maximum = 100;
            tbQuality.TickStyle = TickStyle.None;
            tbQuality.AutoSize = false;
            tbQuality.Size = new Size(S(160), S(28));
            tbQuality.Margin = new Padding(0, S(2), 0, 0);
            tbQuality.ValueChanged += delegate { lQualityVal.Text = tbQuality.Value.ToString(); };

            chkResize.AutoSize = true;
            chkResize.Margin = new Padding(0, S(6), S(4), 0);
            chkResize.CheckedChanged += delegate { numMax.Enabled = chkResize.Checked; };
            numMax.Minimum = 16;
            numMax.Maximum = 20000;
            numMax.Increment = 100;
            numMax.Width = S(80);
            numMax.Margin = new Padding(0, S(3), 0, S(3));

            foreach (var c in new ButtonBase[] { rbSame, rbFolder, chkOverwrite, chkOpenFolder })
            {
                c.AutoSize = true;
                c.Margin = new Padding(0, S(6), S(14), 0);
            }
            rbFolder.Margin = new Padding(0, S(6), S(2), 0);
            rbFolder.CheckedChanged += delegate { txtFolder.Enabled = btnBrowse.Enabled = rbFolder.Checked; };
            txtFolder.Width = S(300);
            txtFolder.Margin = new Padding(0, S(3), S(4), S(3));
            btnBrowse.AutoSize = true;
            btnBrowse.Margin = new Padding(0, S(2), 0, S(2));
            btnBrowse.Click += delegate { BrowseFolder(); };

            t.Controls.Add(lFormat, 0, 0);
            t.Controls.Add(Row(cbFormat, lQuality, tbQuality, lQualityVal), 1, 0);
            t.Controls.Add(lSize, 0, 1);
            t.Controls.Add(Row(chkResize, numMax, lPx), 1, 1);
            t.Controls.Add(lDest, 0, 2);
            t.Controls.Add(Row(rbSame, rbFolder, txtFolder, btnBrowse), 1, 2);
            t.Controls.Add(lOptions, 0, 3);
            t.Controls.Add(Row(chkOverwrite, chkOpenFolder), 1, 3);
            return t;
        }

        Control BuildBottomBar()
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3,
                Padding = new Padding(S(10), S(6), S(10), S(10)), BackColor = Color.FromArgb(244, 246, 249)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status.AutoSize = false;
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            progress.Size = new Size(S(200), S(18));
            progress.Anchor = AnchorStyles.Right;
            progress.Margin = new Padding(S(8), 0, S(10), 0);
            progress.Visible = false;
            btnConvert.AutoSize = true;
            btnConvert.MinimumSize = new Size(S(130), S(34));
            btnConvert.Font = new Font(Font, FontStyle.Bold);
            btnConvert.Click += delegate { if (converting) cancel = true; else StartConvert(); };
            t.Controls.Add(status, 0, 0);
            t.Controls.Add(progress, 1, 0);
            t.Controls.Add(btnConvert, 2, 0);
            return t;
        }

        // La colonna "Stato" occupa lo spazio rimasto, senza barra di scorrimento orizzontale
        void FitLastColumn()
        {
            int used = chFile.Width + chFmt.Width + chDim.Width + chBytes.Width;
            int free = list.ClientSize.Width - used;
            if (free > S(80)) chState.Width = free;
        }

        void PlaceHint()
        {
            dropHint.Bounds = new Rectangle(0, S(26), list.ClientSize.Width, Math.Max(0, list.ClientSize.Height - S(26)));
            dropHint.Visible = items.Count == 0;
        }

        // Tutti i testi dell'interfaccia: richiamato anche quando si cambia lingua.
        void ApplyTexts()
        {
            Text = "AppImg";
            mFile.Text = L.T("&File", "&File");
            mAddFiles.Text = L.T("Aggiungi immagini...", "Add images...");
            mAddFolder.Text = L.T("Aggiungi cartella...", "Add folder...");
            mExit.Text = L.T("Esci", "Exit");
            mLang.Text = "Lingua / Language";
            miLang[0].Text = L.T("Automatica (lingua di Windows)", "Automatic (Windows language)");
            mHelp.Text = "&?";
            mCheckUpd.Text = L.T("Controlla aggiornamenti", "Check for updates");
            mAutoUpd.Text = L.T("Controlla automaticamente (una volta al giorno)", "Check automatically (once a day)");
            if (updVersion != null) mUpdAvail.Text = L.F("Aggiorna ad AppImg {0}...", "Update to AppImg {0}...", updVersion);
            mSite.Text = L.T("Pagina del progetto (GitHub)", "Project page (GitHub)");
            mAbout.Text = L.T("Informazioni su AppImg", "About AppImg");
            foreach (var m in miLang) m.Checked = (string)m.Tag == L.Setting;

            tAddFiles.Text = L.T("+ Immagini", "+ Images");
            tAddFolder.Text = L.T("+ Cartella", "+ Folder");
            tRemove.Text = L.T("Rimuovi", "Remove");
            tClear.Text = L.T("Svuota elenco", "Clear list");

            chFile.Text = L.T("File", "File");
            chFmt.Text = L.T("Formato", "Format");
            chDim.Text = L.T("Dimensioni", "Dimensions");
            chBytes.Text = L.T("Peso", "Size");
            chState.Text = L.T("Stato", "Status");
            dropHint.Text = L.T("Trascina qui immagini o cartelle\noppure clicca per sceglierle",
                                "Drag images or folders here\nor click to choose them");
            cmOpen.Text = L.T("Apri", "Open");
            cmFolder.Text = L.T("Apri cartella", "Open folder");
            cmRemove.Text = L.T("Rimuovi dall'elenco", "Remove from list");

            lFormat.Text = L.T("Converti in:", "Convert to:");
            lQuality.Text = L.T("Qualità:", "Quality:");
            lSize.Text = L.T("Dimensioni:", "Size:");
            chkResize.Text = L.T("Riduci se il lato lungo supera", "Shrink if the long side exceeds");
            lPx.Text = "px";
            lDest.Text = L.T("Salva in:", "Save to:");
            rbSame.Text = L.T("Stessa cartella dell'originale", "Same folder as the original");
            rbFolder.Text = L.T("Cartella:", "Folder:");
            btnBrowse.Text = L.T("Sfoglia...", "Browse...");
            lOptions.Text = L.T("Opzioni:", "Options:");
            chkOverwrite.Text = L.T("Sovrascrivi i file esistenti", "Overwrite existing files");
            chkOpenFolder.Text = L.T("Apri la cartella al termine", "Open the folder when done");
            tips.SetToolTip(chkOverwrite, L.T("Se non è selezionata, i nuovi file vengono rinominati, es. foto (1).png. L'originale non viene mai sovrascritto.",
                                              "If unchecked, new files are renamed, e.g. photo (1).png. The original is never overwritten."));
            tips.SetToolTip(chkResize, L.T("Le immagini più piccole non vengono ingrandite.", "Smaller images are never enlarged."));
            btnConvert.Text = converting ? L.T("Annulla", "Cancel") : L.T("Converti", "Convert");
            if (!converting) UpdateStatusIdle();
            UpdateQualityState();
            ShowPreviewInfo();
            foreach (var it in items) RefreshRow(it, null);
        }

        void SetLang(string code)
        {
            L.Set(code);
            Settings.Set("lang", L.Setting);
            Settings.Save();
            ApplyTexts();
        }

        // ===================== Opzioni =====================

        OutputFormat SelectedFormat { get { return cbFormat.SelectedItem as OutputFormat; } }

        void LoadOptions()
        {
            string fmt = Settings.Get("format", "png");
            var outs = Imaging.Outputs();
            int idx = outs.FindIndex(f => f.Id == fmt);
            cbFormat.SelectedIndex = idx >= 0 ? idx : 0;
            tbQuality.Value = Math.Max(1, Math.Min(100, Settings.Get("quality", 90)));
            lQualityVal.Text = tbQuality.Value.ToString();
            chkResize.Checked = Settings.Get("resize", false);
            numMax.Value = Math.Max(numMax.Minimum, Math.Min(numMax.Maximum, Settings.Get("maxSide", 1920)));
            numMax.Enabled = chkResize.Checked;
            string dir = Settings.Get("outDir", "");
            txtFolder.Text = dir.Length > 0 ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AppImg");
            rbFolder.Checked = Settings.Get("useOutDir", false);
            rbSame.Checked = !rbFolder.Checked;
            txtFolder.Enabled = btnBrowse.Enabled = rbFolder.Checked;
            chkOverwrite.Checked = Settings.Get("overwrite", false);
            chkOpenFolder.Checked = Settings.Get("openFolder", false);
        }

        void SaveOptions()
        {
            var f = SelectedFormat;
            if (f != null) Settings.Set("format", f.Id);
            Settings.Set("quality", tbQuality.Value);
            Settings.Set("resize", chkResize.Checked);
            Settings.Set("maxSide", (int)numMax.Value);
            Settings.Set("useOutDir", rbFolder.Checked);
            Settings.Set("outDir", txtFolder.Text.Trim());
            Settings.Set("overwrite", chkOverwrite.Checked);
            Settings.Set("openFolder", chkOpenFolder.Checked);
            Settings.Set("maximized", WindowState == FormWindowState.Maximized);
            var b = WindowState == FormWindowState.Normal ? ClientSize : RestoreBounds.Size - (Size - ClientSize);
            Settings.Set("width", (int)(b.Width / scale));
            Settings.Set("height", (int)(b.Height / scale));
            Settings.Save();
        }

        void UpdateQualityState()
        {
            var f = SelectedFormat;
            bool q = f != null && f.Quality;
            lQuality.Enabled = tbQuality.Enabled = lQualityVal.Enabled = q;
            string tip = f == null ? "" : f.Alpha ? L.T("Conserva la trasparenza.", "Keeps transparency.")
                                                  : L.T("Le parti trasparenti diventano bianche.", "Transparent areas become white.");
            if (f != null && f.Id == "ico") tip += " " + L.T("Icona con le dimensioni da 16 a 256 px.", "Icon with sizes from 16 to 256 px.");
            if (f != null && f.Id == "pdf") tip += " " + L.T("Un PDF di una pagina per ogni immagine.", "One single-page PDF per image.");
            tips.SetToolTip(cbFormat, tip);
        }

        void BrowseFolder()
        {
            using (var d = new FolderBrowserDialog { Description = L.T("Cartella in cui salvare le immagini convertite", "Folder where converted images are saved"), ShowNewFolderButton = true })
            {
                if (Directory.Exists(txtFolder.Text)) d.SelectedPath = txtFolder.Text;
                if (d.ShowDialog(this) == DialogResult.OK) txtFolder.Text = d.SelectedPath;
            }
        }

        // ===================== Elenco dei file =====================

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !converting && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null) BeginInvoke(new Action(delegate { AddPaths(paths); }));
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete && list.Focused) { RemoveSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.A && e.Control && list.Focused)
            {
                list.BeginUpdate();
                foreach (ListViewItem r in list.Items) r.Selected = true;
                list.EndUpdate();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.V && e.Control && !(ActiveControl is TextBox) && !(ActiveControl is NumericUpDown) && Clipboard.ContainsFileDropList())
            {
                AddPaths(Clipboard.GetFileDropList().Cast<string>());
                e.Handled = true;
            }
        }

        void AddFilesDialog()
        {
            if (converting) return;
            var exts = Imaging.InputExtensions();
            string pattern = string.Join(";", exts.Select(x => "*" + x));
            using (var d = new OpenFileDialog
            {
                Multiselect = true,
                Title = L.T("Aggiungi immagini", "Add images"),
                Filter = L.T("Immagini", "Images") + "|" + pattern + "|" + L.T("Tutti i file", "All files") + " (*.*)|*.*"
            })
            {
                string last = Settings.Get("lastInDir", "");
                if (Directory.Exists(last)) d.InitialDirectory = last;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Settings.Set("lastInDir", Path.GetDirectoryName(d.FileNames[0]));
                AddPaths(d.FileNames);
            }
        }

        void AddFolderDialog()
        {
            if (converting) return;
            using (var d = new FolderBrowserDialog { Description = L.T("Aggiungi tutte le immagini di una cartella (sottocartelle comprese)", "Add all images in a folder (including subfolders)"), ShowNewFolderButton = false })
            {
                string last = Settings.Get("lastInDir", "");
                if (Directory.Exists(last)) d.SelectedPath = last;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Settings.Set("lastInDir", d.SelectedPath);
                AddPaths(new[] { d.SelectedPath });
            }
        }

        // File singoli: vengono provati anche con estensioni sconosciute. Cartelle: solo le estensioni delle immagini.
        void AddPaths(IEnumerable<string> paths)
        {
            if (converting || paths == null) return;
            var known = new HashSet<string>(items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            var add = new List<string>();
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (var raw in paths)
                {
                    string p;
                    try { p = Path.GetFullPath(raw.Trim('"')); } catch { continue; }
                    if (Directory.Exists(p))
                    {
                        IEnumerable<string> files;
                        try { files = SafeEnumerate(p).Where(Imaging.IsImageExt).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
                        catch { continue; }
                        foreach (var f in files) if (known.Add(f)) add.Add(f);
                    }
                    else if (File.Exists(p) && known.Add(p)) add.Add(p);
                }
            }
            finally { Cursor = Cursors.Default; }
            if (add.Count == 0) { UpdateStatusIdle(); return; }

            list.BeginUpdate();
            foreach (var p in add)
            {
                var it = new Item { Path = p };
                it.Row = new ListViewItem(new[] { Path.GetFileName(p), "", "", "", "" }) { Tag = it, ToolTipText = p };
                long len = 0;
                try { len = new FileInfo(p).Length; } catch { }
                it.Row.SubItems[3].Text = FormatBytes(len);
                items.Add(it);
                list.Items.Add(it.Row);
                lock (infoQueue) infoQueue.Enqueue(it);
            }
            list.EndUpdate();
            list.ShowItemToolTips = true;
            PlaceHint();
            if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[list.Items.Count - add.Count].Selected = true;
            UpdateStatusIdle();
            StartInfoLoader();
        }

        // Elenca i file di una cartella e sottocartelle, saltando quelle non accessibili.
        static IEnumerable<string> SafeEnumerate(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files = new string[0], dirs = new string[0];
                try { files = Directory.GetFiles(dir); } catch { }
                try { dirs = Directory.GetDirectories(dir); } catch { }
                foreach (var f in files) yield return f;
                foreach (var d in dirs.Reverse())
                {
                    try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                    stack.Push(d);
                }
            }
        }

        // Legge formato e dimensioni in sottofondo, un file alla volta.
        void StartInfoLoader()
        {
            lock (infoQueue)
            {
                if (infoRunning) return;
                infoRunning = true;
            }
            Program.RunSta(delegate
            {
                while (true)
                {
                    Item it;
                    lock (infoQueue)
                    {
                        if (infoQueue.Count == 0) { infoRunning = false; return; }
                        it = infoQueue.Dequeue();
                    }
                    ImageInfo info = null;
                    try { info = Imaging.GetInfo(it.Path); } catch { }
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            it.Info = info;
                            it.Unsupported = info == null;
                            RefreshRow(it, null);
                            if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag == it) ShowPreviewInfo();
                            if (!converting) UpdateStatusIdle();
                        }));
                    }
                    catch { return; } // finestra chiusa
                }
            });
        }

        void RefreshRow(Item it, string state)
        {
            if (it.Row == null) return;
            if (it.Info != null)
            {
                string fmt = it.Info.Format;
                if (it.Info.Frames > 1) fmt += " ×" + it.Info.Frames;
                it.Row.SubItems[1].Text = fmt;
                it.Row.SubItems[2].Text = it.Info.Width + " × " + it.Info.Height;
            }
            if (state != null) it.Row.SubItems[4].Text = state;
            else if (it.Unsupported)
            {
                it.Row.SubItems[4].Text = L.T("Formato non leggibile", "Unreadable format");
                it.Row.ForeColor = Color.FromArgb(190, 60, 0);
            }
        }

        void RemoveSelected()
        {
            if (converting || list.SelectedItems.Count == 0) return;
            int first = list.SelectedIndices[0];
            list.BeginUpdate();
            foreach (ListViewItem r in list.SelectedItems.Cast<ListViewItem>().ToList())
            {
                var it = (Item)r.Tag;
                items.Remove(it);
                list.Items.Remove(r);
            }
            list.EndUpdate();
            if (list.Items.Count > 0) list.Items[Math.Min(first, list.Items.Count - 1)].Selected = true;
            else ShowPreview();
            PlaceHint();
            UpdateStatusIdle();
        }

        void ClearAll()
        {
            if (converting) return;
            lock (infoQueue) infoQueue.Clear();
            items.Clear();
            list.Items.Clear();
            ShowPreview();
            PlaceHint();
            UpdateStatusIdle();
        }

        Item SelectedItem { get { return list.SelectedItems.Count > 0 ? (Item)list.SelectedItems[0].Tag : null; } }

        void OpenSelected()
        {
            var it = SelectedItem;
            if (it != null) OpenUrl(it.Path);
        }

        void OpenSelectedFolder()
        {
            var it = SelectedItem;
            if (it != null) SelectInExplorer(it.Path);
        }

        static void SelectInExplorer(string file)
        {
            try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { }
        }

        // ===================== Anteprima =====================

        void ShowPreview()
        {
            var it = SelectedItem;
            int seq = ++previewSeq;
            var old = preview.Image;
            preview.Image = null;
            if (old != null) old.Dispose();
            ShowPreviewInfo();
            if (it == null) return;
            string path = it.Path;
            int max = Math.Max(400, Math.Max(preview.Width, preview.Height) * 2);
            // Attesa breve: scorrendo l'elenco con le frecce non si decodificano tutte le immagini
            var timer = new System.Windows.Forms.Timer { Interval = 120 };
            timer.Tick += delegate
            {
                timer.Dispose();
                if (seq != previewSeq) return;
                Program.RunSta(delegate
                {
                    Bitmap bmp = null;
                    try { bmp = Imaging.Preview(path, max); } catch { }
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (seq != previewSeq || IsDisposed) { if (bmp != null) bmp.Dispose(); return; }
                            preview.Image = bmp;
                        }));
                    }
                    catch { if (bmp != null) bmp.Dispose(); }
                });
            };
            timer.Start();
        }

        void ShowPreviewInfo()
        {
            var it = SelectedItem;
            if (it == null) { previewInfo.Text = ""; return; }
            string s = Path.GetFileName(it.Path);
            if (it.Info != null)
            {
                s += "\n" + it.Info.Format + "  ·  " + it.Info.Width + " × " + it.Info.Height + " px  ·  " + it.Row.SubItems[3].Text;
                if (it.Info.Frames > 1) s += "  ·  " + L.F("{0} fotogrammi/pagine (si converte il primo)", "{0} frames/pages (the first is converted)", it.Info.Frames);
            }
            else if (it.Unsupported) s += "\n" + L.T("Windows non sa leggere questo file. Per HEIC, WebP, AVIF o RAW installa l'estensione dal Microsoft Store.",
                                                     "Windows cannot read this file. For HEIC, WebP, AVIF or RAW install the extension from the Microsoft Store.");
            previewInfo.Text = s;
        }

        // ===================== Conversione =====================

        void UpdateStatusIdle()
        {
            if (converting) return;
            int n = items.Count(i => !i.Unsupported);
            status.Text = items.Count == 0 ? L.T("Nessuna immagine in elenco.", "No images in the list.")
                        : n == 1 ? L.T("1 immagine da convertire.", "1 image to convert.")
                        : L.F("{0} immagini da convertire.", "{0} images to convert.", n);
            btnConvert.Enabled = n > 0 || converting;
        }

        void SetBusy(bool busy)
        {
            converting = busy;
            foreach (Control c in new Control[] { cbFormat, tbQuality, chkResize, numMax, rbSame, rbFolder, txtFolder, btnBrowse, chkOverwrite, chkOpenFolder })
                c.Enabled = !busy;
            if (!busy)
            {
                numMax.Enabled = chkResize.Checked;
                txtFolder.Enabled = btnBrowse.Enabled = rbFolder.Checked;
                UpdateQualityState();
            }
            tools.Enabled = mFile.Enabled = !busy;
            progress.Visible = busy;
            btnConvert.Text = busy ? L.T("Annulla", "Cancel") : L.T("Converti", "Convert");
        }

        void StartConvert()
        {
            var f = SelectedFormat;
            var todo = items.Where(i => !i.Unsupported).ToList();
            if (f == null || todo.Count == 0) return;
            string outDir = null;
            if (rbFolder.Checked)
            {
                outDir = txtFolder.Text.Trim();
                try
                {
                    if (outDir.Length == 0 || !Path.IsPathRooted(outDir)) throw new ArgumentException();
                    outDir = Path.GetFullPath(outDir);
                    Directory.CreateDirectory(outDir);
                }
                catch
                {
                    MessageBox.Show(this, L.T("La cartella di destinazione non è valida o non si può creare.", "The destination folder is not valid or cannot be created."),
                        "AppImg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtFolder.Focus();
                    return;
                }
            }
            SaveOptions();
            int quality = tbQuality.Value, maxSide = chkResize.Checked ? (int)numMax.Value : 0;
            bool overwrite = chkOverwrite.Checked, openFolder = chkOpenFolder.Checked;

            cancel = false;
            SetBusy(true);
            progress.Maximum = todo.Count;
            progress.Value = 0;
            foreach (var it in todo) { it.Row.ForeColor = SystemColors.WindowText; RefreshRow(it, ""); }

            Program.RunSta(delegate
            {
                int ok = 0, failed = 0;
                string firstOut = null;
                var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < todo.Count && !cancel; i++)
                {
                    var it = todo[i];
                    Ui(delegate { RefreshRow(it, L.T("Conversione...", "Converting...")); it.Row.EnsureVisible(); status.Text = L.F("Conversione {0} di {1}: {2}", "Converting {0} of {1}: {2}", i + 1, todo.Count, Path.GetFileName(it.Path)); });
                    string state;
                    bool good = false;
                    string dest = null;
                    try
                    {
                        dest = DestPath(it.Path, outDir, f.Ext, overwrite, produced);
                        produced.Add(dest);
                        Imaging.Convert(it.Path, dest, f, quality, maxSide);
                        long len = new FileInfo(dest).Length;
                        state = "✓ " + Path.GetFileName(dest) + "  (" + FormatBytes(len) + ")";
                        good = true;
                        ok++;
                        if (firstOut == null) firstOut = dest;
                    }
                    catch (Exception ex)
                    {
                        state = L.T("Errore", "Error") + ": " + ShortError(ex);
                        failed++;
                    }
                    int done = i + 1;
                    Ui(delegate
                    {
                        RefreshRow(it, state);
                        it.Row.ForeColor = good ? Color.FromArgb(22, 128, 61) : Color.FromArgb(190, 30, 30);
                        if (good) it.Row.ToolTipText = it.Path + "\n→ " + dest;
                        progress.Value = done;
                    });
                }
                bool stopped = cancel;
                Ui(delegate
                {
                    SetBusy(false);
                    string msg = ok == 1 ? L.T("1 immagine convertita", "1 image converted") : L.F("{0} immagini convertite", "{0} images converted", ok);
                    if (failed > 0) msg += ", " + (failed == 1 ? L.T("1 errore", "1 error") : L.F("{0} errori", "{0} errors", failed));
                    if (stopped) msg += " " + L.T("(interrotto)", "(stopped)");
                    msg += "  ·  " + L.F("{0:0.0} s", "{0:0.0} s", sw.Elapsed.TotalSeconds);
                    status.Text = msg;
                    if (openFolder && firstOut != null) SelectInExplorer(firstOut);
                });
            });
        }

        void Ui(Action a)
        {
            try { BeginInvoke(a); } catch { }
        }

        static string ShortError(Exception ex)
        {
            if (ex is NotSupportedException || ex is FileFormatException) return L.T("formato non supportato", "unsupported format");
            if (ex is UnauthorizedAccessException) return L.T("accesso negato", "access denied");
            if (ex is OutOfMemoryException) return L.T("immagine troppo grande", "image too large");
            return ex.Message.Split('\n')[0].Trim();
        }

        // Percorso del file convertito: mai l'originale e mai lo stesso file due volte nella stessa conversione.
        static string DestPath(string src, string outDir, string ext, bool overwrite, HashSet<string> produced)
        {
            string dir = outDir ?? Path.GetDirectoryName(src);
            string name = Path.GetFileNameWithoutExtension(src);
            string dest = Path.Combine(dir, name + ext);
            Func<string, bool> taken = p =>
                string.Equals(Path.GetFullPath(p), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase) ||
                produced.Contains(p) || (!overwrite && File.Exists(p));
            for (int n = 1; taken(dest); n++) dest = Path.Combine(dir, name + " (" + n + ")" + ext);
            return dest;
        }

        static string FormatBytes(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString(b < 10 * 1024 ? "0.0" : "0") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.0") + " MB";
            return (b / 1073741824.0).ToString("0.00") + " GB";
        }

        // ===================== Aggiornamenti =====================
        // Legge l'ultima release da GitHub; propone il download solo dell'installer pubblicato nel repository.
        void CheckUpdates(bool manual)
        {
            if (Interlocked.Exchange(ref checkingUpd, 1) == 1) return;
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
                    req.UserAgent = "AppImg/" + AppVersion.Text;
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
                checkingUpd = 0;
                Ui(delegate { OnUpdateResult(tag, url, sha, manual); });
            });
        }

        void OnUpdateResult(string tag, string url, string sha, bool manual)
        {
            Version latest;
            if (tag == null || !Version.TryParse(tag, out latest))
            {
                if (manual) Msg(L.T("Impossibile controllare gli aggiornamenti. Verifica la connessione a Internet e riprova.",
                                    "Could not check for updates. Check your Internet connection and try again."), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (latest > new Version(AppVersion.Text))
            {
                updVersion = tag; updUrl = url; updSha = sha;
                mUpdAvail.Text = L.F("Aggiorna ad AppImg {0}...", "Update to AppImg {0}...", tag);
                mUpdAvail.Visible = true;
                if (manual) OfferUpdate();
                else if (!converting) status.Text = L.F("È disponibile AppImg {0}: menu ? → Aggiorna.", "AppImg {0} is available: menu ? → Update.", tag);
            }
            else if (manual)
                Msg(L.F("Hai già l'ultima versione di AppImg ({0}).", "You already have the latest version of AppImg ({0}).", AppVersion.Text),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OfferUpdate()
        {
            if (updVersion == null) return;
            string page = AppVersion.RepoUrl + "/releases/tag/v" + updVersion;
            if (updUrl == null) { OpenUrl(page); return; }
            var r = Msg(L.F("È disponibile AppImg {0} (installata: {1}).\n\nScaricare e installare ora? L'installer chiuderà AppImg durante l'aggiornamento; le impostazioni restano.\n\nNovità: {2}",
                            "AppImg {0} is available (installed: {1}).\n\nDownload and install it now? The installer will close AppImg during the update; your settings are kept.\n\nWhat's new: {2}",
                            updVersion, AppVersion.Text, page), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) DownloadUpdate(updUrl, updSha, updVersion, page);
        }

        void DownloadUpdate(string url, string sha, string ver, string page)
        {
            status.Text = L.F("Download di AppImg {0} in corso...", "Downloading AppImg {0}...", ver);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string file = Path.Combine(Path.GetTempPath(), "AppImg-Setup-" + ver + ".exe");
                string err = null;
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "AppImg/" + AppVersion.Text;
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
                Ui(delegate
                {
                    UpdateStatusIdle();
                    if (err == null)
                    {
                        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); return; }
                        catch (Exception ex) { err = ex.Message; }
                    }
                    if (Msg(L.F("Aggiornamento non riuscito: {0}\n\nAprire la pagina di download?", "Update failed: {0}\n\nOpen the download page?", err),
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        OpenUrl(page);
                });
            });
        }

        DialogResult Msg(string text, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBox.Show(this, text, "AppImg", buttons, icon);
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
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
            Text = L.T("Informazioni su AppImg", "About AppImg");
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

            var blue = Color.FromArgb(124, 58, 237);
            var pic = new PictureBox { Location = new Point(20, 20), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom };
            try
            {
                // Le immagini in appimg.ico sono PNG: si legge direttamente quella da 64 px
                using (var st = typeof(AboutForm).Assembly.GetManifestResourceStream("AppImg.appimg.ico"))
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
            var title = new Label { Text = "AppImg " + AppVersion.Text, AutoSize = true, Location = new Point(82, 18), Font = new Font("Segoe UI Semibold", 14f) };
            var desc = new Label
            {
                Text = L.T("Converte immagini tra formati diversi: JPEG, PNG, HEIC, WebP, TIFF, GIF, BMP, ICO, PDF e altri",
                           "Converts images between formats: JPEG, PNG, HEIC, WebP, TIFF, GIF, BMP, ICO, PDF and more"),
                AutoSize = true, MaximumSize = new Size(280, 0), Location = new Point(84, 50), ForeColor = Color.DimGray
            };
            var author = new Label { Text = "© 2026 " + AppVersion.Author, AutoSize = true, Location = new Point(84, 90) };

            string lic = L.T("Licenza MIT", "MIT License");
            var links = new LinkLabel
            {
                Text = lic + "  ·  GitHub", AutoSize = true, Location = new Point(84, 112),
                LinkColor = blue, ActiveLinkColor = blue, LinkBehavior = LinkBehavior.HoverUnderline
            };
            links.Links.Clear();
            links.Links.Add(0, lic.Length, AppVersion.RepoUrl + "/blob/main/LICENSE");
            links.Links.Add(lic.Length + 5, 6, AppVersion.RepoUrl);
            links.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo((string)e.Link.LinkData) { UseShellExecute = true }); } catch { }
            };

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
