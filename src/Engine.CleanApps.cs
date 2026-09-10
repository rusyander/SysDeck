// Windows Process Cleaner — кэши популярных программ (дополнение к BuildCleanCategories).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.IO;

namespace WindowsProcessCleaner
{
    public partial class Engine
    {
        // ================= КЭШИ ПОПУЛЯРНЫХ ПРОГРАММ =================
        // Правило отбора здесь одно и оно жёстче, чем «папка называется Cache»: в список попадает
        // только то, что программа создаёт заново сама и без спроса. Ни одного пути, где могут
        // лежать документы, проекты, сейвы, переписка, лицензии или токены. Всё, в чём есть
        // сомнение, либо не попало сюда вовсе (список исключений — в конце файла), либо лежит
        // в категории без галочки по умолчанию.
        //
        // Каждая строка проходит через AddDir: несуществующая папка просто не становится целью,
        // поэтому каталог одинаково безопасен на машине, где программы нет.

        // Категории те же, что заводит BuildCleanCategories. Если категория уже есть (в ней
        // нашлись цели) — дополняем её, если нет (программ этого рода на машине не было) —
        // создаём такую же сами. Дублировать заголовки приходится: категория, оказавшаяся
        // пустой, в список не добавляется, и от неё не остаётся даже описания.
        private static CleanCategory NewAppCat(string id)
        {
            CleanCategory c = new CleanCategory();
            c.Id = id;
            if (id == "appcache")
            {
                c.Title = Tr.S("Кэши приложений", "App caches");
                c.Recommended = true;
                c.Desc = Tr.S("мессенджеры, редакторы, игровые лаунчеры, утилиты — только кэш",
                              "messengers, editors, game launchers, utilities — cache only");
            }
            else if (id == "browser")
            {
                c.Title = Tr.S("Кэши браузеров", "Browser caches");
                c.Desc = Tr.S("кэш браузеров на движках Chromium и Firefox (пароли, куки и история не трогаются)",
                              "Chromium- and Firefox-based browser caches (passwords, cookies and history untouched)");
            }
            else if (id == "logs")
            {
                c.Title = Tr.S("Старые логи", "Old logs");
                c.Desc = Tr.S("журналы и отчёты о сбоях установленных программ",
                              "logs and crash reports of installed programs");
            }
            else if (id == "devbig")
            {
                c.Title = Tr.S("Dev: скачанные тулчейны", "Dev: downloaded toolchains");
                c.Desc = Tr.S("скачанные SDK, драйверы БД и промежуточные сборки — скачаются заново",
                              "downloaded SDKs, database drivers and derived build data — will re-download");
            }
            else
            {
                c.Title = Tr.S("Кэши Windows (эскизы, иконки, шрифты)", "Windows caches (thumbnails, icons, fonts)");
                c.Recommended = true;
                c.Desc = Tr.S("кэши шрифтов, которые программы строят заново при первом запуске",
                              "font caches that programs rebuild on the next start");
            }
            return c;
        }

        private static CleanCategory AppCat(List<CleanCategory> list, List<CleanCategory> made, string id)
        {
            foreach (CleanCategory c in list)
                if (string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) return c;
            foreach (CleanCategory c in made)
                if (string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) return c;
            CleanCategory n = NewAppCat(id);
            made.Add(n);
            return n;
        }

        // Созданные заново категории встают перед группами winapp2: те собраны из внешнего файла
        // и в списке всегда идут последними — иначе встроенная категория оказалась бы после них.
        private static void InsertAppCats(List<CleanCategory> list, List<CleanCategory> made)
        {
            int at = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                string id = list[i].Id;
                if (id != null && id.StartsWith("winapp2:", StringComparison.OrdinalIgnoreCase)) { at = i; break; }
            }
            foreach (CleanCategory c in made)
                if (c.Targets.Count > 0) list.Insert(at++, c);
        }

        // ---------- помощники, которых не было в Engine.Clean.cs ----------

        // Приложение на WebView2 держит профиль Edge в <папка приложения>\EBWebView: там обычные
        // кэши Chromium. Сама папка — не кэш (в ней же Local Storage приложения), поэтому
        // разбираем её тем же AddChromium, что и браузеры.
        private void AddWebView2Caches(CleanCategory c, string dir)
        {
            string wv = Path.Combine(dir, "EBWebView");
            if (!Directory.Exists(wv)) return;
            AddChromium(c, wv);
            AddDir(c, Path.Combine(wv, "Crashpad\\reports"), true);
        }

        // На WebView2 сегодня сделана половина установщиков и «оболочек» вендоров, перечислять их
        // поимённо бессмысленно: проходим один уровень корня и берём тех, у кого есть EBWebView.
        private void AddWebView2Sweep(CleanCategory c, string root)
        {
            string[] kids;
            try { if (!Directory.Exists(root)) return; kids = Directory.GetDirectories(root); }
            catch { return; }
            foreach (string k in kids) AddWebView2Caches(c, k);
        }

        // IDE на платформе IntelliJ: <Имя><версия>\{caches,log,tmp}. AddJetBrains делает то же
        // самое, но только для %LOCALAPPDATA%\JetBrains, а Android Studio лежит в Google.
        private void AddIntelliJCaches(CleanCategory c, string parent, string prefix)
        {
            string[] kids;
            try { if (!Directory.Exists(parent)) return; kids = Directory.GetDirectories(parent); }
            catch { return; }
            foreach (string k in kids)
            {
                if (prefix != null && !Path.GetFileName(k).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                AddDir(c, Path.Combine(k, "caches"), true);
                AddDir(c, Path.Combine(k, "log"), true);
                AddDir(c, Path.Combine(k, "tmp"), true);
            }
        }

        // Подпапки с именем на prefix: Epic заводит новый webcache_<номер> на каждую версию
        // лаунчера и старые не удаляет.
        private void AddByPrefix(CleanCategory c, string parent, string prefix)
        {
            string[] kids;
            try { if (!Directory.Exists(parent)) return; kids = Directory.GetDirectories(parent); }
            catch { return; }
            foreach (string k in kids)
                if (Path.GetFileName(k).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) AddDir(c, k, true);
        }

        // Игры на Unreal Engine складывают отчёты в %LOCALAPPDATA%\<Игра>\Saved\{Crashes,Logs}.
        // Берём ровно эти две подпапки: в самой Saved рядом лежат SaveGames и Config.
        private void AddUnrealSavedJunk(CleanCategory c, string lad)
        {
            string[] kids;
            try { if (!Directory.Exists(lad)) return; kids = Directory.GetDirectories(lad); }
            catch { return; }
            foreach (string k in kids)
            {
                string saved = Path.Combine(k, "Saved");
                bool ok = false;
                try { ok = Directory.Exists(saved); } catch { }
                if (!ok) continue;
                AddDir(c, Path.Combine(saved, "Crashes"), true);
                AddDir(c, Path.Combine(saved, "Logs"), true);
            }
        }

        // ---------- сам каталог ----------

        private void AddPopularAppTargets(List<CleanCategory> list, string lad, string ad, string up, string pd)
        {
            List<CleanCategory> made = new List<CleanCategory>();
            CleanCategory apps = AppCat(list, made, "appcache");
            CleanCategory br = AppCat(list, made, "browser");
            CleanCategory logs = AppCat(list, made, "logs");
            CleanCategory big = AppCat(list, made, "devbig");
            CleanCategory shell = AppCat(list, made, "shell");
            int i;

            // ========== Приложения на Electron ==========
            // У всех один и тот же набор папок (Cache, Code Cache, GPUCache, Dawn*, Service Worker,
            // Crashpad, logs), и AddElectronCache добавляет только их — корень приложения, где
            // лежат настройки, база сообщений и токен сессии, не трогается никогда.

            // Мессенджеры. Cache здесь — обычный HTTP-кэш Chromium: переписка хранится не в нём
            // (Viber — sqlite в корне, Signal — attachments.noindex, Telegram — tdata).
            string[] msg = new string[] {
                "ViberPC", "WhatsApp", "Microsoft\\Skype for Desktop", "Signal", "Signal Beta",
                "Element", "Riot", "Wire", "Mattermost", "Rocket.Chat", "Threema", "Session",
                "Beeper", "Ferdium", "Franz", "Zulip", "VK Messenger", "discorddevelopment",
            };
            for (i = 0; i < msg.Length; i++) AddElectronCache(apps, Path.Combine(ad, msg[i]));
            AddElectronCache(apps, Path.Combine(lad, "WhatsApp"));
            // Классический Skype: %APPDATA%\Skype\<логин>\media_messaging\media_cache — эскизы
            // и превью присланных файлов. Соседние папки (main.db, chatsync) — это сама переписка.
            AddSubdirCaches(apps, Path.Combine(ad, "Skype"), "media_messaging\\media_cache");
            // Zoom: встроенный браузер конференций. data\ рядом — база чатов, её не трогаем.
            AddDir(apps, Path.Combine(lad, "Zoom\\data\\WebviewCacheX64"), true);
            AddDir(apps, Path.Combine(lad, "Zoom\\data\\WebviewCache"), true);

            // Заметки, задачи, доски: кэш загруженных страниц и картинок. Сами заметки живут
            // на сервере и в корне профиля приложения, а не в Cache.
            string[] work = new string[] {
                "Notion Calendar", "Todoist", "Evernote", "Joplin", "Logseq", "Miro", "ClickUp",
                "Trello", "Linear", "Asana", "Canva",
            };
            for (i = 0; i < work.Length; i++) AddElectronCache(apps, Path.Combine(ad, work[i]));

            // Инструменты разработчика на Electron. Коллекции Postman, соединения DBeaver/Compass
            // и настройки редакторов лежат вне Cache и остаются на месте.
            string[] devApps = new string[] {
                "Code - Insiders", "Code - OSS", "VSCodium", "Windsurf", "Trae", "Insomnia",
                "bruno", "MongoDB Compass", "pgadmin4", "GitHub Desktop", "Docker Desktop",
                "Beekeeper Studio", "Hyper", "Tabby",
            };
            for (i = 0; i < devApps.Length; i++) AddElectronCache(apps, Path.Combine(ad, devApps[i]));
            // Редакторы на базе VS Code держат ещё три папки мимо схемы Electron: распакованный
            // байт-код (CachedData), скачанные VSIX и снимки профилей.
            string[] codeLike = new string[] {
                "Code", "Code - Insiders", "Code - OSS", "VSCodium", "Cursor", "Windsurf", "Trae",
            };
            for (i = 0; i < codeLike.Length; i++)
            {
                AddDir(apps, Path.Combine(ad, codeLike[i] + "\\CachedData"), true);
                AddDir(apps, Path.Combine(ad, codeLike[i] + "\\CachedExtensionVSIXs"), true);
                AddDir(apps, Path.Combine(ad, codeLike[i] + "\\CachedProfilesData"), true);
                AddDir(apps, Path.Combine(ad, codeLike[i] + "\\logs"), true);
            }

            // Прочие настольные приложения на Electron: ассистенты, музыка, видео, панели вендоров.
            string[] misc = new string[] {
                "Claude", "ChatGPT", "Perplexity", "Power-Toys", "Samsung Magician", "G HUB",
                "YandexMusic", "nvidia-broadcast", "Electron", "slobs-client", "Twitch",
                "Amazon Music", "Deezer", "TIDAL", "YouTube Music", "Plex", "Jellyfin Media Player",
                "Stremio", "Loom", "Shift", "Station",
            };
            for (i = 0; i < misc.Length; i++) AddElectronCache(apps, Path.Combine(ad, misc[i]));
            AddElectronCache(apps, Path.Combine(lad, "FortiClient"));
            AddElectronCache(apps, Path.Combine(lad, "LGHUB"));

            // Приложения на WebView2 (Ollama, установщики вендоров, «оболочки» утилит).
            AddWebView2Sweep(apps, ad);
            AddWebView2Sweep(apps, lad);

            // ========== IDE и редакторы ==========
            // Android Studio — та же платформа IntelliJ, но в %LOCALAPPDATA%\Google: индексы
            // (caches) и tmp пересобираются при первом открытии проекта.
            AddIntelliJCaches(apps, Path.Combine(lad, "Google"), "AndroidStudio");
            // JetBrains Toolbox: свои имена папок (cache/logs/temp), под AddJetBrains не подходят.
            AddDir(apps, Path.Combine(lad, "JetBrains\\Toolbox\\cache"), true);
            AddDir(apps, Path.Combine(lad, "JetBrains\\Toolbox\\logs"), true);
            AddDir(apps, Path.Combine(lad, "JetBrains\\Toolbox\\temp"), true);
            // Visual Studio: ShadowCache — копии сборок для конструктора форм, VSApplicationInsights —
            // очередь телеметрии. ComponentModelCache уже разбирает BuildCleanCategories.
            AddSubdirCaches(apps, Path.Combine(lad, "Microsoft\\VisualStudio"), "Designer\\ShadowCache");
            AddDir(apps, Path.Combine(lad, "Microsoft\\VSApplicationInsights"), true);
            // Sublime Text/Merge: Cache — разобранные пакеты и подсветка, собирается заново.
            AddDir(apps, Path.Combine(lad, "Sublime Text\\Cache"), true);
            AddDir(apps, Path.Combine(lad, "Sublime Text 3\\Cache"), true);
            AddDir(apps, Path.Combine(lad, "Sublime Merge\\Cache"), true);
            AddDir(apps, Path.Combine(lad, "NetBeans\\Cache"), true);
            // Unity/Godot/Unreal: редакторские логи и промежуточные данные сборки.
            AddDir(logs, Path.Combine(lad, "Unity\\Editor"), true, "*.log", 0);
            AddDir(logs, Path.Combine(ad, "UnityHub\\logs"), true);
            AddDir(logs, Path.Combine(ad, "Godot\\logs"), true);
            AddDir(logs, Path.Combine(lad, "UnrealEngineLauncher\\Logs"), true);
            // DerivedDataCache Unreal пересобирается компиляцией шейдеров — это часы, поэтому
            // в категорию без галочки.
            AddDir(big, Path.Combine(lad, "UnrealEngine\\Common\\DerivedDataCache"), true);
            // SDK Android: cache — список пакетов SDK Manager, build-cache — промежуточные
            // артефакты старого Gradle-плагина. avd рядом (образы эмулятора) не трогаем.
            AddDir(big, Path.Combine(up, ".android\\cache"), true);
            AddDir(big, Path.Combine(up, ".android\\build-cache"), true);
            // Драйверы JDBC DBeaver качает сам при первом подключении к такой СУБД.
            AddDir(big, Path.Combine(ad, "DBeaverData\\drivers"), true);

            // ========== Дизайн, видео, звук ==========
            // OBS: журналы, отчёты о падениях, скачанные обновления и замеры профилировщика.
            // Сцены и профили (basic\) — рядом, их не трогаем.
            AddDir(logs, Path.Combine(ad, "obs-studio\\logs"), true);
            AddDir(logs, Path.Combine(ad, "obs-studio\\crashes"), true);
            AddDir(logs, Path.Combine(ad, "obs-studio\\profiler_data"), true);
            AddDir(apps, Path.Combine(ad, "obs-studio\\updates"), true);
            AddDir(logs, Path.Combine(ad, "HandBrake\\logs"), true);
            AddDir(logs, Path.Combine(ad, "Blackmagic Design\\DaVinci Resolve\\Support\\logs"), true);
            // VLC: art — обложки, скачанные из сети под воспроизводимые файлы.
            AddDir(apps, Path.Combine(ad, "vlc\\art"), true);
            // Adobe: кэш Camera Raw (превью и разобранные RAW), медиакэш Premiere/After Effects
            // в Local-профиле, кэш Acrobat/Reader по версиям, папки апдейтера.
            AddDir(apps, Path.Combine(lad, "Adobe\\CameraRaw\\Cache"), true);
            AddDir(apps, Path.Combine(lad, "Adobe\\Common\\Media Cache"), true);
            AddDir(apps, Path.Combine(lad, "Adobe\\Common\\Peak Files"), true);
            AddSubdirCaches(apps, Path.Combine(lad, "Adobe\\Acrobat"), "Cache");
            AddDir(apps, Path.Combine(lad, "Adobe\\AAMUpdater"), true);
            AddDir(apps, Path.Combine(pd, "Adobe\\ARM"), true);
            // CoolType строит кэш шрифтов заново при первом запуске любого приложения Adobe.
            AddDir(shell, Path.Combine(ad, "Adobe\\CT Font Cache"), true);
            // fontconfig — общий кэш шрифтов программ на GTK (GIMP, Inkscape, Blender).
            AddDir(shell, Path.Combine(lad, "fontconfig\\cache"), true);

            // ========== Офис и документы ==========
            // Office: WebServiceCache — ответы облачных сервисов, Wef — веб-надстройки,
            // OTele — очередь телеметрии. OfficeFileCache намеренно НЕ трогаем (см. конец файла).
            AddSubdirCaches(apps, Path.Combine(lad, "Microsoft\\Office"), "WebServiceCache");
            AddSubdirCaches(apps, Path.Combine(lad, "Microsoft\\Office"), "Wef");
            AddDir(apps, Path.Combine(lad, "Microsoft\\Office\\OTele"), true);
            AddDir(apps, Path.Combine(ad, "LibreOffice\\4\\cache"), true);
            // WPS Office: cache и log в профиле office6. Соседняя backup — автосохранения
            // документов, она под запретом.
            AddDir(apps, Path.Combine(ad, "kingsoft\\office6\\cache"), true);
            AddDir(logs, Path.Combine(ad, "kingsoft\\office6\\log"), true);
            AddDir(apps, Path.Combine(ad, "kingsoft\\PDF\\Cache"), true);
            AddDir(logs, Path.Combine(ad, "kingsoft\\PDF\\log"), true);
            AddElectronCache(apps, Path.Combine(ad, "ONLYOFFICE\\DesktopEditors"));
            // Sumatra: кэш отрендеренных страниц и обложек.
            AddDir(apps, Path.Combine(lad, "SumatraPDF\\sumatrapdfcache"), true);

            // ========== Игровые лаунчеры ==========
            string steam = SteamPath();
            if (!string.IsNullOrEmpty(steam))
            {
                // librarycache — обложки и логотипы игр из магазина, dumps — отчёты о падениях
                // самого клиента. Ни то, ни другое не относится к установленным играм.
                AddDir(apps, Path.Combine(steam, "appcache\\librarycache"), true);
                AddDir(logs, Path.Combine(steam, "dumps"), true);
            }
            AddDir(apps, Path.Combine(lad, "Epic Games\\EOSOverlay\\BrowserCache"), true);
            AddByPrefix(apps, Path.Combine(lad, "EpicGamesLauncher\\Saved"), "webcache");
            AddDir(logs, Path.Combine(lad, "GOG.com\\Galaxy\\logs"), true);
            AddDir(logs, Path.Combine(pd, "GOG.com\\Galaxy\\logs"), true);
            AddDir(apps, Path.Combine(lad, "Electronic Arts\\EA Desktop\\cache"), true);
            AddDir(logs, Path.Combine(lad, "Electronic Arts\\EA Desktop\\Logs"), true);
            AddDir(logs, Path.Combine(lad, "Origin\\Logs"), true);
            AddDir(logs, Path.Combine(pd, "Origin\\Logs"), true);
            AddDir(logs, Path.Combine(lad, "Ubisoft Game Launcher\\logs"), true);
            AddDir(logs, Path.Combine(ad, "Battle.net\\Logs"), true);
            AddDir(logs, Path.Combine(pd, "Battle.net\\Agent\\Logs"), true);
            AddDir(logs, Path.Combine(lad, "Riot Games\\Riot Client\\Logs"), true);
            // Roblox: rbx-storage — кэш скачанного контента мест, Downloads — установщики версий.
            AddDir(apps, Path.Combine(lad, "Roblox\\rbx-storage"), true);
            AddDir(apps, Path.Combine(lad, "Roblox\\Downloads"), true);
            AddDir(logs, Path.Combine(lad, "Roblox\\logs"), true);
            // Minecraft: журналы и отчёты о падениях. Соседняя saves (миры) закрыта
            // предохранителем, но мы её и не перечисляем.
            AddDir(logs, Path.Combine(ad, ".minecraft\\logs"), true);
            AddDir(logs, Path.Combine(ad, ".minecraft\\crash-reports"), true);
            AddDir(apps, Path.Combine(pd, "Wargaming.net\\GameCenter\\cache"), true);
            AddDir(logs, Path.Combine(pd, "Wargaming.net\\GameCenter\\logs"), true);
            // Отчёты о падениях игр на Unreal Engine: их не удаляет ни одна игра.
            AddUnrealSavedJunk(logs, lad);

            // ========== Вендорские панели и драйверы ==========
            // G HUB держит в ProgramData скачанные пакеты обновлений и прошивок — там легко
            // набирается больше полугигабайта, а нужны они только во время установки.
            AddDir(apps, Path.Combine(pd, "LGHUB\\cache"), true);
            AddDir(logs, Path.Combine(pd, "LGHUB\\logs"), true);
            AddDir(logs, Path.Combine(pd, "NVIDIA Corporation\\NVIDIA Broadcast\\logs"), true);
            AddDir(apps, Path.Combine(lad, "Intel\\PresentMon\\cef-cache"), true);
            // У панелей периферии одна и та же схема: <вендор>\<продукт>\Log(s).
            string[] vendors = new string[] {
                "Logi", "Logishrd", "Razer", "Corsair", "MSI", "SteelSeries", "ASUS", "NZXT",
                "Gigabyte", "Intel", "AMD", "Samsung",
            };
            for (i = 0; i < vendors.Length; i++)
            {
                AddSubdirCaches(logs, Path.Combine(pd, vendors[i]), "Logs");
                AddSubdirCaches(logs, Path.Combine(pd, vendors[i]), "Log");
            }

            // ========== Утилиты ==========
            // qBittorrent: только logs. Соседняя BT_backup — список закачек и их прогресс.
            AddDir(logs, Path.Combine(lad, "qBittorrent\\logs"), true);
            AddDir(logs, Path.Combine(ad, "qBittorrent\\logs"), true);
            // TeamViewer и AnyDesk пишут журналы прямо в корень своей папки, поэтому по маске:
            // сама папка с настройками подключений остаётся.
            AddDir(logs, Path.Combine(ad, "TeamViewer"), true, "*.log", 0);
            AddDir(logs, Path.Combine(pd, "TeamViewer"), true, "*.log", 0);
            AddDir(logs, Path.Combine(ad, "AnyDesk"), true, "*.trace", 0);
            AddDir(logs, Path.Combine(pd, "AnyDesk"), true, "*.trace", 0);
            AddDir(apps, Path.Combine(lad, "Softdeluxe\\Free Download Manager\\cache"), true);
            // Java Web Start / апплеты: скачанные jar, подтягиваются заново.
            AddDir(apps, Path.Combine(lad, "Sun\\Java\\Deployment\\cache"), true);
            // Chocolatey хранит скачанные установщики отдельно от самих пакетов.
            AddDir(apps, Path.Combine(pd, "ChocolateyHttpCache"), true);
            AddDir(logs, Path.Combine(pd, "Microsoft\\EdgeUpdate\\Log"), true);
            AddSubdirCaches(logs, Path.Combine(lad, "Microsoft\\PowerToys"), "Logs");
            AddDir(logs, Path.Combine(ad, "MySQL\\Workbench\\log"), true);

            // ========== Браузеры ==========
            // Opera хранит профиль в Roaming, а кэш — в Local, поэтому две разные схемы.
            string[] opera = new string[] {
                "Opera Stable", "Opera GX Stable", "Opera Beta", "Opera Developer",
                "Opera Crypto Stable", "Opera Neon", "Opera Air",
            };
            for (i = 0; i < opera.Length; i++)
            {
                AddChromium(br, Path.Combine(ad, "Opera Software\\" + opera[i]));
                AddElectronCache(br, Path.Combine(lad, "Opera Software\\" + opera[i]));
            }
            // Остальные сборки Chromium: у всех одинаковый User Data.
            string[] chromium = new string[] {
                "Arc\\User Data", "Thorium\\User Data", "CentBrowser\\User Data",
                "Comodo\\Dragon\\User Data", "SRWare Iron\\User Data", "Slimjet\\User Data",
                "Naver\\Naver Whale\\User Data", "Epic Privacy Browser\\User Data",
                "Atom\\User Data", "Amigo\\User Data", "Sputnik\\Sputnik\\User Data",
                "Google\\Chrome for Testing\\User Data", "Yandex\\YandexBrowserCanary\\User Data",
            };
            for (i = 0; i < chromium.Length; i++) AddChromium(br, Path.Combine(lad, chromium[i]));
            // Форки Firefox: тот же профиль, те же cache2/startupCache.
            string[] gecko = new string[] {
                "zen\\Profiles", "Floorp\\Profiles", "Mercury\\Profiles",
                "Moonchild Productions\\Pale Moon\\Profiles", "Betterbird\\Profiles",
                "Mozilla\\SeaMonkey\\Profiles", "Mozilla\\icecat\\Profiles",
            };
            for (i = 0; i < gecko.Length; i++)
            {
                AddFirefox(br, Path.Combine(ad, gecko[i]));
                AddFirefox(br, Path.Combine(lad, gecko[i]));
            }

            InsertAppCats(list, made);
        }

        // ---------- что сюда сознательно НЕ попало ----------
        // %LOCALAPPDATA%\Microsoft\Office\<ver>\OfficeFileCache и кэш OneNote — там лежат
        //   несинхронизированные правки документов и заметок: удаление теряет их безвозвратно.
        // %LOCALAPPDATA%\Microsoft\Outlook — файлы .ost/.pst, это почта, а не кэш.
        // %ProgramData%\Package Cache и %LOCALAPPDATA%\Package Cache (Visual Studio, WiX) —
        //   без них не работают восстановление и удаление уже установленных программ.
        // %APPDATA%\Notepad++\backup — резервные копии несохранённых вкладок.
        // %LOCALAPPDATA%\qBittorrent\BT_backup и %APPDATA%\uTorrent — списки и прогресс закачек.
        // %USERPROFILE%\.ollama\models, кэши моделей LM Studio — гигабайты, но их скачивал
        //   пользователь вручную.
        // Шейдерные кэши игр (Steam shadercache, <игра>\shader_cache) — они уже в отдельной
        //   категории «Кэши шейдеров GPU» без галочки: место маленькое, а пересборка долгая.
        // Blender, Vegas, Corel, Affinity, foobar2000, Everything, Rufus — у них не нашлось
        //   папки, про которую можно утверждать, что там только кэш; см. tools\scan-app-caches.ps1,
        //   он покажет реальные пути на конкретной машине.
    }
}
