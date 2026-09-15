// SysDeck — запись лагов: пока включена, оверлей складывает время каждого кадра, показатели системы
// раз в секунду, самые занятые процессы раз в две секунды и события (смена окна, новый процесс, сброс частоты).
// После остановки в папке «Документы\SysDeck\Lag reports\<время>_<игра>\» лежат report.md (сводка,
// худшие фризы с тем, что творилось в системе в ту секунду, и подсказки) и CSV — их можно отдать ИИ целиком.
//
// Процесс оверлея бывает повышенным, а «Документы» пишет пользователь. Поэтому: папки создаются по одной и
// проверяются на ссылки (junction/symlink) — ссылку повышенный процесс не проходит; файлы — только новые
// (FileMode.CreateNew) с постоянными именами: ни перезаписать, ни подменить чужой файл так нельзя.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SysDeck.Capture
{
    internal sealed class HudLagRecorder : IDisposable
    {
        public const int MaxMinutes = 120;
        private const int MaxFrames = 2000000;

        // Показатели system.csv: id и заголовок столбца.
        internal static readonly string[][] SystemColumns =
        {
            new[] { "fps", "fps" }, new[] { "fps.low1", "fps_low1" }, new[] { "fps.frametime", "frametime_ms" },
            new[] { "cpu.load", "cpu_load_pct" }, new[] { "cpu.coremax", "cpu_top_core_pct" }, new[] { "cpu.perflimit", "cpu_perf_limit_pct" },
            new[] { "cpu.temp", "cpu_temp_c" }, new[] { "cpu.power", "cpu_power_w" },
            new[] { "gpu.load", "gpu_load_pct" }, new[] { "gpu.temp", "gpu_temp_c" }, new[] { "gpu.hotspot", "gpu_hotspot_c" },
            new[] { "gpu.power", "gpu_power_w" }, new[] { "gpu.throttle", "gpu_throttle" }, new[] { "vram", "vram_used_mb" },
            new[] { "ram", "ram_used_mb" }, new[] { "commit", "commit_mb" }, new[] { "ram.hardfaults", "hard_faults_pages_s" },
            new[] { "app.cpu", "game_cpu_pct" }, new[] { "app.ram", "game_ws_mb" }, new[] { "app.io.read", "game_read_mb_s" },
            new[] { "disk.read", "disk_read_mb_s" }, new[] { "disk.write", "disk_write_mb_s" }, new[] { "net.down", "net_down_mb_s" },
            new[] { "display.hz", "display_hz" }, new[] { "fps.bottleneck", "bottleneck" }, new[] { "fps.presentmode", "present_mode" },
            new[] { "fps.vsync", "vsync" }, new[] { "fps.api", "api" },
        };

        internal sealed class SysRow
        {
            public double T;                                    // секунды от начала
            public readonly Dictionary<string, double> Num = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Text = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal struct ProcRow { public double T; public int Pid; public string Name; public double Cpu, Faults, Read, Write, WsMb; }
        internal struct EventRow { public double T; public string Kind, Text; }

        public readonly string Folder;
        public readonly DateTime Started = DateTime.Now;
        private readonly long _startQpc = Stopwatch.GetTimestamp();
        private readonly string _game;
        private StreamWriter _frames, _system, _procs, _events;
        private long _lastQpc;
        private int _lastPid;
        private double _lastSystemT = -10, _lastProcT = -10;
        internal readonly List<float> FrameMs = new List<float>();
        internal readonly List<double> FrameT = new List<double>();
        internal readonly List<SysRow> Rows = new List<SysRow>();
        internal readonly List<ProcRow> Procs = new List<ProcRow>();
        internal readonly List<EventRow> Events = new List<EventRow>();
        private string _lastApp, _lastThrottle;
        private bool _limited;
        private readonly HudProcSampler _sampler = new HudProcSampler();
        private HashSet<int> _knownPids;

        private HudLagRecorder(string folder, string game)
        {
            Folder = folder;
            _game = game;
        }

        // Только для тестов: вместо «Документов» — временная папка.
        internal static string DocumentsOverride = null;

        private static string Documents
        {
            get { return DocumentsOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
        }

        public static string BaseFolder
        {
            get { return Path.Combine(Path.Combine(Documents, "SysDeck"), "Lag reports"); }
        }

        // null и причина — не получилось создать папку отчёта.
        public static HudLagRecorder Start(string game, out string error)
        {
            error = null;
            string safe = SafeName(game);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            try
            {
                string docs = Documents;
                if (string.IsNullOrEmpty(docs) || !Directory.Exists(docs)) { error = Tr.S("не найдена папка «Документы»", "the Documents folder was not found"); return null; }
                string dir = docs;
                foreach (string part in new[] { "SysDeck", "Lag reports", stamp + "_" + safe })
                {
                    dir = Path.Combine(dir, part);
                    Directory.CreateDirectory(dir);
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    {
                        error = Tr.S("папка отчётов оказалась ссылкой на другое место — запись отменена: ", "the report folder is a link to another place — recording cancelled: ") + dir;
                        return null;
                    }
                }
                HudLagRecorder r = new HudLagRecorder(dir, safe);
                r._frames = r.Open("frames.csv", "t_s,frametime_ms,pid");
                StringBuilder head = new StringBuilder("t_s");
                foreach (string[] c in SystemColumns) head.Append(',').Append(c[1]);
                r._system = r.Open("system.csv", head.ToString());
                r._procs = r.Open("processes.csv", "t_s,pid,name,cpu_pct,hard_faults_s,read_mb_s,write_mb_s,working_set_mb");
                r._events = r.Open("events.csv", "t_s,kind,text");
                r.Event("start", Tr.S("запись начата", "recording started") + " · " + game);
                return r;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        internal static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "desktop";
            StringBuilder sb = new StringBuilder();
            foreach (char ch in name)
            {
                if (sb.Length >= 40) break;
                sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
            }
            string s = sb.ToString().Trim('_');
            return s.Length == 0 ? "desktop" : s;
        }

        private StreamWriter Open(string name, string header)
        {
            FileStream fs = new FileStream(Path.Combine(Folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false));
            w.NewLine = "\n";
            w.WriteLine(header);
            return w;
        }

        public TimeSpan Elapsed { get { return DateTime.Now - Started; } }

        public bool Expired { get { return Elapsed.TotalMinutes >= MaxMinutes; } }

        public string HeaderText()
        {
            TimeSpan e = Elapsed;
            return "● " + Tr.S("Запись лагов ", "Lag recording ") + ((int)e.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + ":"
                   + e.Seconds.ToString("00", CultureInfo.InvariantCulture) + " · " + _game;
        }

        private double Now() { return (Stopwatch.GetTimestamp() - _startQpc) / (double)Stopwatch.Frequency; }

        private static string F(double v)
        {
            return HudFormat.Valid(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "";
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"").Replace('\n', ' ') + "\"";
        }

        private void Event(string kind, string text)
        {
            EventRow e = new EventRow();
            e.T = Now(); e.Kind = kind; e.Text = text;
            Events.Add(e);
            if (_events != null) _events.WriteLine(F(e.T) + "," + kind + "," + Csv(text));
        }

        // Такт оверлея (250 мс): новые кадры — каждый раз, система — раз в секунду, процессы — раз в две.
        public void Sample(HudFrame frame, HudCollector collector)
        {
            double t = Now();
            if (collector != null && collector.Fps != null) AddFrames(collector.Fps.LastFrames, collector.Fps.LastFramesPid, collector.Fps.LastFreq);
            if (t - _lastSystemT >= 1) { _lastSystemT = t; AddSystem(frame, t); }
            if (t - _lastProcT >= 2) { _lastProcT = t; AddProcesses(t); }
        }

        internal void AddFrames(long[] ts, int pid, long freq)
        {
            if (ts == null || ts.Length == 0 || freq <= 0) return;
            if (pid != _lastPid) { _lastPid = pid; _lastQpc = 0; }
            for (int i = 0; i < ts.Length; i++)
            {
                if (ts[i] <= _lastQpc) continue;
                long prev = i > 0 && ts[i - 1] > 0 ? Math.Max(ts[i - 1], _lastQpc) : _lastQpc;
                if (prev > 0 && FrameMs.Count < MaxFrames)
                {
                    double ms = (ts[i] - prev) * 1000.0 / freq;
                    double at = (ts[i] - _startQpc) / (double)freq;
                    if (ms > 0 && ms < 60000)
                    {
                        FrameMs.Add((float)ms);
                        FrameT.Add(at);
                        if (_frames != null) _frames.WriteLine(F(at) + "," + ms.ToString("0.###", CultureInfo.InvariantCulture) + "," + pid.ToString(CultureInfo.InvariantCulture));
                    }
                }
                _lastQpc = ts[i];
            }
        }

        internal void AddSystem(HudFrame frame, double t)
        {
            if (frame == null) return;
            SysRow row = new SysRow();
            row.T = t;
            StringBuilder sb = new StringBuilder(F(t));
            foreach (string[] c in SystemColumns)
            {
                sb.Append(',');
                HudValue v = frame.Get(c[0]);
                if (v == null) continue;
                if (v.Kind == HudKind.Text)
                {
                    row.Text[c[0]] = v.Text;
                    sb.Append(Csv(v.Text));
                    continue;
                }
                double x = v.Value;
                if (!HudFormat.Valid(x)) continue;
                if (v.Kind == HudKind.Memory || v.Kind == HudKind.Bytes || v.Kind == HudKind.Rate) x /= 1024.0 * 1024.0;
                row.Num[c[0]] = x;
                if (v.Kind == HudKind.Memory && HudFormat.Valid(v.Total) && v.Total > 0) row.Num[c[0] + "%"] = v.Value * 100.0 / v.Total;
                sb.Append(F(x));
            }
            Rows.Add(row);
            if (_system != null) _system.WriteLine(sb.ToString());

            HudValue app = frame.Get("app.name");
            string appName = app != null ? app.Text : null;
            if (appName != null && appName != _lastApp)
            {
                if (_lastApp != null) Event("foreground", Tr.S("активное окно: ", "foreground: ") + appName);
                _lastApp = appName;
            }
            string throttle;
            if (row.Text.TryGetValue("gpu.throttle", out throttle) && throttle != _lastThrottle)
            {
                if (_lastThrottle != null) Event("gpu-throttle", throttle);
                _lastThrottle = throttle;
            }
            double limit;
            bool limited = row.Num.TryGetValue("cpu.perflimit", out limit) && limit < 90;
            if (limited != _limited)
            {
                Event("cpu-limit", limited ? Tr.S("частота процессора сброшена до ", "CPU frequency capped at ") + F(limit) + " %" : Tr.S("частота процессора снова полная", "CPU frequency back to full"));
                _limited = limited;
            }
        }

        private void AddProcesses(double t)
        {
            List<HudProcSampler.Proc> list = _sampler.Sample();
            if (list == null) return;
            HashSet<int> pids = new HashSet<int>();
            foreach (HudProcSampler.Proc p in list) pids.Add(p.Pid);
            if (_knownPids != null)
                foreach (HudProcSampler.Proc p in list)
                    if (!_knownPids.Contains(p.Pid) && p.Pid > 4) Event("process-start", p.Name + " (" + p.Pid.ToString(CultureInfo.InvariantCulture) + ")");
            _knownPids = pids;
            list.Sort(delegate(HudProcSampler.Proc a, HudProcSampler.Proc b) { return (b.Cpu + b.Read / 8 + b.Faults / 50).CompareTo(a.Cpu + a.Read / 8 + a.Faults / 50); });
            int n = 0;
            foreach (HudProcSampler.Proc p in list)
            {
                if (n >= 10 || (p.Cpu < 1 && p.Faults < 20 && p.Read < 1 && p.Write < 1)) break;
                if (p.Pid == 0) continue;
                ProcRow r = new ProcRow();
                r.T = t; r.Pid = p.Pid; r.Name = p.Name; r.Cpu = p.Cpu; r.Faults = p.Faults; r.Read = p.Read; r.Write = p.Write; r.WsMb = p.WsMb;
                Procs.Add(r);
                if (_procs != null)
                    _procs.WriteLine(F(t) + "," + p.Pid.ToString(CultureInfo.InvariantCulture) + "," + Csv(p.Name) + "," + F(p.Cpu) + "," + F(p.Faults) + "," + F(p.Read) + "," + F(p.Write) + "," + F(p.WsMb));
                n++;
            }
        }

        // Закрыть файлы и написать report.md. Возвращает путь к отчёту (или null, если записать не удалось).
        public string Stop()
        {
            Event("stop", Tr.S("запись остановлена", "recording stopped"));
            Dispose();
            string path = Path.Combine(Folder, "report.md");
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false)))
                    w.Write(HudLagReport.Build(this, _game, Started, Now()));
                return path;
            }
            catch (Exception ex)
            {
                CapLog.Report(ex);
                return null;
            }
        }

        private void CloseWriters()
        {
            foreach (StreamWriter w in new[] { _frames, _system, _procs, _events })
                if (w != null) { try { w.Dispose(); } catch { } }
            _frames = _system = _procs = _events = null;
        }

        public void Dispose()
        {
            CloseWriters();
            _sampler.Dispose();
        }
    }

    // ------------------------------------------------------------------ //
    //  Процессы: процессор, жёсткие ошибки страниц и чтение-запись одним вызовом NtQuerySystemInformation
    // ------------------------------------------------------------------ //
    internal sealed class HudProcSampler : IDisposable
    {
        internal struct Proc { public int Pid; public string Name; public double Cpu, Faults, Read, Write, WsMb; }
        private struct Raw { public string Name; public long Cpu, Faults, Read, Write; }

        private IntPtr _buf = IntPtr.Zero;
        private int _size;
        private Dictionary<int, Raw> _prev;
        private long _prevAt;

        private const int UserTime = 40, KernelTime = 48;

        public List<Proc> Sample()
        {
            if (_size == 0) { _size = 1 << 20; _buf = Marshal.AllocHGlobal(_size); }
            int got = 0, rc = 0;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                rc = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _buf, _size, out got);
                if ((uint)rc != Native.STATUS_INFO_LENGTH_MISMATCH) break;
                Marshal.FreeHGlobal(_buf);
                _size = got > 0 ? got + (1 << 18) : _size * 2;
                _buf = Marshal.AllocHGlobal(_size);
            }
            if (rc != 0) return null;
            long at = Stopwatch.GetTimestamp();
            double sec = _prev == null ? 0 : (at - _prevAt) / (double)Stopwatch.Frequency;
            Native.ProcInfoLayout L = Native.ProcLayout;
            int readOff = IntPtr.Size == 8 ? 232 : 160, writeOff = readOff + 8;
            Dictionary<int, Raw> now = new Dictionary<int, Raw>();
            List<Proc> list = new List<Proc>();
            long baseAddr = _buf.ToInt64();
            int off = 0;
            int cores = Math.Max(1, Environment.ProcessorCount);
            while (off >= 0 && off + 256 <= _size)
            {
                IntPtr e = new IntPtr(baseAddr + off);
                int next = Marshal.ReadInt32(e, L.NextEntry);
                int pid = (int)Native.ReadPtr(e, L.UniqueProcessId);
                Raw r = new Raw();
                r.Name = Native.ReadImageName(e) ?? (pid == 0 ? "Idle" : "pid " + pid.ToString(CultureInfo.InvariantCulture));
                r.Cpu = Marshal.ReadInt64(e, UserTime) + Marshal.ReadInt64(e, KernelTime);
                r.Faults = Native.ReadU32(e, L.HardFaultCount);
                r.Read = Marshal.ReadInt64(e, readOff);
                r.Write = Marshal.ReadInt64(e, writeOff);
                now[pid] = r;
                Raw old;
                if (sec > 0.1 && _prev.TryGetValue(pid, out old) && old.Name == r.Name)
                {
                    Proc p = new Proc();
                    p.Pid = pid;
                    p.Name = r.Name;
                    p.Cpu = Math.Max(0, r.Cpu - old.Cpu) / (double)TimeSpan.TicksPerSecond / sec / cores * 100.0;
                    p.Faults = Math.Max(0, r.Faults - old.Faults) / sec;
                    p.Read = Math.Max(0, r.Read - old.Read) / sec / (1024.0 * 1024.0);
                    p.Write = Math.Max(0, r.Write - old.Write) / sec / (1024.0 * 1024.0);
                    p.WsMb = Native.ReadPtr(e, L.WorkingSetSize) / (1024.0 * 1024.0);
                    list.Add(p);
                }
                if (next <= 0) break;
                off += next;
            }
            _prev = now;
            _prevAt = at;
            return sec > 0.1 ? list : null;
        }

        public void Dispose()
        {
            if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; _size = 0; }
        }
    }

    // ------------------------------------------------------------------ //
    //  report.md: сводка, худшие фризы с обстановкой, подсказки, описание файлов для ИИ
    // ------------------------------------------------------------------ //
    internal static class HudLagReport
    {
        internal sealed class Hitch { public double T, Ms; }

        internal sealed class Summary
        {
            public int Frames;
            public double Seconds, AvgFps, Low1, Low01, MedianMs, MaxMs;
            public List<Hitch> Hitches = new List<Hitch>();
        }

        // Фриз — кадр дольше и 2,5 медианы, и медианы плюс 16 мс (одного пропущенного кадра на 60 Гц мало, чтобы заметить).
        internal static Summary Analyze(IList<float> ms, IList<double> t)
        {
            Summary s = new Summary();
            s.Frames = ms.Count;
            if (ms.Count < 2) return s;
            double[] sorted = new double[ms.Count];
            double total = 0;
            for (int i = 0; i < ms.Count; i++) { sorted[i] = ms[i]; total += ms[i]; }
            Array.Sort(sorted);
            s.Seconds = total / 1000.0;
            s.AvgFps = ms.Count / Math.Max(0.001, s.Seconds);
            s.MedianMs = sorted[sorted.Length / 2];
            s.MaxMs = sorted[sorted.Length - 1];
            s.Low1 = 1000.0 / WorstMean(sorted, 0.01);
            s.Low01 = 1000.0 / WorstMean(sorted, 0.001);
            double limit = Math.Max(s.MedianMs * 2.5, s.MedianMs + 16);
            for (int i = 0; i < ms.Count; i++)
                if (ms[i] > limit)
                {
                    Hitch h = new Hitch();
                    h.T = t != null && i < t.Count ? t[i] : 0;
                    h.Ms = ms[i];
                    s.Hitches.Add(h);
                }
            return s;
        }

        private static double WorstMean(double[] sorted, double share)
        {
            int k = Math.Max(1, (int)Math.Round(sorted.Length * share));
            double sum = 0;
            for (int i = sorted.Length - k; i < sorted.Length; i++) sum += sorted[i];
            return sum / k;
        }

        private static string N(double v, string format)
        {
            return HudFormat.Valid(v) ? v.ToString(format, CultureInfo.InvariantCulture) : "—";
        }

        internal static HudLagRecorder.SysRow RowAt(List<HudLagRecorder.SysRow> rows, double t)
        {
            HudLagRecorder.SysRow best = null;
            foreach (HudLagRecorder.SysRow r in rows)
                if (best == null || Math.Abs(r.T - t) < Math.Abs(best.T - t)) best = r;
            return best != null && Math.Abs(best.T - t) <= 2 ? best : null;
        }

        private static double Get(HudLagRecorder.SysRow r, string id)
        {
            double v;
            return r != null && r.Num.TryGetValue(id, out v) ? v : double.NaN;
        }

        private static double Mean(List<HudLagRecorder.SysRow> rows, string id)
        {
            double sum = 0; int n = 0;
            foreach (HudLagRecorder.SysRow r in rows) { double v = Get(r, id); if (HudFormat.Valid(v)) { sum += v; n++; } }
            return n == 0 ? double.NaN : sum / n;
        }

        public static string Build(HudLagRecorder rec, string game, DateTime started, double seconds)
        {
            Summary s = Analyze(rec.FrameMs, rec.FrameT);
            StringBuilder sb = new StringBuilder();
            sb.Append("# Lag report — ").Append(game).Append('\n').Append('\n');
            sb.Append("Recorded by SysDeck overlay. Start: ").Append(started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append(", length: ").Append(N(seconds, "0")).Append(" s.\n\n");

            sb.Append("## Summary\n\n");
            if (s.Frames < 2)
                sb.Append("- No frames were captured (the frame trace needs the overlay to run with administrator rights or membership in «Performance Log Users», and a game in windowed or borderless mode).\n");
            else
            {
                sb.Append("- Frames: ").Append(s.Frames).Append(", average FPS: ").Append(N(s.AvgFps, "0.0"))
                  .Append(", 1% low: ").Append(N(s.Low1, "0.0")).Append(", 0.1% low: ").Append(N(s.Low01, "0.0")).Append('\n');
                sb.Append("- Median frame time: ").Append(N(s.MedianMs, "0.00")).Append(" ms, worst: ").Append(N(s.MaxMs, "0.0")).Append(" ms\n");
                sb.Append("- Hitches (frame > max(2.5 × median, median + 16 ms)): ").Append(s.Hitches.Count)
                  .Append(s.Seconds > 0 ? " (" + N(s.Hitches.Count * 60.0 / s.Seconds, "0.0") + " per minute)" : "").Append('\n');
            }
            sb.Append("- Average CPU load ").Append(N(Mean(rec.Rows, "cpu.load"), "0")).Append(" %, busiest core ").Append(N(Mean(rec.Rows, "cpu.coremax"), "0"))
              .Append(" %, GPU load ").Append(N(Mean(rec.Rows, "gpu.load"), "0")).Append(" %, hard faults ").Append(N(Mean(rec.Rows, "ram.hardfaults"), "0")).Append(" pages/s\n\n");

            sb.Append("## Findings\n\n");
            foreach (string hint in Hints(rec, s)) sb.Append("- ").Append(hint).Append('\n');
            sb.Append('\n');

            List<Hitch> worst = new List<Hitch>(s.Hitches);
            worst.Sort(delegate(Hitch a, Hitch b) { return b.Ms.CompareTo(a.Ms); });
            if (worst.Count > 0)
            {
                sb.Append("## Worst hitches and what the system was doing (±1 s)\n\n");
                sb.Append("| t, s | frame, ms | CPU % | top core % | GPU % | VRAM % | RAM % | hard faults/s | disk read MB/s | busiest other processes | events |\n");
                sb.Append("|---|---|---|---|---|---|---|---|---|---|---|\n");
                for (int i = 0; i < Math.Min(15, worst.Count); i++)
                {
                    Hitch h = worst[i];
                    HudLagRecorder.SysRow r = RowAt(rec.Rows, h.T);
                    sb.Append("| ").Append(N(h.T, "0.0")).Append(" | ").Append(N(h.Ms, "0")).Append(" | ").Append(N(Get(r, "cpu.load"), "0"))
                      .Append(" | ").Append(N(Get(r, "cpu.coremax"), "0")).Append(" | ").Append(N(Get(r, "gpu.load"), "0"))
                      .Append(" | ").Append(N(Get(r, "vram%"), "0")).Append(" | ").Append(N(Get(r, "ram%"), "0"))
                      .Append(" | ").Append(N(Get(r, "ram.hardfaults"), "0")).Append(" | ").Append(N(Get(r, "disk.read"), "0.0"))
                      .Append(" | ").Append(TopProcesses(rec, h.T, game)).Append(" | ").Append(EventsNear(rec, h.T)).Append(" |\n");
                }
                sb.Append('\n');
            }

            if (rec.Events.Count > 2)
            {
                sb.Append("## Events\n\n");
                int n = 0;
                foreach (HudLagRecorder.EventRow e in rec.Events)
                {
                    if (++n > 60) { sb.Append("- … more in events.csv\n"); break; }
                    sb.Append("- ").Append(N(e.T, "0.0")).Append(" s — ").Append(e.Kind).Append(": ").Append(e.Text).Append('\n');
                }
                sb.Append('\n');
            }

            sb.Append("## Files (for analysis by a person or an AI)\n\n");
            sb.Append("- `frames.csv` — one row per displayed frame: `t_s` seconds from start, `frametime_ms`, `pid` of the process that presented it.\n");
            sb.Append("- `system.csv` — one row per second: CPU/GPU load and temperatures, power, memory in MB, `hard_faults_pages_s` (paging from disk), disk and network MB/s, refresh rate, detected bottleneck, present mode, V-Sync, API. Empty cell = the source gave no value.\n");
            sb.Append("- `processes.csv` — every 2 s up to 10 busiest processes: CPU % of all cores, hard faults/s, read/write MB/s, working set MB.\n");
            sb.Append("- `events.csv` — foreground window changes, processes started during recording, CPU frequency caps, GPU throttle reasons.\n\n");
            sb.Append("Hitch detection and the findings above are heuristics; correlate `frames.csv` spikes with the same `t_s` in the other files.\n");
            return sb.ToString();
        }

        private static string TopProcesses(HudLagRecorder rec, double t, string game)
        {
            List<HudLagRecorder.ProcRow> near = new List<HudLagRecorder.ProcRow>();
            foreach (HudLagRecorder.ProcRow p in rec.Procs)
                if (Math.Abs(p.T - t) <= 1.5 && HudLagRecorder.SafeName(Path.GetFileNameWithoutExtension(p.Name)) != game) near.Add(p);
            near.Sort(delegate(HudLagRecorder.ProcRow a, HudLagRecorder.ProcRow b) { return b.Cpu.CompareTo(a.Cpu); });
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Math.Min(3, near.Count); i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(near[i].Name.Replace("|", "/")).Append(' ').Append(N(near[i].Cpu, "0")).Append('%');
                if (near[i].Read >= 1) sb.Append(" r").Append(N(near[i].Read, "0")).Append("MB/s");
            }
            return sb.Length == 0 ? "—" : sb.ToString();
        }

        private static string EventsNear(HudLagRecorder rec, double t)
        {
            StringBuilder sb = new StringBuilder();
            foreach (HudLagRecorder.EventRow e in rec.Events)
                if (Math.Abs(e.T - t) <= 2 && e.Kind != "start" && e.Kind != "stop")
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(e.Kind).Append(": ").Append(e.Text.Replace("|", "/"));
                }
            return sb.Length == 0 ? "—" : sb.ToString();
        }

        // Подсказки: сравнивается обстановка во время фризов со средней за запись.
        internal static List<string> Hints(HudLagRecorder rec, Summary s)
        {
            List<string> hints = new List<string>();
            List<HudLagRecorder.SysRow> during = new List<HudLagRecorder.SysRow>();
            foreach (Hitch h in s.Hitches)
            {
                HudLagRecorder.SysRow r = RowAt(rec.Rows, h.T);
                if (r != null && !during.Contains(r)) during.Add(r);
            }
            if (s.Frames >= 2 && s.Hitches.Count == 0) hints.Add("No hitches found: frame pacing was even for the whole recording.");
            double faultsAll = Mean(rec.Rows, "ram.hardfaults"), faultsHitch = Mean(during, "ram.hardfaults");
            if (HudFormat.Valid(faultsHitch) && faultsHitch > 300 && (!HudFormat.Valid(faultsAll) || faultsHitch > faultsAll * 2))
                hints.Add("Hitches coincide with heavy paging from disk (" + N(faultsHitch, "0") + " hard faults/s vs " + N(faultsAll, "0")
                          + " on average): not enough free RAM — close background programs or add memory.");
            double ramHitch = Mean(during, "ram%");
            if (HudFormat.Valid(ramHitch) && ramHitch > 90) hints.Add("RAM was " + N(ramHitch, "0") + " % full during hitches.");
            double vram = Mean(during.Count > 0 ? during : rec.Rows, "vram%");
            if (HudFormat.Valid(vram) && vram > 95) hints.Add("Video memory was " + N(vram, "0") + " % full: lower texture quality or resolution.");
            double core = Mean(during, "cpu.coremax"), gpu = Mean(during, "gpu.load");
            if (HudFormat.Valid(core) && core >= 95 && (!HudFormat.Valid(gpu) || gpu < 90))
                hints.Add("During hitches one CPU thread was at " + N(core, "0") + " % while the GPU waited (" + N(gpu, "0") + " %): the game is CPU/thread-bound here (shader compilation, loading, simulation).");
            double limit = Mean(during.Count > 0 ? during : rec.Rows, "cpu.perflimit");
            if (HudFormat.Valid(limit) && limit < 90) hints.Add("CPU performance was capped at " + N(limit, "0") + " % (thermal, power or power-plan limit).");
            double disk = Mean(during, "disk.read"), diskAll = Mean(rec.Rows, "disk.read");
            if (HudFormat.Valid(disk) && disk > 30 && (!HudFormat.Valid(diskAll) || disk > diskAll * 2))
                hints.Add("The disk was busy reading " + N(disk, "0") + " MB/s during hitches: asset streaming or a background process reading the disk.");
            int throttles = 0;
            foreach (HudLagRecorder.EventRow e in rec.Events) if (e.Kind == "gpu-throttle") throttles++;
            if (throttles > 0) hints.Add("The GPU changed its throttle reason " + throttles + " time(s) — see events.csv.");
            double cpuTemp = Mean(rec.Rows, "cpu.temp"), gpuTemp = Mean(rec.Rows, "gpu.temp");
            if (HudFormat.Valid(cpuTemp) && cpuTemp > 90) hints.Add("CPU averaged " + N(cpuTemp, "0") + " °C — close to throttling.");
            if (HudFormat.Valid(gpuTemp) && gpuTemp > 85) hints.Add("GPU averaged " + N(gpuTemp, "0") + " °C — close to throttling.");
            Dictionary<string, int> busy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Hitch h in s.Hitches)
                foreach (HudLagRecorder.ProcRow p in rec.Procs)
                    if (Math.Abs(p.T - h.T) <= 1.5 && p.Cpu >= 10)
                    {
                        int c;
                        busy[p.Name] = busy.TryGetValue(p.Name, out c) ? c + 1 : 1;
                    }
            foreach (KeyValuePair<string, int> kv in busy)
                if (s.Hitches.Count >= 3 && kv.Value * 3 >= s.Hitches.Count)
                    hints.Add("Process «" + kv.Key + "» used ≥10 % CPU around " + kv.Value + " of " + s.Hitches.Count + " hitches (may be the game itself).");
            int starts = 0;
            foreach (HudLagRecorder.EventRow e in rec.Events) if (e.Kind == "process-start") starts++;
            if (starts > 0) hints.Add(starts + " process(es) started during the recording — check events.csv for updaters or antivirus scans.");
            if (s.Frames >= 2 && s.Low1 > 0 && s.AvgFps > s.Low1 * 2)
                hints.Add("1% low (" + N(s.Low1, "0") + ") is less than half of the average (" + N(s.AvgFps, "0") + "): uneven pacing, noticeable as stutter even without long freezes.");
            if (hints.Count == 0) hints.Add("No clear cause stood out; compare frames.csv spikes with system.csv and processes.csv at the same t_s.");
            return hints;
        }
    }
}
