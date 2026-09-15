// SysDeck — тесты конфига: круговорот полей, миграция версий, полка выбора,
// битый файл, ключи «состава» категории.
//
// Настройки — единственное, что пользователь теряет молча: неудачная сериализация нового поля
// или пропущенная ступень миграции гасит уже сделанный выбор, и заметить это можно только по
// исчезнувшим галочкам. Каждое поле проверяется отдельно и сверяется с литералом, который
// задан здесь же, а не с тем, что вернул сам конфиг.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SysDeck.Tests
{
    internal static class ConfigTests
    {
        private sealed class Fld
        {
            internal string Name;
            internal Action<AppConfig> Set;
            internal Func<AppConfig, object> Get;
            internal object Want;
        }

        internal static void Run()
        {
            RoundTrip();
            Migration();
            Corrupt();
            Shelf();
            TargetChoice();
        }

        // ---------- круговорот всех полей ----------
        private static void RoundTrip()
        {
            Fld[] fields = Fields();

            Engine w = Fx.NewEngine("cfg");
            foreach (Fld f in fields) f.Set(w.Config);
            w.SaveConfig();

            Engine r = Fx.NewEngine("cfg");   // тот же каталог — конфиг читается с диска заново
            foreach (Fld f in fields)
                T.Eq("config field " + f.Name + " survives a save and a reload", f.Want, f.Get(r.Config));

            T.Eq("a saved config is stamped with the current version",
                 AppConfig.CurrentVersion, r.Config.ConfigVersion);
            T.Check("the config file lives in the app data folder",
                    File.Exists(Path.Combine(r.DataDir, "config.json")));
        }

        // Значения выбраны заведомо не умолчательными и внутри допустимых границ: совпадение
        // с умолчанием не отличило бы «поле прочиталось» от «поле потерялось».
        private static Fld[] Fields()
        {
            List<Fld> l = new List<Fld>();
            Add(l, "CpuThresholdPercent", 3.5,
                delegate(AppConfig c) { c.CpuThresholdPercent = 3.5; },
                delegate(AppConfig c) { return c.CpuThresholdPercent; });
            Add(l, "IdleMinutes", 7,
                delegate(AppConfig c) { c.IdleMinutes = 7; },
                delegate(AppConfig c) { return c.IdleMinutes; });
            Add(l, "MinLifetimeMinutes", 9,
                delegate(AppConfig c) { c.MinLifetimeMinutes = 9; },
                delegate(AppConfig c) { return c.MinLifetimeMinutes; });
            Add(l, "AutoIntervalHours", 6,
                delegate(AppConfig c) { c.AutoIntervalHours = 6; },
                delegate(AppConfig c) { return c.AutoIntervalHours; });
            Add(l, "AutoEnabled", true,
                delegate(AppConfig c) { c.AutoEnabled = true; },
                delegate(AppConfig c) { return c.AutoEnabled; });
            Add(l, "Autostart", true,
                delegate(AppConfig c) { c.Autostart = true; },
                delegate(AppConfig c) { return c.Autostart; });
            Add(l, "StartMinimized", true,
                delegate(AppConfig c) { c.StartMinimized = true; },
                delegate(AppConfig c) { return c.StartMinimized; });
            Add(l, "Theme", "dark",
                delegate(AppConfig c) { c.Theme = "dark"; },
                delegate(AppConfig c) { return c.Theme; });
            Add(l, "GlobalScan", true,
                delegate(AppConfig c) { c.GlobalScan = true; },
                delegate(AppConfig c) { return c.GlobalScan; });
            Add(l, "GlobalIdleMinutes", 42,
                delegate(AppConfig c) { c.GlobalIdleMinutes = 42; },
                delegate(AppConfig c) { return c.GlobalIdleMinutes; });
            Add(l, "GlobalExcludeInstalled", false,
                delegate(AppConfig c) { c.GlobalExcludeInstalled = false; },
                delegate(AppConfig c) { return c.GlobalExcludeInstalled; });
            Add(l, "Language", "en",
                delegate(AppConfig c) { c.Language = "en"; },
                delegate(AppConfig c) { return c.Language; });
            Add(l, "Watchlist", "aaa.exe|bbb.exe",
                delegate(AppConfig c) { c.Watchlist = new List<string>(new string[] { "aaa.exe", "bbb.exe" }); },
                delegate(AppConfig c) { return Join(c.Watchlist); });
            Add(l, "Whitelist", "ccc.exe",
                delegate(AppConfig c) { c.Whitelist = new List<string>(new string[] { "ccc.exe" }); },
                delegate(AppConfig c) { return Join(c.Whitelist); });
            Add(l, "DevPorts", "1234|5678",
                delegate(AppConfig c) { c.DevPorts = new List<int>(new int[] { 1234, 5678 }); },
                delegate(AppConfig c) { return JoinInts(c.DevPorts); });
            Add(l, "MonitorIntervalSeconds", 33,
                delegate(AppConfig c) { c.MonitorIntervalSeconds = 33; },
                delegate(AppConfig c) { return c.MonitorIntervalSeconds; });
            Add(l, "MonitorEnabled", false,
                delegate(AppConfig c) { c.MonitorEnabled = false; },
                delegate(AppConfig c) { return c.MonitorEnabled; });
            Add(l, "EmptyWorkingSets", true,
                delegate(AppConfig c) { c.EmptyWorkingSets = true; },
                delegate(AppConfig c) { return c.EmptyWorkingSets; });
            Add(l, "CleanSkipRecentMinutes", 17,
                delegate(AppConfig c) { c.CleanSkipRecentMinutes = 17; },
                delegate(AppConfig c) { return c.CleanSkipRecentMinutes; });
            Add(l, "CleanLogEnabled", false,
                delegate(AppConfig c) { c.CleanLogEnabled = false; },
                delegate(AppConfig c) { return c.CleanLogEnabled; });
            Add(l, "CleanExclude", "C:\\wpc-tests-excluded",
                delegate(AppConfig c) { c.CleanExclude = new List<string>(new string[] { "C:\\wpc-tests-excluded" }); },
                delegate(AppConfig c) { return Join(c.CleanExclude); });
            Add(l, "CleanUnchecked", "dev|c:\\x|*.log",
                delegate(AppConfig c) { c.CleanUnchecked = new List<string>(new string[] { "dev|c:\\x|*.log" }); },
                delegate(AppConfig c) { return Join(c.CleanUnchecked); });
            Add(l, "UpdateExclude", "Vendor.App",
                delegate(AppConfig c) { c.UpdateExclude = new List<string>(new string[] { "Vendor.App" }); },
                delegate(AppConfig c) { return Join(c.UpdateExclude); });
            Add(l, "UpdateIncludeUnknown", false,
                delegate(AppConfig c) { c.UpdateIncludeUnknown = false; },
                delegate(AppConfig c) { return c.UpdateIncludeUnknown; });
            Add(l, "UpdateUseChoco", false,
                delegate(AppConfig c) { c.UpdateUseChoco = false; },
                delegate(AppConfig c) { return c.UpdateUseChoco; });
            Add(l, "UpdateBatchSize", 11,
                delegate(AppConfig c) { c.UpdateBatchSize = 11; },
                delegate(AppConfig c) { return c.UpdateBatchSize; });
            Add(l, "DiskMinMb", 128,
                delegate(AppConfig c) { c.DiskMinMb = 128; },
                delegate(AppConfig c) { return c.DiskMinMb; });
            Add(l, "SmartBoostEnabled", true,
                delegate(AppConfig c) { c.SmartBoostEnabled = true; },
                delegate(AppConfig c) { return c.SmartBoostEnabled; });
            Add(l, "SmartBoostPercent", 77,
                delegate(AppConfig c) { c.SmartBoostPercent = 77; },
                delegate(AppConfig c) { return c.SmartBoostPercent; });
            Add(l, "UiChecks", "clean\tcat|dev\t1",
                delegate(AppConfig c) { c.UiChecks = new List<string>(new string[] { "clean\tcat|dev\t1" }); },
                delegate(AppConfig c) { return Join(c.UiChecks); });
            return l.ToArray();
        }

        private static void Add(List<Fld> l, string name, object want,
                                Action<AppConfig> set, Func<AppConfig, object> get)
        {
            Fld f = new Fld();
            f.Name = name; f.Want = want; f.Set = set; f.Get = get;
            l.Add(f);
        }

        // ---------- миграция со старой версии ----------
        // Конфиг старой сборки физически не содержит новых полей, и сериализатор оставляет их
        // умолчанием типа: bool читается как false. Без ступеней миграции апгрейд молча гасил бы
        // каждую новую возможность.
        private static void Migration()
        {
            string dir = Fx.MakeDir(Fx.Root, "cfg-old");
            AppConfig old = new AppConfig();      // всё по нулям — так выглядит старый файл
            old.ConfigVersion = 0;
            old.Theme = "dark";
            old.IdleMinutes = 3;
            old.Watchlist = new List<string>(new string[] { "node.exe" });
            old.UiChecks = new List<string>(new string[] { "stale\tkey\t1" });
            WriteRaw(Path.Combine(dir, "config.json"), old);

            Engine e = Fx.NewEngine("cfg-old");
            AppConfig c = e.Config;
            T.Check("migration from before version 1 turns background monitoring on", c.MonitorEnabled);
            T.Check("migration from before version 1 turns the clean log on", c.CleanLogEnabled);
            T.Eq("migration from before version 1 sets the default fresh-files window",
                 10, c.CleanSkipRecentMinutes);
            T.Check("migration from before version 2 turns the update sources on",
                    c.UpdateIncludeUnknown && c.UpdateUseChoco);
            T.Eq("migration from before version 3 sets the default update batch size", 5, c.UpdateBatchSize);
            T.Eq("migration from before version 4 sets the default smart boost threshold", 90, c.SmartBoostPercent);
            T.Check("migration from before version 4 leaves smart boost switched off", !c.SmartBoostEnabled);
            T.Eq("migration from before version 5 starts the selection shelf empty", 0, c.UiChecks.Count);
            T.Eq("migration keeps a value the user had set (theme)", "dark", c.Theme);
            T.Eq("migration keeps a value the user had set (idle minutes)", 3, c.IdleMinutes);
            T.Eq("migration keeps a list the user had set", "node.exe", Join(c.Watchlist));
            T.Eq("a migrated config is stamped with the current version",
                 AppConfig.CurrentVersion, c.ConfigVersion);
            // Пин: новая ступень миграции обязана приходить вместе с новым номером версии
            // и новой проверкой выше. Само по себе изменение числа — сигнал дописать тест.
            T.Eq("the config schema version is 5", 5, AppConfig.CurrentVersion);

            // Пустой список в старом файле не должен превратиться в null и уронить обход.
            T.Check("lists absent from an old config are filled in, never left null",
                    c.CleanExclude != null && c.CleanUnchecked != null && c.UpdateExclude != null
                    && c.Whitelist != null && c.DevPorts != null);
        }

        // ---------- битый файл ----------
        private static void Corrupt()
        {
            string dir = Fx.MakeDir(Fx.Root, "cfg-broken");
            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{ this is not json at all", Encoding.UTF8);
            Engine e = Fx.NewEngine("cfg-broken");
            T.Check("a corrupt config does not crash the engine", e.Config != null);
            T.Eq("a corrupt config falls back to the defaults", "system", e.Config.Theme);
            T.Check("a corrupt config is set aside instead of being overwritten",
                    File.Exists(path + ".corrupt"));

            string dir2 = Fx.MakeDir(Fx.Root, "cfg-cut");
            string path2 = Path.Combine(dir2, "config.json");
            AppConfig full = AppConfig.Default();
            full.Theme = "dark";
            WriteRaw(path2, full);
            string text = File.ReadAllText(path2, Encoding.UTF8);
            File.WriteAllText(path2, text.Substring(0, text.Length / 2), Encoding.UTF8);
            Engine e2 = Fx.NewEngine("cfg-cut");
            T.Check("a truncated config does not crash the engine", e2.Config != null);
            T.Eq("a truncated config falls back to the defaults", "system", e2.Config.Theme);
        }

        // ---------- полка выбора (UiChecks) ----------
        private static void Shelf()
        {
            Engine w = Fx.NewEngine("cfg-shelf");
            List<string> shelf = new List<string>();
            for (int i = 0; i < 4100; i++) shelf.Add("scope\tkey" + i + "\t1");
            w.Config.UiChecks = shelf;
            w.SaveConfig();

            Engine r = Fx.NewEngine("cfg-shelf");
            List<string> back = r.Config.UiChecks;
            T.Eq("the selection shelf is capped at four thousand entries", 4000, back.Count);
            T.Eq("the cap the code applies is four thousand", 4000, AppConfig.UiChecksMax);
            T.Eq("the oldest shelf entries are the ones dropped", "scope\tkey100\t1", back[0]);
            T.Eq("the newest shelf entry survives the cap", "scope\tkey4099\t1", back[back.Count - 1]);

            // Разделитель полки — табуляция: «область \t ключ \t значение». JSON её экранирует,
            // и запись обязана вернуться ровно той же строкой.
            Engine w2 = Fx.NewEngine("cfg-shelf2");
            w2.Config.UiChecks = new List<string>(new string[] {
                "clean\tcat|dev\t1", "debloat\tXbox Game Bar\t0", "disk\tmode\tfiles",
            });
            w2.SaveConfig();
            Engine r2 = Fx.NewEngine("cfg-shelf2");
            T.Eq("a shelf entry keeps its tab separators through the JSON round trip",
                 "clean\tcat|dev\t1", r2.Config.UiChecks[0]);
            T.Eq("a shelf entry with spaces in the key round-trips unchanged",
                 "debloat\tXbox Game Bar\t0", r2.Config.UiChecks[1]);
            string[] parts = r2.Config.UiChecks[2].Split('\t');
            T.Eq("a shelf entry decodes into scope, key and value", 3, parts.Length);
            T.Eq("a shelf entry keeps its value part", "files", parts[2]);
        }

        // ---------- выбор внутри категории ----------
        private static void TargetChoice()
        {
            Engine e = Fx.NewEngine("cfg-choice");
            CleanCategory c = new CleanCategory();
            c.Id = "dev";
            c.Title = "Dev";
            CleanTarget t = new CleanTarget();
            t.Path = "C:\\Wpc-Tests\\Cache";
            t.Mask = "*.LOG";

            // Ключ стабилен между запусками и не зависит от регистра, которым путь записан.
            T.Eq("a target key is the category, the path and the mask in lower case",
                 "dev|c:\\wpc-tests\\cache|*.log", Engine.TargetKey(c, t));
            T.Eq("the Recycle Bin has its own key inside a category",
                 "dev|recyclebin", Engine.BinKey(c));

            string key = Engine.TargetKey(c, t);
            T.Check("a fresh target is enabled", !e.IsTargetOff(key));
            e.SetTargetEnabled(key, false);
            T.Check("unchecking a target is remembered", e.IsTargetOff(key));
            e.SaveConfig();

            Engine r = Fx.NewEngine("cfg-choice");
            T.Check("an unchecked target stays unchecked after a restart", r.IsTargetOff(key));
            r.SetTargetEnabled(key, true);
            T.Check("re-checking a target clears the remembered choice", !r.IsTargetOff(key));
            r.SetTargetEnabled(key, true);
            T.Eq("re-checking twice does not leave a duplicate behind", 0, r.Config.CleanUnchecked.Count);
        }

        // ---------- помощники ----------
        private static void WriteRaw(string path, AppConfig c)
        {
            using (FileStream fs = File.Create(path))
            {
                DataContractJsonSerializer s = new DataContractJsonSerializer(typeof(AppConfig));
                s.WriteObject(fs, c);
            }
        }

        private static string Join(List<string> l)
        {
            return l == null ? "<null>" : string.Join("|", l.ToArray());
        }

        private static string JoinInts(List<int> l)
        {
            if (l == null) return "<null>";
            string[] s = new string[l.Count];
            for (int i = 0; i < l.Count; i++) s[i] = l[i].ToString();
            return string.Join("|", s);
        }
    }
}
