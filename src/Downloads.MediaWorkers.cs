// SysDeck — «Загрузки»: медиапоток — инициализация дорожек, рабочие потоки, скачивание и сборка сегментов.
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
        // ---------- init-сегменты: один раз в начало файла данных ----------
        private DlFailure FetchInits()
        {
            foreach (Track rt in _tracks)
            {
                if (Stopping()) return null;
                MdSegment first = null;
                lock (_gate)
                {
                    if (rt.InitDone || rt.Committed > 0) { rt.InitDone = true; continue; }
                    foreach (Seg s in rt.Segs) if (s.Src != null) { first = s.Src; break; }
                }
                if (first == null || first.InitUrl.Length == 0)
                {
                    lock (_gate) rt.InitDone = true;
                    continue;
                }
                byte[] init = null;
                DlFailure f = null;
                for (int quick = 0; ; quick++)
                {
                    string fu, ct;
                    init = _fx.GetBytes(first.InitUrl, first.InitOffset, first.InitLength, MdLoader.MaxManifest, Headers(rt), true, out fu, out ct, out f);
                    if (f == null || Stopping()) break;
                    if ((f.Kind != DlErrorKind.Network && f.Kind != DlErrorKind.Server) || quick >= QuickRetries || !SleepUnlessStopped(1000 * (quick + 1))) break;
                }
                if (Stopping()) return null;
                if (f != null) return f;
                string path = DataPath(rt);
                if (DlFiles.IsReparse(path)) return DlFailure.Make(DlErrorKind.Policy, Tr.S("на месте файла данных ссылка", "a link sits where the data file is"));
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1))
                    {
                        fs.Write(init, 0, init.Length);
                        fs.Flush(true);
                    }
                }
                catch (Exception ex) { return DlFailure.Make(DlErrorKind.Disk, ex.Message); }
                lock (_gate)
                {
                    rt.Committed = init.Length;
                    rt.InitDone = true;
                }
            }
            return null;
        }

        private Dictionary<string, string> Headers(Track rt)
        {
            return MdLoader.Merge(_item.Media.Headers, rt.Src == null ? null : rt.Src.Headers);
        }

        // ------------------------------------------------------------------ //
        //  Сегменты
        // ------------------------------------------------------------------ //
        private void RunWorkers()
        {
            int n = _item.Connections > 0 ? _item.Connections : _settings.Segments;
            n = Math.Max(1, Math.Min(16, n));
            _liveEnded = !_live || _item.Media.StopLive;
            List<Thread> threads = new List<Thread>();
            for (int i = 0; i < n; i++)
            {
                Thread t = new Thread(Worker);
                t.IsBackground = true;
                t.Name = "wpc-md-" + _item.Id + "-" + i;
                threads.Add(t);
                t.Start();
            }
            if (!_liveEnded) LiveLoop();
            _liveEnded = true;
            lock (_gate) Monitor.PulseAll(_gate);
            foreach (Thread t in threads) t.Join();
        }

        private void Worker()
        {
            try
            {
                while (!Stopping() && !HasFailure)
                {
                    Claim c = TakeClaim();
                    if (c == null)
                    {
                        lock (_gate)
                        {
                            if (_liveEnded && !AnyPendingLocked()) break;
                            Monitor.Wait(_gate, 200);
                        }
                        continue;
                    }
                    _item.ActiveConnections = Interlocked.Increment(ref _busy);
                    try
                    {
                        DlFailure f = Fetch(c);
                        if (f != null && !Stopping()) SetFailure(f);
                    }
                    finally
                    {
                        _item.ActiveConnections = Math.Max(0, Interlocked.Decrement(ref _busy));
                        lock (_gate)
                        {
                            c.Seg.Busy = false;
                            int b;
                            if (_hostBusy.TryGetValue(c.Host, out b))
                            {
                                if (b <= 1) _hostBusy.Remove(c.Host);
                                else _hostBusy[c.Host] = b - 1;
                            }
                            if (c.Direct && !c.Committed) c.Track.Writing = false;
                            Monitor.PulseAll(_gate);
                        }
                        Drain(c.Track);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!Stopping()) { DlLog.Report(ex); SetFailure(DlFailure.Make(DlErrorKind.Network, ex.Message)); }
            }
        }

        private bool AnyPendingLocked()
        {
            foreach (Track rt in _tracks)
                for (int i = rt.Head; i < rt.Segs.Count; i++)
                    if (rt.Segs[i].State == Pending) return true;
            return false;
        }

        private bool AllAppended()
        {
            lock (_gate)
            {
                foreach (Track rt in _tracks)
                    foreach (Seg s in rt.Segs)
                        if (s.State != Appended && s.State != Skipped) return false;
                return true;
            }
        }

        private static string HostOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) ? u.Host.ToLowerInvariant() : "";
        }

        private Claim TakeClaim()
        {
            lock (_gate)
            {
                int nt = _tracks.Count;
                int perHost = Math.Max(1, _settings.MaxPerServer);
                for (int k = 0; k < nt; k++)
                {
                    Track rt = _tracks[(_rr + k) % nt];
                    for (int i = rt.Head; i < rt.Segs.Count; i++)
                    {
                        Seg s = rt.Segs[i];
                        if (s.State != Pending || s.Busy) continue;
                        if (s.Src == null) { s.State = Skipped; continue; }
                        bool direct = i == rt.Head && !rt.Writing && !rt.Draining;
                        if (!direct && rt.Buffered >= BufferCap) break;
                        string host = HostOf(s.Src.Url);
                        int busy;
                        _hostBusy.TryGetValue(host, out busy);
                        if (busy >= perHost) break;
                        _hostBusy[host] = busy + 1;
                        s.Busy = true;
                        if (direct) rt.Writing = true;
                        _rr = (_rr + k + 1) % nt;
                        Claim c = new Claim();
                        c.Track = rt;
                        c.Seg = s;
                        c.Direct = direct;
                        c.Host = host;
                        return c;
                    }
                }
                return null;
            }
        }

        private DlFailure Fetch(Claim c)
        {
            int quick = 0;
            while (true)
            {
                if (Stopping() || HasFailure) return null;
                MdSegment src;
                lock (_gate) src = c.Seg.Src;
                int gen = Thread.VolatileRead(ref _generation);
                long bytes;
                DlFailure f = FetchOnce(c, src, out bytes);
                if (f == null)
                {
                    Commit(c, bytes);
                    return null;
                }
                if (Stopping()) return null;
                if (f.Kind == DlErrorKind.LinkExpired)
                {
                    if (c.Track.Live && (f.Status == 404 || f.Status == 410))
                    {
                        SkipGone(c);
                        return null;
                    }
                    if (_item.Media.Source == MdSource.Ytdlp)
                    {
                        DlFailure rf;
                        if (Reextract(gen, out rf)) continue;
                        return rf;
                    }
                    return f;
                }
                if (f.Kind == DlErrorKind.Network || f.Kind == DlErrorKind.Server)
                {
                    if (++quick > QuickRetries) return f;
                    if (!SleepUnlessStopped(1000 * quick)) return null;
                    continue;
                }
                return f;
            }
        }

        // IV ключа из плейлиста или номер сегмента big-endian (RFC 8216 §5.2).
        internal static byte[] IvFor(MdSegment s)
        {
            byte[] iv = new byte[16];
            if (s.Key != null && s.Key.Iv != null)
            {
                int n = Math.Min(16, s.Key.Iv.Length);
                Array.Copy(s.Key.Iv, s.Key.Iv.Length - n, iv, 16 - n, n);
                return iv;
            }
            long q = s.Sequence;
            for (int i = 15; i >= 8; i--)
            {
                iv[i] = (byte)(q & 0xFF);
                q >>= 8;
            }
            return iv;
        }

        private DlFailure FetchOnce(Claim c, MdSegment src, out long bytes)
        {
            bytes = 0;
            Track rt = c.Track;
            DlFailure f;
            byte[] key = null;
            if (src.Key != null && src.Key.Method == "AES-128")
            {
                key = GetKey(rt, src.Key.Uri, out f);
                if (key == null) return f ?? DlFailure.Make(DlErrorKind.Network, "stopped");
            }
            string path = c.Direct ? DataPath(rt) : BinPath(rt, c.Seg.Seq);
            if (DlFiles.IsReparse(path)) return DlFailure.Make(DlErrorKind.Policy, Tr.S("на месте файла частей ссылка", "a link sits where a part file is"));
            string finalUrl;
            HttpWebResponse resp = _fx.Open(src.Url, src.Offset, src.Length, Headers(rt), out finalUrl, out f);
            if (f != null) return f;
            try
            {
                long skip;
                f = _fx.Accept(resp, src.Offset, true, out skip);
                if (f != null) return f;
                long length = src.Length > 0 ? src.Length : -1;
                if (length < 0 && (int)resp.StatusCode == 200 && resp.ContentLength > 0) length = resp.ContentLength;
                FileStream fs;
                long start;
                try
                {
                    if (c.Direct)
                    {
                        long committed;
                        lock (_gate) committed = rt.Committed;
                        // bufferSize 1: каждый Write сразу в ОС — убитый процесс не теряет прочитанное.
                        fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
                        if (fs.Length != committed) fs.SetLength(committed);
                        fs.Seek(committed, SeekOrigin.Begin);
                        start = committed;
                    }
                    else
                    {
                        fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
                        start = 0;
                    }
                }
                catch (Exception ex)
                {
                    return DlFailure.Make(DlErrorKind.Disk, Tr.S("файл частей не открывается: ", "a part file cannot be opened: ") + ex.Message);
                }
                Aes aes = null;
                ICryptoTransform dec = null;
                CryptoStream cs = null;
                try
                {
                    Stream sink = fs;
                    if (key != null)
                    {
                        aes = Aes.Create();
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        aes.Key = key;
                        aes.IV = IvFor(src);
                        dec = aes.CreateDecryptor();
                        cs = new CryptoStream(fs, dec, CryptoStreamMode.Write);
                        sink = cs;
                    }
                    using (Stream net = resp.GetResponseStream())
                        f = _fx.Pump(net, skip, length, sink, 0);
                    if (f != null) return f;
                    if (cs != null)
                    {
                        try { cs.FlushFinalBlock(); }
                        catch (CryptographicException)
                        {
                            Note(Tr.S("ключ AES-128 не подходит к сегменту ", "the AES-128 key does not fit segment ") + c.Seg.Seq);
                            return DlFailure.Make(DlErrorKind.Client, Tr.S("сегмент не расшифровывается: ключ AES-128 не подходит", "the segment does not decrypt: the AES-128 key does not fit"));
                        }
                    }
                    fs.Flush(true);
                    bytes = fs.Position - start;
                    return null;
                }
                catch (IOException ex)
                {
                    return DlFailure.Make(DlErrorKind.Disk, (DlHttp.IsDiskFull(ex) ? Tr.S("диск заполнен: ", "the disk is full: ") : Tr.S("ошибка записи: ", "write error: ")) + ex.Message);
                }
                finally
                {
                    if (cs != null) { try { cs.Dispose(); } catch (Exception) { } }
                    try { fs.Dispose(); } catch (Exception) { }
                    if (dec != null) dec.Dispose();
                    if (aes != null) aes.Dispose();
                }
            }
            finally
            {
                _fx.Release(resp);
            }
        }

        private byte[] GetKey(Track rt, string uri, out DlFailure f)
        {
            f = null;
            lock (_keyGate)
            {
                byte[] k;
                if (_keys.TryGetValue(uri, out k)) return k;
            }
            for (int quick = 0; ; quick++)
            {
                string fu, ct;
                byte[] b = _fx.GetBytes(uri, -1, -1, MdLoader.MaxKey, Headers(rt), true, out fu, out ct, out f);
                if (f == null)
                {
                    if (b.Length != 16)
                    {
                        f = DlFailure.Make(DlErrorKind.Client, Tr.S("ключ AES-128 неверной длины: ", "the AES-128 key has a wrong length: ") + b.Length);
                        Note(f.Message);
                        return null;
                    }
                    lock (_keyGate) _keys[uri] = b;
                    return b;
                }
                if (Stopping()) return null;
                if ((f.Kind == DlErrorKind.Network || f.Kind == DlErrorKind.Server) && quick < QuickRetries && SleepUnlessStopped(1000 * (quick + 1))) continue;
                Note(Tr.S("ключ не получен: ", "the key was not received: ") + f.Message);
                return null;
            }
        }

        private void Commit(Claim c, long bytes)
        {
            Track rt = c.Track;
            lock (_gate)
            {
                c.Seg.Bytes = bytes;
                if (c.Direct)
                {
                    rt.Committed += bytes;
                    c.Seg.State = Appended;
                    rt.Seconds += c.Seg.Duration;
                    rt.Writing = false;
                    c.Committed = true;
                    AdvanceHeadLocked(rt);
                }
                else
                {
                    c.Seg.State = InBin;
                    rt.Buffered += bytes;
                }
            }
            Drain(rt);
            UpdateCounters();
            SaveJournal(false);
            Persist(false);
        }

        // Живое окно ушло вперёд, сегмента на сервере больше нет: пропуск, запись продолжается.
        private void SkipGone(Claim c)
        {
            lock (_gate)
            {
                c.Seg.State = Skipped;
                AdvanceHeadLocked(c.Track);
            }
            Note(Tr.S("пропуск 1 сегмента: сервер его уже не отдаёт (№", "gap of 1 segment: the server no longer serves it (#") + c.Seg.Seq + ")");
        }

        private static void AdvanceHeadLocked(Track rt)
        {
            while (rt.Head < rt.Segs.Count && (rt.Segs[rt.Head].State == Appended || rt.Segs[rt.Head].State == Skipped)) rt.Head++;
        }

        // Дописать в файл данных готовые .bin, стоящие в очереди следующими. Один дописывающий на дорожку.
        private void Drain(Track rt)
        {
            lock (_gate)
            {
                if (rt.Writing || rt.Draining) return;
                rt.Draining = true;
            }
            try
            {
                while (true)
                {
                    Seg s;
                    long committed;
                    lock (_gate)
                    {
                        AdvanceHeadLocked(rt);
                        if (rt.Writing || rt.Head >= rt.Segs.Count || rt.Segs[rt.Head].State != InBin || _killed != 0)
                        {
                            rt.Draining = false;
                            Monitor.PulseAll(_gate);
                            return;
                        }
                        s = rt.Segs[rt.Head];
                        committed = rt.Committed;
                    }
                    string bin = BinPath(rt, s.Seq);
                    long appended = AppendFile(bin, DataPath(rt), committed, s.Bytes);
                    lock (_gate)
                    {
                        if (appended < 0)
                        {
                            rt.Buffered -= s.Bytes;
                            s.Bytes = 0;
                            s.State = Pending;
                            rt.Draining = false;
                            Monitor.PulseAll(_gate);
                            return;
                        }
                        rt.Committed = committed + appended;
                        rt.Buffered -= s.Bytes;
                        rt.Seconds += s.Duration;
                        s.State = Appended;
                        AdvanceHeadLocked(rt);
                    }
                    DeleteOwn(bin);
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    rt.Draining = false;
                    Monitor.PulseAll(_gate);
                }
                if (!Stopping()) SetFailure(DlFailure.Make(DlErrorKind.Disk, Tr.S("ошибка записи: ", "write error: ") + ex.Message));
            }
        }

        // -1 — .bin пропал или не той длины (качать заново).
        private static long AppendFile(string bin, string data, long committed, long expected)
        {
            if (!File.Exists(bin) || DlFiles.IsReparse(bin) || DlFiles.IsReparse(data)) return -1;
            using (FileStream src = new FileStream(bin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (src.Length != expected) return -1;
                using (FileStream dst = new FileStream(data, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1))
                {
                    if (dst.Length != committed) dst.SetLength(committed);
                    dst.Seek(committed, SeekOrigin.Begin);
                    byte[] buf = new byte[1024 * 1024];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
                    dst.Flush(true);
                    return src.Length;
                }
            }
        }

        // ---------- ссылки yt-dlp устарели посреди загрузки ----------
        private bool Reextract(int gen, out DlFailure f)
        {
            f = null;
            lock (_extractGate)
            {
                if (Thread.VolatileRead(ref _generation) != gen) return true;
                if (_reextracted)
                {
                    f = DlFailure.Make(DlErrorKind.LinkExpired, Tr.S("ссылка снова устарела — нужна свежая со страницы, скачанное сохранено",
                                                                     "the link expired again — a fresh one from the page is needed, downloaded data is kept"));
                    return false;
                }
                _reextracted = true;
                Note(Tr.S("сервер отказал (ссылка устарела) — повторное извлечение", "the server refused (link expired) — extracting again"));
                DlFailure ef;
                MdManifest m = Extract(true, out ef);
                if (m == null) { f = ef; return false; }
                foreach (Track rt in _tracks)
                {
                    MdTrack nt = FindSame(m, rt.Src);
                    if (nt == null)
                    {
                        f = DlFailure.Make(DlErrorKind.LinkExpired, Tr.S("дорожка «", "track «") + rt.Id + Tr.S("» больше не отдаётся", "» is no longer offered"));
                        return false;
                    }
                    if (!MdLoader.ResolveTrack(_fx, nt, _item.Media.Headers, true, out ef))
                    {
                        f = ef;
                        return false;
                    }
                    Dictionary<long, MdSegment> fresh = new Dictionary<long, MdSegment>();
                    foreach (MdSegment s in nt.Segments) fresh[s.Sequence] = s;
                    lock (_gate)
                    {
                        foreach (Seg s in rt.Segs)
                        {
                            if (s.State == Appended || s.State == Skipped || s.State == InBin) continue;
                            MdSegment ns;
                            if (!fresh.TryGetValue(s.Seq, out ns) || s.Src == null || ns.Offset != s.Src.Offset || ns.Length != s.Src.Length)
                            {
                                f = DlFailure.Make(DlErrorKind.Changed, Tr.S("после обновления ссылок поток другой — нужно скачать заново", "after refreshing the links the stream differs — it has to be downloaded again"));
                                return false;
                            }
                            s.Src = ns;
                        }
                        rt.Src = nt;
                    }
                }
                _manifest = m;
                Interlocked.Increment(ref _generation);
                Note(Tr.S("ссылки обновлены", "links refreshed"));
                return true;
            }
        }
    }
}
