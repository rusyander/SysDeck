// SysDeck — область «capture»: сочетания клавиш (разбор, перенос из GameCenter, занятость и повторная
// регистрация), имя файла по шаблону и защита корня папки, настройки, геометрия выделения, миниатюра и вырезка кадра,
// редактор снимка и пометки прямо в оверлее.
//
// Прогон не запускает фоновый процесс захвата, не сигналит событиям настоящего агента, не пишет в автозапуск и в
// параметр PrtScn, не снимает экран. Горячая клавиша регистрируется только одна — Ctrl+Alt+Shift+F24, которой нет на
// обычной клавиатуре, и снимается сразу же.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Capture;

namespace SysDeck.Tests
{
    internal static partial class CaptureTests
    {
        internal static void Run()
        {
            Hotkeys();
            HotkeyBusyRetry();
            Templates();
            Settings();
            GameCenter();
            RecordingPlan();
            RecordingMarker();
            Geometry();
            Images();
            Ipc();
            EditorHistory();
            EditorGeometry();
            EditorRedaction();
            EditorSettings();
            EditorSave();
            EditorMouse();
            OverlayInlineEdit();
            OverlayLabelledButtons();
            ToastGallery();
            Gallery();
        }

        // ---------- галерея ----------
        // Ошибка: «Галерея» открывала Проводник, а никакого окна со снимками в программе не было.
        // Проверяется то, на что нажимает пользователь: команда агента «Галерея» и чтение папок, из которых окно берёт список.
        private static void Gallery()
        {
            string dir = Fx.MakeDir(Fx.Root, "capture-gallery");
            string shots = Fx.MakeDir(dir, "shots"), videos = Fx.MakeDir(dir, "videos");
            string perApp = Fx.MakeDir(shots, "Chrome");
            string older = Path.Combine(shots, "old.png");
            string newer = Path.Combine(perApp, "new.png");
            string clip = Path.Combine(videos, "clip.mp4");
            string junk = Path.Combine(shots, "notes.txt");
            using (Bitmap b = new Bitmap(48, 32, PixelFormat.Format32bppRgb)) b.Save(older, ImageFormat.Png);
            using (Bitmap b = new Bitmap(48, 32, PixelFormat.Format32bppRgb)) b.Save(newer, ImageFormat.Png);
            File.WriteAllBytes(clip, new byte[64]);
            File.WriteAllText(junk, "not a screenshot");
            File.SetLastWriteTime(older, DateTime.Now.AddHours(-2));
            File.SetLastWriteTime(clip, DateTime.Now.AddHours(-1));
            File.SetLastWriteTime(newer, DateTime.Now);

            CapSettings s = new CapSettings();
            s.ShotFolder = shots;
            s.VideoFolder = videos;
            List<GalleryItem> found = GalleryLibrary.Read(s);
            T.Eq("the gallery lists screenshots and videos and ignores other files", 3, found.Count);
            T.Eq("the newest file comes first", newer, found.Count > 0 ? found[0].Path : "");
            T.Eq("a file in a per-program subfolder keeps the program name", "Chrome", found.Count > 0 ? found[0].Folder : "");
            T.Eq("the videos section shows only videos", 1, GalleryLibrary.Filter(found, GalleryKind.Videos).Count);
            T.Eq("the screenshots section shows only screenshots", 2, GalleryLibrary.Filter(found, GalleryKind.Shots).Count);
            T.Eq("«everything» shows both", 3, GalleryLibrary.Filter(found, GalleryKind.All).Count);

            s.GalleryOnStart = true;
            s.GalleryTab = 2;
            s.GalleryBounds = new Rectangle(11, 22, 640, 480);
            CapSettings back = s.Clone();
            T.Check("the gallery settings survive a save and load",
                    back.GalleryOnStart && back.GalleryTab == 2 && back.GalleryBounds == new Rectangle(11, 22, 640, 480));

            // Превью берутся у оболочки Windows — проверяется именно этот путь, а не собственная загрузка картинки.
            using (Control invoker = new Control())
            {
                invoker.CreateControl();
                IntPtr force = invoker.Handle;
                T.Check("the preview host has a window", force != IntPtr.Zero);
                ThumbCache cache = new ThumbCache(invoker, 128);
                try
                {
                    cache.Want(new List<string> { newer });
                    Image thumb = null;
                    for (int i = 0; i < 60 && thumb == null; i++)
                    {
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(100);
                        thumb = cache.Get(newer);
                    }
                    T.Check("Windows renders a preview for a saved screenshot", thumb != null && thumb.Width > 0 && thumb.Height > 0);
                }
                finally { cache.Dispose(); }
            }

            // Настройки агента — в перенаправленной папке данных, поэтому окно читает временное дерево, а не папки владельца.
            CapSettings live = CapSettings.Load();
            live.ShotFolder = shots;
            live.VideoFolder = videos;
            live.Save();
            AgentApp agent = new AgentApp();
            try
            {
                agent.Command("Gallery");
                Application.DoEvents();
                GalleryForm form = (GalleryForm)Field(agent, "_gallery");
                T.Check("the Gallery command opens a window of the program, not Explorer",
                        form != null && !form.IsDisposed && form.Visible);
                T.Check("the gallery window is its own top-level window",
                        form != null && form.TopLevel && form.Owner == null && form.ShowInTaskbar);
                for (int i = 0; i < 40 && form != null && GridCount(form) == 0; i++)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(100);
                }
                T.Eq("the window fills with the files from the capture folders", 3, form != null ? GridCount(form) : -1);
            }
            finally { agent.Dispose(); }
        }

        private static int GridCount(GalleryForm form)
        {
            GalleryGrid grid = (GalleryGrid)Field(form, "_grid");
            return grid == null ? 0 : grid.Count;
        }

        // ---------- сочетания ----------
        private static void Hotkeys()
        {
            HotkeySpec s;
            T.Check("Ctrl+Shift+PrtScn parses", HotkeySpec.TryParse("ctrl + shift + printscreen", out s));
            T.Eq("modifiers and key of Ctrl+Shift+PrtScn", "Ctrl+Shift+PrtScn", s.ToString());
            T.Eq("the modifier order is normalised", "Win+Ctrl+Alt+Shift+F9", HotkeySpec.Parse("Win+Shift+Alt+Ctrl+F9").ToString());
            T.Check("an empty text is «no shortcut», not an error", HotkeySpec.TryParse("  ", out s) && s.IsEmpty);
            T.Check("a lone modifier is not a shortcut", !HotkeySpec.TryParse("Shift", out s));
            T.Check("an unknown modifier is rejected", !HotkeySpec.TryParse("Hyper+F3", out s));
            T.Check("a dangling plus is rejected", !HotkeySpec.TryParse("Ctrl+", out s));
            T.Eq("F3 is bare", true, HotkeySpec.Parse("F3").IsBareKey);
            T.Eq("Alt+F3 is not bare", false, HotkeySpec.Parse("Alt+F3").IsBareKey);
            T.Eq("F3 is VK 0x72", 0x72u, HotkeySpec.Parse("F3").Vk);
            T.Eq("a key press in the field becomes Ctrl+Alt+S", "Ctrl+Alt+S", HotkeySpec.FromKeys(Keys.S | Keys.Control | Keys.Alt, false).ToString());
            T.Check("pressing only Ctrl gives no shortcut", HotkeySpec.FromKeys(Keys.ControlKey | Keys.Control, false).IsEmpty);
            T.Eq("the Win key is added from its own state", "Win+Shift+S", HotkeySpec.FromKeys(Keys.S | Keys.Shift, true).ToString());

            // Решение пользователя 13.09.2026: по умолчанию — ровно клавиши его GameCenter.
            T.Eq("default region shot is F3", "F3", CapActions.DefaultHotkey(CapAction.ShotRegion));
            T.Eq("default screen shot is F4", "F4", CapActions.DefaultHotkey(CapAction.ShotScreen));
            T.Eq("default region video is F7", "F7", CapActions.DefaultHotkey(CapAction.RecRegion));
            T.Eq("the window shot has no default", "", CapActions.DefaultHotkey(CapAction.ShotWindow));

            string why = HotkeyOwners.Describe(HotkeyOwners.ErrorHotkeyAlreadyRegistered, HotkeySpec.Parse("F3"));
            T.Check("a busy F3 names GameCenter as the likely owner", why.Contains("GameCenter"), why);
            T.Eq("a registered key has no problem text", "", HotkeyOwners.Describe(0, HotkeySpec.Parse("F3")));
            string snip = HotkeyOwners.Guess(HotkeySpec.Parse("Shift+Win+S")) ?? "";
            T.Check("Win+Shift+S typed in any order is recognised as the Snipping Tool's", snip.Contains("Ножницы") || snip.Contains("Snipping"), snip);
        }

        // Настоящий RegisterHotKey: сочетание, занятое другим окном, даёт 1409, а после освобождения подхватывается
        // повторной попыткой — так агент забирает F3/F4/F7, когда пользователь закрывает GameCenter.
        private static void HotkeyBusyRetry()
        {
            HotkeySpec rare = new HotkeySpec(HotkeySpec.MOD_CONTROL | HotkeySpec.MOD_ALT | HotkeySpec.MOD_SHIFT, 0x87);   // F24
            Dictionary<CapAction, HotkeySpec> keys = new Dictionary<CapAction, HotkeySpec>();
            keys[CapAction.ShotRegion] = rare;
            HotkeyWindow holder = new HotkeyWindow();
            HotkeyWindow agent = new HotkeyWindow();
            try
            {
                Dictionary<CapAction, int> first = holder.RegisterAll(keys);
                if (first[CapAction.ShotRegion] != 0)
                {
                    T.Skip("busy hotkey retry", "Ctrl+Alt+Shift+F24 is already taken on this machine (" + first[CapAction.ShotRegion] + ")");
                    return;
                }
                Dictionary<CapAction, int> errors = agent.RegisterAll(keys);
                T.Eq("a shortcut held by another window fails with 1409", HotkeyOwners.ErrorHotkeyAlreadyRegistered, errors[CapAction.ShotRegion]);
                T.Check("retry while still busy changes nothing", !agent.RetryFailed(keys, errors) && errors[CapAction.ShotRegion] != 0);
                holder.UnregisterAll();
                T.Check("retry after release registers the shortcut", agent.RetryFailed(keys, errors));
                T.Eq("the error is cleared after the retry", 0, errors[CapAction.ShotRegion]);
            }
            finally
            {
                agent.Dispose();
                holder.Dispose();
            }
        }

        // ---------- имя файла ----------
        private static void Templates()
        {
            DateTime when = new DateTime(2026, 9, 13, 21, 5, 7);
            T.Eq("default template", "chrome_2026-09-13_21-05-07", NameTemplate.Expand(NameTemplate.Default, "chrome", when));
            T.Eq("custom date tokens", "shot 13.09.26 2105", NameTemplate.Expand("shot {dd.MM.yy} {HHmm}", "x", when));
            T.Eq("path separators in the template cannot leave the folder", "_.._evil_x", NameTemplate.Expand("\\..\\evil\\{app}", "x", when));
            T.Eq("a reserved device name is defused", "_CON", NameTemplate.SanitizeSegment("CON"));
            T.Eq("a program name with bad characters", "a_b_c", NameTemplate.SanitizeSegment("a:b|c"));
            T.Eq("an empty program name becomes «screen»", "screen", NameTemplate.Expand("{app}", "", when));
            T.Eq("a template that sanitises to nothing falls back to program and time", "x_2026-09-13_21-05-07", NameTemplate.Expand("...", "x", when));

            string root = Fx.MakeDir(Fx.Root, "capture-names");
            string taken = Path.Combine(Path.Combine(root, "chrome"), "chrome_2026-09-13_21-05-07.png");
            string path = NameTemplate.BuildPath(root, true, "chrome", NameTemplate.Default, when, ".png",
                                                 delegate(string p) { return string.Equals(p, taken, StringComparison.OrdinalIgnoreCase); });
            T.Eq("a taken name gets _2 in the program's folder", Path.Combine(Path.Combine(root, "chrome"), "chrome_2026-09-13_21-05-07_2.png"), path);
            string flat = NameTemplate.BuildPath(root, false, "..\\..", "{app}", when, "jpg", delegate(string p) { return false; });
            T.Check("a hostile program name stays inside the root", NameTemplate.IsUnder(flat, root) && Path.GetDirectoryName(flat) == root, flat);
            T.Check("a sibling folder with the same prefix is not inside", !NameTemplate.IsUnder(root + "-other\\a.png", root));
        }

        // ---------- настройки ----------
        private static void Settings()
        {
            CapSettings s = new CapSettings();
            s.Enabled = true; s.ShotFolder = "D:\\Shots"; s.PerAppFolders = false; s.NameTemplate = "{app}-{HHmmss}";
            s.ImageFormat = "jpg"; s.JpegQuality = 85; s.After = ShotAfter.CopyOnly; s.ScreenKey = ScreenTarget.AllMonitors;
            s.DelaySeconds = 5; s.CursorInShots = true; s.ToastSeconds = 10; s.DeferToastsInFullscreen = false;
            s.LastRegion = new Rectangle(-1920, 100, 640, 480);
            s.Hotkeys[CapAction.ShotWindow] = "Ctrl+Shift+PrtScn";
            s.Hotkeys[CapAction.ShotRegion] = "";
            CapSettings back = CapSettings.FromJson(s.ToJson());
            T.Check("capture settings survive a round trip",
                    back != null && back.Enabled && back.ShotFolder == "D:\\Shots" && !back.PerAppFolders && back.NameTemplate == "{app}-{HHmmss}"
                    && back.ImageFormat == "jpg" && back.JpegQuality == 85 && back.After == ShotAfter.CopyOnly
                    && back.ScreenKey == ScreenTarget.AllMonitors && back.DelaySeconds == 5 && back.CursorInShots
                    && back.ToastSeconds == 10 && !back.DeferToastsInFullscreen);
            T.Eq("a region on a left monitor (negative X) survives", new Rectangle(-1920, 100, 640, 480), back.LastRegion);
            T.Eq("an assigned shortcut survives", "Ctrl+Shift+PrtScn", back.Hotkey(CapAction.ShotWindow).ToString());
            T.Check("capture is on out of the box: hotkeys work without pressing Start", new CapSettings().Enabled && CapSettings.FromJson("{}").Enabled);
            CapSettings stopped = new CapSettings();
            stopped.Enabled = false;
            T.Check("a process the user stopped stays stopped", !CapSettings.FromJson(stopped.ToJson()).Enabled);
            T.Check("an early build's \"Enabled\": false (written just by opening the page) does not block the start",
                    CapSettings.FromJson("{\"Enabled\": false}").Enabled);
            T.Check("a cleared shortcut stays cleared, not reset to F3", back.Hotkey(CapAction.ShotRegion).IsEmpty);

            // Сломанный путь: settings.json без «Version» с «ToastEnabled»: false — уведомление
            // о сохранённом снимке не показывалось никогда. Файл ранней сборки один раз включает уведомления;
            // выключенные после этого (файл с версией) остаются выключенными.
            T.Check("an early build's \"ToastEnabled\": false turns toasts back on once",
                    CapSettings.FromJson("{\"ToastEnabled\": false, \"ToastSeconds\": 3}").ToastEnabled);
            CapSettings quiet = new CapSettings();
            quiet.ToastEnabled = false;
            string quietJson = quiet.ToJson();
            T.Check("the settings file records its format version", quietJson.Contains("\"Version\""), quietJson.Substring(0, Math.Min(60, quietJson.Length)));
            T.Check("toasts the user turned off in the current format stay off", !CapSettings.FromJson(quietJson).ToastEnabled);
            T.Check("toasts turned off survive a clone", !quiet.Clone().ToastEnabled);

            CapSettings odd = CapSettings.FromJson("{\"ToastSeconds\": 99, \"DelaySeconds\": 7, \"JpegQuality\": 0, \"ImageFormat\": \"gif\","
                                                   + " \"After\": \"Explode\", \"NameTemplate\": \"  \", \"Hotkeys\": {\"ShotScreen\": \"Hyper+Q\"}}");
            T.Eq("toast time is clamped", CapSettings.MaxToastSeconds, odd.ToastSeconds);
            T.Eq("an unsupported delay becomes none", 0, odd.DelaySeconds);
            T.Eq("JPEG quality is clamped", 1, odd.JpegQuality);
            T.Eq("an unknown format becomes png", "png", odd.ImageFormat);
            T.Eq("an unknown after-action keeps the default", ShotAfter.SaveAndCopy, odd.After);
            T.Eq("a blank template becomes the default", NameTemplate.Default, odd.NameTemplate);
            T.Eq("an unparsable shortcut keeps the default", "F4", odd.Hotkey(CapAction.ShotScreen).ToString());
            T.Check("garbage is not a settings object", CapSettings.FromJson("{nope") == null);

            string file = Path.Combine(Fx.MakeDir(Fx.Root, "capture-settings"), "settings.json");
            File.WriteAllText(file, "\u0000garbage");
            T.Eq("a broken file loads defaults", "F3", CapSettings.Load(file).Hotkey(CapAction.ShotRegion).ToString());
            s.Save(file);
            T.Eq("save then load keeps the format", "jpg", CapSettings.Load(file).ImageFormat);
            // Другой процесс заменяет файл (File.Replace) — чтение на миг получает «файл занят». Раньше Load отдавал
            // умолчания, и следующий Save затирал ими все настройки.
            using (ManualResetEvent held = new ManualResetEvent(false))
            {
                Thread locker = new Thread(delegate()
                {
                    using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        held.Set();
                        Thread.Sleep(200);
                    }
                });
                locker.Start();
                held.WaitOne();
                T.Eq("a settings file busy for a moment is waited for, not replaced by defaults", "jpg", CapSettings.Load(file).ImageFormat);
                locker.Join();
            }
            T.Check("the capture data folder is redirected with the app's", Fx.IsUnder(CapPaths.DataDir, Fx.Root), CapPaths.DataDir);

            CapSettings v = new CapSettings();
            v.VideoFolder = "D:\\Видео"; v.VideoCodec = "av1"; v.VideoEncoder = "intel"; v.VideoQuality = "max"; v.VideoFps = 30;
            v.VideoHeight = 720; v.CursorInVideo = false; v.SystemAudio = false; v.WindowAudioOnly = true; v.Microphone = true;
            v.MicDeviceId = "{0.0.1.00000000}.{abc}"; v.MicVolume = 250; v.MicMono = true; v.SeparateTracks = true;
            v.CountdownSeconds = 5; v.MaxMinutes = 45; v.RecordPanel = false;
            CapSettings vb = CapSettings.FromJson(v.ToJson());
            T.Check("video and sound settings survive a round trip",
                    vb != null && vb.VideoFolder == "D:\\Видео" && vb.VideoCodec == "av1" && vb.VideoEncoder == "intel" && vb.VideoQuality == "max"
                    && vb.VideoFps == 30 && vb.VideoHeight == 720 && !vb.CursorInVideo && !vb.SystemAudio && vb.WindowAudioOnly && vb.Microphone
                    && vb.MicDeviceId == "{0.0.1.00000000}.{abc}" && vb.MicVolume == 250 && vb.MicMono && vb.SeparateTracks
                    && vb.CountdownSeconds == 5 && vb.MaxMinutes == 45 && !vb.RecordPanel);
            CapSettings vodd = CapSettings.FromJson("{\"VideoCodec\": \"vp9\", \"VideoEncoder\": \"voodoo\", \"VideoQuality\": \"ultra\", \"VideoFps\": 144,"
                                                    + " \"VideoHeight\": 480, \"MicVolume\": 9000, \"CountdownSeconds\": 4, \"MaxMinutes\": -3}");
            T.Eq("an unknown codec keeps H.264", "h264", vodd.VideoCodec);
            T.Eq("an unknown encoder keeps auto", "auto", vodd.VideoEncoder);
            T.Eq("an unknown quality keeps optimal", "optimal", vodd.VideoQuality);
            T.Eq("an unsupported frame rate becomes 60", 60, vodd.VideoFps);
            T.Eq("an unsupported output height means «as the source»", 0, vodd.VideoHeight);
            T.Eq("microphone volume is clamped to 400 %", 400, vodd.MicVolume);
            T.Eq("an unsupported countdown becomes none", 0, vodd.CountdownSeconds);
            T.Eq("a negative length limit means no limit", 0, vodd.MaxMinutes);
            T.Check("the video folder defaults under the user's Videos", new CapSettings().EffectiveVideoFolder.EndsWith("\\Captures", StringComparison.OrdinalIgnoreCase));
        }

        // ---------- перенос из GameCenter ----------
        private static void GameCenter()
        {
            string dir = Fx.MakeDir(Fx.Root, "capture-gamecenter");
            string shots = Fx.MakeDir(dir, "Скрины");
            // Тот же вид, что у настоящего GameCenter.ini: UTF-16 с BOM, скан-коды DirectInput.
            string ini = "[General]\r\nLang=ru\r\n[HotKeys]\r\nScreenSelect=61:0\r\nFullscreenShot=62:0\r\nVideoSelect=65:0\r\n\r\n"
                         + "[VideoCapture]\r\nHotKeyTryBindUsed=1\r\nFolder=Q:\\нет такого диска\\\r\n[Microphone]\r\nAuxiliaryAudioEnabled=1\r\n"
                         + "[Screens]\r\nFolder=" + shots + "\\\r\n";
            string file = Path.Combine(dir, "GameCenter.ini");
            File.WriteAllText(file, ini, Encoding.Unicode);
            GameCenterImport gc = GameCenterImport.Read(file);
            T.Check("GameCenter.ini (UTF-16) is read", gc != null);
            if (gc == null) return;
            T.Eq("ScreenSelect 61:0 is F3", "F3", gc.Hotkeys[CapAction.ShotRegion].ToString());
            T.Eq("FullscreenShot 62:0 is F4", "F4", gc.Hotkeys[CapAction.ShotScreen].ToString());
            T.Eq("VideoSelect 65:0 is F7", "F7", gc.Hotkeys[CapAction.RecRegion].ToString());
            T.Eq("the Cyrillic screenshot folder is read", shots + "\\", gc.ShotFolder);

            CapSettings s = new CapSettings();
            s.Hotkeys[CapAction.ShotRegion] = "Ctrl+F1";
            gc.ApplyTo(s);
            T.Eq("import sets the region shortcut", "F3", s.Hotkey(CapAction.ShotRegion).ToString());
            T.Eq("an existing folder is imported without the trailing slash", shots, s.ShotFolder);
            T.Eq("a folder on a missing drive is not imported", "", s.VideoFolder);
            T.Check("an enabled GameCenter microphone turns the microphone on", s.Microphone);
            CapSettings keep = new CapSettings();
            keep.Microphone = true;
            GameCenterImport.Parse("[HotKeys]\nScreenSelect=61:0\n").ApplyTo(keep);
            T.Check("an ini without [Microphone] leaves the microphone choice alone", keep.Microphone);
            GameCenterImport.Parse("[Microphone]\nAuxiliaryAudioEnabled=0\n").ApplyTo(keep);
            T.Check("a disabled GameCenter microphone turns it off", !keep.Microphone);
            T.Check("a microphone-only ini is still something to import", !GameCenterImport.Parse("[Microphone]\nAuxiliaryAudioEnabled=1\n").IsEmpty);

            GameCenterImport mods = GameCenterImport.Parse("[HotKeys]\nScreenSelect=61:4\nFullscreenShot=junk\nVideoSelect=\n");
            T.Eq("shortcuts with an undecoded modifier are skipped, not guessed", 2, mods.SkippedKeys);
            T.Eq("nothing is taken from them", 0, mods.Hotkeys.Count);
            T.Check("a missing file is simply nothing to import", GameCenterImport.Read(Path.Combine(dir, "absent.ini")) == null);

            T.Eq("DIK F1 is VK_F1", 0x70u, GameCenterImport.VkFromDik(0x3B));
            T.Eq("DIK F12 is VK_F12", 0x7Bu, GameCenterImport.VkFromDik(0x58));
            T.Eq("DIK SYSRQ (0xB7) is PrtScn", 0x2Cu, GameCenterImport.VkFromDik(0xB7));
            T.Eq("DIK A is VK_A whatever the layout", 0x41u, GameCenterImport.VkFromDik(0x1E));
        }

        // ---------- видео: параметры записи из настроек ----------
        private static void RecordingPlan()
        {
            MonitorInfo mon = new MonitorInfo();
            mon.Bounds = new Rectangle(-1920, 0, 1920, 1080);
            mon.WorkArea = new Rectangle(-1920, 0, 1920, 1040);
            CapSettings s = new CapSettings();
            RecordTarget t = new RecordTarget();
            t.Monitor = mon;
            t.Area = new Rectangle(-101, 1000, 301, 301);

            VideoOptions o = RecordPlan.Video(s, t, "C:\\v.mp4");
            T.Eq("a region is cut to its monitor and made even for the NV12 encoder", new Rectangle(-101, 1000, 100, 80), o.Area);
            T.Check("defaults: 60 fps, cursor on, no length limit", o.Fps == 60 && o.Cursor && o.MaxDuration == TimeSpan.Zero && o.OutputHeight == 0);
            t.Area = new Rectangle(-1920, 0, 1920, 1080);
            s.VideoHeight = 720; s.VideoFps = 30; s.MaxMinutes = 10; s.CursorInVideo = false;
            o = RecordPlan.Video(s, t, "C:\\v.mp4");
            T.Check("1080p screen with a 720p cap is scaled down; 30 fps; 10 min limit; no cursor",
                    o.OutputHeight == 720 && o.Fps == 30 && o.MaxDuration == TimeSpan.FromMinutes(10) && !o.Cursor, o.OutputHeight + " " + o.Fps + " " + o.MaxDuration);
            s.VideoHeight = 1080;
            t.Area = new Rectangle(-1000, 100, 640, 480);
            T.Eq("a small region is never scaled up", 0, RecordPlan.Video(s, t, "C:\\v.mp4").OutputHeight);

            s.SystemAudio = false; s.Microphone = false;
            T.Check("no system sound and no microphone means a silent video", RecordPlan.Audio(s, t) == null);
            s.SystemAudio = true; s.WindowAudioOnly = true; s.SeparateTracks = true;
            t.Pid = 4242;
            AudioCaptureOptions a = RecordPlan.Audio(s, t);
            T.Eq("a region is not a window: the whole system sound is taken", 0, a.ProcessId);
            T.Check("separate tracks need both sources", !a.SeparateTracks);
            t.Window = new IntPtr(0x1234);
            s.Microphone = true; s.MicVolume = 150; s.MicDeviceId = "";
            a = RecordPlan.Audio(s, t);
            T.Eq("a recorded window with «only its sound» takes that process", 4242, a.ProcessId);
            T.Check("system + microphone with separate tracks keeps them; the default microphone is null",
                    a.SeparateTracks && a.Microphone && a.MicDeviceId == null && Math.Abs(a.MicVolume - 1.5f) < 0.001f);
            s.WindowAudioOnly = false;
            T.Eq("without «only its sound» a window still gets the whole system sound", 0, RecordPlan.Audio(s, t).ProcessId);
            T.Eq("no capture, no tracks", 0, RecordPlan.Tracks(null).Count);
            s.MicDeviceId = "{0.0.1.00000000}.{headset}";
            List<AudioDeviceInfo> present = new List<AudioDeviceInfo>();
            present.Add(new AudioDeviceInfo { Id = "{0.0.1.00000000}.{builtin}", Name = "Built-in" });
            a = RecordPlan.Audio(s, t);
            string note = RecordPlan.ResolveMicrophone(a, present);
            T.Check("an unplugged selected microphone falls back to the default one with a warning", note != null && a.MicDeviceId == null, note);
            present.Add(new AudioDeviceInfo { Id = "{0.0.1.00000000}.{HEADSET}", Name = "Headset" });
            a = RecordPlan.Audio(s, t);
            T.Check("a connected selected microphone is kept (ids compare without case)",
                    RecordPlan.ResolveMicrophone(a, present) == null && a.MicDeviceId == "{0.0.1.00000000}.{headset}");

            T.Eq("elapsed under an hour is mm:ss", "02:14", RecordPlan.FormatElapsed(TimeSpan.FromSeconds(134.9)));
            T.Eq("elapsed over an hour gets hours", "1:00:05", RecordPlan.FormatElapsed(TimeSpan.FromSeconds(3605)));
            T.Eq("a negative elapsed is zero", "00:00", RecordPlan.FormatElapsed(TimeSpan.FromSeconds(-3)));

            T.Check("a user stop needs no explanation", RecordPlan.ReasonText("stopped", false) == null && RecordPlan.ReasonText(null, true) == null);
            string lost = RecordPlan.ReasonText("device-lost", true), reset = RecordPlan.ReasonText("device-lost", false);
            T.Check("a lost window source says the window closed, a lost screen says the device reset",
                    lost != reset && (lost.Contains("окно") || lost.Contains("window")), lost + " / " + reset);
            string err = RecordPlan.ReasonText("error: MF_E_SINK_NO_STREAMS", false);
            T.Check("an engine error keeps its detail", err.EndsWith("MF_E_SINK_NO_STREAMS") && !err.StartsWith("error:"), err);
            T.Check("each limit code has its own text", RecordPlan.ReasonText("limit-duration", false) != RecordPlan.ReasonText("low-disk", false)
                    && RecordPlan.ReasonText("limit-size", false) != RecordPlan.ReasonText("limit-duration", false));

            Rectangle work = new Rectangle(0, 0, 1920, 1040);
            Size panel = new Size(300, 40);
            T.Eq("the panel sits under a region with room below", new Rectangle(250, 508, 300, 40), RecordPanel.Place(new Rectangle(100, 100, 600, 400), work, panel, 8));
            T.Eq("no room below puts it above", new Rectangle(250, 552, 300, 40), RecordPanel.Place(new Rectangle(100, 600, 600, 420), work, panel, 8));
            Rectangle full = RecordPanel.Place(work, work, panel, 8);
            T.Check("a full-screen region keeps the panel inside the monitor", work.Contains(full), full.ToString());
            T.Eq("a region at the right edge keeps the panel on screen", 1620, RecordPanel.Place(new Rectangle(1800, 100, 120, 100), work, panel, 8).X);

            T.Check("recording pause is a known command", CapIpc.IsCommand("RecPause") && CapIpc.IsCommand("RecStop"));
        }

        // ---------- видео: метка незаконченной записи ----------
        private static void RecordingMarker()
        {
            RecordMarker.Clear();
            T.Check("no marker, nothing to recover", RecordMarker.Pending() == null);
            string video = Path.Combine(Fx.MakeDir(Fx.Root, "capture-marker"), "clip.mp4");
            RecordMarker.Set(video);
            T.Check("the marker lives in the redirected data folder", Fx.IsUnder(RecordMarker.File, Fx.Root), RecordMarker.File);
            T.Eq("the marker names the file being recorded", video, RecordMarker.Pending());
            string problem;
            T.Check("a marker for a file that never appeared is silently dropped", RecordMarker.Recover(video, out problem) == null && problem == null);
            File.WriteAllText(Path.ChangeExtension(video, ".txt"), "not a video");
            T.Check("a marker pointing at a non-video is ignored", RecordMarker.Recover(Path.ChangeExtension(video, ".txt"), out problem) == null && problem == null);
            RecordMarker.Clear();
            T.Check("the marker is cleared", RecordMarker.Pending() == null);
        }

        // ---------- выделение ----------
        private static void Geometry()
        {
            T.Eq("dragging up-left is normalised, both end pixels included", new Rectangle(10, 20, 91, 81),
                 RegionMath.Normalize(new Point(100, 100), new Point(10, 20)));
            T.Eq("16:9 from a wide drag keeps the height", new Rectangle(0, 0, 160, 90),
                 RegionMath.WithRatio(new Point(0, 0), new Point(499, 89), 16.0 / 9.0));
            T.Eq("16:9 grows towards the cursor on the left", new Rectangle(-159, 0, 160, 90),
                 RegionMath.WithRatio(new Point(0, 0), new Point(-499, 89), 16.0 / 9.0));
            T.Eq("moving stops at the screen edge", new Rectangle(900, 0, 100, 50),
                 RegionMath.MoveWithin(new Rectangle(880, 10, 100, 50), 500, -500, new Rectangle(0, 0, 1000, 800)));
            T.Eq("the right-bottom handle resizes", new Rectangle(10, 10, 150, 70),
                 RegionMath.Resize(new Rectangle(10, 10, 100, 50), 4, 50, 20, new Rectangle(0, 0, 1000, 800)));
            Rectangle flipped = RegionMath.Resize(new Rectangle(10, 10, 100, 50), 3, -300, 0, new Rectangle(0, 0, 1000, 800));
            T.Check("dragging a side past the other side never makes an empty or negative area", flipped.Width >= 1 && flipped.Height == 50, flipped.ToString());
            T.Eq("video sizes are even", new Size(1280, 718), RegionMath.EvenSize(new Size(1281, 719)));
            T.Eq("a 1-pixel video size becomes 2", new Size(2, 2), RegionMath.EvenSize(new Size(1, 1)));
        }

        // ---------- картинки ----------
        private static void Images()
        {
            using (Bitmap big = new Bitmap(2560, 1440, PixelFormat.Format32bppRgb))
            using (Bitmap thumb = ToastHost.MakeThumbnail(big))
                T.Eq("a 1440p thumbnail is 320x180", new Size(320, 180), thumb.Size);
            using (Bitmap small = new Bitmap(100, 50, PixelFormat.Format32bppRgb))
            using (Bitmap thumb = ToastHost.MakeThumbnail(small))
                T.Eq("a small shot is not upscaled", new Size(100, 50), thumb.Size);
            using (Bitmap tall = new Bitmap(400, 4000, PixelFormat.Format32bppRgb))
            using (Bitmap thumb = ToastHost.MakeThumbnail(tall))
                T.Eq("a tall shot fits the height", new Size(18, 180), thumb.Size);

            // Кадр виртуального экрана начинается в (-1920, 0): левый монитор.
            using (Bitmap frame = new Bitmap(3840, 1080, PixelFormat.Format32bppRgb))
            {
                frame.SetPixel(1920 + 5, 7, Color.FromArgb(255, 10, 200, 30));
                using (Bitmap crop = ScreenGrab.Crop(frame, new Point(-1920, 0), new Rectangle(5, 7, 4, 4)))
                {
                    Color c = crop.GetPixel(0, 0);
                    T.Check("the crop takes screen coordinates relative to the frame origin", c.R == 10 && c.G == 200 && c.B == 30, c.ToString());
                    T.Eq("the crop has the requested size", new Size(4, 4), crop.Size);
                }
            }

            string png = Path.Combine(Fx.MakeDir(Fx.Root, "capture-images"), "shot.png");
            using (Bitmap b = new Bitmap(8, 8, PixelFormat.Format32bppRgb))
            {
                b.SetPixel(3, 3, Color.Red);
                ImageStore.Save(b, png, "png", 92);
            }
            using (Image back = Image.FromFile(png))
                T.Eq("a saved PNG is a real PNG of the right size", new Size(8, 8), back.Size);
            T.Check("the saved file is recognised as an image", ImageStore.IsImage(png));
        }

        // ---------- связь процессов ----------
        private static void Ipc()
        {
            T.Check("hotkey suspension is a known command", CapIpc.IsCommand("HotkeysOff") && CapIpc.IsCommand("HotkeysOn"));
            T.Check("there is no command that could delete or run a file", !CapIpc.IsCommand("Delete") && !CapIpc.IsCommand("Run") && !CapIpc.IsCommand("Exec"));
            T.Eq("--capture-shot screen maps to ShotScreen", "ShotScreen", CapMode.ShotCommand("Screen"));
            T.Eq("an unknown shot kind maps to nothing", null, CapMode.ShotCommand("everything"));
            T.Eq("the autostart command quotes the exe", "\"C:\\Program Files\\WPC\\app.exe\" --capture", CapLauncher.RunCommand("C:\\Program Files\\WPC\\app.exe"));
        }

        // ---------- редактор: история правок ----------
        private static StepShape Step(float x, float y)
        {
            StepShape s = new StepShape();
            s.Center = new PointF(x, y);
            return s;
        }

        private static bool Near(PointF p, float x, float y) { return Math.Abs(p.X - x) < 0.01f && Math.Abs(p.Y - y) < 0.01f; }
    }
}
