// AppImg - convertitore: molte immagini insieme in un altro formato.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AppImg
{
    class Item
    {
        public string Path;
        public ImageInfo Info;
        public bool Unsupported;
        public ListViewItem Row;
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
        public void ApplyTexts()
        {
            Text = L.T("Converti immagini - AppImg", "Convert images - AppImg");
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
            Settings.Set("convMaximized", WindowState == FormWindowState.Maximized);
            var b = WindowState == FormWindowState.Normal ? ClientSize : RestoreBounds.Size - (Size - ClientSize);
            Settings.Set("convWidth", (int)(b.Width / scale));
            Settings.Set("convHeight", (int)(b.Height / scale));
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
            else if (e.KeyCode == Keys.O && e.Control) { AddFilesDialog(); e.Handled = true; }
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
                it.Row.SubItems[3].Text = Util.FormatBytes(len);
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
            string dir = outDir ?? Path.GetDirectoryName(src);
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
