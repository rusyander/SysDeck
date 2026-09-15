// SysDeck — вкладка «Docker»: prune-команды и сжатие диска
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysDeck
{
    public partial class MainForm
    {
        // ---------- Вкладка: Docker ----------
        private Control BuildDockerTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Top;
            // Высота считается по содержимому: кнопки теперь шире (см. AddDockerButton),
            // в строку их влезает меньше, и фиксированные 130 px срезали бы последний ряд.
            flow.AutoSize = true;
            flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _dockerFlow = flow;

            AddDockerButton(flow, Tr.S("Обзор занятого места", "Disk usage (df)"), "system df", false);
            AddDockerButton(flow, Tr.S("Подробно (df -v)", "Details (df -v)"), "system df -v", false);
            AddDockerButton(flow, Tr.S("Удалить остановленные контейнеры", "Remove stopped containers"), "container prune -f", true);
            AddDockerButton(flow, Tr.S("Удалить неиспользуемые образы", "Remove unused images"), "image prune -a -f", true);
            AddDockerButton(flow, Tr.S("Удалить неиспользуемые тома", "Remove unused volumes"), "volume prune -f", true);
            AddDockerButton(flow, Tr.S("Очистить кэш сборки", "Clear build cache"), "builder prune -a -f", true);
            AddDockerButton(flow, Tr.S("Полная очистка", "Full cleanup"), "system prune -a -f --volumes", true);

            Button bCompact = new RoundButton();
            bCompact.Text = Tr.S("★ Сжать диск Docker (вернуть место Windows)",
                                 "★ Compact Docker disk (reclaim Windows space)");
            bCompact.Width = 400; bCompact.Height = 38; bCompact.Margin = new Padding(4, 6, 4, 4);
            bCompact.Tag = "primary";
            bCompact.Click += delegate { DoCompactDocker(); };
            flow.Controls.Add(bCompact);

            // Кнопки «Стоп» у Docker не было вообще: и prune, и сжатие диска идут десятками
            // минут, а всё это время вкладка только показывала одну неподвижную строку.
            _btnDockerCancel = new RoundButton();
            _btnDockerCancel.Text = Tr.S("Стоп", "Stop");
            _btnDockerCancel.Width = 100; _btnDockerCancel.Height = 34;
            _btnDockerCancel.Margin = new Padding(4, 6, 4, 4);
            _btnDockerCancel.Enabled = false;
            _btnDockerCancel.Click += delegate
            {
                _engine.CancelDockerWork();
                _dockerStopping = true;
                DockerTick();
            };
            flow.Controls.Add(_btnDockerCancel);

            // Что удалить перед сжатием — выбор пользователя, по умолчанию только безопасное.
            Label lblPrune = new Label();
            lblPrune.Name = "muted";
            lblPrune.AutoSize = true;
            lblPrune.Text = Tr.S("перед сжатием удалить:", "before compacting remove:");
            lblPrune.Margin = new Padding(10, 16, 4, 4);
            flow.Controls.Add(lblPrune);
            _cmbDockerPrune = new RoundComboBox();
            _cmbDockerPrune.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDockerPrune.Width = 560; _cmbDockerPrune.Margin = new Padding(4, 10, 4, 4);
            _cmbDockerPrune.Items.AddRange(new object[] {
                Tr.S("ничего — только сжать", "nothing — compact only"),
                Tr.S("безопасное: остановленные контейнеры, образы без тега, кэш сборки", "safe: stopped containers, dangling images, build cache"),
                Tr.S("+ все неиспользуемые образы (скачаются заново)", "+ all unused images (re-downloaded when needed)"),
                Tr.S("всё, включая неиспользуемые тома (данные проектов!)", "everything, incl. unused volumes (project data!)") });
            // выбор «что удалить перед сжатием» переживает перезапуск; перед самим действием
            // всё равно показывается окно с точным перечнем команд
            int prune;
            if (!int.TryParse(MemGet("docker", "prune", true), out prune) || prune < 0 || prune > 3) prune = 1;
            _cmbDockerPrune.SelectedIndex = prune;
            _cmbDockerPrune.SelectedIndexChanged += delegate
            {
                MemSet("docker", "prune", _cmbDockerPrune.SelectedIndex.ToString(CultureInfo.InvariantCulture), true);
            };
            flow.Controls.Add(_cmbDockerPrune);

            Label note = new Label();
            note.Name = "muted";
            note.Dock = DockStyle.Top;
            note.Text = Tr.S(
                "Удаляется только НЕиспользуемое (prune): остановленные контейнеры, образы без тегов/ссылок, " +
                "тома без владельцев, кэш сборки. Запущенные контейнеры и используемые образы не трогаются.\r\n" +
                "⚠ prune освобождает место ВНУТРИ виртуального диска Docker, но сам файл на Windows не уменьшается. " +
                "Чтобы реально вернуть место на диске Windows — «Сжать диск Docker» (остановит Docker и сожмёт vhdx).\r\n" +
                "Kubernetes не включён: его очистка бьёт по живому кластеру.",
                "Only UNUSED data is removed (prune): stopped containers, dangling/unreferenced images, " +
                "unused volumes, build cache. Running containers and used images are never touched.\r\n" +
                "⚠ prune frees space INSIDE Docker's virtual disk, but the file on Windows doesn't shrink. " +
                "To actually reclaim Windows disk space use “Compact Docker disk” (stops Docker and compacts the vhdx).\r\n" +
                "Kubernetes is not included: its cleanup affects a live cluster.");
            note.Font = new Font(Font.FontFamily, 9.5F);
            // Текст переносится по ширине окна, поэтому и высота у него не постоянная:
            // при 58 px последняя строка (про Kubernetes) не попадала на экран вообще.
            EventHandler fitNote = delegate
            {
                int w = Math.Max(200, tab.ClientSize.Width - tab.Padding.Horizontal);
                note.Height = TextRenderer.MeasureText(note.Text, note.Font,
                                  new Size(w, 0), TextFormatFlags.WordBreak).Height + 6;
            };
            tab.Resize += fitNote;
            fitNote(null, EventArgs.Empty);

            _txtDocker = new RichTextBox();
            _txtDocker.Dock = DockStyle.Fill;
            _txtDocker.ReadOnly = true;
            _txtDocker.WordWrap = false;
            _txtDocker.BorderStyle = BorderStyle.FixedSingle;
            _txtDocker.Font = new Font("Consolas", 10F);
            _txtDocker.Text = Tr.S(
                "Нажмите «Обзор занятого места», чтобы увидеть, сколько занимает Docker.\r\n" +
                "Требуется установленный Docker CLI (Docker Desktop) и запущенный демон.",
                "Click “Disk usage (df)” to see how much space Docker uses.\r\n" +
                "Requires an installed Docker CLI (Docker Desktop) and a running daemon.");

            Panel outBox = MkBox(_txtDocker, new Padding(8, 6, 4, 6));
            outBox.Dock = DockStyle.Fill;
            tab.Controls.Add(outBox);
            tab.Controls.Add(note);
            tab.Controls.Add(flow);
            return tab;
        }

        private void AddDockerButton(FlowLayoutPanel flow, string title, string args, bool destructive)
        {
            Button b = new RoundButton();
            b.Text = title;
            // 230 px срезали хвост подписи без многоточия: «Удалить неиспользуемые образы»
            // и «…тома» выглядели одинаково — на кнопках, которые удаляют.
            b.Width = Math.Max(230, Unscaled(TextRenderer.MeasureText(title, Font).Width) + 28);
            b.Height = 34;
            b.Margin = new Padding(4);
            b.Click += delegate { RunDocker(title, args, destructive); };
            flow.Controls.Add(b);
        }

        // Второй клик во время выполнения запускал вторую docker-команду параллельно с первой.
        // Молчаливого «игнорируем клик» мало: кнопки оставались нажимаемыми, а в окне вывода
        // висела строка ПЕРВОЙ команды — и пользователь был уверен, что запустил вторую.
        // Поэтому на время работы вся панель кнопок гаснет, кроме «Стоп».
        private int _dockerBusy;
        private RoundComboBox _cmbDockerPrune;
        private FlowLayoutPanel _dockerFlow;
        private Button _btnDockerCancel;
        private System.Windows.Forms.Timer _dockerTick;
        private DateTime _dockerStarted;
        private string _dockerHeader;     // что вообще запущено — первая строка окна вывода
        private bool _dockerStopping;

        private void StartDockerTicker(string header)
        {
            _dockerHeader = header;
            _dockerStarted = DateTime.UtcNow;
            if (_dockerTick == null)
            {
                _dockerTick = new System.Windows.Forms.Timer();
                _dockerTick.Interval = 500;
                _dockerTick.Tick += delegate { DockerTick(); };
            }
            _dockerTick.Start();
            DockerTick();
        }

        private void StopDockerTicker()
        {
            if (_dockerTick != null) _dockerTick.Stop();
        }

        // Секундомер плюс текущий шаг и последняя строка вывода docker/diskpart (Engine.DockerStatus).
        private void DockerTick()
        {
            if (_dockerBusy == 0) { StopDockerTicker(); return; }
            string s = _engine.DockerStatus;
            _txtDocker.Text = _dockerHeader + "\r\n"
                + Tr.S("идёт: ", "elapsed: ") + Elapsed(DateTime.UtcNow - _dockerStarted)
                + (string.IsNullOrEmpty(s) ? "" : "\r\n" + s)
                + (_dockerStopping
                   ? "\r\n" + Tr.S("Останавливаю на ближайшей безопасной границе. Уже начатое сжатие диска прервать нельзя — его придётся дождаться, иначе виртуальный диск останется подключённым.",
                                   "Stopping at the nearest safe point. A compaction already under way cannot be interrupted — it has to finish, otherwise the virtual disk stays attached.")
                   : "");
        }

        private void SetDockerUiBusy(bool busy)
        {
            if (_dockerFlow != null)
                foreach (Control c in _dockerFlow.Controls)
                    if (!ReferenceEquals(c, _btnDockerCancel)) c.Enabled = !busy;
            if (_btnDockerCancel != null) _btnDockerCancel.Enabled = busy;
        }

        private static bool TouchesVolumes(string args)
        {
            return args.IndexOf("volume", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string VolumeWarning()
        {
            return Tr.S("\r\n⚠ Неиспользуемые тома могут хранить базы данных остановленных проектов — вернуть их будет нельзя.",
                        "\r\n⚠ Unused volumes may hold databases of stopped projects — they cannot be recovered.");
        }

        private void RunDocker(string title, string args, bool destructive)
        {
            if (Interlocked.CompareExchange(ref _dockerBusy, 1, 0) != 0) return;
            if (destructive)
            {
                DialogResult dr = MessageBox.Show(this,
                    Tr.S("Выполнить: docker " + args + " ?\r\nБудут удалены неиспользуемые данные Docker.",
                         "Run: docker " + args + " ?\r\nUnused Docker data will be removed.")
                    + (TouchesVolumes(args) ? VolumeWarning() : ""),
                    "Docker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes) { Interlocked.Exchange(ref _dockerBusy, 0); return; }
            }
            _engine.ResetDockerCancel();
            _dockerStopping = false;
            Cursor = Cursors.WaitCursor;
            SetDockerUiBusy(true);
            // Большой prune идёт до получаса (столько ему теперь и отведено): без секундомера
            // и живого вывода это была одна строка «Выполняется: docker …» на всё время.
            StartDockerTicker(Tr.S("Выполняется: docker ", "Running: docker ") + args);
            string op = destructive ? "docker " + args : null;
            if (op != null) BeginWrite(op);
            Thread t = new Thread(delegate()
            {
                string res;
                // Без catch исключение в фоновом потоке валит всё приложение (crash.log, окно ошибки).
                try { res = _engine.Docker(args); }
                catch (Exception ex) { res = Tr.S("[ошибка] ", "[error] ") + ex.Message; }
                finally { if (op != null) EndWrite(op); }
                UiPost(delegate
                {
                    Cursor = Cursors.Default;
                    StopDockerTicker();
                    SetDockerUiBusy(false);
                    _dockerStopping = false;
                    _txtDocker.Text = res;
                    // Флаг занятости снимаем последним и в UI-потоке: снятый на фоновом потоке,
                    // он на мгновение разрешал вторую команду поверх ещё не показанного результата.
                    Interlocked.Exchange(ref _dockerBusy, 0);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DoCompactDocker()
        {
            if (Interlocked.CompareExchange(ref _dockerBusy, 1, 0) != 0) return;
            int scope = _cmbDockerPrune != null ? Math.Max(0, _cmbDockerPrune.SelectedIndex) : 0;
            string[] cmds = Engine.DockerPruneCommands(scope);
            string step1 = cmds.Length == 0
                ? Tr.S("  1. ничего не удаляется (только сжатие);\r\n", "  1. nothing is removed (compaction only);\r\n")
                : Tr.S("  1. выполнено: docker ", "  1. run: docker ") + string.Join("; docker ", cmds) + ";\r\n";
            DialogResult dr = MessageBox.Show(this,
                Tr.S("Будет выполнено одним действием:\r\n", "This will do, in one action:\r\n") + step1 +
                Tr.S("  2. остановлен Docker и весь WSL (wsl --shutdown: завершатся все запущенные контейнеры И все открытые сеансы дистрибутивов WSL — Ubuntu и т.п.!);\r\n" +
                     "  3. сжат виртуальный диск — реально освободится место на диске Windows (на большом диске это может занять 10–30 минут);\r\n" +
                     "  4. Docker Desktop запустится снова.",
                     "  2. stop Docker and all of WSL (wsl --shutdown: every running container AND every open WSL distro session — Ubuntu etc. — will exit!);\r\n" +
                     "  3. compact the virtual disk — actually frees Windows disk space (10–30 minutes on a large disk);\r\n" +
                     "  4. start Docker Desktop again.")
                + (scope == 3 ? VolumeWarning() : "") + Tr.S("\r\n\r\nПродолжить?", "\r\n\r\nContinue?"),
                "Docker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) { Interlocked.Exchange(ref _dockerBusy, 0); return; }

            _engine.ResetDockerCancel();
            _dockerStopping = false;
            Cursor = Cursors.WaitCursor;
            SetDockerUiBusy(true);
            // Обещание «пары минут» противоречило самому же окну подтверждения (10–30 минут)
            // и не имело ничего общего с делом: prune + остановка WSL + diskpart — это до
            // сорока минут. Теперь на экране срок, секундомер и текущий шаг.
            StartDockerTicker(Tr.S("Очистка и сжатие диска Docker (на большом диске это 10–30 минут). Docker и все сеансы WSL на это время остановлены; окно не закрывайте.",
                                   "Cleaning and compacting the Docker disk (10–30 minutes on a large disk). Docker and all WSL sessions are stopped meanwhile; don't close the window."));
            string op = Tr.S("очистка и сжатие диска Docker", "Docker disk cleanup and compaction");
            BeginWrite(op);
            // На других вкладках о происходящем не говорило ничто, а Docker и WSL при этом лежат:
            // всплывающая подсказка у часов — единственное место, видное со всего приложения.
            if (_tray != null)
                _tray.ShowBalloonTip(4000, "Docker",
                    Tr.S("Сжатие диска началось: Docker и WSL остановлены на 10–30 минут.",
                         "Disk compaction started: Docker and WSL are stopped for 10–30 minutes."), ToolTipIcon.Info);
            Thread t = new Thread(delegate()
            {
                string res;
                try { res = _engine.CompactDockerDisk(scope); }
                catch (Exception ex)
                {
                    res = Tr.S("[ошибка] ", "[error] ") + ex.Message
                        + Tr.S("\r\nЕсли Docker Desktop не запустился — запустите его вручную.",
                               "\r\nIf Docker Desktop did not start, start it manually.");
                }
                finally { EndWrite(op); }
                bool cancelled = _engine.DockerCancelled;
                UiPost(delegate
                {
                    Cursor = Cursors.Default;
                    StopDockerTicker();
                    SetDockerUiBusy(false);
                    _dockerStopping = false;
                    _txtDocker.Text = res;
                    if (_tray != null)
                        _tray.ShowBalloonTip(4000, "Docker",
                            cancelled ? Tr.S("Остановлено. Docker Desktop мог остаться выключенным — проверьте.",
                                             "Stopped. Docker Desktop may have stayed off — please check.")
                                      : Tr.S("Сжатие диска Docker завершено.", "Docker disk compaction finished."),
                            cancelled ? ToolTipIcon.Warning : ToolTipIcon.Info);
                    Interlocked.Exchange(ref _dockerBusy, 0);
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
