// Griglia IA: divide l'immagine come una scacchiera (colonne A, B, C... righe 1, 2, 3...).
// Ogni riquadro si può migliorare con Real-ESRGAN (ingrandimento x2/x3/x4 con ricostruzione dei dettagli),
// salvare da solo, oppure "aprire" come nuova scacchiera da suddividere di nuovo, e così via in profondità.
// L'assistente (Claude Code, Claude o un servizio compatibile OpenAI) riceve le richieste scritte
// ("migliora F3 e salvalo", "zooma sulla targa") e risponde con le azioni che AppImg esegue.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppImg
{
    class GridResult : IDisposable
    {
        public Bitmap Image;
        public int Scale;
        public EsrganModel Model;
        public void Dispose() { if (Image != null) { Image.Dispose(); Image = null; } }
    }

    // Un livello della scacchiera: l'immagine intera o un riquadro aperto
    class GridBoard : IDisposable
    {
        public Bitmap Image;
        public string Name;             // riquadro del livello superiore ("F3"); null per l'immagine intera
        public GridBoard Parent;
        public GridResult From;         // se il riquadro aperto era migliorato: scala e modello (solo informazione)
        public int Cols, Rows, SelCol, SelRow;
        public readonly Dictionary<string, GridResult> Done = new Dictionary<string, GridResult>();

        public string PathText { get { return Parent == null ? "" : (Parent.Parent == null ? "" : Parent.PathText + " › ") + Name; } }
        public string PathFile { get { return PathText.Replace(" › ", "-"); } }
        public int Depth { get { return Parent == null ? 0 : Parent.Depth + 1; } }
        public bool HasSel { get { return SelCol >= 0 && SelCol < Cols && SelRow >= 0 && SelRow < Rows; } }

        public Rectangle Cell(int c, int r)
        {
            int x0 = Image.Width * c / Cols, x1 = Image.Width * (c + 1) / Cols;
            int y0 = Image.Height * r / Rows, y1 = Image.Height * (r + 1) / Rows;
            return new Rectangle(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
        }

        public static string ColName(int i)
        {
            string s = "";
            for (i++; i > 0; i /= 26) { i--; s = (char)('A' + i % 26) + s; }
            return s;
        }

        public static string CellName(int c, int r) { return ColName(c) + (r + 1).ToString(CultureInfo.InvariantCulture); }

        public bool Parse(string s, out int c, out int r)
        {
            c = r = -1;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim().ToUpperInvariant();
            int i = 0;
            int col = 0;
            while (i < s.Length && s[i] >= 'A' && s[i] <= 'Z') { col = col * 26 + (s[i] - 'A' + 1); i++; }
            int row;
            if (i == 0 || i == s.Length || !int.TryParse(s.Substring(i), NumberStyles.None, CultureInfo.InvariantCulture, out row)) return false;
            c = col - 1; r = row - 1;
            return c < Cols && r < Rows && r >= 0;
        }

        public void ClearDone()
        {
            foreach (var d in Done.Values) d.Dispose();
            Done.Clear();
        }

        public void Dispose()
        {
            ClearDone();
            if (Image != null) { Image.Dispose(); Image = null; }
        }
    }

    // Disegno della scacchiera: usato dalla finestra e per l'immagine mandata all'assistente
    static class GridPainter
    {
        public static readonly Color Accent = Color.FromArgb(245, 158, 11);

        // Rettangolo dell'immagine dentro area, lasciando la fascia delle etichette in alto e a sinistra
        public static Rectangle Layout(Size img, Rectangle area, int band)
        {
            var inner = new Rectangle(area.X + band, area.Y + band, Math.Max(1, area.Width - band - 4), Math.Max(1, area.Height - band - 4));
            double k = Math.Min((double)inner.Width / img.Width, (double)inner.Height / img.Height);
            int w = Math.Max(1, (int)(img.Width * k)), h = Math.Max(1, (int)(img.Height * k));
            return new Rectangle(inner.X + (inner.Width - w) / 2, inner.Y + (inner.Height - h) / 2, w, h);
        }

        public static void Paint(Graphics g, GridBoard b, Image shown, Rectangle ir, Font font, Color labels, bool namesInCells)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(shown, ir);
            g.SmoothingMode = SmoothingMode.None;
            using (var dark = new Pen(Color.FromArgb(110, 0, 0, 0), 3f))
            using (var light = new Pen(Color.FromArgb(220, 255, 255, 255), 1f))
            {
                for (int c = 1; c < b.Cols; c++)
                {
                    int x = ir.X + ir.Width * c / b.Cols;
                    g.DrawLine(dark, x, ir.Top, x, ir.Bottom); g.DrawLine(light, x, ir.Top, x, ir.Bottom);
                }
                for (int r = 1; r < b.Rows; r++)
                {
                    int y = ir.Y + ir.Height * r / b.Rows;
                    g.DrawLine(dark, ir.Left, y, ir.Right, y); g.DrawLine(light, ir.Left, y, ir.Right, y);
                }
            }
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var br = new SolidBrush(labels))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                int band = font.Height + 6;
                for (int c = 0; c < b.Cols; c++)
                {
                    int x0 = ir.X + ir.Width * c / b.Cols, x1 = ir.X + ir.Width * (c + 1) / b.Cols;
                    g.DrawString(GridBoard.ColName(c), font, br, new RectangleF(x0, ir.Y - band, x1 - x0, band), sf);
                }
                for (int r = 0; r < b.Rows; r++)
                {
                    int y0 = ir.Y + ir.Height * r / b.Rows, y1 = ir.Y + ir.Height * (r + 1) / b.Rows;
                    g.DrawString((r + 1).ToString(CultureInfo.InvariantCulture), font, br, new RectangleF(ir.X - band - 2, y0, band, y1 - y0), sf);
                }
            }
            using (var badge = new SolidBrush(Accent))
            using (var pill = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
            using (var white = new SolidBrush(Color.White))
            using (var small = new Font(font.FontFamily, font.Size * 0.85f, FontStyle.Bold))
                for (int c = 0; c < b.Cols; c++)
                    for (int r = 0; r < b.Rows; r++)
                    {
                        var cr = Screen(b, ir, c, r);
                        string name = GridBoard.CellName(c, r);
                        if (namesInCells && cr.Width >= 30 && cr.Height >= 18)
                        {
                            var sz = g.MeasureString(name, small);
                            g.FillRectangle(pill, cr.X + 2, cr.Y + 2, sz.Width, sz.Height);
                            g.DrawString(name, small, white, cr.X + 2, cr.Y + 2);
                        }
                        if (b.Done.ContainsKey(name) && cr.Width >= 20)
                        {
                            string t = L.T("IA", "AI");
                            var sz = g.MeasureString(t, small);
                            g.FillRectangle(badge, cr.Right - sz.Width - 3, cr.Y + 3, sz.Width, sz.Height);
                            g.DrawString(t, small, white, cr.Right - sz.Width - 3, cr.Y + 3);
                        }
                    }
            if (b.HasSel)
                using (var p = new Pen(Accent, 3f))
                {
                    var cr = Screen(b, ir, b.SelCol, b.SelRow);
                    g.DrawRectangle(p, cr.X + 1, cr.Y + 1, cr.Width - 2, cr.Height - 2);
                }
        }

        public static Rectangle Screen(GridBoard b, Rectangle ir, int c, int r)
        {
            int x0 = ir.X + ir.Width * c / b.Cols, x1 = ir.X + ir.Width * (c + 1) / b.Cols;
            int y0 = ir.Y + ir.Height * r / b.Rows, y1 = ir.Y + ir.Height * (r + 1) / b.Rows;
            return new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }

        // Scacchiera ridotta (lato lungo maxSide) con griglia ed etichette, in JPEG: quella che vede l'assistente
        public static byte[] Jpeg(GridBoard b, int maxSide)
        {
            using (var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                int band = font.Height + 6;
                double k = Math.Min(1.0, (double)maxSide / Math.Max(b.Image.Width, b.Image.Height));
                int w = Math.Max(1, (int)(b.Image.Width * k)), h = Math.Max(1, (int)(b.Image.Height * k));
                using (var bmp = new Bitmap(w + band + 4, h + band + 4, PixelFormat.Format24bppRgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    var ir = new Rectangle(band, band, w, h);
                    Paint(g, b, b.Image, ir, font, Color.Black, true);
                    using (var ms = new MemoryStream())
                    {
                        var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
                        using (var ep = new EncoderParameters(1))
                        {
                            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                            bmp.Save(ms, enc, ep);
                        }
                        return ms.ToArray();
                    }
                }
            }
        }
    }

    class GridCanvas : Control
    {
        public GridBoard Board;
        public string Message;
        public event Action<int, int> CellClicked, CellOpened;
        Bitmap shown;           // immagine già ridotta alle dimensioni dello schermo (ridisegno veloce)
        Rectangle ir;
        int band;

        public GridCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void BoardChanged()
        {
            if (shown != null) { shown.Dispose(); shown = null; }
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { BoardChanged(); base.OnResize(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Board == null || Board.Image == null)
            {
                TextRenderer.DrawText(g, Message ?? "", Font, ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            band = Font.Height + 6;
            ir = GridPainter.Layout(Board.Image.Size, new Rectangle(8, 6, ClientSize.Width - 12, ClientSize.Height - 8), band);
            if (shown == null || shown.Size != ir.Size)
            {
                if (shown != null) shown.Dispose();
                shown = Imaging.Scale(Board.Image, ir.Size);
            }
            GridPainter.Paint(g, Board, shown, ir, Font, Color.FromArgb(90, 90, 90), false);
        }

        bool Hit(Point p, out int c, out int r)
        {
            c = r = -1;
            if (Board == null || Board.Image == null || !ir.Contains(p)) return false;
            c = Math.Min(Board.Cols - 1, (p.X - ir.X) * Board.Cols / ir.Width);
            r = Math.Min(Board.Rows - 1, (p.Y - ir.Y) * Board.Rows / ir.Height);
            return true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int c, r;
            if (e.Button == MouseButtons.Left && Hit(e.Location, out c, out r) && CellClicked != null) CellClicked(c, r);
            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int c, r;
            if (e.Button == MouseButtons.Left && Hit(e.Location, out c, out r) && CellOpened != null) CellOpened(c, r);
            base.OnMouseDoubleClick(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && shown != null) { shown.Dispose(); shown = null; }
            base.Dispose(disposing);
        }
    }

    class GridForm : Form
    {
        const string SystemPrompt =
@"You control the AI grid of AppImg, a Windows image viewer. The user sees an image divided like a chessboard: columns are letters (A, B, C... left to right), rows are numbers (1, 2, 3... top to bottom); a cell is named column+row, so A1 is the top-left cell. Each cell can be enhanced on the user's PC with Real-ESRGAN (AI super-resolution: it enlarges x2, x3 or x4 and reconstructs detail), saved on its own, or entered: entering a cell makes it the new board (its enhanced version if it has one), which can be divided again, so the user can zoom deeper and deeper.

Reply ONLY with one JSON object and nothing else:
{""say"": ""short message for the user, in the user's language"", ""actions"": [ ... ]}
The actions run in order:
{""do"":""grid"",""cols"":C,""rows"":R}  divide the current board (1-26 columns, 1-50 rows); this discards the enhanced cells of the current board
{""do"":""select"",""cell"":""F3""}
{""do"":""enhance"",""cell"":""F3"",""model"":""photo"",""scale"":4}  model: photo (real photos, default), drawing (illustrations, anime, graphics, text on flat background), fast (lighter and quicker); scale 2, 3 or 4
{""do"":""save"",""cell"":""F3"",""format"":""png""}  save the cell (its enhanced version if it has one) next to the original file; format png, jpg or webp; without ""cell"" it saves the whole current board
{""do"":""enter"",""cell"":""F3""}  zoom into the cell: it becomes the current board, with the same grid as its parent unless you send ""grid"" afterwards
{""do"":""back""}  return to the previous board
{""do"":""top""}  return to the whole image

Rules: cell names always refer to the current board at that point of the sequence. The enhanced result is limited to 80 megapixels: for big cells prefer scale 2, or enter the cell and work on smaller cells. ""Zoom with AI into F3"" means enhance F3 and then enter it. If you see the image, use it to find what the user describes (a face, a plate, a sign) and name the cells you chose in ""say"". If the request is unclear, ask in ""say"" with no actions. Do not claim results: the app reports what happened in the next message.";

        readonly string sourcePath;
        readonly int turns;
        GridBoard board;
        float scale = 1f;

        readonly ToolStrip bar = new ToolStrip();
        readonly ToolStripButton bBack = new ToolStripButton(), bTop = new ToolStripButton();
        readonly ToolStripLabel lPath = new ToolStripLabel(), lCols = new ToolStripLabel(), lRows = new ToolStripLabel();
        readonly NumericUpDown nCols = new NumericUpDown { Minimum = 1, Maximum = 26, Width = 52 }, nRows = new NumericUpDown { Minimum = 1, Maximum = 50, Width = 52 };
        readonly GridCanvas canvas = new GridCanvas();
        readonly Panel side = new Panel();
        readonly Label lCell = new Label(), lPreview = new Label(), lModel = new Label(), lScale = new Label(), lAi = new Label(), lProvider = new Label();
        readonly PictureBox preview = new PictureBox();
        readonly ComboBox cModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, cScale = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Button btEnhance = new Button(), btSave = new Button(), btEnter = new Button(), btSend = new Button();
        readonly LinkLabel lnkSettings = new LinkLabel();
        readonly RichTextBox log = new RichTextBox();
        readonly TextBox input = new TextBox();
        readonly StatusStrip status = new StatusStrip();
        readonly ToolStripStatusLabel sText = new ToolStripStatusLabel(), sStop = new ToolStripStatusLabel();
        readonly ToolStripProgressBar sProgress = new ToolStripProgressBar();

        CancellationTokenSource cts;    // operazione in corso (miglioramento, download, richiesta all'assistente)
        bool working, aiRunning;
        bool syncing;                   // aggiornamento dei campi da codice: niente eventi
        readonly List<AiMessage> history = new List<AiMessage>();
        readonly List<string> results = new List<string>();     // esito delle azioni, riferito all'assistente alla richiesta successiva
        readonly Queue<object> queue = new Queue<object>();

        public GridForm(string path, int quarterTurns)
        {
            sourcePath = path;
            turns = quarterTurns;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9f);
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(Settings.Get("gridWidth", 1240)), S(Settings.Get("gridHeight", 760)));
            MinimumSize = new Size(S(820), S(520));
            if (Settings.Get("gridMaximized", false)) WindowState = FormWindowState.Maximized;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildBar();
            BuildSide();
            status.BackColor = Color.White;
            sText.Spring = true; sText.TextAlign = ContentAlignment.MiddleLeft;
            sProgress.Visible = false; sProgress.Size = new Size(S(160), S(16));
            sStop.IsLink = true; sStop.Visible = false;
            sStop.Click += delegate { if (cts != null) cts.Cancel(); };
            status.Items.AddRange(new ToolStripItem[] { sText, sProgress, sStop });

            canvas.Dock = DockStyle.Fill;
            canvas.BackColor = Color.FromArgb(243, 243, 243);
            canvas.ForeColor = Color.FromArgb(110, 110, 110);
            canvas.CellClicked += (c, r) => SelectCell(c, r);
            canvas.CellOpened += (c, r) => { SelectCell(c, r); if (!Busy) OpenCell(c, r); };
            Controls.Add(canvas);
            Controls.Add(side);
            Controls.Add(status);
            Controls.Add(bar);

            ApplyTexts();
            FormClosing += (s, e) =>
            {
                if (cts != null) cts.Cancel();
                Settings.Set("gridMaximized", WindowState == FormWindowState.Maximized);
                var b = WindowState == FormWindowState.Normal ? ClientSize : RestoreBounds.Size - (Size - ClientSize);
                Settings.Set("gridWidth", (int)(b.Width / scale));
                Settings.Set("gridHeight", (int)(b.Height / scale));
                Settings.Save();
            };
            FormClosed += delegate
            {
                var b = board;
                while (b != null) { var p = b.Parent; b.Dispose(); b = p; }
                board = null;
                if (preview.Image != null) preview.Image.Dispose();
            };
            Load += delegate { LoadImage(); };
        }

        int S(int v) { return (int)Math.Round(v * scale); }
        bool Busy { get { return working || aiRunning; } }
        string BaseName { get { return Path.GetFileNameWithoutExtension(sourcePath); } }

        // ===================== Interfaccia =====================

        void BuildBar()
        {
            bar.GripStyle = ToolStripGripStyle.Hidden;
            bar.Padding = new Padding(S(6), S(3), S(6), S(3));
            bar.RenderMode = ToolStripRenderMode.System;
            bar.BackColor = Color.White;
            bBack.Image = Glyphs.Make(Glyphs.Prev, S(20), Color.FromArgb(50, 50, 50));
            bBack.ImageScaling = ToolStripItemImageScaling.None;
            bBack.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            bBack.Click += delegate { if (!Busy) Back(); };
            bTop.DisplayStyle = ToolStripItemDisplayStyle.Text;
            bTop.Click += delegate { if (!Busy) while (board != null && board.Parent != null) Back(); };
            lPath.Font = new Font("Segoe UI Semibold", 10f);
            nCols.ValueChanged += delegate { if (!syncing) SetGrid((int)nCols.Value, board != null ? board.Rows : 8, true); };
            nRows.ValueChanged += delegate { if (!syncing) SetGrid(board != null ? board.Cols : 8, (int)nRows.Value, true); };
            var hc = new ToolStripControlHost(nCols) { Alignment = ToolStripItemAlignment.Right, Margin = new Padding(S(2), S(2), S(10), S(2)) };
            var hr = new ToolStripControlHost(nRows) { Alignment = ToolStripItemAlignment.Right, Margin = new Padding(S(2), S(2), S(10), S(2)) };
            lCols.Alignment = lRows.Alignment = ToolStripItemAlignment.Right;
            // A destra si aggiungono da destra verso sinistra
            bar.Items.AddRange(new ToolStripItem[] { bBack, bTop, new ToolStripSeparator(), lPath, hr, lRows, hc, lCols });
        }

        void BuildSide()
        {
            side.Dock = DockStyle.Right;
            side.Width = S(360);
            side.BackColor = Color.White;
            side.Padding = new Padding(S(14), S(10), S(12), S(10));
            side.Paint += (s, e) => { using (var p = new Pen(Color.FromArgb(225, 225, 225))) e.Graphics.DrawLine(p, 0, 0, 0, side.Height); };

            lCell.Font = new Font("Segoe UI Semibold", 11f);
            lCell.AutoSize = false;
            preview.SizeMode = PictureBoxSizeMode.Zoom;
            preview.BackColor = Color.FromArgb(243, 243, 243);
            preview.Cursor = Cursors.Hand;
            preview.DoubleClick += delegate { if (board != null && board.HasSel && !Busy) OpenCell(board.SelCol, board.SelRow); };
            lPreview.ForeColor = Color.DimGray;
            foreach (var m in Esrgan.Models) cModel.Items.Add(m);
            cModel.SelectedIndex = Math.Max(0, Array.FindIndex(Esrgan.Models, m => m.Key == Settings.Get("gridModel", "photo")));
            cScale.Items.AddRange(new object[] { "×2", "×3", "×4" });
            cScale.SelectedIndex = Math.Max(0, Math.Min(2, Settings.Get("gridScale", 4) - 2));
            cModel.SelectedIndexChanged += delegate { Settings.Set("gridModel", ((EsrganModel)cModel.SelectedItem).Key); };
            cScale.SelectedIndexChanged += delegate { Settings.Set("gridScale", cScale.SelectedIndex + 2); };
            btEnhance.Click += delegate
            {
                if (working && !aiRunning) { if (cts != null) cts.Cancel(); return; }
                if (Busy || board == null || !board.HasSel) return;
                cts = new CancellationTokenSource();
                Enhance(board.SelCol, board.SelRow, (EsrganModel)cModel.SelectedItem, cScale.SelectedIndex + 2, delegate { });
            };
            btSave.Click += delegate { if (!Busy && board != null && board.HasSel) SaveDialog(board.SelCol, board.SelRow); };
            btEnter.Click += delegate { if (!Busy && board != null && board.HasSel) OpenCell(board.SelCol, board.SelRow); };

            lAi.Font = new Font("Segoe UI Semibold", 11f);
            lProvider.ForeColor = Color.DimGray;
            lProvider.AutoEllipsis = true;
            lnkSettings.LinkBehavior = LinkBehavior.HoverUnderline;
            lnkSettings.LinkColor = lnkSettings.ActiveLinkColor = Color.FromArgb(124, 58, 237);
            lnkSettings.LinkClicked += delegate { ShowAiSettings(); };
            log.ReadOnly = true;
            log.BorderStyle = BorderStyle.None;
            log.BackColor = Color.FromArgb(248, 248, 248);
            log.Font = new Font("Segoe UI", 9f);
            log.DetectUrls = false;
            input.Multiline = true;
            input.ScrollBars = ScrollBars.Vertical;
            input.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendOrStop(); }
            };
            btSend.Click += delegate { SendOrStop(); };

            side.Controls.AddRange(new Control[] { lCell, preview, lPreview, lModel, cModel, lScale, cScale, btEnhance, btSave, btEnter,
                                                   lAi, lnkSettings, lProvider, log, input, btSend });
            side.Resize += delegate { LayoutSide(); };
        }

        void LayoutSide()
        {
            int x = side.Padding.Left, w = side.ClientSize.Width - side.Padding.Horizontal, y = side.Padding.Top;
            lCell.SetBounds(x, y, w, S(24)); y += S(28);
            preview.SetBounds(x, y, w, S(190)); y += S(194);
            lPreview.SetBounds(x, y, w, S(18)); y += S(24);
            int half = (w - S(8)) / 2;
            lModel.SetBounds(x, y, half + S(40), S(18)); lScale.SetBounds(x + half + S(48), y, half - S(40), S(18)); y += S(19);
            cModel.SetBounds(x, y, half + S(40), S(24)); cScale.SetBounds(x + half + S(48), y, half - S(40), S(24)); y += S(32);
            btEnhance.SetBounds(x, y, w, S(32)); y += S(36);
            btSave.SetBounds(x, y, half, S(30)); btEnter.SetBounds(x + half + S(8), y, half, S(30)); y += S(44);
            lAi.SetBounds(x, y, w - S(100), S(24));
            lnkSettings.SetBounds(x + w - S(100), y + S(4), S(100), S(18)); lnkSettings.TextAlign = ContentAlignment.TopRight;
            y += S(24);
            lProvider.SetBounds(x, y, w, S(18)); y += S(22);
            int bottom = side.ClientSize.Height - side.Padding.Bottom;
            btSend.SetBounds(x + w - S(80), bottom - S(30), S(80), S(30));
            input.SetBounds(x, bottom - S(36) - S(58), w, S(58));
            log.SetBounds(x, y, w, Math.Max(S(40), input.Top - S(6) - y));
        }

        public void ApplyTexts()
        {
            Text = L.T("Griglia IA", "AI grid") + " - " + Path.GetFileName(sourcePath) + " - AppImg";
            bBack.Text = L.T("Indietro", "Back");
            bBack.ToolTipText = L.T("Torna alla scacchiera precedente (Backspace)", "Back to the previous grid (Backspace)");
            bTop.Text = L.T("Immagine intera", "Whole image");
            lCols.Text = L.T("Colonne", "Columns");
            lRows.Text = L.T("Righe", "Rows");
            lModel.Text = L.T("Modello", "Model");
            lScale.Text = L.T("Ingrandimento", "Enlargement");
            btSave.Text = L.T("Salva riquadro...", "Save tile...");
            btEnter.Text = L.T("Suddividi riquadro", "Subdivide tile");
            lAi.Text = L.T("Assistente", "Assistant");
            lnkSettings.Text = L.T("Impostazioni", "Settings");
            sStop.Text = L.T("Annulla", "Cancel");
            cModel.Refresh();
            UpdateUi();
        }

        void UpdateUi()
        {
            bool has = board != null && board.Image != null;
            bool sel = has && board.HasSel;
            lPath.Text = !has ? "" : board.Parent == null ? Path.GetFileName(sourcePath) + "  ·  " + board.Image.Width + "×" + board.Image.Height
                                                          : board.PathText + (board.From != null ? "  (" + L.T("IA", "AI") + " ×" + board.From.Scale + ")" : "") + "  ·  " + board.Image.Width + "×" + board.Image.Height;
            bBack.Enabled = bTop.Enabled = has && board.Parent != null && !Busy;
            nCols.Enabled = nRows.Enabled = has && !Busy;
            if (has)
            {
                syncing = true;
                nCols.Value = board.Cols; nRows.Value = board.Rows;
                syncing = false;
            }
            btEnhance.Text = working && !aiRunning ? L.T("Annulla", "Cancel") : L.T("Migliora con IA", "Enhance with AI");
            btEnhance.Enabled = sel && (!Busy || (working && !aiRunning));
            btSave.Enabled = btEnter.Enabled = sel && !Busy;
            cModel.Enabled = cScale.Enabled = !Busy;
            btSend.Text = aiRunning ? L.T("Stop", "Stop") : L.T("Invia", "Send");
            btSend.Enabled = has;
            lProvider.Text = AiSettings.Describe();

            if (preview.Image != null) { var old = preview.Image; preview.Image = null; old.Dispose(); }
            if (sel)
            {
                string name = GridBoard.CellName(board.SelCol, board.SelRow);
                var rc = board.Cell(board.SelCol, board.SelRow);
                GridResult res;
                if (board.Done.TryGetValue(name, out res))
                {
                    preview.Image = Imaging.Scale(res.Image, Fit(res.Image.Size, preview.ClientSize));
                    lPreview.Text = L.F("Migliorato con IA ×{0} ({1}) · {2}×{3} px", "Enhanced with AI ×{0} ({1}) · {2}×{3} px",
                                        res.Scale, res.Model.Name, res.Image.Width, res.Image.Height);
                }
                else
                {
                    using (var crop = board.Image.Clone(rc, PixelFormat.Format32bppArgb))
                        preview.Image = Imaging.Scale(crop, Fit(crop.Size, preview.ClientSize));
                    lPreview.Text = L.T("Originale · doppio clic per suddividere", "Original · double-click to subdivide");
                }
                lCell.Text = name + "  ·  " + rc.Width + "×" + rc.Height + " px";
            }
            else
            {
                lCell.Text = has ? L.T("Scegli un riquadro", "Pick a tile") : "";
                lPreview.Text = "";
            }
        }

        static Size Fit(Size img, Size box)
        {
            if (box.Width < 2 || box.Height < 2) box = new Size(2, 2);
            double k = Math.Min(1.0, Math.Min((double)box.Width / img.Width, (double)box.Height / img.Height));
            return new Size(Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
        }

        void SetStatus(string text, int progress = -1)
        {
            sText.Text = text ?? "";
            sProgress.Visible = progress >= 0;
            if (progress >= 0) sProgress.Value = Math.Max(0, Math.Min(100, progress));
            sStop.Visible = cts != null && (working || aiRunning);
        }

        void Log(string text, Color color, bool bold = false)
        {
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.SelectionColor = color;
            log.SelectionFont = new Font(log.Font, bold ? FontStyle.Bold : FontStyle.Regular);
            log.AppendText((log.TextLength > 0 ? "\n" : "") + text);
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }

        static readonly Color CUser = Color.FromArgb(30, 30, 30), CAi = Color.FromArgb(91, 33, 182), CAction = Color.FromArgb(110, 110, 110), CError = Color.FromArgb(185, 28, 28);

        void ShowAiSettings()
        {
            using (var f = new AiSettingsForm()) f.ShowDialog(this);
            UpdateUi();
        }

        // ===================== Caricamento e navigazione =====================

        void LoadImage()
        {
            canvas.Message = L.T("Caricamento...", "Loading...");
            canvas.Invalidate();
            working = true;
            UpdateUi();
            Program.RunSta(delegate
            {
                Bitmap bmp = null;
                string err = null;
                try
                {
                    bmp = Imaging.ToGdi(Imaging.Load(sourcePath));
                    ViewerForm.ApplyTurns(bmp, turns);
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        working = false;
                        if (bmp == null)
                        {
                            canvas.Message = L.F("Impossibile aprire {0}\n{1}", "Cannot open {0}\n{1}", Path.GetFileName(sourcePath), err);
                            canvas.Invalidate();
                            UpdateUi();
                            return;
                        }
                        int cols = Settings.Get("gridCols", 8), rows = Settings.Get("gridRows", 8);
                        board = new GridBoard { Image = bmp, Cols = Math.Max(1, Math.Min(26, cols)), Rows = Math.Max(1, Math.Min(50, rows)), SelCol = -1, SelRow = -1 };
                        ShowBoard();
                        Log(L.T("Scrivi cosa vuoi fare, per esempio: «migliora F3 e salvalo», «dividi in 4×4 e zooma su B2», «trova la targa e rendila leggibile».",
                                "Write what you want to do, for example: \"enhance F3 and save it\", \"divide into 4×4 and zoom into B2\", \"find the plate and make it readable\"."), CAction);
                    }));
                }
                catch { if (bmp != null) bmp.Dispose(); }
            });
        }

        void ShowBoard()
        {
            canvas.Board = board;
            canvas.BoardChanged();
            UpdateUi();
        }

        void SelectCell(int c, int r)
        {
            if (board == null) return;
            board.SelCol = Math.Max(0, Math.Min(board.Cols - 1, c));
            board.SelRow = Math.Max(0, Math.Min(board.Rows - 1, r));
            canvas.Invalidate();
            UpdateUi();
        }

        // Cambia la griglia della scacchiera attuale; i riquadri migliorati non corrispondono più e si perdono
        bool SetGrid(int cols, int rows, bool ask)
        {
            if (board == null) return false;
            cols = Math.Max(1, Math.Min(26, cols)); rows = Math.Max(1, Math.Min(50, rows));
            if (cols == board.Cols && rows == board.Rows) return true;
            if (ask && board.Done.Count > 0 &&
                MessageBox.Show(this, L.F("Cambiando la griglia si perdono i riquadri migliorati di questa scacchiera ({0}). Continuare?",
                                          "Changing the grid discards the enhanced tiles of this grid ({0}). Continue?", board.Done.Count),
                                "AppImg", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                UpdateUi();
                return false;
            }
            board.ClearDone();
            board.Cols = cols; board.Rows = rows;
            board.SelCol = board.SelRow = -1;
            if (board.Parent == null) { Settings.Set("gridCols", cols); Settings.Set("gridRows", rows); }
            canvas.Invalidate();
            UpdateUi();
            return true;
        }

        // Apre il riquadro come nuova scacchiera (la versione migliorata, se c'è)
        void OpenCell(int c, int r)
        {
            string name = GridBoard.CellName(c, r);
            GridResult res;
            Bitmap img = board.Done.TryGetValue(name, out res) ? new Bitmap(res.Image) : board.Image.Clone(board.Cell(c, r), PixelFormat.Format32bppArgb);
            board.SelCol = c; board.SelRow = r;
            board = new GridBoard { Image = img, Name = name, Parent = board, From = res, Cols = board.Cols, Rows = board.Rows, SelCol = -1, SelRow = -1 };
            ShowBoard();
        }

        void Back()
        {
            if (board == null || board.Parent == null) return;
            var old = board;
            board = old.Parent;
            old.Dispose();
            ShowBoard();
        }

        // ===================== Miglioramento, salvataggio =====================

        // Real-ESRGAN presente, o scaricato con il consenso dell'utente; poi then(true/false)
        void EnsureEngine(Action<bool> then)
        {
            if (Esrgan.Installed) { then(true); return; }
            var r = MessageBox.Show(this,
                L.F("Per migliorare i riquadri serve Real-ESRGAN (gratuito, licenza BSD-3-Clause): lavora sul PC con la scheda video, le immagini non escono dal computer.\n\nScaricarlo ora da GitHub? ({0}, una sola volta)",
                    "Enhancing tiles needs Real-ESRGAN (free, BSD-3-Clause license): it runs on this PC using the graphics card, images never leave the computer.\n\nDownload it now from GitHub? ({0}, only once)",
                    Util.FormatBytes(Esrgan.DownloadBytes)),
                "AppImg", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { then(false); return; }
            working = true;
            var token = cts.Token;
            SetStatus(L.T("Download di Real-ESRGAN...", "Downloading Real-ESRGAN..."), 0);
            UpdateUi();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = Esrgan.Install(p => Ui(delegate { SetStatus(L.F("Download di Real-ESRGAN... {0}%", "Downloading Real-ESRGAN... {0}%", p), p); }), token);
                Ui(delegate
                {
                    working = false;
                    SetStatus(err == null ? L.T("Real-ESRGAN installato.", "Real-ESRGAN installed.") : "");
                    UpdateUi();
                    if (err != null) MessageBox.Show(this, L.T("Download non riuscito:", "Download failed:") + "\n" + err, "AppImg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    then(err == null);
                });
            });
        }

        void Ui(Action a)
        {
            try { if (!IsDisposed) BeginInvoke(a); } catch { }
        }

        void Enhance(int c, int r, EsrganModel model, int sc, Action<bool> done)
        {
            EnsureEngine(ok =>
            {
                if (!ok || board == null) { done(false); return; }
                var b = board;
                string name = GridBoard.CellName(c, r);
                var crop = b.Image.Clone(b.Cell(c, r), PixelFormat.Format32bppArgb);
                var token = cts.Token;
                working = true;
                SelectCell(c, r);
                SetStatus(L.F("Miglioramento di {0} ×{1}...", "Enhancing {0} ×{1}...", name, sc), 0);
                UpdateUi();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string err;
                    var t0 = DateTime.Now;
                    Bitmap result;
                    using (crop)
                        result = Esrgan.Upscale(crop, model, sc,
                            p => Ui(delegate { SetStatus(L.F("Miglioramento di {0} ×{1}... {2}%", "Enhancing {0} ×{1}... {2}%", name, sc, p), p); }),
                            token, out err);
                    Ui(delegate
                    {
                        working = false;
                        if (result != null && board == b && b.Image != null)
                        {
                            GridResult old;
                            if (b.Done.TryGetValue(name, out old)) old.Dispose();
                            b.Done[name] = new GridResult { Image = result, Scale = sc, Model = model };
                            string msg = L.F("{0} migliorato ×{1}: {2}×{3} px in {4:0.0} s", "{0} enhanced ×{1}: {2}×{3} px in {4:0.0} s",
                                             name, sc, result.Width, result.Height, (DateTime.Now - t0).TotalSeconds);
                            SetStatus(msg);
                            results.Add("enhance " + name + ": done, " + result.Width + "x" + result.Height + " px");
                            canvas.Invalidate();
                            UpdateUi();
                            done(true);
                            return;
                        }
                        if (result != null) result.Dispose();
                        SetStatus("");
                        UpdateUi();
                        results.Add("enhance " + name + ": failed (" + err + ")");
                        if (err != null && !token.IsCancellationRequested)
                        {
                            if (aiRunning) Log(err, CError);
                            else MessageBox.Show(this, err, "AppImg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        done(false);
                    });
                });
            });
        }

        // Il riquadro (migliorato, se c'è) o, con c < 0, la scacchiera intera; nuova bitmap da liberare
        Bitmap Pixels(int c, int r, out string suffix)
        {
            suffix = Suffix(c, r);
            if (c < 0) return new Bitmap(board.Image);
            GridResult res;
            if (board.Done.TryGetValue(GridBoard.CellName(c, r), out res)) return new Bitmap(res.Image);
            return board.Image.Clone(board.Cell(c, r), PixelFormat.Format32bppArgb);
        }

        // Nome del file: foto_F3-B2_x4 (percorso dei riquadri e ingrandimento)
        string Suffix(int c, int r)
        {
            if (c < 0) return board.Parent == null ? "" : "_" + board.PathFile + (board.From != null ? "_x" + board.From.Scale : "");
            string name = GridBoard.CellName(c, r), path = board.PathFile;
            GridResult res;
            return "_" + (path.Length > 0 ? path + "-" : "") + name + (board.Done.TryGetValue(name, out res) ? "_x" + res.Scale : "");
        }

        void SaveDialog(int c, int r)
        {
            string suffix;
            var outs = Imaging.Outputs();
            using (var d = new SaveFileDialog
            {
                Title = L.T("Salva riquadro", "Save tile"),
                Filter = string.Join("|", outs.Select(o => o.Name + " (*" + o.Ext + ")|*" + o.Ext)),
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = Path.GetDirectoryName(sourcePath),
            })
            {
                d.FileName = BaseName + Suffix(c, r);
                int fi = outs.FindIndex(o => o.Id == Settings.Get("gridFormat", "png"));
                d.FilterIndex = (fi >= 0 ? fi : 0) + 1;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string ext = Path.GetExtension(d.FileName).ToLowerInvariant();
                if (ext == ".jpeg") ext = ".jpg";
                var f = outs.FirstOrDefault(o => o.Ext == ext) ?? outs[Math.Max(0, Math.Min(outs.Count - 1, d.FilterIndex - 1))];
                Settings.Set("gridFormat", f.Id);
                Settings.Save();
                Save(Pixels(c, r, out suffix), d.FileName, f, null);
            }
        }

        // Salvataggio automatico (dall'assistente) accanto al file originale, con un nome che non sovrascrive nulla
        void SaveAuto(int c, int r, string format, Action<bool> done)
        {
            var outs = Imaging.Outputs();
            var f = outs.FirstOrDefault(o => o.Id == (format ?? "").ToLowerInvariant().Replace("jpeg", "jpg")) ?? outs[0];
            string suffix;
            var bmp = Pixels(c, r, out suffix);
            string dir = Path.GetDirectoryName(sourcePath);
            if (!CanWrite(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string dest = Path.Combine(dir, BaseName + suffix + f.Ext);
            for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, BaseName + suffix + " (" + i + ")" + f.Ext);
            Save(bmp, dest, f, done);
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string t = Path.Combine(dir, ".appimg-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                File.WriteAllText(t, "");
                File.Delete(t);
                return true;
            }
            catch { return false; }
        }

        void Save(Bitmap bmp, string dest, OutputFormat f, Action<bool> done)
        {
            int quality = Settings.Get("quality", 90);
            working = true;
            SetStatus(L.T("Salvataggio...", "Saving..."));
            UpdateUi();
            Program.RunSta(delegate
            {
                string err = null;
                try { using (bmp) Imaging.Save(Imaging.FromGdi(bmp, 96, 96), dest, f, quality); }
                catch (Exception ex) { err = ConverterForm.ShortError(ex); }
                Ui(delegate
                {
                    working = false;
                    UpdateUi();
                    if (err == null)
                    {
                        long len = 0;
                        try { len = new FileInfo(dest).Length; } catch { }
                        SetStatus(L.F("Salvato: {0} ({1})", "Saved: {0} ({1})", dest, Util.FormatBytes(len)));
                        results.Add("save: " + Path.GetFileName(dest));
                        if (aiRunning) Log("▸ " + L.F("Salvato: {0}", "Saved: {0}", dest), CAction);
                    }
                    else
                    {
                        SetStatus("");
                        results.Add("save: failed (" + err + ")");
                        if (aiRunning) Log(L.T("Salvataggio non riuscito:", "Saving failed:") + " " + err, CError);
                        else MessageBox.Show(this, L.T("Salvataggio non riuscito:", "Saving failed:") + "\n" + err, "AppImg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    if (done != null) done(err == null);
                });
            });
        }

        // ===================== Assistente =====================

        void SendOrStop()
        {
            if (aiRunning) { if (cts != null) cts.Cancel(); return; }
            if (Busy || board == null) return;
            string text = input.Text.Trim();
            if (text.Length == 0) return;
            string missing;
            var provider = AiSettings.Create(out missing);
            if (provider == null)
            {
                MessageBox.Show(this, missing, "AppImg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ShowAiSettings();
                return;
            }
            input.Clear();
            Log(text, CUser, true);

            string state = State(text);
            byte[] jpeg = null;
            if (AiSettings.SendImage) { try { jpeg = GridPainter.Jpeg(board, 1280); } catch { } }
            var req = new AiRequest { System = SystemPrompt };
            req.Messages.AddRange(history);
            var msg = new AiMessage("user", state, jpeg);
            req.Messages.Add(msg);

            cts = new CancellationTokenSource();
            var token = cts.Token;
            aiRunning = true;
            SetStatus(L.F("{0} sta pensando...", "{0} is thinking...", provider.Name));
            UpdateUi();
            ThreadPool.QueueUserWorkItem(delegate
            {
                var res = provider.Run(req, token);
                Ui(delegate { OnAnswer(msg, res); });
            });
        }

        // Situazione attuale per l'assistente, in JSON, più la richiesta dell'utente
        string State(string request)
        {
            var b = board;
            var st = new Dictionary<string, object>
            {
                { "file", Path.GetFileName(sourcePath) },
                { "current_board", b.Parent == null ? "whole image" : b.PathText.Replace(" › ", " > ") + (b.From != null ? " (enhanced x" + b.From.Scale + ")" : "") },
                { "board_size_px", b.Image.Width + "x" + b.Image.Height },
                { "grid", b.Cols + " columns (A-" + GridBoard.ColName(b.Cols - 1) + ") x " + b.Rows + " rows (1-" + b.Rows + ")" },
                { "cell_size_px", "~" + (b.Image.Width / b.Cols) + "x" + (b.Image.Height / b.Rows) },
                { "selected", b.HasSel ? GridBoard.CellName(b.SelCol, b.SelRow) : null },
                { "enhanced_cells", b.Done.Select(kv => kv.Key + " x" + kv.Value.Scale + " " + kv.Value.Model.Key).ToArray() },
                { "image_attached", AiSettings.SendImage },
                { "user_language", L.Code }
            };
            if (results.Count > 0) st["results_of_previous_actions"] = results.ToArray();
            results.Clear();
            return "State:\n" + AiJson.Json.Serialize(st) + "\n\nUser request:\n" + request;
        }

        void OnAnswer(AiMessage msg, AiResult res)
        {
            if (res.Cancelled) { EndAi(L.T("Interrotto.", "Stopped.")); return; }
            if (!res.Ok) { Log(res.Error, CError); EndAi(""); return; }
            history.Add(new AiMessage("user", msg.Text));    // l'immagine si manda solo con l'ultima richiesta
            history.Add(new AiMessage("assistant", res.Text));
            while (history.Count > 16) history.RemoveRange(0, 2);

            var o = AiJson.ExtractObject(res.Text);
            if (o == null) { Log(res.Text.Trim(), CAi); EndAi(""); return; }
            string say = AiJson.Str(o, "say");
            if (!string.IsNullOrEmpty(say)) Log(say, CAi);
            queue.Clear();
            var acts = AiJson.Get(o, "actions") as System.Collections.IList;
            if (acts != null) foreach (var a in acts) queue.Enqueue(a);
            RunNext();
        }

        void EndAi(string statusText)
        {
            aiRunning = false;
            queue.Clear();
            SetStatus(statusText);
            UpdateUi();
            input.Focus();
        }

        void Fail(string text)
        {
            Log(text, CError);
            results.Add(text);
            queue.Clear();
            EndAi("");
        }

        bool Cell(object a, out int c, out int r, bool required)
        {
            string cell = AiJson.Str(a, "cell");
            c = r = -1;
            if (string.IsNullOrEmpty(cell))
            {
                if (!required) return true;
                if (board.HasSel) { c = board.SelCol; r = board.SelRow; return true; }
                Fail(L.T("L'assistente non ha indicato il riquadro.", "The assistant did not say which tile."));
                return false;
            }
            if (board.Parse(cell, out c, out r)) return true;
            Fail(L.F("Riquadro {0} non presente nella griglia attuale ({1}×{2}).", "Tile {0} is not in the current grid ({1}×{2}).", cell, board.Cols, board.Rows));
            return false;
        }

        void RunNext()
        {
            if (!aiRunning) return;
            if (cts.IsCancellationRequested) { EndAi(L.T("Interrotto.", "Stopped.")); return; }
            if (queue.Count == 0) { EndAi(""); return; }
            var a = queue.Dequeue();
            int c, r;
            switch ((AiJson.Str(a, "do") ?? "").ToLowerInvariant())
            {
                case "grid":
                    {
                        int cols = AiJson.Int(a, "cols", board.Cols), rows = AiJson.Int(a, "rows", board.Rows);
                        SetGrid(cols, rows, false);
                        Log("▸ " + L.F("Griglia {0}×{1}", "Grid {0}×{1}", board.Cols, board.Rows), CAction);
                        results.Add("grid: " + board.Cols + "x" + board.Rows);
                        RunNext();
                        break;
                    }
                case "select":
                    if (!Cell(a, out c, out r, true)) return;
                    SelectCell(c, r);
                    RunNext();
                    break;
                case "enhance":
                    {
                        if (!Cell(a, out c, out r, true)) return;
                        var m = Esrgan.Find(AiJson.Str(a, "model") ?? "photo");
                        int sc = Math.Max(2, Math.Min(4, AiJson.Int(a, "scale", 4)));
                        Log("▸ " + L.F("Migliora {0} ×{1} ({2})", "Enhance {0} ×{1} ({2})", GridBoard.CellName(c, r), sc, m.Name), CAction);
                        Enhance(c, r, m, sc, ok => { if (ok) RunNext(); else EndAi(""); });
                        break;
                    }
                case "save":
                    if (!Cell(a, out c, out r, false)) return;
                    SaveAuto(c, r, AiJson.Str(a, "format"), ok => { if (ok) RunNext(); else EndAi(""); });
                    break;
                case "enter":
                    if (!Cell(a, out c, out r, true)) return;
                    Log("▸ " + L.F("Suddivide {0}", "Subdivide {0}", GridBoard.CellName(c, r)), CAction);
                    OpenCell(c, r);
                    results.Add("enter: now on " + board.PathText.Replace(" › ", " > "));
                    RunNext();
                    break;
                case "back":
                    Back();
                    results.Add("back: now on " + (board.Parent == null ? "whole image" : board.PathText.Replace(" › ", " > ")));
                    RunNext();
                    break;
                case "top":
                    while (board.Parent != null) Back();
                    results.Add("top: now on whole image");
                    RunNext();
                    break;
                default:
                    results.Add("unknown action ignored: " + AiJson.Str(a, "do"));
                    RunNext();
                    break;
            }
        }

        // ===================== Tastiera =====================

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (ActiveControl is TextBox || ActiveControl is NumericUpDown || ActiveControl is ComboBox || ActiveControl is RichTextBox)
                return base.ProcessCmdKey(ref msg, keyData);
            if (board != null && !Busy)
            {
                int c = board.HasSel ? board.SelCol : 0, r = board.HasSel ? board.SelRow : 0;
                switch (keyData)
                {
                    case Keys.Left: SelectCell(board.HasSel ? c - 1 : 0, r); return true;
                    case Keys.Right: SelectCell(board.HasSel ? c + 1 : 0, r); return true;
                    case Keys.Up: SelectCell(c, board.HasSel ? r - 1 : 0); return true;
                    case Keys.Down: SelectCell(c, board.HasSel ? r + 1 : 0); return true;
                    case Keys.Enter: if (board.HasSel) OpenCell(c, r); return true;
                    case Keys.Back: Back(); return true;
                    case Keys.E: btEnhance.PerformClick(); return true;
                    case Keys.Control | Keys.S: if (board.HasSel) SaveDialog(c, r); return true;
                }
            }
            if (keyData == Keys.Escape && cts != null && Busy) { cts.Cancel(); return true; }
            if (keyData == (Keys.Control | Keys.W)) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
