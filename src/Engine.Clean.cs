// Windows Process Cleaner — каталог категорий очистки диска (BuildCleanCategories и помощники)
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

namespace WindowsProcessCleaner
{
    public partial class Engine
    {
        // ================= ОЧИСТКА ДИСКА =================
        // Только известные мусорные пути. Никакого поиска дубликатов по диску.

        private void AddDir(CleanCategory c, string path, bool contentsOnly)
        {
            AddDir(c, path, contentsOnly, null, 0);
        }

        private void AddDir(CleanCategory c, string path, bool contentsOnly, string mask, int minAgeMinutes)
        {
            // с маской по умолчанию НЕ рекурсивно: иначе "*.dmp в %WinDir%" обойдёт
            // весь C:\Windows целиком, а это минуты
            AddDir(c, path, contentsOnly, mask, minAgeMinutes, string.IsNullOrEmpty(mask));
        }

        // mask — только файлы по маске (папка остаётся); minAge — не трогать свежие файлы.
        private void AddDir(CleanCategory c, string path, bool contentsOnly, string mask,
                            int minAgeMinutes, bool recurse)
        {
            AddDir(c, path, contentsOnly, mask, minAgeMinutes, recurse, false);
        }

        // fromRules = цель собрана из внешнего winapp2.ini. Для неё действует отдельный,
        // более строгий предохранитель (IsRuleTargetAllowed): файл правил лежит в папке,
        // доступной на запись обычному процессу, а чистим мы от администратора.
        private void AddDir(CleanCategory c, string path, bool contentsOnly, string mask,
                            int minAgeMinutes, bool recurse, bool fromRules)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!Directory.Exists(path)) return;
                string full = Path.GetFullPath(path).TrimEnd('\\');
                // одна и та же папка часто приходит двумя путями (%TEMP% и %LOCALAPPDATA%\Temp):
                // без дедупликации она обходится дважды
                foreach (CleanTarget ex in c.Targets)
                    if (string.Equals(ex.Path, full, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ex.Mask ?? "", mask ?? "", StringComparison.OrdinalIgnoreCase)) return;
                c.Targets.Add(new CleanTarget
                {
                    Path = full,
                    ContentsOnly = contentsOnly,
                    Mask = mask,
                    MinAgeMinutes = minAgeMinutes,
                    Recurse = recurse,
                    FromRules = fromRules
                });
            }
            catch { }
        }

        // Каждая подпапка *userData*\<profile> получает один и тот же набор кэшей —
        // выносим список, чтобы он не расползался по коду.
        private static readonly string[] _chromiumProfileCaches = new string[] {
            "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
            "GrShaderCache", "ShaderCache", "Media Cache", "Application Cache",
            "Service Worker\\CacheStorage", "Service Worker\\ScriptCache",
            "optimization_guide_prediction_model_downloads",
            "component_crx_cache", "extensions_crx_cache",
        };

        // Storage\ext\<id>\def — хранилища расширений и Chrome-приложений (IndexedDB, Local Storage,
        // их настройки): целиком это данные, а не кэш. Кэшем являются только их собственные подпапки.
        private static readonly string[] _chromiumExtCaches = new string[] {
            "Cache", "Code Cache", "GPUCache", "Service Worker\\CacheStorage", "Service Worker\\ScriptCache",
        };

        private void AddChromiumExtCaches(CleanCategory c, string extRoot)
        {
            string[] exts = null;
            try { if (Directory.Exists(extRoot)) exts = Directory.GetDirectories(extRoot); } catch { }
            if (exts == null) return;
            foreach (string e in exts)
                foreach (string sub in _chromiumExtCaches) AddDir(c, Path.Combine(e, "def\\" + sub), true);
        }

        private void AddChromium(CleanCategory c, string userData)
        {
            if (!Directory.Exists(userData)) return;
            // кэши уровня установки, вне профилей
            AddDir(c, Path.Combine(userData, "ShaderCache"), true);
            AddDir(c, Path.Combine(userData, "GrShaderCache"), true);
            AddDir(c, Path.Combine(userData, "GraphiteDawnCache"), true);
            AddDir(c, Path.Combine(userData, "component_crx_cache"), true);

            string[] profiles = null;
            try { profiles = Directory.GetDirectories(userData); } catch { }
            if (profiles == null) return;
            foreach (string p in profiles)
            {
                string name = Path.GetFileName(p);
                // профили — это Default, Profile 1..N, Guest Profile; служебные папки пропускаем
                bool isProfile = string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(name, "Guest Profile", StringComparison.OrdinalIgnoreCase)
                              || Directory.Exists(Path.Combine(p, "Cache"));
                if (!isProfile) continue;
                foreach (string sub in _chromiumProfileCaches) AddDir(c, Path.Combine(p, sub), true);
                AddChromiumExtCaches(c, Path.Combine(p, "Storage\\ext"));
            }
        }

        private void AddFirefox(CleanCategory c, string profilesDir)
        {
            if (!Directory.Exists(profilesDir)) return;
            string[] ps = null;
            try { ps = Directory.GetDirectories(profilesDir); } catch { }
            if (ps == null) return;
            foreach (string p in ps)
            {
                AddDir(c, Path.Combine(p, "cache2"), true);
                AddDir(c, Path.Combine(p, "startupCache"), true);
                AddDir(c, Path.Combine(p, "shader-cache"), true);
                AddDir(c, Path.Combine(p, "thumbnails"), true);
                AddDir(c, Path.Combine(p, "safebrowsing"), true);
                AddDir(c, Path.Combine(p, "minidumps"), true);
            }
        }

        // Кэш Electron-приложения (Discord/Slack/Teams/VS Code и т.п.)
        private void AddElectronCache(CleanCategory c, string dir)
        {
            if (!Directory.Exists(dir)) return;
            AddDir(c, Path.Combine(dir, "Cache"), true);
            AddDir(c, Path.Combine(dir, "Code Cache"), true);
            AddDir(c, Path.Combine(dir, "GPUCache"), true);
            AddDir(c, Path.Combine(dir, "DawnCache"), true);
            AddDir(c, Path.Combine(dir, "DawnGraphiteCache"), true);
            AddDir(c, Path.Combine(dir, "DawnWebGPUCache"), true);
            AddDir(c, Path.Combine(dir, "GrShaderCache"), true);
            AddDir(c, Path.Combine(dir, "ShaderCache"), true);
            AddDir(c, Path.Combine(dir, "Service Worker\\CacheStorage"), true);
            AddDir(c, Path.Combine(dir, "Service Worker\\ScriptCache"), true);
            AddDir(c, Path.Combine(dir, "Crashpad\\reports"), true);
            // «logs» сюда не входит: набор вызывается из категории «Кэши приложений», отмеченной по
            // умолчанию, и тихий прогон стирал бы собственные журналы каждого Electron-приложения —
            // ровно то, по чему потом разбирают, что сломалось. Журналы чистит категория «Старые логи».
        }

        public List<CleanCategory> BuildCleanCategories()
        {
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string ad = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string temp = Path.GetTempPath();
            string sysDrive = Path.GetPathRoot(_winDir);
            string pf = string.IsNullOrEmpty(_programFiles) ? Path.Combine(sysDrive, "Program Files") : _programFiles;
            List<CleanCategory> list = new List<CleanCategory>();

            // Файлы, изменённые только что, могут принадлежать идущей установке или
            // активной сессии — для temp-папок держим окно неприкосновенности.
            int fresh = Config.CleanSkipRecentMinutes;
            // Остатки обновлений Windows ($WinREAgent, $GetCurrent, ESD) сама система хранит
            // 10 дней на случай отката — повторяем этот срок, чтобы не выдернуть файлы
            // из-под ещё не завершённого обновления.
            const int updateGrace = 10 * 24 * 60;

            // Dev-кэши
            CleanCategory dev = new CleanCategory();
            dev.Id = "dev"; dev.Title = Tr.S("Dev-кэши", "Dev caches"); dev.Recommended = true;
            dev.Desc = Tr.S("npm / pnpm / yarn / bun / pip / uv / poetry / cargo / go / NuGet / Composer / TypeScript (пересоздаются)",
                            "npm / pnpm / yarn / bun / pip / uv / poetry / cargo / go / NuGet / Composer / TypeScript (regenerated)");
            AddDir(dev, Path.Combine(lad, "npm-cache"), true);
            AddDir(dev, Path.Combine(ad, "npm-cache"), true);
            AddDir(dev, Path.Combine(up, ".npm\\_cacache"), true);
            AddDir(dev, Path.Combine(lad, "Yarn\\Cache"), true);
            AddDir(dev, Path.Combine(lad, "Yarn\\berry\\cache"), true);
            AddDir(dev, Path.Combine(up, ".yarn\\cache"), true);
            AddDir(dev, Path.Combine(up, ".yarn\\berry\\cache"), true);
            AddDir(dev, Path.Combine(lad, "pnpm\\store"), true);
            AddDir(dev, Path.Combine(lad, "pnpm-store"), true);
            AddDir(dev, Path.Combine(lad, "pnpm-cache"), true);
            AddDir(dev, Path.Combine(up, ".pnpm-store"), true);
            AddDir(dev, Path.Combine(lad, "bun\\install\\cache"), true);
            AddDir(dev, Path.Combine(up, ".bun\\install\\cache"), true);
            AddDir(dev, Path.Combine(lad, "deno"), true);
            AddDir(dev, Path.Combine(lad, "node-gyp\\Cache"), true);
            AddDir(dev, Path.Combine(lad, "pip\\Cache"), true);
            AddDir(dev, Path.Combine(lad, "pip\\cache"), true);
            AddDir(dev, Path.Combine(up, ".cache\\pip"), true);
            AddDir(dev, Path.Combine(lad, "uv\\cache"), true);
            // pypoetry\Cache целиком — это ещё и virtualenvs (окружения всех Poetry-проектов);
            // кэшем являются только cache (индексы PyPI) и artifacts (скачанные wheel)
            AddDir(dev, Path.Combine(lad, "pypoetry\\Cache\\cache"), true);
            AddDir(dev, Path.Combine(lad, "pypoetry\\Cache\\artifacts"), true);
            // У cargo два склада одного и того же: cache — скачанные архивы, src — они же распакованные.
            // Убираем только распакованное: оно восстанавливается из cache без сети, и cargo build
            // --offline продолжает работать. Снести оба — значит потребовать новую загрузку с crates.io.
            AddDir(dev, Path.Combine(up, ".cargo\\registry\\src"), true);
            AddDir(dev, Path.Combine(up, "go\\pkg\\mod\\cache\\download"), true);
            AddDir(dev, Path.Combine(lad, "go-build"), true);
            AddDir(dev, Path.Combine(lad, "NuGet\\Cache"), true);
            AddDir(dev, Path.Combine(lad, "NuGet\\v3-cache"), true);
            AddDir(dev, Path.Combine(lad, "NuGet\\plugins-cache"), true);
            AddDir(dev, Path.Combine(lad, "Composer"), true);
            AddDir(dev, Path.Combine(ad, "Composer\\cache"), true);
            AddDir(dev, Path.Combine(lad, "Temp\\gradle"), true);
            // tsserver сам качает типы для автодополнения (ATA) — чистый кэш
            AddDir(dev, Path.Combine(lad, "Microsoft\\TypeScript"), true);
            AddDir(dev, Path.Combine(lad, "Microsoft\\vscode-cpptools\\ipch"), true);
            AddSubdirCaches(dev, Path.Combine(lad, "Microsoft\\VisualStudio"), "ComponentModelCache");
            if (dev.Targets.Count > 0) list.Add(dev);

            // Тяжёлые dev-загрузки: восстановимы, но качаются заново долго — не рекомендуем по умолчанию
            CleanCategory devBig = new CleanCategory();
            devBig.Id = "devbig"; devBig.Title = Tr.S("Dev: скачанные тулчейны", "Dev: downloaded toolchains");
            devBig.Desc = Tr.S("старые сборки браузеров Playwright (текущая остаётся), браузеры Puppeteer/Cypress, кэш electron-builder, dotslash, Expo Go, кэш и дистрибутивы Gradle, репозиторий Maven, пакеты NuGet — скачаются заново",
                               "old Playwright browser builds (the current one stays), Puppeteer/Cypress browsers, electron-builder cache, dotslash, Expo Go, Gradle caches and distributions, Maven repository, NuGet packages — will re-download");
            // Playwright держит по папке на КАЖДУЮ скачанную сборку браузера (chromium-1223,
            // chromium-1228, …). Удаляются только старые: текущую используют проекты, а проект,
            // закреплённый на прошлой версии, после её удаления требует npx playwright install —
            // поэтому здесь, без галочки по умолчанию, а не в рекомендованных Dev-кэшах.
            AddOldVersionsByPrefix(devBig, Path.Combine(lad, "ms-playwright"), 1, null);
            AddOldVersionsByPrefix(devBig, Path.Combine(lad, "ms-playwright-mcp"), 1, null);
            AddDir(devBig, Path.Combine(lad, "puppeteer"), true);
            AddDir(devBig, Path.Combine(up, ".cache\\puppeteer"), true);
            AddDir(devBig, Path.Combine(lad, "Cypress\\Cache"), true);
            AddDir(devBig, Path.Combine(lad, "electron"), true);
            AddDir(devBig, Path.Combine(lad, "electron-builder\\Cache"), true);
            AddDir(devBig, Path.Combine(lad, "dotslash"), true);
            AddDir(devBig, Path.Combine(up, ".expo\\android-apk-cache"), true);
            AddDir(devBig, Path.Combine(up, ".expo\\expo-go"), true);
            AddDir(devBig, Path.Combine(up, ".gradle\\wrapper\\dists"), true);
            // ~/.gradle/caches — тот же класс, что репозиторий Maven: все зависимости всех
            // проектов, гигабайты и минуты повторной загрузки; в рекомендованных ему не место
            AddDir(devBig, Path.Combine(up, ".gradle\\caches"), true);
            AddDir(devBig, Path.Combine(up, ".m2\\repository"), true);
            // глобальный кэш пакетов NuGet — тот же класс, что репозиторий Maven: скачается заново,
            // но на больших решениях это гигабайты и минуты restore
            AddDir(devBig, Path.Combine(up, ".nuget\\packages"), true);
            if (devBig.Targets.Count > 0) list.Add(devBig);

            // Системный мусор
            CleanCategory sys = new CleanCategory();
            sys.Id = "sys"; sys.Title = Tr.S("Системный мусор", "System junk"); sys.Recommended = true; sys.RecycleBin = true;
            sys.Desc = Tr.S("temp (включая temp служебных профилей), Корзина, кэш Windows Update, дампы падений, отчёты об ошибках, Delivery Optimization",
                            "temp (incl. service-profile temp), Recycle Bin, Windows Update cache, crash dumps, error reports, Delivery Optimization");
            AddDir(sys, temp, true, null, fresh);
            AddDir(sys, Path.Combine(lad, "Temp"), true, null, fresh);
            AddDir(sys, Path.Combine(_winDir, "Temp"), true, null, fresh);
            // temp служебных учёток: туда пишут установщики и службы, руками их никто не чистит
            AddDir(sys, Path.Combine(_winDir, "ServiceProfiles\\LocalService\\AppData\\Local\\Temp"), true, null, fresh);
            AddDir(sys, Path.Combine(_winDir, "ServiceProfiles\\NetworkService\\AppData\\Local\\Temp"), true, null, fresh);
            AddDir(sys, Path.Combine(_winDir, "System32\\config\\systemprofile\\AppData\\Local\\Temp"), true, null, fresh);
            AddDir(sys, Path.Combine(_winDir, "System32\\config\\systemprofile\\AppData\\Local\\Microsoft\\Windows\\INetCache"), true);
            AddDir(sys, Path.Combine(_winDir, "SysWOW64\\config\\systemprofile\\AppData\\Local\\Microsoft\\Windows\\INetCache"), true);
            AddDir(sys, Path.Combine(lad, "SquirrelTemp"), true, null, fresh);
            // окно свежести и здесь: файл, который Центр обновления докачивает прямо сейчас, — не мусор
            AddDir(sys, Path.Combine(_winDir, "SoftwareDistribution\\Download"), true, null, fresh);
            AddDir(sys, Path.Combine(_winDir, "ServiceProfiles\\NetworkService\\AppData\\Local\\Microsoft\\Windows\\DeliveryOptimization\\Cache"), true);
            AddDir(sys, Path.Combine(lad, "CrashDumps"), true);
            AddDir(sys, Path.Combine(lad, "Microsoft\\Windows\\WER"), true);
            AddDir(sys, Path.Combine(pd, "Microsoft\\Windows\\WER"), true);
            AddDir(sys, Path.Combine(_winDir, "LiveKernelReports"), true);
            AddDir(sys, Path.Combine(_winDir, "Minidump"), true);
            // Prefetch намеренно НЕ чистится: это кэш загрузки программ, он весит мегабайты,
            // а после удаления каждая программа стартует медленнее, пока Windows не соберёт
            // его заново. Освободить нечего, потерять есть что.
            AddDir(sys, Path.Combine(_winDir, "Panther"), true);
            // MEMORY.DMP (полный дамп ядра, гигабайты) попадает под ту же маску — отдельная цель
            // считала бы его дважды
            AddDir(sys, _winDir, true, "*.dmp", 0);
            list.Add(sys);

            // Кэши отрисовки/эскизов Windows — восстанавливаются автоматически
            CleanCategory shell = new CleanCategory();
            shell.Id = "shell"; shell.Title = Tr.S("Кэши Windows (эскизы, иконки, шрифты)", "Windows caches (thumbnails, icons, fonts)");
            shell.Recommended = true;
            shell.Desc = Tr.S("thumbcache/iconcache, кэш шрифтов, картинки уведомлений, кэш RDP — Windows пересоберёт сама",
                              "thumbcache/iconcache, font cache, notification images, RDP cache — Windows rebuilds them");
            string explorerDir = Path.Combine(lad, "Microsoft\\Windows\\Explorer");
            AddDir(shell, explorerDir, true, "thumbcache_*.db", 0);
            AddDir(shell, explorerDir, true, "iconcache_*.db", 0);
            AddDir(shell, Path.Combine(explorerDir, "ThumbCacheToDelete"), true);
            AddDir(shell, lad, true, "IconCache.db", 0);
            // INetCache — ещё и Content.Outlook / Content.Word: вложение, открытое прямо из письма,
            // лежит здесь, пока его редактируют, — свежие файлы под защитой, как в temp
            AddDir(shell, Path.Combine(lad, "Microsoft\\Windows\\INetCache"), true, null, fresh);
            AddDir(shell, Path.Combine(lad, "Microsoft\\Windows\\Notifications\\wpnidm"), true);
            AddDir(shell, Path.Combine(lad, "Microsoft\\Terminal Server Client\\Cache"), true);
            AddDir(shell, Path.Combine(_winDir, "ServiceProfiles\\LocalService\\AppData\\Local\\FontCache"), true);
            if (shell.Targets.Count > 0) list.Add(shell);

            // Кэши шейдеров GPU — не «мусор»: после удаления игры, Chrome и Electron-приложения
            // компилируют шейдеры заново, и первые минуты идут с подтормаживаниями, а места
            // освобождается мало. Поэтому отдельная категория и по умолчанию не отмечена (06.09.2026).
            string steam = SteamPath();
            CleanCategory shaders = new CleanCategory();
            shaders.Id = "shaders"; shaders.Title = Tr.S("Кэши шейдеров GPU", "GPU shader caches");
            shaders.Recommended = false;
            shaders.Desc = Tr.S("DirectX (D3DSCache), NVIDIA, AMD, Intel и shadercache Steam — после очистки игры и Chrome/Electron компилируют шейдеры заново (подтормаживания в первые минуты), места даёт мало",
                                "DirectX (D3DSCache), NVIDIA, AMD, Intel and Steam shadercache — after cleaning, games and Chrome/Electron recompile shaders (stutter for the first minutes), frees little");
            AddDir(shaders, Path.Combine(lad, "D3DSCache"), true);
            AddDir(shaders, Path.Combine(lad, "NVIDIA\\DXCache"), true);
            AddDir(shaders, Path.Combine(lad, "NVIDIA\\GLCache"), true);
            AddDir(shaders, Path.Combine(lad, "NVIDIA\\ComputeCache"), true);
            AddDir(shaders, Path.Combine(lad, "NVIDIA\\OptixCache"), true);
            AddDir(shaders, Path.Combine(lad, "NVIDIA\\PerDriverVersion\\DXCache"), true);
            AddDir(shaders, Path.Combine(lad, "NVIDIA\\PerDriverVersion\\GLCache"), true);
            AddDir(shaders, Path.Combine(lad, "AMD\\DxCache"), true);
            AddDir(shaders, Path.Combine(lad, "AMD\\DxcCache"), true);
            AddDir(shaders, Path.Combine(lad, "AMD\\GLCache"), true);
            AddDir(shaders, Path.Combine(lad, "AMD\\VkCache"), true);
            AddDir(shaders, Path.Combine(lad, "Intel\\ShaderCache"), true);
            if (steam != null) AddDir(shaders, Path.Combine(steam, "steamapps\\shadercache"), true);
            if (shaders.Targets.Count > 0) list.Add(shaders);

            // Кэши приложений из Microsoft Store (UWP): у каждого пакета свои INetCache/Temp/TempState
            CleanCategory store = new CleanCategory();
            store.Id = "store"; store.Title = Tr.S("Кэши приложений Microsoft Store", "Microsoft Store app caches");
            store.Recommended = true;
            store.Desc = Tr.S("INetCache / Temp / TempState пакетов UWP, WebView-кэш нового Teams — данные и настройки приложений не трогаются",
                              "INetCache / Temp / TempState of UWP packages, WebView cache of the new Teams — app data and settings untouched");
            AddStoreAppCaches(store, Path.Combine(lad, "Packages"));
            if (store.Targets.Count > 0) list.Add(store);

            // Кэши браузеров
            CleanCategory br = new CleanCategory();
            br.Id = "browser"; br.Title = Tr.S("Кэши браузеров", "Browser caches");
            br.Desc = Tr.S("Chrome / Edge / Brave / Yandex / Opera / Vivaldi / Chromium / Firefox / LibreWolf / Waterfox / Thunderbird — только кэш (пароли, куки и история не трогаются)",
                           "Chrome / Edge / Brave / Yandex / Opera / Vivaldi / Chromium / Firefox / LibreWolf / Waterfox / Thunderbird — cache only (passwords, cookies, history untouched)");
            AddChromium(br, Path.Combine(lad, "Google\\Chrome\\User Data"));
            AddChromium(br, Path.Combine(lad, "Google\\Chrome Beta\\User Data"));
            AddChromium(br, Path.Combine(lad, "Google\\Chrome Dev\\User Data"));
            AddChromium(br, Path.Combine(lad, "Google\\Chrome SxS\\User Data"));
            AddChromium(br, Path.Combine(lad, "Microsoft\\Edge\\User Data"));
            AddChromium(br, Path.Combine(lad, "Microsoft\\Edge Beta\\User Data"));
            AddChromium(br, Path.Combine(lad, "Microsoft\\Edge Dev\\User Data"));
            AddChromium(br, Path.Combine(lad, "Microsoft\\Edge SxS\\User Data"));
            AddChromium(br, Path.Combine(lad, "Chromium\\User Data"));
            AddChromium(br, Path.Combine(lad, "BraveSoftware\\Brave-Browser\\User Data"));
            AddChromium(br, Path.Combine(lad, "BraveSoftware\\Brave-Browser-Beta\\User Data"));
            AddChromium(br, Path.Combine(lad, "BraveSoftware\\Brave-Browser-Nightly\\User Data"));
            AddChromium(br, Path.Combine(lad, "Yandex\\YandexBrowser\\User Data"));
            AddChromium(br, Path.Combine(lad, "Vivaldi\\User Data"));
            AddChromium(br, Path.Combine(ad, "Opera Software\\Opera Stable"));
            AddChromium(br, Path.Combine(ad, "Opera Software\\Opera GX Stable"));
            AddFirefox(br, Path.Combine(lad, "Mozilla\\Firefox\\Profiles"));
            AddFirefox(br, Path.Combine(ad, "Mozilla\\Firefox\\Profiles"));
            // те же Gecko-профили у форков и у Thunderbird (cache2 / startupCache в Local)
            AddFirefox(br, Path.Combine(lad, "librewolf\\Profiles"));
            AddFirefox(br, Path.Combine(ad, "librewolf\\Profiles"));
            AddFirefox(br, Path.Combine(lad, "Waterfox\\Profiles"));
            AddFirefox(br, Path.Combine(ad, "Waterfox\\Profiles"));
            AddFirefox(br, Path.Combine(lad, "Thunderbird\\Profiles"));
            AddFirefox(br, Path.Combine(ad, "Thunderbird\\Profiles"));
            if (br.Targets.Count > 0) list.Add(br);

            // Кэши приложений (Electron/медиа/IDE)
            CleanCategory apps = new CleanCategory();
            apps.Id = "appcache"; apps.Title = Tr.S("Кэши приложений", "App caches");
            apps.Recommended = true;
            apps.Desc = Tr.S("Discord / Slack / Teams / Spotify / VS Code / JetBrains / Steam / Telegram / Figma / игровые лаунчеры — только кэш",
                             "Discord / Slack / Teams / Spotify / VS Code / JetBrains / Steam / Telegram / Figma / game launchers — cache only");
            AddElectronCache(apps, Path.Combine(ad, "discord"));
            AddElectronCache(apps, Path.Combine(ad, "discordptb"));
            AddElectronCache(apps, Path.Combine(ad, "discordcanary"));
            AddElectronCache(apps, Path.Combine(ad, "Slack"));
            AddElectronCache(apps, Path.Combine(ad, "Microsoft\\Teams"));
            AddElectronCache(apps, Path.Combine(lad, "Microsoft\\Teams"));
            AddElectronCache(apps, Path.Combine(ad, "Code"));
            AddElectronCache(apps, Path.Combine(ad, "Cursor"));
            AddElectronCache(apps, Path.Combine(ad, "Postman"));
            AddElectronCache(apps, Path.Combine(ad, "Figma"));
            // Figma держит Chromium-профиль не в корне, а в DesktopProfile\v<N>
            AddSubdirCaches(apps, Path.Combine(ad, "Figma\\DesktopProfile"), null);
            AddElectronCache(apps, Path.Combine(ad, "Notion"));
            AddElectronCache(apps, Path.Combine(ad, "obsidian"));
            AddDir(apps, Path.Combine(ad, "Code\\CachedData"), true);
            AddDir(apps, Path.Combine(ad, "Code\\CachedExtensionVSIXs"), true);
            AddDir(apps, Path.Combine(ad, "Cursor\\CachedData"), true);
            AddDir(apps, Path.Combine(lad, "Spotify\\Browser"), true);
            AddDir(apps, Path.Combine(lad, "Steam\\htmlcache"), true);
            AddDir(apps, Path.Combine(ad, "Telegram Desktop\\tdata\\user_data\\cache"), true);
            AddDir(apps, Path.Combine(ad, "Telegram Desktop\\tdata\\user_data\\media_cache"), true);
            AddDir(apps, Path.Combine(ad, "Telegram Desktop\\tdata\\emoji"), true);
            // Premiere/After Effects держат медиакэш в ROAMING (`%APPDATA%\Adobe\Common`);
            // Local-путь оставлен для старых версий
            AddDir(apps, Path.Combine(lad, "Adobe\\Common\\Media Cache Files"), true);
            AddDir(apps, Path.Combine(ad, "Adobe\\Common\\Media Cache Files"), true);
            AddDir(apps, Path.Combine(ad, "Adobe\\Common\\Media Cache"), true);
            AddDir(apps, Path.Combine(ad, "Adobe\\Common\\Peak Files"), true);
            AddDir(apps, Path.Combine(lad, "Unity\\cache"), true);
            AddDir(apps, Path.Combine(lad, "GameCenter\\Cache"), true);
            AddDir(apps, Path.Combine(lad, "EpicGamesLauncher\\Saved\\webcache"), true);
            AddDir(apps, Path.Combine(lad, "Battle.net\\Cache"), true);
            AddDir(apps, Path.Combine(pd, "Battle.net\\Agent\\data\\cache"), true);
            AddDir(apps, Path.Combine(lad, "Ubisoft Game Launcher\\cache"), true);
            AddDir(apps, Path.Combine(lad, "GOG.com\\Galaxy\\webcache"), true);
            AddJetBrains(apps, Path.Combine(lad, "JetBrains"));
            AddSteamCaches(apps, steam);
            if (apps.Targets.Count > 0) list.Add(apps);

            // Кэши, в которых живёт офлайн-контент: для одних это мусор, для других — скачанная
            // музыка. Поэтому отдельная категория и без галочки по умолчанию.
            CleanCategory offline = new CleanCategory();
            offline.Id = "offline"; offline.Title = Tr.S("Кэши с офлайн-контентом", "Caches holding offline content");
            offline.Desc = Tr.S("Spotify Storage/Data: потоковый кэш вместе с офлайн-загрузками Premium — после удаления треки скачаются заново",
                                "Spotify Storage/Data: the streaming cache together with Premium offline downloads — tracks re-download afterwards");
            AddDir(offline, Path.Combine(lad, "Spotify\\Storage"), true);
            AddDir(offline, Path.Combine(lad, "Spotify\\Data"), true);
            if (offline.Targets.Count > 0) list.Add(offline);

            // NVIDIA: то, что копится от обновлений драйвера и NVIDIA App
            CleanCategory nv = new CleanCategory();
            nv.Id = "nvidia"; nv.Title = Tr.S("NVIDIA: кэши и старые версии", "NVIDIA: caches and old versions");
            nv.Recommended = true;
            nv.Desc = Tr.S("старые версии моделей NGX (DLSS, Broadcast…), кэш NVIDIA App/Overlay, распаковка драйвера в C:\\NVIDIA — текущие версии, сам драйвер и подготовленные к установке пакеты не трогаются",
                           "old NGX model versions (DLSS, Broadcast…), NVIDIA App/Overlay cache, the driver unpack folder C:\\NVIDIA — current versions, the driver itself and staged installer packages untouched");
            // NGX Updater докачивает новые версии DLSS/Broadcast-моделей в
            // ProgramData\NVIDIA\NGX\models\<модель>\versions\<N> и НИКОГДА не удаляет старые;
            // за год набегают гигабайты. Игры и NVIDIA App берут только самую новую.
            AddNgxOldVersions(nv, Path.Combine(pd, "NVIDIA\\NGX\\models"));
            // Единственная цель категории в %ProgramData%, про которую проверено, что NGX Updater
            // скачает её заново: остальным целям в общесистемных деревьях движок отдаёт только
            // журналы и временные файлы (см. Engine.DiskWork, «общесистемные деревья приложений»).
            foreach (CleanTarget ngx in nv.Targets) ngx.Disposable = true;
            AddDir(nv, Path.Combine(lad, "NVIDIA Corporation\\NVIDIA App\\CefCache"), true);
            AddDir(nv, Path.Combine(lad, "NVIDIA Corporation\\NVIDIA Overlay\\CefCache"), true);
            AddDir(nv, Path.Combine(lad, "NVIDIA Corporation\\GeForce Experience\\CefCache"), true);
            // NvBackend\ApplicationOntology намеренно НЕ трогаем: это не кэш, а база распознавания
            // игр NVIDIA App (ontology.json + детекторы). После её удаления 06.09.2026 бэкенд писал
            // «LoadApplicationDetectors failed» на каждый новый процесс, пока не скачал базу заново;
            // путь дополнительно закрыт в IsAllowedTarget.
            AddDir(nv, Path.Combine(lad, "NVIDIA Corporation\\NV_Cache"), true);
            AddDir(nv, Path.Combine(pd, "NVIDIA Corporation\\NVIDIA App\\Logs"), true);
            AddDir(nv, Path.Combine(pd, "NVIDIA Corporation\\NVIDIA Broadcast\\temp"), true, null, fresh);
            // Downloader и NetService переехали в «Остатки обновлений» (по явному выбору): это
            // подготовленные к установке пакеты драйвера, а не кэш, и по умолчанию их не трогаем.
            // C:\NVIDIA — распаковка уже установленного драйвера: содержимое убираем, саму папку
            // оставляем, и только спустя срок отката обновления.
            AddDir(nv, Path.Combine(sysDrive, "NVIDIA"), true, null, updateGrace);
            if (nv.Targets.Count > 0) list.Add(nv);

            // Старые логи
            CleanCategory logs = new CleanCategory();
            logs.Id = "logs"; logs.Title = Tr.S("Старые логи", "Old logs");
            logs.Desc = Tr.S("логи CBS/DISM/установки Windows, Update Orchestrator, npm/yarn/gradle, Docker Desktop, OneDrive, Zoom, Chocolatey",
                             "CBS/DISM/Windows setup logs, Update Orchestrator, npm/yarn/gradle, Docker Desktop, OneDrive, Zoom, Chocolatey");
            AddDir(logs, Path.Combine(_winDir, "Logs\\CBS"), true);
            AddDir(logs, Path.Combine(_winDir, "Logs\\DISM"), true);
            AddDir(logs, Path.Combine(_winDir, "Logs\\MoSetup"), true);
            AddDir(logs, Path.Combine(_winDir, "Logs\\WindowsUpdate"), true);
            AddDir(logs, Path.Combine(_winDir, "Logs\\SIH"), true);
            AddDir(logs, Path.Combine(_winDir, "Logs\\MeasuredBoot"), true);
            AddDir(logs, Path.Combine(_winDir, "Logs\\NetSetup"), true);
            AddDir(logs, Path.Combine(pd, "USOShared\\Logs"), true);
            AddDir(logs, Path.Combine(pd, "chocolatey\\logs"), true);
            AddDir(logs, Path.Combine(lad, "Microsoft\\CLR_v4.0\\UsageLogs"), true);
            AddDir(logs, Path.Combine(lad, "Microsoft\\OneDrive\\logs"), true);
            AddDir(logs, Path.Combine(lad, "npm-cache\\_logs"), true);
            AddDir(logs, Path.Combine(up, ".npm\\_logs"), true);
            AddDir(logs, Path.Combine(lad, "Yarn\\logs"), true);
            // ~/.gradle/daemon/<версия>/: рядом с daemon-*.out.log лежит registry.bin работающих
            // демонов — удалить его значит осиротить их; берём только логи
            AddDir(logs, Path.Combine(up, ".gradle\\daemon"), true, "*.log", 0, true);
            AddDir(logs, Path.Combine(ad, "Docker Desktop\\log"), true);
            AddDir(logs, Path.Combine(lad, "Docker\\log"), true);
            AddDir(logs, Path.Combine(ad, "Zoom\\logs"), true);
            // Журналы VS Code и Cursor: раньше они уезжали вместе с кэшем в отмеченной по умолчанию
            // категории — теперь только здесь, по явному выбору.
            AddDir(logs, Path.Combine(ad, "Code\\logs"), true);
            AddDir(logs, Path.Combine(ad, "Cursor\\logs"), true);
            if (logs.Targets.Count > 0) list.Add(logs);

            // Следы недавних файлов (приватность) — то, что FluentCleaner называет "recently opened"
            CleanCategory recent = new CleanCategory();
            recent.Id = "recent"; recent.Title = Tr.S("Списки недавних файлов", "Recent file lists");
            recent.Desc = Tr.S("«Недавние документы», списки переходов проводника и Office (сами файлы не трогаются; закреплённое в «Быстром доступе» и в списках переходов придётся закрепить заново)",
                               "Recent documents, Explorer/Office jump lists (the files themselves are untouched; items pinned to Quick Access and jump lists must be pinned again)");
            AddDir(recent, Path.Combine(ad, "Microsoft\\Windows\\Recent"), true, "*.lnk", 0);
            AddDir(recent, Path.Combine(ad, "Microsoft\\Windows\\Recent\\AutomaticDestinations"), true);
            AddDir(recent, Path.Combine(ad, "Microsoft\\Windows\\Recent\\CustomDestinations"), true);
            AddDir(recent, Path.Combine(ad, "Microsoft\\Office\\Recent"), true);
            if (recent.Targets.Count > 0) list.Add(recent);

            // Предыдущие версии программ, оставленные автообновлением
            CleanCategory oldver = new CleanCategory();
            oldver.Id = "oldver"; oldver.Title = Tr.S("Старые версии программ", "Old program versions");
            oldver.Desc = Tr.S("копии предыдущих версий после автообновления (Squirrel: Postman, Figma, Discord…; WPS Office) — текущая версия остаётся",
                               "previous-version copies left by auto-update (Squirrel: Postman, Figma, Discord…; WPS Office) — the current version stays");
            AddSquirrelOldVersions(oldver, lad);
            AddWpsOldVersions(oldver, Path.Combine(lad, "Kingsoft\\WPS Office"));
            if (oldver.Targets.Count > 0) list.Add(oldver);

            // Остатки обновлений Windows и установщиков драйверов
            CleanCategory drv = new CleanCategory();
            drv.Id = "drivers"; drv.Title = Tr.S("Остатки обновлений Windows и драйверов", "Windows update and driver leftovers");
            drv.Desc = Tr.S("Windows.old, $WinREAgent, $SysReset (следы прошлого сброса системы), ESD, распакованные установщики AMD/Intel, загрузчик NVIDIA (DriverStore, Installer2 и кэш MSI-патчей не трогаются: из них система чинит уже установленное)",
                            "Windows.old, $WinREAgent, $SysReset (traces of a past system reset), ESD, unpacked AMD/Intel installers, the NVIDIA downloader (DriverStore, Installer2 and the MSI patch cache untouched: they are what repairs already-installed software)");
            // Installer2 (Program Files и ProgramData) больше не цель: это дистрибутив драйвера, из
            // которого NVIDIA чинит и удаляет уже установленное, — тот же класс, что LGHUB\cache.
            AddDir(drv, Path.Combine(pd, "NVIDIA Corporation\\Downloader"), true, null, updateGrace);
            AddDir(drv, Path.Combine(pd, "NVIDIA Corporation\\NetService"), true, null, updateGrace);
            // Срок неприкосновенности здесь по той же причине, что у остатков обновления: пока установка
            // драйвера идёт, распакованный дистрибутив ей нужен — без отсрочки очистка выпотрошила бы её.
            AddDir(drv, Path.Combine(sysDrive, "AMD"), false, null, updateGrace);
            AddDir(drv, Path.Combine(sysDrive, "Intel"), false, null, updateGrace);
            // Windows.old — то, откуда система откатывается первые 10 дней после обновления:
            // тот же срок неприкосновенности, что у остальных остатков обновления
            AddDir(drv, Path.Combine(sysDrive, "Windows.old"), false, null, updateGrace);
            // $Windows.~BT / ~WS — распаковка идущего обновления компонентов: срок тот же, что у $WinREAgent
            AddDir(drv, Path.Combine(sysDrive, "$Windows.~BT"), false, null, updateGrace);
            AddDir(drv, Path.Combine(sysDrive, "$Windows.~WS"), false, null, updateGrace);
            // %WinDir%\Installer\$PatchCache$ больше не цель ни по какому выбору: это базовые копии
            // файлов для MSI-патчей, без них восстановление и удаление патчей Office, SQL Server и
            // Visual Studio просят исходный установщик, которого у пользователя обычно уже нет.
            AddDir(drv, Path.Combine(sysDrive, "$WinREAgent"), false, null, updateGrace);
            AddDir(drv, Path.Combine(sysDrive, "$GetCurrent"), false, null, updateGrace);
            AddDir(drv, Path.Combine(sysDrive, "$SysReset"), false, null, updateGrace);
            AddDir(drv, Path.Combine(sysDrive, "ESD"), false, null, updateGrace);
            // Эти папки принадлежат TrustedInstaller: без смены владельца DeleteFileW отвечает
            // «отказано в доступе», и категория показывала гигабайты, освобождая ноль.
            foreach (CleanTarget t in drv.Targets)
            {
                string name = Path.GetFileName(t.Path);
                if (name.Equals("Windows.old", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("$Windows.~BT", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("$Windows.~WS", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("$WinREAgent", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("$GetCurrent", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("$SysReset", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("ESD", StringComparison.OrdinalIgnoreCase))
                    t.TakeOwnership = true;
            }
            if (drv.Targets.Count > 0) list.Add(drv);

            // Категории-действия: считаются и чистятся внешними утилитами Windows
            CleanCategory ds = new CleanCategory();
            ds.Id = "driverstore"; ds.Kind = "driverstore";
            ds.Title = Tr.S("Старые пакеты драйверов (DriverStore)", "Old driver packages (DriverStore)");
            ds.Desc = Tr.S("версии, которые заменены более новыми и не привязаны ни к одному устройству — удаляются через pnputil, текущие драйверы не трогаются",
                           "versions superseded by newer ones and bound to no device — removed via pnputil, current drivers untouched");
            list.Add(ds);

            CleanCategory sxs = new CleanCategory();
            sxs.Id = "winsxs"; sxs.Kind = "winsxs";
            sxs.Title = Tr.S("Хранилище компонентов Windows (WinSxS)", "Windows component store (WinSxS)");
            sxs.Desc = Tr.S("устаревшие версии компонентов после обновлений — DISM /StartComponentCleanup; после этого уже заменённые обновления нельзя откатить",
                            "superseded component versions left by updates — DISM /StartComponentCleanup; superseded updates can no longer be rolled back afterwards");
            // DISM работает минутами: пока строка ждёт, пользователь должен видеть, почему
            sxs.Note = Tr.S("считает DISM, это может занять несколько минут", "DISM is measuring, this can take a few minutes");
            list.Add(sxs);

            // Правила из winapp2.ini, если база положена рядом (формат FluentCleaner/BleachBit)
            try { list.AddRange(LoadWinapp2Categories()); } catch { }

            AddPopularAppTargets(list, lad, ad, up, pd);
            ApplyTargetChoice(list);
            return list;
        }

        // Кэши JetBrains-IDE: подпапки вида ...\JetBrains\IntelliJIdea2024.1\{caches,log,tmp}
        private void AddJetBrains(CleanCategory c, string jbRoot)
        {
            if (!Directory.Exists(jbRoot)) return;
            string[] ides = null;
            try { ides = Directory.GetDirectories(jbRoot); } catch { return; }
            if (ides == null) return;
            foreach (string ide in ides)
            {
                AddDir(c, Path.Combine(ide, "caches"), true);
                AddDir(c, Path.Combine(ide, "log"), true);
                AddDir(c, Path.Combine(ide, "tmp"), true);
            }
        }

        // Одна и та же подпапка-кэш в каждом ребёнке parent: VisualStudio\<ver>\ComponentModelCache,
        // Figma\DesktopProfile\v<N>\{Cache,…}. sub == null — ребёнок сам Chromium-профиль.
        private void AddSubdirCaches(CleanCategory c, string parent, string sub)
        {
            string[] kids;
            try { if (!Directory.Exists(parent)) return; kids = Directory.GetDirectories(parent); }
            catch { return; }
            foreach (string k in kids)
            {
                if (sub == null) AddElectronCache(c, k);
                else AddDir(c, Path.Combine(k, sub), true);
            }
        }

        // ---------- «Оставить только самую новую версию» ----------
        // Ключ версии — числовые куски имени по порядку: "1.0.9249" -> 1,0,9249; "20318464".
        // Имя с буквами (хеш сборки) ключа не даёт — такие папки сравниваются по дате.
        private static List<long> VersionKey(string ver)
        {
            List<long> key = new List<long>();
            if (string.IsNullOrEmpty(ver)) return key;
            foreach (char ch in ver)
                if (!char.IsDigit(ch) && ch != '.' && ch != '_') return key;
            foreach (string part in ver.Split('.', '_'))
            {
                if (part.Length == 0) continue;
                long v = 0;
                foreach (char ch in part) { if (v < long.MaxValue / 20) v = v * 10 + (ch - '0'); }
                key.Add(v);
            }
            return key;
        }

        private class VerDir
        {
            public DirectoryInfo Dir;
            public string Ver;
            public List<long> Key;
            public DateTime Time;
        }

        private static VerDir MakeVerDir(DirectoryInfo d, string ver)
        {
            VerDir v = new VerDir();
            v.Dir = d; v.Ver = ver; v.Key = VersionKey(ver);
            try { v.Time = d.LastWriteTimeUtc; } catch { v.Time = DateTime.MinValue; }
            return v;
        }

        // Новее — первым. Числовой ключ бьёт дату: папка, которую недавно ТРОГАЛИ, не обязана
        // быть самой новой версией. Без ключа (хеши) остаётся только дата.
        private static int CompareNewestFirst(VerDir a, VerDir b)
        {
            int n = Math.Min(a.Key.Count, b.Key.Count);
            for (int i = 0; i < n; i++)
                if (a.Key[i] != b.Key[i]) return b.Key[i].CompareTo(a.Key[i]);
            if (a.Key.Count != b.Key.Count) return b.Key.Count.CompareTo(a.Key.Count);
            return b.Time.CompareTo(a.Time);
        }

        // Все подпапки parent — версии одной сущности (NGX: models\dlss\versions\<N>).
        // Оставляем keep самых новых, остальные — целиком под удаление.
        private void AddOldVersions(CleanCategory c, string parent, int keep)
        {
            DirectoryInfo[] kids;
            try { if (!Directory.Exists(parent)) return; kids = new DirectoryInfo(parent).GetDirectories(); }
            catch { return; }
            List<VerDir> vers = new List<VerDir>();
            foreach (DirectoryInfo d in kids)
            {
                if (IsDotName(d.Name)) continue;
                vers.Add(MakeVerDir(d, d.Name));
            }
            if (vers.Count <= keep) return;
            vers.Sort(CompareNewestFirst);
            for (int i = keep; i < vers.Count; i++) AddDir(c, vers[i].Dir.FullName, false);
        }

        // Подпапки вида <имя>-<версия> (chromium-1234, app-1.0.9249, mcp-chrome-8192e92):
        // группируем по <имя>, в каждой группе оставляем keep самых новых.
        // onlyPrefix != null — рассматриваем только папки с таким началом имени.
        private void AddOldVersionsByPrefix(CleanCategory c, string parent, int keep, string onlyPrefix)
        {
            DirectoryInfo[] kids;
            try { if (!Directory.Exists(parent)) return; kids = new DirectoryInfo(parent).GetDirectories(); }
            catch { return; }
            Dictionary<string, List<VerDir>> groups = new Dictionary<string, List<VerDir>>(StringComparer.OrdinalIgnoreCase);
            foreach (DirectoryInfo d in kids)
            {
                string name = d.Name;
                if (onlyPrefix != null && !name.StartsWith(onlyPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                int dash = name.LastIndexOf('-');
                if (dash <= 0 || dash == name.Length - 1) continue;
                string group = name.Substring(0, dash);
                List<VerDir> g;
                if (!groups.TryGetValue(group, out g)) { g = new List<VerDir>(); groups[group] = g; }
                g.Add(MakeVerDir(d, name.Substring(dash + 1)));
            }
            foreach (List<VerDir> g in groups.Values)
            {
                if (g.Count <= keep) continue;
                g.Sort(CompareNewestFirst);
                for (int i = keep; i < g.Count; i++) AddDir(c, g[i].Dir.FullName, false);
            }
        }

        // NGX Updater: ProgramData\NVIDIA\NGX\models\<модель>\versions\<N>. Актуальна самая
        // новая версия каждой модели, старые NVIDIA не подчищает никогда.
        private void AddNgxOldVersions(CleanCategory c, string models)
        {
            string[] kids;
            try { if (!Directory.Exists(models)) return; kids = Directory.GetDirectories(models); }
            catch { return; }
            foreach (string m in kids) AddOldVersions(c, Path.Combine(m, "versions"), 1);
        }

        // Squirrel (Discord, Postman, Figma, Slack, GitHub Desktop…): %LOCALAPPDATA%\<App>\Update.exe
        // плюс app-<версия> на каждую скачанную версию; запускается самая новая, предыдущая
        // остаётся «на всякий случай» и весит столько же, сколько сама программа.
        private void AddSquirrelOldVersions(CleanCategory c, string lad)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(lad); } catch { return; }
            foreach (string d in dirs)
            {
                bool squirrel = false;
                try { squirrel = File.Exists(Path.Combine(d, "Update.exe")); } catch { }
                if (!squirrel) continue;
                AddOldVersionsByPrefix(c, d, 1, "app-");
            }
        }

        // WPS Office ставится в %LOCALAPPDATA%\Kingsoft\WPS Office\<версия> и после обновления
        // оставляет предыдущую папку (~1,5 ГБ). Текущую версию берём из реестра
        // (InstallRoot), а не «самую новую по номеру»: если обновление не доустановилось,
        // рабочей может быть и старая.
        private void AddWpsOldVersions(CleanCategory c, string root)
        {
            DirectoryInfo[] kids;
            try { if (!Directory.Exists(root)) return; kids = new DirectoryInfo(root).GetDirectories(); }
            catch { return; }
            List<VerDir> vers = new List<VerDir>();
            foreach (DirectoryInfo d in kids)
                if (VersionKey(d.Name).Count >= 2) vers.Add(MakeVerDir(d, d.Name));
            if (vers.Count < 2) return;
            vers.Sort(CompareNewestFirst);

            string keepName = null;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Kingsoft\Office\6.0\Common"))
                {
                    string ir = k == null ? null : k.GetValue("InstallRoot") as string;
                    if (!string.IsNullOrEmpty(ir)) keepName = Path.GetFileName(ir.TrimEnd('\\'));
                }
            }
            catch { }
            VerDir keep = vers[0];
            if (keepName != null)
                foreach (VerDir v in vers)
                    if (string.Equals(v.Dir.Name, keepName, StringComparison.OrdinalIgnoreCase)) { keep = v; break; }
            foreach (VerDir v in vers)
                if (!ReferenceEquals(v, keep)) AddDir(c, v.Dir.FullName, false);
        }

        // Пакеты UWP: %LOCALAPPDATA%\Packages\<Пакет>\{AC\INetCache, AC\Temp, TempState} — по
        // контракту платформы это кэш, который система вправе стереть сама. LocalState и
        // Settings не трогаем: там данные. Новый Teams держит в LocalCache WebView2-профили —
        // обычный Chromium-кэш.
        private void AddStoreAppCaches(CleanCategory c, string packages)
        {
            string[] kids;
            try { if (!Directory.Exists(packages)) return; kids = Directory.GetDirectories(packages); }
            catch { return; }
            foreach (string p in kids)
            {
                AddDir(c, Path.Combine(p, "AC\\INetCache"), true);
                AddDir(c, Path.Combine(p, "AC\\Temp"), true);
                AddDir(c, Path.Combine(p, "TempState"), true);
                string teams = Path.Combine(p, "LocalCache\\Microsoft\\MSTeams");
                bool hasTeams = false;
                try { hasTeams = Directory.Exists(teams); } catch { }
                if (!hasTeams) continue;
                string[] profs = null;
                try { profs = Directory.GetDirectories(Path.Combine(teams, "EBWebView"), "WV2Profile_*"); } catch { }
                if (profs != null) foreach (string pr in profs) AddElectronCache(c, pr);
                AddDir(c, Path.Combine(teams, "Logs"), true);
            }
        }

        // ================= DRIVERSTORE =================

        // Steam ставится куда угодно — берём путь из реестра, а не угадываем диск.
        // Папка Steam из реестра (HKCU\Software\Valve\Steam\SteamPath) или null
        private static string SteamPath()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (k == null) return null;
                    string s = k.GetValue("SteamPath") as string;
                    return string.IsNullOrEmpty(s) ? null : s.Replace('/', '\\');
                }
            }
            catch { return null; }
        }

        // Кэши Steam без шейдеров: shadercache живёт в категории «Кэши шейдеров GPU»
        private void AddSteamCaches(CleanCategory c, string steam)
        {
            if (string.IsNullOrEmpty(steam)) return;
            AddDir(c, Path.Combine(steam, "appcache\\httpcache"), true);
            AddDir(c, Path.Combine(steam, "logs"), true);
        }
    }
}
