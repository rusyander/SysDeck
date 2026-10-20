// SysDeck — «ТВ-трансляция»: какие файлы видит телевизор, ответы веб-интерфейсу и Media Station X.
// Общее ядро с отдельным приложением PcStream (см. Tv.Model.cs).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SysDeck.TvStream
{
    internal static class TvLibrary
    {
        private static readonly string[] VideoExt = { ".mp4", ".m4v", ".webm", ".mov", ".mkv", ".avi", ".ts", ".m2ts", ".mts",
                                                      ".wmv", ".flv", ".mpg", ".mpeg", ".3gp", ".vob" };
        // Эти телевизор и браузер играют сами, с перемоткой по Range; остальное — через ffmpeg на лету.
        private static readonly string[] DirectExt = { ".mp4", ".m4v", ".webm", ".mov" };

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);

        public static bool IsVideo(string name) { return Array.IndexOf(VideoExt, Ext(name)) >= 0; }
        public static bool IsDirect(string name) { return Array.IndexOf(DirectExt, Ext(name)) >= 0; }

        private static string Ext(string name)
        {
            try { return (Path.GetExtension(name ?? "") ?? "").ToLowerInvariant(); }
            catch (ArgumentException) { return ""; }
        }

        // Путь внутри раздела → полный путь на диске; null, если выходит за корень раздела или раздела нет.
        // rel приходит из адреса запроса, то есть от любого устройства в сети: «..», «C:\», «\\server» отсекаются здесь.
        public static string Resolve(IList<TvFolder> roots, int root, string rel)
        {
            if (roots == null || root < 0 || root >= roots.Count) return null;
            string basePath = roots[root].Path;
            if (string.IsNullOrEmpty(basePath)) return null;
            rel = (rel ?? "").Replace('/', '\\').Trim('\\');
            if (rel.IndexOf(':') >= 0) return null;               // диск или альтернативный поток NTFS
            try
            {
                string full = Path.GetFullPath(Path.Combine(basePath, rel)).TrimEnd('\\');
                string b = Path.GetFullPath(basePath).TrimEnd('\\');
                if (string.Equals(full, b, StringComparison.OrdinalIgnoreCase)) return basePath;
                if (full.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)) return full;
            }
            catch (Exception) { }   // недопустимые символы, слишком длинный путь
            return null;
        }

        // Содержимое папки: подпапки и видео, скрытые и системные пропускаются, порядок «как в Проводнике» (2 < 10).
        public static void List(string dir, List<string> dirs, List<string> files)
        {
            DirectoryInfo di = new DirectoryInfo(dir);
            foreach (FileSystemInfo fi in di.EnumerateFileSystemInfos())
            {
                string n = fi.Name;
                if (n.StartsWith(".")) continue;
                if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if ((fi.Attributes & FileAttributes.Directory) != 0) dirs.Add(n);
                else if (IsVideo(n)) files.Add(n);
            }
            dirs.Sort(Compare);
            files.Sort(Compare);
        }

        private static int Compare(string a, string b)
        {
            try { return StrCmpLogicalW(a, b); }
            catch (Exception) { return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase); }
        }

        // Сколько видео в разделе (с подпапками). Счёт останавливается на cap: это подпись в окне, не инвентаризация.
        public static int CountVideos(string dir, int cap)
        {
            int n = 0;
            Stack<string> stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0 && n < cap)
            {
                string d = stack.Pop();
                try
                {
                    foreach (FileSystemInfo fi in new DirectoryInfo(d).EnumerateFileSystemInfos())
                    {
                        if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if ((fi.Attributes & FileAttributes.Directory) != 0)
                        {
                            if ((fi.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(fi.FullName);
                        }
                        else if (IsVideo(fi.Name) && ++n >= cap) break;
                    }
                }
                catch (Exception) { }   // нет доступа к подпапке — считаем остальное
            }
            return n;
        }

        public static string JoinRel(string rel, string name) { return string.IsNullOrEmpty(rel) ? name : rel + "/" + name; }

        public static string ParentRel(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "";
            int i = rel.LastIndexOf('/');
            return i < 0 ? "" : rel.Substring(0, i);
        }

        // Путь в адресе: каждый сегмент экранируется отдельно, разделитель остаётся «/».
        public static string EncodeRel(string rel)
        {
            string[] parts = (rel ?? "").Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }

        public static string PlayUrl(string baseUrl, int root, string rel)
        {
            return baseUrl + (IsDirect(rel) ? "/media/" : "/tx/") + root + "/" + EncodeRel(rel);
        }

        public static string ThumbUrl(string baseUrl, int root, string rel) { return baseUrl + "/thumb/" + root + "/" + EncodeRel(rel); }

        public static string Title(string fileName)
        {
            string t = Path.GetFileNameWithoutExtension(fileName);
            return string.IsNullOrEmpty(t) ? fileName : t;
        }
    }

    // Ответы веб-интерфейсу телевизора (index.html).
    internal static class TvApi
    {
        public static string Info(string ip, int port, IList<TvFolder> roots, bool transcode)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"ip\":").Append(TvJson.Str(ip)).Append(",\"port\":").Append(port)
              .Append(",\"transcode\":").Append(transcode ? "true" : "false").Append(",\"roots\":[");
            for (int i = 0; i < roots.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(i).Append(",\"name\":").Append(TvJson.Str(roots[i].Name)).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        public static string List(int root, string rel, List<string> dirs, List<string> files, Func<string, double> duration)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"dirs\":[");
            for (int i = 0; i < dirs.Count; i++) { if (i > 0) sb.Append(','); sb.Append(TvJson.Str(dirs[i])); }
            sb.Append("],\"files\":[");
            for (int i = 0; i < files.Count; i++)
            {
                if (i > 0) sb.Append(',');
                string r = TvLibrary.JoinRel(rel, files[i]);
                sb.Append("{\"name\":").Append(TvJson.Str(files[i]))
                  .Append(",\"url\":").Append(TvJson.Str(TvLibrary.PlayUrl("", root, r)))
                  .Append(",\"thumb\":").Append(TvJson.Str(TvLibrary.ThumbUrl("", root, r)))
                  .Append(",\"transcode\":").Append(TvLibrary.IsDirect(files[i]) ? "false" : "true")
                  .Append(",\"duration\":").Append(TvJson.Num(duration == null ? 0 : duration(r)))
                  .Append('}');
            }
            return sb.Append("]}").ToString();
        }
    }

    // Media Station X: стартовый параметр, меню разделов, списки. Видео играет плеер MSX (action "video:<url>").
    internal static class TvMsx
    {
        public static string Start(string baseUrl)
        {
            return "{\"name\":\"PC Stream\",\"version\":\"1.0.0\",\"parameter\":" + TvJson.Str("menu:" + baseUrl + "/msx/menu.json") + "}";
        }

        public static string Menu(string baseUrl, IList<TvFolder> roots)
        {
            StringBuilder sb = new StringBuilder("{\"headline\":\"PC Stream\",\"menu\":[");
            for (int i = 0; i < roots.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"icon\":\"folder\",\"label\":").Append(TvJson.Str(roots[i].Name))
                  .Append(",\"data\":").Append(TvJson.Str(ListUrl(baseUrl, i, ""))).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        public static string ListUrl(string baseUrl, int root, string rel)
        {
            return baseUrl + "/msx/list.json?root=" + root + "&path=" + Uri.EscapeDataString(rel ?? "");
        }

        public static string List(string baseUrl, int root, string rel, string headline, List<string> dirs, List<string> files)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"type\":\"list\",\"headline\":").Append(TvJson.Str(headline))
              .Append(",\"template\":{\"type\":\"separate\",\"layout\":\"0,0,3,2\",\"color\":\"msx-glass\",\"imageFiller\":\"cover\"},\"items\":[");
            bool first = true;
            foreach (string d in dirs)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"icon\":\"folder\",\"title\":").Append(TvJson.Str(d))
                  .Append(",\"action\":").Append(TvJson.Str("content:" + ListUrl(baseUrl, root, TvLibrary.JoinRel(rel, d)))).Append('}');
            }
            foreach (string f in files)
            {
                if (!first) sb.Append(',');
                first = false;
                string r = TvLibrary.JoinRel(rel, f);
                sb.Append("{\"title\":").Append(TvJson.Str(TvLibrary.Title(f)))
                  .Append(",\"image\":").Append(TvJson.Str(TvLibrary.ThumbUrl(baseUrl, root, r)))
                  .Append(",\"action\":").Append(TvJson.Str("video:" + TvLibrary.PlayUrl(baseUrl, root, r))).Append('}');
            }
            if (first) sb.Append("{\"title\":").Append(TvJson.Str(SysDeck.Tr.S("Здесь нет видео", "No videos here"))).Append(",\"enable\":false}");
            return sb.Append("]}").ToString();
        }
    }
}
