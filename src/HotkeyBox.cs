// SysDeck — поле, в котором сочетание клавиш задаётся нажатием (страница «Захват»).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Capture;

namespace SysDeck
{
    // Поле, в котором сочетание задаётся нажатием. Пока поле в фокусе, агент снимает свои клавиши (иначе F3 в поле
    // открыло бы выделение области), а Alt+F4 и прочие системные сочетания до окна не доходят.
    internal sealed class HotkeyBox : TextBox
    {
        public event Action<HotkeySpec> HotkeyPicked;
        public event EventHandler CaptureStarted, CaptureEnded;

        private HotkeySpec _spec;
        private bool _capturing;

        public HotkeyBox()
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
            TabStop = false;        // фокус при открытии страницы снял бы клавиши агента
            Cursor = Cursors.Hand;
            TextAlign = HorizontalAlignment.Center;
        }

        public HotkeySpec Spec
        {
            get { return _spec; }
            set { _spec = value; if (!_capturing) ShowSpec(); }
        }

        private void ShowSpec() { Text = _spec.IsEmpty ? "—" : _spec.ToString(); }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            _capturing = true;
            Text = Tr.S("нажмите сочетание…", "press a shortcut…");
            EventHandler h = CaptureStarted;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            EndCapture();
        }

        private void EndCapture()
        {
            if (!_capturing) return;
            _capturing = false;
            ShowSpec();
            EventHandler h = CaptureEnded;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!_capturing) return base.ProcessCmdKey(ref msg, keyData);
            Keys key = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;
            if (key == Keys.Tab && (mods & (Keys.Control | Keys.Alt)) == 0) return base.ProcessCmdKey(ref msg, keyData);
            if (mods == Keys.None && key == Keys.Escape) { Finish(); return true; }
            if (mods == Keys.None && (key == Keys.Back || key == Keys.Delete)) { Pick(new HotkeySpec()); return true; }
            bool win = (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;
            HotkeySpec spec = HotkeySpec.FromKeys(keyData, win);
            if (!spec.IsEmpty && HotkeySpec.IsKnownKey(spec.Vk)) Pick(spec);
            return true;    // модификатор без клавиши и системные сочетания окну не отдаются
        }

        // PrtScn приходит только отпусканием: нажатие забирает система.
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (_capturing && e.KeyCode == Keys.PrintScreen)
            {
                bool win = (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;
                Pick(HotkeySpec.FromKeys(e.KeyData, win));
            }
        }

        private void Pick(HotkeySpec spec)
        {
            _spec = spec;
            Action<HotkeySpec> h = HotkeyPicked;
            if (h != null) h(spec);
            Finish();
        }

        // Фокус уходит с поля — захват окончен, агент возвращает клавиши.
        private void Finish()
        {
            EndCapture();
            Form f = FindForm();
            if (f != null) f.ActiveControl = null;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetKeyState(int vk);
    }
}
