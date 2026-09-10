// Windows Process Cleaner — область «browsers»: обратная запись закладок.
//
// Это самый опасный путь во всём приложении, который НЕ требует прав администратора:
// программа переписывает живой файл профиля браузера. Поэтому проверка идёт на КОПИИ
// настоящего профиля (если он есть на машине) и на собранном вручную файле — но самим
// производственным кодом, тем же BrowserData.SaveBookmarks, каким пишет окно.
//
// Настоящий профиль пользователя не открывается на запись ни в одной проверке.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WindowsProcessCleaner.Tests
{
    internal static class BrowserTests
    {
        internal static void Run()
        {
            RoundTripOnSyntheticProfile();
            RoundTripOnCopyOfRealProfile();
            BackupsFollowTheDataDir();
        }

        // ---------- собранный вручную профиль ----------
        private static void RoundTripOnSyntheticProfile()
        {
            Fx.NewEngine("browsers-data");
            string dir = Fx.MakeDir(Fx.Root, "browsers-synth");
            string path = Path.Combine(dir, "Bookmarks");
            File.WriteAllText(path, SyntheticBookmarks(), new UTF8Encoding(false));

            JVal doc = Jsn.Parse(File.ReadAllText(path));
            T.Check("собранный файл закладок разбирается", doc != null, null);

            // Контрольная сумма в исходном файле заведомо неверна: её считает браузер,
            // а не мы. Проверка обязана это увидеть — иначе она не проверяет ничего.
            T.Check("подложная контрольная сумма распознана как неверная",
                    !BrowserData.ChecksumMatches(doc), null);

            string before = File.ReadAllText(path);
            string backup = BrowserData.SaveBookmarks(Profile(dir, "synthetic"), doc);

            T.Check("копия файла сделана до записи", File.Exists(backup), backup);
            T.Eq("копия совпадает с тем, что было до записи", before, File.ReadAllText(backup));

            JVal after = Jsn.Parse(File.ReadAllText(path));
            T.Check("после записи файл снова разбирается", after != null, null);
            T.Check("контрольная сумма пересчитана и сходится",
                    BrowserData.ChecksumMatches(after), after == null ? null : after.GetStr("checksum"));
            T.Eq("закладка на месте и не переименована", "Пример",
                 FirstBookmarkName(after));
            T.Check("временный файл не остался рядом", !File.Exists(path + ".wpctmp"), null);
        }

        // ---------- копия настоящего профиля ----------
        private static void RoundTripOnCopyOfRealProfile()
        {
            BrowserProfile real = null;
            foreach (BrowserProfile p in BrowserData.FindProfiles())
                if (p.Dir != null && File.Exists(Path.Combine(p.Dir, "Bookmarks"))) { real = p; break; }

            if (real == null)
            {
                T.Skip("обратная запись в настоящий профиль", "на этой машине нет профиля с закладками");
                return;
            }

            string dir = Fx.MakeDir(Fx.Root, "browsers-real");
            string src = Path.Combine(real.Dir, "Bookmarks");
            string dst = Path.Combine(dir, "Bookmarks");
            File.Copy(src, dst, true);
            long srcLen = new FileInfo(src).Length;

            JVal doc = Jsn.Parse(File.ReadAllText(dst));
            T.Check("настоящий файл закладок разбирается", doc != null, real.Display);

            // У живого профиля сумма обязана сходиться ДО того, как мы что-то трогали:
            // если нет, дальше сравнивать не с чем.
            bool okBefore = BrowserData.ChecksumMatches(doc);
            T.Check("у настоящего профиля контрольная сумма сходится до записи", okBefore, real.Display);

            BrowserData.SaveBookmarks(Profile(dir, "copy-of-real"), doc);

            JVal after = Jsn.Parse(File.ReadAllText(dst));
            T.Check("после нашей записи настоящий формат не сломан", after != null, null);
            if (okBefore)
                T.Eq("контрольная сумма после записи та же, что была у браузера",
                     doc.GetStr("checksum"), after == null ? null : after.GetStr("checksum"));
            T.Check("файл не опустел", new FileInfo(dst).Length > srcLen / 2,
                    new FileInfo(dst).Length + " vs " + srcLen);

            // Настоящий профиль остался нетронутым — ради этого вся возня с копией.
            T.Eq("файл настоящего профиля не изменился в размере", srcLen, new FileInfo(src).Length);
        }

        // ---------- копии лежат вместе с остальными данными ----------
        private static void BackupsFollowTheDataDir()
        {
            Engine e = Fx.NewEngine("browsers-backup");
            T.Check("копии закладок лежат в папке данных, а не жёстко в %APPDATA%",
                    Fx.IsUnder(BrowserData.BackupDir, e.DataDir), BrowserData.BackupDir);
        }

        // ---------- вспомогательное ----------
        private static BrowserProfile Profile(string dir, string name)
        {
            BrowserProfile p = new BrowserProfile();
            p.Browser = "wpc-tests";
            p.ProcessName = "wpc-tests";
            p.UserDataDir = dir;
            p.Dir = dir;
            p.ProfileName = name;
            return p;
        }

        private static string FirstBookmarkName(JVal doc)
        {
            if (doc == null) return null;
            JVal roots = doc.Get("roots");
            if (roots == null) return null;
            JVal bar = roots.Get("bookmark_bar");
            if (bar == null) return null;
            JVal kids = bar.Get("children");
            if (kids == null || kids.V == null || kids.V.Count == 0) return null;
            return kids.V[0].GetStr("name");
        }

        // Формат Chrome/Edge: roots + три корня, узлы с id/name/type/url. Контрольная сумма
        // намеренно неверная — её пересчёт и есть то, что проверяется.
        private static string SyntheticBookmarks()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"checksum\":\"00000000000000000000000000000000\",");
            sb.Append("\"roots\":{");
            sb.Append("\"bookmark_bar\":{\"children\":[");
            sb.Append("{\"date_added\":\"13300000000000000\",\"guid\":\"11111111-1111-1111-1111-111111111111\",");
            sb.Append("\"id\":\"5\",\"name\":\"Пример\",\"type\":\"url\",\"url\":\"https://example.org/\"}");
            sb.Append("],\"date_added\":\"13300000000000000\",\"date_modified\":\"13300000000000000\",");
            sb.Append("\"guid\":\"00000000-0000-4000-a000-000000000002\",\"id\":\"1\",");
            sb.Append("\"name\":\"Панель закладок\",\"type\":\"folder\"},");
            sb.Append("\"other\":{\"children\":[],\"date_added\":\"13300000000000000\",");
            sb.Append("\"guid\":\"00000000-0000-4000-a000-000000000003\",\"id\":\"2\",");
            sb.Append("\"name\":\"Другие закладки\",\"type\":\"folder\"},");
            sb.Append("\"synced\":{\"children\":[],\"date_added\":\"13300000000000000\",");
            sb.Append("\"guid\":\"00000000-0000-4000-a000-000000000004\",\"id\":\"3\",");
            sb.Append("\"name\":\"Мобильные закладки\",\"type\":\"folder\"}");
            sb.Append("},\"version\":1}");
            return sb.ToString();
        }
    }
}
