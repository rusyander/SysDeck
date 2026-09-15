// SysDeck — «Загрузки»: медиапоток — прямой эфир, завершение, счётчики и журнал.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SysDeck.Downloads
{
    internal sealed partial class DlMediaRun : IDlRun
    {
        // ------------------------------------------------------------------ //
        //  Трансляция
        // ------------------------------------------------------------------ //
        private void LiveLoop()
        {
            double target = 0;
            foreach (Track rt in _tracks) if (rt.Live && rt.Src.TargetDuration > target) target = rt.Src.TargetDuration;
            if (target <= 0) target = 5;
            target = Math.Max(1, Math.Min(30, target));
            Stopwatch idle = Stopwatch.StartNew();
            int failures = 0;
            while (!Stopping() && !HasFailure)
            {
                if (_item.Media.StopLive) { Note(Tr.S("запись трансляции остановлена — сборка того, что есть", "live recording stopped — assembling what is there")); break; }
                if (!SleepLive((int)(target * 1000))) break;
                if (_item.Media.StopLive) continue;
                bool anyNew = false, stillLive = false, anyLiveTrack = false;
                foreach (Track rt in _tracks)
                {
                    if (!rt.Live) continue;
                    anyLiveTrack = true;
                    MdTrack fresh;
                    DlFailure f = Reload(rt, out fresh);
                    if (Stopping()) break;
                    if (f != null)
                    {
                        stillLive = true;
                        failures++;
                        if (failures <= 3 || failures % 20 == 0) Note(Tr.S("плейлист трансляции не обновился: ", "the live playlist did not refresh: ") + f.Message);
                        continue;
                    }
                    failures = 0;
                    int added, gap;
                    MergeLive(rt, fresh, out added, out gap);
                    if (gap > 0) Note(Tr.S("пропуск ", "gap of ") + gap + Tr.S(" сегментов: окно трансляции ушло вперёд", " segments: the live window moved on"));
                    if (added > 0) anyNew = true;
                    if (fresh.Live) stillLive = true;
                    else lock (_gate) rt.Live = false;
                }
                if (anyNew)
                {
                    idle.Restart();
                    UpdateCounters();
                    lock (_gate) Monitor.PulseAll(_gate);
                    SaveJournal(false);
                }
                if (!anyLiveTrack || !stillLive) { Note(Tr.S("трансляция закончилась", "the live stream ended")); break; }
                if (idle.Elapsed.TotalSeconds > 3 * target + LiveIdleExtraSeconds) { Note(Tr.S("новых сегментов нет — запись завершена", "no new segments — recording finished")); break; }
            }
        }

        private bool SleepLive(int ms)
        {
            Stopwatch w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ms)
            {
                if (Stopping()) return false;
                if (_item.Media.StopLive) return true;
                Thread.Sleep(50);
            }
            return !Stopping();
        }

        private DlFailure Reload(Track rt, out MdTrack fresh)
        {
            fresh = null;
            DlMedia md = _item.Media;
            DlFailure f;
            if (_manifest != null && _manifest.Source == MdSource.Dash)
            {
                string url = md.ManifestUrl.Length > 0 ? md.ManifestUrl : _item.Url;
                MdManifest m = MdLoader.Load(_fx, url, md.Headers, out f);
                if (f != null) return f;
                fresh = FindSame(m, rt.Src);
                if (fresh == null) return DlFailure.Make(DlErrorKind.Client, Tr.S("дорожка пропала из манифеста", "the track disappeared from the manifest"));
                fresh.Live = m.Live;
                return null;
            }
            string fu, ct;
            byte[] body = _fx.GetBytes(rt.Src.Url, -1, -1, MdLoader.MaxManifest, Headers(rt), false, out fu, out ct, out f);
            if (f != null) return f;
            MdTrack t = new MdTrack();
            t.Id = rt.Id;
            t.Kind = rt.Kind;
            t.Layout = rt.Layout;
            t.Url = rt.Src.Url;
            string err;
            bool refused;
            if (!MdHls.ParseMedia(MdLoader.Decode(body), fu, t, out err, out refused))
                return DlFailure.Make(refused ? DlErrorKind.Policy : DlErrorKind.Client, err);
            fresh = t;
            return null;
        }

        // Новые сегменты окна — по номеру, без повторов; разрыв номеров — пропуск (только у сплошной нумерации).
        private void MergeLive(Track rt, MdTrack fresh, out int added, out int gap)
        {
            added = 0;
            gap = 0;
            bool ytdlp = _item.Media.Source == MdSource.Ytdlp;
            List<MdSegment> list = new List<MdSegment>(fresh.Segments);
            list.Sort(delegate(MdSegment a, MdSegment b) { return a.Sequence.CompareTo(b.Sequence); });
            bool contiguous = true;
            for (int i = 1; i < list.Count; i++) if (list[i].Sequence != list[i - 1].Sequence + 1) contiguous = false;
            lock (_gate)
            {
                long maxSeq = rt.Segs.Count > 0 ? rt.Segs[rt.Segs.Count - 1].Seq : long.MinValue;
                bool first = true;
                foreach (MdSegment s in list)
                {
                    if (rt.BySeq.ContainsKey(s.Sequence)) continue;
                    if (s.Sequence < maxSeq) continue;
                    if (first && contiguous && maxSeq != long.MinValue && s.Sequence > maxSeq + 1)
                        gap = (int)Math.Min(int.MaxValue, s.Sequence - maxSeq - 1);
                    first = false;
                    Seg x = NewSeg(s, ytdlp);
                    rt.Segs.Add(x);
                    rt.BySeq[s.Sequence] = x;
                    maxSeq = s.Sequence;
                    added++;
                }
            }
        }

        // ------------------------------------------------------------------ //
        //  Склейка и завершение
        // ------------------------------------------------------------------ //
        private DlFailure Finish()
        {
            DlMedia md = _item.Media;
            md.Phase = "mux";
            Persist(true);
            Func<MdMuxJob, MdMuxResult> mux = MdHooks.Mux;
            if (mux == null)
                return DlFailure.Make(DlErrorKind.Policy, Tr.S("склейка недоступна в этой сборке — части сохранены", "assembly is unavailable in this build — the parts are kept"));
            MdMuxJob job = new MdMuxJob();
            bool webm = false;
            lock (_gate)
                foreach (Track rt in _tracks)
                {
                    if (rt.Committed <= 0) continue;
                    MdInput input = new MdInput();
                    input.Path = DataPath(rt);
                    input.Kind = rt.Kind;
                    input.Layout = rt.Kind == MdTrackKind.Subtitles && rt.Layout != MdLayout.Ttml ? MdLayout.Vtt : rt.Layout;
                    input.Codec = rt.Src == null ? "" : rt.Src.Codec;
                    input.Language = rt.Src == null ? "" : rt.Src.Language;
                    input.Name = rt.Src == null ? "" : rt.Src.Name;
                    job.Inputs.Add(input);
                    if (rt.Layout == MdLayout.WebM) webm = true;
                }
            if (job.Inputs.Count == 0) return DlFailure.Make(DlErrorKind.Client, Tr.S("нечего собирать: ни одного сегмента", "nothing to assemble: no segments"));
            string outPath = Path.Combine(_partsDir, "out" + ExtFor(md.Output, webm));
            DeleteOwn(outPath);
            DeleteOwn(outPath + ".tmp");
            job.OutPath = outPath;
            job.Output = md.Output;
            job.AllowTruncated = _live;
            job.Cancel = Stopping;
            Note(Tr.S("склейка: дорожек ", "assembly: tracks ") + job.Inputs.Count);
            MdMuxResult result;
            try { result = mux(job); }
            catch (Exception ex)
            {
                DlLog.Report(ex);
                result = new MdMuxResult();
                result.Error = ex.Message;
            }
            if (Stopping()) return null;
            if (result == null || !result.Ok)
            {
                string why = result == null || string.IsNullOrEmpty(result.Error) ? Tr.S("неизвестная ошибка", "unknown error") : result.Error;
                Note(Tr.S("склейка не удалась: ", "assembly failed: ") + why);
                return DlFailure.Make(DlErrorKind.Policy, Tr.S("склейка не удалась: ", "assembly failed: ") + why);
            }
            string actual = string.IsNullOrEmpty(result.OutPath) ? outPath : result.OutPath;
            try
            {
                if (!File.Exists(actual) || !DlFiles.IsSameOrUnder(Path.GetFullPath(actual), Path.GetFullPath(_partsDir)))
                    return DlFailure.Make(DlErrorKind.Policy, Tr.S("склейка не оставила файла в папке частей", "the assembly left no file in the parts folder"));
            }
            catch (Exception ex) { return DlFailure.Make(DlErrorKind.Policy, ex.Message); }
            Note(Tr.S("склейка готова: ", "assembly done: ") + result.Output + (result.DurationMs > 0 ? ", " + (result.DurationMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + Tr.S(" с", " s") : ""));

            // Расширение цели — по фактическому контейнеру.
            string ext = Path.GetExtension(actual);
            string stem = Path.GetFileNameWithoutExtension(_item.FileName);
            string wanted = DlFiles.SanitizeName(stem + ext);
            if (!string.Equals(wanted, _item.FileName, StringComparison.OrdinalIgnoreCase))
            {
                string unique = _host.ReserveName(_item, _item.Folder, wanted);
                if (unique == null) return DlFailure.Make(DlErrorKind.Disk, Tr.S("не найдено свободное имя файла", "no free file name found"));
                _item.FileName = unique;
            }
            string target = DlFiles.PathInside(_item.Folder, _item.FileName);
            if (target == null) return DlFailure.Make(DlErrorKind.Policy, Tr.S("имя файла уводит из папки загрузки", "the file name leads out of the download folder"));
            string part = target + DlPaths.PartSuffix;
            if (DlFiles.IsReparse(part)) return DlFailure.Make(DlErrorKind.Policy, Tr.S("на месте частичного файла ссылка", "a link sits where the partial file is"));
            try
            {
                if (File.Exists(part)) File.Delete(part);
                File.Move(actual, part);
                _item.Total = new FileInfo(part).Length;
            }
            catch (Exception ex) { return DlFailure.Make(DlErrorKind.Disk, Tr.S("готовый файл не переносится: ", "the result cannot be moved: ") + ex.Message); }
            md.Phase = "verify";
            Persist(true);
            DlFailure f = DlFinish.Complete(_item, _settings, _env, _host, Stopping);
            if (f != null || _item.CompletedUtc == DateTime.MinValue) return f;

            // Готово: субтитры рядом с целью, свои части — по точным именам.
            string baseName = Path.GetFileNameWithoutExtension(outPath);
            string targetStem = Path.GetFileNameWithoutExtension(_item.FileName);
            foreach (string sf in result.SubtitleFiles)
            {
                try
                {
                    if (!File.Exists(sf) || DlFiles.IsReparse(sf) || !DlFiles.IsSameOrUnder(Path.GetFullPath(sf), Path.GetFullPath(_partsDir))) continue;
                    string fileName = Path.GetFileName(sf);
                    string suffix = fileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase) ? fileName.Substring(baseName.Length) : "." + fileName;
                    string name = DlFiles.UniqueName(_item.Folder, DlFiles.SanitizeName(targetStem + suffix), null);
                    if (name == null) continue;
                    File.Move(sf, Path.Combine(_item.Folder, name));
                    Note(Tr.S("субтитры: ", "subtitles: ") + name);
                }
                catch (Exception ex) { Note(Tr.S("субтитры не перенесены: ", "subtitles were not moved: ") + ex.Message); }
            }
            CleanupParts(outPath);
            md.Phase = "";
            md.PreviewPath = "";
            Persist(true);
            return null;
        }

        private void CleanupParts(string outPath)
        {
            lock (_journalGate)
            {
                lock (_gate)
                {
                    foreach (Track rt in _tracks)
                    {
                        DeleteOwn(DataPath(rt));
                        foreach (Seg s in rt.Segs) DeleteOwn(BinPath(rt, s.Seq));
                    }
                }
                DeleteOwn(outPath);
                DeleteOwn(outPath + ".tmp");
                string journal = Path.Combine(_partsDir, JournalName);
                DeleteOwn(journal);
                DeleteOwn(journal + ".bak");
                DeleteOwn(journal + ".tmp");
                try
                {
                    if (Directory.Exists(_partsDir) && !DlFiles.IsReparse(_partsDir) && Directory.GetFileSystemEntries(_partsDir).Length == 0)
                        Directory.Delete(_partsDir, false);
                }
                catch (Exception ex) { DlLog.Report(ex); }
                _partsDir = "";
            }
        }

        // ------------------------------------------------------------------ //
        //  Счётчики, журнал, сохранение записи
        // ------------------------------------------------------------------ //
        private void UpdateCounters()
        {
            DlMedia md = _item.Media;
            lock (_gate)
            {
                int total = 0, done = 0;
                double secs = 0;
                for (int t = 0; t < _tracks.Count; t++)
                {
                    Track rt = _tracks[t];
                    foreach (Seg s in rt.Segs)
                    {
                        if (s.State == Skipped) continue;
                        total++;
                        if (s.State == InBin || s.State == Appended)
                        {
                            done++;
                            if (t == 0) secs += s.Duration;
                        }
                    }
                }
                md.SegmentsTotal = _live ? -1 : total;
                md.SegmentsDone = done;
                md.SecondsDone = secs;
            }
        }

        private void Persist(bool force)
        {
            if (_killed != 0 || _host == null) return;
            long now = _clock.ElapsedMilliseconds;
            if (!force && now - Interlocked.Read(ref _persistAt) < 1000) return;
            Interlocked.Exchange(ref _persistAt, now);
            _host.Persist(_item);
        }

        private void SaveJournal(bool force)
        {
            if (_killed != 0) return;
            long now = _clock.ElapsedMilliseconds;
            if (!force && now - Interlocked.Read(ref _journalAt) < 1000) return;
            lock (_journalGate)
            {
                if (_killed != 0 || _partsDir.Length == 0) return;
                Interlocked.Exchange(ref _journalAt, _clock.ElapsedMilliseconds);
                string text;
                lock (_gate) text = Jsn.Write(JournalJson());
                try { DlPaths.WriteAtomic(Path.Combine(_partsDir, JournalName), text); }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }

        private JVal JournalJson()
        {
            JVal o = JVal.NewObj();
            o.Set("v", DlJson.N(1));
            JVal v = JVal.NewObj();
            v.Set("id", DlJson.S(_variantId));
            v.Set("h", DlJson.N(_variantHeight));
            v.Set("bw", DlJson.N(_variantBandwidth));
            o.Set("variant", v);
            o.Set("live", DlJson.B(_live));
            JVal arr = JVal.NewArr();
            foreach (Track rt in _tracks)
            {
                JVal t = JVal.NewObj();
                t.Set("id", DlJson.S(rt.Id));
                t.Set("file", DlJson.S(rt.File));
                t.Set("kind", DlJson.S(rt.Kind.ToString()));
                t.Set("layout", DlJson.S(rt.Layout.ToString()));
                t.Set("init", DlJson.B(rt.InitDone));
                t.Set("committed", DlJson.N(rt.Committed));
                JVal segs = JVal.NewArr();
                foreach (Seg s in rt.Segs)
                {
                    JVal e = JVal.NewArr();
                    e.V.Add(DlJson.N(s.Seq));
                    e.V.Add(DlJson.S(s.Id));
                    // Сегмент в работе в журнале — ещё не скачан.
                    e.V.Add(DlJson.N(s.State));
                    e.V.Add(DlJson.N(s.State == InBin || s.State == Appended ? s.Bytes : 0));
                    e.V.Add(DlJson.N((long)Math.Round(s.Duration * 1000)));
                    segs.V.Add(e);
                }
                t.Set("segs", segs);
                arr.V.Add(t);
            }
            o.Set("tracks", arr);
            return o;
        }

        private bool SleepUnlessStopped(int ms)
        {
            Stopwatch w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ms)
            {
                if (Stopping()) return false;
                Thread.Sleep(50);
            }
            return !Stopping();
        }
    }
}
