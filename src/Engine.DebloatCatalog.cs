// SysDeck — каталог «Windows: лишнее». Статический список: что это, зачем
// выключать, чем рискуете, рекомендация. По умолчанию отмечен только универсальный мусор
// (телеметрия, реклама, Bing, заглушки Store, мёртвые компоненты); всё спорное — Copilot, Xbox,
// OneDrive, Teams, Phone Link, поиск Windows — в списке, но без галочки и с предупреждением.
// Механика действий — Engine.Debloat.cs.

using System;
using System.Collections.Generic;

namespace SysDeck
{
    public partial class Engine
    {
        // ключи разделов, чтобы не опечататься в тридцати местах
        private const string CDM = "Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager";
        private const string ExplorerAdv = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";
        private const string PolDataCollection = "SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection";
        private const string PolWindowsAI = "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI";

        private static DebloatItem D(List<DebloatItem> list, string cat, string id, string title, string what, string pro, string con, int recommend, bool defChecked, bool serious)
        {
            DebloatItem it = new DebloatItem();
            it.Category = cat; it.Id = id; it.Title = title; it.What = what; it.Pro = pro; it.Con = con;
            it.Recommend = recommend; it.DefaultChecked = defChecked; it.Serious = serious;
            list.Add(it);
            return it;
        }

        public static string[] DebloatCategories()
        {
            return new string[] {
                Tr.S("ИИ: Copilot, Recall, Cortana", "AI: Copilot, Recall, Cortana"),
                Tr.S("Телеметрия и диагностика", "Telemetry and diagnostics"),
                Tr.S("Реклама, подсказки и Bing", "Ads, tips and Bing"),
                Tr.S("Виджеты и новости", "Widgets and news"),
                Tr.S("Предустановленные приложения Store", "Preinstalled Store apps"),
                Tr.S("Сторонние приложения-заглушки", "Third-party stub apps"),
                Tr.S("Xbox и игры", "Xbox and gaming"),
                Tr.S("OneDrive, Teams, Phone Link", "OneDrive, Teams, Phone Link"),
                Tr.S("Службы Windows", "Windows services"),
                Tr.S("Компоненты Windows", "Windows features"),
                "PowerToys" };
        }

        public List<DebloatItem> DebloatCatalog()
        {
            string[] C = DebloatCategories();
            string cAI = C[0], cTel = C[1], cAds = C[2], cWid = C[3], cApps = C[4], cThird = C[5], cXbox = C[6], cCloud = C[7], cSvc = C[8], cFeat = C[9], cPt = C[10];
            List<DebloatItem> l = new List<DebloatItem>();
            const string ptPath = "settings.json";

            // ---------- ИИ ----------
            D(l, cAI, "copilot", "Copilot",
              Tr.S("Ассистент Microsoft на панели задач и в приложениях; отправляет запросы в облако.", "Microsoft's assistant on the taskbar and inside apps; sends queries to the cloud."),
              Tr.S("Меньше фонового трафика и лишняя кнопка исчезает; Edge и Office ассистента не теряют.", "Less background traffic and one button fewer; Edge and Office keep their own assistants."),
              Tr.S("Кто пользуется Copilot в Windows — потеряет его; «Удалить» сносит и приложение из Store.", "Anyone using Copilot in Windows loses it; “Remove” also uninstalls the Store app."),
              0, false, false)
                .Reg("HKCU", "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot", 1)
                .Reg("HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot", 1)
                .Reg("HKCU", ExplorerAdv, "ShowCopilotButton", 0)
                .Appx("Microsoft.Copilot", false).Appx("Microsoft.Windows.Ai.Copilot.Provider", false);
            D(l, cAI, "recall", "Recall",
              Tr.S("Снимки экрана каждые несколько секунд с распознаванием текста — «память» ПК на Copilot+ устройствах.", "Screenshots every few seconds with OCR — the PC “memory” on Copilot+ devices."),
              Tr.S("Не копится база всего, что было на экране (пароли, переписка, документы); экономия диска и батареи.", "No database of everything on screen (passwords, chats, documents); saves disk and battery."),
              Tr.S("Функция «найти, что я видел на прошлой неделе» перестанет работать; на обычных ПК её и так нет.", "The “find what I saw last week” feature stops working; ordinary PCs don't have it anyway."),
              1, false, true)
                .Reg("HKLM", PolWindowsAI, "DisableAIDataAnalysis", 1).Reg("HKLM", PolWindowsAI, "AllowRecallEnablement", 0)
                .Reg("HKCU", "Software\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableAIDataAnalysis", 1)
                .Feature("Recall", true);
            D(l, cAI, "clicktodo", "Click to Do",
              Tr.S("Действия ИИ по содержимому экрана (Win+клик): суммировать текст, убрать фон с картинки.", "AI actions on screen content (Win+click): summarise text, remove image backgrounds."),
              Tr.S("Одним фоновым анализом экрана меньше.", "One background screen analysis fewer."),
              Tr.S("Только на Copilot+ ПК; там некоторые им пользуются.", "Copilot+ PCs only; some people there do use it."),
              0, false, false)
                .Reg("HKLM", PolWindowsAI, "DisableClickToDo", 1);
            D(l, cAI, "cortana", "Cortana",
              Tr.S("Голосовой ассистент, снятый Microsoft с поддержки в 2023 году; приложение осталось в системе.", "The voice assistant Microsoft retired in 2023; the app is still in the system."),
              Tr.S("Мёртвое приложение и его фоновые компоненты исчезают.", "A dead app and its background parts go away."),
              Tr.S("Ничем: Cortana больше не работает ни в одном регионе.", "Nothing: Cortana no longer works anywhere."),
              2, true, false)
                .Reg("HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "AllowCortana", 0)
                .Appx("Microsoft.549981C3F5F10");

            // ---------- Телеметрия ----------
            D(l, cTel, "telemetry", Tr.S("Диагностические данные (уровень «Безопасность»)", "Diagnostic data (Security level)"),
              Tr.S("Windows отправляет в Microsoft сведения об устройстве, ошибках и использовании приложений.", "Windows sends device, error and app-usage data to Microsoft."),
              Tr.S("Минимальный уровень отправки; на Pro/Home система всё равно оставит «обязательные» данные, но не больше.", "The lowest level; Pro/Home still keep the “required” data, nothing beyond it."),
              Tr.S("Программа предварительной оценки (Insider) требует полной диагностики; в остальном незаметно.", "The Insider program requires full diagnostics; otherwise unnoticeable."),
              1, true, false)
                .Reg("HKLM", PolDataCollection, "AllowTelemetry", 0)
                .Reg("HKLM", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\DataCollection", "AllowTelemetry", 0);
            D(l, cTel, "diagtrack", Tr.S("Служба телеметрии (DiagTrack)", "Telemetry service (DiagTrack)"),
              Tr.S("«Функциональные возможности для подключённых пользователей и телеметрия» — собирает и отправляет диагностику.", "“Connected User Experiences and Telemetry” — collects and uploads diagnostics."),
              Tr.S("Не работает фоновый сборщик; меньше записей на диск и трафика.", "No background collector; fewer disk writes and less traffic."),
              Tr.S("Страница «Диагностика и отзывы» может показывать ошибку; Insider-сборки не получат.", "The “Diagnostics & feedback” page may show an error; Insider builds won't arrive."),
              1, true, false)
                .Svc("DiagTrack");
            D(l, cTel, "dmwappush", Tr.S("Служба WAP Push (dmwappushservice)", "WAP Push service (dmwappushservice)"),
              Tr.S("Маршрутизация push-сообщений управления устройством; в списках «телеметрии» из-за старых сборок.", "Routes device-management push messages; lands on “telemetry” lists because of old builds."),
              Tr.S("Одной фоновой службой меньше на домашнем ПК.", "One background service fewer on a home PC."),
              Tr.S("Корпоративное управление (Intune/MDM) и регистрация в рабочей учётной записи могут сломаться.", "Corporate management (Intune/MDM) and work-account enrolment may break."),
              0, false, true)
                .Svc("dmwappushservice");
            D(l, cTel, "ceip", Tr.S("Задачи сбора данных (CEIP, Appraiser)", "Data-collection tasks (CEIP, Appraiser)"),
              Tr.S("Планировщик запускает «Программу улучшения качества» и оценку совместимости, даже когда вы в ней не участвуете.", "The scheduler runs the “Customer Experience Improvement Program” and compatibility appraisal even when you opted out."),
              Tr.S("Меньше фоновой нагрузки на диск и CPU при простое.", "Less idle-time disk and CPU load."),
              Tr.S("Оценка совместимости используется перед крупными обновлениями Windows — они пройдут, но проверка будет дольше.", "Compatibility appraisal is used before feature updates — they still run, the check just takes longer."),
              1, true, false)
                .Task("\\Microsoft\\Windows\\Customer Experience Improvement Program\\Consolidator")
                .Task("\\Microsoft\\Windows\\Customer Experience Improvement Program\\UsbCeip")
                .Task("\\Microsoft\\Windows\\Application Experience\\Microsoft Compatibility Appraiser")
                .Task("\\Microsoft\\Windows\\Application Experience\\Microsoft Compatibility Appraiser Exp")
                .Task("\\Microsoft\\Windows\\Application Experience\\ProgramDataUpdater")
                .Task("\\Microsoft\\Windows\\Autochk\\Proxy")
                .Task("\\Microsoft\\Windows\\DiskDiagnostic\\Microsoft-Windows-DiskDiagnosticDataCollector");
            D(l, cTel, "feedback", Tr.S("Запросы отзывов", "Feedback requests"),
              Tr.S("Всплывающие «Оцените Windows» и фоновые задачи Feedback Hub.", "The “rate Windows” pop-ups and the Feedback Hub background tasks."),
              Tr.S("Уведомления с просьбой оценить пропадают.", "The rating prompts stop."),
              Tr.S("Ничем заметным.", "Nothing noticeable."),
              1, true, false)
                .Task("\\Microsoft\\Windows\\Feedback\\Siuf\\DmClient").Task("\\Microsoft\\Windows\\Feedback\\Siuf\\DmClientOnScenarioDownload")
                .Reg("HKCU", "Software\\Microsoft\\Siuf\\Rules", "NumberOfSIUFInPeriod", 0)
                .Reg("HKLM", PolDataCollection, "DoNotShowFeedbackNotifications", 1);
            D(l, cTel, "adid", Tr.S("Рекламный идентификатор", "Advertising ID"),
              Tr.S("Уникальный ID, по которому приложения из Store показывают персональную рекламу.", "A unique ID Store apps use for personalised ads."),
              Tr.S("Реклама в приложениях перестаёт следить за вами между приложениями.", "In-app ads stop tracking you across apps."),
              Tr.S("Реклама останется, просто менее «личная».", "Ads remain, just less “personal”."),
              1, true, false)
                .Reg("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo", "Enabled", 0)
                .Reg("HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\AdvertisingInfo", "DisabledByGroupPolicy", 1);
            D(l, cTel, "tailored", Tr.S("Индивидуальные рекомендации по диагностике", "Tailored experiences"),
              Tr.S("Советы и реклама, подобранные по вашим диагностическим данным.", "Tips and ads picked from your diagnostic data."),
              Tr.S("Диагностика не используется для рекламы.", "Diagnostics are no longer used for advertising."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false)
                .Reg("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)
                .Reg("HKCU", "Software\\Policies\\Microsoft\\Windows\\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1);
            D(l, cTel, "activity", Tr.S("Журнал действий (Timeline)", "Activity history (Timeline)"),
              Tr.S("Windows ведёт историю открытых документов и сайтов и может отправлять её в учётную запись Microsoft.", "Windows keeps a history of opened documents and sites and may upload it to your Microsoft account."),
              Tr.S("История не собирается и не уходит в облако.", "No history is collected or uploaded."),
              Tr.S("Список недавних файлов в Пуске и переключение между устройствами станут беднее.", "Recent-files lists in Start and cross-device resume get poorer."),
              1, true, false)
                .Reg("HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\System", "EnableActivityFeed", 0)
                .Reg("HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\System", "PublishUserActivities", 0)
                .Reg("HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\System", "UploadUserActivities", 0);
            D(l, cTel, "inking", Tr.S("Сбор данных рукописного ввода и набора", "Inking and typing data collection"),
              Tr.S("Персонализация распознавания: Windows сохраняет образцы набора и контакты для словаря.", "Recognition personalisation: Windows keeps typing samples and contacts for its dictionary."),
              Tr.S("Образцы текста не хранятся и не отправляются.", "Text samples are neither stored nor sent."),
              Tr.S("Подсказки при наборе и распознавание рукописного текста чуть хуже подстраиваются под вас.", "Typing suggestions and handwriting recognition adapt to you a bit less."),
              1, true, false)
                .Reg("HKCU", "Software\\Microsoft\\InputPersonalization", "RestrictImplicitInkCollection", 1)
                .Reg("HKCU", "Software\\Microsoft\\InputPersonalization", "RestrictImplicitTextCollection", 1)
                .Reg("HKCU", "Software\\Microsoft\\InputPersonalization\\TrainedDataStore", "HarvestContacts", 0)
                .Reg("HKCU", "Software\\Microsoft\\Personalization\\Settings", "AcceptedPrivacyPolicy", 0);
            D(l, cTel, "wer", Tr.S("Отчёты об ошибках (Windows Error Reporting)", "Windows Error Reporting"),
              Tr.S("При сбое программы Windows собирает дамп и отправляет отчёт в Microsoft.", "On a crash Windows collects a dump and sends a report to Microsoft."),
              Tr.S("Нет окна «программа перестала работать» с ожиданием отправки; ничего не уходит наружу.", "No “program stopped working” dialog waiting to upload; nothing leaves the PC."),
              Tr.S("Разработчикам полезны локальные дампы WER; часть решений «проблема исправлена обновлением» приходит именно через отчёты.", "Developers rely on local WER dumps; some “fixed by an update” solutions arrive through these reports."),
              0, false, false)
                .Reg("HKLM", "SOFTWARE\\Microsoft\\Windows\\Windows Error Reporting", "Disabled", 1)
                .Reg("HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Error Reporting", "Disabled", 1)
                .Task("\\Microsoft\\Windows\\Windows Error Reporting\\QueueReporting")
                .Svc("WerSvc");

            // ---------- Реклама и подсказки ----------
            D(l, cAds, "startsugg", Tr.S("Рекомендации и реклама в «Пуске»", "Suggestions and ads in Start"),
              Tr.S("Раздел «Рекомендуем» показывает продвигаемые приложения и сайты рядом с вашими файлами.", "The “Recommended” area shows promoted apps and sites next to your files."),
              Tr.S("В Пуске остаются только ваши приложения и файлы.", "Start shows only your apps and files."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false)
                .Reg("HKCU", CDM, "SubscribedContent-338388Enabled", 0).Reg("HKCU", CDM, "SystemPaneSuggestionsEnabled", 0)
                .Reg("HKCU", CDM, "SubscribedContent-353698Enabled", 0).Reg("HKCU", ExplorerAdv, "Start_IrisRecommendations", 0);
            D(l, cAds, "tips", Tr.S("Советы и подсказки Windows", "Windows tips and suggestions"),
              Tr.S("Уведомления «попробуйте эту функцию» и подсказки при использовании Windows.", "“Try this feature” notifications and usage tips."),
              Tr.S("Меньше уведомлений-баннеров.", "Fewer banner notifications."),
              Tr.S("Новичку подсказки иногда полезны.", "Beginners occasionally find the tips useful."),
              1, true, false)
                .Reg("HKCU", CDM, "SubscribedContent-338389Enabled", 0).Reg("HKCU", CDM, "SoftLandingEnabled", 0)
                .Reg("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings\\Windows.SystemToast.Suggested", "Enabled", 0);
            D(l, cAds, "lockscreen", Tr.S("Подсказки и факты на экране блокировки", "Lock-screen tips and fun facts"),
              Tr.S("Тексты и ссылки поверх картинки Windows Spotlight на экране блокировки.", "Texts and links over the Windows Spotlight picture on the lock screen."),
              Tr.S("Картинки Spotlight остаются, реклама поверх них — нет.", "Spotlight pictures stay, the overlay ads don't."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false)
                .Reg("HKCU", CDM, "RotatingLockScreenOverlayEnabled", 0).Reg("HKCU", CDM, "SubscribedContent-338387Enabled", 0);
            D(l, cAds, "settingsads", Tr.S("Реклама в «Параметрах»", "Ads in Settings"),
              Tr.S("Баннеры Microsoft 365, OneDrive и Game Pass на страницах Параметров.", "Microsoft 365, OneDrive and Game Pass banners on Settings pages."),
              Tr.S("Страницы Параметров без рекламы.", "Settings pages without ads."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false)
                .Reg("HKCU", CDM, "SubscribedContent-338393Enabled", 0).Reg("HKCU", CDM, "SubscribedContent-353694Enabled", 0).Reg("HKCU", CDM, "SubscribedContent-353696Enabled", 0);
            D(l, cAds, "silentinstall", Tr.S("Автоустановка рекомендуемых приложений", "Automatic install of suggested apps"),
              Tr.S("Windows сама ставит продвигаемые приложения и игры (Candy Crush, TikTok…) после обновлений и на новые учётные записи.", "Windows installs promoted apps and games (Candy Crush, TikTok…) on its own after updates and for new accounts."),
              Tr.S("Заглушки не появляются снова после того, как вы их удалили.", "Stub apps stop coming back after you remove them."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false)
                .Reg("HKCU", CDM, "SilentInstalledAppsEnabled", 0).Reg("HKCU", CDM, "PreInstalledAppsEnabled", 0)
                .Reg("HKCU", CDM, "OemPreInstalledAppsEnabled", 0).Reg("HKCU", CDM, "PreInstalledAppsEverEnabled", 0);
            D(l, cAds, "welcome", Tr.S("«Добро пожаловать» после обновлений", "Welcome experience after updates"),
              Tr.S("Страница с рекламой новых функций после входа, когда Windows обновилась.", "The page advertising new features after sign-in once Windows has updated."),
              Tr.S("Вход без рекламной страницы.", "Sign-in without the promo page."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false)
                .Reg("HKCU", CDM, "SubscribedContent-310093Enabled", 0);
            D(l, cAds, "scoobe", Tr.S("Напоминание «Завершите настройку устройства»", "“Finish setting up your device” nag"),
              Tr.S("Периодический экран, предлагающий включить OneDrive, Microsoft 365 и вход через Microsoft.", "The recurring screen pushing OneDrive, Microsoft 365 and Microsoft sign-in."),
              Tr.S("Экран больше не появляется.", "The screen stops appearing."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false)
                .Reg("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0);
            D(l, cAds, "explorerads", Tr.S("Реклама OneDrive в Проводнике", "OneDrive ads in File Explorer"),
              Tr.S("«Уведомления поставщика синхронизации» — баннеры Microsoft 365 внутри окна Проводника.", "“Sync provider notifications” — Microsoft 365 banners inside the Explorer window."),
              Tr.S("Проводник без баннеров.", "Explorer without banners."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false)
                .Reg("HKCU", ExplorerAdv, "ShowSyncProviderNotifications", 0);
            D(l, cAds, "accountnotif", Tr.S("Уведомления учётной записи в «Пуске»", "Account notifications in Start"),
              Tr.S("Напоминания у аватара в Пуске: «сделайте резервную копию», «подтвердите учётную запись».", "Prompts at the avatar in Start: “back up your PC”, “verify your account”."),
              Tr.S("Аватар без бейджей-напоминаний.", "The avatar without nag badges."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false)
                .Reg("HKCU", ExplorerAdv, "Start_AccountNotifications", 0);
            D(l, cAds, "bingsearch", Tr.S("Поиск Bing в «Пуске»", "Bing web search in Start"),
              Tr.S("Строка поиска показывает результаты из интернета (Bing) вперемешку с локальными.", "The search box mixes Bing web results with local ones."),
              Tr.S("Поиск быстрее, только ваши файлы и программы, ничего не уходит в Bing.", "Faster search, only your files and programs, nothing goes to Bing."),
              Tr.S("Быстрый веб-поиск из Пуска пропадает.", "Quick web search from Start goes away."),
              1, true, false)
                .Reg("HKCU", "Software\\Policies\\Microsoft\\Windows\\Explorer", "DisableSearchBoxSuggestions", 1)
                .Reg("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\Search", "BingSearchEnabled", 0);
            D(l, cAds, "highlights", Tr.S("«Основные моменты» в поиске", "Search highlights"),
              Tr.S("Картинки и «интересные факты» в окне поиска на панели задач.", "Pictures and “fun facts” in the taskbar search window."),
              Tr.S("Окно поиска без развлекательного контента.", "The search window without entertainment content."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false)
                .Reg("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\SearchSettings", "IsDynamicSearchBoxEnabled", 0);

            // ---------- Виджеты ----------
            D(l, cWid, "widgets", Tr.S("Виджеты и лента новостей", "Widgets and the news feed"),
              Tr.S("Панель Win+W с погодой, новостями MSN и рекламой; фоновый процесс Widgets.exe.", "The Win+W panel with weather, MSN news and ads; the Widgets.exe background process."),
              Tr.S("Кнопка погоды и фоновый процесс исчезают, меньше памяти и трафика.", "The weather button and the background process disappear; less memory and traffic."),
              Tr.S("Виджеты погоды/календаря пропадают. «Выключить» прячет панель политикой, «Удалить» сносит пакет Windows Web Experience.", "Weather/calendar widgets are gone. “Disable” hides the panel by policy, “Remove” uninstalls the Windows Web Experience pack."),
              1, true, false)
                .Reg("HKLM", "SOFTWARE\\Policies\\Microsoft\\Dsh", "AllowNewsAndInterests", 0).Reg("HKCU", ExplorerAdv, "TaskbarDa", 0)
                .Appx("MicrosoftWindows.Client.WebExperience", false).Appx("Microsoft.WidgetsPlatformRuntime", false);
            D(l, cWid, "bingnews", Tr.S("Новости (Microsoft News / Bing News)", "News (Microsoft News / Bing News)"),
              Tr.S("Новостное приложение MSN.", "The MSN news app."),
              Tr.S("Минус приложение, которым мало кто пользуется.", "One rarely used app fewer."),
              Tr.S("Ничем; ставится заново из Store.", "Nothing; reinstalls from the Store."),
              2, true, false).Appx("Microsoft.BingNews");
            D(l, cWid, "bingweather", Tr.S("Погода (MSN Weather)", "Weather (MSN Weather)"),
              Tr.S("Приложение погоды MSN.", "The MSN weather app."),
              Tr.S("Минус приложение; погода есть в виджетах и любом браузере.", "One app fewer; weather is in widgets and any browser."),
              Tr.S("Кто открывает именно его — потеряет; ставится заново из Store.", "Anyone who opens this one loses it; reinstalls from the Store."),
              0, false, false).Appx("Microsoft.BingWeather");
            D(l, cWid, "bingsearchapp", Tr.S("Приложение Bing Search", "Bing Search app"),
              Tr.S("Веб-поиск Bing, встроенный в Windows 11 как отдельный пакет.", "Bing web search shipped in Windows 11 as a separate package."),
              Tr.S("Ещё один канал Bing исчезает.", "One more Bing channel gone."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Appx("Microsoft.BingSearch");
            D(l, cWid, "bingfinance", Tr.S("MSN Финансы и Спорт", "MSN Money and Sports"),
              Tr.S("Старые приложения MSN из Windows 10.", "The old MSN apps from Windows 10."),
              Tr.S("Минус два ненужных приложения.", "Two unneeded apps fewer."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Appx("Microsoft.BingFinance").Appx("Microsoft.BingSports");

            // ---------- Предустановленные приложения ----------
            D(l, cApps, "gethelp", Tr.S("Техническая поддержка (Get Help)", "Get Help"),
              Tr.S("Окно поддержки Microsoft с чат-ботом.", "Microsoft's support window with a chat bot."),
              Tr.S("Минус приложение.", "One app fewer."),
              Tr.S("Некоторые средства устранения неполадок в Параметрах открывают именно его.", "Some troubleshooters in Settings open this app."),
              2, true, false).Appx("Microsoft.GetHelp");
            D(l, cApps, "tipsapp", Tr.S("Советы (Tips)", "Tips"),
              Tr.S("Приложение с обучающими подсказками по Windows.", "The app with Windows how-to tips."),
              Tr.S("Минус приложение.", "One app fewer."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Appx("Microsoft.Getstarted");
            D(l, cApps, "feedbackhub", Tr.S("Центр отзывов (Feedback Hub)", "Feedback Hub"),
              Tr.S("Приложение для отправки отзывов и ошибок в Microsoft.", "The app for sending feedback and bug reports to Microsoft."),
              Tr.S("Минус приложение.", "One app fewer."),
              Tr.S("Участникам Insider оно нужно; остальным — нет.", "Insiders need it; everyone else doesn't."),
              2, true, false).Appx("Microsoft.WindowsFeedbackHub");
            D(l, cApps, "solitaire", Tr.S("Microsoft Solitaire Collection", "Microsoft Solitaire Collection"),
              Tr.S("Пасьянсы с рекламой и подпиской.", "Solitaire games with ads and a subscription."),
              Tr.S("Минус игра с рекламой.", "One ad-laden game fewer."),
              Tr.S("Кто раскладывает пасьянс — поставит заново из Store.", "Solitaire fans reinstall it from the Store."),
              2, true, false).Appx("Microsoft.MicrosoftSolitaireCollection");
            D(l, cApps, "officehub", Tr.S("Microsoft 365 (Office Hub)", "Microsoft 365 (Office Hub)"),
              Tr.S("Рекламная витрина Microsoft 365, а не сам Office.", "The Microsoft 365 storefront, not Office itself."),
              Tr.S("Минус реклама подписки.", "One subscription ad fewer."),
              Tr.S("Ничем: установленный Office продолжит работать.", "Nothing: an installed Office keeps working."),
              2, true, false).Appx("Microsoft.MicrosoftOfficeHub");
            D(l, cApps, "skype", "Skype",
              Tr.S("Мессенджер, закрытый Microsoft в мае 2025 года.", "The messenger Microsoft shut down in May 2025."),
              Tr.S("Мёртвое приложение.", "A dead app."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Appx("Microsoft.SkypeApp");
            D(l, cApps, "mixedreality", Tr.S("Портал смешанной реальности", "Mixed Reality Portal"),
              Tr.S("Оболочка для гарнитур Windows Mixed Reality, снятых с поддержки.", "The shell for the discontinued Windows Mixed Reality headsets."),
              Tr.S("Мёртвая платформа.", "A dead platform."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Appx("Microsoft.MixedReality.Portal");
            D(l, cApps, "3d", Tr.S("3D Viewer, Paint 3D, Print 3D", "3D Viewer, Paint 3D, Print 3D"),
              Tr.S("Трио 3D-приложений эпохи Windows 10; Microsoft убрала их из Store.", "The Windows 10-era 3D trio; Microsoft pulled them from the Store."),
              Tr.S("Минус три приложения.", "Three apps fewer."),
              Tr.S("Кто редактирует 3D-модели в Paint 3D — потеряет его.", "Anyone editing 3D models in Paint 3D loses it."),
              2, true, false).Appx("Microsoft.Microsoft3DViewer").Appx("Microsoft.MSPaint").Appx("Microsoft.Print3D");
            D(l, cApps, "wallet", Tr.S("Кошелёк (Wallet)", "Wallet"),
              Tr.S("Приложение платежей Microsoft, закрытое в 2019 году.", "Microsoft's payment app, closed in 2019."),
              Tr.S("Мёртвое приложение.", "A dead app."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Appx("Microsoft.Wallet");
            D(l, cApps, "people", Tr.S("Люди (People)", "People"),
              Tr.S("Адресная книга, замещённая Outlook и Phone Link.", "The address book superseded by Outlook and Phone Link."),
              Tr.S("Минус приложение.", "One app fewer."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Appx("Microsoft.People");
            D(l, cApps, "maps", Tr.S("Карты и офлайн-карты", "Maps and offline maps"),
              Tr.S("Приложение «Карты», служба загрузки офлайн-карт и её задачи.", "The Maps app, the offline-maps download service and its tasks."),
              Tr.S("Минус приложение и фоновая служба.", "One app and one background service fewer."),
              Tr.S("Кто пользуется офлайн-картами Windows — потеряет.", "Anyone using Windows offline maps loses them."),
              2, true, false).Appx("Microsoft.WindowsMaps").Svc("MapsBroker")
                .Task("\\Microsoft\\Windows\\Maps\\MapsToastTask").Task("\\Microsoft\\Windows\\Maps\\MapsUpdateTask");
            D(l, cApps, "teamsold", Tr.S("Чат Teams (старый, 2022)", "Teams chat (old, 2022)"),
              Tr.S("Личный чат Teams из Windows 11 22H2 — заменён новым Teams.", "The personal Teams chat from Windows 11 22H2 — replaced by the new Teams."),
              Tr.S("Мёртвое приложение.", "A dead app."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Appx("MicrosoftTeams");
            D(l, cApps, "powerautomate", "Power Automate",
              Tr.S("Конструктор автоматизаций (RPA) для бизнеса.", "The business automation (RPA) designer."),
              Tr.S("Минус тяжёлое приложение, которым дома почти не пользуются.", "One heavy app fewer, rarely used at home."),
              Tr.S("Кто строит сценарии Power Automate — поставит заново из Store.", "Power Automate users reinstall it from the Store."),
              // без галочки: живой инструмент, у кого-то в нём рабочие потоки
              2, false, false).Appx("Microsoft.PowerAutomateDesktop");
            D(l, cApps, "devhome", "Dev Home",
              Tr.S("Панель разработчика, которую Microsoft прекратила развивать в 2025 году.", "The developer dashboard Microsoft stopped developing in 2025."),
              Tr.S("Мёртвое приложение.", "A dead app."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Appx("Microsoft.Windows.DevHome");
            D(l, cApps, "family", Tr.S("Microsoft Family", "Microsoft Family"),
              Tr.S("Приложение семейной безопасности (родительский контроль).", "The Family Safety (parental controls) app."),
              Tr.S("Минус приложение.", "One app fewer."),
              Tr.S("Семьи с детскими учётными записями пользуются им.", "Families with child accounts use it."),
              0, false, false).Appx("MicrosoftCorporationII.MicrosoftFamily");
            D(l, cApps, "linkedin", "LinkedIn",
              Tr.S("Веб-обёртка LinkedIn, предустановленная на некоторых ПК.", "The LinkedIn web wrapper preinstalled on some PCs."),
              Tr.S("Минус заглушка; сайт работает в браузере.", "One stub fewer; the site works in a browser."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Appx("7EE7776C.LinkedInforWindows");
            D(l, cApps, "clipchamp", "Clipchamp",
              Tr.S("Онлайн-видеоредактор Microsoft с подпиской.", "Microsoft's online video editor with a subscription."),
              Tr.S("Минус тяжёлое приложение и реклама подписки.", "One heavy app and its subscription ads fewer."),
              Tr.S("Кто монтирует видео в Clipchamp — потеряет; ставится заново из Store.", "Clipchamp editors lose it; reinstalls from the Store."),
              0, false, false).Appx("Clipchamp.Clipchamp");
            D(l, cApps, "outlooknew", Tr.S("Outlook (новый)", "Outlook (new)"),
              Tr.S("Новый почтовый клиент Outlook для Windows.", "The new Outlook mail client for Windows."),
              Tr.S("Минус приложение, если почта в браузере или другом клиенте.", "One app fewer if your mail lives in a browser or another client."),
              Tr.S("Кто читает почту в нём — потеряет; ставится заново из Store.", "Anyone reading mail in it loses it; reinstalls from the Store."),
              0, false, false).Appx("Microsoft.OutlookForWindows");
            D(l, cApps, "mail", Tr.S("Почта и Календарь (старые)", "Mail and Calendar (old)"),
              Tr.S("Старые приложения Windows 10, снятые с поддержки в 2024 году.", "The old Windows 10 apps retired in 2024."),
              Tr.S("Мёртвые приложения.", "Dead apps."),
              Tr.S("Ничем; замена — новый Outlook.", "Nothing; the new Outlook replaces them."),
              2, true, false).Appx("microsoft.windowscommunicationsapps");
            D(l, cApps, "todo", "Microsoft To Do",
              Tr.S("Список задач Microsoft.", "Microsoft's task list."),
              Tr.S("Минус приложение.", "One app fewer."),
              Tr.S("Кто ведёт в нём задачи — потеряет; ставится заново из Store.", "Anyone keeping tasks in it loses it; reinstalls from the Store."),
              0, false, false).Appx("Microsoft.Todos");
            D(l, cApps, "stickynotes", Tr.S("Записки (Sticky Notes)", "Sticky Notes"),
              Tr.S("Заметки-стикеры на рабочем столе.", "Desktop sticky notes."),
              Tr.S("Минус приложение.", "One app fewer."),
              Tr.S("Стикеры на рабочем столе пропадут вместе с ним (они синхронизированы с учётной записью).", "Your desktop notes go with it (they are synced to the account)."),
              0, false, false).Appx("Microsoft.MicrosoftStickyNotes");
            D(l, cApps, "onenote", Tr.S("OneNote для Windows 10", "OneNote for Windows 10"),
              Tr.S("Старая версия OneNote из Store, снятая с поддержки в 2025 году.", "The old Store version of OneNote, retired in 2025."),
              Tr.S("Мёртвое приложение; десктопный OneNote остаётся.", "A dead app; desktop OneNote stays."),
              Tr.S("Ничем, если стоит обычный OneNote из Office.", "Nothing if the regular Office OneNote is installed."),
              // без галочки: поддержка закончилась, но приложение работает и в нём чьи-то заметки
              2, false, false).Appx("Microsoft.Office.OneNote");
            D(l, cApps, "media", Tr.S("Будильники, Камера, Запись звука, Медиаплеер, Кино и ТВ", "Alarms, Camera, Sound Recorder, Media Player, Movies & TV"),
              Tr.S("Стандартные приложения-утилиты Windows.", "The standard Windows utility apps."),
              Tr.S("Место и меньше пунктов в Пуске.", "Space and a shorter Start list."),
              Tr.S("Без Камеры не работает веб-камера в некоторых сценариях, без Медиаплеера — двойной клик по музыке. Только если есть замены.", "Without Camera some webcam scenarios break, without Media Player double-clicking music does nothing. Only if you have replacements."),
              0, false, true).Appx("Microsoft.WindowsAlarms").Appx("Microsoft.WindowsCamera").Appx("Microsoft.WindowsSoundRecorder").Appx("Microsoft.ZuneMusic").Appx("Microsoft.ZuneVideo");
            D(l, cApps, "quickassist", Tr.S("Быстрая помощь (Quick Assist)", "Quick Assist"),
              Tr.S("Удалённая помощь через Microsoft: кто-то подключается к вашему экрану по коду.", "Remote help via Microsoft: someone connects to your screen with a code."),
              Tr.S("Одним каналом удалённого доступа меньше (его используют и мошенники «из техподдержки»).", "One remote-access channel fewer (also abused by “tech support” scammers)."),
              Tr.S("Если вам помогают удалённо через Quick Assist — оставьте.", "Keep it if someone helps you remotely through Quick Assist."),
              0, false, false).Appx("MicrosoftCorporationII.QuickAssist").Cap("App.Support.QuickAssist");
            D(l, cApps, "whiteboard", "Microsoft Whiteboard",
              Tr.S("Совместная доска для рисования.", "The collaborative drawing board."),
              Tr.S("Минус приложение.", "One app fewer."),
              Tr.S("Ничем; ставится заново из Store.", "Nothing; reinstalls from the Store."),
              2, true, false).Appx("Microsoft.Whiteboard");

            // ---------- Сторонние заглушки ----------
            D(l, cThird, "spotify", "Spotify",
              Tr.S("Заглушка Spotify, предустановленная Windows.", "The Spotify stub preinstalled by Windows."),
              Tr.S("Минус заглушка.", "One stub fewer."),
              Tr.S("Кто слушает Spotify через это приложение — поставит из Store или с сайта; офлайн-загрузки Premium при этом пропадут.", "Spotify listeners reinstall from the Store or the website; Premium offline downloads are lost."),
              // без галочки: у многих это не заглушка, а рабочее приложение с офлайн-музыкой
              2, false, false).Appx("SpotifyAB.SpotifyMusic");
            D(l, cThird, "socialstubs", Tr.S("TikTok, Instagram, Facebook, Twitter", "TikTok, Instagram, Facebook, Twitter"),
              Tr.S("Веб-обёртки социальных сетей из «рекомендуемых» приложений.", "Social-network web wrappers from the “suggested” apps."),
              Tr.S("Минус заглушки; сайты работают в браузере.", "Stubs gone; the sites work in a browser."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Appx("BytedancePte.Ltd.TikTok").Appx("Facebook.InstagramBeta").Appx("Facebook.Facebook").Appx("9E2F88E3.Twitter");
            D(l, cThird, "videostubs", Tr.S("Disney+, Netflix, Prime Video, Hulu", "Disney+, Netflix, Prime Video, Hulu"),
              Tr.S("Заглушки стриминговых сервисов.", "Streaming-service stubs."),
              Tr.S("Минус заглушки.", "Stubs gone."),
              Tr.S("Кто смотрит через приложение (офлайн-загрузки) — поставит заново из Store.", "Anyone watching through the app (offline downloads) reinstalls from the Store."),
              // без галочки: офлайн-загрузки фильмов живут только внутри приложения
              2, false, false).Appx("Disney.37853FC22B2CE").Appx("4DF9E0F8.Netflix").Appx("AmazonVideo.PrimeVideo").Appx("HULULLC.HULUPLUS");
            D(l, cThird, "gamestubs", Tr.S("Игры-заглушки (Candy Crush и подобные)", "Game stubs (Candy Crush and friends)"),
              Tr.S("Candy Crush, Bubble Witch, March of Empires, Asphalt, Cooking Fever, Royal Revolt, Hidden City…", "Candy Crush, Bubble Witch, March of Empires, Asphalt, Cooking Fever, Royal Revolt, Hidden City…"),
              Tr.S("Минус реклама, приходящая под видом игр.", "Ads disguised as games are gone."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false)
                .Appx("king.com.CandyCrushSaga").Appx("king.com.CandyCrushFriends").Appx("king.com.CandyCrushSodaSaga").Appx("king.com.BubbleWitch3Saga")
                .Appx("A278AB0D.MarchofEmpires").Appx("A278AB0D.DisneyMagicKingdoms").Appx("828B5831.HiddenCityMysteryofShadows")
                .Appx("GAMELOFTSA.Asphalt8Airborne").Appx("NORDCURRENT.COOKINGFEVER").Appx("flaregamesGmbH.RoyalRevolt2").Appx("26720RandomSaladGamesLLC.SimpleSolitaire");
            D(l, cThird, "mcafee", Tr.S("McAfee (заглушка)", "McAfee (stub)"),
              Tr.S("Рекламный модуль антивируса McAfee из Store.", "The McAfee antivirus promo module from the Store."),
              Tr.S("Минус реклама антивируса.", "One antivirus ad fewer."),
              Tr.S("Полноценный McAfee, если он установлен, удаляется через «Программы», не здесь.", "A full McAfee, if installed, is removed via “Programs”, not here."),
              2, true, false).Appx("5A894077.McAfeeSecurity");
            // Keeper (менеджер паролей) отсюда убран: удалять по умолчанию хранилище паролей нельзя, даже облачное
            D(l, cThird, "otherstubs", Tr.S("Прочие заглушки (PicsArt, Duolingo, Pandora, Plex, Adobe Express…)", "Other stubs (PicsArt, Duolingo, Pandora, Plex, Adobe Express…)"),
              Tr.S("Остальные «рекомендуемые» приложения, которые ставятся без спроса.", "The remaining “suggested” apps installed without asking."),
              Tr.S("Минус заглушки.", "Stubs gone."),
              Tr.S("Кто пользуется каким-то из них — поставит заново из Store.", "Anyone using one of them reinstalls it from the Store."),
              2, true, false)
                .Appx("2FE3CB00.PicsArt-PhotoStudio").Appx("D5EA27B7.Duolingo-LearnLanguagesforFree").Appx("PandoraMediaInc.29680B314EFC2")
                .Appx("46928bounde.EclipseManager").Appx("ActiproSoftwareLLC.562882FEEB491").Appx("Flipboard.Flipboard").Appx("ShazamEntertainmentLtd.Shazam")
                .Appx("TheNewYorkTimes.NYTCrossword").Appx("AdobeSystemsIncorporated.AdobePhotoshopExpress")
                .Appx("Drawboard.DrawboardPDF").Appx("Fitbit.FitbitCoach").Appx("CAF9E577.Plex");

            // ---------- Xbox ----------
            D(l, cXbox, "gamebar", Tr.S("Xbox Game Bar", "Xbox Game Bar"),
              Tr.S("Оверлей Win+G: запись экрана, счётчик FPS, чат Xbox, виджеты производительности.", "The Win+G overlay: screen recording, FPS counter, Xbox chat, performance widgets."),
              Tr.S("Не висит фоном в играх, не перехватывает Win+G и кнопку Xbox на геймпаде; на слабых ПК чуть больше FPS.", "No background overlay in games, no Win+G / Xbox-button hijack; slightly more FPS on weak PCs."),
              Tr.S("⚠ Пропадает запись игр и «последние 30 секунд», счётчик FPS, чат Game Pass; часть игр и Xbox-приложение зовут его для наложений. «Выключить» отключает оверлей политикой и оставляет приложение; «Удалить» сносит пакет.", "⚠ Game recording and “last 30 seconds”, the FPS counter and Game Pass chat are gone; some games and the Xbox app call it for overlays. “Disable” turns the overlay off by policy and keeps the app; “Remove” uninstalls the package."),
              // Отмечен по умолчанию: запись и счётчик FPS даёт свой «Захват» и оверлей.
              0, true, true)
                .Reg("HKCU", "Software\\Microsoft\\GameBar", "UseNexusForGameBarEnabled", 0)
                .Reg("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR", "AppCaptureEnabled", 0)
                .Reg("HKCU", "System\\GameConfigStore", "GameDVR_Enabled", 0)
                .Appx("Microsoft.XboxGamingOverlay", false).Appx("Microsoft.XboxGameOverlay", false);
            D(l, cXbox, "gamedvr", Tr.S("Фоновая запись игр (Game DVR)", "Background game recording (Game DVR)"),
              Tr.S("Windows постоянно пишет последние секунды игры в буфер, чтобы их можно было сохранить.", "Windows continuously buffers the last seconds of gameplay so they can be saved."),
              Tr.S("Меньше нагрузки на диск и GPU во время игры.", "Less disk and GPU load while gaming."),
              Tr.S("⚠ Политика запрещает захват Game Bar целиком — «запись за последние 30 секунд» и запись по горячей клавише работать не будут.", "⚠ The policy blocks Game Bar capture entirely — “record last 30 seconds” and hotkey recording stop working."),
              0, false, true)
                .Reg("HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\GameDVR", "AllowGameDVR", 0);
            D(l, cXbox, "xboxapp", Tr.S("Приложение Xbox", "Xbox app"),
              Tr.S("Магазин и библиотека Xbox / PC Game Pass.", "The Xbox / PC Game Pass store and library."),
              Tr.S("Минус тяжёлое приложение, если вы не играете в игры Xbox/Game Pass.", "One heavy app fewer if you don't play Xbox/Game Pass games."),
              Tr.S("⚠ Без него не запустить игры Game Pass и не установить игры из магазина Xbox.", "⚠ Without it Game Pass games won't launch and Xbox store games can't be installed."),
              0, false, true).Appx("Microsoft.GamingApp");
            D(l, cXbox, "xboxidentity", Tr.S("Xbox Identity Provider", "Xbox Identity Provider"),
              Tr.S("Вход в учётную запись Xbox для игр.", "Xbox account sign-in for games."),
              Tr.S("Минус фоновый компонент.", "One background component fewer."),
              Tr.S("⚠ Minecraft, Forza, Sea of Thieves и другие игры Microsoft не смогут войти в аккаунт. Оставьте, если играете во что-то от Microsoft.", "⚠ Minecraft, Forza, Sea of Thieves and other Microsoft games can't sign in. Keep it if you play anything from Microsoft."),
              0, false, true).Appx("Microsoft.XboxIdentityProvider");
            D(l, cXbox, "xboxtcui", Tr.S("Xbox TCUI и голосовой оверлей", "Xbox TCUI and speech-to-text overlay"),
              Tr.S("Окна Xbox Live (друзья, достижения) и субтитры голосового чата для Game Bar.", "Xbox Live dialogs (friends, achievements) and voice-chat captions for Game Bar."),
              Tr.S("Минус два компонента, нужные только игрокам Xbox Live.", "Two components only Xbox Live players need."),
              Tr.S("Достижения и приглашения друзей в играх Microsoft перестанут показываться.", "Achievements and friend invites in Microsoft games stop appearing."),
              0, false, false).Appx("Microsoft.Xbox.TCUI").Appx("Microsoft.XboxSpeechToTextOverlay");
            D(l, cXbox, "gameassist", Tr.S("Edge Game Assist", "Edge Game Assist"),
              Tr.S("Браузер Edge внутри Game Bar с «подсказками к игре».", "An Edge browser inside Game Bar with “game tips”."),
              Tr.S("Минус ещё одно окно Edge в оверлее.", "One more Edge surface in the overlay gone."),
              Tr.S("Кто читает гайды не выходя из игры — потеряет.", "Anyone reading guides without leaving the game loses it."),
              1, false, false).Appx("Microsoft.Edge.GameAssist");
            D(l, cXbox, "xboxsvc", Tr.S("Службы Xbox Live", "Xbox Live services"),
              Tr.S("Четыре службы (аутентификация, сохранения, сеть, геймпад Xbox) и задача синхронизации сохранений.", "Four services (auth, saves, networking, Xbox controller) and the save-sync task."),
              Tr.S("Они и так запускаются только по требованию; отключение даёт почти ничего, кроме гарантии, что не стартуют.", "They already start on demand; disabling gains almost nothing except the guarantee they never start."),
              Tr.S("⚠ Игры Microsoft теряют облачные сохранения и вход; геймпад Xbox по USB может перестать работать (XboxGipSvc).", "⚠ Microsoft games lose cloud saves and sign-in; a USB Xbox controller may stop working (XboxGipSvc)."),
              0, false, true).Svc("XblAuthManager").Svc("XblGameSave").Svc("XboxNetApiSvc").Svc("XboxGipSvc").Task("\\Microsoft\\XblGameSave\\XblGameSaveTask");

            // ---------- Облако ----------
            D(l, cCloud, "onedrive", "OneDrive",
              Tr.S("Синхронизация папок Рабочий стол/Документы/Изображения с облаком Microsoft.", "Syncs Desktop/Documents/Pictures with Microsoft's cloud."),
              Tr.S("Нет фонового процесса и постоянной синхронизации; файлы остаются локально.", "No background process or constant sync; files stay local."),
              Tr.S("⚠ Если папки «Документы» и «Рабочий стол» перенесены в OneDrive, сначала верните их в Параметрах OneDrive, иначе файлы окажутся только в облаке. «Выключить» = остановить, запретить политикой и убрать из автозапуска; «Удалить» = деинсталлировать.", "⚠ If Documents and Desktop were moved into OneDrive, move them back in OneDrive settings first, or the files stay cloud-only. “Disable” = stop, block by policy, drop from startup; “Remove” = uninstall."),
              0, false, true).OneDrive().Appx("Microsoft.OneDriveSync", false);
            D(l, cCloud, "teams", Tr.S("Microsoft Teams", "Microsoft Teams"),
              Tr.S("Новый Teams (личный и рабочий) из Store.", "The new Teams (personal and work) from the Store."),
              Tr.S("Минус тяжёлое приложение и автозапуск, если Teams не нужен.", "One heavy app and its autostart fewer if you don't need Teams."),
              Tr.S("Кто созванивается в Teams — потеряет; рабочий Teams ставится заново администратором или из Store.", "Teams users lose it; the work Teams reinstalls via the admin or the Store."),
              0, false, false).Appx("MSTeams").Reg("HKCU", ExplorerAdv, "TaskbarMn", 0);
            D(l, cCloud, "phonelink", Tr.S("Связь с телефоном (Phone Link)", "Phone Link"),
              Tr.S("Уведомления, SMS и фото со смартфона на ПК; компонент «Мобильные устройства».", "Phone notifications, SMS and photos on the PC; the “Mobile devices” component."),
              Tr.S("Минус фоновое приложение и Bluetooth-связь, если телефон к ПК не привязан.", "One background app and Bluetooth link fewer if no phone is paired."),
              Tr.S("Кто читает SMS и уведомления на ПК — потеряет; ставится заново из Store.", "Anyone reading SMS and notifications on the PC loses it; reinstalls from the Store."),
              0, false, false).Appx("Microsoft.YourPhone").Appx("MicrosoftWindows.CrossDevice");

            // ---------- Службы ----------
            D(l, cSvc, "remotereg", Tr.S("Удалённый реестр (RemoteRegistry)", "Remote Registry"),
              Tr.S("Позволяет менять реестр этого ПК по сети.", "Lets the registry of this PC be edited over the network."),
              Tr.S("Закрыт один из классических каналов удалённого управления.", "One classic remote-control channel closed."),
              Tr.S("Нужен только администраторам доменной сети; в Windows 11 обычно уже отключён.", "Only domain administrators need it; usually already disabled in Windows 11."),
              1, true, false).Svc("RemoteRegistry");
            D(l, cSvc, "fax", Tr.S("Факс (служба и «Факсы и сканирование»)", "Fax (service and Fax & Scan)"),
              Tr.S("Отправка факсов через модем и старая программа сканирования.", "Fax sending via a modem and the old scanning app."),
              Tr.S("Минус служба и компонент.", "One service and one feature fewer."),
              Tr.S("Кто сканирует именно через «Факсы и сканирование» — потеряет.", "Anyone scanning specifically through Fax & Scan loses it."),
              1, true, false).Svc("Fax").Cap("Print.Fax.Scan");
            D(l, cSvc, "wmpnet", Tr.S("Общий доступ к медиа (WMPNetworkSvc)", "Media sharing (WMPNetworkSvc)"),
              Tr.S("Раздаёт медиатеку Windows Media Player по сети (DLNA).", "Shares the Windows Media Player library over the network (DLNA)."),
              Tr.S("Минус фоновая служба и открытый порт.", "One background service and open port fewer."),
              Tr.S("Кто стримит фильмы с ПК на телевизор через DLNA — потеряет.", "Anyone streaming films from the PC to a TV via DLNA loses it."),
              1, true, false).Svc("WMPNetworkSvc");
            D(l, cSvc, "retaildemo", Tr.S("Демо-режим магазина (RetailDemo)", "Retail demo (RetailDemo)"),
              Tr.S("Витринный режим для ПК в магазине.", "The showroom mode for PCs in shops."),
              Tr.S("Минус служба, бесполезная дома.", "A service useless at home."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false).Svc("RetailDemo");
            D(l, cSvc, "walletsvc", Tr.S("Служба кошелька (WalletService)", "Wallet service (WalletService)"),
              Tr.S("Служба закрытого приложения «Кошелёк».", "The service of the closed Wallet app."),
              Tr.S("Минус служба.", "One service fewer."),
              Tr.S("Ничем.", "Nothing."),
              1, true, false).Svc("WalletService");
            D(l, cSvc, "insider", Tr.S("Служба Windows Insider (wisvc)", "Windows Insider service (wisvc)"),
              Tr.S("Обслуживает участие в программе предварительной оценки.", "Serves the Windows Insider program."),
              Tr.S("Минус служба, если вы не Insider.", "One service fewer if you are not an Insider."),
              Tr.S("Участники Insider останутся без предварительных сборок.", "Insiders stop receiving preview builds."),
              1, false, false).Svc("wisvc");
            D(l, cSvc, "parental", Tr.S("Родительский контроль (WpcMonSvc)", "Parental controls (WpcMonSvc)"),
              Tr.S("Следит за ограничениями семейной безопасности для детских учётных записей.", "Enforces Family Safety limits for child accounts."),
              Tr.S("Минус служба, если детских учётных записей нет.", "One service fewer without child accounts."),
              Tr.S("Ограничения для детей перестанут действовать.", "Limits for children stop applying."),
              0, false, false).Svc("WpcMonSvc").Task("\\Microsoft\\Windows\\Shell\\FamilySafetyMonitor").Task("\\Microsoft\\Windows\\Shell\\FamilySafetyRefreshTask");
            D(l, cSvc, "geoloc", Tr.S("Геолокация (lfsvc)", "Geolocation (lfsvc)"),
              Tr.S("Определяет местоположение ПК для приложений и «Найти устройство».", "Provides the PC location to apps and “Find my device”."),
              Tr.S("Приложения не узнают, где вы.", "Apps can't learn where you are."),
              Tr.S("Погода, карты, часовой пояс по местоположению и «Найти устройство» перестанут работать.", "Weather, maps, location-based time zone and “Find my device” stop working."),
              0, false, false).Svc("lfsvc");
            D(l, cSvc, "wsearch", Tr.S("Индексирование поиска (Windows Search)", "Search indexing (Windows Search)"),
              Tr.S("Служба индексирует файлы и почту, чтобы поиск в Пуске и Проводнике был мгновенным.", "Indexes files and mail so Start and Explorer search are instant."),
              Tr.S("Нет фоновой нагрузки на диск при простое; на HDD это заметно.", "No idle disk load; noticeable on HDDs."),
              Tr.S("⚠ Поиск по содержимому файлов в Пуске/Проводнике и поиск в Outlook станут медленными или перестанут находить.", "⚠ Content search in Start/Explorer and Outlook search become slow or stop finding things."),
              0, false, true).Svc("WSearch");
            D(l, cSvc, "phonesvc", Tr.S("Служба телефонии (PhoneSvc)", "Phone service (PhoneSvc)"),
              Tr.S("Состояние звонков для Phone Link и приложений телефонии.", "Call state for Phone Link and telephony apps."),
              Tr.S("Минус служба.", "One service fewer."),
              Tr.S("Звонки через Phone Link перестанут работать.", "Calls through Phone Link stop working."),
              0, false, false).Svc("PhoneSvc");

            // ---------- Компоненты ----------
            D(l, cFeat, "ps2", Tr.S("Windows PowerShell 2.0", "Windows PowerShell 2.0"),
              Tr.S("Старый движок PowerShell без журналирования — им пользуются вредоносные скрипты, чтобы обойти защиту.", "The old PowerShell engine without logging — malware uses it to bypass protections."),
              Tr.S("Закрыта известная дыра; PowerShell 5.1 и 7 не затронуты.", "A known hole closed; PowerShell 5.1 and 7 are unaffected."),
              Tr.S("Единичные древние скрипты с «-Version 2» перестанут запускаться.", "Rare ancient scripts run with “-Version 2” stop working."),
              1, true, false).Feature("MicrosoftWindowsPowerShellV2Root", false).Feature("MicrosoftWindowsPowerShellV2", false);
            D(l, cFeat, "smb1", Tr.S("Протокол SMB 1.0", "SMB 1.0 protocol"),
              Tr.S("Сетевой протокол 1980-х, через который распространялся WannaCry.", "The 1980s network protocol WannaCry spread through."),
              Tr.S("Закрыта серьёзная уязвимость.", "A serious vulnerability closed."),
              Tr.S("⚠ Очень старые NAS, принтеры и роутеры с общими папками перестанут открываться.", "⚠ Very old NAS boxes, printers and routers with shared folders stop opening."),
              // без галочки: на современной Windows SMB1 выключен из коробки, и если он включён —
              // его включали руками ради старого NAS или принтера
              1, false, true).Feature("SMB1Protocol", false);
            D(l, cFeat, "xps", Tr.S("Службы XPS", "XPS services"),
              Tr.S("Печать в формат XPS — предшественник PDF, которым никто не пользуется.", "Printing to XPS — a PDF predecessor nobody uses."),
              Tr.S("Минус компонент и виртуальный принтер.", "One feature and one virtual printer fewer."),
              Tr.S("Ничем; PDF-принтер остаётся.", "Nothing; the PDF printer stays."),
              1, true, false).Feature("Printing-XPSServices-Features", false);
            D(l, cFeat, "workfolders", Tr.S("Рабочие папки (Work Folders)", "Work Folders"),
              Tr.S("Синхронизация с корпоративным файловым сервером.", "Sync with a corporate file server."),
              Tr.S("Минус компонент, нужный только в доменной сети.", "A feature only domain networks need."),
              Tr.S("Сотрудники, у которых настроены Рабочие папки, потеряют синхронизацию.", "Employees with Work Folders configured lose sync."),
              1, true, false).Feature("WorkFolders-Client", false);
            D(l, cFeat, "wmp", Tr.S("Проигрыватель Windows Media (старый)", "Windows Media Player (legacy)"),
              Tr.S("Классический WMP из Windows 7.", "The classic WMP from Windows 7."),
              Tr.S("Минус компонент.", "One feature fewer."),
              Tr.S("Кто слушает CD и старые медиатеки именно в нём — потеряет.", "Anyone playing CDs and old libraries in it loses it."),
              0, false, false).Feature("WindowsMediaPlayer", false);
            D(l, cFeat, "stepsrecorder", Tr.S("Средство записи действий (Steps Recorder)", "Steps Recorder"),
              Tr.S("Утилита, снятая с поддержки в 2024 году.", "A utility retired in 2024."),
              Tr.S("Мёртвый компонент.", "A dead feature."),
              Tr.S("Ничем.", "Nothing."),
              2, true, false).Cap("App.StepsRecorder");
            D(l, cFeat, "wordpad", "WordPad",
              Tr.S("Редактор, убранный из Windows 11 24H2.", "The editor removed from Windows 11 24H2."),
              Tr.S("Мёртвый компонент.", "A dead feature."),
              Tr.S("Открыть .rtf двойным кликом станет нечем, если нет Word/LibreOffice.", "Nothing opens .rtf on double-click without Word/LibreOffice."),
              2, true, false).Cap("Microsoft.Windows.WordPad");
            D(l, cFeat, "mathrec", Tr.S("Распознаватель математики", "Math Recognizer"),
              Tr.S("Панель ввода формул от руки.", "The handwriting maths input panel."),
              Tr.S("Минус компонент.", "One feature fewer."),
              Tr.S("Ничем, если не пишете формулы стилусом.", "Nothing unless you write formulas with a stylus."),
              2, true, false).Cap("MathRecognizer");
            D(l, cFeat, "psise", Tr.S("PowerShell ISE", "PowerShell ISE"),
              Tr.S("Старая среда редактирования скриптов PowerShell.", "The old PowerShell script editor."),
              Tr.S("Минус компонент; VS Code его заменил.", "One feature fewer; VS Code replaced it."),
              Tr.S("Кто правит скрипты в ISE — потеряет.", "Anyone editing scripts in ISE loses it."),
              0, false, false).Cap("Microsoft.Windows.PowerShell.ISE");
            D(l, cFeat, "iemode", Tr.S("Internet Explorer (файлы режима IE)", "Internet Explorer (IE mode files)"),
              Tr.S("Остатки IE 11, нужные режиму Internet Explorer в Edge.", "The IE 11 remnants Edge's Internet Explorer mode needs."),
              Tr.S("Минус старый движок.", "One old engine fewer."),
              Tr.S("⚠ Режим IE в Edge (старые корпоративные и банковские сайты) перестанет работать.", "⚠ IE mode in Edge (old corporate and banking sites) stops working."),
              0, false, true).Cap("Browser.InternetExplorer");

            // ---------- PowerToys ----------
            string ptp;
            Dictionary<string, bool> pt = ReadPowerToys(out ptp);
            if (pt != null)
            {
                List<string> keys = new List<string>(pt.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string k in keys)
                {
                    string[] d = PowerToysModuleText(k);
                    D(l, cPt, "pt:" + k, d[0], d[1],
                      Tr.S("Модуль не грузится вместе с PowerToys: меньше памяти и одна фоновая горячая клавиша меньше. Действие то же, что переключатель в настройках PowerToys, применяется сразу.",
                           "The module no longer loads with PowerToys: less memory and one background hotkey fewer. Same as the toggle in PowerToys settings, applied immediately."),
                      d[2], 0, !PowerToysKeptByDefault(k), false).Pt(k);
                }
            }
            else
            {
                D(l, cPt, "pt:none", Tr.S("PowerToys не установлен", "PowerToys is not installed"),
                  Tr.S("Модули PowerToys появятся здесь, когда PowerToys установлен (" + ptPath + " в %LOCALAPPDATA%\\Microsoft\\PowerToys).",
                       "PowerToys modules appear here once PowerToys is installed (" + ptPath + " under %LOCALAPPDATA%\\Microsoft\\PowerToys)."),
                  "", "", 0, false, false);
            }
            return l;
        }

        // Модули, которые по умолчанию остаются включёнными : дополнения
        // Проводника и контекстного меню. Остальные, в том числе новые неизвестные, отмечены к выключению.
        internal static bool PowerToysKeptByDefault(string key)
        {
            switch (key)
            {
                case "File Explorer": case "File Locksmith": case "Image Resizer": case "Peek": case "PowerRename": case "RegistryPreview":
                    return true;
                default: return false;
            }
        }

        // [заголовок, что это, чем рискуете] для модулей PowerToys; неизвестный модуль — общими словами.
        private static string[] PowerToysModuleText(string key)
        {
            switch (key)
            {
                case "AdvancedPaste": return new string[] { "Advanced Paste", Tr.S("Вставка с преобразованием (текст без форматирования, JSON, ИИ через ключ OpenAI).", "Paste with conversion (plain text, JSON, AI via an OpenAI key)."), Tr.S("Теряется Win+Shift+V.", "Win+Shift+V is lost.") };
                case "AlwaysOnTop": return new string[] { "Always On Top", Tr.S("Закрепить окно поверх остальных (Win+Ctrl+T).", "Pin a window above the others (Win+Ctrl+T)."), Tr.S("Теряется закрепление окон.", "Window pinning is lost.") };
                case "Awake": return new string[] { "Awake", Tr.S("Не давать ПК уснуть без смены схемы питания.", "Keep the PC awake without changing the power plan."), Tr.S("Теряется удобный «не спать».", "The handy “stay awake” is lost.") };
                case "CmdNotFound": return new string[] { "Command Not Found", Tr.S("Подсказка winget-пакета для неизвестной команды в PowerShell 7.", "Suggests the winget package for an unknown command in PowerShell 7."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                case "CmdPal": return new string[] { "Command Palette", Tr.S("Быстрый запуск и поиск (наследник PowerToys Run).", "Quick launcher and search (the successor of PowerToys Run)."), Tr.S("Теряется горячая клавиша запуска.", "The launcher hotkey is lost.") };
                case "ColorPicker": return new string[] { "Color Picker", Tr.S("Пипетка цвета с экрана (Win+Shift+C).", "Screen colour picker (Win+Shift+C)."), Tr.S("Теряется пипетка.", "The picker is lost.") };
                case "CropAndLock": return new string[] { "Crop And Lock", Tr.S("Вырезать часть окна в отдельное окно.", "Crop part of a window into its own window."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                case "CursorWrap": return new string[] { "Cursor Wrap", Tr.S("Курсор переходит с края экрана на противоположный.", "The cursor wraps from one screen edge to the opposite one."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                case "EnvironmentVariables": return new string[] { "Environment Variables", Tr.S("Редактор переменных окружения с профилями.", "Environment-variable editor with profiles."), Tr.S("Ничем: штатный редактор Windows остаётся.", "Nothing: the built-in Windows editor stays.") };
                case "FancyZones": return new string[] { "FancyZones", Tr.S("Раскладки окон по зонам.", "Window layouts by zones."), Tr.S("Теряются зоны и Shift-перетаскивание окон.", "Zones and Shift-drag snapping are lost.") };
                case "File Explorer": return new string[] { Tr.S("Дополнения Проводника", "File Explorer add-ons"), Tr.S("Предпросмотр и миниатюры SVG, Markdown, PDF, G-code, STL, QOI в Проводнике.", "Preview and thumbnails for SVG, Markdown, PDF, G-code, STL, QOI in Explorer."), Tr.S("Проводник перестанет показывать эти форматы в панели предпросмотра.", "Explorer stops showing those formats in the preview pane.") };
                case "File Locksmith": return new string[] { "File Locksmith", Tr.S("«Кто держит файл» в контекстном меню.", "“What is using this file” in the context menu."), Tr.S("Теряется пункт меню.", "The menu entry is lost.") };
                case "FindMyMouse": return new string[] { "Find My Mouse", Tr.S("Подсветить курсор по двойному Ctrl.", "Spotlight the cursor on double Ctrl."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                case "GrabAndMove": return new string[] { "Grab And Move", Tr.S("Перетаскивать окна за любую точку с клавишей.", "Drag windows from anywhere with a modifier key."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                case "Hosts": return new string[] { "Hosts File Editor", Tr.S("Редактор файла hosts.", "Editor for the hosts file."), Tr.S("Ничем: файл правится Блокнотом.", "Nothing: Notepad edits the file.") };
                case "Image Resizer": return new string[] { "Image Resizer", Tr.S("Изменение размера картинок из контекстного меню.", "Resize images from the context menu."), Tr.S("Теряется пункт меню.", "The menu entry is lost.") };
                case "Keyboard Manager": return new string[] { "Keyboard Manager", Tr.S("Переназначение клавиш и сочетаний.", "Key and shortcut remapping."), Tr.S("⚠ Все ваши переназначения клавиш перестанут действовать.", "⚠ All your key remappings stop applying.") };
                case "LightSwitch": return new string[] { "Light Switch", Tr.S("Автопереключение светлой/тёмной темы по расписанию.", "Automatic light/dark theme switching by schedule."), Tr.S("Тема перестанет меняться сама.", "The theme stops switching on its own.") };
                case "Measure Tool": return new string[] { "Screen Ruler", Tr.S("Измерение пикселей на экране (Win+Shift+M).", "Measure pixels on screen (Win+Shift+M)."), Tr.S("Теряется линейка.", "The ruler is lost.") };
                case "MouseHighlighter": return new string[] { "Mouse Highlighter", Tr.S("Подсветка кликов мыши для демонстраций.", "Highlight mouse clicks for demos."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                case "MouseJump": return new string[] { "Mouse Jump", Tr.S("Быстрый прыжок курсора по мини-карте экранов.", "Jump the cursor via a screen minimap."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                case "MousePointerCrosshairs": return new string[] { "Mouse Pointer Crosshairs", Tr.S("Перекрестие вокруг курсора.", "Crosshairs around the cursor."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                case "MouseWithoutBorders": return new string[] { "Mouse Without Borders", Tr.S("Одна мышь и клавиатура на несколько ПК по сети.", "One mouse and keyboard across several PCs over the network."), Tr.S("⚠ Управление соседними ПК прекратится.", "⚠ Control of neighbouring PCs stops.") };
                case "NewPlus": return new string[] { "New+", Tr.S("Создание файлов из своих шаблонов через меню «Создать».", "Create files from your templates via the “New” menu."), Tr.S("Теряется пункт меню.", "The menu entry is lost.") };
                case "Peek": return new string[] { "Peek", Tr.S("Быстрый просмотр файла по Ctrl+Пробел.", "Quick file preview on Ctrl+Space."), Tr.S("Теряется быстрый просмотр.", "Quick preview is lost.") };
                case "PowerDisplay": return new string[] { "Power Display", Tr.S("Управление яркостью и параметрами мониторов.", "Brightness and monitor settings control."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                case "PowerRename": return new string[] { "PowerRename", Tr.S("Массовое переименование с регулярными выражениями.", "Bulk rename with regular expressions."), Tr.S("Теряется пункт меню.", "The menu entry is lost.") };
                case "PowerToys Run": return new string[] { "PowerToys Run", Tr.S("Быстрый запуск по Alt+Пробел (устаревает в пользу Command Palette).", "Alt+Space launcher (being replaced by Command Palette)."), Tr.S("Теряется Alt+Пробел.", "Alt+Space is lost.") };
                case "QuickAccent": return new string[] { "Quick Accent", Tr.S("Ввод букв с диакритикой удержанием клавиши.", "Type accented letters by holding a key."), Tr.S("Ничем, если не печатаете на языках с диакритикой.", "Nothing unless you type accented languages.") };
                case "RegistryPreview": return new string[] { "Registry Preview", Tr.S("Просмотр .reg-файлов перед импортом.", "Preview .reg files before import."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                case "Shortcut Guide": return new string[] { "Shortcut Guide", Tr.S("Подсказка сочетаний с Win по удержанию Win.", "Win-key shortcut overlay when holding Win."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                case "TextExtractor": return new string[] { "Text Extractor", Tr.S("Распознать текст с экрана (Win+Shift+T).", "OCR text from the screen (Win+Shift+T)."), Tr.S("Теряется OCR с экрана.", "Screen OCR is lost.") };
                case "VideoConference": return new string[] { "Video Conference Mute", Tr.S("Общее выключение камеры и микрофона (снято с поддержки).", "Global camera/mic mute (deprecated)."), Tr.S("Ничем.", "Nothing.") };
                case "Workspaces": return new string[] { "Workspaces", Tr.S("Сохранённые наборы окон.", "Saved window sets."), Tr.S("Теряются сохранённые рабочие пространства.", "Saved workspaces are lost.") };
                case "ZoomIt": return new string[] { "ZoomIt", Tr.S("Лупа и рисование на экране для презентаций.", "Screen zoom and annotation for presentations."), Tr.S("Ничем заметным.", "Nothing noticeable.") };
                default: return new string[] { key, Tr.S("Модуль PowerToys «" + key + "».", "PowerToys module “" + key + "”."), Tr.S("Функция этого модуля перестанет работать.", "This module's feature stops working.") };
            }
        }
    }
}
