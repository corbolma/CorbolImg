// AppImg - visualizzatore: apre un'immagine e scorre le altre della stessa cartella,
// con zoom, rotazione, schermo intero, presentazione, informazioni EXIF, Salva come e convertitore.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

namespace AppImg
{
    // Icone della barra: glifi del font di sistema di Windows 10/11 (Segoe Fluent Icons / Segoe MDL2 Assets).
    static class Glyphs
    {
        public const char Open = '', Prev = '', Next = '', ZoomOut = '', ZoomIn = '',
            Fit = '', Rotate = '', Delete = '', Info = '', FullScreen = '',
            SaveAs = '', Convert = '', More = '', Copy = '', Folder = '', Slideshow = '',
            Grid = '', Mic = '';

        static string font;
        static string Font
        {
            get
            {
                if (font == null)
                {
                    font = "";
                    foreach (var n in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                        using (var f = new System.Drawing.Font(n, 10f))
                            if (f.Name == n) { font = n; break; }
                }
                return font;
            }
        }

        public static Bitmap Make(char c, int px, Color color, bool mirror = false)
        {
            var b = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            if (Font.Length == 0) return b;
            using (var g = Graphics.FromImage(b))
            using (var f = new System.Drawing.Font(Font, px * 0.8f, GraphicsUnit.Pixel))
            using (var br = new SolidBrush(color))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(c.ToString(), f, br, new RectangleF(0, 1, px, px), sf);
            }
            if (mirror) b.RotateFlip(RotateFlipType.RotateNoneFlipX);
            return b;
        }
    }

    // Area dell'immagine: adatta alla finestra, zoom con la rotellina attorno al cursore, trascinamento,
    // frecce ai lati per passare all'immagine precedente/successiva.
    class ImageCanvas : Control
    {
        Image image;
        bool animated, hasAlpha;
        float zoom = 1f, offX, offY;
        bool fit = true;
        Bitmap hq;                       // copia ridotta di alta qualità per lo zoom < 100%
        readonly Timer hqTimer = new Timer { Interval = 140 };
        bool dragging;
        Point dragStart;
        float dragOffX, dragOffY;
        int hover;                       // -1 freccia sinistra, 1 destra
        TextureBrush checker;
        float scale = 1f;

        public bool NavEnabled;
        public string Message = "";
        public event Action<int> Navigate;
        public event EventHandler ViewChanged, EmptyClicked;

        public ImageCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            hqTimer.Tick += delegate { hqTimer.Stop(); BuildHq(); };
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            int c = (int)(8 * scale);
            using (var tile = new Bitmap(c * 2, c * 2))
            {
                using (var g = Graphics.FromImage(tile))
                {
                    g.Clear(Color.White);
                    using (var br = new SolidBrush(Color.FromArgb(225, 225, 225)))
                    {
                        g.FillRectangle(br, 0, 0, c, c);
                        g.FillRectangle(br, c, c, c, c);
                    }
                }
                checker = new TextureBrush(tile);
            }
        }

        public Image Image { get { return image; } }
        public float Zoom { get { return zoom; } }
        public bool IsFit { get { return fit; } }

        public void SetImage(Image img, bool anim, bool alpha)
        {
            if (animated && image != null) ImageAnimator.StopAnimate(image, OnFrame);
            image = img;
            animated = anim && img != null;
            hasAlpha = alpha;
            DropHq();
            dragging = false;
            if (animated) ImageAnimator.Animate(image, OnFrame);
            FitToWindow();
        }

        // Da richiamare quando l'immagine mostrata cambia (es. ruotata).
        public void ImageChanged()
        {
            DropHq();
            FitToWindow();
        }

        void OnFrame(object s, EventArgs e)
        {
            try { if (IsHandleCreated) BeginInvoke(new MethodInvoker(Invalidate)); } catch { }
        }

        float FitZoom()
        {
            if (image == null || ClientSize.Width < 1 || ClientSize.Height < 1) return 1f;
            float z = Math.Min((float)ClientSize.Width / image.Width, (float)ClientSize.Height / image.Height);
            return Math.Min(1f, z);
        }

        public void FitToWindow()
        {
            fit = true;
            zoom = FitZoom();
            Clamp();
            Changed();
        }

        void Clamp()
        {
            if (image == null) return;
            float w = image.Width * zoom, h = image.Height * zoom;
            offX = w <= ClientSize.Width ? (ClientSize.Width - w) / 2f : Math.Min(0, Math.Max(ClientSize.Width - w, offX));
            offY = h <= ClientSize.Height ? (ClientSize.Height - h) / 2f : Math.Min(0, Math.Max(ClientSize.Height - h, offY));
        }

        Point Middle { get { return new Point(ClientSize.Width / 2, ClientSize.Height / 2); } }

        public void ZoomTo(float z, Point? at)
        {
            if (image == null) return;
            Point p = at ?? Middle;
            z = Math.Max(Math.Min(FitZoom(), 0.05f), Math.Min(32f, z));
            float ix = (p.X - offX) / zoom, iy = (p.Y - offY) / zoom;
            zoom = z;
            fit = false;
            offX = p.X - ix * z;
            offY = p.Y - iy * z;
            Clamp();
            Changed();
        }

        public void ZoomStep(int dir, Point? at)
        {
            if (image == null) return;
            float f = FitZoom();
            float z = dir > 0 ? zoom * 1.25f : zoom / 1.25f;
            if (dir < 0 && zoom > f && z <= f * 1.001f) { FitToWindow(); return; }
            if ((zoom < 1f && z > 1f) || (zoom > 1f && z < 1f)) z = 1f; // si ferma al 100%
            ZoomTo(z, at);
        }

        void Changed()
        {
            hqTimer.Stop();
            if (image != null && zoom < 1f && !animated) hqTimer.Start();
            Invalidate();
            if (ViewChanged != null) ViewChanged(this, EventArgs.Empty);
        }

        void DropHq()
        {
            if (hq != null) { hq.Dispose(); hq = null; }
        }

        int HqW { get { return Math.Max(1, (int)Math.Round(image.Width * zoom)); } }
        int HqH { get { return Math.Max(1, (int)Math.Round(image.Height * zoom)); } }

        void BuildHq()
        {
            if (image == null || animated || zoom >= 1f) return;
            int w = HqW, h = HqH;
            if (hq != null && hq.Width == w && hq.Height == h) return;
            DropHq();
            try
            {
                hq = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(hq))
                using (var ia = new ImageAttributes())
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(image, new Rectangle(0, 0, w, h), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, ia);
                }
            }
            catch { DropHq(); }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (image == null)
            {
                using (var f = new Font("Segoe UI", 11f))
                using (var br = new SolidBrush(ForeColor))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(Message ?? "", f, br, ClientRectangle, sf);
                return;
            }
            float w = image.Width * zoom, h = image.Height * zoom;
            var dest = new RectangleF(offX, offY, w, h);
            if (hasAlpha)
            {
                var r = RectangleF.Intersect(dest, ClientRectangle);
                g.FillRectangle(checker, r);
            }
            try
            {
                if (animated)
                {
                    ImageAnimator.UpdateFrames(image);
                    g.InterpolationMode = zoom < 1f ? InterpolationMode.HighQualityBilinear : zoom >= 2f ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(image, dest);
                }
                else if (zoom < 1f)
                {
                    if (hq != null && hq.Width == HqW && hq.Height == HqH)
                        g.DrawImageUnscaled(hq, (int)Math.Round(offX), (int)Math.Round(offY));
                    else
                    {
                        // Disegno veloce durante lo zoom; la versione di qualità arriva subito dopo
                        g.InterpolationMode = InterpolationMode.Bilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                        g.DrawImage(image, dest);
                    }
                }
                else
                {
                    // Solo la parte visibile, allineata ai pixel dell'immagine
                    int x0 = Math.Max(0, (int)Math.Floor(-offX / zoom)), y0 = Math.Max(0, (int)Math.Floor(-offY / zoom));
                    int x1 = Math.Min(image.Width, (int)Math.Ceiling((ClientSize.Width - offX) / zoom));
                    int y1 = Math.Min(image.Height, (int)Math.Ceiling((ClientSize.Height - offY) / zoom));
                    if (x1 > x0 && y1 > y0)
                    {
                        g.InterpolationMode = zoom >= 2f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        using (var ia = new ImageAttributes())
                        {
                            ia.SetWrapMode(WrapMode.TileFlipXY);
                            var d = new RectangleF(offX + x0 * zoom, offY + y0 * zoom, (x1 - x0) * zoom, (y1 - y0) * zoom);
                            g.DrawImage(image, new[] { d.Location, new PointF(d.Right, d.Top), new PointF(d.Left, d.Bottom) },
                                        new RectangleF(x0, y0, x1 - x0, y1 - y0), GraphicsUnit.Pixel, ia);
                        }
                    }
                }
            }
            catch { } // immagine eliminata mentre la si disegna: si ridisegna al prossimo giro

            if (NavEnabled && hover != 0) DrawArrow(g, hover);
        }

        int ArrowSize { get { return (int)(44 * scale); } }

        void DrawArrow(Graphics g, int side)
        {
            int s = ArrowSize, m = (int)(14 * scale);
            var r = new Rectangle(side < 0 ? m : ClientSize.Width - m - s, (ClientSize.Height - s) / 2, s, s);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var br = new SolidBrush(Color.FromArgb(150, 20, 20, 20))) g.FillEllipse(br, r);
            using (var pen = new Pen(Color.White, 2.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float cx = r.X + s / 2f + (side < 0 ? -2 : 2) * scale, cy = r.Y + s / 2f, d = s * 0.16f;
                g.DrawLines(pen, new[] { new PointF(cx - side * d * 0.5f, cy - d), new PointF(cx + side * d * 0.5f, cy), new PointF(cx - side * d * 0.5f, cy + d) });
            }
        }

        int Zone(Point p)
        {
            if (!NavEnabled) return 0;
            int band = (int)(90 * scale);
            if (p.X < band) return -1;
            if (p.X > ClientSize.Width - band) return 1;
            return 0;
        }

        bool CanPan { get { return image != null && (image.Width * zoom > ClientSize.Width + 0.5f || image.Height * zoom > ClientSize.Height + 0.5f); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.XButton1) { if (Navigate != null) Navigate(-1); return; }
            if (e.Button == MouseButtons.XButton2) { if (Navigate != null) Navigate(1); return; }
            if (e.Button != MouseButtons.Left) return;
            if (image == null && Zone(e.Location) == 0) { if (EmptyClicked != null) EmptyClicked(this, EventArgs.Empty); return; }
            int z = Zone(e.Location);
            if (z != 0) { if (Navigate != null) Navigate(z); return; }
            if (CanPan)
            {
                dragging = true;
                dragStart = e.Location;
                dragOffX = offX; dragOffY = offY;
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                offX = dragOffX + (e.X - dragStart.X);
                offY = dragOffY + (e.Y - dragStart.Y);
                Clamp();
                Invalidate();
                return;
            }
            int z = Zone(e.Location);
            if (z != hover) { hover = z; Invalidate(); }
            Cursor = z != 0 ? Cursors.Hand : image == null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; Cursor = Cursors.Default; }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != 0) { hover = 0; Invalidate(); }
        }

        // Doppio clic: dimensioni reali nel punto cliccato, oppure di nuovo adatta alla finestra.
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (image == null || e.Button != MouseButtons.Left || Zone(e.Location) != 0) return;
            if (!fit) FitToWindow();
            else ZoomTo(FitZoom() >= 1f ? 2f : 1f, e.Location);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (image != null && e.Delta != 0) ZoomStep(Math.Sign(e.Delta), e.Location);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (image == null) return;
            if (fit) { zoom = FitZoom(); Clamp(); Changed(); }
            else { Clamp(); Invalidate(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (animated && image != null) ImageAnimator.StopAnimate(image, OnFrame);
                DropHq();
                hqTimer.Dispose();
                if (checker != null) checker.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Immagini decodificate in memoria: quella mostrata più le vicine, caricate in anticipo in sottofondo.
    class PictureCache : IDisposable
    {
        const int MaxSide = 12000; // oltre, l'immagine viene ridotta per lo schermo
        readonly Control ui;
        readonly Dictionary<string, Imaging.Picture> pics = new Dictionary<string, Imaging.Picture>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly object lk = new object();
        List<string> queue = new List<string>();
        List<string> order = new List<string>();
        bool running, disposed;
        public event Action<string> Loaded;

        public PictureCache(Control ui) { this.ui = ui; }

        public Imaging.Picture Get(string p) { Imaging.Picture x; return p != null && pics.TryGetValue(p, out x) ? x : null; }
        public string Error(string p) { string x; return p != null && errors.TryGetValue(p, out x) ? x : null; }

        // Le immagini da tenere, in ordine di priorità (la prima è quella mostrata). Le altre vengono liberate.
        public void Want(List<string> wanted)
        {
            order = wanted;
            foreach (var k in pics.Keys.ToList())
                if (!wanted.Contains(k, StringComparer.OrdinalIgnoreCase)) { pics[k].Dispose(); pics.Remove(k); }
            foreach (var k in errors.Keys.ToList())
                if (!wanted.Contains(k, StringComparer.OrdinalIgnoreCase)) errors.Remove(k);
            lock (lk)
            {
                queue = wanted.Where(p => !pics.ContainsKey(p) && !errors.ContainsKey(p)).ToList();
                if (running || queue.Count == 0) return;
                running = true;
            }
            Program.RunSta(Work);
        }

        public void Remove(string p)
        {
            Imaging.Picture x;
            if (pics.TryGetValue(p, out x)) { x.Dispose(); pics.Remove(p); }
            errors.Remove(p);
        }

        void Work()
        {
            while (true)
            {
                string p;
                lock (lk)
                {
                    if (queue.Count == 0 || disposed) { running = false; return; }
                    p = queue[0];
                    queue.RemoveAt(0);
                }
                Imaging.Picture pic = null;
                string err = null;
                try { pic = Imaging.Picture.Load(p, MaxSide); }
                catch (Exception ex) { err = ConverterForm.ShortError(ex); }
                try
                {
                    ui.BeginInvoke(new Action(delegate
                    {
                        // Non serve più (si è passati oltre) o è già in memoria: si libera
                        if (disposed || !order.Contains(p, StringComparer.OrdinalIgnoreCase) || pics.ContainsKey(p))
                        {
                            if (pic != null) pic.Dispose();
                            return;
                        }
                        if (pic != null) pics[p] = pic; else errors[p] = err;
                        if (Loaded != null) Loaded(p);
                    }));
                }
                catch { if (pic != null) pic.Dispose(); lock (lk) running = false; return; }
            }
        }

        public void Dispose()
        {
            disposed = true;
            lock (lk) queue.Clear();
            foreach (var p in pics.Values) p.Dispose();
            pics.Clear();
        }
    }

    class ViewerForm : Form
    {
        // Barra
        readonly ToolStrip bar = new ToolStrip();
        readonly ToolStripButton bOpen = new ToolStripButton(), bPrev = new ToolStripButton(), bNext = new ToolStripButton(),
            bZoomOut = new ToolStripButton(), bFit = new ToolStripButton(), b100 = new ToolStripButton(), bZoomIn = new ToolStripButton(),
            bRotL = new ToolStripButton(), bRotR = new ToolStripButton(), bDelete = new ToolStripButton(),
            bSaveAs = new ToolStripButton(), bInfo = new ToolStripButton(), bFull = new ToolStripButton(), bGrid = new ToolStripButton();
        readonly ToolStripSplitButton bConvert = new ToolStripSplitButton();
        readonly ToolStripDropDownButton bMore = new ToolStripDropDownButton();
        readonly ToolStripMenuItem cvThis = new ToolStripMenuItem(), cvFolder = new ToolStripMenuItem(), cvOpen = new ToolStripMenuItem();
        readonly ToolStripMenuItem mUpdAvail = new ToolStripMenuItem(), mFolder = new ToolStripMenuItem(), mCopy = new ToolStripMenuItem(),
            mSlideshow = new ToolStripMenuItem(), mDefault = new ToolStripMenuItem(), mLang = new ToolStripMenuItem(), mCheckUpd = new ToolStripMenuItem(),
            mAutoUpd = new ToolStripMenuItem(), mSite = new ToolStripMenuItem(), mAbout = new ToolStripMenuItem();
        readonly List<ToolStripMenuItem> miLang = new List<ToolStripMenuItem>();
        // Menu del tasto destro
        readonly ContextMenuStrip ctx = new ContextMenuStrip();
        readonly ToolStripMenuItem cSave = new ToolStripMenuItem(), cConvert = new ToolStripMenuItem(), cCopy = new ToolStripMenuItem(),
            cFolder = new ToolStripMenuItem(), cRotL = new ToolStripMenuItem(), cRotR = new ToolStripMenuItem(), cDelete = new ToolStripMenuItem(),
            cInfo = new ToolStripMenuItem(), cFull = new ToolStripMenuItem(), cGrid = new ToolStripMenuItem();
        // Immagine, informazioni, barra di stato
        readonly ImageCanvas canvas = new ImageCanvas();
        readonly Panel infoPanel = new Panel();
        readonly TableLayoutPanel infoTable = new TableLayoutPanel();
        readonly StatusStrip status = new StatusStrip();
        readonly ToolStripStatusLabel sName = new ToolStripStatusLabel(), sPos = new ToolStripStatusLabel(), sDims = new ToolStripStatusLabel(),
            sSize = new ToolStripStatusLabel(), sFmt = new ToolStripStatusLabel(), sZoom = new ToolStripStatusLabel();
        readonly Timer flashTimer = new Timer { Interval = 3500 }, slideTimer = new Timer { Interval = 4000 };
        readonly ToolTip tips = new ToolTip();

        readonly PictureCache cache;
        List<string> files = new List<string>();
        int index = -1;
        string folder;                    // cartella sfogliata (null se si guardano file scelti uno per uno)
        readonly Dictionary<string, int> turns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // rotazioni non salvate
        string flash;                     // messaggio temporaneo nella barra di stato
        int detailsSeq;
        bool fullScreen, slideshow;
        FormWindowState savedState;
        Rectangle savedBounds;
        float scale = 1f;

        static readonly Color CanvasBack = Color.FromArgb(243, 243, 243);

        public ViewerForm(IList<string> paths)
        {
            AutoScaleMode = AutoScaleMode.None; // dimensioni scalate a mano con S()
            Font = new Font("Segoe UI", 9f);
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(Settings.Get("viewWidth", 1100)), S(Settings.Get("viewHeight", 720)));
            MinimumSize = new Size(S(560), S(380));
            if (Settings.Get("viewMaximized", false)) WindowState = FormWindowState.Maximized;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            cache = new PictureCache(this);
            cache.Loaded += OnLoaded;

            BuildBar();
            BuildContextMenu();
            BuildInfo();
            BuildStatus();
            canvas.Dock = DockStyle.Fill;
            canvas.BackColor = CanvasBack;
            canvas.ForeColor = Color.FromArgb(110, 110, 110);
            canvas.ContextMenuStrip = ctx;
            canvas.AllowDrop = true;
            canvas.Navigate += d => Go(d);
            canvas.ViewChanged += delegate { UpdateZoomLabel(); };
            canvas.EmptyClicked += delegate { OpenDialog(); };
            canvas.DragEnter += OnDragEnter;
            canvas.DragDrop += OnDragDrop;

            Controls.Add(canvas);
            Controls.Add(infoPanel);
            Controls.Add(status);
            Controls.Add(bar);
            infoPanel.Visible = Settings.Get("info", false);
            bInfo.Checked = infoPanel.Visible;

            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            flashTimer.Tick += delegate { flashTimer.Stop(); flash = null; UpdateStatus(); };
            slideTimer.Tick += delegate { Go(1); };
            Updater.Available += OnUpdateAvailable;
            FormClosing += delegate { SaveWindow(); };
            FormClosed += delegate
            {
                Updater.Available -= OnUpdateAvailable;
                canvas.SetImage(null, false, false);
                cache.Dispose();
            };
            ApplyTexts();
            Load += delegate
            {
                OnUpdateAvailable();
                OpenPaths(paths);
                canvas.Focus();
                Updater.CheckIfDue(this);
            };
        }

        int S(int v) { return (int)Math.Round(v * scale); }

        string Current { get { return index >= 0 && index < files.Count ? files[index] : null; } }

        // ===================== Costruzione dell'interfaccia =====================

        void Glyph(ToolStripItem b, char c, bool mirror = false)
        {
            b.Image = Glyphs.Make(c, S(20), Color.FromArgb(50, 50, 50), mirror);
            b.ImageScaling = ToolStripItemImageScaling.None;
        }

        static ToolStripMenuItem Mi(ToolStripMenuItem m, EventHandler click)
        {
            if (click != null) m.Click += click;
            return m;
        }

        void BuildBar()
        {
            bar.GripStyle = ToolStripGripStyle.Hidden;
            bar.Padding = new Padding(S(6), S(3), S(6), S(3));
            bar.RenderMode = ToolStripRenderMode.System;
            bar.BackColor = Color.White;
            bar.CanOverflow = true;

            Glyph(bOpen, Glyphs.Open); bOpen.Click += delegate { OpenDialog(); };
            Glyph(bPrev, Glyphs.Prev); bPrev.Click += delegate { Go(-1); };
            Glyph(bNext, Glyphs.Next); bNext.Click += delegate { Go(1); };
            Glyph(bZoomOut, Glyphs.ZoomOut); bZoomOut.Click += delegate { canvas.ZoomStep(-1, null); };
            Glyph(bFit, Glyphs.Fit); bFit.Click += delegate { canvas.FitToWindow(); };
            b100.Text = "1:1"; b100.DisplayStyle = ToolStripItemDisplayStyle.Text; b100.Font = new Font("Segoe UI Semibold", 9.5f);
            b100.Click += delegate { canvas.ZoomTo(1f, null); };
            Glyph(bZoomIn, Glyphs.ZoomIn); bZoomIn.Click += delegate { canvas.ZoomStep(1, null); };
            Glyph(bRotL, Glyphs.Rotate, true); bRotL.Click += delegate { Rotate(-1); };
            Glyph(bRotR, Glyphs.Rotate); bRotR.Click += delegate { Rotate(1); };
            Glyph(bDelete, Glyphs.Delete); bDelete.Click += delegate { DeleteCurrent(); };
            foreach (var b in new ToolStripItem[] { bOpen, bPrev, bNext, bZoomOut, bFit, bZoomIn, bRotL, bRotR, bDelete })
                b.DisplayStyle = ToolStripItemDisplayStyle.Image;
            foreach (var b in new ToolStripItem[] { bOpen, bPrev, bNext, bZoomOut, bFit, b100, bZoomIn, bRotL, bRotR, bDelete, bSaveAs, bConvert, bInfo, bFull, bMore, bGrid })
                b.Padding = new Padding(S(4), S(2), S(4), S(2));

            // A destra (aggiunti da destra verso sinistra)
            Glyph(bMore, Glyphs.More); bMore.DisplayStyle = ToolStripItemDisplayStyle.Image; bMore.ShowDropDownArrow = false;
            Glyph(bFull, Glyphs.FullScreen); bFull.DisplayStyle = ToolStripItemDisplayStyle.Image; bFull.Click += delegate { ToggleFullScreen(); };
            Glyph(bInfo, Glyphs.Info); bInfo.DisplayStyle = ToolStripItemDisplayStyle.Image; bInfo.Click += delegate { ToggleInfo(); };
            Glyph(bConvert, Glyphs.Convert); bConvert.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            bConvert.ButtonClick += delegate { ConvertThis(); };
            bConvert.DropDownItems.AddRange(new ToolStripItem[]
            {
                Mi(cvThis, delegate { ConvertThis(); }),
                Mi(cvFolder, delegate { ConvertAll(); }),
                new ToolStripSeparator(),
                Mi(cvOpen, delegate { Program.OpenConverter(null); })
            });
            Glyph(bSaveAs, Glyphs.SaveAs); bSaveAs.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText; bSaveAs.Click += delegate { SaveAs(); };
            Glyph(bGrid, Glyphs.Grid); bGrid.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText; bGrid.Click += delegate { OpenGrid(); };
            foreach (var b in new ToolStripItem[] { bMore, bFull, bInfo, bConvert, bSaveAs, bGrid })
                b.Alignment = ToolStripItemAlignment.Right;

            // Menu "Altro"
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
                if (v) Updater.Check(this, false);
            };
            mUpdAvail.Visible = false;
            mUpdAvail.Font = new Font(mUpdAvail.Font, FontStyle.Bold);
            Glyph(mFolder, Glyphs.Folder); Glyph(mCopy, Glyphs.Copy); Glyph(mSlideshow, Glyphs.Slideshow);
            mCopy.ShortcutKeyDisplayString = "Ctrl+C";
            mSlideshow.ShortcutKeyDisplayString = "F5";
            bMore.DropDownItems.AddRange(new ToolStripItem[]
            {
                Mi(mUpdAvail, delegate { Updater.Offer(this); }),
                Mi(mFolder, delegate { if (Current != null) Util.SelectInExplorer(Current); }),
                Mi(mCopy, delegate { CopyCurrent(); }),
                Mi(mSlideshow, delegate { StartSlideshow(); }),
                new ToolStripSeparator(),
                Mi(mDefault, delegate { SetAsDefault(); }),
                mLang,
                new ToolStripSeparator(),
                Mi(mCheckUpd, delegate { Updater.Check(this, true); }),
                mAutoUpd,
                Mi(mSite, delegate { Util.OpenUrl(AppVersion.RepoUrl); }),
                Mi(mAbout, delegate { AboutForm.ShowOnce(Icon); })
            });
            bMore.DropDownOpening += delegate
            {
                mAutoUpd.Checked = Settings.Get("autoUpdate", false);
                foreach (var m in miLang) m.Checked = (string)m.Tag == L.Setting;
                mFolder.Enabled = mCopy.Enabled = Current != null;
                mSlideshow.Enabled = files.Count > 1;
            };

            bar.Items.AddRange(new ToolStripItem[]
            {
                bOpen, new ToolStripSeparator(), bPrev, bNext, new ToolStripSeparator(),
                bZoomOut, bFit, b100, bZoomIn, new ToolStripSeparator(), bRotL, bRotR, new ToolStripSeparator(), bDelete,
                bMore, bFull, bInfo, new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right }, bConvert, bSaveAs, bGrid
            });
        }

        void BuildContextMenu()
        {
            Glyph(cSave, Glyphs.SaveAs); Glyph(cConvert, Glyphs.Convert); Glyph(cCopy, Glyphs.Copy); Glyph(cFolder, Glyphs.Folder);
            Glyph(cRotL, Glyphs.Rotate, true); Glyph(cRotR, Glyphs.Rotate); Glyph(cDelete, Glyphs.Delete); Glyph(cInfo, Glyphs.Info); Glyph(cFull, Glyphs.FullScreen); Glyph(cGrid, Glyphs.Grid);
            cGrid.ShortcutKeyDisplayString = "G";
            cSave.ShortcutKeyDisplayString = "Ctrl+S";
            cConvert.ShortcutKeyDisplayString = "Ctrl+E";
            cCopy.ShortcutKeyDisplayString = "Ctrl+C";
            cRotL.ShortcutKeyDisplayString = "Shift+R";
            cRotR.ShortcutKeyDisplayString = "R";
            cDelete.ShortcutKeyDisplayString = "Canc";
            cInfo.ShortcutKeyDisplayString = "I";
            cFull.ShortcutKeyDisplayString = "F11";
            ctx.Items.AddRange(new ToolStripItem[]
            {
                Mi(cSave, delegate { SaveAs(); }),
                Mi(cConvert, delegate { ConvertThis(); }),
                Mi(cGrid, delegate { OpenGrid(); }),
                Mi(cCopy, delegate { CopyCurrent(); }),
                Mi(cFolder, delegate { if (Current != null) Util.SelectInExplorer(Current); }),
                new ToolStripSeparator(),
                Mi(cRotL, delegate { Rotate(-1); }),
                Mi(cRotR, delegate { Rotate(1); }),
                new ToolStripSeparator(),
                Mi(cDelete, delegate { DeleteCurrent(); }),
                new ToolStripSeparator(),
                Mi(cInfo, delegate { ToggleInfo(); }),
                Mi(cFull, delegate { ToggleFullScreen(); })
            });
            ctx.Opening += (s, e) =>
            {
                bool has = Current != null;
                foreach (var m in new[] { cSave, cConvert, cCopy, cFolder, cRotL, cRotR, cDelete, cGrid }) m.Enabled = has;
                cInfo.Checked = infoPanel.Visible;
                cFull.Checked = fullScreen;
            };
        }

        void BuildInfo()
        {
            infoPanel.Dock = DockStyle.Right;
            infoPanel.Width = S(270);
            infoPanel.BackColor = Color.White;
            infoPanel.Padding = new Padding(S(16), S(12), S(12), S(12));
            infoPanel.AutoScroll = true;
            infoTable.Dock = DockStyle.Top;
            infoTable.AutoSize = true;
            infoTable.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            infoTable.ColumnCount = 1;
            infoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            infoPanel.Controls.Add(infoTable);
            // Linea di separazione a sinistra
            infoPanel.Paint += (s, e) => { using (var p = new Pen(Color.FromArgb(225, 225, 225))) e.Graphics.DrawLine(p, 0, 0, 0, infoPanel.Height); };
        }

        void BuildStatus()
        {
            status.SizingGrip = true;
            status.BackColor = Color.White;
            sName.Spring = true;
            sName.TextAlign = ContentAlignment.MiddleLeft;
            foreach (var l in new[] { sPos, sDims, sSize, sFmt, sZoom })
            {
                l.BorderSides = ToolStripStatusLabelBorderSides.Left;
                l.BorderStyle = Border3DStyle.Etched;
                l.Padding = new Padding(S(6), 0, S(6), 0);
            }
            sZoom.IsLink = true;
            sZoom.LinkBehavior = LinkBehavior.HoverUnderline;
            sZoom.LinkColor = sZoom.ActiveLinkColor = Color.FromArgb(50, 50, 50);
            sZoom.Click += delegate { if (canvas.IsFit) canvas.ZoomTo(1f, null); else canvas.FitToWindow(); };
            status.Items.AddRange(new ToolStripItem[] { sName, sPos, sDims, sSize, sFmt, sZoom });
        }

        public void ApplyTexts()
        {
            bOpen.ToolTipText = L.T("Apri (Ctrl+O)", "Open (Ctrl+O)");
            bPrev.ToolTipText = L.T("Immagine precedente (←)", "Previous image (←)");
            bNext.ToolTipText = L.T("Immagine successiva (→)", "Next image (→)");
            bZoomOut.ToolTipText = L.T("Riduci (-)", "Zoom out (-)");
            bFit.ToolTipText = L.T("Adatta alla finestra (0)", "Fit to window (0)");
            b100.ToolTipText = L.T("Dimensioni reali (1)", "Actual size (1)");
            bZoomIn.ToolTipText = L.T("Ingrandisci (+)", "Zoom in (+)");
            bRotL.ToolTipText = L.T("Ruota a sinistra (Shift+R)", "Rotate left (Shift+R)");
            bRotR.ToolTipText = L.T("Ruota a destra (R)", "Rotate right (R)");
            bDelete.ToolTipText = L.T("Sposta nel Cestino (Canc)", "Move to Recycle Bin (Del)");
            bSaveAs.Text = L.T("Salva come", "Save as");
            bSaveAs.ToolTipText = L.T("Salva in un altro formato (Ctrl+S)", "Save in another format (Ctrl+S)");
            bConvert.Text = L.T("Converti", "Convert");
            bConvert.ToolTipText = L.T("Converti più immagini insieme (Ctrl+E)", "Convert several images at once (Ctrl+E)");
            cvThis.Text = L.T("Converti questa immagine...", "Convert this image...");
            cvFolder.Text = files.Count > 1 && folder != null ? L.F("Converti tutte le immagini della cartella ({0})...", "Convert all images in the folder ({0})...", files.Count)
                                                             : L.T("Converti tutte le immagini aperte...", "Convert all open images...");
            cvOpen.Text = L.T("Apri il convertitore", "Open the converter");
            bGrid.Text = L.T("Griglia IA", "AI grid");
            bGrid.ToolTipText = L.T("Dividi l'immagine a scacchiera e migliora i riquadri con l'IA (G)", "Divide the image into a grid and enhance tiles with AI (G)");
            bInfo.ToolTipText = L.T("Informazioni sull'immagine (I)", "Image information (I)");
            bFull.ToolTipText = L.T("Schermo intero (F11)", "Full screen (F11)");
            bMore.ToolTipText = L.T("Altro", "More");

            if (Updater.Version != null) mUpdAvail.Text = L.F("Aggiorna ad AppImg {0}...", "Update to AppImg {0}...", Updater.Version);
            mFolder.Text = L.T("Mostra in Esplora file", "Show in File Explorer");
            mCopy.Text = L.T("Copia", "Copy");
            mSlideshow.Text = L.T("Presentazione", "Slideshow");
            mDefault.Text = L.T("Imposta come visualizzatore predefinito...", "Set as default image viewer...");
            mLang.Text = "Lingua / Language";
            miLang[0].Text = L.T("Automatica (lingua di Windows)", "Automatic (Windows language)");
            mCheckUpd.Text = L.T("Controlla aggiornamenti", "Check for updates");
            mAutoUpd.Text = L.T("Controlla automaticamente (una volta al giorno)", "Check automatically (once a day)");
            mSite.Text = L.T("Pagina del progetto (GitHub)", "Project page (GitHub)");
            mAbout.Text = L.T("Informazioni su AppImg", "About AppImg");

            cSave.Text = L.T("Salva come...", "Save as...");
            cConvert.Text = L.T("Converti...", "Convert...");
            cCopy.Text = L.T("Copia", "Copy");
            cFolder.Text = L.T("Mostra in Esplora file", "Show in File Explorer");
            cRotL.Text = L.T("Ruota a sinistra", "Rotate left");
            cRotR.Text = L.T("Ruota a destra", "Rotate right");
            cDelete.Text = L.T("Sposta nel Cestino", "Move to Recycle Bin");
            cGrid.Text = L.T("Griglia IA...", "AI grid...");
            cInfo.Text = L.T("Informazioni", "Information");
            cFull.Text = L.T("Schermo intero", "Full screen");
            tips.SetToolTip(canvas, null);
            ShowCurrent();
        }

        void SetLang(string code)
        {
            L.Set(code);
            Settings.Set("lang", L.Setting);
            Settings.Save();
            Program.LanguageChanged();
        }

        void SaveWindow()
        {
            if (fullScreen) ToggleFullScreen();
            Settings.Set("viewMaximized", WindowState == FormWindowState.Maximized);
            var b = WindowState == FormWindowState.Normal ? ClientSize : RestoreBounds.Size - (Size - ClientSize);
            Settings.Set("viewWidth", (int)(b.Width / scale));
            Settings.Set("viewHeight", (int)(b.Height / scale));
            Settings.Set("info", infoPanel.Visible);
            Settings.Save();
        }

        void OnUpdateAvailable()
        {
            if (Updater.Version == null) return;
            mUpdAvail.Text = L.F("Aggiorna ad AppImg {0}...", "Update to AppImg {0}...", Updater.Version);
            mUpdAvail.Visible = true;
            ShowFlash(L.F("È disponibile AppImg {0}: menu ⋯ → Aggiorna.", "AppImg {0} is available: menu ⋯ → Update.", Updater.Version));
        }

        // ===================== Apertura e navigazione =====================

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null) BeginInvoke(new Action(delegate { OpenPaths(paths); Activate(); }));
        }

        void OpenDialog()
        {
            var exts = Imaging.InputExtensions();
            using (var d = new OpenFileDialog
            {
                Multiselect = true,
                Title = L.T("Apri immagini", "Open images"),
                Filter = L.T("Immagini", "Images") + "|" + string.Join(";", exts.Select(x => "*" + x)) + "|" + L.T("Tutti i file", "All files") + " (*.*)|*.*"
            })
            {
                string dir = folder ?? Settings.Get("lastInDir", "");
                if (Directory.Exists(dir)) d.InitialDirectory = dir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Settings.Set("lastInDir", Path.GetDirectoryName(d.FileNames[0]));
                OpenPaths(d.FileNames);
            }
        }

        // Un file: si sfoglia la sua cartella. Una cartella: le sue immagini. Più file: solo quelli.
        void OpenPaths(IList<string> raw)
        {
            var paths = new List<string>();
            foreach (var r in raw ?? new string[0])
                try { paths.Add(Path.GetFullPath(r.Trim('"'))); } catch { }
            paths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (paths.Count == 0) { if (files.Count == 0) ShowCurrent(); return; }

            foreach (var p in files) cache.Remove(p);
            turns.Clear();
            if (paths.Count == 1)
            {
                string p = paths[0];
                if (Directory.Exists(p)) LoadFolder(p, null);
                else LoadFolder(Path.GetDirectoryName(p), p);
            }
            else
            {
                folder = null;
                files = new List<string>();
                foreach (var p in paths)
                {
                    if (Directory.Exists(p)) files.AddRange(FolderImages(p));
                    else files.Add(p);
                }
                files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                index = files.Count > 0 ? 0 : -1;
            }
            ApplyTexts();
        }

        static List<string> FolderImages(string dir)
        {
            try
            {
                var l = Directory.GetFiles(dir).Where(f => Imaging.IsImageExt(f) && (File.GetAttributes(f) & FileAttributes.Hidden) == 0).ToList();
                l.Sort((a, b) => Util.CompareNames(Path.GetFileName(a), Path.GetFileName(b)));
                return l;
            }
            catch { return new List<string>(); }
        }

        void LoadFolder(string dir, string select)
        {
            folder = dir;
            files = FolderImages(dir);
            if (select != null && !files.Contains(select, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(select);
                files.Sort((a, b) => Util.CompareNames(Path.GetFileName(a), Path.GetFileName(b)));
            }
            index = select != null ? files.FindIndex(f => string.Equals(f, select, StringComparison.OrdinalIgnoreCase)) : files.Count > 0 ? 0 : -1;
        }

        void Go(int delta)
        {
            if (files.Count < 2) return;
            index = ((index + delta) % files.Count + files.Count) % files.Count;
            ShowCurrent();
        }

        void GoTo(int i)
        {
            if (files.Count == 0) return;
            index = Math.Max(0, Math.Min(files.Count - 1, i));
            ShowCurrent();
        }

        void ShowCurrent()
        {
            string p = Current;
            canvas.NavEnabled = files.Count > 1;
            if (p == null)
            {
                canvas.SetImage(null, false, false);
                canvas.Message = folder != null ? L.T("Nessuna immagine in questa cartella.", "No images in this folder.")
                                                : L.T("Apri un'immagine (Ctrl+O) o trascinala qui", "Open an image (Ctrl+O) or drag it here");
                canvas.Invalidate();
                Text = "AppImg";
                cache.Want(new List<string>());
                UpdateUi();
                return;
            }
            var pic = cache.Get(p);
            if (pic != null)
            {
                if (canvas.Image != pic.Image) canvas.SetImage(pic.Image, pic.Animated, pic.HasAlpha);
            }
            else
            {
                canvas.SetImage(null, false, false);
                string err = cache.Error(p);
                canvas.Message = err != null ? L.F("Impossibile aprire {0}\n{1}", "Cannot open {0}\n{1}", Path.GetFileName(p), err) +
                                               "\n\n" + L.T("Per HEIC, WebP, AVIF o RAW installa l'estensione dal Microsoft Store.",
                                                            "For HEIC, WebP, AVIF or RAW install the extension from the Microsoft Store.")
                                             : L.T("Caricamento...", "Loading...");
                canvas.Invalidate();
            }
            Text = Path.GetFileName(p) + " - AppImg";

            // In memoria: questa, la successiva, la precedente e quella dopo ancora
            var order = new List<string> { p };
            foreach (int d in new[] { 1, -1, 2 })
            {
                if (files.Count <= 1) break;
                string q = files[((index + d) % files.Count + files.Count) % files.Count];
                if (!order.Contains(q, StringComparer.OrdinalIgnoreCase)) order.Add(q);
            }
            cache.Want(order);
            UpdateUi();
            if (infoPanel.Visible) LoadDetails();
        }

        void OnLoaded(string p)
        {
            int t;
            var pic = cache.Get(p);
            if (pic != null && !pic.Animated && turns.TryGetValue(p, out t) && t != 0) ApplyTurns(pic.Image, t);
            if (string.Equals(p, Current, StringComparison.OrdinalIgnoreCase)) ShowCurrent();
        }

        // ===================== Barra di stato e pannello informazioni =====================

        void UpdateUi()
        {
            bool has = Current != null;
            var pic = cache.Get(Current);
            bPrev.Enabled = bNext.Enabled = files.Count > 1;
            bZoomOut.Enabled = bFit.Enabled = b100.Enabled = bZoomIn.Enabled = pic != null;
            bRotL.Enabled = bRotR.Enabled = pic != null && !pic.Animated;
            bDelete.Enabled = bSaveAs.Enabled = has;
            bConvert.Enabled = true;
            cvThis.Enabled = has;
            cvFolder.Enabled = files.Count > 0;
            UpdateStatus();
        }

        void ShowFlash(string text)
        {
            flash = text;
            flashTimer.Stop();
            flashTimer.Start();
            UpdateStatus();
        }

        // Dimensioni dell'immagine mostrata (con la rotazione non salvata)
        void Dims(Imaging.Picture pic, out int w, out int h)
        {
            w = pic.Info.Width; h = pic.Info.Height;
            int t;
            if (turns.TryGetValue(Current, out t) && t % 2 != 0) { int x = w; w = h; h = x; }
        }

        void UpdateStatus()
        {
            string p = Current;
            var pic = cache.Get(p);
            sName.Text = flash ?? (p != null ? Path.GetFileName(p) : "");
            sPos.Text = p != null ? (index + 1) + " / " + files.Count : "";
            if (pic != null)
            {
                int w, h;
                Dims(pic, out w, out h);
                sDims.Text = w + " × " + h;
                sSize.Text = Util.FormatBytes(pic.Bytes);
                sFmt.Text = pic.Info.Format + (pic.Animated ? " " + L.T("animata", "animated") : "");
            }
            else sDims.Text = sSize.Text = sFmt.Text = "";
            foreach (var l in new[] { sPos, sDims, sSize, sFmt }) l.Visible = l.Text.Length > 0;
            UpdateZoomLabel();
        }

        void UpdateZoomLabel()
        {
            var pic = cache.Get(Current);
            if (pic == null || canvas.Image == null) { sZoom.Visible = false; return; }
            int w, h;
            Dims(pic, out w, out h);
            // Lo zoom si riferisce all'immagine originale (quella mostrata può essere ridotta se enorme)
            double z = canvas.Zoom * canvas.Image.Width / Math.Max(1, w);
            sZoom.Text = (z * 100).ToString(z < 0.1 ? "0.#" : "0", CultureInfo.CurrentCulture) + "%";
            sZoom.ToolTipText = canvas.IsFit ? L.T("Clic: dimensioni reali", "Click: actual size") : L.T("Clic: adatta alla finestra", "Click: fit to window");
            sZoom.Visible = true;
        }

        void ToggleInfo()
        {
            infoPanel.Visible = !infoPanel.Visible;
            bInfo.Checked = infoPanel.Visible;
            if (infoPanel.Visible) LoadDetails();
        }

        void LoadDetails()
        {
            string p = Current;
            int seq = ++detailsSeq;
            FillInfo(p, null);
            if (p == null) return;
            Program.RunSta(delegate
            {
                List<KeyValuePair<string, string>> d = null;
                try { d = Imaging.Details(p); } catch { }
                try { BeginInvoke(new Action(delegate { if (seq == detailsSeq) FillInfo(p, d); })); } catch { }
            });
        }

        static string DetailLabel(string key)
        {
            switch (key)
            {
                case "Date taken": return L.T("Data dello scatto", "Date taken");
                case "Camera": return L.T("Fotocamera", "Camera");
                case "Exposure": return L.T("Esposizione", "Exposure");
                case "Lens": return L.T("Obiettivo", "Lens");
                case "Author": return L.T("Autore", "Author");
                case "Title": return L.T("Titolo", "Title");
                case "Location": return L.T("Posizione", "Location");
                default: return key;
            }
        }

        void FillInfo(string p, List<KeyValuePair<string, string>> details)
        {
            infoTable.SuspendLayout();
            foreach (Control c in infoTable.Controls.Cast<Control>().ToList()) c.Dispose();
            infoTable.Controls.Clear();
            infoTable.RowStyles.Clear();
            int width = infoPanel.ClientSize.Width - infoPanel.Padding.Horizontal - S(4);
            Action<string> heading = t => infoTable.Controls.Add(new Label
            {
                Text = t, AutoSize = true, MaximumSize = new Size(width, 0), Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 0, 0, S(6))
            });
            Action<string, string, string> row = (k, v, link) =>
            {
                infoTable.Controls.Add(new Label { Text = k, AutoSize = true, ForeColor = Color.Gray, Font = new Font("Segoe UI", 8.25f), Margin = new Padding(0, S(8), 0, 0) });
                if (link == null)
                    infoTable.Controls.Add(new Label { Text = v, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, S(1), 0, 0) });
                else
                {
                    var ll = new LinkLabel { Text = v, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, S(1), 0, 0), LinkBehavior = LinkBehavior.HoverUnderline };
                    ll.LinkClicked += delegate { if (link.StartsWith("http")) Util.OpenUrl(link); else Util.SelectInExplorer(link); };
                    infoTable.Controls.Add(ll);
                }
            };
            if (p == null)
                heading(L.T("Nessuna immagine", "No image"));
            else
            {
                heading(Path.GetFileName(p));
                row(L.T("Cartella", "Folder"), Path.GetDirectoryName(p), p);
                var pic = cache.Get(p);
                if (pic != null)
                {
                    int w, h;
                    Dims(pic, out w, out h);
                    row(L.T("Formato", "Format"), pic.Info.Format + (pic.Info.Frames > 1 ? "  ·  " + L.F("{0} fotogrammi/pagine", "{0} frames/pages", pic.Info.Frames) : ""), null);
                    row(L.T("Dimensioni", "Dimensions"), w + " × " + h + " px  ·  " + ((double)w * h / 1e6).ToString("0.0", CultureInfo.CurrentCulture) + " MP", null);
                    row(L.T("Peso", "File size"), Util.FormatBytes(pic.Bytes), null);
                }
                try { row(L.T("Modificato", "Modified"), File.GetLastWriteTime(p).ToString("g", CultureInfo.CurrentCulture), null); } catch { }
                if (details != null)
                    foreach (var kv in details)
                    {
                        string link = null;
                        if (kv.Key == "Location")
                        {
                            var ll = kv.Value.Split(',');
                            link = "https://www.openstreetmap.org/?mlat=" + ll[0].Trim() + "&mlon=" + ll[1].Trim() + "#map=15/" + ll[0].Trim() + "/" + ll[1].Trim();
                        }
                        row(DetailLabel(kv.Key), kv.Value, link);
                    }
            }
            infoTable.ResumeLayout();
        }

        // ===================== Azioni =====================

        internal static void ApplyTurns(Image img, int t)
        {
            t = ((t % 4) + 4) % 4;
            if (t == 1) img.RotateFlip(RotateFlipType.Rotate90FlipNone);
            else if (t == 2) img.RotateFlip(RotateFlipType.Rotate180FlipNone);
            else if (t == 3) img.RotateFlip(RotateFlipType.Rotate270FlipNone);
        }

        // Ruota la visualizzazione; con Salva come la rotazione viene applicata al file salvato.
        void Rotate(int dir)
        {
            var pic = cache.Get(Current);
            if (pic == null || pic.Animated) return;
            int t;
            turns.TryGetValue(Current, out t);
            t = ((t + dir) % 4 + 4) % 4;
            turns[Current] = t;
            ApplyTurns(pic.Image, dir);
            canvas.ImageChanged();
            UpdateStatus();
            if (infoPanel.Visible) LoadDetails();
            if (t != 0) ShowFlash(L.T("Rotazione non ancora salvata: usa Salva come (Ctrl+S).", "Rotation not saved yet: use Save as (Ctrl+S)."));
        }

        void DeleteCurrent()
        {
            string p = Current;
            if (p == null) return;
            if (MessageBox.Show(this, L.F("Spostare «{0}» nel Cestino?", "Move \"{0}\" to the Recycle Bin?", Path.GetFileName(p)), "AppImg",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes) return;
            canvas.SetImage(null, false, false);
            cache.Remove(p);
            try { FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
            catch (OperationCanceledException) { ShowCurrent(); return; }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Impossibile eliminare il file:", "Cannot delete the file:") + "\n" + ex.Message, "AppImg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ShowCurrent();
                return;
            }
            turns.Remove(p);
            files.RemoveAt(index);
            if (index >= files.Count) index = files.Count - 1;
            ShowCurrent();
            ShowFlash(L.F("«{0}» spostato nel Cestino.", "\"{0}\" moved to the Recycle Bin.", Path.GetFileName(p)));
        }

        // Copia sia l'immagine (per incollarla in un programma) sia il file (per incollarlo in Esplora file).
        void CopyCurrent()
        {
            string p = Current;
            if (p == null) return;
            var d = new DataObject();
            var pic = cache.Get(p);
            if (pic != null) d.SetImage(new Bitmap(pic.Image));
            d.SetFileDropList(new StringCollection { p });
            try
            {
                Clipboard.SetDataObject(d, true);
                ShowFlash(L.T("Immagine copiata negli appunti.", "Image copied to the clipboard."));
            }
            catch { }
        }

        // Griglia IA sull'immagine mostrata (con la rotazione non ancora salvata)
        void OpenGrid()
        {
            string p = Current;
            if (p == null) return;
            var pic = cache.Get(p);
            if (pic != null && pic.Animated) { ShowFlash(L.T("La griglia IA non funziona con le GIF animate.", "The AI grid does not work with animated GIFs.")); return; }
            int t;
            turns.TryGetValue(p, out t);
            Program.OpenGrid(p, t);
        }

        void ConvertThis()
        {
            if (Current != null) Program.OpenConverter(new[] { Current });
            else Program.OpenConverter(null);
        }

        void ConvertAll()
        {
            if (files.Count > 0) Program.OpenConverter(files.ToList());
        }

        // Salva l'immagine (con la rotazione scelta) in un formato a scelta.
        void SaveAs()
        {
            string p = Current;
            if (p == null) return;
            var outs = Imaging.Outputs();
            int t;
            turns.TryGetValue(p, out t);
            int quality = Settings.Get("quality", 90);
            using (var d = new SaveFileDialog
            {
                Title = L.T("Salva come", "Save as"),
                Filter = string.Join("|", outs.Select(o => o.Name + " (*" + o.Ext + ")|*" + o.Ext)),
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = Path.GetDirectoryName(p),
                FileName = Path.GetFileNameWithoutExtension(p)
            })
            {
                int fi = outs.FindIndex(o => o.Id == Settings.Get("saveFormat", "png"));
                d.FilterIndex = (fi >= 0 ? fi : 0) + 1;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                // Il formato segue l'estensione scritta (es. foto.jpg), altrimenti il tipo scelto nell'elenco
                string ext = Path.GetExtension(d.FileName).ToLowerInvariant();
                if (ext == ".jpeg" || ext == ".jpe" || ext == ".jfif") ext = ".jpg";
                if (ext == ".tiff") ext = ".tif";
                if (ext == ".wdp" || ext == ".hdp") ext = ".jxr";
                if (ext == ".heif") ext = ".heic";
                var f = outs.FirstOrDefault(o => o.Ext == ext) ?? outs[Math.Max(0, Math.Min(outs.Count - 1, d.FilterIndex - 1))];
                string dest = d.FileName;
                Settings.Set("saveFormat", f.Id);
                Settings.Save();
                ShowFlash(L.T("Salvataggio...", "Saving..."));
                Program.RunSta(delegate
                {
                    string err = null;
                    try { Imaging.Convert(p, dest, f, quality, 0, t); }
                    catch (Exception ex) { err = ConverterForm.ShortError(ex); }
                    try { BeginInvoke(new Action(delegate { AfterSave(p, dest, f, quality, err); })); } catch { }
                });
            }
        }

        void AfterSave(string src, string dest, OutputFormat f, int quality, string err)
        {
            if (err != null)
            {
                flash = null;
                UpdateStatus();
                MessageBox.Show(this, L.T("Salvataggio non riuscito:", "Saving failed:") + "\n" + err, "AppImg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            long len = 0;
            try { len = new FileInfo(dest).Length; } catch { }
            string msg = L.F("Salvato: {0} ({1})", "Saved: {0} ({1})", Path.GetFileName(dest), Util.FormatBytes(len));
            if (f.Quality) msg += "  ·  " + L.F("qualità {0}, si cambia nel convertitore", "quality {0}, change it in the converter", quality);
            if (string.Equals(dest, src, StringComparison.OrdinalIgnoreCase))
            {
                // Sovrascritto l'originale: si ricarica (la rotazione ora è nel file)
                turns.Remove(src);
                cache.Remove(src);
                canvas.SetImage(null, false, false);
            }
            else if (folder != null && string.Equals(Path.GetDirectoryName(dest), folder, StringComparison.OrdinalIgnoreCase) &&
                     Imaging.IsImageExt(dest) && !files.Contains(dest, StringComparer.OrdinalIgnoreCase))
            {
                // Nuovo file nella cartella sfogliata: entra nell'elenco senza cambiare l'immagine mostrata
                files.Add(dest);
                files.Sort((a, b) => Util.CompareNames(Path.GetFileName(a), Path.GetFileName(b)));
                index = files.FindIndex(x => string.Equals(x, src, StringComparison.OrdinalIgnoreCase));
            }
            ShowCurrent();
            ShowFlash(msg);
        }

        // Windows non permette di cambiare l'app predefinita da programma: si apre la pagina delle Impostazioni.
        void SetAsDefault()
        {
            MessageBox.Show(this, L.T("Si aprirà Impostazioni → App predefinite.\n\nScegli AppImg e impostalo per i tipi di immagine che vuoi aprire con un doppio clic (JPG, PNG, HEIC...).\n\nIn alternativa: tasto destro su un'immagine → Apri con → Scegli un'altra app → AppImg → Sempre.",
                                      "Settings → Default apps will open.\n\nChoose AppImg and set it for the image types you want to open with a double-click (JPG, PNG, HEIC...).\n\nAlternatively: right-click an image → Open with → Choose another app → AppImg → Always."),
                "AppImg", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { System.Diagnostics.Process.Start("ms-settings:defaultapps?registeredAppUser=AppImg"); }
            catch { Util.OpenUrl("ms-settings:defaultapps"); }
        }

        // ===================== Schermo intero e presentazione =====================

        void ToggleFullScreen()
        {
            if (!fullScreen)
            {
                fullScreen = true;
                savedState = WindowState;
                savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                bar.Visible = status.Visible = false;
                infoPanel.Tag = infoPanel.Visible;
                infoPanel.Visible = false;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                Bounds = Screen.FromControl(this).Bounds;
                canvas.BackColor = Color.Black;
                canvas.ForeColor = Color.Gray;
            }
            else
            {
                fullScreen = false;
                StopSlideshow();
                FormBorderStyle = FormBorderStyle.Sizable;
                Bounds = savedBounds;
                WindowState = savedState;
                bar.Visible = status.Visible = true;
                infoPanel.Visible = infoPanel.Tag is bool && (bool)infoPanel.Tag;
                canvas.BackColor = CanvasBack;
                canvas.ForeColor = Color.FromArgb(110, 110, 110);
            }
            canvas.Focus();
        }

        void StartSlideshow()
        {
            if (files.Count < 2) return;
            if (!fullScreen) ToggleFullScreen();
            slideshow = true;
            slideTimer.Start();
            Cursor.Hide();
        }

        void StopSlideshow()
        {
            if (!slideshow) return;
            slideshow = false;
            slideTimer.Stop();
            Cursor.Show();
        }

        // ===================== Tastiera =====================

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (slideshow && keyData != Keys.Right && keyData != Keys.Left && keyData != Keys.Space)
            {
                StopSlideshow();
                if (keyData == Keys.Escape) { ToggleFullScreen(); return true; }
            }
            switch (keyData)
            {
                case Keys.Left: case Keys.PageUp: case Keys.Back: Go(-1); return true;
                case Keys.Right: case Keys.PageDown: case Keys.Space: Go(1); return true;
                case Keys.Home: GoTo(0); return true;
                case Keys.End: GoTo(files.Count - 1); return true;
                case Keys.Add: case Keys.Oemplus: case Keys.Control | Keys.Add: case Keys.Control | Keys.Oemplus: canvas.ZoomStep(1, null); return true;
                case Keys.Subtract: case Keys.OemMinus: case Keys.Control | Keys.Subtract: case Keys.Control | Keys.OemMinus: canvas.ZoomStep(-1, null); return true;
                case Keys.D0: case Keys.NumPad0: case Keys.Control | Keys.D0: canvas.FitToWindow(); return true;
                case Keys.D1: case Keys.NumPad1: case Keys.Control | Keys.D1: canvas.ZoomTo(1f, null); return true;
                case Keys.R: Rotate(1); return true;
                case Keys.Shift | Keys.R: case Keys.L: Rotate(-1); return true;
                case Keys.Delete: DeleteCurrent(); return true;
                case Keys.F11: case Keys.F: ToggleFullScreen(); return true;
                case Keys.Escape: if (fullScreen) { ToggleFullScreen(); return true; } break;
                case Keys.F5: if (slideshow) StopSlideshow(); else StartSlideshow(); return true;
                case Keys.I: case Keys.Alt | Keys.Enter: ToggleInfo(); return true;
                case Keys.Control | Keys.O: OpenDialog(); return true;
                case Keys.Control | Keys.S: case Keys.Control | Keys.Shift | Keys.S: SaveAs(); return true;
                case Keys.Control | Keys.C: CopyCurrent(); return true;
                case Keys.Control | Keys.E: ConvertThis(); return true;
                case Keys.G: OpenGrid(); return true;
                case Keys.Control | Keys.W: Close(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
