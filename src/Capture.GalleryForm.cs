// SysDeck — «Захват»: окно галереи снимков и записей.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  Окно
    // ------------------------------------------------------------------ //
    internal sealed class GalleryForm : Form
    {
        private const int WM_DPICHANGED = 0x02E0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private readonly GalleryTabs _tabs = new GalleryTabs();
        private readonly GalleryGrid _grid = new GalleryGrid();
        private readonly FlowLayoutPanel _bar = new FlowLayoutPanel();
        private readonly Label _status = new Label();
        private readonly List<Button> _needSelection = new List<Button>();
        private readonly System.Windows.Forms.Timer _rescan = new System.Windows.Forms.Timer();
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private Button _edit;
        private Font _barFont;
        private CapSettings _settings;
        private List<GalleryItem> _all = new List<GalleryItem>();
        private string _reveal;
        private float _scale = 1f;
        private bool _scanning;

        // Запрос «открыть в редакторе»: окно редактора заводит агент, у него же живут настройки и мониторы.
        public event Action<string, MonitorInfo> EditRequested;

        public GalleryForm(CapSettings settings)
        {
            _settings = settings.Clone();
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            MonitorInfo mon = CurrentMonitor();
            if (mon != null) work = mon.WorkArea;
            _scale = Math.Max(1f, CapDpi.ScaleAt(new Point(work.X + work.Width / 2, work.Y + work.Height / 2)));

            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = EditorColors.Back;
            ForeColor = EditorColors.Text;
            KeyPreview = true;
            ShowInTaskbar = true;
            Text = Tr.S("Галерея — снимки и видео", "Gallery — screenshots and videos");
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            _bar.Dock = DockStyle.Top;
            _bar.BackColor = EditorColors.Back;
            _bar.WrapContents = false;
            _bar.AutoSize = false;

            _status.Dock = DockStyle.Bottom;
            _status.BackColor = EditorColors.Back;
            _status.ForeColor = EditorColors.Dim;
            _status.TextAlign = ContentAlignment.MiddleLeft;

            _grid.Dock = DockStyle.Fill;
            _tabs.Dock = DockStyle.Top;

            BuildBar();

            Controls.Add(_grid);
            Controls.Add(_status);
            Controls.Add(_bar);
            Controls.Add(_tabs);

            _tabs.Changed += delegate { ApplyFilter(); };
            _grid.SelectionChanged += delegate { UpdateStatus(); };
            _grid.Activated += Open;
            _grid.CopyRequested += delegate { Copy(); };
            _grid.DeleteRequested += delegate { Delete(); };

            _rescan.Interval = 900;
            _rescan.Tick += delegate { _rescan.Stop(); Reload(); };

            ApplyScale(_scale);
            Place(work);
            _tabs.Kind = (GalleryKind)Math.Max(0, Math.Min(2, _settings.GalleryTab));
        }

        private static MonitorInfo CurrentMonitor()
        {
            try { return ScreenGrab.MonitorAt(ScreenGrab.Monitors(), CapNative.CursorPosition()); }
            catch { return null; }
        }

        private int S(float v) { return (int)Math.Round(v * _scale); }

        private Button Mk(string text, int width, bool needsSelection, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = EditorColors.Line;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 56, 56);
            b.BackColor = Color.FromArgb(44, 44, 44);
            b.ForeColor = EditorColors.Text;
            b.AutoSize = false;
            b.Tag = width;                                   // ширина в логических точках, масштабируется в ApplyScale
            b.Click += click;
            _bar.Controls.Add(b);
            if (needsSelection) _needSelection.Add(b);
            return b;
        }

        private void BuildBar()
        {
            Mk(Tr.S("Открыть", "Open"), 100, true, delegate { Open(First()); });
            _edit = Mk(Tr.S("Редактировать", "Edit"), 140, true, delegate { Edit(); });
            Mk(Tr.S("Копировать", "Copy"), 120, true, delegate { Copy(); });
            Mk(Tr.S("Показать в папке", "Show in folder"), 160, true, delegate { ShowInFolder(); });
            Mk(Tr.S("Удалить", "Delete"), 110, true, delegate { Delete(); });
            Mk(Tr.S("Папка", "Folder"), 100, false, delegate { OpenFolder(); });
            Mk(Tr.S("Обновить", "Refresh"), 110, false, delegate { Reload(); });
        }

        private void ApplyScale(float scale)
        {
            _scale = scale;
            MinimumSize = new Size(S(560), S(420));
            _tabs.ApplyScale(scale);
            _grid.ApplyScale(scale);
            _bar.Height = S(46);
            _bar.Padding = new Padding(S(10), S(8), S(10), S(8));
            _status.Height = S(28);
            _status.Padding = new Padding(S(14), 0, S(14), 0);
            Font old = _barFont;
            _barFont = new Font("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point);
            _status.Font = _barFont;
            foreach (Control c in _bar.Controls)
            {
                Button b = c as Button;
                if (b == null) continue;
                b.Font = _barFont;
                b.Height = S(30);
                b.Margin = new Padding(0, 0, S(6), 0);
                b.Width = S((int)b.Tag);
            }
            if (old != null) old.Dispose();
        }

        private void Place(Rectangle work)
        {
            Rectangle saved = _settings.GalleryBounds;
            if (!saved.IsEmpty && OnAnyScreen(saved))
            {
                Bounds = saved;
                return;
            }
            int w = Math.Min((int)(work.Width * 0.86), S(1180));
            int h = Math.Min((int)(work.Height * 0.86), S(780));
            w = Math.Max(w, MinimumSize.Width);
            h = Math.Max(h, MinimumSize.Height);
            Bounds = new Rectangle(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h);
        }

        // Сохранённое положение с отключённого монитора вернуло бы окно за край экрана.
        private static bool OnAnyScreen(Rectangle r)
        {
            foreach (Screen s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(r)) return true;
            return false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                if (Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4) != 0)
                    Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, 4);
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartWatchers();
            Reload();
            _grid.Focus();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                RECT r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                float scale = Math.Max(1f, ((int)m.WParam & 0xFFFF) / 96f);
                ApplyScale(scale);
                Bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                PerformLayout();
                Invalidate(true);
                return;
            }
            base.WndProc(ref m);
        }

        // ---- данные ----

        public void Reload()
        {
            if (_scanning) return;
            _scanning = true;
            CapSettings settings = CapSettings.Load();
            _settings.ShotFolder = settings.ShotFolder;
            _settings.VideoFolder = settings.VideoFolder;
            List<string> picked = new List<string>();
            foreach (GalleryItem it in _grid.Selected()) picked.Add(it.Path);
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<GalleryItem> found;
                try { found = GalleryLibrary.Read(settings); }
                catch (Exception ex) { CapLog.Report(ex); found = new List<GalleryItem>(); }
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _scanning = false;
                        if (IsDisposed) return;
                        Dictionary<string, bool> keep = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        foreach (string path in picked) keep[path] = true;
                        foreach (GalleryItem it in found) it.Selected = keep.ContainsKey(it.Path);
                        _all = found;
                        ApplyFilter();
                    }));
                }
                catch (Exception ex) { CapLog.Report(ex); _scanning = false; }
            });
        }

        private void ApplyFilter()
        {
            int videos = GalleryLibrary.CountVideos(_all);
            _tabs.SetCounts(_all.Count, _all.Count - videos, videos);
            _grid.SetItems(GalleryLibrary.Filter(_all, _tabs.Kind));
            if (_reveal != null)
            {
                _grid.SelectPath(_reveal);
                _reveal = null;
            }
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            List<GalleryItem> picked = _grid.Selected();
            long bytes = 0;
            foreach (GalleryItem it in picked) bytes += it.Size;
            string text = _grid.Count.ToString(CultureInfo.InvariantCulture) + Tr.S(" файлов", " files");
            if (picked.Count > 0)
                text += Tr.S("  ·  выбрано ", "  ·  selected ") + picked.Count.ToString(CultureInfo.InvariantCulture)
                        + "  ·  " + GalleryGrid.Bytes(bytes);
            _status.Text = text;
            bool any = picked.Count > 0;
            foreach (Button b in _needSelection) b.Enabled = any;
            bool image = false;
            foreach (GalleryItem it in picked) if (!it.Video) { image = true; break; }
            _edit.Enabled = image;
        }

        private void StartWatchers()
        {
            Watch(_settings.EffectiveShotFolder);
            string video = _settings.EffectiveVideoFolder;
            if (!string.Equals(video, _settings.EffectiveShotFolder, StringComparison.OrdinalIgnoreCase)) Watch(video);
        }

        private void Watch(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                FileSystemWatcher w = new FileSystemWatcher(dir);
                w.IncludeSubdirectories = true;
                w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                w.SynchronizingObject = this;
                FileSystemEventHandler changed = delegate { _rescan.Stop(); _rescan.Start(); };
                w.Created += changed;
                w.Deleted += changed;
                w.Changed += changed;
                w.Renamed += delegate { _rescan.Stop(); _rescan.Start(); };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        // ---- действия ----

        private GalleryItem First()
        {
            List<GalleryItem> picked = _grid.Selected();
            return picked.Count > 0 ? picked[0] : null;
        }

        private void Open(GalleryItem it)
        {
            if (it == null) return;
            if (!File.Exists(it.Path)) { Reload(); return; }
            ToastHost.OpenFile(it.Path);
        }

        private void Edit()
        {
            foreach (GalleryItem it in _grid.Selected())
            {
                if (it.Video || !File.Exists(it.Path)) continue;
                if (EditRequested != null) EditRequested(it.Path, CurrentMonitor());
                return;
            }
        }

        // Кроме списка файлов — сама картинка, если выбрана одна: так её берут чаты и редакторы.
        private void Copy()
        {
            List<GalleryItem> picked = _grid.Selected();
            if (picked.Count == 0) return;
            StringCollection files = new StringCollection();
            foreach (GalleryItem it in picked) if (File.Exists(it.Path)) files.Add(it.Path);
            if (files.Count == 0) { Reload(); return; }
            try
            {
                DataObject data = new DataObject();
                data.SetFileDropList(files);
                if (picked.Count == 1 && !picked[0].Video)
                {
                    using (Bitmap bmp = ImageStore.LoadUnlocked(picked[0].Path))
                    {
                        data.SetData(DataFormats.Bitmap, true, new Bitmap(bmp));
                        using (MemoryStream png = new MemoryStream())
                        {
                            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                            data.SetData("PNG", false, new MemoryStream(png.ToArray()));
                        }
                    }
                }
                Clipboard.SetDataObject(data, true, 5, 60);
                Flash(Tr.S("Скопировано: ", "Copied: ") + files.Count.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                CapLog.Report(ex);
                Flash(Tr.S("Не удалось скопировать: ", "Could not copy: ") + ex.Message);
            }
        }

        private void ShowInFolder()
        {
            GalleryItem it = First();
            if (it == null) return;
            if (!File.Exists(it.Path)) { Reload(); return; }
            ToastHost.ShowInFolder(it.Path);
        }

        private void OpenFolder()
        {
            GalleryItem it = First();
            ToastHost.OpenFolder(it != null
                ? AgentApp.GalleryFolder(_settings, it.Path)
                : _tabs.Kind == GalleryKind.Videos ? _settings.EffectiveVideoFolder : _settings.EffectiveShotFolder);
        }

        // Удаление — только в Корзину: снимок, удалённый мимо неё, вернуть нечем.
        private void Delete()
        {
            List<GalleryItem> picked = _grid.Selected();
            if (picked.Count == 0) return;
            string question = picked.Count == 1
                ? Tr.S("Переместить в Корзину «", "Move to the Recycle Bin: “") + picked[0].Name + "»?"
                : Tr.S("Переместить в Корзину выбранные файлы (", "Move the selected files to the Recycle Bin (")
                  + picked.Count.ToString(CultureInfo.InvariantCulture) + ")?";
            if (MessageBox.Show(this, question, Tr.S("Галерея", "Gallery"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            List<string> paths = new List<string>();
            foreach (GalleryItem it in picked) paths.Add(it.Path);
            bool aborted;
            int tooLong;
            int rc = Native.RecycleFiles(Handle, paths, out aborted, out tooLong);
            if (rc != 0) Flash(Tr.S("Не всё удалось удалить (код ", "Some files could not be deleted (code ") + rc.ToString(CultureInfo.InvariantCulture) + ")");
            else if (aborted) Flash(Tr.S("Удаление прервано", "Deletion cancelled"));
            else if (tooLong > 0) Flash(Tr.S("Слишком длинные пути: ", "Paths too long: ") + tooLong.ToString(CultureInfo.InvariantCulture));
            Reload();
        }

        private void Flash(string text)
        {
            _status.Text = text;
            System.Windows.Forms.Timer back = new System.Windows.Forms.Timer();
            back.Interval = 3000;
            back.Tick += delegate
            {
                back.Stop();
                back.Dispose();
                if (!IsDisposed) UpdateStatus();
            };
            back.Start();
        }

        // Список читается с диска в фоне, поэтому файл, который просили показать, выделяется после первой загрузки.
        public void Reveal(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _reveal = path;
            _tabs.Kind = GalleryKind.All;
            _grid.SelectPath(path);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SavePlace();
            base.OnFormClosing(e);
        }

        private void SavePlace()
        {
            try
            {
                Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                CapSettings onDisk = CapSettings.Load();
                onDisk.GalleryBounds = bounds;
                onDisk.GalleryTab = (int)_tabs.Kind;
                onDisk.Save();
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (FileSystemWatcher w in _watchers)
                {
                    try { w.EnableRaisingEvents = false; w.Dispose(); }
                    catch { }
                }
                _watchers.Clear();
                _rescan.Dispose();
                if (_barFont != null) { _barFont.Dispose(); _barFont = null; }
            }
            base.Dispose(disposing);
        }
    }
}
