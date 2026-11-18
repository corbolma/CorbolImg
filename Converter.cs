// CorbolImg - convertitore: molte immagini insieme in un altro formato.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CorbolImg
{
    class Item
    {
        public string Path;
        public ImageInfo Info;
        public bool Unsupported;
        public ListViewItem Row;
        public long Bytes;
        public long Estimate = -1;   // peso previsto con le opzioni attuali (-1 = non ancora calcolato, -2 = errore)
    }

    // Convertitore: molte immagini insieme in un altro formato, con qualità, riduzione e cartella di destinazione.
    class ConverterForm : Form
    {
        // Barra strumenti
        readonly ToolStrip tools = new ToolStrip();
        readonly ToolStripButton tAddFiles = new ToolStripButton(), tAddFolder = new ToolStripButton(), tRemove = new ToolStripButton(), tClear = new ToolStripButton();
        // Elenco e anteprima
        readonly SplitContainer split = new SplitContainer();
        readonly ListView list = new ListView();
        readonly ColumnHeader chFile = new ColumnHeader(), chFmt = new ColumnHeader(), chDim = new ColumnHeader(), chBytes = new ColumnHeader(), chEst = new ColumnHeader(), chState = new ColumnHeader();
        readonly Label dropHint = new Label();
        readonly PictureBox preview = new PictureBox();
        readonly Label previewInfo = new Label();
        readonly ContextMenuStrip listMenu = new ContextMenuStrip();
        readonly ToolStripMenuItem cmOpen = new ToolStripMenuItem(), cmFolder = new ToolStripMenuItem(), cmRemove = new ToolStripMenuItem();
        // Opzioni
        readonly Label lFormat = new Label(), lQuality = new Label(), lQualityVal = new Label(), lSize = new Label(), lPx = new Label(), lBy = new Label(), lDest = new Label(), lOptions = new Label();
        readonly ComboBox cbFormat = new ComboBox();
        readonly TrackBar tbQuality = new TrackBar();
        readonly ComboBox cbResize = new ComboBox();
        readonly CheckBox chkOverwrite = new CheckBox(), chkOpenFolder = new CheckBox();
        readonly NumericUpDown numMax = new NumericUpDown(), numH = new NumericUpDown();
        ResizeMode resizeMode = ResizeMode.None;
        int maxSide = 1920, boxW = 1920, boxH = 1080, percent = 50;   // valori ricordati per ogni modalità
        bool loadingResize, keepRatio = true;
        readonly CheckBox chkRatio = new CheckBox();
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

        public ConverterForm(IEnumerable<string> paths)
        {
            // Dimensioni scalate a mano con S(): il font in punti segue già i DPI dello schermo
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9f);
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(Settings.Get("convWidth", 940)), S(Settings.Get("convHeight", 620)));
            MinimumSize = new Size(S(700), S(480));
            if (Settings.Get("convMaximized", false)) WindowState = FormWindowState.Maximized;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildTools();
            BuildList();
            var opts = BuildOptions();
            var bottom = BuildBottomBar();

            Controls.Add(split);
            Controls.Add(opts);
            Controls.Add(bottom);
            Controls.Add(tools);

            LoadOptions();
            ApplyTexts();

            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            KeyPreview = true;
            KeyDown += OnKeyDown;
            FormClosing += delegate { cancel = true; estGen++; SaveOptions(); };
            estTimer.Tick += delegate { estTimer.Stop(); StartEstimate(); };
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
                AddPaths(paths);
            };
        }

        // Aggiunge file dal visualizzatore e porta la finestra in primo piano.
        public void AddFiles(IEnumerable<string> paths)
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            AddPaths(paths);
        }

        // ===================== Costruzione dell'interfaccia =====================

        int S(int v) { return (int)Math.Round(v * scale); }

        ToolStripMenuItem Mi(ToolStripMenuItem m, EventHandler click)
        {
            if (click != null) m.Click += click;
            return m;
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
            list.Columns.AddRange(new[] { chFile, chFmt, chDim, chBytes, chEst, chState });
            chFile.Width = S(200); chFmt.Width = S(66); chDim.Width = S(86); chBytes.Width = S(66); chEst.Width = S(76); chState.Width = S(160);
            chBytes.TextAlign = chEst.TextAlign = HorizontalAlignment.Right;
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
            previewInfo.Height = S(78);
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
            foreach (var l in new[] { lQuality, lQualityVal, lPx, lBy })
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
            cbFormat.SelectedIndexChanged += delegate { UpdateQualityState(); ScheduleEstimate(); };

            tbQuality.Minimum = 1;
            tbQuality.Maximum = 100;
            tbQuality.TickStyle = TickStyle.None;
            tbQuality.AutoSize = false;
            tbQuality.Size = new Size(S(160), S(28));
            tbQuality.Margin = new Padding(0, S(2), 0, 0);
            tbQuality.ValueChanged += delegate { lQualityVal.Text = tbQuality.Value.ToString(); ScheduleEstimate(); };

            cbResize.DropDownStyle = ComboBoxStyle.DropDownList;
            cbResize.Width = S(220);
            cbResize.Margin = new Padding(0, S(3), S(8), S(3));
            cbResize.SelectedIndexChanged += delegate { if (!loadingResize && cbResize.SelectedIndex >= 0) SetResizeMode((ResizeMode)cbResize.SelectedIndex); };
            foreach (var n in new[] { numMax, numH })
            {
                n.Minimum = 16;
                n.Maximum = 20000;
                n.Increment = 100;
                n.Width = S(80);
                n.Margin = new Padding(0, S(3), 0, S(3));
                n.ValueChanged += (s, e) => ResizeValuesChanged(s);
            }
            chkRatio.AutoSize = true;
            chkRatio.Margin = new Padding(S(14), S(6), 0, 0);
            chkRatio.CheckedChanged += delegate
            {
                if (loadingResize) return;
                keepRatio = chkRatio.Checked;
                if (keepRatio) LinkBox(false);
                ScheduleEstimate();
            };

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
            t.Controls.Add(Row(cbResize, numMax, lBy, numH, lPx, chkRatio), 1, 1);
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
            int used = chFile.Width + chFmt.Width + chDim.Width + chBytes.Width + chEst.Width;
            int free = list.ClientSize.Width - used;
            if (free > S(80)) chState.Width = free;
        }

        void PlaceHint()
        {
            dropHint.Bounds = new Rectangle(0, S(26), list.ClientSize.Width, Math.Max(0, list.ClientSize.Height - S(26)));
            dropHint.Visible = items.Count == 0;
        }

        // Tutti i testi dell'interfaccia: richiamato anche quando si cambia lingua.
        public void ApplyTexts()
        {
            Text = L.T("Converti immagini - CorbolImg", "Convert images - CorbolImg");
            tAddFiles.Text = L.T("+ Immagini", "+ Images");
            tAddFolder.Text = L.T("+ Cartella", "+ Folder");
            tRemove.Text = L.T("Rimuovi", "Remove");
            tClear.Text = L.T("Svuota elenco", "Clear list");

            chFile.Text = L.T("File", "File");
            chFmt.Text = L.T("Formato", "Format");
            chDim.Text = L.T("Dimensioni", "Dimensions");
            chBytes.Text = L.T("Peso", "Size");
            chEst.Text = L.T("Previsto", "Estimated");
            chState.Text = L.T("Stato", "Status");
            dropHint.Text = L.T("Trascina qui immagini o cartelle, incollale (Ctrl+V)\noppure clicca per sceglierle",
                                "Drag images or folders here, paste them (Ctrl+V)\nor click to choose them");
            cmOpen.Text = L.T("Apri", "Open");
            cmFolder.Text = L.T("Apri cartella", "Open folder");
            cmRemove.Text = L.T("Rimuovi dall'elenco", "Remove from list");

            lFormat.Text = L.T("Converti in:", "Convert to:");
            lQuality.Text = L.T("Qualità:", "Quality:");
            lSize.Text = L.T("Dimensioni:", "Size:");
            loadingResize = true;
            cbResize.Items.Clear();
            cbResize.Items.Add(L.T("Dimensioni originali", "Original size"));
            cbResize.Items.Add(L.T("Riduci il lato lungo a", "Shrink the long side to"));
            cbResize.Items.Add(L.T("Riduci per stare in", "Shrink to fit within"));
            cbResize.Items.Add(L.T("Riduci in percentuale al", "Shrink by percentage to"));
            cbResize.SelectedIndex = (int)resizeMode;
            loadingResize = false;
            lBy.Text = "×";
            lPx.Text = resizeMode == ResizeMode.Percent ? "%" : "px";
            lDest.Text = L.T("Salva in:", "Save to:");
            rbSame.Text = L.T("Stessa cartella dell'originale", "Same folder as the original");
            rbFolder.Text = L.T("Cartella:", "Folder:");
            btnBrowse.Text = L.T("Sfoglia...", "Browse...");
            lOptions.Text = L.T("Opzioni:", "Options:");
            chkOverwrite.Text = L.T("Sovrascrivi i file esistenti", "Overwrite existing files");
            chkOpenFolder.Text = L.T("Apri la cartella al termine", "Open the folder when done");
            tips.SetToolTip(chkOverwrite, L.T("Se non è selezionata, i nuovi file vengono rinominati, es. foto (1).png. L'originale non viene mai sovrascritto.",
                                              "If unchecked, new files are renamed, e.g. photo (1).png. The original is never overwritten."));
            tips.SetToolTip(cbResize, L.T("Le immagini più piccole non vengono ingrandite.", "Smaller images are never enlarged."));
            chkRatio.Text = L.T("Mantieni proporzioni", "Keep aspect ratio");
            tips.SetToolTip(chkRatio, L.T("Selezionata: cambiando un lato cambia anche l'altro e ogni immagine entra nel riquadro senza deformarsi.\nNon selezionata: i due lati vengono portati esattamente a larghezza e altezza, anche deformando l'immagine (mai ingrandendola).",
                                          "Checked: changing one side also changes the other, and each image fits in the box without distortion.\nUnchecked: the two sides are resized to exactly width and height, even if this distorts the image (never enlarging it)."));
            tips.SetToolTip(numMax, resizeMode == ResizeMode.Box ? L.T("Larghezza massima", "Maximum width") : null);
            tips.SetToolTip(numH, L.T("Altezza massima", "Maximum height"));
            btnConvert.Text = converting ? L.T("Annulla", "Cancel") : L.T("Converti", "Convert");
            if (!converting) UpdateStatusIdle();
            UpdateQualityState();
            ShowPreviewInfo();
            foreach (var it in items) RefreshRow(it, null);
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
            // "resize" era la casella della versione 1.0 (solo lato lungo)
            string mode = Settings.Get("resizeMode", Settings.Get("resize", false) ? "long" : "none");
            maxSide = Clamp(Settings.Get("maxSide", 1920), 16, 20000);
            boxW = Clamp(Settings.Get("maxWidth", 1920), 16, 20000);
            boxH = Clamp(Settings.Get("maxHeight", 1080), 16, 20000);
            percent = Clamp(Settings.Get("percent", 50), 1, 99);
            keepRatio = Settings.Get("keepRatio", true);
            loadingResize = true;
            chkRatio.Checked = keepRatio;
            loadingResize = false;
            SetResizeMode(mode == "long" ? ResizeMode.LongSide : mode == "box" ? ResizeMode.Box : mode == "percent" ? ResizeMode.Percent : ResizeMode.None);
            string dir = Settings.Get("outDir", "");
            txtFolder.Text = dir.Length > 0 ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CorbolImg");
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
            Settings.Set("resizeMode", resizeMode == ResizeMode.LongSide ? "long" : resizeMode == ResizeMode.Box ? "box" : resizeMode == ResizeMode.Percent ? "percent" : "none");
            Settings.Set("resize", resizeMode != ResizeMode.None);
            Settings.Set("maxSide", maxSide);
            Settings.Set("maxWidth", boxW);
            Settings.Set("maxHeight", boxH);
            Settings.Set("keepRatio", keepRatio);
            Settings.Set("percent", percent);
            Settings.Set("useOutDir", rbFolder.Checked);
            Settings.Set("outDir", txtFolder.Text.Trim());
            Settings.Set("overwrite", chkOverwrite.Checked);
            Settings.Set("openFolder", chkOpenFolder.Checked);
            Settings.Set("convMaximized", WindowState == FormWindowState.Maximized);
            var b = WindowState == FormWindowState.Normal ? ClientSize : RestoreBounds.Size - (Size - ClientSize);
            Settings.Set("convWidth", (int)(b.Width / scale));
            Settings.Set("convHeight", (int)(b.Height / scale));
            Settings.Save();
        }

        static int Clamp(int v, int min, int max) { return Math.Max(min, Math.Min(max, v)); }

        // Riduzione scelta: i campi numerici mostrano i valori della modalità attiva.
        void SetResizeMode(ResizeMode m)
        {
            loadingResize = true;
            resizeMode = m;
            if (cbResize.Items.Count > (int)m && cbResize.SelectedIndex != (int)m) cbResize.SelectedIndex = (int)m;
            bool pct = m == ResizeMode.Percent;
            // Prima si allargano i limiti, poi si mette il valore, infine si stringono
            numMax.Minimum = 1;
            numMax.Maximum = 20000;
            numMax.Value = Clamp(m == ResizeMode.Box ? boxW : pct ? percent : maxSide, pct ? 1 : 16, pct ? 99 : 20000);
            numMax.Minimum = pct ? 1 : 16;
            numMax.Maximum = pct ? 99 : 20000;
            numMax.Increment = pct ? 5 : 100;
            numH.Value = Clamp(boxH, 16, 20000);
            numMax.Visible = lPx.Visible = m != ResizeMode.None;
            numH.Visible = lBy.Visible = chkRatio.Visible = m == ResizeMode.Box;
            lPx.Text = pct ? "%" : "px";
            tips.SetToolTip(numMax, m == ResizeMode.Box ? L.T("Larghezza massima", "Maximum width") : null);
            loadingResize = false;
            ScheduleEstimate();
        }

        void ResizeValuesChanged(object sender)
        {
            if (loadingResize) return;
            int v = (int)numMax.Value;
            if (resizeMode == ResizeMode.LongSide) maxSide = v;
            else if (resizeMode == ResizeMode.Box)
            {
                boxW = v; boxH = (int)numH.Value;
                // Proporzioni bloccate: cambiando un lato cambia anche l'altro
                if (keepRatio) LinkBox(sender == numH);
            }
            else if (resizeMode == ResizeMode.Percent) percent = v;
            else return;
            ScheduleEstimate();
        }

        // Rapporto larghezza/altezza a cui legare i due campi: quello dell'immagine selezionata,
        // altrimenti della prima in elenco, altrimenti quello attuale del riquadro.
        double RefRatio()
        {
            var it = SelectedItem;
            if (it == null || it.Info == null) it = items.FirstOrDefault(i => i.Info != null && i.Info.Width > 0 && i.Info.Height > 0);
            if (it != null && it.Info != null && it.Info.Width > 0 && it.Info.Height > 0) return (double)it.Info.Width / it.Info.Height;
            return boxH > 0 ? (double)boxW / boxH : 16.0 / 9;
        }

        // Ricalcola l'altro lato del riquadro con il rapporto di riferimento.
        void LinkBox(bool fromHeight)
        {
            double r = RefRatio();
            loadingResize = true;
            if (fromHeight) { boxW = Clamp((int)Math.Round(boxH * r), 16, 20000); numMax.Value = boxW; }
            else { boxH = Clamp((int)Math.Round(boxW / r), 16, 20000); numH.Value = boxH; }
            loadingResize = false;
        }

        ResizeSpec CurrentResize
        {
            get
            {
                switch (resizeMode)
                {
                    case ResizeMode.LongSide: return new ResizeSpec { Mode = ResizeMode.LongSide, A = maxSide };
                    case ResizeMode.Box: return new ResizeSpec { Mode = ResizeMode.Box, A = boxW, B = boxH, Stretch = !keepRatio };
                    case ResizeMode.Percent: return new ResizeSpec { Mode = ResizeMode.Percent, A = percent };
                    default: return new ResizeSpec();
                }
            }
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
            else if (e.KeyCode == Keys.O && e.Control) { AddFilesDialog(); e.Handled = true; }
            else if (((e.KeyCode == Keys.V && e.Control) || (e.KeyCode == Keys.Insert && e.Shift)) && !(ActiveControl is TextBox) && !(ActiveControl is NumericUpDown))
            {
                PasteImages();
                e.Handled = true;
            }
        }

        // Ctrl+V: file copiati in Esplora file, o un'immagine copiata da un programma (salvata come PNG tra le immagini incollate)
        void PasteImages()
        {
            if (converting) return;
            List<string> paths;
            string saved;
            try { paths = ClipboardImages.Get(out saved); }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Impossibile salvare l'immagine degli appunti:", "Could not save the clipboard image:") + "\n" + ex.Message,
                                "CorbolImg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (paths.Count == 0) { System.Media.SystemSounds.Beep.Play(); return; }
            AddPaths(paths);
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
                it.Row = new ListViewItem(new[] { Path.GetFileName(p), "", "", "", "", "" }) { Tag = it, ToolTipText = p };
                try { it.Bytes = new FileInfo(p).Length; } catch { }
                it.Row.SubItems[3].Text = Util.FormatBytes(it.Bytes);
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
            ScheduleEstimate();
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
            it.Row.SubItems[4].Text = it.Unsupported ? "" : it.Estimate >= 0 ? "≈ " + Util.FormatBytes(it.Estimate) : it.Estimate == -2 ? "—" : "…";
            if (state != null) it.Row.SubItems[5].Text = state;
            else if (it.Unsupported)
            {
                it.Row.SubItems[5].Text = L.T("Formato non leggibile", "Unreadable format");
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
            ScheduleEstimate();
        }

        void ClearAll()
        {
            if (converting) return;
            lock (infoQueue) infoQueue.Clear();
            estGen++;
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
            if (it != null) Program.OpenViewer(new[] { it.Path });
        }

        void OpenSelectedFolder()
        {
            var it = SelectedItem;
            if (it != null) Util.SelectInExplorer(it.Path);
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
                var to = CurrentResize.Target(it.Info.Width, it.Info.Height);
                if (to != null) s += "\n" + L.F("Ridotta a {0} × {1} px", "Shrunk to {0} × {1} px", to.Value.Width, to.Value.Height);
                var f = SelectedFormat;
                if (it.Estimate >= 0 && f != null)
                    s += "\n" + L.F("Dopo la conversione in {0}: ≈ {1}", "After converting to {0}: ≈ {1}", f.Name, Util.FormatBytes(it.Estimate)) + Change(it.Bytes, it.Estimate);
                else if (it.Estimate == -1 && f != null) s += "\n" + L.T("Calcolo del peso previsto...", "Estimating the new size...");
            }
            else if (it.Unsupported) s += "\n" + L.T("Windows non sa leggere questo file. Per HEIC, WebP, AVIF o RAW installa l'estensione dal Microsoft Store.",
                                                     "Windows cannot read this file. For HEIC, WebP, AVIF or RAW install the extension from the Microsoft Store.");
            previewInfo.Text = s;
        }

        // ===================== Peso previsto =====================

        readonly Timer estTimer = new Timer { Interval = 300 };
        volatile int estGen;
        // Ultime immagini già lette e ridotte: muovendo il cursore della qualità si ricodificano senza rileggerle
        readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> prepared = new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>();
        readonly Queue<string> preparedOrder = new Queue<string>();

        static string Change(long before, long after)
        {
            if (before <= 0) return "";
            double pct = (after - before) * 100.0 / before;
            return "  (" + (pct >= 0 ? "+" : "−") + Math.Abs(pct).ToString(Math.Abs(pct) < 10 ? "0.#" : "0") + "%)";
        }

        // Le opzioni sono cambiate: si ricalcola dopo una breve pausa (trascinando il cursore non si ricalcola a ogni passo).
        void ScheduleEstimate()
        {
            if (converting || cbFormat.SelectedItem == null) return;
            estGen++;
            foreach (var it in items) { it.Estimate = -1; RefreshRow(it, null); }
            ShowPreviewInfo();
            UpdateStatusIdle();
            estTimer.Stop();
            estTimer.Start();
        }

        // Codifica in memoria ogni immagine con le opzioni attuali: prima quella selezionata, poi le altre in ordine.
        void StartEstimate()
        {
            var f = SelectedFormat;
            if (f == null || converting) return;
            int gen = estGen, quality = tbQuality.Value;
            var resize = CurrentResize;
            var sel = SelectedItem;
            var todo = items.Where(i => !i.Unsupported).OrderBy(i => i == sel ? 0 : 1).ToList();
            Program.RunSta(delegate
            {
                foreach (var it in todo)
                {
                    if (gen != estGen) return;
                    long size;
                    try
                    {
                        string key = it.Path + "|" + resize.Key;
                        System.Windows.Media.Imaging.BitmapSource src;
                        lock (prepared)
                            prepared.TryGetValue(key, out src);
                        if (src == null)
                        {
                            src = Imaging.Prepare(it.Path, resize);
                            lock (prepared)
                            {
                                prepared[key] = src;
                                preparedOrder.Enqueue(key);
                                while (preparedOrder.Count > 3) prepared.Remove(preparedOrder.Dequeue());
                            }
                        }
                        if (gen != estGen) return;
                        size = Imaging.EstimateSize(src, f, quality);
                    }
                    catch { size = -2; }
                    var item = it;
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (gen != estGen || !items.Contains(item)) return;
                            item.Estimate = size;
                            RefreshRow(item, null);
                            if (item == SelectedItem) ShowPreviewInfo();
                            UpdateStatusIdle();
                        }));
                    }
                    catch { return; }
                }
            });
        }

        // ===================== Conversione =====================

        void UpdateStatusIdle()
        {
            if (converting) return;
            var ok = items.Where(i => !i.Unsupported).ToList();
            int n = ok.Count;
            string text = items.Count == 0 ? L.T("Nessuna immagine in elenco.", "No images in the list.")
                        : n == 1 ? L.T("1 immagine da convertire.", "1 image to convert.")
                        : L.F("{0} immagini da convertire.", "{0} images to convert.", n);
            if (n > 0)
            {
                var done = ok.Where(i => i.Estimate >= 0).ToList();
                long now = ok.Sum(i => i.Bytes), est = done.Sum(i => i.Estimate);
                if (done.Count == n)
                    text += "  " + L.F("Peso previsto: ≈ {0} (ora {1})", "Estimated size: ≈ {0} (now {1})", Util.FormatBytes(est), Util.FormatBytes(now)) + Change(now, est);
                else if (done.Count > 0)
                {
                    // Stima parziale: proporzione sui file già calcolati
                    long partNow = done.Sum(i => i.Bytes);
                    long guess = partNow > 0 ? (long)((double)est / partNow * now) : est;
                    text += "  " + L.F("Peso previsto: ≈ {0} (ora {1}), calcolo {2} di {3}...", "Estimated size: ≈ {0} (now {1}), estimating {2} of {3}...",
                                       Util.FormatBytes(guess), Util.FormatBytes(now), done.Count, n);
                }
                else text += "  " + L.T("Calcolo del peso previsto...", "Estimating the new size...");
            }
            status.Text = text;
            btnConvert.Enabled = n > 0 || converting;
        }

        void SetBusy(bool busy)
        {
            converting = busy;
            foreach (Control c in new Control[] { cbFormat, tbQuality, cbResize, numMax, numH, chkRatio, rbSame, rbFolder, txtFolder, btnBrowse, chkOverwrite, chkOpenFolder })
                c.Enabled = !busy;
            if (!busy)
            {
                txtFolder.Enabled = btnBrowse.Enabled = rbFolder.Checked;
                UpdateQualityState();
            }
            tools.Enabled = !busy;
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
                        "CorbolImg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtFolder.Focus();
                    return;
                }
            }
            SaveOptions();
            estGen++; // la stima si ferma durante la conversione
            int quality = tbQuality.Value;
            var resize = CurrentResize;
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
                        Imaging.Convert(it.Path, dest, f, quality, resize);
                        long len = new FileInfo(dest).Length;
                        state = "✓ " + Path.GetFileName(dest) + "  (" + Util.FormatBytes(len) + ")";
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
                    if (openFolder && firstOut != null) Util.SelectInExplorer(firstOut);
                });
            });
        }

        void Ui(Action a)
        {
            try { BeginInvoke(a); } catch { }
        }

        internal static string ShortError(Exception ex)
        {
            if (ex is NotSupportedException || ex is FileFormatException) return L.T("formato non supportato", "unsupported format");
            if (ex is UnauthorizedAccessException) return L.T("accesso negato", "access denied");
            if (ex is OutOfMemoryException) return L.T("immagine troppo grande", "image too large");
            return ex.Message.Split('\n')[0].Trim();
        }

        // Percorso del file convertito: mai l'originale e mai lo stesso file due volte nella stessa conversione.
        static string DestPath(string src, string outDir, string ext, bool overwrite, HashSet<string> produced)
        {
            string dir = outDir ?? ClipboardImages.SaveDir(src); // le immagini incollate dagli appunti: in Immagini
            string name = Path.GetFileNameWithoutExtension(src);
            string dest = Path.Combine(dir, name + ext);
            Func<string, bool> taken = p =>
                string.Equals(Path.GetFullPath(p), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase) ||
                produced.Contains(p) || (!overwrite && File.Exists(p));
            for (int n = 1; taken(dest); n++) dest = Path.Combine(dir, name + " (" + n + ")" + ext);
            return dest;
        }
    }
}
