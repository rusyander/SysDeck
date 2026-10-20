// SysDeck — «ТВ-трансляция»: ffmpeg — длительность и кодек, кадры-превью, перекодирование на лету.
// Общее ядро с отдельным приложением PcStream (см. Tv.Model.cs).
// Без ffmpeg трансляция работает, но только для MP4/WebM/MOV и без превью: окно говорит об этом прямо.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SysDeck.TvStream
{
    internal sealed class TvProbe
    {
        public string VideoCodec;   // null — неизвестно (нет ffprobe или файл не читается)
        public double Duration;     // секунды, 0 — неизвестно
    }

    internal sealed class TvFfmpeg
    {
        public readonly string FfmpegPath, FfprobePath;
        private readonly Dictionary<string, TvProbe> _probes = new Dictionary<string, TvProbe>(StringComparer.OrdinalIgnoreCase);
        // Не больше двух ffmpeg на превью одновременно: папка на 200 файлов не должна запустить 200 процессов.
        private readonly Semaphore _thumbSlots = new Semaphore(2, 2);

        public TvFfmpeg(string ffmpeg, string ffprobe) { FfmpegPath = ffmpeg; FfprobePath = ffprobe; }

        public bool Available { get { return FfmpegPath != null; } }

        // Своя копия, скачанная кнопкой «Скачать ffmpeg» (папка данных приложения); задаёт TvHost.Init.
        public static string OwnDir;

        // Где искать: своя копия, PATH, ссылки winget (Gyan.FFmpeg ставит их в WinGet\Links), пакеты winget, папка ffmpeg рядом с exe.
        public static TvFfmpeg Locate()
        {
            List<string> dirs = new List<string>();
            if (!string.IsNullOrEmpty(OwnDir)) dirs.Add(OwnDir);
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string d in path.Split(';')) if (d.Trim().Length > 0) dirs.Add(d.Trim().Trim('"'));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dirs.Add(Path.Combine(local, @"Microsoft\WinGet\Links"));
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            dirs.Add(Path.Combine(exeDir, @"ffmpeg\bin"));
            dirs.Add(Path.Combine(exeDir, "ffmpeg"));
            try
            {
                string pkgs = Path.Combine(local, @"Microsoft\WinGet\Packages");
                if (Directory.Exists(pkgs))
                    foreach (string p in Directory.GetDirectories(pkgs, "*FFmpeg*"))
                        foreach (string sub in Directory.GetDirectories(p))
                            dirs.Add(Path.Combine(sub, "bin"));
            }
            catch (Exception) { }

            foreach (string d in dirs)
            {
                try
                {
                    string ff = Path.Combine(d, "ffmpeg.exe");
                    if (!File.Exists(ff)) continue;
                    string fp = Path.Combine(d, "ffprobe.exe");
                    return new TvFfmpeg(ff, File.Exists(fp) ? fp : null);
                }
                catch (ArgumentException) { }   // мусор в PATH (недопустимые символы)
            }
            return new TvFfmpeg(null, null);
        }

        // Вывод `ffprobe -show_entries stream=codec_name:format=duration -of default=nw=1`: строки «ключ=значение».
        public static TvProbe ParseProbe(string output)
        {
            TvProbe p = new TvProbe();
            foreach (string raw in (output ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("codec_name=") && p.VideoCodec == null) p.VideoCodec = line.Substring(11).Trim();
                else if (line.StartsWith("duration="))
                {
                    double d;
                    if (double.TryParse(line.Substring(9), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0) p.Duration = d;
                }
            }
            if (p.VideoCodec == "" || p.VideoCodec == "N/A") p.VideoCodec = null;
            return p;
        }

        public TvProbe Probe(string file)
        {
            string key;
            try { key = file + "|" + File.GetLastWriteTimeUtc(file).Ticks; }
            catch (Exception) { return new TvProbe(); }
            lock (_probes)
            {
                TvProbe cached;
                if (_probes.TryGetValue(key, out cached)) return cached;
            }
            TvProbe p = FfprobePath == null ? new TvProbe()
                : ParseProbe(Run(FfprobePath, "-v error -select_streams v:0 -show_entries stream=codec_name:format=duration -of default=nw=1 " + Quote(file), 15000));
            lock (_probes) _probes[key] = p;
            return p;
        }

        // Аргументы перекодирования в фрагментированный MP4 (играет без перемотки назад по файлу, сразу по мере записи).
        // H.264 копируется как есть — это почти бесплатно; остальное (HEVC 10 бит, MPEG-4, VC-1) пережимается быстро.
        // Звук всегда AAC стерео: AC3/DTS/FLAC телевизоры в браузере не играют. Перемотка = новый запуск с -ss.
        public static string TranscodeArgs(string file, double startSec, string videoCodec)
        {
            StringBuilder sb = new StringBuilder("-hide_banner -loglevel error -nostdin ");
            if (startSec > 0) sb.Append("-ss ").Append(((long)startSec).ToString(CultureInfo.InvariantCulture)).Append(' ');
            sb.Append("-i ").Append(Quote(file)).Append(" -map 0:v:0 -map 0:a:0? ");
            if (videoCodec == "h264") sb.Append("-c:v copy ");
            else sb.Append("-c:v libx264 -preset veryfast -crf 21 -pix_fmt yuv420p ");
            sb.Append("-c:a aac -b:a 192k -ac 2 -movflags frag_keyframe+empty_moov+default_base_moof -f mp4 pipe:1");
            return sb.ToString();
        }

        public Process StartTranscode(string file, double startSec)
        {
            if (FfmpegPath == null) return null;
            ProcessStartInfo psi = new ProcessStartInfo(FfmpegPath, TranscodeArgs(file, startSec, Probe(file).VideoCodec));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            return Process.Start(psi);
        }

        // Кадр на 10 % длительности, 480 px по ширине, в кэш; повторный запрос отдаёт готовый файл.
        public string Thumbnail(string file, string cacheDir)
        {
            if (FfmpegPath == null) return null;
            string jpg;
            try
            {
                string key = file.ToLowerInvariant() + "|" + File.GetLastWriteTimeUtc(file).Ticks;
                using (SHA1 sha = SHA1.Create())
                    jpg = Path.Combine(cacheDir, BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "") + ".jpg");
                if (File.Exists(jpg)) return jpg;
                Directory.CreateDirectory(cacheDir);
            }
            catch (Exception) { return null; }

            if (!_thumbSlots.WaitOne(30000)) return null;
            try
            {
                if (File.Exists(jpg)) return jpg;   // пока ждали слот, его мог сделать соседний запрос
                double at = Math.Max(1, Math.Floor(Probe(file).Duration * 0.1));
                string tmp = jpg + ".tmp.jpg";
                Run(FfmpegPath, "-hide_banner -loglevel error -nostdin -y -ss " + ((long)at).ToString(CultureInfo.InvariantCulture)
                    + " -i " + Quote(file) + " -frames:v 1 -vf scale=480:-2 " + Quote(tmp), 30000);
                if (!File.Exists(tmp)) return null;
                try { File.Move(tmp, jpg); } catch (IOException) { File.Delete(tmp); }
                return File.Exists(jpg) ? jpg : null;
            }
            finally { _thumbSlots.Release(); }
        }

        public static string Quote(string s) { return "\"" + s + "\""; }   // в именах файлов Windows кавычек не бывает

        private static string Run(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    string output = null;
                    Thread reader = new Thread(delegate() { try { output = p.StandardOutput.ReadToEnd(); } catch (Exception) { } });
                    reader.IsBackground = true;
                    reader.Start();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } }
                    reader.Join(2000);
                    return output ?? "";
                }
            }
            catch (Exception) { return ""; }
        }
    }
}
