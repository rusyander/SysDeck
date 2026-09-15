// SysDeck — обновления программ: winget / Chocolatey — разбор таблиц, серьёзность, установка пакетами
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
    public partial class Engine
    {
        // ---------- Обновления программ (winget / Chocolatey) ----------
        //
        // Почему именно так:
        //  * winget — это официальная база Microsoft (winget-pkgs, десятки тысяч манифестов).
        //    Он сам сопоставляет установленную программу из "Установка и удаление" с пакетом,
        //    поэтому сравнение версий делает он, а не мы. Своя база версий была бы заведомо
        //    менее точной и быстро устаревала.
        //  * Chocolatey добавляется как второй источник, если он установлен: там есть пакеты,
        //    которых нет в winget.
        //  * Реестр не правим, ничего не скачиваем сами — обновление выполняет менеджер пакетов.
        //
        // Машинно-читаемого вывода у `winget upgrade` нет (проверено на 1.29: только таблица),
        // поэтому таблица разбирается по НАЧАЛАМ КОЛОНОК из строки заголовка. Разбиение по
        // пробелам здесь неприменимо: в реальном выводе колонки регулярно разделены одним
        // пробелом (длинное имя, версия вида "26183.1903.4892.4448"), а сами версии бывают
        // с пробелом внутри ("7.0.6 (43848)", "< 17.14.35"). Колонки берутся ПО ПОРЯДКУ,
        // а не по именам заголовков, — иначе локализованный winget не распознаётся.
        private volatile bool _cancelUpdates;
        public void CancelUpdateWork() { _cancelUpdates = true; }
        public void ResetUpdateCancel() { _cancelUpdates = false; _updateStatus = null; }
        public bool UpdatesCancelled { get { return _cancelUpdates; } }

        // Что установщик делает прямо сейчас — строкой для вкладки. Без этого «Обновление… 0/20»
        // стояло неподвижно, пока одна групповая установка шла до двух часов.
        private volatile string _updateStatus;
        public string UpdateStatus { get { return _updateStatus; } }

        // Отмену раньше опрашивали только МЕЖДУ менеджерами и МЕЖДУ группами: нажатый «Стоп»
        // ничего не делал до конца текущего запуска (до 2 часов). Теперь его видит сам RunCapture.
        private Func<bool> UpdateCancelPoll() { return delegate { return _cancelUpdates; }; }

        private Action<string, long> UpdateProgressPoll(string what)
        {
            return delegate(string line, long ms)
            {
                string s = (line ?? "").Trim();
                if (s.Length > 90) s = s.Substring(0, 90) + "…";
                _updateStatus = what + (s.Length > 0 ? " · " + s : "");
            };
        }

        private static string GroupNames(List<UpdateItem> group)
        {
            if (group == null || group.Count == 0) return "";
            return group.Count == 1 ? group[0].Name : group[0].Name + " +" + (group.Count - 1);
        }

        public string UpdateLogPath
        {
            get { return Path.Combine(_dir, "updates-" + DateTime.Now.ToString("yyyy-MM") + ".log"); }
        }

        // Проверка «утилита вообще есть» — это `--version`, доли секунды. 20 секунд здесь
        // означали, что первый заход на вкладку обновлений мог молча простоять 40 секунд.
        private static bool ToolAvailable(string exe, string args)
        {
            string so; int code;
            return RunCapture(exe, args, 5000, out so, out code) && code == 0;
        }

        // Установщик отработал успешно: 0 — готово, 3010 — готово, нужна перезагрузка,
        // 1641 — готово, перезагрузка уже запускается. Раньше успехом считался только 0,
        // и удачно поставленное обновление попадало в лог как неудача и предлагалось снова.
        private static bool IsInstallOk(int code) { return code == 0 || code == 3010 || code == 1641; }

        private static bool NeedsReboot(int code) { return code == 3010 || code == 1641; }

        private bool? _hasWinget, _hasChoco;
        public bool HasWinget
        {
            get
            {
                if (_hasWinget == null) _hasWinget = ToolAvailable("winget.exe", "--version");
                return _hasWinget.Value;
            }
        }
        public bool HasChoco
        {
            get
            {
                if (_hasChoco == null) _hasChoco = ToolAvailable("choco.exe", "--version");
                return _hasChoco.Value;
            }
        }

        // Запуск консольной утилиты с чтением stdout. stderr читается отдельным потоком:
        // если его не вычитывать, буфер трубы заполняется и процесс встаёт навсегда.
        private const int RunTimeout = -2;
        private const int RunCancelled = -3;
        private const int RunEarly = -4;      // всё нужное уже сказано в выводе, а сам процесс не завершился

        private static bool RunCapture(string exe, string args, int timeoutMs, out string stdout, out int exitCode)
        {
            return RunCapture(exe, args, timeoutMs, out stdout, out exitCode, null, null);
        }

        // Человеческая подпись к неудачному запуску внешней утилиты.
        // public: тем же текстом окно объясняет отказ запуска, таймаут и остановку —
        // иначе на экран попадали сырые «-1» и «-2».
        public static string RunFailText(bool ran, int code)
        {
            if (code == RunEarly) return Tr.S("утилита не завершилась сама", "the tool did not exit on its own");
            if (ran) return code == 740
                ? Tr.S("нужны права администратора", "administrator rights required")
                : Tr.S("код ", "code ") + code;
            if (code == RunTimeout) return Tr.S("не уложился в отведённое время", "timed out");
            if (code == RunCancelled) return Tr.S("остановлено", "cancelled");
            return Tr.S("не запустился", "failed to start");
        }

        // encoding == null — UTF-8 (winget/choco/docker); pnputil и прочие классические консольные
        // утилиты пишут в OEM-кодировке. cancel — опрос отмены: процесс убивается, как только
        // он вернёт true (DISM работает минутами, ждать его после «Стоп» нельзя).
        private static bool RunCapture(string exe, string args, int timeoutMs, out string stdout, out int exitCode,
                                       Encoding encoding, Func<bool> cancel)
        {
            return RunCapture(exe, args, timeoutMs, out stdout, out exitCode, encoding, cancel, null, null);
        }

        // onProgress(последняя строка вывода, сколько миллисекунд идёт) — вызывается примерно раз
        // в 200 мс, чтобы показать пользователю живой ход длинной утилиты вместо замершего «…».
        // stopWhen(строка) — «в выводе уже есть всё, что нам нужно»: после срабатывания ждём
        // завершения ещё 5 секунд и уходим, убив процесс. Это не перестраховка: Dism.exe с
        // отложенной перезагрузкой печатает отчёт, потом спрашивает «Do you want to restart the
        // computer now? (Y/N)» и ждёт ответа вечно (10.09.2026: отчёт готов за 39 с, процесс жив
        // через 12 минут, приложение при этом стояло до таймаута в 20 минут).
        private static bool RunCapture(string exe, string args, int timeoutMs, out string stdout, out int exitCode,
                                       Encoding encoding, Func<bool> cancel,
                                       Action<string, long> onProgress, Func<string, bool> stopWhen)
        {
            stdout = string.Empty;
            exitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                // stdin перенаправляем и сразу закрываем. У нас GUI-процесс без консоли: утилита,
                // решившая что-нибудь спросить, иначе ждёт ответа, которого никто не даст.
                // С закрытым stdin она получает конец файла и идёт дальше.
                psi.RedirectStandardInput = true;
                psi.StandardOutputEncoding = encoding ?? Encoding.UTF8;
                psi.StandardErrorEncoding = encoding ?? Encoding.UTF8;
                // winget иначе рисует прогресс-спиннер и ждёт нажатий
                psi.EnvironmentVariables["WINGET_DISABLE_INTERACTIVITY"] = "1";
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    try { p.StandardInput.Close(); } catch { }
                    // оба потока вычитываются отдельно: не вычитанная труба заполняется,
                    // и процесс встаёт навсегда
                    StringBuilder err = new StringBuilder();
                    RunOutput outp = new RunOutput(stopWhen);
                    Thread drainErr = new Thread(delegate() { try { err.Append(p.StandardError.ReadToEnd()); } catch { } });
                    Thread drainOut = new Thread(delegate() { outp.Pump(p.StandardOutput); });
                    drainErr.IsBackground = true; drainOut.IsBackground = true;
                    drainErr.Start(); drainOut.Start();

                    Stopwatch sw = Stopwatch.StartNew();
                    bool exited = false, cancelled = false, early = false;
                    long doneAt = -1;
                    while (true)
                    {
                        if (p.WaitForExit(200)) { exited = true; break; }
                        long ms = sw.ElapsedMilliseconds;
                        if (ms >= timeoutMs) break;
                        if (cancel != null && cancel()) { cancelled = true; break; }
                        if (outp.Done)
                        {
                            if (doneAt < 0) doneAt = ms;
                            else if (ms - doneAt >= 5000) { early = true; break; }
                        }
                        if (onProgress != null) { try { onProgress(outp.Current, ms); } catch { } }
                    }
                    if (!exited)
                    {
                        // код вызывающему: -2 = таймаут, -3 = отменено, -4 = вывод получен,
                        // но процесс завис (а -1 = не запустился)
                        exitCode = early ? RunEarly : cancelled ? RunCancelled : RunTimeout;
                        try { p.Kill(); } catch { }
                        try { drainOut.Join(2000); } catch { }
                        try { drainErr.Join(1000); } catch { }
                        stdout = outp.Text;
                        // то, что утилита успела сказать, теряться не должно: раньше по таймауту
                        // вызывающий получал пустоту и не мог объяснить пользователю, что случилось
                        if (stdout.Length == 0 && err.Length > 0) stdout = err.ToString();
                        return early;
                    }
                    try { drainOut.Join(5000); } catch { }
                    try { drainErr.Join(2000); } catch { }
                    exitCode = p.ExitCode;
                    stdout = outp.Text;
                    if (stdout.Length == 0 && err.Length > 0) stdout = err.ToString();
                    return true;
                }
            }
            catch { return false; }   // утилиты нет в PATH — это нормально
        }

        // Потоковое чтение stdout: строки видны сразу, а не одним куском после выхода утилиты.
        // Нужно ровно для двух вещей — показать живой прогресс и понять, что работа сделана,
        // даже если процесс потом не завершается.
        private sealed class RunOutput
        {
            private readonly StringBuilder _all = new StringBuilder();
            private readonly StringBuilder _line = new StringBuilder();
            private readonly Func<string, bool> _stopWhen;
            private readonly object _gate = new object();
            private volatile string _current = "";
            private volatile bool _done;
            private int _tick;

            public RunOutput(Func<string, bool> stopWhen) { _stopWhen = stopWhen; }
            public bool Done { get { return _done; } }
            public string Current { get { return _current; } }
            public string Text { get { lock (_gate) { return _all.ToString(); } } }

            public void Pump(StreamReader r)
            {
                char[] buf = new char[4096];
                try
                {
                    int n;
                    while ((n = r.Read(buf, 0, buf.Length)) > 0)
                    {
                        lock (_gate) { _all.Append(buf, 0, n); }
                        for (int i = 0; i < n; i++) Feed(buf[i]);
                    }
                }
                catch { }
                Feed('\n');   // хвост без перевода строки тоже должен дойти до stopWhen
            }

            // Прогресс-бар DISM рисуется забоями (\b) поверх той же строки: без их обработки
            // «строкой» стал бы весь бар целиком вместе с историей своих перерисовок.
            private void Feed(char ch)
            {
                if (ch == '\b') { if (_line.Length > 0) _line.Length--; return; }
                if (ch != '\n' && ch != '\r')
                {
                    _line.Append(ch);
                    int t = Environment.TickCount;
                    if (t - _tick >= 200) { _tick = t; _current = _line.ToString(); }
                    return;
                }
                if (_line.Length == 0) return;
                string s = _line.ToString();
                _line.Length = 0;
                if (s.Trim().Length > 0) _current = s.Trim();
                if (!_done && _stopWhen != null)
                {
                    try { if (_stopWhen(s)) _done = true; }
                    catch { }
                }
            }
        }

        // winget выравнивает таблицу по ЯЧЕЙКАМ терминала, а не по символам: восточноазиатский
        // широкий / полноширинный символ (CJK, хангыль, полноширинная латиница, часть эмодзи)
        // занимает две ячейки. Строка с именем «QQ小程序开发者工具» поэтому короче заголовка на 8
        // символов, и срез по символьным позициям заголовка отрезал бы Id до «QQDevTools» —
        // `winget upgrade --id … --exact` такого пакета не найдёт. Кириллица, «…» и прочие
        // «неоднозначные» по ширине символы у winget — одна ячейка (проверено на живом выводе).
        // Символы вне BMP (суррогатная пара) считаются по кодовой точке: эмодзи и иероглифы
        // дополнительных плоскостей — две ячейки, прочие — одна, как считает winget.
        private static readonly int[] _wideRanges = {
            0x1100, 0x115F, 0x231A, 0x231B, 0x2329, 0x232A, 0x23E9, 0x23EC, 0x23F0, 0x23F0, 0x23F3, 0x23F3,
            0x25FD, 0x25FE, 0x2614, 0x2615, 0x2648, 0x2653, 0x267F, 0x267F, 0x2693, 0x2693, 0x26A1, 0x26A1,
            0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26CE, 0x26CE, 0x26D4, 0x26D4, 0x26EA, 0x26EA,
            0x26F2, 0x26F3, 0x26F5, 0x26F5, 0x26FA, 0x26FA, 0x26FD, 0x26FD, 0x2705, 0x2705, 0x270A, 0x270B,
            0x2728, 0x2728, 0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757, 0x2795, 0x2797,
            0x27B0, 0x27B0, 0x27BF, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55, 0x2E80, 0x303E,
            0x3041, 0x3247, 0x3250, 0x33FF, 0x3400, 0x4DBF, 0x4E00, 0x9FFF, 0xA000, 0xA4CF, 0xA960, 0xA97F, 0xAC00, 0xD7A3,
            0xF900, 0xFAFF, 0xFE10, 0xFE19, 0xFE30, 0xFE6F, 0xFF00, 0xFF60, 0xFFE0, 0xFFE6 };

        // Широкие (W) кодовые точки вне BMP: иероглифы дополнительных плоскостей, кана, эмодзи.
        private static readonly int[] _wideSupp = {
            0x16FE0, 0x16FE4, 0x17000, 0x18AFF, 0x1B000, 0x1B2FF, 0x1F004, 0x1F004, 0x1F0CF, 0x1F0CF,
            0x1F18E, 0x1F18E, 0x1F191, 0x1F19A, 0x1F200, 0x1F202, 0x1F210, 0x1F23B, 0x1F240, 0x1F248,
            0x1F250, 0x1F251, 0x1F260, 0x1F265, 0x1F300, 0x1F320, 0x1F32D, 0x1F335, 0x1F337, 0x1F37C,
            0x1F37E, 0x1F393, 0x1F3A0, 0x1F3CA, 0x1F3CF, 0x1F3D3, 0x1F3E0, 0x1F3F0, 0x1F3F4, 0x1F3F4,
            0x1F3F8, 0x1F43E, 0x1F440, 0x1F440, 0x1F442, 0x1F4FC, 0x1F4FF, 0x1F53D, 0x1F54B, 0x1F54E,
            0x1F550, 0x1F567, 0x1F57A, 0x1F57A, 0x1F595, 0x1F596, 0x1F5A4, 0x1F5A4, 0x1F5FB, 0x1F64F,
            0x1F680, 0x1F6C5, 0x1F6CC, 0x1F6CC, 0x1F6D0, 0x1F6D2, 0x1F6D5, 0x1F6D7, 0x1F6EB, 0x1F6EC,
            0x1F6F4, 0x1F6FC, 0x1F7E0, 0x1F7EB, 0x1F90C, 0x1F93A, 0x1F93C, 0x1F945, 0x1F947, 0x1F9FF,
            0x1FA70, 0x1FAFF, 0x20000, 0x2FFFD, 0x30000, 0x3FFFD };

        // Ширина символа на позиции i с учётом суррогатных пар: старшая половина несёт ширину
        // всей кодовой точки, младшая — 0 (уже учтена), одиночный суррогат — 1.
        public static int CellWidthAt(string s, int i)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                int cp = char.ConvertToUtf32(c, s[i + 1]);
                for (int k = 0; k < _wideSupp.Length; k += 2)
                {
                    if (cp < _wideSupp[k]) return 1;
                    if (cp <= _wideSupp[k + 1]) return 2;
                }
                return 1;
            }
            if (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(s[i - 1])) return 0;
            return CellWidth(c);
        }

        public static int CellWidth(char c)
        {
            if (c < 0x1100) return 1;
            if (c >= 0xD800 && c <= 0xDFFF) return 1;
            for (int i = 0; i < _wideRanges.Length; i += 2)
            {
                if (c < _wideRanges[i]) return 1;
                if (c <= _wideRanges[i + 1]) return 2;
            }
            return 1;
        }

        // Индекс символа, с которого начинается ячейка cell. Граница колонки в таблице всегда
        // приходится на пробел, но если она попала внутрь широкого символа — берём следующий.
        public static int CellToIndex(string line, int cell)
        {
            int cells = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (cells >= cell && !(char.IsLowSurrogate(line[i]) && i > 0 && char.IsHighSurrogate(line[i - 1]))) return i;
                cells += CellWidthAt(line, i);
            }
            return line.Length;
        }

        // Начала колонок (в ячейках) по строке заголовка: колонка начинается там, где после
        // разрыва в 2+ пробела снова идёт непробельный символ.
        public static List<int> ColumnStarts(string header)
        {
            List<int> starts = new List<int>();
            if (string.IsNullOrEmpty(header)) return starts;
            starts.Add(0);
            int i = 0, cell = 0;
            while (i < header.Length)
            {
                if (header[i] == ' ')
                {
                    int j = i;
                    while (j < header.Length && header[j] == ' ') j++;
                    if (j - i >= 2 && j < header.Length) starts.Add(cell + (j - i));
                    cell += j - i;
                    i = j;
                }
                else { cell += CellWidthAt(header, i); i++; }
            }
            return starts;
        }

        private static string Slice(string line, List<int> starts, int col)
        {
            if (col >= starts.Count) return string.Empty;
            int a = CellToIndex(line, starts[col]);
            if (a >= line.Length) return string.Empty;
            int b = (col + 1 < starts.Count) ? CellToIndex(line, starts[col + 1]) : line.Length;
            if (b <= a) return string.Empty;
            return line.Substring(a, b - a).Trim();
        }

        private static bool IsDashLine(string t)
        {
            return t.Length > 10 && t.Replace("-", "").Length == 0;
        }

        public static List<UpdateItem> ParseWingetTable(string text)
        {
            List<UpdateItem> list = new List<UpdateItem>();
            if (string.IsNullOrEmpty(text)) return list;

            List<string> lines = new List<string>();
            foreach (string l in text.Replace("\r", "\n").Split('\n'))
                if (l.Trim().Length > 0) lines.Add(l);

            List<int> starts = null;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (IsDashLine(line.Trim())) continue;

                // Строка перед разделителем из дефисов — заголовок СВОЕЙ таблицы: winget печатает
                // вторую («требуется явное указание для обновления») со своими ширинами колонок,
                // и её заголовок раньше попадал в список как пакет «Имя / ИД».
                if (i + 1 < lines.Count && IsDashLine(lines[i + 1].Trim()))
                {
                    List<int> s = ColumnStarts(line);
                    starts = s.Count >= 4 ? s : null;   // не та таблица
                    continue;
                }
                if (starts == null) continue;

                string id = Slice(line, starts, 1);
                // Хвост вида "35 upgrades available." и заметки про пины колонок не имеют
                if (id.Length == 0 || id.IndexOf(' ') >= 0) continue;
                string name = Slice(line, starts, 0);
                string cur = Slice(line, starts, 2);
                string avail = Slice(line, starts, 3);
                if (name.Length == 0 || avail.Length == 0) continue;

                UpdateItem u = new UpdateItem();
                u.Name = name;
                u.Id = id;
                u.Current = cur;
                u.Available = avail;
                u.Manager = "winget";
                u.Source = starts.Count > 4 ? Slice(line, starts, 4) : "winget";
                list.Add(u);
            }
            return list;
        }

        public static List<UpdateItem> ParseChocoOutdated(string text)
        {
            List<UpdateItem> list = new List<UpdateItem>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string line in text.Replace("\r", "\n").Split('\n'))
            {
                // формат -r: name|current|available|pinned
                string[] p = line.Split('|');
                if (p.Length < 4) continue;
                string name = p[0].Trim();
                if (name.Length == 0 || name.IndexOf(' ') >= 0) continue;
                bool pinned;
                if (!bool.TryParse(p[3].Trim(), out pinned)) continue;   // отсекает заголовки/мусор
                if (pinned) continue;
                if (p[1].Trim() == p[2].Trim()) continue;

                UpdateItem u = new UpdateItem();
                u.Name = name;
                u.Id = name;
                u.Current = p[1].Trim();
                u.Available = p[2].Trim();
                u.Manager = "choco";
                u.Source = "chocolatey";
                list.Add(u);
            }
            return list;
        }

        // Сопоставление choco-пакета с winget-пакетом: сравниваем нормализованное имя
        // с последним сегментом winget-Id (Graphviz.Graphviz -> graphviz). Совпало — помечаем
        // дублем, но НЕ скрываем: решение остаётся за пользователем.
        private static string NormalizeKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // Один и тот же софт в winget и choco зовётся по-разному:
        // Graphviz.Graphviz/graphviz, Python.Python.3.14/python314, python3,
        // Microsoft.VCRedist.2015+.x64/vcredist140. Точного маппинга между
        // менеджерами не существует (его нет и в UniGetUI), поэтому сравниваем
        // нормализованные строки: имя choco против всего Id и против каждого
        // сегмента Id, в обе стороны. Порог 5 символов отсекает мусорные
        // совпадения вроде "x64" и "2015".
        // Ошибка здесь безопасна в обе стороны: пометка «дубль» только
        // предупреждает и исключает строку из кнопки «Все» — ничего не удаляет
        // и не блокирует ручную отметку.
        // Токены разрядности/года совпадают у чего угодно и значат не софт, а вариант
        // сборки: сами по себе они не признак одного и того же пакета.
        private static readonly string[] _archTokens = { "x64", "x86", "arm64", "arm", "win32", "win64", "amd64" };

        private static bool IsJunkToken(string k)
        {
            if (k.Length == 0) return true;
            bool allDigits = true;
            foreach (char c in k) if (!char.IsDigit(c)) { allDigits = false; break; }
            if (allDigits) return true;
            foreach (string t in _archTokens) if (k == t) return true;
            return false;
        }

        // Версии в реальном выводе бывают "7.0.6 (43848)", "< 17.14.35", "1.29.279.0",
        // "26.00", "v2.5.1", "Unknown". Берём числовые группы по порядку и игнорируем
        // всё остальное: сравнивать нужно только цифры.
        public static int[] ParseVersionParts(string v)
        {
            if (string.IsNullOrEmpty(v)) return new int[0];
            if (v.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0) return new int[0];
            List<int> parts = new List<int>();
            int i = 0;
            while (i < v.Length && parts.Count < 6)
            {
                if (!char.IsDigit(v[i])) { i++; continue; }
                int start = i;
                while (i < v.Length && char.IsDigit(v[i])) i++;
                string num = v.Substring(start, i - start);
                if (num.Length > 9) num = num.Substring(0, 9);   // защита от переполнения
                int n;
                if (int.TryParse(num, out n)) parts.Add(n);
            }
            return parts.ToArray();
        }

        private static int PartAt(int[] a, int i) { return i < a.Length ? a[i] : 0; }

        // "<" в текущей версии значит «winget не знает точную» — доверия к сравнению нет.
        public static void ClassifySeverity(UpdateItem u)
        {
            if (u == null) return;
            int[] cur = ParseVersionParts(u.Current);
            int[] av = ParseVersionParts(u.Available);
            bool fuzzy = !string.IsNullOrEmpty(u.Current) && u.Current.IndexOf('<') >= 0;

            if (cur.Length == 0 || av.Length == 0 || fuzzy)
            {
                u.SeverityLevel = 0;
                u.SeverityText = Tr.S("неизвестно", "unknown");
                return;
            }

            if (PartAt(av, 0) != PartAt(cur, 0))
            {
                u.SeverityLevel = 3;
                u.SeverityText = Tr.S("крупное", "major");
                return;
            }
            // Semver 0.x: там ломающие изменения выходят во второй позиции, а не в первой.
            if (PartAt(cur, 0) == 0 && PartAt(av, 1) != PartAt(cur, 1))
            {
                u.SeverityLevel = 3;
                u.SeverityText = Tr.S("крупное", "major");
                return;
            }
            if (PartAt(av, 1) != PartAt(cur, 1))
            {
                u.SeverityLevel = 2;
                u.SeverityText = Tr.S("среднее", "minor");
                return;
            }
            u.SeverityLevel = 1;
            u.SeverityText = Tr.S("мелкое", "patch");
        }

        public static bool LooksLikeSameSoftware(string chocoName, List<string> wingetIds)
        {
            string c = NormalizeKey(chocoName);
            if (c.Length < 3 || wingetIds == null || IsJunkToken(c)) return false;
            foreach (string id in wingetIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                string full = NormalizeKey(id);
                if (full.Length == 0) continue;
                if (full == c) return true;
                if (c.Length >= 5 && full.IndexOf(c, StringComparison.Ordinal) >= 0) return true;
                foreach (string part in id.Split('.'))
                {
                    string p = NormalizeKey(part);
                    if (IsJunkToken(p)) continue;
                    if (p == c) return true;
                    if (p.Length >= 5 && c.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
                }
            }
            return false;
        }

        public List<UpdateItem> ScanUpdates(out string note)
        {
            note = null;
            List<UpdateItem> all = new List<UpdateItem>();
            List<string> notes = new List<string>();

            if (HasWinget)
            {
                string args = "upgrade --accept-source-agreements --disable-interactivity";
                if (Config.UpdateIncludeUnknown) args += " --include-unknown";
                string so; int code;
                // Строка состояния нужна и на поиске: без неё вкладка пять минут показывала
                // один секундомер, хотя winget в это время перебирал источники.
                if (RunCapture("winget.exe", args, 300000, out so, out code, null, UpdateCancelPoll(),
                               UpdateProgressPoll(Tr.S("winget: поиск обновлений", "winget: checking for updates")), null))
                    all.AddRange(ParseWingetTable(so));
                else
                    notes.Add("winget: " + RunFailText(false, code));
            }
            else notes.Add(Tr.S("winget не найден", "winget not found"));

            if (_cancelUpdates) { note = Tr.S("Отменено", "Cancelled"); return all; }

            if (Config.UpdateUseChoco && HasChoco)
            {
                string so; int code;
                // choco outdated возвращает 2, когда обновления есть — это не ошибка
                if (RunCapture("choco.exe", "outdated -r --limit-output --no-color", 300000, out so, out code, null, UpdateCancelPoll(),
                               UpdateProgressPoll(Tr.S("choco: поиск обновлений", "choco: checking for updates")), null))
                {
                    List<UpdateItem> ch = ParseChocoOutdated(so);
                    List<string> wingetIds = new List<string>();
                    foreach (UpdateItem w in all) wingetIds.Add(w.Id);
                    foreach (UpdateItem c in ch)
                        c.Duplicate = LooksLikeSameSoftware(c.Name, wingetIds);
                    all.AddRange(ch);
                }
                else notes.Add("choco: " + RunFailText(false, code));
            }

            _updateStatus = null;   // поиск закончен: строка состояния не должна «застыть» на нём

            // исключения пользователя
            if (Config.UpdateExclude != null && Config.UpdateExclude.Count > 0)
            {
                Dictionary<string, bool> skip = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (string s in Config.UpdateExclude)
                {
                    string v = (s ?? "").Trim();
                    if (v.Length > 0) skip[v] = true;
                }
                List<UpdateItem> kept = new List<UpdateItem>();
                foreach (UpdateItem u in all)
                    if (!skip.ContainsKey(u.Id) && !skip.ContainsKey(u.Name)) kept.Add(u);
                all = kept;
            }

            foreach (UpdateItem u in all) ClassifySeverity(u);

            // Крупные наверх: список длинный, и то, что меняет мажорную версию,
            // пользователь должен увидеть без прокрутки.
            all.Sort(delegate(UpdateItem a, UpdateItem b)
            {
                if (a.SeverityLevel != b.SeverityLevel) return b.SeverityLevel - a.SeverityLevel;
                int d = string.Compare(a.Manager, b.Manager, StringComparison.OrdinalIgnoreCase);
                if (d != 0) return d;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            if (notes.Count > 0) note = string.Join(" · ", notes.ToArray());
            return all;
        }

        // Обновление одного пакета силами самого менеджера. Возвращает true при успехе.
        public bool ApplyUpdate(UpdateItem u, out string message)
        {
            message = null;
            if (u == null) return false;
            string exe, args;
            if (u.Manager == "choco")
            {
                exe = "choco.exe";
                args = "upgrade " + u.Id + " -y --no-progress --limit-output";
            }
            else
            {
                exe = "winget.exe";
                args = "upgrade --id " + u.Id + " --exact --silent --disable-interactivity"
                     + " --accept-package-agreements --accept-source-agreements";
                // без этого пакеты с Current=Unknown winget обновлять отказывается
                if (string.IsNullOrEmpty(u.Current) ||
                    u.Current.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0)
                    args += " --include-unknown";
            }

            string so; int code;
            bool finished = RunCapture(exe, args, 1800000, out so, out code, null, UpdateCancelPoll(),
                                       UpdateProgressPoll(u.Name), null);   // 30 мин на установщик
            _updateStatus = null;
            if (!finished)
            {
                message = RunFailText(false, code);
                u.Status = message;
                u.LastOk = false;
                AppendUpdateLog(u, false, message);
                return false;
            }
            bool ok = IsInstallOk(code);
            if (!ok)
            {
                string tail = LastMeaningfulLine(so);
                message = Tr.S("код ", "exit ") + code + (tail.Length > 0 ? ": " + tail : "");
            }
            else message = Tr.S("обновлено до ", "updated to ") + u.Available
                         + (NeedsReboot(code) ? Tr.S(" · нужна перезагрузка", " · restart required") : "");
            u.Status = message;
            u.LastOk = ok;
            AppendUpdateLog(u, ok, message);
            return ok;
        }

        // Что менеджер СЕЙЧАС считает устаревшим: ключ → доступная версия.
        // Нужно для проверки результата группового обновления.
        private Dictionary<string, string> QueryOutdatedMap(string manager)
        {
            string so; int code;
            List<UpdateItem> list;
            if (manager == "choco")
            {
                if (!RunCapture("choco.exe", "outdated -r --limit-output --no-color", 300000, out so, out code, null, UpdateCancelPoll(),
                                UpdateProgressPoll(Tr.S("choco: проверяю результат", "choco: verifying the result")), null))
                    return null;
                list = ParseChocoOutdated(so);
            }
            else
            {
                string a = "upgrade --accept-source-agreements --disable-interactivity";
                if (Config.UpdateIncludeUnknown) a += " --include-unknown";
                if (!RunCapture("winget.exe", a, 300000, out so, out code, null, UpdateCancelPoll(),
                                UpdateProgressPoll(Tr.S("winget: проверяю результат", "winget: verifying the result")), null)) return null;
                list = ParseWingetTable(so);
            }
            _updateStatus = null;
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (UpdateItem u in list) map[u.Id] = u.Available;
            return map;
        }

        // Разбивка выбранного на команды. Смешивать менеджеров в одной команде нельзя,
        // поэтому группируем сначала по менеджеру, потом нарезаем по batch. Порядок
        // внутри менеджера сохраняем — он уже отсортирован по важности.
        public static List<List<UpdateItem>> BuildUpdateGroups(List<UpdateItem> sel, int batch)
        {
            List<List<UpdateItem>> groups = new List<List<UpdateItem>>();
            if (sel == null || sel.Count == 0) return groups;
            if (batch < 1) batch = 1;

            List<string> managers = new List<string>();
            foreach (UpdateItem u in sel)
            {
                string m = u.Manager ?? "";
                if (!managers.Contains(m)) managers.Add(m);
            }
            foreach (string mgr in managers)
            {
                List<UpdateItem> ofMgr = new List<UpdateItem>();
                foreach (UpdateItem u in sel) if ((u.Manager ?? "") == mgr) ofMgr.Add(u);
                for (int i = 0; i < ofMgr.Count; i += batch)
                {
                    List<UpdateItem> g = new List<UpdateItem>();
                    for (int j = i; j < ofMgr.Count && j < i + batch; j++) g.Add(ofMgr[j]);
                    groups.Add(g);
                }
            }
            return groups;
        }

        private static string QuoteArg(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return "\"" + s.Replace("\"", "") + "\"";
        }

        // Групповое обновление: менеджеру отдаётся сразу несколько пакетов одной
        // командой — оба это принимают (проверено живьём: `winget upgrade <q1> <q2>`,
        // `choco upgrade a b`).
        //
        // Настоящей ПАРАЛЛЕЛЬНОСТИ здесь нет и быть не может: Windows Installer держит
        // машинный мьютекс _MSIExecute, а Chocolatey — свой глобальный лок, поэтому два
        // установщика, запущенных одновременно, просто отвалятся с ошибкой. Внутри
        // одной команды менеджер ставит пакеты по очереди сам; выигрыш — старт процесса
        // и загрузка индекса источника один раз на группу, а не на каждый пакет.
        //
        // Результат по каждому пакету НЕ вытаскиваем из вывода: он локализован (у
        // пользователя winget отвечает по-русски) и его формат не документирован.
        // Вместо этого повторно спрашиваем менеджер, что ещё устарело, и сверяем —
        // это верно в любой локали. Возвращает число успешно обновлённых, статус
        // каждого пакета кладёт в u.Status.
        public int ApplyUpdateBatch(List<UpdateItem> group, out string groupMessage)
        {
            groupMessage = null;
            if (group == null || group.Count == 0) return 0;

            string manager = group[0].Manager;
            StringBuilder ids = new StringBuilder();
            bool anyUnknown = false;
            foreach (UpdateItem u in group)
            {
                ids.Append(' ').Append(QuoteArg(u.Id));
                if (string.IsNullOrEmpty(u.Current) ||
                    u.Current.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0) anyUnknown = true;
            }

            string exe, args;
            if (manager == "choco")
            {
                exe = "choco.exe";
                args = "upgrade" + ids + " -y --no-progress --limit-output";
            }
            else
            {
                exe = "winget.exe";
                args = "upgrade --exact --silent --disable-interactivity"
                     + " --accept-package-agreements --accept-source-agreements" + ids;
                if (anyUnknown) args += " --include-unknown";
            }

            // 30 мин на первый установщик + 15 на каждый следующий, но не больше 2 часов
            int timeout = 1800000 + 900000 * (group.Count - 1);
            if (timeout > 7200000) timeout = 7200000;

            string so; int code;
            bool finished = RunCapture(exe, args, timeout, out so, out code, null, UpdateCancelPoll(),
                                       UpdateProgressPoll(GroupNames(group)), null);
            _updateStatus = null;
            if (!finished)
            {
                groupMessage = RunFailText(false, code);
                foreach (UpdateItem u in group)
                {
                    u.Status = groupMessage;
                    u.LastOk = false;
                    AppendUpdateLog(u, false, groupMessage);
                }
                return 0;
            }

            Dictionary<string, string> still = QueryOutdatedMap(manager);
            int ok = 0;

            if (still == null)
            {
                // Проверить не смогли — честно говорим это, а не выдаём код за успех.
                bool good = IsInstallOk(code);
                groupMessage = Tr.S("код ", "exit ") + code;
                foreach (UpdateItem u in group)
                {
                    u.Status = good
                        ? Tr.S("вероятно обновлено (проверка недоступна)", "probably updated (verify unavailable)")
                        : Tr.S("код ", "exit ") + code + ": " + LastMeaningfulLine(so);
                    u.LastOk = good;
                    if (good) ok++;
                    AppendUpdateLog(u, good, u.Status);
                }
                return ok;
            }

            foreach (UpdateItem u in group)
            {
                string left;
                bool listed = still.TryGetValue(u.Id, out left);
                bool good;
                if (!listed) good = true;                                   // пропал из списка устаревших
                else if (!string.Equals(left, u.Available, StringComparison.OrdinalIgnoreCase))
                    good = true;                                            // подтянулся, но вышла ещё новее
                else good = false;                                          // так и остался на старой

                u.Status = good
                    ? Tr.S("обновлено до ", "updated to ") + u.Available
                    : Tr.S("не обновилось (осталось ", "not updated (still ") + u.Current + ")";
                u.LastOk = good;
                if (good) ok++;
                AppendUpdateLog(u, good, u.Status);
            }

            groupMessage = Tr.S("обновлено ", "updated ") + ok + "/" + group.Count;
            return ok;
        }

        private static string LastMeaningfulLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string[] lines = text.Replace("\r", "\n").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.Length > 0) return l.Length > 160 ? l.Substring(0, 160) : l;
            }
            return string.Empty;
        }

        private void AppendUpdateLog(UpdateItem u, bool ok, string message)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ");
                sb.Append(ok ? "OK   " : "FAIL ");
                sb.Append(u.Manager).Append("  ").Append(u.Id).Append("  ");
                sb.Append(u.Current).Append(" -> ").Append(u.Available);
                if (!string.IsNullOrEmpty(message)) sb.Append("  ").Append(message);
                File.AppendAllText(UpdateLogPath, sb.ToString() + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
