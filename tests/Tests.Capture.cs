// Windows Process Cleaner — область «capture»: сочетания клавиш (разбор, перенос из GameCenter, занятость и повторная
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
using System.Windows.Forms;
using WindowsProcessCleaner.Capture;

namespace WindowsProcessCleaner.Tests
{
    internal static class CaptureTests
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
        // Жалоба владельца 14.09.2026: «Галерея» открывала Проводник, а никакого окна со снимками в программе не было.
        // Проверяется то, на что он нажимал: команда агента «Галерея» и чтение папок, из которых окно берёт список.
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

            // Сломанный путь владельца (14.09.2026): settings.json без «Version» с «ToastEnabled»: false — уведомление
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

        private static void EditorHistory()
        {
            using (EditorDoc doc = new EditorDoc(new Bitmap(200, 100, PixelFormat.Format32bppRgb)))
            {
                T.Eq("a screen grab (32bppRgb) is kept as 32bppArgb", PixelFormat.Format32bppArgb, doc.Image.PixelFormat);
                T.Check("a fresh document is not dirty and has no history", !doc.Dirty && !doc.CanUndo && !doc.CanRedo);
                doc.Add(Step(1, 1));
                doc.Add(Step(2, 2));
                doc.Add(Step(3, 3));
                T.Check("undo twice leaves one shape", doc.Undo() && doc.Undo() && doc.Shapes.Count == 1);
                T.Check("redo brings the second back", doc.Redo() && doc.Shapes.Count == 2 && doc.CanRedo);
                doc.Add(Step(9, 9));
                T.Check("a new edit after undo drops the redo branch", !doc.CanRedo && doc.Shapes.Count == 3);

                doc.MarkSaved();
                T.Check("saved state is clean", !doc.Dirty);
                doc.Undo();
                T.Check("undo past the save is dirty", doc.Dirty);
                doc.Redo();
                T.Check("redo back to the saved state is clean again", !doc.Dirty);

                // Правка на месте (перетаскивание) и откат по Esc.
                CapShape moved = doc.Shapes[0];
                EditSnapshot before = doc.Capture();
                moved.Offset(50, 50);
                doc.Revert(before);
                T.Check("Esc during a drag restores the position without a history step",
                        Near(((StepShape)doc.Shapes[0]).Center, 1, 1) && !doc.Dirty);

                StrokeShape stroke = new StrokeShape();
                stroke.Points.Add(new PointF(0, 0));
                doc.Add(stroke);
                doc.Change(delegate { stroke.Points.Add(new PointF(10, 10)); });
                doc.Undo();
                T.Eq("undo restores a stroke's own point list, not a shared one", 1, ((StrokeShape)doc.Shapes[doc.Shapes.Count - 1]).Points.Count);

                for (int i = 0; i < EditorDoc.MaxHistory + 20; i++) doc.Add(Step(i, i));
                T.Eq("history is capped", EditorDoc.MaxHistory, doc.UndoDepth);
            }

            using (EditorDoc doc = new EditorDoc(new Bitmap(200, 100, PixelFormat.Format32bppArgb)))
            {
                doc.Add(Step(50, 40));
                T.Check("crop to the same size is not an edit", !doc.Crop(new Rectangle(0, 0, 200, 100)));
                T.Check("crop outside the image is not an edit", !doc.Crop(new Rectangle(500, 500, 10, 10)));
                T.Check("crop applies", doc.Crop(new Rectangle(20, 10, 100, 60)));
                T.Eq("the image takes the cropped size", new Size(100, 60), doc.Size);
                T.Check("shapes move with the crop origin", Near(((StepShape)doc.Shapes[0]).Center, 30, 30));
                T.Eq("the pre-crop image is kept for undo", 2, doc.LiveImages);
                doc.Undo();
                T.Check("undo of a crop restores size and coordinates", doc.Size == new Size(200, 100) && Near(((StepShape)doc.Shapes[0]).Center, 50, 40));
                doc.Add(Step(1, 1));
                T.Eq("an image only the dropped redo branch used is released", 1, doc.LiveImages);
            }
        }

        // ---------- редактор: геометрия фигур ----------
        private static void EditorGeometry()
        {
            using (Bitmap src = new Bitmap(200, 100, PixelFormat.Format32bppArgb))
            {
                src.SetPixel(10, 20, Color.Red);
                using (EditorDoc doc = new EditorDoc((Bitmap)src.Clone()))
                {
                    doc.Add(Step(10.5f, 20.5f));
                    doc.Rotate(true);
                    T.Eq("rotating right swaps the sides", new Size(100, 200), doc.Size);
                    Color moved = doc.Image.GetPixel(100 - 1 - 20, 10);
                    T.Check("the pixel lands where RotateFlip put it", moved.ToArgb() == Color.Red.ToArgb(), moved.ToString());
                    T.Check("a shape at a pixel centre follows the same pixel", Near(((StepShape)doc.Shapes[0]).Center, 100 - 20.5f, 10.5f),
                            ((StepShape)doc.Shapes[0]).Center.ToString());
                    doc.Rotate(false);
                    T.Check("rotating left undoes rotating right", doc.Size == new Size(200, 100) && Near(((StepShape)doc.Shapes[0]).Center, 10.5f, 20.5f));
                    T.Check("the pixel is back in place", doc.Image.GetPixel(10, 20).ToArgb() == Color.Red.ToArgb());
                }
            }

            LineShape line = new LineShape();
            line.A = new PointF(0, 0);
            line.B = new PointF(100, 0);
            line.Width = 4;
            T.Check("a click on the line hits it", line.HitTest(new PointF(50, 3), 2));
            T.Check("a click beside the line misses", !line.HitTest(new PointF(50, 10), 2));
            line.Arrow = true;
            T.Check("the arrow head is hit beside its tip", line.HitTest(new PointF(95, 7), 1));
            PointF[] head;
            line.ArrowGeometry(out head);
            T.Check("the arrow head ends at the arrow's end point", Near(head[0], 100, 0));

            BoxShape ellipse = new BoxShape();
            ellipse.Ellipse = true;
            ellipse.Rect = new RectangleF(0, 0, 100, 50);
            T.Check("an outlined ellipse is hit on its edge", ellipse.HitTest(new PointF(50, 1), 3));
            T.Check("an outlined ellipse is not hit in its hollow middle", !ellipse.HitTest(new PointF(50, 25), 3));
            T.Check("an outlined ellipse is not hit at the corner of its box", !ellipse.HitTest(new PointF(2, 2), 3));
            ellipse.Fill = true;
            T.Check("a filled ellipse is hit in the middle", ellipse.HitTest(new PointF(50, 25), 3));

            TextShape text = new TextShape();
            text.Origin = new PointF(100, 100);
            text.Text = "Привет\nмир";
            RectangleF tb = text.Bounds;
            T.Check("two text lines are taller than one", tb.Height > text.FontSize * 1.5f && tb.Contains(110, 120), tb.ToString());
            RectangleF before = text.Bounds;
            text.Map(delegate(PointF p) { return new PointF(1000 - p.Y, p.X); });
            RectangleF after = text.Bounds;
            T.Check("text keeps its size after a rotation (stays upright)", Math.Abs(after.Width - before.Width) < 0.5f && Math.Abs(after.Height - before.Height) < 0.5f);
            T.Check("the text centre follows the rotation",
                    Near(new PointF(after.X + after.Width / 2, after.Y + after.Height / 2), 1000 - (before.Y + before.Height / 2), before.X + before.Width / 2));

            // Живая проверка: угол области скрытия, перетянутый на противоположную сторону, давал ширину 0 — область
            // сохранялась невидимой и ничего не скрывала.
            RectangleF squashed = EditGeometry.Resize(new RectangleF(200, 245, 360, 45), 6, 360, 45);
            T.Check("a handle dragged exactly onto the opposite side leaves a visible area", squashed.Width >= 1 && squashed.Height >= 1, squashed.ToString());

            PointF snapped = EditorCanvas.Snap45(new PointF(0, 0), new PointF(10, 1));
            T.Check("Shift snaps a nearly flat line to horizontal", Math.Abs(snapped.Y) < 0.001f && snapped.X > 10, snapped.ToString());
            T.Check("Shift makes a square towards the cursor", Near(EditorCanvas.SquareCorner(new PointF(10, 10), new PointF(0, 30)), -10, 30));
            T.Eq("zooming in from 90% stops at 100%", 1f, EditorCanvas.NextZoom(0.9f, 1));
            T.Eq("zooming out from 110% stops at 100%", 1f, EditorCanvas.NextZoom(1.1f, -1));
            T.Eq("zoom is capped", 16f, EditorCanvas.NextZoom(16f, 1));
        }

        // ---------- редактор: скрытие данных ----------
        // Шахматка 1px чёрное/белое: после размытия или пикселизации в области не остаётся ни одного чистого чёрного
        // или белого пикселя, а вне области не меняется ни один.
        private static Bitmap Checker(int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    b.SetPixel(x, y, (x + y) % 2 == 0 ? Color.Black : Color.White);
            return b;
        }

        private static string RedactionProblems(Bitmap result, Bitmap original, Rectangle area)
        {
            int outside = 0, pure = 0;
            for (int y = 0; y < result.Height; y++)
                for (int x = 0; x < result.Width; x++)
                {
                    int c = result.GetPixel(x, y).ToArgb();
                    if (area.Contains(x, y))
                    {
                        if (c == Color.Black.ToArgb() || c == Color.White.ToArgb()) pure++;
                    }
                    else if (c != original.GetPixel(x, y).ToArgb()) outside++;
                }
            return outside == 0 && pure == 0 ? null : "changed outside: " + outside + ", original pixels inside: " + pure;
        }

        private static void EditorRedaction()
        {
            Rectangle area = new Rectangle(16, 12, 40, 30);
            foreach (bool pixelate in new bool[] { true, false })
            {
                string kind = pixelate ? "pixelate" : "blur";
                using (Bitmap original = Checker(80, 60))
                using (EditorDoc doc = new EditorDoc((Bitmap)original.Clone()))
                {
                    RedactShape r = new RedactShape();
                    r.Pixelate = pixelate;
                    r.Rect = area;
                    doc.Add(r);
                    T.Check(kind + ": the document raster itself is not touched", RedactionProblems(doc.Image, original, Rectangle.Empty) == null);
                    using (Bitmap output = doc.Render(null))
                    {
                        string problems = RedactionProblems(output, original, area);
                        T.Check(kind + ": only the area changes and no original pixel survives in it", problems == null, problems);
                    }
                }
            }

            // Фигура под скрытием скрывается вместе с растром, фигура поверх — остаётся чёткой.
            using (EditorDoc doc = new EditorDoc(new Bitmap(64, 64, PixelFormat.Format32bppArgb)))
            {
                using (Graphics g = Graphics.FromImage(doc.Image)) g.Clear(Color.Black);
                LineShape under = new LineShape();
                under.A = new PointF(0, 32);
                under.B = new PointF(64, 32);
                under.Width = 3;
                under.Color = Color.White;
                doc.Add(under);
                RedactShape r = new RedactShape();
                r.Pixelate = true;
                r.Rect = new RectangleF(16, 16, 32, 32);
                doc.Add(r);
                LineShape over = (LineShape)under.Clone();
                over.A = new PointF(0, 40);
                over.B = new PointF(64, 40);
                doc.Add(over);
                using (Bitmap output = doc.Render(null))
                {
                    T.Check("a line drawn before the redaction is redacted too", output.GetPixel(30, 32).ToArgb() != Color.White.ToArgb(), output.GetPixel(30, 32).ToString());
                    T.Check("the same line outside the area is untouched", output.GetPixel(4, 32).ToArgb() == Color.White.ToArgb());
                    T.Check("a line drawn after the redaction stays crisp", output.GetPixel(30, 40).ToArgb() == Color.White.ToArgb(), output.GetPixel(30, 40).ToString());
                }
                using (Bitmap live = doc.Render(r))
                    T.Check("the canvas can render without the shape being dragged", live.GetPixel(30, 32).ToArgb() == Color.White.ToArgb());
            }
        }

        // ---------- редактор: настройки ----------
        private static void EditorSettings()
        {
            Color c;
            T.Check("#1E88E5 parses", HexColor.TryParse(" #1e88e5 ", out c) && c.ToArgb() == Color.FromArgb(30, 136, 229).ToArgb());
            T.Eq("a colour is written as #RRGGBB", "#1E88E5", HexColor.Format(Color.FromArgb(30, 136, 229)));
            T.Check("a colour name is not a hex colour", !HexColor.TryParse("red", out c));

            CapSettings s = new CapSettings();
            s.After = ShotAfter.OpenEditor;
            s.EditorColor = "#43A047";
            s.EditorWidth = 8;
            s.EditorFontSize = 48;
            s.EditorFill = true;
            s.EditorTool = EditTool.Pixelate;
            CapSettings back = CapSettings.FromJson(s.ToJson());
            T.Check("editor preferences survive a round trip",
                    back.After == ShotAfter.OpenEditor && back.EditorColor == "#43A047" && back.EditorWidth == 8 && back.EditorFontSize == 48
                    && back.EditorFill && back.EditorTool == EditTool.Pixelate);
            CapSettings odd = CapSettings.FromJson("{\"EditorColor\": \"red\", \"EditorWidth\": 999, \"EditorFontSize\": 1, \"EditorTool\": \"Crop\"}");
            T.Eq("a bad colour keeps the default", "#E53935", odd.EditorColor);
            T.Eq("a huge width is clamped", 40, odd.EditorWidth);
            T.Eq("a tiny font is clamped", 8, odd.EditorFontSize);
            T.Eq("the editor never opens in crop mode", EditTool.Arrow, odd.EditorTool);

            EditorStyle style = EditorStyle.From(back);
            CapSettings copy = new CapSettings();
            style.CopyTo(copy);
            T.Check("the editor style maps to settings and back", copy.EditorColor == "#43A047" && copy.EditorWidth == 8 && copy.EditorFill && copy.EditorTool == EditTool.Pixelate);
        }

        // ---------- редактор: сохранение настоящим окном ----------
        // Окно не показывается. Первое сохранение нового снимка — файл по шаблону в папке программы; перезапись
        // существующего файла отправляет прежний в Корзину, поэтому здесь не проверяется (тесты Корзину не трогают).
        private static void EditorSave()
        {
            string root = Fx.MakeDir(Fx.Root, "capture-editor");
            CapSettings s = new CapSettings();
            s.ShotFolder = root;
            s.PerAppFolders = true;
            s.NameTemplate = "{app}";
            Rectangle area = new Rectangle(10, 10, 30, 20);
            using (Bitmap original = Checker(60, 40))
            using (EditorForm form = new EditorForm((Bitmap)original.Clone(), "Test App", null, null, s))
            {
                RedactShape r = new RedactShape();
                r.Pixelate = true;
                r.Rect = area;
                form.Doc.Add(r);
                T.Check("an edited shot is dirty before saving", form.Doc.Dirty);
                T.Check("the editor saves a new shot", form.Save(false));
                string expected = Path.Combine(Path.Combine(root, "Test App"), "Test App.png");
                T.Eq("the file goes to the program's folder by the name template", expected, form.FilePath);
                T.Check("the document is clean after saving", !form.Doc.Dirty);
                if (File.Exists(expected))
                    using (Bitmap saved = ImageStore.LoadUnlocked(expected))
                    {
                        string problems = RedactionProblems(saved, original, area);
                        T.Check("the saved file keeps no original pixels under the pixelation", problems == null, problems);
                    }
                T.Check("no temporary file is left next to it", Directory.GetFiles(Path.GetDirectoryName(expected)).Length == 1);
            }
        }

        // ---------- редактор: мышь по настоящему холсту ----------
        // Сообщения мыши уходят в скрытое окно через SendMessage: курсор не двигается, фокус ни у кого не отнимается.
        // Снимок крупнее окна, поэтому холст уменьшен — ровно тот масштаб, при котором ручки задевали соседние фигуры.
        private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, MK_LBUTTON = 1, WM_KEYDOWN = 0x0100;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void EditorMouse()
        {
            CapSettings s = new CapSettings();
            s.ShotFolder = Fx.MakeDir(Fx.Root, "capture-editor-mouse");
            using (EditorForm form = new EditorForm(new Bitmap(1600, 900), "Test App", null, null, s))
            {
                form.ClientSize = new Size(900, 600);
                EditorCanvas canvas = form.Canvas;
                canvas.FitView();
                T.Check("the canvas shows the shot scaled down", canvas.Zoom < 0.7f, canvas.Zoom.ToString());

                form.Bar.SelectTool(EditTool.Pixelate);
                MouseDrag(canvas, new PointF(200, 245), new PointF(560, 290));
                // Размытие начинается в 5 px от нижнего левого угла только что нарисованной области.
                form.Bar.SelectTool(EditTool.Blur);
                MouseDrag(canvas, new PointF(200, 295), new PointF(560, 340));
                T.Eq("a blur started next to a pixelation corner is a new shape", 2, form.Doc.Shapes.Count);
                T.Check("the pixelation next to it keeps its size", Math.Abs(RedactRect(form, 0).Height - 45) < 1, RedactRect(form, 0).ToString());

                form.Bar.SelectTool(EditTool.Select);
                MouseDrag(canvas, new PointF(380, 268), new PointF(380, 268));
                int depth = form.Doc.UndoDepth;
                MouseDrag(canvas, new PointF(380, 290), new PointF(380, 245));
                T.Check("a handle dragged onto the opposite side leaves the area as it was, with no history step",
                        Math.Abs(RedactRect(form, 0).Height - 45) < 1 && form.Doc.UndoDepth == depth && form.Doc.Shapes.Count == 2,
                        RedactRect(form, 0) + " undo " + form.Doc.UndoDepth);

                MouseDrag(canvas, new PointF(380, 268), new PointF(380, 268));
                MouseDrag(canvas, new PointF(380, 290), new PointF(380, 310));
                T.Check("the Select tool still resizes by a handle", Math.Abs(RedactRect(form, 0).Height - 65) < 1, RedactRect(form, 0).ToString());
            }
        }

        // ---------- правки прямо в оверлее ----------
        // Оверлей не показывается: мышь — SendMessage в его окно (выделение, панель, ручки области) и в холст поверх
        // выделения. Сценарий пользователя: две рамки разного цвета, ручка уже нарисованной фигуры при инструменте
        // рисования, выделение щелчком и удаление, область растянута после рисования, «Сохранить».
        private static void OverlayInlineEdit()
        {
            Color mark = Color.FromArgb(0, 200, 0);
            Bitmap frame = new Bitmap(1600, 900, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(frame)) g.Clear(Color.FromArgb(90, 90, 90));
            frame.SetPixel(250, 70, mark);
            OverlayResult result = null;
            Color first, second;
            using (RegionOverlay overlay = new RegionOverlay(frame, new Rectangle(0, 0, 1600, 900), new List<MonitorInfo>(), new List<WindowCandidate>(), Rectangle.Empty))
            {
                overlay.Finished += delegate(RegionOverlay o, OverlayResult r) { result = r; };
                OverlayDrag(overlay, new Point(300, 100), new Point(1000, 500));
                Rectangle sel = (Rectangle)Field(overlay, "_sel");
                OverlayClick(overlay, "Tool:Rect");
                EditorCanvas canvas = (EditorCanvas)Field(overlay, "_canvas");
                T.Check("a tool on the overlay panel opens a canvas right over the selection", canvas != null && canvas.Bounds == sel, sel.ToString());
                if (canvas == null) return;
                EditorDoc doc = (EditorDoc)Field(overlay, "_doc");
                first = (Color)Field(PanelButtonOf(overlay, "Color:0"), "Color");
                second = (Color)Field(PanelButtonOf(overlay, "Color:2"), "Color");

                OverlayClick(overlay, "Color:0");
                MouseDrag(canvas, new PointF(20, 20), new PointF(220, 160));
                OverlayClick(overlay, "Color:2");
                MouseDrag(canvas, new PointF(300, 60), new PointF(500, 260));
                T.Check("a colour picked after a box applies to the next box only",
                        doc.Shapes.Count == 2 && doc.Shapes[0].Color.ToArgb() == first.ToArgb() && doc.Shapes[1].Color.ToArgb() == second.ToArgb(), Describe(doc));

                MouseDrag(canvas, new PointF(220, 160), new PointF(260, 200));
                T.Check("with a drawing tool the handle of an earlier box resizes it",
                        doc.Shapes.Count == 2 && Near(BoxRect(doc, 0), 20, 20, 240, 180), Describe(doc));

                MouseDrag(canvas, new PointF(20, 80), new PointF(20, 80));
                OverlayClick(overlay, "Delete");
                T.Check("a click on a box selects it and the panel deletes it",
                        doc.Shapes.Count == 1 && doc.Shapes[0].Color.ToArgb() == second.ToArgb(), Describe(doc));
                OverlayClick(overlay, "Undo");
                T.Eq("undo on the panel brings the box back", 2, doc.Shapes.Count);

                OverlayDrag(overlay, sel.Location, new Point(sel.X - 60, sel.Y - 40));
                sel = (Rectangle)Field(overlay, "_sel");
                T.Check("the region grows with the drawing on it: shapes stay where they are on screen",
                        canvas.Bounds == sel && doc.Size == sel.Size && Near(BoxRect(doc, 0), 80, 60, 240, 180) && Near(BoxRect(doc, 1), 360, 100, 200, 200),
                        sel + " " + Describe(doc));
                T.Check("the grown canvas shows the newly uncovered part of the frame", doc.Image.GetPixel(10, 10).ToArgb() == mark.ToArgb());
                // История, записанная до растягивания, отматывается в тех же местах экрана — в обе стороны.
                OverlayClick(overlay, "Undo");
                T.Check("undo after growing the region takes the resize back at the box's moved place",
                        doc.Shapes.Count == 2 && Near(BoxRect(doc, 0), 80, 60, 200, 140), Describe(doc));
                OverlayClick(overlay, "Redo");
                OverlayClick(overlay, "Redo");
                T.Check("redo after growing the region deletes the box at its moved place",
                        doc.Shapes.Count == 1 && Near(BoxRect(doc, 0), 360, 100, 200, 200), Describe(doc));
                OverlayClick(overlay, "Undo");

                OverlayClick(overlay, "Action:Save");
                Application.DoEvents();
                T.Check("Save hands the drawing over with the region",
                        result != null && result.Action == OverlayAction.Default && result.Area == sel && result.Doc == doc,
                        result == null ? "no result" : result.Action + " " + result.Area);
            }
            if (result == null || result.Doc == null) return;
            using (EditorDoc doc = result.Doc)
            using (Bitmap output = doc.Render(null))
            {
                Color edge = output.GetPixel(80, 150);
                T.Check("the saved picture has the first box in its own colour after the overlay is closed", edge.ToArgb() == first.ToArgb(), edge.ToString());
                T.Check("the saved picture keeps the frame under the drawing", output.GetPixel(10, 10).ToArgb() == mark.ToArgb());
            }
        }

        // ---------- «Сохранить» и «Отмена» оверлея — подписанные кнопки ----------
        // Замечание владельца 14.09.2026: значки без подписи не читаются как «сохранить» и «отменить». Кнопки проверяются
        // тем же путём, что и мышь пользователя: сообщения в окно оверлея; Enter и Esc — через PreProcessMessage,
        // как их передаёт цикл сообщений.
        private static void OverlayLabelledButtons()
        {
            {
                Rectangle all = new Rectangle(0, 0, 1600, 900);
                using (RegionOverlay overlay = new RegionOverlay(OverlayFrame(), all, new List<MonitorInfo>(), new List<WindowCandidate>(), Rectangle.Empty))
                {
                    OverlayDrag(overlay, new Point(300, 100), new Point(1000, 500));
                    object save = PanelButtonOf(overlay, "Action:Save"), cancel = PanelButtonOf(overlay, "Action:Cancel"), copy = PanelButtonOf(overlay, "Action:Copy");
                    Rectangle rs = BoundsOf(save), rc = BoundsOf(cancel), rcopy = BoundsOf(copy);
                    string saveText = OptField(save, "Caption") as string, cancelText = OptField(cancel, "Caption") as string;
                    T.Check("Save on the overlay panel is a labelled button, not a bare icon",
                            !string.IsNullOrEmpty(saveText) && rs.Width > rs.Height * 2, saveText + " " + rs);
                    T.Check("Save is the accent (primary) button", true.Equals(OptField(save, "Primary")));
                    T.Check("Cancel on the overlay panel is a labelled button", !string.IsNullOrEmpty(cancelText) && rc.Width > rc.Height * 3 / 2, cancelText + " " + rc);
                    T.Check("the labelled buttons close the panel: icons, then Save, then Cancel",
                            !rcopy.IsEmpty && rcopy.Right <= rs.Left && rs.Right <= rc.Left && rs.Y == rc.Y, rcopy + " " + rs + " " + rc);
                    Rectangle panel = (Rectangle)overlay.GetType().GetMethod("PanelRect", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(overlay, null);
                    T.Check("the wider panel still fits the screen", all.Contains(panel), panel.ToString());
                }

                T.Eq("a click on the labelled Cancel cancels", OverlayAction.Cancel, OverlayOutcome(all, delegate(RegionOverlay o) { OverlayClick(o, "Action:Cancel"); }));
                T.Eq("Enter still saves", OverlayAction.Default, OverlayOutcome(all, delegate(RegionOverlay o) { OverlayKey(o, Keys.Enter); }));
                T.Eq("Esc still cancels", OverlayAction.Cancel, OverlayOutcome(all, delegate(RegionOverlay o) { OverlayKey(o, Keys.Escape); }));

                // Монитор уже панели с подписями: главные кнопки сжимаются до значков, панель не вылезает за край.
                List<MonitorInfo> narrow = new List<MonitorInfo>();
                MonitorInfo left = new MonitorInfo();
                left.Device = "narrow"; left.Bounds = left.WorkArea = new Rectangle(0, 0, 420, 900);
                MonitorInfo right = new MonitorInfo();
                right.Device = "wide"; right.Bounds = right.WorkArea = new Rectangle(420, 0, 1180, 900);
                narrow.Add(left);
                narrow.Add(right);
                using (RegionOverlay overlay = new RegionOverlay(OverlayFrame(), all, narrow, new List<WindowCandidate>(), Rectangle.Empty))
                {
                    OverlayDrag(overlay, new Point(20, 100), new Point(400, 500));
                    Rectangle rs = BoundsOf(PanelButtonOf(overlay, "Action:Save"));
                    T.Check("on a monitor too narrow for captions Save falls back to an icon", false.Equals(OptField(overlay, "_captions")) && rs.Width == rs.Height,
                            rs.ToString());
                }

                using (RegionOverlay overlay = new RegionOverlay(OverlayFrame(), all, new List<MonitorInfo>(), new List<WindowCandidate>(), Rectangle.Empty, true))
                {
                    OverlayDrag(overlay, new Point(300, 100), new Point(1000, 500));
                    object record = PanelButtonOf(overlay, "Action:Record");
                    Rectangle rr = BoundsOf(record), rc = BoundsOf(PanelButtonOf(overlay, "Action:Cancel"));
                    T.Check("the video overlay labels Start recording, next to Cancel",
                            !string.IsNullOrEmpty(OptField(record, "Caption") as string) && rr.Width > rr.Height * 2 && rr.Right <= rc.Left, rr + " " + rc);
                }
            }
        }

        // Оверлей забирает кадр себе и освобождает его при закрытии — у каждого оверлея свой кадр.
        private static Bitmap OverlayFrame() { return new Bitmap(1600, 900, PixelFormat.Format32bppArgb); }

        private static OverlayAction? OverlayOutcome(Rectangle all, Action<RegionOverlay> act)
        {
            OverlayResult result = null;
            using (RegionOverlay overlay = new RegionOverlay(OverlayFrame(), all, new List<MonitorInfo>(), new List<WindowCandidate>(), Rectangle.Empty))
            {
                overlay.Finished += delegate(RegionOverlay o, OverlayResult r) { result = r; };
                OverlayDrag(overlay, new Point(300, 100), new Point(1000, 500));
                act(overlay);
                Application.DoEvents();
            }
            if (result == null) return null;
            if (result.Doc != null) result.Doc.Dispose();
            return result.Action;
        }

        private static void OverlayKey(Control target, Keys key)
        {
            Message m = Message.Create(target.Handle, WM_KEYDOWN, new IntPtr((int)key), IntPtr.Zero);
            target.PreProcessMessage(ref m);
        }

        private static Rectangle BoundsOf(object panelButton)
        {
            return panelButton == null ? Rectangle.Empty : (Rectangle)Field(panelButton, "Bounds");
        }

        // Поле, которого может не быть (проверка нового поведения на старом дереве даёт провал, а не исключение).
        private static object OptField(object o, string name)
        {
            if (o == null) return null;
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f == null ? null : f.GetValue(o);
        }

        // ---------- уведомление о снимке: показывается и открывает галерею ----------
        // Путь владельца: settings.json ранней сборки («ToastEnabled»: false, без «Version») → ToastHost.Apply → Show.
        // До исправления уведомление молча отбрасывалось. Окно показывается на секунду в углу основного монитора и
        // закрывается щелчком по «Галерее»; Проводник не открывается — событие перехватывает тест, а не агент.
        private static void ToastGallery()
        {
            string dir = Fx.MakeDir(Fx.Root, "capture-toast");
            string shots = Fx.MakeDir(dir, "shots"), videos = Fx.MakeDir(dir, "videos");
            string saved = Path.Combine(Fx.MakeDir(shots, "app"), "shot.png");
            using (Bitmap b = new Bitmap(8, 8, PixelFormat.Format32bppRgb)) b.Save(saved, ImageFormat.Png);
            string file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, "{\"ToastEnabled\": false, \"DeferToastsInFullscreen\": false, \"ShotFolder\": \"" + shots.Replace("\\", "\\\\")
                                    + "\", \"VideoFolder\": \"" + videos.Replace("\\", "\\\\") + "\"}");
            CapSettings s = CapSettings.Load(file);

            MethodInfo pick = typeof(AgentApp).GetMethod("GalleryFolder", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            T.Check("the agent opens the root screenshots folder for a saved shot",
                    pick != null && shots.Equals(pick.Invoke(null, new object[] { s, saved })));
            T.Check("the agent opens the root videos folder for a saved video",
                    pick != null && videos.Equals(pick.Invoke(null, new object[] { s, Path.Combine(videos, "clip.mp4") })));

            Screen screen = Screen.PrimaryScreen;
            MonitorInfo mon = new MonitorInfo();
            mon.Device = screen.DeviceName; mon.Bounds = screen.Bounds; mon.WorkArea = screen.WorkingArea; mon.Primary = true;
            string asked = null;
            ToastHost host = new ToastHost();
            try
            {
                host.Apply(s);
                EventInfo ev = typeof(ToastHost).GetEvent("GalleryRequested", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ev != null) ev.AddEventHandler(host, (Action<string>)delegate(string p) { asked = p; });
                host.Show(ToastInfo.Saved(new Bitmap(64, 36, PixelFormat.Format32bppPArgb), saved, true), mon);
                IList open = (IList)Field(host, "_open");
                T.Eq("a saved-shot toast shows with settings written by an early build", 1, open.Count);
                if (open.Count != 1) return;
                ToastWindow w = (ToastWindow)open[0];

                Rectangle gallery = Rectangle.Empty;
                List<Rectangle> others = new List<Rectangle>();
                foreach (object kv in (IList)Field(w, "_buttons"))
                {
                    string key = kv.GetType().GetProperty("Key").GetValue(kv, null).ToString();
                    Rectangle r = (Rectangle)kv.GetType().GetProperty("Value").GetValue(kv, null);
                    if (key == "Gallery") gallery = r; else others.Add(r);
                }
                bool apart = true;
                foreach (Rectangle r in others) apart &= !r.IntersectsWith(gallery);
                T.Check("the toast has a Gallery button inside the window, clear of the other buttons",
                        !gallery.IsEmpty && w.ClientRectangle.Contains(gallery) && apart, gallery + " in " + w.ClientSize);
                Font title = (Font)Field(w, "_titleFont");
                int textLeft = (int)w.GetType().GetProperty("TextLeft", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(w, null);
                int textRight = (int)w.GetType().GetMethod("S", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(w, new object[] { 46f });
                int need = TextRenderer.MeasureText(Tr.S("Снимок сохранён и скопирован", "Screenshot saved and copied"), title, Size.Empty,
                                                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
                T.Check("the toast title fits without an ellipsis", need <= w.Width - textRight - textLeft, need + " > " + (w.Width - textRight - textLeft));
                if (gallery.IsEmpty) return;

                IntPtr at = new IntPtr(((gallery.Y + gallery.Height / 2) << 16) | ((gallery.X + gallery.Width / 2) & 0xFFFF));
                SendMessage(w.Handle, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), at);
                SendMessage(w.Handle, WM_LBUTTONUP, IntPtr.Zero, at);
                Application.DoEvents();
                T.Eq("a click on Gallery asks the agent to open the gallery for this file", saved, asked);
                T.Check("the toast closes after Gallery", w.IsClosingToast);
            }
            finally { host.Dispose(); }
        }

        private static object Field(object o, string name)
        {
            return o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);
        }

        // Кнопка панели оверлея по имени: «Tool:Rect», «Color:2» (номер цвета на панели), «Action:Save», «Delete».
        private static object PanelButtonOf(RegionOverlay overlay, string key)
        {
            int colors = 0;
            foreach (object b in (IList)Field(overlay, "_buttons"))
            {
                string name = Field(b, "Kind").ToString();
                if (name == "Tool") name += ":" + Field(b, "Tool");
                else if (name == "Action") name += ":" + Field(b, "Action");
                else if (name == "Color") name += ":" + colors++;
                if (name == key) return b;
            }
            return null;
        }

        private static void OverlayClick(RegionOverlay overlay, string key)
        {
            object b = PanelButtonOf(overlay, key);
            Rectangle r = b == null ? Rectangle.Empty : (Rectangle)Field(b, "Bounds");
            Point c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            if (r.IsEmpty) T.Check("the overlay panel shows " + key, false);
            else OverlayDrag(overlay, c, c);
        }

        private static void OverlayDrag(Control target, Point from, Point to)
        {
            IntPtr h = target.Handle;
            for (int i = 0; i <= 6; i++)
            {
                IntPtr at = new IntPtr(((from.Y + (to.Y - from.Y) * i / 6) << 16) | ((from.X + (to.X - from.X) * i / 6) & 0xFFFF));
                if (i == 0) SendMessage(h, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), at);
                SendMessage(h, WM_MOUSEMOVE, new IntPtr(MK_LBUTTON), at);
                if (i == 6) SendMessage(h, WM_LBUTTONUP, IntPtr.Zero, at);
            }
        }

        private static RectangleF BoxRect(EditorDoc doc, int index)
        {
            BoxShape b = doc.Shapes.Count > index ? doc.Shapes[index] as BoxShape : null;
            return b != null ? b.Rect : RectangleF.Empty;
        }

        private static bool Near(RectangleF r, float x, float y, float w, float h)
        {
            return Math.Abs(r.X - x) < 1 && Math.Abs(r.Y - y) < 1 && Math.Abs(r.Width - w) < 1 && Math.Abs(r.Height - h) < 1;
        }

        private static string Describe(EditorDoc doc)
        {
            StringBuilder sb = new StringBuilder(doc.Size + ":");
            foreach (CapShape s in doc.Shapes) sb.Append(' ').Append(s.Bounds).Append(' ').Append(s.Color.Name);
            return sb.ToString();
        }

        private static RectangleF RedactRect(EditorForm form, int index)
        {
            RedactShape r = form.Doc.Shapes.Count > index ? form.Doc.Shapes[index] as RedactShape : null;
            return r != null ? r.Rect : RectangleF.Empty;
        }

        // Точка снимка переводится в точку холста тем же масштабом и сдвигом, которыми холст рисует.
        private static void MouseDrag(EditorCanvas canvas, PointF from, PointF to)
        {
            PointF pan = (PointF)Field(canvas, "_pan");
            IntPtr h = canvas.Handle;
            for (int i = 0; i <= 6; i++)
            {
                float x = (from.X + (to.X - from.X) * i / 6) * canvas.Zoom + pan.X, y = (from.Y + (to.Y - from.Y) * i / 6) * canvas.Zoom + pan.Y;
                IntPtr at = new IntPtr(((int)Math.Round(y) << 16) | ((int)Math.Round(x) & 0xFFFF));
                if (i == 0) SendMessage(h, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), at);
                SendMessage(h, WM_MOUSEMOVE, new IntPtr(MK_LBUTTON), at);
                if (i == 6) SendMessage(h, WM_LBUTTONUP, IntPtr.Zero, at);
            }
        }
    }
}
