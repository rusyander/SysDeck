// SysDeck — профили браузеров, закладки, группы вкладок, сеансы, контрольная сумма закладок
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
    // ---------------- модели ----------------

    public class BrowserProfile
    {
        public string Browser;          // «Google Chrome»
        public string ProcessName;      // chrome — для проверки «браузер запущен»
        public string UserDataDir;
        public string Dir;              // каталог профиля
        public string ProfileName;      // человекочитаемое имя профиля
        public string Display { get { return Browser + " — " + ProfileName; } }
    }

    public class BmNode
    {
        public string Id, Guid, Name, Url;
        public bool IsFolder;
        public DateTime? Added, LastUsed, Modified;
        public List<BmNode> Children = new List<BmNode>();
        public BmNode Parent;
        public JVal Raw;                // узел в исходном JSON — по нему и правим
        public string PathText;         // «Панель закладок / Работа / React»
        public int TotalUrls;           // ссылок внутри, с подпапками
        public string LinkState;        // результат проверки ссылки
    }

    public class TabRec
    {
        public string Url, Title;
        public long Pos;
        public DateTime? Created, Updated;
        public string LinkState;
    }

    public class TabGroupRec
    {
        public string Guid, Title, Color;
        public DateTime? Created, Updated;
        public List<TabRec> Tabs = new List<TabRec>();
        public bool OpenNow;
    }

    public class ReadingRec
    {
        public string Url, Title;
        public DateTime? Added, Updated;
        public bool Read;
        public string LinkState;
    }

    public class OpenTabRec
    {
        public int TabId, WindowId, Index;
        public string Url, Title, Group;
        public string LinkState;
    }

    public class OpenWindowRec
    {
        public int WindowId;
        public List<OpenTabRec> Tabs = new List<OpenTabRec>();
    }

    public class BrowserSnapshot
    {
        public BrowserProfile Profile;
        public JVal BookmarksDoc;                       // весь файл Bookmarks
        public List<BmNode> Roots = new List<BmNode>(); // «Панель закладок» / «Другие» / «Мобильные»
        public List<TabGroupRec> Groups = new List<TabGroupRec>();
        public List<ReadingRec> Reading = new List<ReadingRec>();
        public List<OpenWindowRec> Windows = new List<OpenWindowRec>();
        public string SessionFileNote;                  // какой файл сеанса удалось прочитать
        public List<string> Notes = new List<string>(); // что прочитать не удалось
        public int BookmarkCount, FolderCount;
        public bool ChecksumOk;              // сумма исходного файла воспроизводится нашим алгоритмом
    }

    public static class BrowserData
    {
        // ---------- поиск профилей ----------

        private class Install
        {
            public string Name, ProcessName, Root;
            public bool Flat;                            // Opera: каталог профиля = корень
            public Install(string n, string p, string r, bool flat) { Name = n; ProcessName = p; Root = r; Flat = flat; }
        }

        public static List<BrowserProfile> FindProfiles()
        {
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string rad = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            List<Install> installs = new List<Install>();
            installs.Add(new Install("Google Chrome", "chrome", Path.Combine(lad, "Google\\Chrome\\User Data"), false));
            installs.Add(new Install("Chrome Beta", "chrome", Path.Combine(lad, "Google\\Chrome Beta\\User Data"), false));
            installs.Add(new Install("Chrome Dev", "chrome", Path.Combine(lad, "Google\\Chrome Dev\\User Data"), false));
            installs.Add(new Install("Chrome Canary", "chrome", Path.Combine(lad, "Google\\Chrome SxS\\User Data"), false));
            installs.Add(new Install("Microsoft Edge", "msedge", Path.Combine(lad, "Microsoft\\Edge\\User Data"), false));
            installs.Add(new Install("Edge Beta", "msedge", Path.Combine(lad, "Microsoft\\Edge Beta\\User Data"), false));
            installs.Add(new Install("Edge Dev", "msedge", Path.Combine(lad, "Microsoft\\Edge Dev\\User Data"), false));
            installs.Add(new Install("Yandex Browser", "browser", Path.Combine(lad, "Yandex\\YandexBrowser\\User Data"), false));
            installs.Add(new Install("Brave", "brave", Path.Combine(lad, "BraveSoftware\\Brave-Browser\\User Data"), false));
            installs.Add(new Install("Vivaldi", "vivaldi", Path.Combine(lad, "Vivaldi\\User Data"), false));
            installs.Add(new Install("Chromium", "chrome", Path.Combine(lad, "Chromium\\User Data"), false));
            installs.Add(new Install("Opera", "opera", Path.Combine(rad, "Opera Software\\Opera Stable"), true));
            installs.Add(new Install("Opera GX", "opera", Path.Combine(rad, "Opera Software\\Opera GX Stable"), true));
            installs.Add(new Install("Opera Developer", "opera", Path.Combine(rad, "Opera Software\\Opera Developer"), true));

            List<BrowserProfile> res = new List<BrowserProfile>();
            foreach (Install ins in installs)
            {
                if (!Directory.Exists(ins.Root)) continue;
                if (ins.Flat)
                {
                    BrowserProfile p = MakeProfile(ins, ins.Root, ins.Root);
                    if (p != null) res.Add(p);
                    continue;
                }
                string[] subs;
                try { subs = Directory.GetDirectories(ins.Root); }
                catch { continue; }
                foreach (string d in subs)
                {
                    string name = Path.GetFileName(d);
                    bool candidate = name == "Default" || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
                    if (!candidate) continue;
                    BrowserProfile p = MakeProfile(ins, ins.Root, d);
                    if (p != null) res.Add(p);
                }
            }
            return res;
        }

        private static BrowserProfile MakeProfile(Install ins, string root, string dir)
        {
            bool hasBm = File.Exists(Path.Combine(dir, "Bookmarks"));
            bool hasPref = File.Exists(Path.Combine(dir, "Preferences"));
            if (!hasBm && !hasPref) return null;

            BrowserProfile p = new BrowserProfile();
            p.Browser = ins.Name;
            p.ProcessName = ins.ProcessName;
            p.UserDataDir = root;
            p.Dir = dir;
            p.ProfileName = Path.GetFileName(dir);
            try
            {
                if (hasPref)
                {
                    string txt = ReadTextShared(Path.Combine(dir, "Preferences"));
                    JVal pref = Jsn.Parse(txt);
                    JVal pr = pref.Get("profile");
                    string nm = pr == null ? null : pr.GetStr("name");
                    if (!string.IsNullOrEmpty(nm)) p.ProfileName = nm + " (" + Path.GetFileName(dir) + ")";
                }
            }
            catch { }
            return p;
        }

        public static string ReadTextShared(string path)
        {
            byte[] b = LevelDbLite.ReadShared(path);
            return new UTF8Encoding(false).GetString(b);
        }

        public static bool IsRunning(BrowserProfile p)
        {
            if (p == null || string.IsNullOrEmpty(p.ProcessName)) return false;
            try { return Process.GetProcessesByName(p.ProcessName).Length > 0; }
            catch { return false; }
        }

        // ---------- загрузка ----------

        public static BrowserSnapshot Load(BrowserProfile p)
        {
            BrowserSnapshot s = new BrowserSnapshot();
            s.Profile = p;
            LoadBookmarks(s);
            LoadSyncStore(s);
            LoadSession(s);
            return s;
        }

        private static readonly DateTime WinEpoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static DateTime? FromWinMicros(string s)
        {
            long v;
            if (string.IsNullOrEmpty(s) || !long.TryParse(s, out v) || v <= 0) return null;
            return FromWinMicros((ulong)v);
        }

        public static DateTime? FromWinMicros(ulong us)
        {
            if (us == 0) return null;
            try { return WinEpoch.AddTicks((long)us * 10).ToLocalTime(); }
            catch { return null; }
        }

        public static DateTime? FromUnixMicros(ulong us)
        {
            if (us == 0) return null;
            try { return UnixEpoch.AddTicks((long)us * 10).ToLocalTime(); }
            catch { return null; }
        }

        // ---- закладки ----

        private static void LoadBookmarks(BrowserSnapshot s)
        {
            string path = Path.Combine(s.Profile.Dir, "Bookmarks");
            if (!File.Exists(path)) return;
            try
            {
                s.BookmarksDoc = Jsn.Parse(ReadTextShared(path));
            }
            catch (Exception ex)
            {
                s.Notes.Add(Tr.S("Закладки не прочитаны: ", "Bookmarks not read: ") + ex.Message);
                return;
            }
            JVal roots = s.BookmarksDoc.Get("roots");
            if (roots == null) return;

            string[] titles = {
                Tr.S("Панель закладок", "Bookmarks bar"),
                Tr.S("Другие закладки", "Other bookmarks"),
                Tr.S("Мобильные закладки", "Mobile bookmarks") };
            List<string> order = RootOrder(s.BookmarksDoc);
            for (int i = 0; i < order.Count; i++)
            {
                JVal r = roots.Get(order[i]);
                if (r == null || r.Kind != JKind.Obj) continue;
                BmNode n = BuildNode(r, null, "");
                if (string.IsNullOrEmpty(n.Name)) n.Name = i < titles.Length ? titles[i] : order[i];
                n.PathText = n.Name;
                FixPaths(n);
                s.Roots.Add(n);
            }
            s.ChecksumOk = ChecksumMatches(s.BookmarksDoc);
            foreach (BmNode r in s.Roots) Count(r, s);
        }

        private static BmNode BuildNode(JVal j, BmNode parent, string parentPath)
        {
            BmNode n = new BmNode();
            n.Raw = j;
            n.Parent = parent;
            n.Id = j.GetStr("id");
            n.Guid = j.GetStr("guid");
            n.Name = j.GetStr("name") ?? "";
            n.Url = j.GetStr("url");
            string type = j.GetStr("type");
            n.IsFolder = (type == "folder") || (type == null && j.Get("children") != null);
            n.Added = FromWinMicros(j.GetStr("date_added"));
            n.Modified = FromWinMicros(j.GetStr("date_modified"));
            DateTime? lu = FromWinMicros(j.GetStr("date_last_used"));
            JVal meta = j.Get("meta_info");
            if (meta != null)
            {
                DateTime? v1 = FromWinMicros(meta.GetStr("last_visited"));
                DateTime? v2 = FromWinMicros(meta.GetStr("last_visited_desktop"));
                if (v1.HasValue && (!lu.HasValue || v1 > lu)) lu = v1;
                if (v2.HasValue && (!lu.HasValue || v2 > lu)) lu = v2;
            }
            n.LastUsed = lu;

            JVal kids = j.Get("children");
            if (kids != null && kids.Kind == JKind.Arr)
                foreach (JVal c in kids.V)
                    if (c.Kind == JKind.Obj)
                        n.Children.Add(BuildNode(c, n, ""));
            return n;
        }

        private static void FixPaths(BmNode n)
        {
            foreach (BmNode c in n.Children)
            {
                c.PathText = string.IsNullOrEmpty(n.PathText) ? c.Name : n.PathText + " / " + c.Name;
                if (c.IsFolder) FixPaths(c);
            }
        }

        private static int Count(BmNode n, BrowserSnapshot s)
        {
            int total = 0;
            foreach (BmNode c in n.Children)
            {
                if (c.IsFolder) { s.FolderCount++; total += Count(c, s); }
                else { s.BookmarkCount++; total++; }
            }
            n.TotalUrls = total;
            return total;
        }

        // ---- сохранённые группы вкладок + список для чтения ----

        private static readonly string[] SyncColors = {
            "-", "серый", "синий", "красный", "жёлтый", "зелёный", "розовый", "фиолетовый", "голубой", "оранжевый" };
        private static readonly string[] SyncColorsEn = {
            "-", "grey", "blue", "red", "yellow", "green", "pink", "purple", "cyan", "orange" };

        public static string ColorName(long v, bool syncScale)
        {
            long idx = syncScale ? v : v + 1;             // в сеансе нумерация на единицу меньше
            if (idx < 0 || idx >= SyncColors.Length) return "?";
            return Tr.En ? SyncColorsEn[idx] : SyncColors[idx];
        }

        private static void LoadSyncStore(BrowserSnapshot s)
        {
            string dir = Path.Combine(s.Profile.Dir, "Sync Data\\LevelDB");
            if (!Directory.Exists(dir)) return;
            Dictionary<string, byte[]> db;
            try { db = LevelDbLite.ReadAll(dir); }
            catch (Exception ex)
            {
                s.Notes.Add(Tr.S("Хранилище синхронизации не прочитано: ", "Sync store not read: ") + ex.Message);
                return;
            }

            Dictionary<string, TabGroupRec> groups = new Dictionary<string, TabGroupRec>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<TabRec>> tabs = new Dictionary<string, List<TabRec>>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, byte[]> kv in db)
            {
                string k = kv.Key;
                if (k.StartsWith("saved_tab_group-dt-", StringComparison.Ordinal))
                {
                    try { ParseSavedGroup(kv.Value, groups, tabs); } catch { }
                }
                else if (k.StartsWith("reading_list-dt-", StringComparison.Ordinal))
                {
                    try { ParseReading(kv.Value, k.Substring("reading_list-dt-".Length), s); } catch { }
                }
            }

            foreach (KeyValuePair<string, List<TabRec>> kv in tabs)
            {
                TabGroupRec g;
                if (!groups.TryGetValue(kv.Key, out g))
                {
                    // Вкладки без своей группы — показываем отдельной строкой, а не молча теряем.
                    g = new TabGroupRec();
                    g.Guid = kv.Key;
                    g.Title = Tr.S("(группа не найдена)", "(group missing)");
                    g.Color = "?";
                    groups[kv.Key] = g;
                }
                kv.Value.Sort(delegate(TabRec a, TabRec b) { return a.Pos.CompareTo(b.Pos); });
                g.Tabs = kv.Value;
                foreach (TabRec t in kv.Value)
                    if (t.Updated.HasValue && (!g.Updated.HasValue || t.Updated > g.Updated)) g.Updated = t.Updated;
            }

            s.Groups = groups.Values.ToList();
            s.Groups.Sort(delegate(TabGroupRec a, TabGroupRec b)
            {
                DateTime da = a.Updated ?? DateTime.MinValue, dbb = b.Updated ?? DateTime.MinValue;
                return dbb.CompareTo(da);
            });
            s.Reading.Sort(delegate(ReadingRec a, ReadingRec b)
            {
                DateTime da = a.Updated ?? a.Added ?? DateTime.MinValue, dbb = b.Updated ?? b.Added ?? DateTime.MinValue;
                return dbb.CompareTo(da);
            });
        }

        // SavedTabGroupSpecifics: 1 guid, 2 created, 3 updated, 4 group{2 title,3 color}, 5 tab{1 group_guid,2 pos,3 url,4 title}
        private static void ParseSavedGroup(byte[] val,
                                            Dictionary<string, TabGroupRec> groups,
                                            Dictionary<string, List<TabRec>> tabs)
        {
            Dictionary<int, List<object>> top = Pb.Parse(val);
            Dictionary<int, List<object>> spec = Pb.Msg(top, 2);
            if (spec == null) return;
            string guid = Pb.Str(spec, 1);
            DateTime? created = FromWinMicros(Pb.U64(spec, 2));
            DateTime? updated = FromWinMicros(Pb.U64(spec, 3));

            Dictionary<int, List<object>> g = Pb.Msg(spec, 4);
            if (g != null)
            {
                TabGroupRec r = new TabGroupRec();
                r.Guid = guid;
                r.Title = Pb.Str(g, 2) ?? "";
                r.Color = ColorName((long)Pb.U64(g, 3), true);
                r.Created = created;
                r.Updated = updated;
                if (string.IsNullOrEmpty(r.Title)) r.Title = Tr.S("(без названия)", "(untitled)");
                groups[guid ?? ""] = r;
                return;
            }
            Dictionary<int, List<object>> t = Pb.Msg(spec, 5);
            if (t != null)
            {
                string gg = Pb.Str(t, 1) ?? "";
                TabRec tr = new TabRec();
                tr.Pos = (long)Pb.U64(t, 2);
                tr.Url = Pb.Str(t, 3) ?? "";
                tr.Title = Pb.Str(t, 4) ?? "";
                tr.Created = created;
                tr.Updated = updated;
                List<TabRec> lst;
                if (!tabs.TryGetValue(gg, out lst)) { lst = new List<TabRec>(); tabs[gg] = lst; }
                lst.Add(tr);
            }
        }

        // ReadingListLocal: 1 entry_id, 2 title, 3 url, 4 creation_us, 5 update_us, 6 state
        private static void ParseReading(byte[] val, string keyUrl, BrowserSnapshot s)
        {
            Dictionary<int, List<object>> m = Pb.Parse(val);
            ReadingRec r = new ReadingRec();
            r.Url = Pb.Str(m, 3) ?? Pb.Str(m, 1) ?? keyUrl;
            r.Title = Pb.Str(m, 2) ?? "";
            r.Added = FromUnixMicros(Pb.U64(m, 4));
            r.Updated = FromUnixMicros(Pb.U64(m, 5));
            r.Read = Pb.U64(m, 6) == 2;
            s.Reading.Add(r);
        }

        // ---- открытый сеанс ----

        private static void LoadSession(BrowserSnapshot s)
        {
            string dir = Path.Combine(s.Profile.Dir, "Sessions");
            if (!Directory.Exists(dir)) return;
            string[] files;
            try { files = Directory.GetFiles(dir, "Session_*"); }
            catch { return; }
            if (files.Length == 0) return;
            Array.Sort(files, delegate(string a, string b)
            {
                return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a));
            });

            foreach (string f in files)
            {
                try
                {
                    if (ParseSession(f, s)) { s.SessionFileNote = Path.GetFileName(f); return; }
                }
                catch { }
            }
            s.Notes.Add(Tr.S("Файл текущего сеанса прочитать не удалось (занят браузером).",
                             "Could not read the current session file (locked by the browser)."));
        }

        private static bool ParseSession(string path, BrowserSnapshot s)
        {
            List<Snss.Cmd> cmds = Snss.Read(path);
            if (cmds.Count == 0) return false;

            HashSet<int> windowIds = new HashSet<int>();
            Dictionary<int, int> tabWindow = new Dictionary<int, int>();
            Dictionary<int, int> tabIndex = new Dictionary<int, int>();
            Dictionary<int, int> tabSelNav = new Dictionary<int, int>();
            Dictionary<int, string> tabGroup = new Dictionary<int, string>();
            Dictionary<string, string> groupTitle = new Dictionary<string, string>();
            Dictionary<int, Dictionary<int, string[]>> navs = new Dictionary<int, Dictionary<int, string[]>>();
            HashSet<int> closedTabs = new HashSet<int>();
            HashSet<int> closedWindows = new HashSet<int>();

            foreach (Snss.Cmd c in cmds)
            {
                switch (c.Id)
                {
                    case 9:                                     // SetWindowType
                        if (c.Data.Length >= 4) windowIds.Add(BitConverter.ToInt32(c.Data, 0));
                        break;
                    case 16:                                    // TabClosed
                        if (c.Data.Length >= 4) closedTabs.Add(BitConverter.ToInt32(c.Data, 0));
                        break;
                    case 17:                                    // WindowClosed
                        if (c.Data.Length >= 4) closedWindows.Add(BitConverter.ToInt32(c.Data, 0));
                        break;
                }
            }

            foreach (Snss.Cmd c in cmds)
            {
                try
                {
                    switch (c.Id)
                    {
                        case 0:                                 // SetTabWindow: {window_id, tab_id}
                            if (c.Data.Length >= 8)
                            {
                                int a = BitConverter.ToInt32(c.Data, 0);
                                int b = BitConverter.ToInt32(c.Data, 4);
                                int win = windowIds.Contains(a) ? a : (windowIds.Contains(b) ? b : a);
                                int tab = win == a ? b : a;
                                tabWindow[tab] = win;
                            }
                            break;
                        case 2:                                 // SetTabIndexInWindow: {tab_id, index}
                            if (c.Data.Length >= 8) tabIndex[BitConverter.ToInt32(c.Data, 0)] = BitConverter.ToInt32(c.Data, 4);
                            break;
                        case 7:                                 // SetSelectedNavigationIndex: {tab_id, index}
                            if (c.Data.Length >= 8) tabSelNav[BitConverter.ToInt32(c.Data, 0)] = BitConverter.ToInt32(c.Data, 4);
                            break;
                        case 6:                                 // UpdateTabNavigation (pickle)
                            {
                                PickleReader p = new PickleReader(c.Data, 0);
                                p.Int32();                      // размер вложенного pickle
                                int tab = p.Int32();
                                int idx = p.Int32();
                                string url = p.Str();
                                string title = p.Str16();
                                if (url == null) break;
                                Dictionary<int, string[]> byIdx;
                                if (!navs.TryGetValue(tab, out byIdx)) { byIdx = new Dictionary<int, string[]>(); navs[tab] = byIdx; }
                                byIdx[idx] = new string[] { url, title ?? "" };
                            }
                            break;
                        case 25:                                // SetTabGroup: {tab_id, _, hi, lo, has}
                            if (c.Data.Length >= 24)
                            {
                                int tab = BitConverter.ToInt32(c.Data, 0);
                                ulong hi = BitConverter.ToUInt64(c.Data, 8);
                                ulong lo = BitConverter.ToUInt64(c.Data, 16);
                                bool has = c.Data.Length < 25 || c.Data[24] != 0;
                                if (has && (hi != 0 || lo != 0)) tabGroup[tab] = hi.ToString("x16") + lo.ToString("x16");
                                else tabGroup.Remove(tab);
                            }
                            break;
                        case 27:                                // SetTabGroupMetadata2 (pickle)
                            {
                                PickleReader p = new PickleReader(c.Data, 0);
                                p.Int32();
                                ulong hi = (ulong)p.Int64();
                                ulong lo = (ulong)p.Int64();
                                string title = p.Str16();
                                long color = p.Int32();
                                string id = hi.ToString("x16") + lo.ToString("x16");
                                string nm = string.IsNullOrEmpty(title) ? Tr.S("(без названия)", "(untitled)") : title;
                                groupTitle[id] = nm + " [" + ColorName(color, false) + "]";
                            }
                            break;
                    }
                }
                catch { }
            }

            if (navs.Count == 0) return false;

            Dictionary<int, OpenWindowRec> wins = new Dictionary<int, OpenWindowRec>();
            foreach (KeyValuePair<int, Dictionary<int, string[]>> kv in navs)
            {
                int tab = kv.Key;
                if (closedTabs.Contains(tab)) continue;
                int win;
                if (!tabWindow.TryGetValue(tab, out win)) continue;
                if (closedWindows.Contains(win)) continue;

                int sel;
                string[] nav = null;
                if (tabSelNav.TryGetValue(tab, out sel)) kv.Value.TryGetValue(sel, out nav);
                if (nav == null)
                {
                    int max = int.MinValue;
                    foreach (KeyValuePair<int, string[]> n in kv.Value) if (n.Key > max) { max = n.Key; nav = n.Value; }
                }
                if (nav == null) continue;

                OpenTabRec t = new OpenTabRec();
                t.TabId = tab; t.WindowId = win;
                t.Index = tabIndex.ContainsKey(tab) ? tabIndex[tab] : 0;
                t.Url = nav[0]; t.Title = nav[1];
                string gid;
                if (tabGroup.TryGetValue(tab, out gid))
                    t.Group = groupTitle.ContainsKey(gid) ? groupTitle[gid] : Tr.S("(группа)", "(group)");
                else t.Group = "";

                OpenWindowRec w;
                if (!wins.TryGetValue(win, out w)) { w = new OpenWindowRec(); w.WindowId = win; wins[win] = w; }
                w.Tabs.Add(t);
            }
            foreach (OpenWindowRec w in wins.Values)
                w.Tabs.Sort(delegate(OpenTabRec a, OpenTabRec b) { return a.Index.CompareTo(b.Index); });
            s.Windows = wins.Values.OrderBy(delegate(OpenWindowRec w) { return w.WindowId; }).ToList();
            return s.Windows.Count > 0;
        }

        // ---------- запись закладок ----------

        // Контрольная сумма BookmarkCodec: MD5 по обходу дерева.
        // Строки имён идут в UTF-16LE, id и url — как есть.
        // Проверено на реальном файле: совпадает с полем "checksum" байт в байт.
        // Порядок обхода корней: три штатных, затем всё, что добавил конкретный
        // браузер. У Яндекса это «collections», и без него сумма не сходится —
        // проверено на его реальном файле.
        public static List<string> RootOrder(JVal doc)
        {
            List<string> order = new List<string>();
            JVal roots = doc == null ? null : doc.Get("roots");
            if (roots == null || roots.Kind != JKind.Obj) return order;
            string[] known = { "bookmark_bar", "other", "synced" };
            foreach (string k in known) if (roots.Get(k) != null) order.Add(k);
            for (int i = 0; i < roots.K.Count; i++)
                if (roots.V[i].Kind == JKind.Obj && !order.Contains(roots.K[i])) order.Add(roots.K[i]);
            return order;
        }

        // Единственная гарантия, что мы вправе переписывать файл: наша контрольная
        // сумма НЕтронутого файла обязана совпасть с той, что записал браузер.
        // Не совпала — значит у этой сборки свой алгоритм, и запись запрещена.
        public static bool ChecksumMatches(JVal doc)
        {
            string stored = doc == null ? null : doc.GetStr("checksum");
            if (string.IsNullOrEmpty(stored)) return false;
            try { return string.Equals(stored, Checksum(doc), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static string Checksum(JVal doc)
        {
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                List<byte> buf = new List<byte>(1 << 20);
                JVal roots = doc.Get("roots");
                foreach (string r in RootOrder(doc))
                {
                    JVal n = roots == null ? null : roots.Get(r);
                    if (n != null && n.Kind == JKind.Obj) HashNode(n, buf);
                }
                byte[] hash = md5.ComputeHash(buf.ToArray());
                StringBuilder sb = new StringBuilder(32);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void HashNode(JVal n, List<byte> buf)
        {
            string id = n.GetStr("id") ?? "";
            string name = n.GetStr("name") ?? "";
            string type = n.GetStr("type");
            JVal kids = n.Get("children");
            bool folder = (type == "folder") || (type == null && kids != null);
            buf.AddRange(Encoding.UTF8.GetBytes(id));
            buf.AddRange(Encoding.Unicode.GetBytes(name));
            if (folder)
            {
                buf.AddRange(Encoding.UTF8.GetBytes("folder"));
                if (kids != null && kids.Kind == JKind.Arr)
                    foreach (JVal c in kids.V) if (c.Kind == JKind.Obj) HashNode(c, buf);
            }
            else
            {
                buf.AddRange(Encoding.UTF8.GetBytes("url"));
                buf.AddRange(Encoding.UTF8.GetBytes(n.GetStr("url") ?? ""));
            }
        }

        // Копии закладок лежат там же, где остальные данные программы. Раньше путь был
        // прибит к %APPDATA% напрямую, и портативная сборка, у которой всё хранится рядом
        // с exe, всё равно оставляла копии в профиле — на чужой машине это ровно то,
        // чего портативная сборка и должна избегать.
        public static string BackupDir
        {
            get
            {
                string d = Path.Combine(Engine.DefaultDataDir(), "browser-backups");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        // Пишем закладки обратно: сначала копия, потом пересчёт контрольной суммы,
        // потом атомарная замена через временный файл.
        public static string SaveBookmarks(BrowserProfile p, JVal doc)
        {
            string path = Path.Combine(p.Dir, "Bookmarks");
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string safe = (p.Browser + "-" + p.ProfileName).Replace(' ', '_');
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            string backup = Path.Combine(BackupDir, safe + "-" + stamp + ".bak");
            File.Copy(path, backup, true);

            doc.Set("checksum", JVal.NewStr(Checksum(doc)));
            string text = Jsn.Write(doc);

            string tmp = path + ".wpctmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            // Chrome сам держит .bak рядом с Bookmarks; свой пишем в каталог программы,
            // чтобы не мешать его собственному механизму восстановления.
            // File.Replace — атомарная замена: между Delete и Move было окно, в котором
            // файла закладок не существовало вовсе (сбой в этот момент = профиль без закладок).
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return backup;
        }
    }
}
