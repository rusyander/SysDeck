using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace WpcSetup
{
    // Подтверждение удаления. Отдельное окно, а не MessageBox, из-за флажка: настройки и
    // история — не то, что можно удалить молча вместе с программой.
    internal sealed class UninstallDialog : Form
    {
        private readonly CheckBox _data;

        public bool DeleteData { get { return _data.Checked; } }

        public UninstallDialog(string dir)
        {
            Text = L.S("Удаление Windows Process Cleaner", "Uninstall Windows Process Cleaner");
            Font = Ui.Base;
            BackColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Ui.Px(540), Ui.Px(320));
            Ui.SetIcon(this);

            int pad = Ui.Px(22);
            int w = ClientSize.Width - pad * 2;

            Label title = Ui.Text(this, L.S("Удалить Windows Process Cleaner?", "Remove Windows Process Cleaner?"),
                                  pad, Ui.Px(20), w, false);
            title.Font = Ui.Head;
            title.Size = new Size(w, Ui.Px(28));

            Ui.Note(this, L.S("Будут удалены программа, её ярлыки, задача автозапуска и запись в списке установленных программ:",
                              "The program, its shortcuts, the autostart task and the installed-programs entry will be removed:"),
                    pad, Ui.Px(56), w, 2);
            Ui.Text(this, dir, pad, Ui.Px(96), w, false).Font = Ui.Strong;

            _data = Ui.Check(this, L.S("Удалить также настройки и историю", "Remove settings and history as well"),
                             pad, Ui.Px(126), w);
            _data.Checked = false;

            // Приложение обещает пользователю, что запомненный выбор живёт до полного
            // удаления программы, — здесь это обещание и заканчивается, поэтому текст
            // говорит прямо, что именно исчезнет и где оно лежало.
            Ui.Note(this,
                L.S("Запомненный выбор, настройки и история лежат в %APPDATA%\\WindowsProcessCleaner и сохраняются до полного удаления программы. Оставьте флажок снятым — они переживут удаление, и после новой установки всё вернётся как было. Поставьте — исчезнут вместе с программой.",
                    "Remembered selections, settings and history live in %APPDATA%\\WindowsProcessCleaner and are kept until the program is fully uninstalled. Leave the box unticked and they survive, so a new installation picks up exactly where you left off. Tick it and they go away with the program."),
                pad + Ui.Px(22), Ui.Px(152), w - Ui.Px(22), 4);

            int bw = Ui.Px(118);
            Button cancel = Ui.MakeButton(this, L.S("Отмена", "Cancel"), bw);
            cancel.Location = new Point(ClientSize.Width - Ui.Px(20) - bw, ClientSize.Height - Ui.Px(48));
            cancel.DialogResult = DialogResult.Cancel;

            Button ok = Ui.MakeButton(this, L.S("Удалить", "Remove"), bw);
            ok.Location = new Point(cancel.Left - Ui.Px(10) - bw, cancel.Top);
            ok.DialogResult = DialogResult.OK;

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    // Окно хода работы для удаления: те же журнал и вопросы, что у мастера, но без страниц.
    internal sealed class WorkForm : Form, IWorkHost
    {
        private readonly Action<WorkContext> _job;
        private readonly WorkContext _context;
        private readonly Label _status;
        private readonly ProgressBar _bar;
        private readonly TextBox _log;

        public WorkForm(string caption, string heading, Action<WorkContext> job, WorkContext context)
        {
            _job = job;
            _context = context;
            _context.Host = this;

            Text = caption;
            Font = Ui.Base;
            BackColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Ui.Px(560), Ui.Px(300));
            Ui.SetIcon(this);

            int pad = Ui.Px(22);
            int w = ClientSize.Width - pad * 2;

            _status = Ui.Text(this, heading, pad, pad, w, false);

            _bar = new ProgressBar();
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;
            _bar.Location = new Point(pad, pad + Ui.Px(26));
            _bar.Size = new Size(w, Ui.Px(12));
            Controls.Add(_bar);

            _log = Ui.Log(this, pad, pad + Ui.Px(50), w, ClientSize.Height - pad * 2 - Ui.Px(50));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private void Run()
        {
            string error = null;
            bool cancelled = false;
            try { _job(_context); }
            catch (SetupCancelled) { cancelled = true; }
            catch (Exception ex) { error = ex.Message; }

            string err = error;
            bool can = cancelled;
            try { BeginInvoke((MethodInvoker)delegate { Done(err, can); }); }
            catch { }
        }

        private void Done(string error, bool cancelled)
        {
            _bar.MarqueeAnimationSpeed = 0;
            if (error != null)
                MessageBox.Show(this, error, L.S("Ошибка", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);

            DialogResult = error == null && !cancelled ? DialogResult.OK : DialogResult.Cancel;
            Close();
        }

        public void Log(string line)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _log.AppendText(line + "\r\n");
                    if (line.Length > 0 && line[0] != ' ') _status.Text = line;
                });
            }
            catch { }
        }

        public DialogResult Ask(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            if (InvokeRequired)
                return (DialogResult)Invoke((Func<DialogResult>)delegate { return Ask(text, caption, buttons, icon); });
            return MessageBox.Show(this, text, caption, buttons, icon);
        }
    }
}
