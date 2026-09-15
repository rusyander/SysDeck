// Windows Process Cleaner — «Загрузки»: вопрос «куда скачивать» для загрузки, пришедшей из браузера.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Запись заводится сразу, но стоит на паузе с пометкой «ждёт выбора папки»: браузеру ответ нужен немедленно, а человек
// отвечает, когда увидит окно. Пока он не ответил, не скачано ни байта; закрыл окно — запись так и осталась на паузе,
// её можно продолжить позже, выбрав папку через «Переместить в…». Окно живёт в потоке уведомлений фонового процесса
// (Downloads.Notify.cs): у процесса загрузок своего цикла сообщений нет.
// Запомненные пути — общий список мест, ни к сайту, ни к типу файла не привязанный: это делают правила в настройках.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using WindowsProcessCleaner.Capture;

namespace WindowsProcessCleaner.Downloads
{
    internal sealed class DlFolderChoice
    {
        public string Folder = "";
        public bool Remember;      // добавить путь в список запомненных
        public bool StopAsking;    // больше не спрашивать (AskFolder = false)
    }

    internal static class DlFolderAsk
    {
        // Спрашивать имеет смысл, только когда папку не назвал сам вызывающий: окно «Добавить…» её уже спросило.
        // Сам файл .torrent — служебный: по щелчку на трекере он скачивается первым, а вопрос уместен один, про раздачу.
        public static bool Needed(DlSettings s, string folder, string name)
        {
            if (s == null || !s.AskFolder || !string.IsNullOrEmpty(folder)) return false;
            string n = (name ?? "").Trim();
            return !n.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        }

        // Вызывается из потока команд и не ждёт ответа: окно показывается в потоке уведомлений, запись до ответа стоит.
        public static void Begin(DlEngine engine, DlNotifier notifier, string id, string name)
        {
            Begin(engine, notifier, id, name, false);
        }

        // dropOnCancel — запись завелась сама (торрент из скачанного .torrent), человек её в список не добавлял:
        // отказ убирает её целиком, чтобы повторный щелчок на трекере начал разговор с начала, а не наткнулся
        // на «этот торрент уже в списке». Всё, что человек добавил руками, отказ оставляет на паузе.
        public static void Begin(DlEngine engine, DlNotifier notifier, string id, string name, bool dropOnCancel)
        {
            if (engine == null || notifier == null || string.IsNullOrEmpty(id)) return;
            notifier.Hold(true);
            bool posted = notifier.Post(delegate { Ask(engine, notifier, id, name, dropOnCancel); });
            if (posted) return;
            notifier.Hold(false);
            Apply(engine, id, null, false);   // потока с окном нет — запись просто стоит, пока человек не выберет папку сам
        }

        // Окно немодальное намеренно. Модальный цикл принадлежит потоку, а не окну: любое уведомление, закрывшееся
        // рядом, обрывало его — вопрос уходил сам через шесть секунд с ответом «Отмена», и загрузка вставала
        // с пометкой «папка не выбрана». Ответ разбирается в FormClosed, ждать здесь нечего.
        private static void Ask(DlEngine engine, DlNotifier notifier, string id, string name, bool dropOnCancel)
        {
            DlSettings s = engine.Settings;
            // Список мест: папка из настроек, системные «Загрузки» и всё, что человек сам просил запомнить, —
            // чтобы выбирать между знакомыми папками, а не искать их заново через «Обзор».
            List<string> places = new List<string>();
            AddPlace(places, s.EffectiveFolder);
            AddPlace(places, DlPaths.DefaultFolder);
            foreach (string f in s.RecentFolders) AddPlace(places, f);
            DlEngine eng = engine;
            DlNotifier note = notifier;
            string rid = id;
            try
            {
                DlFolderPromptForm form = new DlFolderPromptForm(name, places);
                form.FormClosed += delegate
                {
                    DlFolderChoice choice = form.DialogResult == DialogResult.OK ? form.Choice : null;
                    try { Forget(eng, form.Forgotten); Apply(eng, rid, choice, dropOnCancel); }
                    finally { note.HoldToasts(false); note.Hold(false); form.Dispose(); }
                };
                note.HoldToasts(true);          // уведомления ждут: они перекрывают вопрос и мешают ответить
                form.Show();
                return;
            }
            catch (Exception ex) { DlLog.Report(ex); }
            notifier.Hold(false);
            Apply(engine, id, null, false);
        }

        private static void AddPlace(List<string> places, string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            foreach (string p in places) if (string.Equals(p, folder, StringComparison.OrdinalIgnoreCase)) return;
            places.Add(folder);
        }

        // Ответ получен — папка записи и её продолжение; отказ оставляет запись на паузе, ничего не удаляя,
        // а заведённую саму собой (dropOnCancel) убирает из списка — скачано ноль байт, терять нечего.
        internal static void Apply(DlEngine engine, string id, DlFolderChoice choice, bool dropOnCancel)
        {
            try
            {
                if (choice == null)
                {
                    string gone;
                    if (dropOnCancel && engine.Remove(id, false, out gone)) return;
                    engine.SetWaitReason(id, Tr.S("папка не выбрана — нажмите «Продолжить»", "no folder chosen — press «Resume»"));
                    return;
                }
                string why;
                if (choice.Folder.Length > 0 && !engine.SetFolderBeforeStart(id, choice.Folder, out why))
                {
                    engine.SetWaitReason(id, why);
                    return;
                }
                if (choice.Remember || choice.StopAsking)
                {
                    DlSettings s = engine.Settings.Clone();
                    if (choice.Remember && choice.Folder.Length > 0) RememberFolder(s, choice.Folder);
                    if (choice.StopAsking) s.AskFolder = false;
                    s.Save();
                    engine.UpdateSettings(s);
                }
                engine.Resume(id);
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        // Убранное из списка забывается независимо от ответа: человек правил список, а не отвечал на вопрос.
        private static void Forget(DlEngine engine, List<string> gone)
        {
            if (gone == null || gone.Count == 0) return;
            try
            {
                DlSettings s = engine.Settings.Clone();
                bool changed = false;
                foreach (string g in gone)
                    for (int i = s.RecentFolders.Count - 1; i >= 0; i--)
                        if (string.Equals(s.RecentFolders[i], g, StringComparison.OrdinalIgnoreCase))
                        { s.RecentFolders.RemoveAt(i); changed = true; }
                if (!changed) return;
                s.Save();
                engine.UpdateSettings(s);
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        internal static void RememberFolder(DlSettings s, string folder)
        {
            for (int i = s.RecentFolders.Count - 1; i >= 0; i--)
                if (string.Equals(s.RecentFolders[i], folder, StringComparison.OrdinalIgnoreCase)) s.RecentFolders.RemoveAt(i);
            s.RecentFolders.Insert(0, folder);
            while (s.RecentFolders.Count > DlSettings.MaxRecentFolders) s.RecentFolders.RemoveAt(s.RecentFolders.Count - 1);
        }
    }

    internal sealed class DlFolderPromptForm : Form
    {
        private readonly ListBox _places;
        private readonly CheckBox _remember, _stopAsking;
        private readonly Label _path;
        private string _folder = "";

        public DlFolderChoice Choice;
        public readonly List<string> Forgotten = new List<string>();   // убранные из списка запомненных

        public DlFolderPromptForm(string name, List<string> places)
        {
            Text = Tr.S("Куда скачивать?", "Where to download?");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            TopMost = true;
            BackColor = EditorColors.Back;
            ForeColor = EditorColors.Text;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(560, 372);

            Label what = new Label();
            what.Text = string.IsNullOrEmpty(name) ? Tr.S("Новая загрузка", "A new download") : name;
            what.AutoEllipsis = true;
            what.Bounds = new Rectangle(16, 14, 528, 20);
            what.ForeColor = EditorColors.Text;

            Label hint = new Label();
            hint.Text = Tr.S("Выберите одну из запомненных папок или укажите свою. Пока не выбрано, загрузка стоит и не скачала ни байта.",
                             "Pick one of the remembered folders or choose your own. Until then the download waits and has not fetched a byte.");
            hint.Bounds = new Rectangle(16, 36, 528, 34);
            hint.ForeColor = EditorColors.Dim;

            _places = new ListBox();
            _places.Bounds = new Rectangle(16, 76, 528, 150);
            _places.BackColor = EditorColors.Canvas;
            _places.ForeColor = EditorColors.Text;
            _places.BorderStyle = BorderStyle.FixedSingle;
            _places.IntegralHeight = false;
            foreach (string p in places) _places.Items.Add(p);
            if (_places.Items.Count > 0) _places.SelectedIndex = 0;
            _places.SelectedIndexChanged += delegate { if (_places.SelectedItem != null) SetFolder((string)_places.SelectedItem); };
            _places.DoubleClick += delegate { if (_folder.Length > 0) Accept(); };
            _places.KeyDown += delegate(object sender, KeyEventArgs ke) { if (ke.KeyCode == Keys.Delete) { Forget(); ke.Handled = true; } };

            Button browse = MakeButton(Tr.S("Обзор…", "Browse…"), new Rectangle(16, 234, 120, 30));
            browse.Click += delegate { Browse(); };

            Button forget = MakeButton(Tr.S("Убрать из списка", "Remove from list"), new Rectangle(400, 234, 144, 30));
            forget.Click += delegate { Forget(); };

            _path = new Label();
            _path.Bounds = new Rectangle(146, 240, 246, 20);
            _path.AutoEllipsis = true;
            _path.ForeColor = EditorColors.Dim;

            _remember = MakeCheck(Tr.S("Запомнить эту папку", "Remember this folder"), new Rectangle(16, 272, 528, 22));
            _remember.Checked = true;
            _stopAsking = MakeCheck(Tr.S("Больше не спрашивать — качать по правилам из настроек",
                                         "Stop asking — use the rules from the settings"), new Rectangle(16, 296, 528, 22));

            Button ok = MakeButton(Tr.S("Скачать", "Download"), new Rectangle(324, 328, 104, 30));
            ok.Click += delegate { Accept(); };
            Button cancel = MakeButton(Tr.S("Отмена", "Cancel"), new Rectangle(440, 328, 104, 30));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.AddRange(new Control[] { what, hint, _places, browse, forget, _path, _remember, _stopAsking, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            if (places.Count > 0) SetFolder(places[0]);
        }

        private static Button MakeButton(string text, Rectangle bounds)
        {
            Button b = new Button();
            b.Text = text;
            b.Bounds = bounds;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(88, 88, 88);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 56, 56);
            b.BackColor = Color.FromArgb(44, 44, 44);
            b.ForeColor = EditorColors.Text;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        private static CheckBox MakeCheck(string text, Rectangle bounds)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Bounds = bounds;
            c.ForeColor = EditorColors.Text;
            c.FlatStyle = FlatStyle.Flat;
            return c;
        }

        private void SetFolder(string folder)
        {
            _folder = folder ?? "";
            _path.Text = _folder;
        }

        // Список чистится прямо здесь: иначе забыть случайно запомненную папку можно было бы только в настройках.
        private void Forget()
        {
            int at = _places.SelectedIndex;
            if (at < 0) return;
            string gone = (string)_places.Items[at];
            Forgotten.Add(gone);
            _places.Items.RemoveAt(at);
            if (_places.Items.Count == 0) { SetFolder(""); return; }
            _places.SelectedIndex = Math.Min(at, _places.Items.Count - 1);
        }

        private void Browse()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = Tr.S("Куда скачивать", "Where to download");
                dlg.ShowNewFolderButton = true;
                if (_folder.Length > 0 && Directory.Exists(_folder)) dlg.SelectedPath = _folder;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                SetFolder(dlg.SelectedPath);
                int at = -1;
                for (int i = 0; i < _places.Items.Count; i++)
                    if (string.Equals((string)_places.Items[i], _folder, StringComparison.OrdinalIgnoreCase)) at = i;
                if (at < 0) at = _places.Items.Add(_folder);
                _places.SelectedIndex = at;
            }
        }

        private void Accept()
        {
            if (_folder.Length == 0) { Browse(); return; }
            string why;
            if (DlFiles.CheckFolder(_folder, out why) == null)
            {
                MessageBox.Show(this, why, Tr.S("Куда скачивать", "Where to download"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Choice = new DlFolderChoice();
            Choice.Folder = _folder;
            Choice.Remember = _remember.Checked;
            Choice.StopAsking = _stopAsking.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Фоновый процесс не имеет права поднимать окна поверх чужих — окно поверх всех и тянется в передний план само.
            try
            {
                int on = 1;
                if (Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4) != 0)
                    Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, 4);
            }
            catch { }
            try { Activate(); } catch { }
        }
    }
}
