// SysDeck — «Захват»: настройки (JSON, миграции) и импорт из Game Center.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  Настройки — свой JSON в capture\settings.json
    // ------------------------------------------------------------------ //
    internal sealed class CapSettings
    {
        public const int MinToastSeconds = 3, MaxToastSeconds = 15;
        public const int CurrentVersion = 4;              // версия формата settings.json; без поля — ранние сборки

        public bool Enabled = true;                        // false — пользователь нажал «Остановить»: сам не запускается
        public string ShotFolder = "";                     // пусто — «Изображения\Screenshots»
        public string VideoFolder = "";                    // пусто — «Видео\Captures»
        public bool PerAppFolders = true;
        public string NameTemplate = Capture.NameTemplate.Default;
        public string ImageFormat = "png";                 // png | jpg
        public int JpegQuality = 92;
        public ShotAfter After = ShotAfter.SaveAndCopy;
        public ScreenTarget ScreenKey = ScreenTarget.CursorMonitor;
        public int DelaySeconds;                           // 0 / 3 / 5 / 10
        public bool CursorInShots;
        public bool ShutterSound;
        public bool ToastEnabled = true;
        public int ToastSeconds = 3;
        public bool DeferToastsInFullscreen = true;
        public bool PrintScreenOverride;                   // PrtScn вместо Ножниц (HKCU, по согласию)
        public bool TakeBusyKeys = true;                   // сочетание занято другой программой (NVIDIA App: Alt+R) — забрать хуком клавиатуры
        public int PrintScreenPrevious = -1;               // прежнее значение PrintScreenKeyForSnippingEnabled; -1 — не было
        public Rectangle LastRegion = Rectangle.Empty;     // для «повторить последнюю область»
        public string EditorColor = "#E53935";             // редактор запоминает цвет, толщину, размер текста, заливку, инструмент
        public int EditorWidth = 4;
        public int EditorFontSize = 28;
        public bool EditorFill;
        public EditTool EditorTool = EditTool.Rect;
        public string VideoCodec = "h264";                 // h264 | hevc | av1
        public string VideoEncoder = "auto";               // auto | nvidia | amd | intel | software
        public string VideoQuality = "optimal";            // low | optimal | high | max
        public int VideoFps = 60;                          // 30 / 60
        public int VideoHeight = 1080;                     // 0 — как у источника; 1080 / 720 — только уменьшение
        public bool CursorInVideo = true;
        public bool SystemAudio = true;
        public bool WindowAudioOnly;                       // запись окна: звук только его процесса (Windows 10 2004+)
        public bool Microphone = true;
        public string MicDeviceId = "";                    // пусто — микрофон по умолчанию
        public int MicVolume = 100;                        // 0..400 %
        public bool MicMono;
        public bool SeparateTracks;                        // дорожка 1 смешанная, 2 — система, 3 — микрофон
        public int CountdownSeconds;                       // 0 / 3 / 5
        public int MaxMinutes;                             // 0 — без ограничения
        public bool RecordPanel = true;                    // рамка области и панель с таймером
        public bool GalleryOnStart;                        // открывать галерею при запуске фонового процесса
        public Rectangle GalleryBounds = Rectangle.Empty;  // где стояло окно галереи
        public int GalleryTab;                             // 0 — всё, 1 — снимки, 2 — видео
        public string HudItems = HudItem.Default;          // строки оверлея: «id:флаги:интервал:цвет;…» (HudItem)
        public int HudGraphSeconds = 60;                   // окно графиков, 10..600 с
        public int HudScale = 100;                         // размер столбика, 50..300 %
        public bool HudElevated;                           // оверлей с правами администратора (задача Планировщика)
        public bool HudHwinfo = true;                      // поднимать HWiNFO в фоне ради датчиков, если он не запущен
        public HudCorner HudCorner = HudCorner.TopRight;
        public int HudMonitor;                             // 0 — основной, дальше остальные
        public int HudOpacity = 75;                        // непрозрачность подложки, 20..100 %
        public bool HudInCaptures;                         // столбик виден на своих снимках и видео
        public bool HudShown;                              // был показан — вернётся после перезапуска агента
        public string HudItems2 = "";                      // второй и третий наборы строк (переключаются клавишей)
        public string HudItems3 = "";
        public int HudScene;                               // 0..2 — какой набор показан
        public int HudX = 1000, HudY = 0;                  // своё место (HudCorner.Custom): доля свободного поля, ‰
        public string HudFont = HudStyle.DefaultFont;      // семейство шрифта из HudStyle.Fonts
        public int HudFontSize = 16;                      // кегль значения при размере 100 %, px
        public bool HudBoldLabels = true;                  // подписи «ЦП», «ОЗУ» — жирным
        public bool HudGroupColors = true;                 // подписи цветом группы (ЦП голубым, ГП зелёным…)
        public bool HudShadow = true;                      // тень под текстом — читается на светлом фоне
        public bool HudRowLayout;                          // строкой вдоль экрана вместо столбика
        public int HudStatsSeconds;                        // окно мин./сред./макс., с (до 600 — столько хранит история); 0 — как окно графиков

        public string SceneItems(int scene)
        {
            return scene == 1 ? HudItems2 : scene == 2 ? HudItems3 : HudItems;
        }

        public void SetSceneItems(int scene, string items)
        {
            if (scene == 1) HudItems2 = items; else if (scene == 2) HudItems3 = items; else HudItems = items;
        }

        public string ActiveItems { get { return SceneItems(HudScene); } }
        public readonly Dictionary<CapAction, string> Hotkeys = new Dictionary<CapAction, string>();

        public CapSettings()
        {
            foreach (CapAction a in CapActions.All) Hotkeys[a] = CapActions.DefaultHotkey(a);
        }

        public string EffectiveShotFolder { get { return string.IsNullOrEmpty(ShotFolder) ? CapPaths.DefaultShotFolder : ShotFolder; } }
        public string EffectiveVideoFolder { get { return string.IsNullOrEmpty(VideoFolder) ? CapPaths.DefaultVideoFolder : VideoFolder; } }
        public string ImageExtension { get { return ImageFormat == "jpg" ? ".jpg" : ".png"; } }

        public HotkeySpec Hotkey(CapAction a)
        {
            string text;
            return Hotkeys.TryGetValue(a, out text) ? HotkeySpec.Parse(text) : new HotkeySpec();
        }

        // Первый запуск (файла настроек ещё нет): сочетания и папки берутся из VK Play GameCenter, если он стоит.
        public static CapSettings Load()
        {
            if (File.Exists(CapPaths.SettingsFile)) return Load(CapPaths.SettingsFile);
            CapSettings s = new CapSettings();
            GameCenterImport found = GameCenterImport.Read(GameCenterImport.IniPath);
            if (found != null) found.ApplyTo(s);
            return s;
        }

        // Чтение, совпавшее с заменой файла другим процессом (File.Replace), падает на миг. Умолчания в этот момент
        // нельзя: Load → изменить поле → Save записал бы их поверх всех настроек человека.
        public static CapSettings Load(string file)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(file)) return new CapSettings();
                    CapSettings s = FromJson(File.ReadAllText(file));
                    return s ?? new CapSettings();
                }
                catch (Exception ex)
                {
                    if ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 20) { Thread.Sleep(25); continue; }
                    CapLog.Report(ex);
                    return new CapSettings();
                }
            }
        }

        public bool Save() { return Save(CapPaths.SettingsFile); }

        public bool Save(string file)
        {
            try { CapPaths.WriteAtomic(file, ToJson()); return true; }
            catch (Exception ex) { CapLog.Report(ex); return false; }
        }

        internal static CapSettings FromJson(string text)
        {
            JVal root;
            try { root = Jsn.Parse(text ?? ""); }
            catch { return null; }
            if (root == null || root.Kind != JKind.Obj) return null;
            CapSettings s = new CapSettings();
            // Хранится только явная остановка. Поле «Enabled» ранних сборок записывалось и при простом открытии вкладки
            // (умолчание было «выключено») — по нему не отличить «не запускали» от «остановили», поэтому оно не читается.
            s.Enabled = !Bool(root, "Stopped", !s.Enabled);
            s.ShotFolder = Str(root, "ShotFolder", s.ShotFolder);
            s.VideoFolder = Str(root, "VideoFolder", s.VideoFolder);
            s.PerAppFolders = Bool(root, "PerAppFolders", s.PerAppFolders);
            s.NameTemplate = Str(root, "NameTemplate", s.NameTemplate);
            if (string.IsNullOrEmpty(s.NameTemplate.Trim())) s.NameTemplate = Capture.NameTemplate.Default;
            string fmt = Str(root, "ImageFormat", s.ImageFormat).ToLowerInvariant();
            s.ImageFormat = fmt == "jpg" || fmt == "jpeg" ? "jpg" : "png";
            s.JpegQuality = Clamp(Int(root, "JpegQuality", s.JpegQuality), 1, 100);
            s.After = EnumOr(root, "After", s.After);
            s.ScreenKey = EnumOr(root, "ScreenKey", s.ScreenKey);
            int delay = Int(root, "DelaySeconds", s.DelaySeconds);
            s.DelaySeconds = delay == 3 || delay == 5 || delay == 10 ? delay : 0;
            s.CursorInShots = Bool(root, "CursorInShots", s.CursorInShots);
            s.ShutterSound = Bool(root, "ShutterSound", s.ShutterSound);
            s.ToastEnabled = Bool(root, "ToastEnabled", s.ToastEnabled);
            // Файлы без «Version» записаны ранними сборками: в них «ToastEnabled»: false встречался без выбора пользователя,
            // и уведомление о сохранённом снимке молча не показывалось. Один раз уведомления включаются; следующее
            // сохранение пишет версию, и снятая после этого галочка уже остаётся снятой.
            if (Int(root, "Version", 0) < 3) s.ToastEnabled = true;
            s.ToastSeconds = Clamp(Int(root, "ToastSeconds", s.ToastSeconds), MinToastSeconds, MaxToastSeconds);
            s.DeferToastsInFullscreen = Bool(root, "DeferToastsInFullscreen", s.DeferToastsInFullscreen);
            s.PrintScreenOverride = Bool(root, "PrintScreenOverride", s.PrintScreenOverride);
            s.TakeBusyKeys = Bool(root, "TakeBusyKeys", s.TakeBusyKeys);
            s.PrintScreenPrevious = Clamp(Int(root, "PrintScreenPrevious", s.PrintScreenPrevious), -1, 1);
            Color editorColor;
            if (HexColor.TryParse(Str(root, "EditorColor", s.EditorColor), out editorColor)) s.EditorColor = HexColor.Format(editorColor);
            s.EditorWidth = Clamp(Int(root, "EditorWidth", s.EditorWidth), 1, 40);
            s.EditorFontSize = Clamp(Int(root, "EditorFontSize", s.EditorFontSize), 8, 200);
            s.EditorFill = Bool(root, "EditorFill", s.EditorFill);
            s.EditorTool = EnumOr(root, "EditorTool", s.EditorTool);
            s.VideoCodec = OneOf(Str(root, "VideoCodec", s.VideoCodec), s.VideoCodec, "h264", "hevc", "av1");
            s.VideoEncoder = OneOf(Str(root, "VideoEncoder", s.VideoEncoder), s.VideoEncoder, "auto", "nvidia", "amd", "intel", "software");
            s.VideoQuality = OneOf(Str(root, "VideoQuality", s.VideoQuality), s.VideoQuality, "low", "optimal", "high", "max");
            s.VideoFps = Int(root, "VideoFps", s.VideoFps) == 30 ? 30 : 60;
            int height = Int(root, "VideoHeight", s.VideoHeight);
            s.VideoHeight = height == 1080 || height == 720 ? height : 0;
            s.CursorInVideo = Bool(root, "CursorInVideo", s.CursorInVideo);
            s.SystemAudio = Bool(root, "SystemAudio", s.SystemAudio);
            s.WindowAudioOnly = Bool(root, "WindowAudioOnly", s.WindowAudioOnly);
            s.Microphone = Bool(root, "Microphone", s.Microphone);
            s.MicDeviceId = Str(root, "MicDeviceId", s.MicDeviceId);
            s.MicVolume = Clamp(Int(root, "MicVolume", s.MicVolume), 0, 400);
            s.MicMono = Bool(root, "MicMono", s.MicMono);
            s.SeparateTracks = Bool(root, "SeparateTracks", s.SeparateTracks);
            int countdown = Int(root, "CountdownSeconds", s.CountdownSeconds);
            s.CountdownSeconds = countdown == 3 || countdown == 5 ? countdown : 0;
            s.MaxMinutes = Clamp(Int(root, "MaxMinutes", s.MaxMinutes), 0, 24 * 60);
            s.RecordPanel = Bool(root, "RecordPanel", s.RecordPanel);
            s.GalleryOnStart = Bool(root, "GalleryOnStart", s.GalleryOnStart);
            s.GalleryTab = Clamp(Int(root, "GalleryTab", s.GalleryTab), 0, 2);
            // Старый набор «Cpu,Ram» (до каталога показателей) переводится в строки нового вида один раз.
            JVal legacy = root.Get("HudMetrics");
            if (root.Get("HudItems") == null && legacy != null && legacy.Kind == JKind.Str)
            {
                List<HudItem> migrated = new List<HudItem>();
                foreach (string id in HudCatalog.FromLegacy(legacy.Raw)) migrated.Add(new HudItem(id));
                s.HudItems = HudItem.FormatList(migrated);
            }
            else s.HudItems = HudItem.FormatList(HudItem.ParseList(Str(root, "HudItems", s.HudItems)));
            s.HudGraphSeconds = Clamp(Int(root, "HudGraphSeconds", s.HudGraphSeconds), 10, 600);
            s.HudScale = Clamp(Int(root, "HudScale", s.HudScale), 50, 300);
            s.HudElevated = Bool(root, "HudElevated", s.HudElevated);
            s.HudHwinfo = Bool(root, "HudHwinfo", s.HudHwinfo);
            s.HudCorner = EnumOr(root, "HudCorner", s.HudCorner);
            s.HudMonitor = Clamp(Int(root, "HudMonitor", s.HudMonitor), 0, 16);
            s.HudOpacity = Clamp(Int(root, "HudOpacity", s.HudOpacity), 20, 100);
            s.HudInCaptures = Bool(root, "HudInCaptures", s.HudInCaptures);
            s.HudShown = Bool(root, "HudShown", s.HudShown);
            s.HudItems2 = HudItem.FormatList(HudItem.ParseList(Str(root, "HudItems2", s.HudItems2)));
            s.HudItems3 = HudItem.FormatList(HudItem.ParseList(Str(root, "HudItems3", s.HudItems3)));
            s.HudScene = Clamp(Int(root, "HudScene", s.HudScene), 0, 2);
            s.HudX = Clamp(Int(root, "HudX", s.HudX), 0, 1000);
            s.HudY = Clamp(Int(root, "HudY", s.HudY), 0, 1000);
            s.HudFont = HudStyle.ValidFont(Str(root, "HudFont", s.HudFont));
            s.HudFontSize = Clamp(Int(root, "HudFontSize", s.HudFontSize), HudStyle.MinFontSize, HudStyle.MaxFontSize);
            s.HudBoldLabels = Bool(root, "HudBoldLabels", s.HudBoldLabels);
            s.HudGroupColors = Bool(root, "HudGroupColors", s.HudGroupColors);
            s.HudShadow = Bool(root, "HudShadow", s.HudShadow);
            s.HudRowLayout = Bool(root, "HudRowLayout", s.HudRowLayout);
            s.HudStatsSeconds = Clamp(Int(root, "HudStatsSeconds", s.HudStatsSeconds), 0, 600);
            JVal gallery = root.Get("GalleryBounds");
            if (gallery != null && gallery.Kind == JKind.Arr && gallery.V.Count == 4)
            {
                int[] g = new int[4];
                bool ok = true;
                for (int i = 0; i < 4; i++)
                    ok &= gallery.V[i].Kind == JKind.Num && int.TryParse(gallery.V[i].Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out g[i]);
                if (ok && g[2] > 0 && g[3] > 0) s.GalleryBounds = new Rectangle(g[0], g[1], g[2], g[3]);
            }
            // Обрезка — разовое действие: окно не должно открываться сразу в ней.
            if (s.EditorTool == EditTool.Crop) s.EditorTool = EditTool.Arrow;
            JVal last = root.Get("LastRegion");
            if (last != null && last.Kind == JKind.Arr && last.V.Count == 4)
            {
                int[] v = new int[4];
                bool ok = true;
                for (int i = 0; i < 4; i++)
                    ok &= last.V[i].Kind == JKind.Num && int.TryParse(last.V[i].Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]);
                if (ok && v[2] > 0 && v[3] > 0) s.LastRegion = new Rectangle(v[0], v[1], v[2], v[3]);
            }
            JVal keys = root.Get("Hotkeys");
            if (keys != null && keys.Kind == JKind.Obj)
                foreach (CapAction a in CapActions.All)
                {
                    JVal k = keys.Get(a.ToString());
                    HotkeySpec spec;
                    if (k != null && k.Kind == JKind.Str && HotkeySpec.TryParse(k.Raw, out spec)) s.Hotkeys[a] = spec.ToString();
                }
            // Версия 3: оверлей по умолчанию — Alt+R. Кто оставил прежнее сочетание по умолчанию, получает новое;
            // выбранное самим человеком не трогается.
            if (Int(root, "Version", 0) < 3 && s.Hotkeys[CapAction.Hud] == "Ctrl+Alt+F12") s.Hotkeys[CapAction.Hud] = CapActions.DefaultHotkey(CapAction.Hud);
            // Версия 4: «следующий набор» уходит с Ctrl+Alt+S — его держит панель «Размеры папок».
            if (Int(root, "Version", 0) < 4 && s.Hotkeys[CapAction.HudScene] == "Ctrl+Alt+S") s.Hotkeys[CapAction.HudScene] = CapActions.DefaultHotkey(CapAction.HudScene);
            return s;
        }

        internal string ToJson()
        {
            JVal o = JVal.NewObj();
            o.Set("Version", N(CurrentVersion));
            o.Set("Stopped", B(!Enabled));
            o.Set("ShotFolder", JVal.NewStr(ShotFolder ?? ""));
            o.Set("VideoFolder", JVal.NewStr(VideoFolder ?? ""));
            o.Set("PerAppFolders", B(PerAppFolders));
            o.Set("NameTemplate", JVal.NewStr(NameTemplate ?? Capture.NameTemplate.Default));
            o.Set("ImageFormat", JVal.NewStr(ImageFormat == "jpg" ? "jpg" : "png"));
            o.Set("JpegQuality", N(Clamp(JpegQuality, 1, 100)));
            o.Set("After", JVal.NewStr(After.ToString()));
            o.Set("ScreenKey", JVal.NewStr(ScreenKey.ToString()));
            o.Set("DelaySeconds", N(DelaySeconds));
            o.Set("CursorInShots", B(CursorInShots));
            o.Set("ShutterSound", B(ShutterSound));
            o.Set("ToastEnabled", B(ToastEnabled));
            o.Set("ToastSeconds", N(Clamp(ToastSeconds, MinToastSeconds, MaxToastSeconds)));
            o.Set("DeferToastsInFullscreen", B(DeferToastsInFullscreen));
            o.Set("PrintScreenOverride", B(PrintScreenOverride));
            o.Set("TakeBusyKeys", B(TakeBusyKeys));
            o.Set("PrintScreenPrevious", N(PrintScreenPrevious));
            o.Set("EditorColor", JVal.NewStr(EditorColor ?? "#E53935"));
            o.Set("EditorWidth", N(Clamp(EditorWidth, 1, 40)));
            o.Set("EditorFontSize", N(Clamp(EditorFontSize, 8, 200)));
            o.Set("EditorFill", B(EditorFill));
            o.Set("EditorTool", JVal.NewStr(EditorTool.ToString()));
            o.Set("VideoCodec", JVal.NewStr(VideoCodec ?? "h264"));
            o.Set("VideoEncoder", JVal.NewStr(VideoEncoder ?? "auto"));
            o.Set("VideoQuality", JVal.NewStr(VideoQuality ?? "optimal"));
            o.Set("VideoFps", N(VideoFps));
            o.Set("VideoHeight", N(VideoHeight));
            o.Set("CursorInVideo", B(CursorInVideo));
            o.Set("SystemAudio", B(SystemAudio));
            o.Set("WindowAudioOnly", B(WindowAudioOnly));
            o.Set("Microphone", B(Microphone));
            o.Set("MicDeviceId", JVal.NewStr(MicDeviceId ?? ""));
            o.Set("MicVolume", N(Clamp(MicVolume, 0, 400)));
            o.Set("MicMono", B(MicMono));
            o.Set("SeparateTracks", B(SeparateTracks));
            o.Set("CountdownSeconds", N(CountdownSeconds));
            o.Set("MaxMinutes", N(Clamp(MaxMinutes, 0, 24 * 60)));
            o.Set("RecordPanel", B(RecordPanel));
            o.Set("GalleryOnStart", B(GalleryOnStart));
            o.Set("GalleryTab", N(Clamp(GalleryTab, 0, 2)));
            o.Set("HudItems", JVal.NewStr(HudItem.FormatList(HudItem.ParseList(HudItems))));
            o.Set("HudGraphSeconds", N(Clamp(HudGraphSeconds, 10, 600)));
            o.Set("HudScale", N(Clamp(HudScale, 50, 300)));
            o.Set("HudElevated", B(HudElevated));
            o.Set("HudHwinfo", B(HudHwinfo));
            o.Set("HudCorner", JVal.NewStr(HudCorner.ToString()));
            o.Set("HudMonitor", N(Clamp(HudMonitor, 0, 16)));
            o.Set("HudOpacity", N(Clamp(HudOpacity, 20, 100)));
            o.Set("HudInCaptures", B(HudInCaptures));
            o.Set("HudShown", B(HudShown));
            o.Set("HudItems2", JVal.NewStr(HudItem.FormatList(HudItem.ParseList(HudItems2))));
            o.Set("HudItems3", JVal.NewStr(HudItem.FormatList(HudItem.ParseList(HudItems3))));
            o.Set("HudScene", N(Clamp(HudScene, 0, 2)));
            o.Set("HudX", N(Clamp(HudX, 0, 1000)));
            o.Set("HudY", N(Clamp(HudY, 0, 1000)));
            o.Set("HudFont", JVal.NewStr(HudStyle.ValidFont(HudFont)));
            o.Set("HudFontSize", N(Clamp(HudFontSize, HudStyle.MinFontSize, HudStyle.MaxFontSize)));
            o.Set("HudBoldLabels", B(HudBoldLabels));
            o.Set("HudGroupColors", B(HudGroupColors));
            o.Set("HudShadow", B(HudShadow));
            o.Set("HudRowLayout", B(HudRowLayout));
            o.Set("HudStatsSeconds", N(Clamp(HudStatsSeconds, 0, 600)));
            if (!GalleryBounds.IsEmpty)
            {
                JVal box = JVal.NewArr();
                box.V.Add(N(GalleryBounds.X)); box.V.Add(N(GalleryBounds.Y)); box.V.Add(N(GalleryBounds.Width)); box.V.Add(N(GalleryBounds.Height));
                o.Set("GalleryBounds", box);
            }
            if (!LastRegion.IsEmpty)
            {
                JVal arr = JVal.NewArr();
                arr.V.Add(N(LastRegion.X)); arr.V.Add(N(LastRegion.Y)); arr.V.Add(N(LastRegion.Width)); arr.V.Add(N(LastRegion.Height));
                o.Set("LastRegion", arr);
            }
            JVal keys = JVal.NewObj();
            foreach (CapAction a in CapActions.All)
            {
                string text;
                keys.Set(a.ToString(), JVal.NewStr(Hotkeys.TryGetValue(a, out text) ? text ?? "" : ""));
            }
            o.Set("Hotkeys", keys);
            return Jsn.Write(o);
        }

        public CapSettings Clone() { return FromJson(ToJson()) ?? new CapSettings(); }

        private static int Clamp(int v, int min, int max) { return v < min ? min : v > max ? max : v; }

        private static string OneOf(string value, string fallback, params string[] allowed)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            foreach (string a in allowed) if (v == a) return a;
            return fallback;
        }

        private static JVal B(bool value)
        {
            JVal j = new JVal();
            j.Kind = JKind.Bool;
            j.B = value;
            return j;
        }

        private static JVal N(int value) { return JVal.NewNum(value.ToString(CultureInfo.InvariantCulture)); }

        private static bool Bool(JVal root, string name, bool fallback)
        {
            JVal v = root.Get(name);
            return v != null && v.Kind == JKind.Bool ? v.B : fallback;
        }

        private static string Str(JVal root, string name, string fallback)
        {
            JVal v = root.Get(name);
            return v != null && v.Kind == JKind.Str ? v.Raw : fallback;
        }

        private static int Int(JVal root, string name, int fallback)
        {
            JVal v = root.Get(name);
            int n;
            return v != null && v.Kind == JKind.Num && int.TryParse(v.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        private static T EnumOr<T>(JVal root, string name, T fallback) where T : struct
        {
            JVal v = root.Get(name);
            T parsed;
            if (v != null && v.Kind == JKind.Str && Enum.TryParse(v.Raw, true, out parsed) && Enum.IsDefined(typeof(T), parsed)) return parsed;
            return fallback;
        }
    }

    // ------------------------------------------------------------------ //
    //  Перенос настроек из VK Play GameCenter: %LOCALAPPDATA%\GameCenter\GameCenter.ini (UTF-16)
    // ------------------------------------------------------------------ //
    internal sealed class GameCenterImport
    {
        public readonly Dictionary<CapAction, HotkeySpec> Hotkeys = new Dictionary<CapAction, HotkeySpec>();
        public string ShotFolder = "";
        public string VideoFolder = "";
        public int SkippedKeys;             // сочетания с модификатором, формат которого не известен
        public int Microphone = -1;         // [Microphone] AuxiliaryAudioEnabled: 1 / 0; -1 — не задано

        public static string IniPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"GameCenter\GameCenter.ini"); }
        }

        public bool IsEmpty { get { return Hotkeys.Count == 0 && ShotFolder.Length == 0 && VideoFolder.Length == 0 && Microphone < 0; } }

        // null — файла нет или в нём нечего взять.
        public static GameCenterImport Read(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                GameCenterImport r = Parse(File.ReadAllText(path));   // BOM определяет UTF-16
                return r.IsEmpty ? null : r;
            }
            catch (Exception ex) { CapLog.Report(ex); return null; }
        }

        // [HotKeys] ScreenSelect=61:0 — скан-код DirectInput и модификаторы. Проверено только «:0» (без модификатора):
        // остальные значения не расшифрованы, и угаданное сочетание было бы хуже пропущенного.
        internal static GameCenterImport Parse(string text)
        {
            GameCenterImport r = new GameCenterImport();
            string section = "";
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[' && line.EndsWith("]", StringComparison.Ordinal)) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string name = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (section.Equals("HotKeys", StringComparison.OrdinalIgnoreCase))
                {
                    CapAction action;
                    if (!ActionOf(name, out action)) continue;
                    HotkeySpec spec;
                    if (TryKey(value, out spec)) r.Hotkeys[action] = spec;
                    else if (value.Length > 0) r.SkippedKeys++;
                }
                else if (section.Equals("Microphone", StringComparison.OrdinalIgnoreCase) && name.Equals("AuxiliaryAudioEnabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (value == "0" || value == "1") r.Microphone = value == "1" ? 1 : 0;
                }
                else if (name.Equals("Folder", StringComparison.OrdinalIgnoreCase))
                {
                    if (section.Equals("Screens", StringComparison.OrdinalIgnoreCase)) r.ShotFolder = value;
                    else if (section.Equals("VideoCapture", StringComparison.OrdinalIgnoreCase)) r.VideoFolder = value;
                }
            }
            return r;
        }

        private static bool ActionOf(string name, out CapAction action)
        {
            switch (name.ToLowerInvariant())
            {
                case "screenselect": action = CapAction.ShotRegion; return true;
                case "fullscreenshot": action = CapAction.ShotScreen; return true;
                case "videoselect": action = CapAction.RecRegion; return true;
                default: action = CapAction.ShotRegion; return false;
            }
        }

        internal static bool TryKey(string value, out HotkeySpec spec)
        {
            spec = new HotkeySpec();
            string[] parts = value.Split(':');
            int dik, mods;
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out dik)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out mods)
                || mods != 0 || dik <= 0 || dik > 0xFF) return false;
            uint vk = VkFromDik(dik);
            if (vk == 0 || HotkeySpec.IsModifierKey(vk) || !HotkeySpec.IsKnownKey(vk)) return false;
            spec = new HotkeySpec(0, vk);
            return true;
        }

        // Скан-коды DirectInput 0x80+ — клавиши с префиксом E0 (стрелки, PrtScn, Insert…).
        internal static uint VkFromDik(int dik)
        {
            if (dik >= 0x3B && dik <= 0x44) return (uint)(0x70 + dik - 0x3B);    // F1..F10 — без раскладки
            if (dik == 0x57) return 0x7A;                                           // F11
            if (dik == 0x58) return 0x7B;                                           // F12
            if (dik == 0xB7) return 0x2C;                                           // PrtScn
            uint scan = dik >= 0x80 ? (uint)(0xE000 | (dik - 0x80)) : (uint)dik;
            try { return CapNative.MapVirtualKey(scan, 3); }                        // MAPVK_VSC_TO_VK_EX
            catch { return 0; }
        }

        // Папка переносится, только если она на месте: диск G: у другого компьютера может и не быть.
        public void ApplyTo(CapSettings s)
        {
            foreach (KeyValuePair<CapAction, HotkeySpec> kv in Hotkeys) s.Hotkeys[kv.Key] = kv.Value.ToString();
            if (ShotFolder.Length > 0 && SafeExists(ShotFolder)) s.ShotFolder = ShotFolder.TrimEnd('\\', '/');
            if (VideoFolder.Length > 0 && SafeExists(VideoFolder)) s.VideoFolder = VideoFolder.TrimEnd('\\', '/');
            if (Microphone >= 0) s.Microphone = Microphone == 1;
        }

        private static bool SafeExists(string dir)
        {
            try { return Path.IsPathRooted(dir) && Directory.Exists(dir); }
            catch { return false; }
        }
    }
}
