// SysDeck — «Загрузки»: карточка торрента на странице — разбор ответа команд torrent и updateInfo, строки вкладок.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Без окна и канала, как DlView: разбор и подписи проверяются тестами. Пока процесс загрузок не запущен, карточка
// строится из файла торрента рядом с записями (файлы и размеры без прогресса).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SysDeck.Downloads
{
    internal sealed class DlTorrentFile
    {
        public int Index;                         // номер в метаданных (с заполнителями) — для setFilePriorities
        public string Path = "";
        public long Size;
        public long Done = -1;                    // -1 — неизвестно (процесс не запущен)
        public int Priority = 1;                  // 0 — не качать, 1 — обычный, 2 — высокий
    }

    internal sealed class DlTorrentPeer
    {
        public string Address = "", Client = "", Flags = "", Origin = "";
        public bool Encrypted, Outgoing, Seed;
        public double Progress;
        public long Down, Up, Downloaded, Uploaded;
    }

    internal sealed class DlTorrentTracker
    {
        public string Url = "", Status = "", Message = "";
        public int Tier, Seeders = -1, Leechers = -1, PeersReceived;
        public DateTime NextUtc = DateTime.MinValue;
    }

    internal sealed class DlTorrentCard
    {
        public string Hash = "", Name = "", Comment = "", CreatedBy = "";
        public DateTime CreatedUtc = DateTime.MinValue;
        public bool Live, Running, HasMeta, Private;
        public int Version, PieceCount, FileCount;
        public long PieceLength, TotalSize;
        public byte[] Pieces;                     // битовое поле проверенных кусков; null — неизвестно
        public readonly List<DlTorrentFile> Files = new List<DlTorrentFile>();
        public readonly List<DlTorrentPeer> Peers = new List<DlTorrentPeer>();
        public readonly List<DlTorrentTracker> Trackers = new List<DlTorrentTracker>();
        public bool HasStats;
        public long Downloaded, Uploaded, Wasted, SeedSeconds, ActiveSeconds, EtaSeconds = -1;
        public double Ratio, Availability;
        public int PeerCount, SeedCount;
        public bool HasSession, Inbound;
        public int Port, DhtNodes = -1;
        public string Mapper = "";

        // Ответ процесса (поле torrent команды torrent). null — ответа нет.
        public static DlTorrentCard FromJson(JVal t)
        {
            if (t == null || t.Kind != JKind.Obj) return null;
            DlTorrentCard c = new DlTorrentCard();
            c.Live = true;
            c.Hash = DlJson.Str(t, "hash", "");
            c.Running = DlJson.Bool(t, "running", false);
            JVal files = t.Get("files");
            c.HasMeta = files != null && files.Kind == JKind.Arr;
            if (c.HasMeta)
            {
                c.Name = DlJson.Str(t, "name", "");
                c.Comment = DlJson.Str(t, "comment", "");
                c.CreatedBy = DlJson.Str(t, "createdBy", "");
                c.CreatedUtc = DlJson.Date(t, "created");
                c.Private = DlJson.Bool(t, "private", false);
                c.Version = DlJson.Int(t, "version", 1);
                c.PieceLength = DlJson.Long(t, "pieceLength", 0);
                c.PieceCount = DlJson.Int(t, "pieceCount", 0);
                c.TotalSize = DlJson.Long(t, "totalSize", 0);
                foreach (JVal j in files.V)
                {
                    DlTorrentFile f = new DlTorrentFile();
                    f.Index = DlJson.Int(j, "index", -1);
                    f.Path = DlJson.Str(j, "path", "");
                    f.Size = DlJson.Long(j, "size", 0);
                    f.Done = DlJson.Long(j, "done", -1);
                    f.Priority = DlJson.Clamp(DlJson.Int(j, "priority", 1), 0, 2);
                    if (f.Index >= 0) c.Files.Add(f);
                }
                int maxIndex = -1;
                foreach (DlTorrentFile f in c.Files) maxIndex = Math.Max(maxIndex, f.Index);
                c.FileCount = Math.Max(DlJson.Int(t, "fileCount", 0), maxIndex + 1);
                string pieces = DlJson.Str(t, "pieces", "");
                if (pieces.Length > 0)
                    try { c.Pieces = Convert.FromBase64String(pieces); } catch (FormatException) { c.Pieces = null; }
            }
            JVal stats = t.Get("stats");
            if (stats != null && stats.Kind == JKind.Obj)
            {
                c.HasStats = true;
                c.Downloaded = DlJson.Long(stats, "downloaded", 0);
                c.Uploaded = DlJson.Long(stats, "uploaded", 0);
                c.Wasted = DlJson.Long(stats, "wasted", 0);
                c.Ratio = Num(stats, "ratio");
                c.Availability = Num(stats, "availability");
                c.PeerCount = DlJson.Int(stats, "peers", 0);
                c.SeedCount = DlJson.Int(stats, "seeds", 0);
                c.SeedSeconds = DlJson.Long(stats, "seedSeconds", 0);
                c.ActiveSeconds = DlJson.Long(stats, "activeSeconds", 0);
                c.EtaSeconds = DlJson.Long(stats, "eta", -1);
            }
            JVal peers = t.Get("peers");
            if (peers != null && peers.Kind == JKind.Arr)
                foreach (JVal j in peers.V)
                {
                    DlTorrentPeer p = new DlTorrentPeer();
                    p.Address = DlJson.Str(j, "address", "");
                    p.Client = DlJson.Str(j, "client", "");
                    p.Flags = DlJson.Str(j, "flags", "");
                    p.Origin = DlJson.Str(j, "origin", "");
                    p.Encrypted = DlJson.Bool(j, "encrypted", false);
                    p.Outgoing = DlJson.Bool(j, "outgoing", false);
                    p.Seed = DlJson.Bool(j, "seed", false);
                    p.Progress = Math.Max(0, Math.Min(1, Num(j, "progress")));
                    p.Down = DlJson.Long(j, "down", 0);
                    p.Up = DlJson.Long(j, "up", 0);
                    p.Downloaded = DlJson.Long(j, "downloaded", 0);
                    p.Uploaded = DlJson.Long(j, "uploaded", 0);
                    c.Peers.Add(p);
                }
            JVal trackers = t.Get("trackers");
            if (trackers != null && trackers.Kind == JKind.Arr)
                foreach (JVal j in trackers.V)
                {
                    DlTorrentTracker tr = new DlTorrentTracker();
                    tr.Url = DlJson.Str(j, "url", "");
                    tr.Tier = DlJson.Int(j, "tier", 0);
                    tr.Status = DlJson.Str(j, "status", "");
                    tr.Message = DlJson.Str(j, "message", "");
                    tr.Seeders = DlJson.Int(j, "seeders", -1);
                    tr.Leechers = DlJson.Int(j, "leechers", -1);
                    tr.PeersReceived = DlJson.Int(j, "peersReceived", 0);
                    tr.NextUtc = DlJson.Date(j, "next");
                    c.Trackers.Add(tr);
                }
            JVal session = t.Get("session");
            if (session != null && session.Kind == JKind.Obj)
            {
                c.HasSession = true;
                c.Port = DlJson.Int(session, "port", 0);
                c.Inbound = DlJson.Bool(session, "inbound", false);
                c.DhtNodes = DlJson.Int(session, "dhtNodes", -1);
                c.Mapper = DlJson.Str(session, "mapper", "");
            }
            return c;
        }

        // Процесс не запущен: файлы и размеры из файла торрента, выбор файлов — из записи.
        public static DlTorrentCard FromMeta(BtMeta meta, int[] priorities)
        {
            if (meta == null) return null;
            DlTorrentCard c = new DlTorrentCard();
            c.Hash = meta.HexHash;
            c.HasMeta = true;
            c.Name = meta.Name;
            c.Comment = meta.Comment;
            c.CreatedBy = meta.CreatedBy;
            c.CreatedUtc = meta.CreatedUtc;
            c.Private = meta.Private;
            c.Version = meta.Version;
            c.PieceLength = meta.PieceLength;
            c.PieceCount = meta.PieceCount;
            c.TotalSize = meta.TotalSize;
            c.FileCount = meta.Files.Count;
            for (int i = 0; i < meta.Files.Count; i++)
            {
                if (meta.Files[i].Pad) continue;
                DlTorrentFile f = new DlTorrentFile();
                f.Index = i;
                f.Path = meta.Files[i].RelPath;
                f.Size = meta.Files[i].Length;
                f.Priority = priorities != null && i < priorities.Length ? Math.Max(0, Math.Min(2, priorities[i])) : 1;
                c.Files.Add(f);
            }
            return c;
        }

        private static double Num(JVal root, string name)
        {
            JVal v = root.Get(name);
            if (v == null) return 0;
            double d;
            return (v.Kind == JKind.Str || v.Kind == JKind.Num) && double.TryParse(v.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        // Новый выбор файлов целиком: у отмеченных (номера из Files) — value, у остальных — как было, заполнители — 1.
        public int[] PrioritiesWith(ICollection<int> indexes, int value)
        {
            int[] p = new int[FileCount];
            for (int i = 0; i < p.Length; i++) p[i] = 1;
            foreach (DlTorrentFile f in Files)
                if (f.Index < p.Length) p[f.Index] = indexes.Contains(f.Index) ? Math.Max(0, Math.Min(2, value)) : f.Priority;
            return p;
        }

        // Сколько кусков проверено; -1 — неизвестно.
        public int PiecesHave()
        {
            if (Pieces == null || PieceCount <= 0) return -1;
            int n = 0;
            for (int i = 0; i < PieceCount && (i >> 3) < Pieces.Length; i++)
                if ((Pieces[i >> 3] & (0x80 >> (i & 7))) != 0) n++;
            return n;
        }

        // Доля проверенных кусков в отрезке [from, to) — для полосы карты кусков.
        public double PiecesFraction(int from, int to)
        {
            if (Pieces == null || to <= from) return 0;
            int n = 0, total = 0;
            for (int i = from; i < to && i < PieceCount; i++)
            {
                total++;
                if ((i >> 3) < Pieces.Length && (Pieces[i >> 3] & (0x80 >> (i & 7))) != 0) n++;
            }
            return total == 0 ? 0 : (double)n / total;
        }
    }

    // Ответ команды updateInfo: новая версия раздачи и что изменит замена.
    internal sealed class DlUpdateFile
    {
        public string Path = "", Action = "", From = "";
        public long Size;
        public bool Identical, Skipped, Removed;
    }

    internal sealed class DlUpdateInfo
    {
        public string TopicUrl = "", Site = "", UpdateHash = "", Name = "", Refusal = "";
        public DateTime CheckedUtc = DateTime.MinValue;
        public bool Probing, Checking, Busy, Ready;
        public long KeepBytes, MoveBytes, ChangedBytes, NewBytes, RemovedBytes;
        public readonly List<string> Collisions = new List<string>();
        public readonly List<DlUpdateFile> Files = new List<DlUpdateFile>();

        public bool CanApply { get { return Ready && !Busy && UpdateHash.Length > 0 && Refusal.Length == 0 && Collisions.Count == 0; } }

        public static DlUpdateInfo FromJson(JVal u)
        {
            if (u == null || u.Kind != JKind.Obj) return null;
            DlUpdateInfo x = new DlUpdateInfo();
            x.TopicUrl = DlJson.Str(u, "topicUrl", "");
            x.Site = DlJson.Str(u, "site", "");
            x.UpdateHash = DlJson.Str(u, "updateHash", "");
            x.CheckedUtc = DlJson.Date(u, "checked");
            x.Probing = DlJson.Bool(u, "probing", false);
            x.Checking = DlJson.Bool(u, "checking", false);
            x.Busy = DlJson.Bool(u, "busy", false);
            x.Ready = DlJson.Bool(u, "ready", false);
            x.Name = DlJson.Str(u, "name", "");
            x.Refusal = DlJson.Str(u, "refusal", "");
            x.KeepBytes = DlJson.Long(u, "keepBytes", 0);
            x.MoveBytes = DlJson.Long(u, "moveBytes", 0);
            x.ChangedBytes = DlJson.Long(u, "changedBytes", 0);
            x.NewBytes = DlJson.Long(u, "newBytes", 0);
            x.RemovedBytes = DlJson.Long(u, "removedBytes", 0);
            JVal c = u.Get("collisions");
            if (c != null && c.Kind == JKind.Arr)
                foreach (JVal j in c.V) if (j.Kind == JKind.Str) x.Collisions.Add(j.Raw);
            JVal files = u.Get("files");
            if (files != null && files.Kind == JKind.Arr)
                foreach (JVal j in files.V)
                {
                    DlUpdateFile f = new DlUpdateFile();
                    f.Path = DlJson.Str(j, "path", "");
                    f.Size = DlJson.Long(j, "size", 0);
                    f.Action = DlJson.Str(j, "action", "");
                    f.From = DlJson.Str(j, "from", "");
                    f.Identical = DlJson.Bool(j, "identical", false);
                    f.Skipped = DlJson.Bool(j, "skipped", false);
                    x.Files.Add(f);
                }
            JVal removed = u.Get("removed");
            if (removed != null && removed.Kind == JKind.Arr)
                foreach (JVal j in removed.V)
                {
                    DlUpdateFile f = new DlUpdateFile();
                    f.Path = DlJson.Str(j, "path", "");
                    f.Size = DlJson.Long(j, "size", 0);
                    f.Removed = true;
                    x.Files.Add(f);
                }
            return x;
        }

        public int Count(string action)
        {
            int n = 0;
            foreach (DlUpdateFile f in Files) if (!f.Removed && f.Action == action) n++;
            return n;
        }

        public int RemovedCount
        {
            get
            {
                int n = 0;
                foreach (DlUpdateFile f in Files) if (f.Removed) n++;
                return n;
            }
        }
    }

    internal static class DlTorrentView
    {
        public static string PriorityText(int p)
        {
            return p <= 0 ? Tr.S("не качать", "skip") : p >= 2 ? Tr.S("высокий", "high") : Tr.S("обычный", "normal");
        }

        public static string VersionText(int version)
        {
            return version == 3 ? Tr.S("гибридный (v1 + v2)", "hybrid (v1 + v2)") : version == 2 ? "v2" : "v1";
        }

        // Файл, размер, готово, приоритет.
        public static List<string[]> FileRows(DlTorrentCard c)
        {
            List<string[]> rows = new List<string[]>();
            foreach (DlTorrentFile f in c.Files)
            {
                string done;
                if (f.Done < 0) done = "";
                else if (f.Size <= 0 || f.Done >= f.Size) done = Tr.S("готов", "done");
                else done = (int)Math.Floor(100.0 * f.Done / f.Size) + " % · " + Engine.FormatBytes(f.Done);
                rows.Add(new[] { f.Path, Engine.FormatBytes(f.Size), done, PriorityText(f.Priority) });
            }
            return rows;
        }

        // Адрес, клиент, есть у пира, приём, отдача, подробности.
        public static List<string[]> PeerRows(DlTorrentCard c)
        {
            List<string[]> rows = new List<string[]>();
            List<DlTorrentPeer> peers = new List<DlTorrentPeer>(c.Peers);
            // Сначала те, с кем идёт обмен.
            peers.Sort(delegate(DlTorrentPeer a, DlTorrentPeer b) { return (b.Down + b.Up).CompareTo(a.Down + a.Up); });
            foreach (DlTorrentPeer p in peers)
            {
                List<string> details = new List<string>();
                details.Add(p.Outgoing ? Tr.S("мы подключились", "outgoing") : Tr.S("подключился к нам", "incoming"));
                details.Add(OriginText(p.Origin));
                if (p.Encrypted) details.Add(Tr.S("шифрование", "encrypted"));
                if (p.Flags.Length > 0) details.Add(p.Flags);
                details.Add(Tr.S("всего ↓ ", "total ↓ ") + Engine.FormatBytes(p.Downloaded) + " ↑ " + Engine.FormatBytes(p.Uploaded));
                rows.Add(new[]
                {
                    p.Address, p.Client.Length > 0 ? p.Client : "—",
                    p.Seed ? Tr.S("всё (сид)", "all (seed)") : (int)Math.Floor(p.Progress * 100) + " %",
                    DlView.Speed(p.Down), DlView.Speed(p.Up), string.Join(" · ", details.ToArray())
                });
            }
            return rows;
        }

        public static string OriginText(string origin)
        {
            switch (origin)
            {
                case "Tracker": return Tr.S("от трекера", "tracker");
                case "Dht": return "DHT";
                case "Pex": return Tr.S("обмен пирами", "peer exchange");
                case "Lsd": return Tr.S("локальная сеть", "local network");
                case "Incoming": return Tr.S("входящее", "incoming");
                default: return Tr.S("вручную", "manual");
            }
        }

        // Трекер, состояние, сиды/личи, пиров получено, следующий запрос, сообщение.
        public static List<string[]> TrackerRows(DlTorrentCard c, DateTime utcNow)
        {
            List<string[]> rows = new List<string[]>();
            foreach (DlTorrentTracker t in c.Trackers)
            {
                string swarm = t.Seeders < 0 && t.Leechers < 0 ? ""
                             : (t.Seeders < 0 ? "?" : t.Seeders.ToString(CultureInfo.InvariantCulture)) + " / "
                               + (t.Leechers < 0 ? "?" : t.Leechers.ToString(CultureInfo.InvariantCulture));
                string next = t.NextUtc == DateTime.MinValue ? "" : t.NextUtc <= utcNow ? Tr.S("сейчас", "now")
                            : Tr.S("через ", "in ") + DlView.Duration((long)Math.Ceiling((t.NextUtc - utcNow).TotalSeconds));
                rows.Add(new[] { t.Url, t.Status, swarm, t.PeersReceived.ToString(CultureInfo.InvariantCulture), next, t.Message });
            }
            return rows;
        }

        // Строка «Сеть» обзора: порт, входящие, DHT, проброс порта.
        public static string SessionText(DlTorrentCard c)
        {
            if (!c.HasSession) return "";
            string text = Tr.S("порт ", "port ") + c.Port.ToString(CultureInfo.InvariantCulture)
                          + (c.Inbound ? Tr.S(" · входящие открыты", " · incoming open") : Tr.S(" · входящие закрыты", " · incoming closed"));
            if (c.DhtNodes >= 0) text += Tr.S(" · узлов DHT: ", " · DHT nodes: ") + c.DhtNodes.ToString(CultureInfo.InvariantCulture);
            if (c.Mapper.Length > 0) text += Tr.S(" · роутер: ", " · router: ") + c.Mapper;
            return text;
        }

        // ---------- новая версия раздачи ----------

        // Что будет с файлом при обновлении.
        public static string UpdateActionText(DlUpdateFile f)
        {
            string text;
            if (f.Removed) return Tr.S("в Корзину — в новой версии его нет", "to the Recycle Bin — not in the new version");
            switch (f.Action)
            {
                case "Keep":
                    text = f.Identical ? Tr.S("без изменений", "unchanged") : Tr.S("остаётся — проверится хешем", "stays — checked by hash");
                    break;
                case "Changed": text = Tr.S("изменён — докачается", "changed — downloads again"); break;
                case "Move": text = Tr.S("перенесётся из ", "moves from ") + f.From; break;
                default: text = Tr.S("новый — скачается", "new — downloads"); break;
            }
            return f.Skipped ? text + Tr.S(" · не качать, как и раньше", " · skipped, as before") : text;
        }

        // Файл, размер, что будет: сначала то, что качается, в конце — уходящее в Корзину.
        public static List<string[]> UpdateRows(DlUpdateInfo u)
        {
            List<DlUpdateFile> files = new List<DlUpdateFile>(u.Files);
            int[] rank = new int[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                DlUpdateFile f = files[i];
                rank[i] = f.Removed ? 4 : f.Action == "New" ? 0 : f.Action == "Changed" ? 1 : f.Action == "Move" ? 2 : 3;
            }
            List<int> order = new List<int>();
            for (int i = 0; i < files.Count; i++) order.Add(i);
            order.Sort(delegate(int a, int b) { int c = rank[a].CompareTo(rank[b]); return c != 0 ? c : a.CompareTo(b); });
            List<string[]> rows = new List<string[]>();
            foreach (int i in order) rows.Add(new[] { files[i].Path, Engine.FormatBytes(files[i].Size), UpdateActionText(files[i]) });
            return rows;
        }

        // Строки под именем в окне: откуда версия, сколько чего.
        public static List<string> UpdateLead(DlUpdateInfo u)
        {
            List<string> lines = new List<string>();
            if (u.UpdateHash.Length == 0)
            {
                lines.Add(Tr.S("Новой версии сейчас нет.", "There is no new version now."));
                return lines;
            }
            lines.Add(Tr.S("Новая версия: ", "New version: ") + u.UpdateHash + (u.Ready && u.Name.Length > 0 ? " · " + u.Name : ""));
            if (!u.Ready)
            {
                lines.Add(u.Probing ? Tr.S("Метаданные новой версии запрашиваются у пиров раздачи — обычно это минута-другая. Откройте окно позже.",
                                           "The new version's metadata is being requested from the swarm — usually a minute or two. Open this window later.")
                                    : Tr.S("Метаданные новой версии пока не получены: пиров с ней не нашлось, повтор — через час. Можно скачать .torrent со страницы темы и выбрать «Из файла .torrent…».",
                                           "The new version's metadata has not arrived: no peers had it, retry in an hour. You can download the .torrent from the topic page and pick “From a .torrent file…”."));
                return lines;
            }
            List<string> parts = new List<string>();
            int keep = u.Count("Keep"), changed = u.Count("Changed"), moved = u.Count("Move"), added = u.Count("New"), removed = u.RemovedCount;
            if (keep > 0) parts.Add(Tr.S("остаются ", "stay ") + keep + " (" + Engine.FormatBytes(u.KeepBytes) + ")");
            if (moved > 0) parts.Add(Tr.S("переносятся ", "move ") + moved + " (" + Engine.FormatBytes(u.MoveBytes) + ")");
            if (changed > 0) parts.Add(Tr.S("изменены ", "changed ") + changed + " (" + Engine.FormatBytes(u.ChangedBytes) + ")");
            if (added > 0) parts.Add(Tr.S("новых ", "new ") + added + " (" + Engine.FormatBytes(u.NewBytes) + ")");
            if (removed > 0) parts.Add(Tr.S("в Корзину ", "to the Recycle Bin ") + removed + " (" + Engine.FormatBytes(u.RemovedBytes) + ")");
            lines.Add(Tr.S("Файлы: ", "Files: ") + (parts.Count > 0 ? string.Join(" · ", parts.ToArray()) : Tr.S("нет", "none")));
            lines.Add(Tr.S("Скачанное остаётся на месте, папка раздачи не переименовывается. После обновления данные проверяются хешем новой версии и докачивается только изменённое.",
                           "What was downloaded stays in place and the torrent folder keeps its name. After the update the data is checked against the new version and only the changes download."));
            return lines;
        }

        // Почему «Обновить» недоступно; "" — доступно.
        public static string UpdateBlocker(DlUpdateInfo u)
        {
            if (u.Busy) return Tr.S("Раздача сейчас обновляется.", "The torrent is being updated right now.");
            if (u.UpdateHash.Length == 0 || !u.Ready) return "";
            if (u.Refusal.Length > 0) return Tr.S("Обновить нельзя: ", "Cannot update: ") + u.Refusal;
            if (u.Collisions.Count > 0)
                return Tr.S("На местах новых файлов лежат чужие файлы — уберите их и откройте окно снова: ", "Foreign files sit where new files go — move them away and open this window again: ")
                       + string.Join("; ", u.Collisions.GetRange(0, Math.Min(2, u.Collisions.Count)).ToArray()) + (u.Collisions.Count > 2 ? " (+" + (u.Collisions.Count - 2) + ")" : "");
            return "";
        }

        // Строка «Новая версия» в обзоре торрента; "" — строки нет (в торренте нет ссылки на тему и новой версии не находили).
        public static string UpdateStateText(DlItem it, DateTime utcNow)
        {
            if (!it.IsTorrent) return "";
            if (it.UpdateHash.Length > 0) return Tr.S("есть — «Обновить раздачу…» в меню записи", "available — “Update the torrent…” in the item's menu");
            if (it.TopicUrl.Length == 0) return "";
            if (BtTopic.RutrackerId(it.TopicUrl) == null)
                return Tr.S("программа сама проверяет только rutracker — новую версию можно взять файлом со страницы темы", "the program itself checks only rutracker — take a new version as a file from the topic page");
            return it.UpdateCheckedUtc == DateTime.MinValue ? Tr.S("ещё не проверялась", "not checked yet")
                                                            : Tr.S("нет · проверено ", "none · checked ") + DlView.Local(it.UpdateCheckedUtc, utcNow);
        }
    }
}
