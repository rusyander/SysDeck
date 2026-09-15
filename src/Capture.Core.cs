// SysDeck — «Захват»: пути, журнал, настройки, сочетания клавиш, шаблон имени, связь процессов.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Скриншоты и запись видео живут в отдельном фоновом процессе того же exe — ключ --capture, без прав
// администратора. Главное окно из автозапуска бывает повышенным, а горячие клавиши нужны и при закрытом окне;
// кроме того, повышенный процесс не может отдать файл перетаскиванием в обычную программу. Процессы говорят
// именованными событиями и общими файлами в папке данных. Команды, которая принимала бы путь для удаления или
// запуска, нет: канал не должен превращаться в «сделай что угодно».
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  Пути и журнал
    // ------------------------------------------------------------------ //
    internal static class CapPaths
    {
        // Внутри папки данных приложения: SYSDECK_DATA_DIR уводит туда же и тесты.
        public static string DataDir { get { return Path.Combine(Engine.DefaultDataDir(), "capture"); } }
        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.json"); } }
        public static string StatusFile { get { return Path.Combine(DataDir, "status.json"); } }
        public static string OpenFile { get { return Path.Combine(DataDir, "open.txt"); } }
        public static string IndexFile { get { return Path.Combine(DataDir, "index.json"); } }
        public static string CrashLog { get { return Path.Combine(DataDir, "crash.log"); } }
        public static string RecordingFile { get { return Path.Combine(DataDir, "recording.txt"); } }
        public static string EncoderCacheFile { get { return Path.Combine(DataDir, "video-encoders.cache"); } }
        public static string ExecutablePath { get { return Application.ExecutablePath; } }

        public static string DefaultShotFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"); }
        }

        public static string DefaultVideoFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures"); }
        }

        // Запись рядом и перенос на место: оборванная запись не оставит половину файла.
        public static void WriteAtomic(string file, string text)
        {
            string dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            // Читатель в другом процессе (страница «Оверлей» раз в секунду) на миг держит файл открытым — замена
            // тогда падает с «не удаётся удалить заменяемый файл». Несколько коротких попыток вместо записи в crash.log.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(file)) File.Replace(tmp, file, null);
                    else File.Move(tmp, file);
                    return;
                }
                catch (IOException)
                {
                    if (attempt >= 4) throw;
                    Thread.Sleep(25);
                }
            }
        }

        // Чтение, которое не мешает WriteAtomic в другом процессе заменить файл.
        public static string ReadShared(string file)
        {
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader r = new StreamReader(fs, Encoding.UTF8, true))
                return r.ReadToEnd();
        }
    }

    internal static class CapLog
    {
        private static readonly object Gate = new object();

        public static void Report(Exception ex)
        {
            if (ex == null) return;
            Write(ex.ToString());
        }

        public static void Write(string text)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(CapPaths.DataDir);
                    File.AppendAllText(CapPaths.CrashLog,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " + text + "\r\n\r\n",
                        Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Swallow(Action action)
        {
            try { action(); }
            catch (Exception ex) { Report(ex); }
        }
    }

    // ------------------------------------------------------------------ //
    //  Действия, на которые можно назначить сочетание клавиш
    // ------------------------------------------------------------------ //
    internal enum CapAction { ShotRegion, ShotScreen, ShotWindow, RecRegion, RecScreen, RecPause, Gallery, Hud, HudReset, HudScene, LagRecord }

    internal enum ShotAfter { Save, SaveAndCopy, CopyOnly, OpenEditor }

    internal enum ScreenTarget { CursorMonitor, AllMonitors, ActiveWindow }

    internal static class CapActions
    {
        public static readonly CapAction[] All =
        {
            CapAction.ShotRegion, CapAction.ShotScreen, CapAction.ShotWindow,
            CapAction.RecRegion, CapAction.RecScreen, CapAction.RecPause, CapAction.Gallery, CapAction.Hud,
            CapAction.HudReset, CapAction.HudScene, CapAction.LagRecord
        };

        public static string Title(CapAction a)
        {
            switch (a)
            {
                case CapAction.ShotRegion: return Tr.S("Скриншот области", "Region screenshot");
                case CapAction.ShotScreen: return Tr.S("Скриншот экрана", "Screen screenshot");
                case CapAction.ShotWindow: return Tr.S("Скриншот активного окна", "Active window screenshot");
                case CapAction.RecRegion: return Tr.S("Видео области: начать / остановить", "Region video: start / stop");
                case CapAction.RecScreen: return Tr.S("Видео экрана: начать / остановить", "Screen video: start / stop");
                case CapAction.RecPause: return Tr.S("Пауза записи", "Pause recording");
                case CapAction.Hud: return Tr.S("Оверлей показателей: показать / скрыть", "Metrics overlay: show / hide");
                case CapAction.HudReset: return Tr.S("Оверлей: сбросить мин. / сред. / макс.", "Overlay: reset min / avg / max");
                case CapAction.HudScene: return Tr.S("Оверлей: следующий набор строк", "Overlay: next row set");
                case CapAction.LagRecord: return Tr.S("Запись лагов: начать / остановить", "Lag recording: start / stop");
                default: return Tr.S("Открыть галерею", "Open the gallery");
            }
        }

        // По умолчанию — те же клавиши, что у VK Play GameCenter (решение пользователя 13.09.2026: переход без
        // переучивания). У GameCenter нет снимка окна, видео экрана, паузы и галереи — у них сочетаний по умолчанию нет.
        public static string DefaultHotkey(CapAction a)
        {
            switch (a)
            {
                case CapAction.ShotRegion: return "F3";
                case CapAction.ShotScreen: return "F4";
                case CapAction.RecRegion: return "F7";
                // Alt+R — как у оверлея NVIDIA App (решение пользователя 15.09.2026): кто держит NVIDIA App, увидит у
                // строки «сочетание занято» и выберет другое. Остальные — Ctrl+Alt+буква: в играх почти не заняты.
                case CapAction.Hud: return "Alt+R";
                case CapAction.HudReset: return "Ctrl+Alt+R";
                // Ctrl+Alt+S занят панелью «Размеры папок» этой же программы — N от «next».
                case CapAction.HudScene: return "Ctrl+Alt+N";
                case CapAction.LagRecord: return "Ctrl+Alt+L";
                default: return "";
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Сочетание клавиш: «Ctrl+Shift+PrtScn» ⇄ (модификаторы, виртуальная клавиша)
    // ------------------------------------------------------------------ //
    internal struct HotkeySpec
    {
        public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;

        public uint Mods;
        public uint Vk;

        public bool IsEmpty { get { return Vk == 0; } }

        public HotkeySpec(uint mods, uint vk) { Mods = mods; Vk = vk; }

        // Названия клавиш одинаковы для обоих языков интерфейса: на клавиатуре они латиницей.
        private static readonly Dictionary<uint, string> Names = BuildNames();

        private static Dictionary<uint, string> BuildNames()
        {
            Dictionary<uint, string> d = new Dictionary<uint, string>();
            d[0x2C] = "PrtScn"; d[0x13] = "Pause"; d[0x91] = "ScrollLock";
            d[0x20] = "Space"; d[0x0D] = "Enter"; d[0x09] = "Tab"; d[0x08] = "Backspace";
            d[0x2D] = "Insert"; d[0x2E] = "Delete"; d[0x24] = "Home"; d[0x23] = "End";
            d[0x21] = "PgUp"; d[0x22] = "PgDn";
            d[0x25] = "Left"; d[0x26] = "Up"; d[0x27] = "Right"; d[0x28] = "Down";
            d[0xC0] = "`"; d[0xBD] = "-"; d[0xBB] = "="; d[0xDB] = "["; d[0xDD] = "]";
            d[0xDC] = "\\"; d[0xBA] = ";"; d[0xDE] = "'"; d[0xBC] = ","; d[0xBE] = "."; d[0xBF] = "/";
            for (uint i = 0; i < 24; i++) d[0x70 + i] = "F" + (i + 1).ToString(CultureInfo.InvariantCulture);
            for (uint i = 0; i < 10; i++) d[0x30 + i] = i.ToString(CultureInfo.InvariantCulture);
            for (uint i = 0; i < 26; i++) d[0x41 + i] = ((char)('A' + i)).ToString();
            for (uint i = 0; i < 10; i++) d[0x60 + i] = "Num" + i.ToString(CultureInfo.InvariantCulture);
            d[0x6A] = "Num*"; d[0x6B] = "Num+"; d[0x6D] = "Num-"; d[0x6E] = "Num."; d[0x6F] = "Num/";
            return d;
        }

        // Клавиши, которые сами по себе модификаторы, сочетанием не бывают.
        public static bool IsModifierKey(uint vk)
        {
            return vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C
                || (vk >= 0xA0 && vk <= 0xA5);
        }

        public static bool IsKnownKey(uint vk) { return Names.ContainsKey(vk); }

        public override string ToString()
        {
            if (IsEmpty) return "";
            StringBuilder sb = new StringBuilder();
            // Win первым, как пишет сама Windows («Win+Shift+S»): по этой строке HotkeyOwners узнаёт системные сочетания.
            if ((Mods & MOD_WIN) != 0) sb.Append("Win+");
            if ((Mods & MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((Mods & MOD_ALT) != 0) sb.Append("Alt+");
            if ((Mods & MOD_SHIFT) != 0) sb.Append("Shift+");
            string name;
            sb.Append(Names.TryGetValue(Vk, out name) ? name : "0x" + Vk.ToString("X2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // false — текст не разобрать; пустой текст — это «без сочетания», разбирается успешно.
        public static bool TryParse(string text, out HotkeySpec spec)
        {
            spec = new HotkeySpec();
            if (text == null) return true;
            text = text.Trim();
            if (text.Length == 0) return true;
            string[] parts = text.Split('+');
            // «Ctrl++» — плюс как клавиша: последний пустой кусок после разделителя
            List<string> tokens = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) { if (i == parts.Length - 1 && i > 0) return false; continue; }
                tokens.Add(p);
            }
            if (tokens.Count == 0) return false;
            uint mods = 0;
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                string m = tokens[i].ToLowerInvariant();
                if (m == "ctrl" || m == "control") mods |= MOD_CONTROL;
                else if (m == "alt") mods |= MOD_ALT;
                else if (m == "shift") mods |= MOD_SHIFT;
                else if (m == "win" || m == "windows") mods |= MOD_WIN;
                else return false;
            }
            string key = tokens[tokens.Count - 1];
            uint vk = 0;
            foreach (KeyValuePair<uint, string> kv in Names)
                if (string.Equals(kv.Value, key, StringComparison.OrdinalIgnoreCase)) { vk = kv.Key; break; }
            if (vk == 0)
            {
                string k = key.ToLowerInvariant();
                if (k == "printscreen" || k == "prtsc" || k == "print") vk = 0x2C;
                else if (k.StartsWith("0x", StringComparison.Ordinal))
                {
                    uint raw;
                    if (uint.TryParse(k.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out raw) && raw > 0 && raw < 0xFF) vk = raw;
                }
            }
            if (vk == 0 || IsModifierKey(vk)) return false;
            spec = new HotkeySpec(mods, vk);
            return true;
        }

        public static HotkeySpec Parse(string text)
        {
            HotkeySpec s;
            return TryParse(text, out s) ? s : new HotkeySpec();
        }

        // Сочетание из нажатия в поле ввода.
        public static HotkeySpec FromKeys(Keys keyData, bool win)
        {
            uint vk = (uint)(keyData & Keys.KeyCode);
            uint mods = 0;
            if ((keyData & Keys.Control) != 0) mods |= MOD_CONTROL;
            if ((keyData & Keys.Alt) != 0) mods |= MOD_ALT;
            if ((keyData & Keys.Shift) != 0) mods |= MOD_SHIFT;
            if (win) mods |= MOD_WIN;
            if (IsModifierKey(vk) || vk == 0) return new HotkeySpec();
            return new HotkeySpec(mods, vk);
        }

        // Одиночная клавиша без модификаторов перестаёт работать во всех остальных программах.
        public bool IsBareKey { get { return !IsEmpty && Mods == 0; } }
    }

    // Кто, скорее всего, держит сочетание, если регистрация отказала (ошибка 1409). Только подсказка:
    // Windows владельца сочетания не называет.
    internal static class HotkeyOwners
    {
        public const int ErrorHotkeyAlreadyRegistered = 1409;
        public const int TakenOver = -1;   // занято другой программой, но агент забирает нажатие хуком клавиатуры (KeyGrabber)

        public static string Guess(HotkeySpec spec)
        {
            string s = spec.ToString();
            switch (s)
            {
                case "PrtScn": return Tr.S("Ножницы Windows (клавиша PrtScn)", "Windows Snipping Tool (PrtScn key)");
                case "Win+Shift+S": return Tr.S("Ножницы Windows", "Windows Snipping Tool");
                case "Alt+F1": case "Alt+F9": case "Alt+F10": case "Alt+Z": case "Alt+R": case "Ctrl+Shift+Left":
                    return "NVIDIA App / GeForce Experience";
                case "F3": case "F4": case "F5": case "F7": case "Alt+F3": case "Alt+F4": case "Alt+F5": case "Alt+F7":
                    return "VK Play GameCenter";
                case "Ctrl+Alt+S": return Tr.S("«Размеры папок» этой программы (панель)", "This app's Folder sizes panel");
                case "Alt+PrtScn": return Tr.S("Windows (снимок окна в буфер)", "Windows (window to clipboard)");
                default:
                    if ((spec.Mods & HotkeySpec.MOD_WIN) != 0 && (spec.Mods & HotkeySpec.MOD_ALT) != 0) return "Xbox Game Bar";
                    if ((spec.Mods & HotkeySpec.MOD_WIN) != 0) return Tr.S("Windows (системное сочетание)", "Windows (system shortcut)");
                    return null;
            }
        }

        public static string Describe(int error, HotkeySpec spec)
        {
            if (error == 0) return "";
            if (error == ErrorHotkeyAlreadyRegistered)
            {
                string owner = Guess(spec);
                return Tr.S("сочетание занято другой программой", "the shortcut is taken by another program")
                       + (owner == null ? "" : " (" + Tr.S("возможно, ", "possibly ") + owner + ")");
            }
            return Tr.S("не удалось назначить, код ", "could not register, code ") + error.ToString(CultureInfo.InvariantCulture);
        }
    }

    // ------------------------------------------------------------------ //
    //  Имя файла по шаблону и защита корня
    // ------------------------------------------------------------------ //
    internal static class NameTemplate
    {
        public const string Default = "{app}_{yyyy-MM-dd_HH-mm-ss}";
        public const int MaxNameLength = 120;

        // {app} — программа; любое другое {…} — формат даты .NET. Разделителей пути в результате нет: подпапку по
        // программе добавляет только сам захват, так что имя из шаблона никогда не уводит из корня.
        public static string Expand(string template, string app, DateTime when)
        {
            if (string.IsNullOrEmpty(template)) template = Default;
            string cleanApp = SanitizeSegment(app);
            if (cleanApp.Length == 0) cleanApp = "screen";
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c == '{')
                {
                    int close = template.IndexOf('}', i + 1);
                    if (close > i + 1)
                    {
                        string token = template.Substring(i + 1, close - i - 1);
                        if (string.Equals(token, "app", StringComparison.OrdinalIgnoreCase)) sb.Append(cleanApp);
                        else
                        {
                            string formatted;
                            try { formatted = when.ToString(token, CultureInfo.InvariantCulture); }
                            catch (FormatException) { formatted = token; }
                            sb.Append(formatted);
                        }
                        i = close + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            string name = SanitizeSegment(sb.ToString());
            if (name.Length == 0) name = cleanApp + "_" + when.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            return name;
        }

        // Один сегмент пути: недопустимые символы и разделители → «_», без хвостовых точек и пробелов, ограничение длины,
        // зарезервированные имена устройств (CON, NUL, COM1…) получают подчёркивание.
        public static string SanitizeSegment(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c < 32 || Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\' || c == ':') sb.Append('_');
                else sb.Append(c);
            }
            string s = sb.ToString().Trim();
            if (s.Length > MaxNameLength) s = s.Substring(0, MaxNameLength);
            s = s.TrimEnd('.', ' ');
            while (s.StartsWith("..", StringComparison.Ordinal)) s = s.Substring(1);
            if (s == ".") s = "";
            string stem = s;
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot);
            string upper = stem.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL"
                || (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(upper[3])))
                s = "_" + s;
            return s;
        }

        // Полный путь нового файла: корень [\программа]\имя[_N].расширение. exists — подменяется в тестах.
        public static string BuildPath(string root, bool perApp, string app, string template, DateTime when, string extension,
                                       Func<string, bool> exists)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException("root");
            if (exists == null) exists = File.Exists;
            string dir = Path.GetFullPath(root);
            if (perApp)
            {
                string sub = SanitizeSegment(app);
                if (sub.Length > 0) dir = Path.Combine(dir, sub);
            }
            string name = Expand(template, app, when);
            string ext = extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
            string candidate = Path.Combine(dir, name + ext);
            for (int n = 2; exists(candidate); n++)
            {
                candidate = Path.Combine(dir, name + "_" + n.ToString(CultureInfo.InvariantCulture) + ext);
                if (n > 9999) throw new IOException("too many files with the same name: " + name);
            }
            if (!IsUnder(candidate, root)) throw new IOException("the file name leaves the capture folder: " + candidate);
            return candidate;
        }

        // Путь внутри корня (или равен ему) — без учёта регистра, после нормализации «..».
        public static bool IsUnder(string path, string root)
        {
            try
            {
                string p = Path.GetFullPath(path).TrimEnd('\\');
                string r = Path.GetFullPath(root).TrimEnd('\\');
                return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
                       || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------ //
    //  «PrtScn вместо Ножниц»: HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled.
    //  Пишется только по явному согласию, прежнее значение запоминается и возвращается при выключении.
    // ------------------------------------------------------------------ //
    internal static class PrintScreenKey
    {
        private const string KeyPath = @"Control Panel\Keyboard";
        private const string ValueName = "PrintScreenKeyForSnippingEnabled";

        // -1 — значения нет (Windows 11 тогда отдаёт PrtScn Ножницам), 0/1 — записанное.
        public static int Read()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    object v = k == null ? null : k.GetValue(ValueName);
                    return v is int ? ((int)v != 0 ? 1 : 0) : -1;
                }
            }
            catch { return -1; }
        }

        // Возвращает прежнее значение, чтобы его можно было вернуть.
        public static int Take()
        {
            int previous = Read();
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                if (k != null) k.SetValue(ValueName, 0, RegistryValueKind.DWord);
            return previous;
        }

        public static void Restore(int previous)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (k == null) return;
                if (previous < 0) k.DeleteValue(ValueName, false);
                else k.SetValue(ValueName, previous, RegistryValueKind.DWord);
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Связь главного окна с фоновым процессом захвата
    // ------------------------------------------------------------------ //
    internal static class CapIpc
    {
        public const string MutexName = @"Local\SysDeck.Capture";
        private const string Prefix = @"Local\SysDeck.Capture.";

        // Полный набор команд. Ни одна не несёт путь для удаления или запуска; «Open» читает путь из open.txt, но
        // только показывает файл, и то лишь изображение или видео.
        public static readonly string[] Commands =
        {
            "Shutdown", "Reload", "ShotRegion", "ShotScreen", "ShotWindow", "RecRegion", "RecScreen", "RecStop", "RecPause", "Gallery", "Open",
            "HotkeysOff", "HotkeysOn", "Hud"
        };

        public static string EventName(string command) { return Prefix + command; }

        public static bool IsCommand(string command) { return Array.IndexOf(Commands, command) >= 0; }

        private static SecurityIdentifier User()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) return id.User;
        }

        public static Mutex CreateInstanceMutex(out bool first)
        {
            try
            {
                MutexSecurity sec = new MutexSecurity();
                sec.AddAccessRule(new MutexAccessRule(User(), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
                return new Mutex(true, MutexName, out first, sec);
            }
            catch (UnauthorizedAccessException)
            {
                first = false;
                return null;
            }
        }

        public static EventWaitHandle CreateEvent(string command)
        {
            if (!IsCommand(command)) throw new ArgumentException("unknown capture command: " + command);
            bool created;
            EventWaitHandleSecurity sec = new EventWaitHandleSecurity();
            sec.AddAccessRule(new EventWaitHandleAccessRule(User(),
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
            return new EventWaitHandle(false, EventResetMode.AutoReset, EventName(command), out created, sec);
        }

        public static bool IsRunning()
        {
            Mutex m;
            try
            {
                if (Mutex.TryOpenExisting(MutexName, MutexRights.Synchronize, out m)) { m.Dispose(); return true; }
                return false;
            }
            catch (UnauthorizedAccessException) { return true; }
            catch { return false; }
        }

        public static bool Signal(string command)
        {
            if (!IsCommand(command)) return false;
            EventWaitHandle h;
            try
            {
                if (!EventWaitHandle.TryOpenExisting(EventName(command), EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, out h)) return false;
                using (h) h.Set();
                return true;
            }
            catch (Exception) { return false; }
        }

        // Показать файл в окне просмотра агента: путь кладётся в файл, событие будит агент.
        public static bool OpenInViewer(string path)
        {
            try { CapPaths.WriteAtomic(CapPaths.OpenFile, path ?? ""); }
            catch (Exception ex) { CapLog.Report(ex); return false; }
            return Signal("Open");
        }

        public static bool WaitStopped(int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (IsRunning())
            {
                if (clock.ElapsedMilliseconds > timeoutMs) return false;
                Thread.Sleep(100);
            }
            return true;
        }

        public static bool WaitRunning(int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!IsRunning())
            {
                if (clock.ElapsedMilliseconds > timeoutMs) return false;
                Thread.Sleep(100);
            }
            return true;
        }
    }

    // Что главное окно знает о фоновом процессе: пишет сам процесс при старте и после каждой перерегистрации клавиш.
    internal sealed class CapStatus
    {
        public int Pid;
        public bool Elevated;
        public DateTime StartedUtc;
        public readonly Dictionary<CapAction, int> HotkeyErrors = new Dictionary<CapAction, int>();

        public static void Write(bool elevated, IDictionary<CapAction, int> hotkeyErrors, DateTime startedUtc)
        {
            try
            {
                JVal o = JVal.NewObj();
                o.Set("pid", JVal.NewNum(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)));
                JVal e = new JVal(); e.Kind = JKind.Bool; e.B = elevated;
                o.Set("elevated", e);
                o.Set("started", JVal.NewStr(startedUtc.ToString("o", CultureInfo.InvariantCulture)));
                JVal keys = JVal.NewObj();
                if (hotkeyErrors != null)
                    foreach (KeyValuePair<CapAction, int> kv in hotkeyErrors)
                        keys.Set(kv.Key.ToString(), JVal.NewNum(kv.Value.ToString(CultureInfo.InvariantCulture)));
                o.Set("hotkeys", keys);
                CapPaths.WriteAtomic(CapPaths.StatusFile, Jsn.Write(o));
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        public static void Clear()
        {
            try { File.Delete(CapPaths.StatusFile); } catch { }
        }

        // null — фоновый процесс не запущен.
        public static CapStatus Read()
        {
            if (!CapIpc.IsRunning()) return null;
            CapStatus s = new CapStatus();
            try
            {
                if (!File.Exists(CapPaths.StatusFile)) return s;
                JVal o = Jsn.Parse(CapPaths.ReadShared(CapPaths.StatusFile));
                if (o == null || o.Kind != JKind.Obj) return s;
                int.TryParse(o.GetStr("pid"), out s.Pid);
                JVal e = o.Get("elevated");
                s.Elevated = e != null && e.Kind == JKind.Bool && e.B;
                DateTime.TryParse(o.GetStr("started"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out s.StartedUtc);
                JVal keys = o.Get("hotkeys");
                if (keys != null && keys.Kind == JKind.Obj)
                    foreach (CapAction a in CapActions.All)
                    {
                        int code;
                        JVal k = keys.Get(a.ToString());
                        if (k != null && int.TryParse(k.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out code)) s.HotkeyErrors[a] = code;
                    }
            }
            catch { }
            return s;
        }
    }

    // ------------------------------------------------------------------ //
    //  Автозапуск и запуск агента без прав администратора
    // ------------------------------------------------------------------ //
    internal static class CapLauncher
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValueName = "SysDeck.Capture";

        // HKCU\Run запускает процесс при входе с обычными правами — ровно то, что нужно агенту. Задача Планировщика
        // «при входе» требует прав администратора на создание и агенту ничего не даёт.
        public static bool IsAutostartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string value = key == null ? null : key.GetValue(RunValueName) as string;
                    return value != null && value.IndexOf(CapMode.Switch, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        public static bool SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return false;
                    if (on) key.SetValue(RunValueName, RunCommand(CapPaths.ExecutablePath));
                    else key.DeleteValue(RunValueName, false);
                }
                return true;
            }
            catch (Exception ex) { CapLog.Report(ex); return false; }
        }

        internal static string RunCommand(string exe) { return "\"" + exe + "\" " + CapMode.Switch; }

        // Вместе с окном программы: горячие клавиши работают сразу, без кнопки «Запустить». Остановленный пользователем
        // процесс (Enabled = false) не поднимается. Фоновый поток: запуск через Проводник может занять секунды.
        public static void StartIfEnabled()
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    if (CapIpc.IsRunning() || !CapSettings.Load().Enabled) return;
                    string why = StartAgent();
                    if (why != null) CapLog.Write("autostart with the app failed: " + why);
                }
                catch (Exception ex) { CapLog.Report(ex); }
            });
            t.Name = "wpc-capture-autostart";
            t.IsBackground = true;
            t.Start();
        }

        // null — запущен (или уже работал), иначе причина. Из повышенного процесса агент стартует с токеном Проводника:
        // повышенный агент не отдал бы файл перетаскиванием в обычную программу.
        public static string StartAgent()
        {
            if (CapIpc.IsRunning()) return null;
            string exe = CapPaths.ExecutablePath;
            try
            {
                if (Elevation.IsElevated)
                {
                    string why = ShellExecuteUnelevated(exe, CapMode.Switch);
                    if (why == null) return null;
                    CapLog.Write("unelevated start failed (" + why + "), starting directly");
                }
                ProcessStartInfo psi = new ProcessStartInfo(exe, CapMode.Switch);
                psi.UseShellExecute = false;
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                CapLog.Report(ex);
                return ex.Message;
            }
        }

        // Известный приём: рабочий стол Проводника → IShellFolderViewDual → IShellDispatch2.ShellExecute. Процесс запускает
        // сам Проводник со своим (обычным) токеном. Ничего в системе не создаётся.
        internal static string ShellExecuteUnelevated(string file, string arguments)
        {
            object shellWindows = null, desktop = null, view = null, folderView = null, app = null;
            try
            {
                Type t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
                shellWindows = Activator.CreateInstance(t);
                object loc = 0, root = null;   // CSIDL_DESKTOP как VT_I4, корень — VT_EMPTY
                int hwnd;
                const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
                desktop = ((IShellWindows)shellWindows).FindWindowSW(ref loc, ref root, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH);
                if (desktop == null) return "no desktop shell window";
                Guid sidTop = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");   // SID_STopLevelBrowser
                Guid iidBrowser = typeof(IShellBrowser).GUID;
                object browserObj;
                int hr = ((IServiceProvider)desktop).QueryService(ref sidTop, ref iidBrowser, out browserObj);
                if (hr != 0 || browserObj == null) return "QueryService 0x" + hr.ToString("X8");
                IShellBrowser browser = (IShellBrowser)browserObj;
                IShellView shellView;
                hr = browser.QueryActiveShellView(out shellView);
                if (hr != 0 || shellView == null) return "QueryActiveShellView 0x" + hr.ToString("X8");
                view = shellView;
                Guid iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
                const uint SVGIO_BACKGROUND = 0;
                hr = shellView.GetItemObject(SVGIO_BACKGROUND, ref iidDispatch, out folderView);
                if (hr != 0 || folderView == null) return "GetItemObject 0x" + hr.ToString("X8");
                app = folderView.GetType().InvokeMember("Application", System.Reflection.BindingFlags.GetProperty, null, folderView, null);
                if (app == null) return "no Shell.Application";
                // ShellExecute(File, vArgs, vDir, vOperation, vShow)
                app.GetType().InvokeMember("ShellExecute", System.Reflection.BindingFlags.InvokeMethod, null, app,
                    new object[] { file, arguments, Path.GetDirectoryName(file), "open", 1 });
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                foreach (object o in new object[] { app, folderView, view, desktop, shellWindows })
                    if (o != null && Marshal.IsComObject(o)) { try { Marshal.ReleaseComObject(o); } catch { } }
            }
        }

        // Двойной интерфейс: слоты до FindWindowSW — заглушки по порядку vtable, они никогда не вызываются.
        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IShellWindows
        {
            int Count { get; }
            void Item();
            void NewEnum();
            void Register();
            void RegisterPending();
            void Revoke();
            void OnNavigate();
            void OnActivated();
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object pvarloc,
                                [In, MarshalAs(UnmanagedType.Struct)] ref object pvarlocRoot,
                                int swClass, out int pHWND, int swfwOptions);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [PreserveSig]
            int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            void GetWindow();                      // IOleWindow
            void ContextSensitiveHelp();
            void InsertMenusSB();                  // IShellBrowser
            void SetMenuSB();
            void RemoveMenusSB();
            void SetStatusTextSB();
            void EnableModelessSB();
            void TranslateAcceleratorSB();
            void BrowseObject();
            void GetViewStateStream();
            void GetControlWindow();
            void SendControlMsg();
            [PreserveSig]
            int QueryActiveShellView(out IShellView ppshv);
        }

        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellView
        {
            void GetWindow();                      // IOleWindow
            void ContextSensitiveHelp();
            void TranslateAccelerator();           // IShellView
            void EnableModeless();
            void UIActivate();
            void Refresh();
            void CreateViewWindow();
            void DestroyViewWindow();
            void GetCurrentInfo();
            void AddPropertySheetPages();
            void SaveViewState();
            void SelectItem();
            [PreserveSig]
            int GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.IDispatch)] out object ppv);
        }
    }
}
