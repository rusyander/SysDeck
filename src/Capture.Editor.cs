// Windows Process Cleaner — «Захват»: окно редактора снимка — панель инструментов, холст, сохранение.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Окно живёт в потоке интерфейса фонового процесса захвата (PMv2): WinForms 4.x сам не масштабирует, поэтому размеры
// считаются вручную от DPI монитора и пересчитываются по WM_DPICHANGED. Модель правок — Capture.EditorModel.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsProcessCleaner.Capture
{
    internal enum EditorCommand { RotateLeft, RotateRight, Undo, Redo, Copy, Save, SaveAs, Folder }

    internal enum StyleField { Color, Width, Fill, FontSize }

    // Текущие цвет, толщина, заливка и размер текста — для новых фигур и для выделенной.
    internal sealed class EditorStyle
    {
        public Color Color = Color.FromArgb(229, 57, 53);
        public float Width = 4f;
        public float FontSize = 28f;
        public bool Fill;
        public EditTool Tool = EditTool.Arrow;

        public static EditorStyle From(CapSettings s)
        {
            EditorStyle st = new EditorStyle();
            Color c;
            if (HexColor.TryParse(s.EditorColor, out c)) st.Color = c;
            st.Width = s.EditorWidth;
            st.FontSize = s.EditorFontSize;
            st.Fill = s.EditorFill;
            st.Tool = s.EditorTool;
            return st;
        }

        public void CopyTo(CapSettings s)
        {
            s.EditorColor = HexColor.Format(Color);
            s.EditorWidth = (int)Math.Round(Width);
            s.EditorFontSize = (int)Math.Round(FontSize);
            s.EditorFill = Fill;
            s.EditorTool = Tool;
        }
    }

    internal static class EditorColors
    {
        public static readonly Color Back = Color.FromArgb(32, 32, 32);
        public static readonly Color Canvas = Color.FromArgb(22, 22, 22);
        public static readonly Color Text = Color.FromArgb(228, 228, 228);
        public static readonly Color Dim = Color.FromArgb(140, 140, 140);
        public static readonly Color Line = Color.FromArgb(58, 58, 58);
        public static readonly Color Accent = Color.FromArgb(59, 130, 246);
    }

    // ------------------------------------------------------------------ //
    //  Окно
    // ------------------------------------------------------------------ //
    internal sealed class EditorForm : Form
    {
        private const int WM_DPICHANGED = 0x02E0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private readonly EditorDoc _doc;
        private readonly EditorStyle _style;
        private readonly EditorBar _bar;
        private readonly EditorCanvas _canvas;
        private readonly EditorStatus _status;
        private readonly CapSettings _settings;
        private readonly string _app;
        private readonly System.Windows.Forms.Timer _messageTimer = new System.Windows.Forms.Timer();
        private string _path;
        private bool _overwriteAsked, _forceClose;
        private int _copiedVersion = -1;
        private float _scale;

        // Для живой проверки: окно появляется, не забирая фокус у того, с чем сейчас работают.
        internal bool OpenInactive { get; set; }

        // image переходит во владение окна; path — файл снимка на диске или null, если снимок ещё не сохранялся.
        public EditorForm(Bitmap image, string app, string path, MonitorInfo mon, CapSettings settings)
            : this(new EditorDoc(image), app, path, mon, settings)
        {
        }

        // Документ с фигурами и историей правок (начатый в оверлее) переходит во владение окна.
        public EditorForm(EditorDoc doc, string app, string path, MonitorInfo mon, CapSettings settings)
        {
            _settings = settings.Clone();
            _app = string.IsNullOrEmpty(app) ? AppNaming.Desktop : app;
            _path = path;
            _doc = doc;
            _style = EditorStyle.From(_settings);

            Rectangle work = mon != null ? mon.WorkArea : Screen.PrimaryScreen.WorkingArea;
            _scale = Math.Max(1f, CapDpi.ScaleAt(new Point(work.X + work.Width / 2, work.Y + work.Height / 2)));

            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = EditorColors.Back;
            ForeColor = EditorColors.Text;
            KeyPreview = true;
            ShowInTaskbar = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            _bar = new EditorBar(_style);
            _bar.Dock = DockStyle.Top;
            _status = new EditorStatus();
            _status.Dock = DockStyle.Bottom;
            _canvas = new EditorCanvas(_doc, _style);
            _canvas.Dock = DockStyle.Fill;
            Controls.Add(_canvas);
            Controls.Add(_status);
            Controls.Add(_bar);

            _bar.CommandEnabled = CommandEnabled;
            _bar.ToolChanged += delegate { _canvas.ToolChanged(); UpdateStatus(); };
            _bar.StyleEdited += delegate(StyleField f) { _canvas.ApplyStyleField(f); };
            _bar.CommandInvoked += Run;
            _canvas.Changed += delegate { UpdateTitle(); _bar.Invalidate(); UpdateStatus(); };
            _canvas.ViewChanged += UpdateStatus;
            _canvas.CropApplied += delegate { Message(Tr.S("Обрезано", "Cropped")); };
            _status.ZoomClicked += delegate { _canvas.ToggleFit(); };
            _messageTimer.Interval = 4000;
            _messageTimer.Tick += delegate { _messageTimer.Stop(); _status.Message = null; };

            ApplyScale(_scale);
            PlaceOn(work);
            UpdateTitle();
            UpdateStatus();
        }

        internal EditorDoc Doc { get { return _doc; } }
        internal EditorCanvas Canvas { get { return _canvas; } }
        internal EditorBar Bar { get { return _bar; } }
        public EditorStyle Style { get { return _style; } }
        public string FilePath { get { return _path; } }

        protected override bool ShowWithoutActivation { get { return OpenInactive; } }

        private int S(float v) { return (int)Math.Round(v * _scale); }

        private void ApplyScale(float scale)
        {
            _scale = scale;
            MinimumSize = new Size(S(640), S(420));
            _bar.ApplyScale(scale);
            _status.ApplyScale(scale);
            _canvas.ApplyScale(scale);
        }

        // Окно по размеру снимка, но не больше рабочей области монитора — посередине.
        private void PlaceOn(Rectangle work)
        {
            _bar.LayoutFor(Math.Min(work.Width, _doc.Size.Width + S(80)));
            Size chrome = SizeFromClientSize(new Size(0, 0));
            int w = Math.Min((int)(work.Width * 0.92), Math.Max(S(900), _doc.Size.Width + S(64) + chrome.Width));
            int h = Math.Min((int)(work.Height * 0.92), Math.Max(S(560), _doc.Size.Height + S(64) + _bar.Height + _status.Height + chrome.Height));
            w = Math.Max(w, MinimumSize.Width);
            h = Math.Max(h, MinimumSize.Height);
            Bounds = new Rectangle(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h);
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
            _canvas.FitView();
            if (!OpenInactive) _canvas.Focus();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                RECT r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                if (dpi > 0) ApplyScale(dpi / 96f);
                Bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        private void UpdateTitle()
        {
            string name = _path != null ? Path.GetFileName(_path) : Tr.S("новый снимок", "new screenshot");
            Text = (NeedsSave ? "• " : "") + name + " — " + Tr.S("Редактор снимка", "Screenshot editor");
        }

        private void UpdateStatus()
        {
            _status.Hint = _canvas.Hint;
            _status.Info = _doc.Size.Width.ToString(CultureInfo.InvariantCulture) + " × " + _doc.Size.Height.ToString(CultureInfo.InvariantCulture)
                           + "   " + (int)Math.Round(_canvas.Zoom * 100) + "%" + (_canvas.IsFit ? " " + Tr.S("(по окну)", "(fit)") : "");
        }

        private void Message(string text)
        {
            _status.Message = text;
            _messageTimer.Stop();
            _messageTimer.Start();
        }

        private bool NeedsSave { get { return _doc.Dirty || (_path == null && _copiedVersion != _doc.Version); } }

        private bool CommandEnabled(EditorCommand c)
        {
            switch (c)
            {
                case EditorCommand.Undo: return _doc.CanUndo;
                case EditorCommand.Redo: return _doc.CanRedo;
                default: return true;
            }
        }

        internal void Run(EditorCommand c)
        {
            switch (c)
            {
                case EditorCommand.Undo: _canvas.Undo(); break;
                case EditorCommand.Redo: _canvas.Redo(); break;
                case EditorCommand.RotateLeft: _canvas.Rotate(false); break;
                case EditorCommand.RotateRight: _canvas.Rotate(true); break;
                case EditorCommand.Copy: CopyImage(); break;
                case EditorCommand.Save: Save(false); break;
                case EditorCommand.SaveAs: Save(true); break;
                case EditorCommand.Folder: ShowFolder(); break;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_canvas.EditingText)
            {
                if (keyData == (Keys.Control | Keys.S)) { Save(false); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            switch (keyData)
            {
                case Keys.Control | Keys.Z: _canvas.Undo(); return true;
                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z: _canvas.Redo(); return true;
                case Keys.Control | Keys.S: Save(false); return true;
                case Keys.Control | Keys.Shift | Keys.S: Save(true); return true;
                case Keys.Control | Keys.C: CopyImage(); return true;
                case Keys.Control | Keys.D0:
                case Keys.Control | Keys.NumPad0: _canvas.FitView(); return true;
                case Keys.Control | Keys.D1:
                case Keys.Control | Keys.NumPad1: _canvas.ZoomTo(1f); return true;
                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Add: _canvas.ZoomStep(1); return true;
                case Keys.Control | Keys.OemMinus:
                case Keys.Control | Keys.Subtract: _canvas.ZoomStep(-1); return true;
                case Keys.Delete:
                case Keys.Back: _canvas.DeleteSelected(); return true;
                case Keys.Escape: _canvas.Escape(); return true;
                case Keys.Enter: _canvas.EnterPressed(); return true;
                case Keys.Left: _canvas.Nudge(-1, 0); return true;
                case Keys.Right: _canvas.Nudge(1, 0); return true;
                case Keys.Up: _canvas.Nudge(0, -1); return true;
                case Keys.Down: _canvas.Nudge(0, 1); return true;
                case Keys.Shift | Keys.Left: _canvas.Nudge(-10, 0); return true;
                case Keys.Shift | Keys.Right: _canvas.Nudge(10, 0); return true;
                case Keys.Shift | Keys.Up: _canvas.Nudge(0, -10); return true;
                case Keys.Shift | Keys.Down: _canvas.Nudge(0, 10); return true;
                case Keys.F: _bar.ToggleFill(); return true;
            }
            EditTool tool;
            if (EditorBar.ToolForKey(keyData, out tool)) { _bar.SelectTool(tool); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && !_canvas.EditingText) { _canvas.SpaceHeld = true; e.Handled = true; e.SuppressKeyPress = true; }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) _canvas.SpaceHeld = false;
            base.OnKeyUp(e);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            _canvas.SpaceHeld = false;
            base.OnDeactivate(e);
        }

        // ---------- буфер обмена и файлы ----------

        private void CopyImage()
        {
            _canvas.CommitText();
            try
            {
                using (Bitmap output = _doc.Render(null)) ImageStore.CopyImage(output);
                _copiedVersion = _doc.Version;
                Message(Tr.S("Скопировано в буфер обмена", "Copied to clipboard"));
                UpdateTitle();
            }
            catch (Exception ex)
            {
                CapLog.Report(ex);
                Message(Tr.S("Не удалось скопировать: ", "Copy failed: ") + ex.Message);
            }
        }

        private static bool Writable(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg";
        }

        internal bool Save(bool saveAs)
        {
            _canvas.CommitText();
            string target = _path;
            if (!saveAs && target != null && !Writable(target)) saveAs = true;
            if (!saveAs && target != null && File.Exists(target) && !_overwriteAsked)
            {
                DialogResult answer = MessageBox.Show(this,
                    Tr.S("Заменить файл снимка отредактированным?\n\nПрежний вариант уйдёт в Корзину. «Нет» — сохранить под другим именем.",
                         "Replace the screenshot file with the edited one?\n\nThe previous version goes to the Recycle Bin. \"No\" saves under another name."),
                    Tr.S("Редактор снимка", "Screenshot editor"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return false;
                if (answer == DialogResult.No) saveAs = true;
            }
            try
            {
                if (saveAs)
                {
                    target = AskPath();
                    if (target == null) return false;
                }
                else if (target == null)
                {
                    target = NameTemplate.BuildPath(_settings.EffectiveShotFolder, _settings.PerAppFolders, _app, _settings.NameTemplate,
                                                    DateTime.Now, _settings.ImageExtension, null);
                }
                Cursor = Cursors.WaitCursor;
                using (Bitmap output = _doc.Render(null)) WriteReplacing(output, target, _settings.JpegQuality, Handle);
            }
            catch (Exception ex)
            {
                CapLog.Report(ex);
                string reason = ex is UnauthorizedAccessException ? Tr.S("нет доступа к папке", "no access to the folder") : ex.Message;
                MessageBox.Show(this, Tr.S("Не удалось сохранить снимок: ", "Could not save the screenshot: ") + reason,
                                Tr.S("Редактор снимка", "Screenshot editor"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
            _path = target;
            _overwriteAsked = true;
            _doc.MarkSaved();
            Message(Tr.S("Сохранено: ", "Saved: ") + Path.GetFileName(target));
            UpdateTitle();
            return true;
        }

        // Новый файл пишется рядом, старый уходит в Корзину, и только потом новый встаёт на его место: ошибка на любом
        // шаге оставляет прежний снимок целым (или в Корзине, откуда его можно вернуть).
        internal static void WriteReplacing(Image image, string path, int jpegQuality, IntPtr owner)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            string format = ext == ".jpg" || ext == ".jpeg" ? "jpg" : "png";
            if (!File.Exists(path))
            {
                ImageStore.Save(image, path, format, jpegQuality);
                return;
            }
            string fresh = path + ".new";
            ImageStore.Save(image, fresh, format, jpegQuality);
            try
            {
                bool aborted;
                int tooLong;
                int rc = Native.RecycleFiles(owner, new string[] { path }, out aborted, out tooLong);
                if (rc != 0 || aborted || tooLong > 0 || File.Exists(path))
                    throw new IOException(Tr.S("прежний файл не удалось переместить в Корзину", "the previous file could not be moved to the Recycle Bin"));
                File.Move(fresh, path);
            }
            catch
            {
                try { if (File.Exists(fresh)) File.Delete(fresh); } catch { }
                throw;
            }
        }

        private string AskPath()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = Tr.S("Сохранить снимок как", "Save screenshot as");
                dlg.Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg";
                string suggested = _path;
                if (suggested == null)
                {
                    try
                    {
                        suggested = NameTemplate.BuildPath(_settings.EffectiveShotFolder, _settings.PerAppFolders, _app, _settings.NameTemplate,
                                                           DateTime.Now, _settings.ImageExtension, null);
                    }
                    catch (Exception ex) { CapLog.Report(ex); }
                }
                if (suggested != null)
                {
                    string dir = Path.GetDirectoryName(suggested);
                    while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(dir)) dlg.InitialDirectory = dir;
                    dlg.FileName = Path.GetFileNameWithoutExtension(suggested);
                    string ext = (Path.GetExtension(suggested) ?? "").ToLowerInvariant();
                    dlg.FilterIndex = ext == ".jpg" || ext == ".jpeg" ? 2 : 1;
                }
                dlg.AddExtension = true;
                dlg.OverwritePrompt = true;
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
            }
        }

        private void ShowFolder()
        {
            if (_path != null && File.Exists(_path)) { ToastHost.ShowInFolder(_path); return; }
            string dir = _settings.EffectiveShotFolder;
            if (_settings.PerAppFolders)
            {
                string sub = Path.Combine(dir, NameTemplate.SanitizeSegment(_app));
                if (Directory.Exists(sub)) dir = sub;
            }
            if (!Directory.Exists(dir)) { Message(Tr.S("Папка снимков ещё не создана", "The screenshots folder does not exist yet")); return; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "\"" + dir + "\"");
                psi.UseShellExecute = false;
                using (Process.Start(psi)) { }
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        // ---------- закрытие ----------

        // Остановка фонового процесса без вопросов (правки теряются) — только когда спрашивать уже некому.
        internal void ForceClose()
        {
            _forceClose = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_forceClose || e.Cancel) return;
            _canvas.CommitText();
            if (!NeedsSave) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            string question = _path == null
                ? Tr.S("Снимок ещё не сохранён. Сохранить его?", "The screenshot has not been saved. Save it?")
                : Tr.S("Сохранить изменения в снимке?", "Save changes to the screenshot?");
            DialogResult answer = MessageBox.Show(this, question, Tr.S("Редактор снимка", "Screenshot editor"),
                                                  MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !Save(false))) e.Cancel = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _messageTimer.Dispose();
                _doc.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ //
    //  Панель инструментов: рисуется целиком сама, переносится на новые строки в узком окне
    // ------------------------------------------------------------------ //
    internal sealed class EditorBar : Control
    {
        private enum Kind { Tool, Command, Swatch, CustomColor, Width, Fill, FontDown, FontLabel, FontUp }

        private sealed class Item
        {
            public Kind Kind;
            public EditTool Tool;
            public EditorCommand Command;
            public Color Swatch;
            public float Value;
            public string Tip;
            public string Label;
            public int Group;
            public bool Output;
            public Rectangle Bounds;
        }

        public static readonly Color[] Palette =
        {
            Color.FromArgb(229, 57, 53), Color.FromArgb(251, 140, 0), Color.FromArgb(253, 216, 53), Color.FromArgb(67, 160, 71),
            Color.FromArgb(30, 136, 229), Color.FromArgb(142, 36, 170), Color.FromArgb(33, 33, 33), Color.FromArgb(255, 255, 255)
        };
        public static readonly float[] Widths = { 2f, 4f, 8f };
        public static readonly float[] FontSizes = { 14f, 18f, 24f, 28f, 36f, 48f, 64f, 88f, 120f };

        private readonly EditorStyle _style;
        private readonly List<Item> _items = new List<Item>();
        private readonly List<Rectangle> _separators = new List<Rectangle>();
        private readonly ToolTip _tip = new ToolTip();
        private float _scale = 1f;
        private Font _labelFont, _glyphFont, _textIconFont;
        private Item _hot, _pressed;
        private Color _customColor = Color.FromArgb(0, 188, 212);

        public Func<EditorCommand, bool> CommandEnabled;
        public event Action ToolChanged;
        public event Action<StyleField> StyleEdited;
        public event Action<EditorCommand> CommandInvoked;

        public EditorBar(EditorStyle style)
        {
            _style = style;
            if (Array.IndexOf(Palette, style.Color) < 0) _customColor = style.Color;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // Щелчок по панели не забирает фокус: цвет, выбранный во время набора текста, применяется к этому тексту.
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = EditorColors.Back;

            AddTool(0, EditTool.Select, Tr.S("Выбор и перемещение (V)", "Select and move (V)"));
            AddTool(0, EditTool.Pen, Tr.S("Карандаш (P)", "Pen (P)"));
            AddTool(0, EditTool.Marker, Tr.S("Маркер (H)", "Highlighter (H)"));
            AddTool(0, EditTool.Line, Tr.S("Линия (L) · Shift — шаг 45°", "Line (L) · Shift snaps to 45°"));
            AddTool(0, EditTool.Arrow, Tr.S("Стрелка (A) · Shift — шаг 45°", "Arrow (A) · Shift snaps to 45°"));
            AddTool(0, EditTool.Rect, Tr.S("Прямоугольник (R) · Shift — квадрат", "Rectangle (R) · Shift for a square"));
            AddTool(0, EditTool.Ellipse, Tr.S("Эллипс (O) · Shift — круг", "Ellipse (O) · Shift for a circle"));
            AddTool(0, EditTool.Step, Tr.S("Номер шага (N)", "Step number (N)"));
            AddTool(0, EditTool.Text, Tr.S("Текст (T)", "Text (T)"));
            AddTool(1, EditTool.Blur, Tr.S("Размытие (B) — скрыть данные", "Blur (B) — hide data"));
            AddTool(1, EditTool.Pixelate, Tr.S("Пикселизация (X) — скрыть данные", "Pixelate (X) — hide data"));
            AddTool(1, EditTool.Crop, Tr.S("Обрезка (C) · Enter — применить", "Crop (C) · Enter to apply"));
            AddCommand(2, EditorCommand.RotateLeft, Tr.S("Повернуть влево", "Rotate left"), null, false);
            AddCommand(2, EditorCommand.RotateRight, Tr.S("Повернуть вправо", "Rotate right"), null, false);
            AddCommand(3, EditorCommand.Undo, Tr.S("Отменить (Ctrl+Z)", "Undo (Ctrl+Z)"), null, false);
            AddCommand(3, EditorCommand.Redo, Tr.S("Повторить (Ctrl+Y)", "Redo (Ctrl+Y)"), null, false);
            foreach (Color c in Palette)
            {
                Item it = new Item();
                it.Kind = Kind.Swatch;
                it.Group = 4;
                it.Swatch = c;
                it.Tip = Tr.S("Цвет", "Color");
                _items.Add(it);
            }
            Item custom = new Item();
            custom.Kind = Kind.CustomColor;
            custom.Group = 4;
            custom.Tip = Tr.S("Другой цвет…", "Another color…");
            _items.Add(custom);
            foreach (float w in Widths)
            {
                Item it = new Item();
                it.Kind = Kind.Width;
                it.Group = 5;
                it.Value = w;
                it.Tip = Tr.S("Толщина линии", "Line width");
                _items.Add(it);
            }
            AddSimple(5, Kind.Fill, Tr.S("Заливка (F): фигура закрашена, текст на подложке", "Fill (F): solid shapes, text on a backing"));
            AddSimple(6, Kind.FontDown, Tr.S("Текст мельче", "Smaller text"));
            AddSimple(6, Kind.FontLabel, Tr.S("Размер текста", "Text size"));
            AddSimple(6, Kind.FontUp, Tr.S("Текст крупнее", "Larger text"));
            AddCommand(7, EditorCommand.Copy, Tr.S("Копировать в буфер обмена (Ctrl+C)", "Copy to clipboard (Ctrl+C)"), Tr.S("Копировать", "Copy"), true);
            AddCommand(7, EditorCommand.Save, Tr.S("Сохранить (Ctrl+S)", "Save (Ctrl+S)"), Tr.S("Сохранить", "Save"), true);
            AddCommand(7, EditorCommand.SaveAs, Tr.S("Сохранить как… (Ctrl+Shift+S)", "Save as… (Ctrl+Shift+S)"), Tr.S("Сохранить как…", "Save as…"), true);
            AddCommand(7, EditorCommand.Folder, Tr.S("Показать в папке", "Show in folder"), Tr.S("Папка", "Folder"), true);
            ApplyScale(1f);
        }

        private void AddTool(int group, EditTool tool, string tip)
        {
            Item it = new Item();
            it.Kind = Kind.Tool;
            it.Group = group;
            it.Tool = tool;
            it.Tip = tip;
            _items.Add(it);
        }

        private void AddCommand(int group, EditorCommand command, string tip, string label, bool output)
        {
            Item it = new Item();
            it.Kind = Kind.Command;
            it.Group = group;
            it.Command = command;
            it.Tip = tip;
            it.Label = label;
            it.Output = output;
            _items.Add(it);
        }

        private void AddSimple(int group, Kind kind, string tip)
        {
            Item it = new Item();
            it.Kind = kind;
            it.Group = group;
            it.Tip = tip;
            _items.Add(it);
        }

        public EditTool Tool { get { return _style.Tool; } }

        public static bool ToolForKey(Keys key, out EditTool tool)
        {
            tool = EditTool.Select;
            switch (key)
            {
                case Keys.V: tool = EditTool.Select; return true;
                case Keys.P: tool = EditTool.Pen; return true;
                case Keys.H: tool = EditTool.Marker; return true;
                case Keys.L: tool = EditTool.Line; return true;
                case Keys.A: tool = EditTool.Arrow; return true;
                case Keys.R: tool = EditTool.Rect; return true;
                case Keys.O: tool = EditTool.Ellipse; return true;
                case Keys.N: tool = EditTool.Step; return true;
                case Keys.T: tool = EditTool.Text; return true;
                case Keys.B: tool = EditTool.Blur; return true;
                case Keys.X: tool = EditTool.Pixelate; return true;
                case Keys.C: tool = EditTool.Crop; return true;
            }
            return false;
        }

        public void SelectTool(EditTool tool)
        {
            _style.Tool = tool;
            Invalidate();
            if (ToolChanged != null) ToolChanged();
        }

        public void ToggleFill()
        {
            _style.Fill = !_style.Fill;
            RaiseStyle(StyleField.Fill);
        }

        private void RaiseStyle(StyleField f)
        {
            Invalidate();
            if (StyleEdited != null) StyleEdited(f);
        }

        public void ApplyScale(float scale)
        {
            _scale = scale;
            if (_labelFont != null) _labelFont.Dispose();
            if (_glyphFont != null) _glyphFont.Dispose();
            if (_textIconFont != null) _textIconFont.Dispose();
            _labelFont = new Font("Segoe UI", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _glyphFont = CapFonts.Icons(16f * scale);
            _textIconFont = new Font("Segoe UI", 14f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            LayoutFor(Width > 0 ? Width : 1000);
        }

        private int S(float v) { return (int)Math.Round(v * _scale); }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            LayoutFor(Width);
        }

        private int ItemWidth(Item it)
        {
            switch (it.Kind)
            {
                case Kind.Swatch:
                case Kind.CustomColor: return S(28);
                case Kind.Width: return S(30);
                case Kind.FontDown:
                case Kind.FontUp: return S(28);
                case Kind.FontLabel: return S(34);
            }
            if (!it.Output || it.Label == null) return S(36);
            int text = TextRenderer.MeasureText(it.Label, _labelFont).Width;
            return S(10) + (CapFonts.HasIcons ? S(24) : 0) + text + S(8);
        }

        // Раскладка слева направо с переносом; группа вывода (копировать, сохранить) прижата вправо.
        public void LayoutFor(int width)
        {
            if (_labelFont == null) return;
            int size = S(36), gap = S(2), groupGap = S(14), pad = S(6);
            width = Math.Max(width, S(200));
            _separators.Clear();
            int x = pad, y = pad, lastGroup = -1;
            foreach (Item it in _items)
            {
                if (it.Output) continue;
                int w = ItemWidth(it);
                int step = x == pad ? 0 : it.Group != lastGroup ? groupGap : gap;
                if (x + step + w > width - pad && x > pad)
                {
                    x = pad;
                    y += size + gap;
                    step = 0;
                }
                if (step == groupGap) _separators.Add(new Rectangle(x + groupGap / 2, y + S(8), 1, size - S(16)));
                x += step;
                it.Bounds = new Rectangle(x, y, w, size);
                x += w;
                lastGroup = it.Group;
            }
            int outWidth = 0;
            foreach (Item it in _items) if (it.Output) outWidth += ItemWidth(it) + gap;
            int ox;
            if (x + groupGap + outWidth <= width - pad) ox = width - pad - outWidth;
            else
            {
                y += size + gap;
                ox = Math.Max(pad, width - pad - outWidth);
            }
            foreach (Item it in _items)
            {
                if (!it.Output) continue;
                int w = ItemWidth(it);
                if (ox + w > width - pad && ox > pad) { ox = pad; y += size + gap; }
                it.Bounds = new Rectangle(ox, y, w, size);
                ox += w + gap;
            }
            int height = y + size + pad;
            if (Height != height) Height = height;
            Invalidate();
        }

        private Item ItemAt(Point p)
        {
            foreach (Item it in _items) if (it.Bounds.Contains(p)) return it;
            return null;
        }

        private bool IsEnabled(Item it)
        {
            return it.Kind != Kind.Command || CommandEnabled == null || CommandEnabled(it.Command);
        }

        private bool Checked(Item it)
        {
            switch (it.Kind)
            {
                case Kind.Tool: return it.Tool == _style.Tool;
                case Kind.Swatch: return it.Swatch.ToArgb() == _style.Color.ToArgb();
                case Kind.CustomColor:
                    foreach (Color c in Palette) if (c.ToArgb() == _style.Color.ToArgb()) return false;
                    return true;
                case Kind.Width: return Math.Abs(it.Value - _style.Width) < 0.01f;
                case Kind.Fill: return _style.Fill;
            }
            return false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Item it = ItemAt(e.Location);
            if (it == _hot) return;
            _hot = it;
            _tip.Hide(this);
            if (it != null && it.Tip != null)
            {
                string tip = it.Kind == Kind.FontLabel ? it.Tip + ": " + ((int)_style.FontSize).ToString(CultureInfo.InvariantCulture) : it.Tip;
                _tip.Show(tip, this, it.Bounds.X, it.Bounds.Bottom + S(4), 4000);
            }
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = null;
            _pressed = null;
            _tip.Hide(this);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _pressed = ItemAt(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Item it = ItemAt(e.Location);
            Item pressed = _pressed;
            _pressed = null;
            Invalidate();
            if (it != null && it == pressed && IsEnabled(it)) Activate(it);
        }

        private void Activate(Item it)
        {
            _tip.Hide(this);
            switch (it.Kind)
            {
                case Kind.Tool: SelectTool(it.Tool); break;
                case Kind.Command: if (CommandInvoked != null) CommandInvoked(it.Command); break;
                case Kind.Swatch: _style.Color = it.Swatch; RaiseStyle(StyleField.Color); break;
                case Kind.CustomColor:
                    using (ColorDialog dlg = new ColorDialog())
                    {
                        dlg.FullOpen = true;
                        dlg.Color = _customColor;
                        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                        _customColor = Color.FromArgb(255, dlg.Color);
                    }
                    _style.Color = _customColor;
                    RaiseStyle(StyleField.Color);
                    break;
                case Kind.Width: _style.Width = it.Value; RaiseStyle(StyleField.Width); break;
                case Kind.Fill: ToggleFill(); break;
                case Kind.FontDown: StepFont(-1); break;
                case Kind.FontUp: StepFont(1); break;
            }
        }

        public void StepFont(int direction)
        {
            float next = _style.FontSize;
            if (direction > 0)
            {
                foreach (float f in FontSizes) if (f > _style.FontSize + 0.01f) { next = f; break; }
            }
            else
            {
                for (int i = FontSizes.Length - 1; i >= 0; i--) if (FontSizes[i] < _style.FontSize - 0.01f) { next = FontSizes[i]; break; }
            }
            if (Math.Abs(next - _style.FontSize) < 0.01f) return;
            _style.FontSize = next;
            RaiseStyle(StyleField.FontSize);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (Pen line = new Pen(EditorColors.Line)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
            foreach (Rectangle sep in _separators)
                using (SolidBrush b = new SolidBrush(EditorColors.Line)) g.FillRectangle(b, sep);
            foreach (Item it in _items)
            {
                bool enabled = IsEnabled(it), on = Checked(it);
                Rectangle r = it.Bounds;
                if (it.Kind != Kind.Swatch && it.Kind != Kind.CustomColor && it.Kind != Kind.FontLabel)
                {
                    Color back = Color.Empty;
                    if (on) back = Color.FromArgb(90, EditorColors.Accent);
                    else if (enabled && it == _pressed) back = Color.FromArgb(46, 255, 255, 255);
                    else if (enabled && it == _hot) back = Color.FromArgb(26, 255, 255, 255);
                    if (it.Output && it.Command == EditorCommand.Save && !on) back = it == _hot ? Color.FromArgb(215, EditorColors.Accent) : Color.FromArgb(170, EditorColors.Accent);
                    if (back != Color.Empty)
                    {
                        Rectangle box = Rectangle.Inflate(r, -S(2), -S(2));
                        using (GraphicsPath p = EditGeometry.Rounded(box, S(5)))
                        using (SolidBrush b = new SolidBrush(back))
                            g.FillPath(b, p);
                    }
                }
                Color fg = enabled ? EditorColors.Text : Color.FromArgb(90, 90, 90);
                DrawItem(g, it, r, fg, on);
            }
        }

        private void DrawItem(Graphics g, Item it, Rectangle r, Color fg, bool on)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            switch (it.Kind)
            {
                case Kind.Tool:
                    DrawToolIcon(g, it.Tool, new RectangleF(cx - S(10), cy - S(10), S(20), S(20)), fg);
                    return;
                case Kind.Swatch:
                case Kind.CustomColor:
                    DrawSwatch(g, new PointF(cx, cy), _scale, it.Kind == Kind.Swatch ? it.Swatch : _customColor, it.Kind == Kind.CustomColor, on, it == _hot);
                    return;
                case Kind.Width:
                {
                    float w = Math.Max(1.5f, it.Value * _scale * 0.8f);
                    using (Pen p = new Pen(fg, w))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        g.DrawLine(p, cx - S(8), cy, cx + S(8), cy);
                    }
                    return;
                }
                case Kind.Fill:
                {
                    RectangleF box = new RectangleF(cx - S(8), cy - S(7), S(16), S(14));
                    using (Pen p = new Pen(fg, Math.Max(1.5f, 1.5f * _scale))) g.DrawRectangle(p, box.X, box.Y, box.Width, box.Height);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(on ? 255 : 90, fg)))
                        g.FillRectangle(b, box.X + S(3), box.Y + S(3), box.Width - S(6), box.Height - S(6));
                    return;
                }
                case Kind.FontDown:
                case Kind.FontUp:
                case Kind.FontLabel:
                {
                    string text = it.Kind == Kind.FontDown ? "A−" : it.Kind == Kind.FontUp ? "A+" : ((int)_style.FontSize).ToString(CultureInfo.InvariantCulture);
                    Font font = it.Kind == Kind.FontLabel ? _labelFont : _textIconFont;
                    TextRenderer.DrawText(g, text, font, r, it.Kind == Kind.FontLabel ? EditorColors.Dim : fg,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    return;
                }
            }
            // Команды
            string glyph = null, fallback = null;
            switch (it.Command)
            {
                case EditorCommand.Copy: glyph = CapFonts.Copy; fallback = "⧉"; break;
                case EditorCommand.Save: glyph = CapFonts.Save; break;
                case EditorCommand.SaveAs: glyph = ""; break;
                case EditorCommand.Folder: glyph = CapFonts.Folder; fallback = Tr.S("Папка", "Folder"); break;
            }
            if (it.Output)
            {
                int x = r.X + S(10);
                if (CapFonts.HasIcons && glyph != null)
                {
                    Rectangle gr = new Rectangle(x, r.Y, it.Label != null ? S(20) : r.Width - S(20), r.Height);
                    TextRenderer.DrawText(g, glyph, _glyphFont, gr, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    x += S(24);
                }
                string label = it.Label ?? (CapFonts.HasIcons ? null : fallback);
                if (label != null)
                    TextRenderer.DrawText(g, label, _labelFont, new Rectangle(x, r.Y, r.Right - x, r.Height), fg,
                                          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            RectangleF icon = new RectangleF(cx - S(10), cy - S(10), S(20), S(20));
            switch (it.Command)
            {
                case EditorCommand.RotateLeft: DrawTurn(g, icon, fg, false, false); break;
                case EditorCommand.RotateRight: DrawTurn(g, icon, fg, true, false); break;
                case EditorCommand.Undo: DrawTurn(g, icon, fg, false, true); break;
                case EditorCommand.Redo: DrawTurn(g, icon, fg, true, true); break;
            }
        }

        // Кружок цвета; custom — «другой цвет»: круг из четырёх цветов с выбранным в середине. Общий с панелью оверлея.
        internal static void DrawSwatch(Graphics g, PointF c, float scale, Color color, bool custom, bool on, bool hot)
        {
            float d = (float)Math.Round(18 * scale);
            RectangleF dot = new RectangleF(c.X - d / 2, c.Y - d / 2, d, d);
            if (on || hot)
            {
                float o = (float)Math.Round((on ? 26 : 24) * scale);
                using (Pen ring = new Pen(on ? EditorColors.Accent : Color.FromArgb(90, 255, 255, 255), (float)Math.Round(2 * scale)))
                    g.DrawEllipse(ring, c.X - o / 2, c.Y - o / 2, o, o);
            }
            if (!custom)
            {
                using (SolidBrush b = new SolidBrush(color)) g.FillEllipse(b, dot);
            }
            else
            {
                Color[] wheel = { Color.FromArgb(229, 57, 53), Color.FromArgb(253, 216, 53), Color.FromArgb(67, 160, 71), Color.FromArgb(30, 136, 229) };
                for (int i = 0; i < 4; i++)
                    using (SolidBrush b = new SolidBrush(wheel[i])) g.FillPie(b, dot.X, dot.Y, dot.Width, dot.Height, i * 90 - 90, 90);
                float inner = d * 0.5f;
                using (SolidBrush b = new SolidBrush(color)) g.FillEllipse(b, c.X - inner / 2, c.Y - inner / 2, inner, inner);
            }
            using (Pen edge = new Pen(Color.FromArgb(70, 255, 255, 255), 1)) g.DrawEllipse(edge, dot);
        }

        // Значки рисуются векторами в сетке 20x20: одинаково на любом шрифте и масштабе.
        internal static void DrawToolIcon(Graphics g, EditTool tool, RectangleF box, Color fg)
        {
            float k = box.Width / 20f;
            Func<float, float, PointF> P = delegate(float x, float y) { return new PointF(box.X + x * k, box.Y + y * k); };
            using (Pen pen = new Pen(fg, Math.Max(1.4f, 1.6f * k)))
            using (SolidBrush brush = new SolidBrush(fg))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (tool)
                {
                    case EditTool.Select:
                        g.FillPolygon(brush, new PointF[] { P(5, 2), P(5, 16.5f), P(8.6f, 13.2f), P(11.2f, 18.6f), P(13.4f, 17.6f), P(10.9f, 12.3f), P(15.6f, 12.3f) });
                        break;
                    case EditTool.Pen:
                        g.DrawPolygon(pen, new PointF[] { P(4, 16), P(5, 12), P(14, 3), P(17, 6), P(8, 15) });
                        g.DrawLine(pen, P(12, 5), P(15, 8));
                        break;
                    case EditTool.Marker:
                        using (Pen wide = new Pen(Color.FromArgb(150, fg), 5f * k))
                        {
                            wide.StartCap = LineCap.Round;
                            wide.EndCap = LineCap.Round;
                            g.DrawLine(wide, P(4, 13), P(16, 7));
                        }
                        g.DrawLine(pen, P(3, 18), P(17, 18));
                        break;
                    case EditTool.Line:
                        g.DrawLine(pen, P(3.5f, 16.5f), P(16.5f, 3.5f));
                        break;
                    case EditTool.Arrow:
                        g.DrawLine(pen, P(3.5f, 16.5f), P(14.5f, 5.5f));
                        g.FillPolygon(brush, new PointF[] { P(17.5f, 2.5f), P(9.5f, 5), P(15, 10.5f) });
                        break;
                    case EditTool.Rect:
                        g.DrawRectangle(pen, box.X + 3 * k, box.Y + 5 * k, 14 * k, 10 * k);
                        break;
                    case EditTool.Ellipse:
                        g.DrawEllipse(pen, box.X + 2.5f * k, box.Y + 4 * k, 15 * k, 12 * k);
                        break;
                    case EditTool.Step:
                        g.DrawEllipse(pen, box.X + 2.5f * k, box.Y + 2.5f * k, 15 * k, 15 * k);
                        g.DrawLine(pen, P(8, 7.5f), P(10.5f, 6));
                        g.DrawLine(pen, P(10.5f, 6), P(10.5f, 14));
                        break;
                    case EditTool.Text:
                        g.DrawLine(pen, P(4, 4), P(16, 4));
                        g.DrawLine(pen, P(10, 4), P(10, 17));
                        g.DrawLine(pen, P(7.5f, 17), P(12.5f, 17));
                        break;
                    case EditTool.Blur:
                        using (GraphicsPath drop = new GraphicsPath())
                        {
                            drop.AddEllipse(box.X + 3 * k, box.Y + 3 * k, 14 * k, 14 * k);
                            using (PathGradientBrush soft = new PathGradientBrush(drop))
                            {
                                soft.CenterColor = fg;
                                soft.SurroundColors = new Color[] { Color.FromArgb(0, fg) };
                                g.FillPath(soft, drop);
                            }
                        }
                        break;
                    case EditTool.Pixelate:
                        for (int y = 0; y < 3; y++)
                            for (int x = 0; x < 3; x++)
                            {
                                int alpha = (x + y) % 2 == 0 ? 255 : 90;
                                using (SolidBrush cell = new SolidBrush(Color.FromArgb(alpha, fg)))
                                    g.FillRectangle(cell, box.X + (3.5f + x * 4.5f) * k, box.Y + (3.5f + y * 4.5f) * k, 4 * k, 4 * k);
                            }
                        break;
                    case EditTool.Crop:
                        g.DrawLines(pen, new PointF[] { P(6, 2), P(6, 14), P(18, 14) });
                        g.DrawLines(pen, new PointF[] { P(2, 6), P(14, 6), P(14, 18) });
                        break;
                }
            }
        }

        // Поворот — дуга в три четверти с разрывом сверху; отмена и повтор — полукруг над строкой. Стрелка — в конце дуги,
        // по направлению обхода. Углы GDI+ растут по часовой (ось Y вниз).
        internal static void DrawTurn(Graphics g, RectangleF box, Color fg, bool clockwise, bool history)
        {
            float k = box.Width / 20f;
            RectangleF arc;
            float start, sweep;
            if (history)
            {
                arc = new RectangleF(box.X + 3.5f * k, box.Y + 6 * k, 13 * k, 11 * k);
                start = clockwise ? 180f : 0f;
                sweep = clockwise ? 180f : -180f;
            }
            else
            {
                arc = new RectangleF(box.X + 3.5f * k, box.Y + 3.5f * k, 13 * k, 13 * k);
                start = clockwise ? 300f : 240f;
                sweep = clockwise ? 270f : -270f;
            }
            using (Pen pen = new Pen(fg, Math.Max(1.4f, 1.6f * k)))
            using (SolidBrush brush = new SolidBrush(fg))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, arc, start, sweep);
                double at = (start + sweep) * Math.PI / 180;
                PointF end = new PointF((float)(arc.X + arc.Width / 2 + Math.Cos(at) * arc.Width / 2),
                                        (float)(arc.Y + arc.Height / 2 + Math.Sin(at) * arc.Height / 2));
                double tangent = at + (sweep > 0 ? Math.PI / 2 : -Math.PI / 2);
                float tx = (float)Math.Cos(tangent), ty = (float)Math.Sin(tangent), len = 6f * k;
                g.FillPolygon(brush, new PointF[]
                {
                    new PointF(end.X + tx * len * 0.6f, end.Y + ty * len * 0.6f),
                    new PointF(end.X - tx * len * 0.4f - ty * len * 0.5f, end.Y - ty * len * 0.4f + tx * len * 0.5f),
                    new PointF(end.X - tx * len * 0.4f + ty * len * 0.5f, end.Y - ty * len * 0.4f - tx * len * 0.5f)
                });
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tip.Dispose();
                if (_labelFont != null) _labelFont.Dispose();
                if (_glyphFont != null) _glyphFont.Dispose();
                if (_textIconFont != null) _textIconFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ //
    //  Строка состояния
    // ------------------------------------------------------------------ //
    internal sealed class EditorStatus : Control
    {
        private float _scale = 1f;
        private Font _font;
        private string _hint = "", _info = "", _message;
        private Rectangle _infoBounds;

        public event Action ZoomClicked;

        public EditorStatus()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = EditorColors.Back;
            ApplyScale(1f);
        }

        public string Hint { set { if (value != _hint) { _hint = value ?? ""; Invalidate(); } } }
        public string Info { set { if (value != _info) { _info = value ?? ""; Invalidate(); } } }
        public string Message { set { _message = value; Invalidate(); } }

        public void ApplyScale(float scale)
        {
            _scale = scale;
            if (_font != null) _font.Dispose();
            _font = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Height = (int)Math.Round(26 * scale);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (Pen line = new Pen(EditorColors.Line)) g.DrawLine(line, 0, 0, Width, 0);
            int pad = (int)(10 * _scale);
            Size info = TextRenderer.MeasureText(_info, _font);
            _infoBounds = new Rectangle(Width - pad - info.Width, 0, info.Width, Height);
            TextRenderer.DrawText(g, _info, _font, _infoBounds, EditorColors.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            string left = _message ?? _hint;
            TextRenderer.DrawText(g, left, _font, new Rectangle(pad, 0, Math.Max(0, _infoBounds.X - pad * 2), Height),
                                  _message != null ? EditorColors.Text : EditorColors.Dim,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_infoBounds.Contains(e.Location) && ZoomClicked != null) ZoomClicked();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = _infoBounds.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _font != null) _font.Dispose();
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ //
    //  Холст: вид (масштаб, прокрутка), рисование, выделение, текст, обрезка
    // ------------------------------------------------------------------ //
    internal sealed class EditorCanvas : Control
    {
        private enum Drag { None, Draw, Move, Handle, Pan, CropNew, CropMove, CropHandle }

        private const float MinZoom = 0.05f, MaxZoom = 16f;

        private readonly EditorDoc _doc;
        private readonly EditorStyle _style;
        private float _scale = 1f, _zoom = 1f;
        private bool _fit = true, _space, _moved, _textDirty, _editingNew, _inline;
        private PointF _pan;

        private Bitmap _composite, _scaled;
        private int _compositeVersion = -1;
        private CapShape _compositeSkip;
        private Bitmap _compositeImage;
        private float _scaledZoom;

        private CapShape _selected, _live, _hover;
        private EditSnapshot _before;
        private Drag _drag;
        private PointF _dragStart, _lastImage, _startA, _startB;
        private RectangleF _startRect;
        private int _handle = -1;
        private Point _panMouse, _downClient;
        private PointF _panStart;
        private Rectangle _crop, _cropStart;

        private TextBox _textBox;
        private TextShape _editing;
        private EditSnapshot _textBefore;
        private Font _textFont;
        private Font _labelFont;

        public event Action Changed;
        public event Action ViewChanged;
        public event Action CropApplied;

        public EditorCanvas(EditorDoc doc, EditorStyle style)
        {
            _doc = doc;
            _style = style;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = EditorColors.Canvas;
            ApplyScale(1f);
        }

        public float Zoom { get { return _zoom; } }
        public bool IsFit { get { return _fit; } }
        public bool EditingText { get { return _textBox != null; } }
        internal CapShape Selected { get { return _selected; } }
        internal Rectangle CropArea { get { return _crop; } }

        // Холст поверх выделения в оверлее захвата: снимок 1:1 без прокрутки и масштаба, ручки размера — у любой фигуры
        // при любом инструменте (рядом с исходными пикселями экрана случайно задеть чужую ручку мелким масштабом нельзя).
        internal bool Inline
        {
            get { return _inline; }
            set
            {
                _inline = value;
                _fit = !value;
                _zoom = 1f;
                _pan = PointF.Empty;
                Invalidate();
            }
        }

        public bool SpaceHeld
        {
            set
            {
                if (_space == value) return;
                _space = value;
                UpdateCursor(PointToClient(Cursor.Position));
            }
        }

        public string Hint
        {
            get
            {
                switch (_style.Tool)
                {
                    case EditTool.Select: return Tr.S("Щелчок — выбрать фигуру, перетаскивание — переместить, Delete — удалить, двойной щелчок по тексту — править",
                                                      "Click to select, drag to move, Delete removes, double-click text to edit");
                    case EditTool.Pen:
                    case EditTool.Marker: return Tr.S("Shift — прямая линия · Ctrl+колесо — масштаб · пробел или средняя кнопка — сдвиг",
                                                      "Shift for a straight line · Ctrl+wheel zooms · Space or middle button pans");
                    case EditTool.Step: return Tr.S("Щелчок ставит следующий номер: ", "Click places the next number: ") + _doc.NextStep.ToString(CultureInfo.InvariantCulture);
                    case EditTool.Text: return Tr.S("Щелчок — новый текст, Enter — готово, Shift+Enter — новая строка, Esc — отмена",
                                                    "Click for new text, Enter to finish, Shift+Enter for a new line, Esc cancels");
                    case EditTool.Blur:
                    case EditTool.Pixelate: return Tr.S("Выделите область: в сохранённом снимке исходных пикселей под ней не останется",
                                                        "Drag over an area: the saved image keeps none of the original pixels under it");
                    case EditTool.Crop: return _crop.IsEmpty
                        ? Tr.S("Выделите, что оставить", "Drag over what to keep")
                        : Tr.S("Enter или двойной щелчок — обрезать, Esc — отмена · ", "Enter or double-click to crop, Esc cancels · ")
                          + _crop.Width.ToString(CultureInfo.InvariantCulture) + " × " + _crop.Height.ToString(CultureInfo.InvariantCulture);
                }
                return Tr.S("Shift — ровно · Ctrl+колесо — масштаб · пробел или средняя кнопка — сдвиг",
                            "Shift to constrain · Ctrl+wheel zooms · Space or middle button pans");
            }
        }

        private int S(float v) { return (int)Math.Round(v * _scale); }

        // Фокус берётся только в активном окне: настоящий щелчок сперва активирует окно, а сообщение, пришедшее
        // неактивному окну, не должно отнимать фокус у того, с чем пользователь сейчас работает.
        private bool WindowActive
        {
            get
            {
                Form form = FindForm();
                return form != null && Form.ActiveForm == form;
            }
        }

        public void ApplyScale(float scale)
        {
            _scale = scale;
            if (_labelFont != null) _labelFont.Dispose();
            _labelFont = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            if (_textBox != null) PlaceTextBox();
            Invalidate();
        }

        private void RaiseChanged()
        {
            Invalidate();
            if (Changed != null) Changed();
        }

        private void RaiseView()
        {
            if (_textBox != null) PlaceTextBox();
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        // ---------- вид ----------

        private PointF ToImage(PointF client) { return new PointF((client.X - _pan.X) / _zoom, (client.Y - _pan.Y) / _zoom); }

        private PointF ToClient(PointF image) { return new PointF(image.X * _zoom + _pan.X, image.Y * _zoom + _pan.Y); }

        private RectangleF ToClient(RectangleF r)
        {
            return new RectangleF(r.X * _zoom + _pan.X, r.Y * _zoom + _pan.Y, r.Width * _zoom, r.Height * _zoom);
        }

        private RectangleF ImageOnClient { get { return ToClient(new RectangleF(PointF.Empty, _doc.Size)); } }

        private float FitZoom()
        {
            int margin = S(24);
            float zx = (ClientSize.Width - margin * 2) / (float)_doc.Size.Width;
            float zy = (ClientSize.Height - margin * 2) / (float)_doc.Size.Height;
            return Math.Max(MinZoom, Math.Min(1f, Math.Min(zx, zy)));
        }

        public void FitView()
        {
            _fit = true;
            _zoom = FitZoom();
            ClampPan();
            RaiseView();
        }

        public void ToggleFit()
        {
            if (_fit && Math.Abs(_zoom - 1f) > 0.001f) ZoomTo(1f);
            else FitView();
        }

        public void ZoomTo(float zoom)
        {
            ZoomAt(zoom, new Point(ClientSize.Width / 2, ClientSize.Height / 2));
        }

        public void ZoomStep(int direction)
        {
            ZoomAt(NextZoom(_zoom, direction), new Point(ClientSize.Width / 2, ClientSize.Height / 2));
        }

        // Шаг ×1.25, но через 100 % не перескакивает: на нём пиксели снимка совпадают с пикселями экрана.
        internal static float NextZoom(float zoom, int direction)
        {
            float next = direction > 0 ? zoom * 1.25f : zoom / 1.25f;
            if ((zoom < 1f && next > 1f) || (zoom > 1f && next < 1f)) next = 1f;
            return Math.Max(MinZoom, Math.Min(MaxZoom, next));
        }

        private void ZoomAt(float zoom, Point anchor)
        {
            zoom = Math.Max(MinZoom, Math.Min(MaxZoom, zoom));
            PointF img = ToImage(anchor);
            _zoom = zoom;
            _fit = false;
            _pan = new PointF(anchor.X - img.X * zoom, anchor.Y - img.Y * zoom);
            ClampPan();
            RaiseView();
        }

        // Снимок меньше окна — посередине; больше — прокручивается, но не уезжает за край дальше отступа.
        private void ClampPan()
        {
            if (_inline) { _pan = PointF.Empty; return; }
            int margin = S(24);
            float w = _doc.Size.Width * _zoom, h = _doc.Size.Height * _zoom;
            float x = _pan.X, y = _pan.Y;
            if (w + margin * 2 <= ClientSize.Width) x = (ClientSize.Width - w) / 2;
            else x = Math.Min(margin, Math.Max(ClientSize.Width - w - margin, x));
            if (h + margin * 2 <= ClientSize.Height) y = (ClientSize.Height - h) / 2;
            else y = Math.Min(margin, Math.Max(ClientSize.Height - h - margin, y));
            _pan = new PointF((float)Math.Round(x), (float)Math.Round(y));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (_doc == null) return;
            if (_fit) _zoom = FitZoom();
            ClampPan();
            RaiseView();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_inline) return;
            int notches = e.Delta / 120;
            if (notches == 0) notches = Math.Sign(e.Delta);
            if ((ModifierKeys & Keys.Control) != 0)
            {
                float z = _zoom;
                for (int i = 0; i < Math.Abs(notches); i++) z = NextZoom(z, Math.Sign(notches));
                ZoomAt(z, e.Location);
                return;
            }
            float step = S(60) * notches;
            if ((ModifierKeys & Keys.Shift) != 0) _pan = new PointF(_pan.X + step, _pan.Y);
            else _pan = new PointF(_pan.X, _pan.Y + step);
            ClampPan();
            RaiseView();
        }

        // ---------- слой изображения ----------

        // Фигура, которую сейчас тянут или правят, не входит в собранную картинку: её рисует холст поверх.
        private CapShape SkipShape
        {
            get
            {
                if ((_drag == Drag.Move || _drag == Drag.Handle) && _moved) return _selected;
                if (_editing != null && !_editingNew) return _editing;
                return null;
            }
        }

        private void EnsureComposite()
        {
            CapShape skip = SkipShape;
            if (_composite != null && _compositeVersion == _doc.Version && _compositeSkip == skip && _compositeImage == _doc.Image) return;
            if (_composite != null) _composite.Dispose();
            using (Bitmap render = _doc.Render(skip))
            {
                _composite = new Bitmap(render.Width, render.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(_composite))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(render, 0, 0);
                }
            }
            _compositeVersion = _doc.Version;
            _compositeSkip = skip;
            _compositeImage = _doc.Image;
            DisposeScaled();
        }

        private void DisposeScaled()
        {
            if (_scaled != null) { _scaled.Dispose(); _scaled = null; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            EnsureComposite();
            RectangleF img = ImageOnClient;
            if (_zoom < 1f)
            {
                // Уменьшенная копия считается один раз на масштаб: бикубика по кадру 4K на каждое движение мыши тормозит.
                if (_scaled == null || Math.Abs(_scaledZoom - _zoom) > 0.0001f)
                {
                    DisposeScaled();
                    int w = Math.Max(1, (int)Math.Round(_composite.Width * _zoom)), h = Math.Max(1, (int)Math.Round(_composite.Height * _zoom));
                    _scaled = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    using (Graphics sg = Graphics.FromImage(_scaled))
                    {
                        sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        sg.CompositingMode = CompositingMode.SourceCopy;
                        using (ImageAttributes clamp = new ImageAttributes())
                        {
                            clamp.SetWrapMode(WrapMode.TileFlipXY);
                            sg.DrawImage(_composite, new Rectangle(0, 0, w, h), 0, 0, _composite.Width, _composite.Height, GraphicsUnit.Pixel, clamp);
                        }
                    }
                    _scaledZoom = _zoom;
                }
                g.DrawImageUnscaled(_scaled, (int)_pan.X, (int)_pan.Y);
            }
            else
            {
                RectangleF clip = RectangleF.Intersect(e.ClipRectangle, img);
                if (clip.Width > 0 && clip.Height > 0)
                {
                    int x0 = Math.Max(0, (int)Math.Floor((clip.X - _pan.X) / _zoom));
                    int y0 = Math.Max(0, (int)Math.Floor((clip.Y - _pan.Y) / _zoom));
                    int x1 = Math.Min(_composite.Width, (int)Math.Ceiling((clip.Right - _pan.X) / _zoom));
                    int y1 = Math.Min(_composite.Height, (int)Math.Ceiling((clip.Bottom - _pan.Y) / _zoom));
                    if (x1 > x0 && y1 > y0)
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(_composite, new RectangleF(_pan.X + x0 * _zoom, _pan.Y + y0 * _zoom, (x1 - x0) * _zoom, (y1 - y0) * _zoom),
                                    new RectangleF(x0, y0, x1 - x0, y1 - y0), GraphicsUnit.Pixel);
                        g.PixelOffsetMode = PixelOffsetMode.Default;
                    }
                }
            }
            using (Pen frame = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawRectangle(frame, img.X - 1, img.Y - 1, img.Width + 1, img.Height + 1);

            // Живые фигуры — в координатах изображения.
            GraphicsState state = g.Save();
            g.TranslateTransform(_pan.X, _pan.Y);
            g.ScaleTransform(_zoom, _zoom);
            EditorDoc.Prepare(g);
            g.SetClip(new RectangleF(PointF.Empty, _doc.Size));
            CapShape skip = SkipShape;
            if (skip != null && skip != _editing) DrawLive(g, skip);
            if (_live != null) DrawLive(g, _live);
            g.Restore(state);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hover != null && _hover != _selected && _drag == Drag.None)
            {
                DrawOutline(g, _hover, Color.FromArgb(120, 255, 255, 255));
                if (_inline && _editing == null) DrawHandles(g, _hover);
            }
            if (_selected != null && _editing == null) DrawSelection(g, _selected);
            if (_style.Tool == EditTool.Crop) DrawCrop(g, img);
        }

        private void DrawLive(Graphics g, CapShape shape)
        {
            RedactShape redact = shape as RedactShape;
            if (redact == null) { shape.Draw(g); return; }
            Rectangle r = redact.PixelRect(_doc.Size);
            if (r.Width < 1 || r.Height < 1) return;
            // Предпросмотр по уже собранной картинке — тем же кодом, что при сохранении. Огромная область — только штриховкой.
            if ((long)r.Width * r.Height <= 1500000)
            {
                using (Bitmap piece = _composite.Clone(r, PixelFormat.Format32bppArgb))
                {
                    RedactShape local = (RedactShape)redact.Clone();
                    local.Rect = new RectangleF(0, 0, r.Width, r.Height);
                    local.Apply(piece);
                    g.DrawImage(piece, r);
                }
            }
            else
            {
                using (HatchBrush hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(120, 255, 255, 255), Color.FromArgb(120, 0, 0, 0)))
                    g.FillRectangle(hatch, r);
            }
        }

        private void DrawOutline(Graphics g, CapShape shape, Color color)
        {
            RectangleF b = ToClient(shape.Bounds);
            using (Pen p = new Pen(color, 1))
            {
                p.DashStyle = DashStyle.Dash;
                g.DrawRectangle(p, b.X, b.Y, b.Width, b.Height);
            }
        }

        private void DrawSelection(Graphics g, CapShape shape)
        {
            if (!(shape is LineShape)) DrawOutline(g, shape, Color.FromArgb(200, EditorColors.Accent));
            if (_style.Tool == EditTool.Select || _inline) DrawHandles(g, shape);
        }

        private void DrawHandles(Graphics g, CapShape shape)
        {
            float hs = S(4);
            foreach (PointF p in HandlesOf(shape))
            {
                RectangleF box = new RectangleF(p.X - hs, p.Y - hs, hs * 2, hs * 2);
                using (SolidBrush fill = new SolidBrush(Color.White)) g.FillEllipse(fill, box);
                using (Pen edge = new Pen(EditorColors.Accent, Math.Max(1f, S(1.5f)))) g.DrawEllipse(edge, box);
            }
        }

        private void DrawCrop(Graphics g, RectangleF img)
        {
            if (_crop.IsEmpty) return;
            RectangleF cr = ToClient(_crop);
            using (Region outside = new Region(img))
            {
                outside.Exclude(cr);
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRegion(shade, outside);
            }
            using (Pen thirds = new Pen(Color.FromArgb(60, 255, 255, 255), 1))
                for (int i = 1; i < 3; i++)
                {
                    g.DrawLine(thirds, cr.X + cr.Width * i / 3, cr.Y, cr.X + cr.Width * i / 3, cr.Bottom);
                    g.DrawLine(thirds, cr.X, cr.Y + cr.Height * i / 3, cr.Right, cr.Y + cr.Height * i / 3);
                }
            using (Pen border = new Pen(Color.White, Math.Max(1f, S(1.5f)))) g.DrawRectangle(border, cr.X, cr.Y, cr.Width, cr.Height);
            float hs = S(4);
            foreach (PointF p in EditGeometry.HandlePoints(cr))
            {
                using (SolidBrush fill = new SolidBrush(Color.White)) g.FillRectangle(fill, p.X - hs, p.Y - hs, hs * 2, hs * 2);
                using (Pen edge = new Pen(Color.FromArgb(40, 40, 40), 1)) g.DrawRectangle(edge, p.X - hs, p.Y - hs, hs * 2, hs * 2);
            }
            string label = _crop.Width.ToString(CultureInfo.InvariantCulture) + " × " + _crop.Height.ToString(CultureInfo.InvariantCulture);
            Size sz = TextRenderer.MeasureText(label, _labelFont);
            Rectangle lr = new Rectangle((int)cr.X, (int)cr.Y - sz.Height - S(8), sz.Width + S(12), sz.Height + S(4));
            if (lr.Y < 0) lr.Y = (int)cr.Y + S(6);
            using (SolidBrush back = new SolidBrush(Color.FromArgb(200, 20, 20, 20))) g.FillRectangle(back, lr);
            TextRenderer.DrawText(g, label, _labelFont, lr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ---------- фигуры под курсором и ручки ----------

        private CapShape ShapeAt(PointF image)
        {
            float tolerance = S(5) / _zoom;
            List<CapShape> shapes = _doc.Shapes;
            for (int i = shapes.Count - 1; i >= 0; i--)
                if (shapes[i].HitTest(image, tolerance)) return shapes[i];
            return null;
        }

        private List<PointF> HandlesOf(CapShape shape)
        {
            List<PointF> list = new List<PointF>();
            LineShape line = shape as LineShape;
            if (line != null)
            {
                list.Add(ToClient(line.A));
                list.Add(ToClient(line.B));
                return list;
            }
            RectangleF rect;
            if (RectOf(shape, out rect)) list.AddRange(EditGeometry.HandlePoints(ToClient(rect)));
            return list;
        }

        private static bool RectOf(CapShape shape, out RectangleF rect)
        {
            BoxShape box = shape as BoxShape;
            if (box != null) { rect = box.Rect; return true; }
            RedactShape redact = shape as RedactShape;
            if (redact != null) { rect = redact.Rect; return true; }
            rect = RectangleF.Empty;
            return false;
        }

        private static void SetRect(CapShape shape, RectangleF rect)
        {
            BoxShape box = shape as BoxShape;
            if (box != null) box.Rect = rect;
            RedactShape redact = shape as RedactShape;
            if (redact != null) redact.Rect = rect;
        }

        private int HandleAt(Point client)
        {
            CapShape owner;
            return HandleAt(client, out owner);
        }

        // owner — фигура, чью ручку взяли: выделенная, а на холсте оверлея — и любая другая, сверху вниз.
        private int HandleAt(Point client, out CapShape owner)
        {
            owner = null;
            // В окне редактора ручки берёт только «Выбор»: рисующим инструментом следующая фигура, начатая вплотную к углу
            // предыдущей, при мелком масштабе растягивала бы предыдущую вместо новой.
            if (_style.Tool != EditTool.Select && !_inline) return -1;
            List<CapShape> candidates = new List<CapShape>();
            if (_selected != null) candidates.Add(_selected);
            if (_inline)
                for (int i = _doc.Shapes.Count - 1; i >= 0; i--)
                    if (_doc.Shapes[i] != _selected) candidates.Add(_doc.Shapes[i]);
            foreach (CapShape shape in candidates)
            {
                List<PointF> handles = HandlesOf(shape);
                for (int i = handles.Count - 1; i >= 0; i--)
                    if (EditGeometry.Distance(handles[i], client) <= S(7)) { owner = shape; return i; }
            }
            return -1;
        }

        private int CropHandleAt(Point client)
        {
            if (_crop.IsEmpty) return -1;
            PointF[] handles = EditGeometry.HandlePoints(ToClient(_crop));
            for (int i = 0; i < handles.Length; i++)
                if (EditGeometry.Distance(handles[i], client) <= S(8)) return i;
            return -1;
        }

        private static Cursor HandleCursor(int handle)
        {
            switch (handle)
            {
                case 0: case 4: return Cursors.SizeNWSE;
                case 2: case 6: return Cursors.SizeNESW;
                case 1: case 5: return Cursors.SizeNS;
                default: return Cursors.SizeWE;
            }
        }

        private void UpdateCursor(Point client)
        {
            if (_drag == Drag.Pan || _space) { Cursor = _drag == Drag.Pan ? Cursors.SizeAll : Cursors.Hand; return; }
            PointF image = ToImage(client);
            if (_style.Tool == EditTool.Crop)
            {
                int ch = CropHandleAt(client);
                if (ch >= 0) Cursor = HandleCursor(ch);
                else Cursor = !_crop.IsEmpty && ToClient(_crop).Contains(client) ? Cursors.SizeAll : Cursors.Cross;
                return;
            }
            CapShape owner;
            int h = HandleAt(client, out owner);
            if (h >= 0) { Cursor = owner is LineShape ? Cursors.SizeAll : HandleCursor(h); return; }
            switch (_style.Tool)
            {
                case EditTool.Select: Cursor = ShapeAt(image) != null ? Cursors.SizeAll : Cursors.Default; break;
                case EditTool.Text: Cursor = Cursors.IBeam; break;
                default: Cursor = Cursors.Cross; break;
            }
        }

        // ---------- мышь ----------

        private StyleSnapshot Current { get { return new StyleSnapshot(_style); } }

        private struct StyleSnapshot
        {
            public readonly Color Color;
            public readonly float Width, FontSize;
            public readonly bool Fill;

            public StyleSnapshot(EditorStyle s)
            {
                Color = s.Color;
                Width = s.Width;
                FontSize = s.FontSize;
                Fill = s.Fill;
            }
        }

        private void ApplyStyleTo(CapShape shape)
        {
            StyleSnapshot st = Current;
            shape.Color = st.Color;
            shape.Width = st.Width;
            shape.Fill = st.Fill;
            TextShape text = shape as TextShape;
            if (text != null) text.FontSize = st.FontSize;
        }

        private CapShape NewShape(EditTool tool, PointF at)
        {
            CapShape shape;
            switch (tool)
            {
                case EditTool.Pen:
                case EditTool.Marker:
                {
                    StrokeShape s = new StrokeShape();
                    s.Marker = tool == EditTool.Marker;
                    s.Points.Add(at);
                    shape = s;
                    break;
                }
                case EditTool.Line:
                case EditTool.Arrow:
                {
                    LineShape s = new LineShape();
                    s.Arrow = tool == EditTool.Arrow;
                    s.A = at;
                    s.B = at;
                    shape = s;
                    break;
                }
                case EditTool.Rect:
                case EditTool.Ellipse:
                {
                    BoxShape s = new BoxShape();
                    s.Ellipse = tool == EditTool.Ellipse;
                    s.Rect = new RectangleF(at, SizeF.Empty);
                    shape = s;
                    break;
                }
                case EditTool.Blur:
                case EditTool.Pixelate:
                {
                    RedactShape s = new RedactShape();
                    s.Pixelate = tool == EditTool.Pixelate;
                    s.Rect = new RectangleF(at, SizeF.Empty);
                    shape = s;
                    break;
                }
                default: return null;
            }
            ApplyStyleTo(shape);
            return shape;
        }

        internal static PointF Snap45(PointF from, PointF to)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            return new PointF((float)(from.X + Math.Cos(angle) * len), (float)(from.Y + Math.Sin(angle) * len));
        }

        internal static PointF SquareCorner(PointF from, PointF to)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new PointF(from.X + (dx < 0 ? -side : side), from.Y + (dy < 0 ? -side : side));
        }

        private PointF ClampToImage(PointF p)
        {
            return new PointF(Math.Max(0, Math.Min(_doc.Size.Width, p.X)), Math.Max(0, Math.Min(_doc.Size.Height, p.Y)));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_textBox != null) CommitText();
            if (!Focused && WindowActive) Focus();
            PointF image = ToImage(e.Location);
            _downClient = e.Location;
            if (_drag != Drag.None) return;
            if (!_inline && (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && _space)))
            {
                _drag = Drag.Pan;
                _panMouse = e.Location;
                _panStart = _pan;
                Capture = true;
                UpdateCursor(e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            Capture = true;
            if (_style.Tool == EditTool.Crop) { CropDown(e, image); return; }

            CapShape owner;
            int handle = HandleAt(e.Location, out owner);
            if (handle >= 0) { _selected = owner; BeginEdit(Drag.Handle, image, handle); return; }
            switch (_style.Tool)
            {
                case EditTool.Select:
                {
                    CapShape hit = ShapeAt(image);
                    _selected = hit;
                    if (hit != null)
                    {
                        TextShape text = hit as TextShape;
                        if (e.Clicks >= 2 && text != null) { Capture = false; BeginText(text, PointF.Empty); return; }
                        BeginEdit(Drag.Move, image, -1);
                    }
                    RaiseChanged();
                    return;
                }
                case EditTool.Text:
                {
                    Capture = false;
                    BeginText(ShapeAt(image) as TextShape, image);
                    return;
                }
                case EditTool.Step:
                {
                    Capture = false;
                    StepShape step = new StepShape();
                    ApplyStyleTo(step);
                    step.Center = image;
                    EditSnapshot before = _doc.Capture();
                    step.Number = _doc.NextStep;
                    _doc.Shapes.Add(step);
                    _doc.NextStep++;
                    _doc.Commit(before);
                    _selected = step;
                    RaiseChanged();
                    return;
                }
            }
            _selected = null;
            _live = NewShape(_style.Tool, image);
            if (_live == null) { Capture = false; return; }
            _drag = Drag.Draw;
            _dragStart = image;
            Invalidate();
        }

        private void BeginEdit(Drag drag, PointF image, int handle)
        {
            _before = _doc.Capture();
            _drag = drag;
            _dragStart = image;
            _lastImage = image;
            _handle = handle;
            _moved = false;
            RectOf(_selected, out _startRect);
            LineShape line = _selected as LineShape;
            if (line != null) { _startA = line.A; _startB = line.B; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            PointF image = ToImage(e.Location);
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            switch (_drag)
            {
                case Drag.Pan:
                    _pan = new PointF(_panStart.X + e.X - _panMouse.X, _panStart.Y + e.Y - _panMouse.Y);
                    ClampPan();
                    RaiseView();
                    return;
                case Drag.Draw:
                    UpdateLive(image, shift);
                    Invalidate();
                    return;
                case Drag.Move:
                    if (!_moved && EditGeometry.Distance(e.Location, _downClient) < S(3)) return;
                    _moved = true;
                    _selected.Offset(image.X - _lastImage.X, image.Y - _lastImage.Y);
                    _lastImage = image;
                    Invalidate();
                    return;
                case Drag.Handle:
                    _moved = true;
                    ResizeSelected(image, shift);
                    Invalidate();
                    return;
                case Drag.CropNew:
                case Drag.CropMove:
                case Drag.CropHandle:
                    CropDrag(image, shift);
                    RaiseChanged();
                    return;
            }
            CapShape hover = null;
            if (_inline) HandleAt(e.Location, out hover);
            if (hover == null && (_style.Tool == EditTool.Select || _inline)) hover = ShapeAt(image);
            if (hover != _hover) { _hover = hover; Invalidate(); }
            UpdateCursor(e.Location);
        }

        private void UpdateLive(PointF image, bool shift)
        {
            StrokeShape stroke = _live as StrokeShape;
            if (stroke != null)
            {
                if (shift)
                {
                    PointF first = stroke.Points[0];
                    stroke.Points.Clear();
                    stroke.Points.Add(first);
                    stroke.Points.Add(image);
                }
                else if (EditGeometry.Distance(stroke.Points[stroke.Points.Count - 1], image) >= 1.5f / _zoom) stroke.Points.Add(image);
                return;
            }
            LineShape line = _live as LineShape;
            if (line != null)
            {
                line.B = shift ? Snap45(line.A, image) : image;
                return;
            }
            PointF corner = shift ? SquareCorner(_dragStart, image) : image;
            RectangleF rect = EditGeometry.Normalize(_dragStart, corner);
            if (_live is RedactShape) rect = RectangleF.Intersect(rect, new RectangleF(PointF.Empty, _doc.Size));
            SetRect(_live, rect);
        }

        private void ResizeSelected(PointF image, bool shift)
        {
            float dx = image.X - _dragStart.X, dy = image.Y - _dragStart.Y;
            LineShape line = _selected as LineShape;
            if (line != null)
            {
                if (_handle == 0)
                {
                    PointF a = new PointF(_startA.X + dx, _startA.Y + dy);
                    line.A = shift ? Snap45(line.B, a) : a;
                }
                else
                {
                    PointF b = new PointF(_startB.X + dx, _startB.Y + dy);
                    line.B = shift ? Snap45(line.A, b) : b;
                }
                return;
            }
            SetRect(_selected, EditGeometry.Resize(_startRect, _handle, dx, dy));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Drag drag = _drag;
            if (drag == Drag.None) return;
            if (drag == Drag.Pan ? e.Button != MouseButtons.Middle && e.Button != MouseButtons.Left : e.Button != MouseButtons.Left) return;
            _drag = Drag.None;
            Capture = false;
            switch (drag)
            {
                case Drag.Draw: FinishLive(); break;
                case Drag.Move:
                case Drag.Handle:
                    // Рамку, сжатую ручкой до черты, не оставляем: невидимая фигура только мешала бы попадать мышью.
                    if (_moved && drag == Drag.Handle && TooSmall(_selected)) { _doc.Revert(_before); _selected = null; _moved = false; }
                    if (_moved) _doc.Commit(_before);
                    _before = null;
                    _moved = false;
                    RaiseChanged();
                    break;
                case Drag.CropNew:
                case Drag.CropMove:
                case Drag.CropHandle:
                    if (_crop.Width < 2 || _crop.Height < 2) _crop = Rectangle.Empty;
                    RaiseChanged();
                    break;
            }
            UpdateCursor(e.Location);
        }

        private void FinishLive()
        {
            CapShape shape = _live;
            _live = null;
            if (shape == null) return;
            if (!TooSmall(shape))
            {
                _doc.Add(shape);
                _selected = shape is StrokeShape ? null : shape;
            }
            // На холсте оверлея нет «Выбора»: щелчок без протягивания выделяет фигуру под курсором (ручки, Delete).
            else if (_inline) _selected = ShapeAt(_dragStart);
            RaiseChanged();
        }

        private bool TooSmall(CapShape shape)
        {
            float min = 2f / _zoom;
            LineShape line = shape as LineShape;
            if (line != null) return EditGeometry.Distance(line.A, line.B) < min;
            RectangleF rect;
            return RectOf(shape, out rect) && (rect.Width < min || rect.Height < min);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_style.Tool == EditTool.Crop && !_crop.IsEmpty && ToClient(_crop).Contains(e.Location)) ApplyCrop();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != null) { _hover = null; Invalidate(); }
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (_textBox == null && _drag != Drag.None && !Capture) CancelDrag();
        }

        // ---------- обрезка ----------

        private void CropDown(MouseEventArgs e, PointF image)
        {
            if (!_crop.IsEmpty)
            {
                int h = CropHandleAt(e.Location);
                if (h >= 0)
                {
                    _drag = Drag.CropHandle;
                    _handle = h;
                    _cropStart = _crop;
                    _dragStart = image;
                    return;
                }
                if (ToClient(_crop).Contains(e.Location))
                {
                    if (e.Clicks >= 2) { Capture = false; return; }
                    _drag = Drag.CropMove;
                    _cropStart = _crop;
                    _dragStart = image;
                    return;
                }
            }
            _drag = Drag.CropNew;
            _dragStart = ClampToImage(image);
            _crop = Rectangle.Empty;
            RaiseChanged();
        }

        private void CropDrag(PointF image, bool shift)
        {
            Rectangle bounds = new Rectangle(Point.Empty, _doc.Size);
            switch (_drag)
            {
                case Drag.CropNew:
                {
                    PointF p = ClampToImage(shift ? SquareCorner(_dragStart, image) : image);
                    _crop = Round(EditGeometry.Normalize(_dragStart, p), bounds);
                    break;
                }
                case Drag.CropMove:
                {
                    int x = _cropStart.X + (int)Math.Round(image.X - _dragStart.X);
                    int y = _cropStart.Y + (int)Math.Round(image.Y - _dragStart.Y);
                    x = Math.Max(0, Math.Min(bounds.Width - _cropStart.Width, x));
                    y = Math.Max(0, Math.Min(bounds.Height - _cropStart.Height, y));
                    _crop = new Rectangle(x, y, _cropStart.Width, _cropStart.Height);
                    break;
                }
                case Drag.CropHandle:
                    _crop = Round(EditGeometry.Resize(_cropStart, _handle, image.X - _dragStart.X, image.Y - _dragStart.Y), bounds);
                    break;
            }
        }

        private static Rectangle Round(RectangleF r, Rectangle bounds)
        {
            Rectangle ri = Rectangle.FromLTRB((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
            return Rectangle.Intersect(ri, bounds);
        }

        public void ApplyCrop()
        {
            if (_crop.Width < 1 || _crop.Height < 1) return;
            Rectangle area = _crop;
            _crop = Rectangle.Empty;
            _selected = null;
            _hover = null;
            if (_doc.Crop(area))
            {
                FitView();
                if (CropApplied != null) CropApplied();
            }
            RaiseChanged();
        }

        // ---------- текст ----------

        private void BeginText(TextShape existing, PointF at)
        {
            CommitText();
            _textBefore = _doc.Capture();
            _textDirty = false;
            if (existing != null)
            {
                _editing = existing;
                _editingNew = false;
                _selected = existing;
            }
            else
            {
                _editing = new TextShape();
                ApplyStyleTo(_editing);
                _editing.Origin = at;
                _editingNew = true;
                _selected = null;
            }
            TextBox box = new TextBox();
            box.Multiline = true;
            box.AcceptsReturn = true;
            box.WordWrap = false;
            box.BorderStyle = BorderStyle.None;
            box.ScrollBars = ScrollBars.None;
            box.Text = existing != null ? existing.Text.Replace("\n", "\r\n") : "";
            box.KeyDown += TextKeyDown;
            box.TextChanged += delegate { PlaceTextBox(); };
            box.Leave += delegate { if (IsHandleCreated && !IsDisposed) BeginInvoke((MethodInvoker)delegate { if (_textBox == box) CommitText(); }); };
            _textBox = box;
            StyleTextBox();
            PlaceTextBox();
            Controls.Add(box);
            if (WindowActive) box.Focus();
            box.SelectAll();
            RaiseChanged();
        }

        private void TextKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CancelText();
            }
            else if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CommitText();
            }
        }

        private void StyleTextBox()
        {
            if (_textBox == null) return;
            Color c = _editing.Color;
            if (_editing.Fill)
            {
                _textBox.BackColor = c;
                _textBox.ForeColor = CapShape.Contrast(c);
            }
            else
            {
                _textBox.ForeColor = c;
                _textBox.BackColor = CapShape.Contrast(c) == Color.Black ? Color.FromArgb(34, 34, 34) : Color.FromArgb(236, 236, 236);
            }
        }

        private void PlaceTextBox()
        {
            if (_textBox == null) return;
            float px = Math.Max(6f, _editing.FontSize * _zoom);
            if (_textFont == null || Math.Abs(_textFont.Size - px) > 0.01f)
            {
                Font old = _textFont;
                _textFont = new Font(EditorFonts.Family, px, FontStyle.Bold, GraphicsUnit.Pixel);
                _textBox.Font = _textFont;
                if (old != null) old.Dispose();
            }
            Size text = TextRenderer.MeasureText(_textBox.Text.Length == 0 ? "W" : _textBox.Text + "W", _textFont);
            PointF at = ToClient(_editing.Origin);
            _textBox.Bounds = new Rectangle((int)at.X, (int)at.Y, Math.Max(S(40), text.Width + S(6)), text.Height + S(4));
        }

        public void CommitText()
        {
            if (_textBox == null) return;
            TextBox box = _textBox;
            TextShape shape = _editing;
            string text = box.Text.Replace("\r\n", "\n").TrimEnd();
            CloseTextBox();
            if (_editingNew)
            {
                if (text.Trim().Length > 0)
                {
                    shape.Text = text;
                    _doc.Shapes.Add(shape);
                    _doc.Commit(_textBefore);
                    _selected = shape;
                }
            }
            else if (text.Trim().Length == 0)
            {
                _doc.Shapes.Remove(shape);
                _doc.Commit(_textBefore);
                _selected = null;
            }
            else if (text != shape.Text || _textDirty)
            {
                shape.Text = text;
                _doc.Commit(_textBefore);
            }
            _textBefore = null;
            RaiseChanged();
        }

        private void CancelText()
        {
            if (_textBox == null) return;
            bool revert = !_editingNew && _textDirty;
            CloseTextBox();
            if (revert) { _doc.Revert(_textBefore); _selected = null; }
            _textBefore = null;
            RaiseChanged();
        }

        private void CloseTextBox()
        {
            TextBox box = _textBox;
            _textBox = null;
            _editing = null;
            bool hadFocus = box.Focused;
            Controls.Remove(box);
            box.Dispose();
            if (hadFocus && CanFocus) Focus();
        }

        // ---------- команды окна ----------

        public void ToolChanged()
        {
            CommitText();
            if (_style.Tool != EditTool.Crop) _crop = Rectangle.Empty;
            else _selected = null;
            _hover = null;
            UpdateCursor(PointToClient(Cursor.Position));
            RaiseChanged();
        }

        private static bool Applies(CapShape shape, StyleField f)
        {
            switch (f)
            {
                case StyleField.Color: return !(shape is RedactShape);
                case StyleField.Width: return shape is StrokeShape || shape is LineShape || shape is BoxShape || shape is StepShape;
                case StyleField.Fill: return shape is BoxShape || shape is TextShape;
                case StyleField.FontSize: return shape is TextShape;
            }
            return false;
        }

        private void Assign(CapShape shape, StyleField f)
        {
            switch (f)
            {
                case StyleField.Color: shape.Color = _style.Color; break;
                case StyleField.Width: shape.Width = _style.Width; break;
                case StyleField.Fill: shape.Fill = _style.Fill; break;
                case StyleField.FontSize: ((TextShape)shape).FontSize = _style.FontSize; break;
            }
        }

        // Смена цвета, толщины, заливки или размера текста меняет и выделенную фигуру (одним шагом отмены).
        public void ApplyStyleField(StyleField f)
        {
            if (_editing != null)
            {
                if (!Applies(_editing, f)) return;
                Assign(_editing, f);
                _textDirty = true;
                StyleTextBox();
                PlaceTextBox();
                return;
            }
            CapShape shape = _selected;
            if (shape == null || !Applies(shape, f)) return;
            _doc.Change(delegate { Assign(shape, f); });
            RaiseChanged();
        }

        public void DeleteSelected()
        {
            if (_selected == null) return;
            _doc.Remove(_selected);
            _selected = null;
            _hover = null;
            RaiseChanged();
        }

        public void Nudge(int dx, int dy)
        {
            if (_selected == null || _drag != Drag.None) return;
            CapShape shape = _selected;
            _doc.Change(delegate { shape.Offset(dx, dy); });
            RaiseChanged();
        }

        public void Escape()
        {
            if (_drag != Drag.None) { CancelDrag(); return; }
            if (_style.Tool == EditTool.Crop && !_crop.IsEmpty) { _crop = Rectangle.Empty; RaiseChanged(); return; }
            if (_selected != null) { _selected = null; RaiseChanged(); }
        }

        public void EnterPressed()
        {
            if (_style.Tool == EditTool.Crop) ApplyCrop();
        }

        private void CancelDrag()
        {
            Drag drag = _drag;
            _drag = Drag.None;
            Capture = false;
            switch (drag)
            {
                case Drag.Draw: _live = null; break;
                case Drag.Move:
                case Drag.Handle:
                    if (_moved) { _doc.Revert(_before); _selected = null; }
                    _before = null;
                    _moved = false;
                    break;
                case Drag.CropNew:
                case Drag.CropMove:
                case Drag.CropHandle:
                    _crop = drag == Drag.CropNew ? Rectangle.Empty : _cropStart;
                    break;
            }
            RaiseChanged();
        }

        private void AfterHistory(Size before)
        {
            _selected = null;
            _hover = null;
            _crop = Rectangle.Empty;
            if (_doc.Size != before) FitView();
            RaiseChanged();
        }

        public void Undo()
        {
            CommitText();
            if (_drag != Drag.None) CancelDrag();
            Size size = _doc.Size;
            if (_doc.Undo()) AfterHistory(size);
        }

        public void Redo()
        {
            CommitText();
            if (_drag != Drag.None) CancelDrag();
            Size size = _doc.Size;
            if (_doc.Redo()) AfterHistory(size);
        }

        public void Rotate(bool clockwise)
        {
            CommitText();
            if (_drag != Drag.None) CancelDrag();
            _doc.Rotate(clockwise);
            _crop = Rectangle.Empty;
            FitView();
            RaiseChanged();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_composite != null) _composite.Dispose();
                DisposeScaled();
                if (_textFont != null) _textFont.Dispose();
                if (_labelFont != null) _labelFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
