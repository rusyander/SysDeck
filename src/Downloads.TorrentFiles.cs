// SysDeck — «Загрузки»: торрент — метаданные и resume на диске, удаление, перепроверка, JSON.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;

namespace SysDeck.Downloads
{
    internal sealed partial class DlEngine
    {
        private void BtJournal(string id, string text)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it != null) it.Log(_env.UtcNow, text);
            }
        }

        // ---------- файлы рядом с записью ----------
        private BtMeta BtLoadMeta(string hash) { return LoadTorrentMeta(TorrentsDir, hash); }

        // Файл торрента рядом с записями; окно читает его сам (magnet-ссылка из метаданных, диалог файлов).
        internal static BtMeta LoadTorrentMeta(string torrentsDir, string hash)
        {
            if (!DlItem.IsHexHash(hash)) return null;
            return LoadMetaFile(Path.Combine(torrentsDir, hash + ".torrent"), hash);
        }

        private static BtMeta LoadMetaFile(string file, string hash)
        {
            if (!DlItem.IsHexHash(hash)) return null;
            FileInfo fi = new FileInfo(file);
            if (!fi.Exists || fi.Length > BtMaxTorrentBytes) return null;
            string err;
            BtMeta m = BtMeta.Parse(File.ReadAllBytes(file), out err);
            // Испорченный или чужой файл под этим именем — как если бы его не было: magnet получит метаданные заново.
            return m != null && m.HexHash == hash ? m : null;
        }

        private void BtEnsureSideFile(BtTorrent t)
        {
            BtMeta m = t.Meta;
            if (m == null) return;
            string file = Path.Combine(TorrentsDir, m.HexHash + ".torrent");
            if (!File.Exists(file)) BtWriteBytes(file, m.BuildTorrentFile());
        }

        private void BtSaveResume(BtTorrent t)
        {
            try
            {
                BtEnsureSideFile(t);
                BtResume r = t.CaptureResume();
                if (r != null) r.Save(BtResume.FileFor(TorrentsDir, Bencode.Hex(t.InfoHash)));
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        private static void BtWriteBytes(string file, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string tmp = file + ".tmp";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(file)) File.Replace(tmp, file, null, true);
            else File.Move(tmp, file);
        }

        private void BtDeleteResume(string hash)
        {
            string resume = BtResume.FileFor(TorrentsDir, hash);
            foreach (string f in new[] { resume, resume + ".bak", resume + ".tmp" })
                if (File.Exists(f)) File.Delete(f);
        }

        private void BtDeleteSideFiles(string hash)
        {
            if (!DlItem.IsHexHash(hash)) return;
            try
            {
                string torrent = Path.Combine(TorrentsDir, hash + ".torrent");
                foreach (string f in new[] { torrent, torrent + ".tmp" })
                    if (File.Exists(f)) File.Delete(f);
                BtDeleteResume(hash);
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        // ---------- команды ----------
        private bool BtRemove(string id, bool recycleFiles, out string why)
        {
            why = null;
            lock (_lock)
                if (_btRun.ContainsKey(id) || _btStarting.Contains(id)) _btStop[id] = DlState.Paused;
            _wake.Set();
            string root = null, folder = null, hash;
            int deadline = Environment.TickCount + 20000;
            while (true)
            {
                lock (_lock)
                {
                    DlItem it = FindLocked(id);
                    if (it == null) { why = Tr.S("нет такой загрузки", "no such download"); return false; }
                    if (_btUpdBusy.Contains(id)) { why = Tr.S("раздача сейчас обновляется", "the torrent is being updated right now"); return false; }
                    if (!_btRun.ContainsKey(id) && !_btStarting.Contains(id))
                    {
                        // Планировщик больше не выберет её к запуску, пока файлы уходят в Корзину.
                        if (it.State != DlState.Completed && it.State != DlState.Failed) it.State = DlState.Paused;
                        _btStop.Remove(id);
                        hash = it.InfoHash;
                        if (recycleFiles && !string.IsNullOrEmpty(it.FileName))
                        {
                            folder = it.Folder ?? "";
                            root = it.FileName;
                        }
                        break;
                    }
                }
                if (Environment.TickCount - deadline > 0) { why = Tr.S("торрент не остановился", "the torrent did not stop"); return false; }
                Thread.Sleep(50);
            }
            if (root != null)
            {
                BtMeta meta = BtLoadMeta(hash);
                if (meta != null)
                {
                    // По списку файлов торрента: папка уходит целиком, только если чужого в ней нет (данные «уже на диске»).
                    using (BtStorage storage = new BtStorage(meta, folder, root))
                    {
                        why = storage.Validate();
                        if (why != null) return false;
                        List<string> errors = storage.RecycleData();
                        if (errors.Count > 0) { why = errors[0]; return false; }
                    }
                }
                else
                {
                    string path = DlFiles.PathInside(folder, root);
                    foreach (string p in new[] { path, path + DlPaths.PartSuffix })
                    {
                        why = DlFiles.Recycle(p);
                        if (why != null) return false;
                    }
                }
            }
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it != null)
                {
                    _items.Remove(it);
                    ReleaseReservation(id);
                    _store.Delete(id);
                    SaveOrder();
                }
            }
            BtDeleteSideFiles(hash);
            BtUpdateForget(id);
            return true;
        }

        // Проверить данные хешем: в сессии — сразу, иначе при следующем старте проверяются все куски (счётчики сохраняются).
        public bool Recheck(string id, out string why)
        {
            why = null;
            BtTorrent t;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || !it.IsTorrent) { why = Tr.S("это не торрент", "this is not a torrent"); return false; }
                if (_btUpdBlocked.Contains(id)) { why = Tr.S("раздача сейчас обновляется", "the torrent is being updated right now"); return false; }
                if (_btStarting.Contains(id) || _btStop.ContainsKey(id))
                {
                    why = Tr.S("торрент запускается или останавливается — повторите через секунду", "the torrent is starting or stopping — try again in a second");
                    return false;
                }
                Journal(it, Tr.S("проверка данных по запросу", "data check requested"));
                if (!_btRun.TryGetValue(id, out t))
                {
                    try
                    {
                        string file = BtResume.FileFor(TorrentsDir, it.InfoHash);
                        BtResume r = BtResume.Load(file);
                        if (r != null)
                        {
                            r.PieceCount = 0;
                            r.Have = new byte[0];
                            r.Save(file);
                        }
                    }
                    catch (Exception ex) { why = ex.Message; return false; }
                    if (it.State != DlState.Scheduled)
                    {
                        it.State = DlState.Queued;
                        it.WaitReason = "";
                    }
                    it.ErrorKind = DlErrorKind.None;
                    it.Error = "";
                    it.NextRetryUtc = DateTime.MinValue;
                    _store.Save(it);
                }
            }
            if (t != null) t.Recheck();
            _wake.Set();
            return true;
        }

        public bool Reannounce(string id)
        {
            BtTorrent t;
            lock (_lock) if (!_btRun.TryGetValue(id, out t)) return false;
            t.Reannounce();
            return true;
        }

        public bool SetSequential(string id, bool on)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || !it.IsTorrent) return false;
                it.Sequential = on;
                _store.Save(it);
            }
            _wake.Set();
            return true;
        }

        public bool SetFilePriorities(string id, int[] priorities, out string why)
        {
            why = null;
            string hash;
            BtTorrent t;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || !it.IsTorrent) { why = Tr.S("это не торрент", "this is not a torrent"); return false; }
                if (_btUpdBlocked.Contains(id)) { why = Tr.S("раздача сейчас обновляется", "the torrent is being updated right now"); return false; }
                hash = it.InfoHash;
                _btRun.TryGetValue(id, out t);
            }
            BtMeta meta = t != null && t.Meta != null ? t.Meta : BtLoadMeta(hash);
            if (meta == null) { why = Tr.S("метаданные ещё не получены", "the metadata has not arrived yet"); return false; }
            int[] prio = ClampPriorities(priorities);
            if (prio == null || prio.Length != meta.Files.Count) { why = Tr.S("выбор файлов не совпадает с торрентом", "the file selection does not match the torrent"); return false; }
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) return false;
                it.FilePriorities = prio;
                // Готовая раздача с новыми файлами — снова в очередь: старт проверит, чего не хватает.
                if (it.State == DlState.Completed) it.State = DlState.Queued;
                _store.Save(it);
                _btRun.TryGetValue(id, out t);
            }
            if (t != null) t.SetPriorities(prio);
            _wake.Set();
            return true;
        }

        // ---------- карточка торрента ----------
        public JVal TorrentJson(string id)
        {
            DlItem it;
            BtTorrent t;
            BtSession ses;
            IBtPortMapper mapper;
            int[] prio;
            string hash;
            lock (_lock)
            {
                it = FindLocked(id);
                if (it == null || !it.IsTorrent) return null;
                _btRun.TryGetValue(id, out t);
                ses = _bt;
                mapper = _btMapper;
                hash = it.InfoHash;
                prio = it.FilePriorities;
            }
            BtMeta meta = t != null && t.Meta != null ? t.Meta : BtLoadMeta(hash);
            BtBitfield have = t != null ? t.Have : null;
            if (have == null && meta != null)
            {
                BtResume r = BtResume.Load(BtResume.FileFor(TorrentsDir, hash));
                if (r != null && r.PieceCount == meta.PieceCount) have = BtBitfield.FromBytes(r.Have, meta.PieceCount);
            }
            JVal o = JVal.NewObj();
            o.Set("hash", DlJson.S(hash));
            o.Set("running", DlJson.B(t != null));
            if (meta != null)
            {
                o.Set("name", DlJson.S(meta.Name));
                o.Set("comment", DlJson.S(meta.Comment));
                o.Set("createdBy", DlJson.S(meta.CreatedBy));
                o.Set("created", DlJson.D(meta.CreatedUtc));
                o.Set("private", DlJson.B(meta.Private));
                o.Set("version", DlJson.N(meta.Version));
                o.Set("pieceLength", DlJson.N(meta.PieceLength));
                o.Set("pieceCount", DlJson.N(meta.PieceCount));
                o.Set("totalSize", DlJson.N(meta.TotalSize));
                o.Set("fileCount", DlJson.N(meta.Files.Count));      // с заполнителями: длина массива для setFilePriorities
                JVal files = JVal.NewArr();
                for (int i = 0; i < meta.Files.Count; i++)
                {
                    BtFile f = meta.Files[i];
                    if (f.Pad) continue;
                    JVal j = JVal.NewObj();
                    j.Set("index", DlJson.N(i));
                    j.Set("path", DlJson.S(f.RelPath));
                    j.Set("size", DlJson.N(f.Length));
                    j.Set("priority", DlJson.N(prio != null && i < prio.Length ? prio[i] : 1));
                    j.Set("done", DlJson.N(FileDoneBytes(meta, f, have)));
                    files.V.Add(j);
                }
                o.Set("files", files);
                if (have != null) o.Set("pieces", DlJson.S(Convert.ToBase64String(have.ToBytes())));
            }
            if (t != null)
            {
                BtTorrentStats st = t.Stats();
                JVal stats = JVal.NewObj();
                stats.Set("downloaded", DlJson.N(st.Downloaded));
                stats.Set("uploaded", DlJson.N(st.Uploaded));
                stats.Set("wasted", DlJson.N(st.Wasted));
                stats.Set("ratio", DlJson.S((st.Downloaded > 0 ? (double)st.Uploaded / st.Downloaded : 0).ToString("0.###", CultureInfo.InvariantCulture)));
                stats.Set("availability", DlJson.S(st.Availability.ToString("0.###", CultureInfo.InvariantCulture)));
                stats.Set("peers", DlJson.N(st.Peers));
                stats.Set("seeds", DlJson.N(st.Seeds));
                stats.Set("seedSeconds", DlJson.N(st.SeedSeconds));
                stats.Set("activeSeconds", DlJson.N(st.ActiveSeconds));
                stats.Set("eta", DlJson.N(st.EtaSeconds));
                o.Set("stats", stats);
                JVal peers = JVal.NewArr();
                foreach (BtPeerInfo p in t.ConnectedPeers())
                {
                    JVal j = JVal.NewObj();
                    j.Set("address", DlJson.S(p.Endpoint == null ? "" : EndpointText(p.Endpoint)));
                    j.Set("client", DlJson.S(p.Client));
                    j.Set("flags", DlJson.S(p.Flags));
                    j.Set("origin", DlJson.S(p.Origin.ToString()));
                    j.Set("encrypted", DlJson.B(p.Encrypted));
                    j.Set("outgoing", DlJson.B(p.Outgoing));
                    j.Set("seed", DlJson.B(p.Seed));
                    j.Set("progress", DlJson.S(p.Progress.ToString("0.###", CultureInfo.InvariantCulture)));
                    j.Set("down", DlJson.N(p.DownBps));
                    j.Set("up", DlJson.N(p.UpBps));
                    j.Set("downloaded", DlJson.N(p.Downloaded));
                    j.Set("uploaded", DlJson.N(p.Uploaded));
                    peers.V.Add(j);
                }
                o.Set("peers", peers);
                JVal trackers = JVal.NewArr();
                foreach (BtTrackerInfo tr in t.Trackers())
                {
                    JVal j = JVal.NewObj();
                    j.Set("url", DlJson.S(tr.Url));
                    j.Set("tier", DlJson.N(tr.Tier));
                    j.Set("status", DlJson.S(tr.Status));
                    j.Set("message", DlJson.S(tr.Message));
                    j.Set("seeders", DlJson.N(tr.Seeders));
                    j.Set("leechers", DlJson.N(tr.Leechers));
                    j.Set("peersReceived", DlJson.N(tr.PeersReceived));
                    j.Set("next", DlJson.D(tr.NextAnnounceUtc));
                    trackers.V.Add(j);
                }
                o.Set("trackers", trackers);
            }
            if (ses != null)
            {
                BtContext ctx = ses.Context;
                JVal sj = JVal.NewObj();
                sj.Set("port", DlJson.N(ctx.Port));
                sj.Set("inbound", DlJson.B(ctx.InboundOpen));
                sj.Set("dhtNodes", DlJson.N(ctx.Dht != null ? ctx.Dht.NodeCount : -1));
                sj.Set("mapper", DlJson.S(mapper != null ? mapper.Status : ""));
                o.Set("session", sj);
            }
            return o;
        }

        private static string EndpointText(BtEndpoint ep)
        {
            string a = ep.Address.ToString();
            return (a.IndexOf(':') >= 0 ? "[" + a + "]" : a) + ":" + ep.Port.ToString(CultureInfo.InvariantCulture);
        }

        // Байты файла в проверенных кусках.
        private static long FileDoneBytes(BtMeta meta, BtFile f, BtBitfield have)
        {
            if (have == null || f.Length == 0 || f.LastPiece < 0) return 0;
            long done = 0;
            for (int p = f.FirstPiece; p <= f.LastPiece; p++)
            {
                if (!have[p]) continue;
                long start = meta.PieceStart(p), end = start + meta.PieceSize(p);
                long n = Math.Min(end, f.End) - Math.Max(start, f.Offset);
                if (n > 0) done += n;
            }
            return done;
        }
    }
}
