// SysDeck — «Захват»: окно редактора снимка — панель инструментов, холст, сохранение.
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

namespace SysDeck.Capture
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
}
