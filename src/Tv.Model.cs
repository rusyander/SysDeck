// SysDeck — «ТВ-трансляция»: настройки, сетевые адреса, запись JSON.
// Общее ядро: те же файлы Tv.*.cs собирают и отдельное приложение PcStream (D:\Work\NIS\tv-stream, sync.bat).
// Поэтому здесь только BCL — ни Engine, ни Json.cs, ни MainForm.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SysDeck.TvStream
{
    // Папка, которую видит телевизор: путь на диске и название раздела в меню.
    [DataContract]
    internal sealed class TvFolder
    {
        [DataMember(Name = "path")] public string Path;
        [DataMember(Name = "name")] public string Name;

        public TvFolder() { }
        public TvFolder(string path, string name) { Path = path; Name = name; }
    }

    [DataContract]
    internal sealed class TvSettings
    {
        public const int DefaultPort = 8090;
        public const int CurrentVersion = 1;

        [DataMember(Name = "folders")] public List<TvFolder> Folders;
        [DataMember(Name = "port")] public int Port;
        [DataMember(Name = "autoStart")] public bool AutoStart;
        [DataMember(Name = "thumbnails")] public bool Thumbnails;
        [DataMember(Name = "version")] public int Version;
        [DataMember(Name = "guideSeen")] public bool GuideSeen;   // пошаговое окно уже открывалось само при первом запуске

        public static TvSettings Defaults()
        {
            TvSettings s = new TvSettings();
            s.Folders = new List<TvFolder>();
            s.Normalize();
            return s;
        }

        // DataContractJsonSerializer оставляет отсутствующие поля по умолчанию (bool = false), поэтому включённые
        // по умолчанию флаги ставит шаг версии, а не конструктор.
        public void Normalize()
        {
            if (Version < 1) { AutoStart = true; Thumbnails = true; }
            Version = CurrentVersion;
            if (Port < 1024 || Port > 65535) Port = DefaultPort;
            List<TvFolder> clean = new List<TvFolder>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Folders != null)
                foreach (TvFolder f in Folders)
                {
                    if (f == null || string.IsNullOrWhiteSpace(f.Path)) continue;
                    string full, raw = f.Path.Trim();
                    // «D:» без слеша — это «текущая папка на D:», а не корень диска: GetFullPath вернул бы чужую папку.
                    if (raw.Length == 2 && raw[1] == ':') raw += "\\";
                    try { full = System.IO.Path.GetFullPath(raw).TrimEnd('\\'); }
                    catch (Exception) { continue; }
                    if (full.Length == 2 && full[1] == ':') full += "\\";   // корень диска остаётся «D:\»
                    if (!seen.Add(full)) continue;
                    string name = string.IsNullOrWhiteSpace(f.Name) ? DefaultName(full) : f.Name.Trim();
                    clean.Add(new TvFolder(full, name));
                }
            Folders = clean;
        }

        public static string DefaultName(string path)
        {
            string n = System.IO.Path.GetFileName(path.TrimEnd('\\'));
            return string.IsNullOrEmpty(n) ? path : n;
        }

        public static TvSettings Load(string file)
        {
            try
            {
                if (File.Exists(file))
                    using (FileStream fs = File.OpenRead(file))
                    {
                        TvSettings s = (TvSettings)new DataContractJsonSerializer(typeof(TvSettings)).ReadObject(fs);
                        if (s != null) { s.Normalize(); return s; }
                    }
            }
            catch (Exception)
            {
                // Битый файл не роняет окно: откладываем его рядом и начинаем с умолчаний.
                try { File.Copy(file, file + ".corrupt", true); } catch (Exception) { }
            }
            return Defaults();
        }

        // Запись атомарная: временный файл рядом, затем замена — обрыв посреди записи не оставит полфайла.
        public void Save(string file)
        {
            Normalize();
            string dir = System.IO.Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = file + ".tmp";
            using (FileStream fs = File.Create(tmp))
                new DataContractJsonSerializer(typeof(TvSettings)).WriteObject(fs, this);
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }

        public TvSettings Clone()
        {
            TvSettings s = (TvSettings)MemberwiseClone();
            s.Folders = new List<TvFolder>();
            if (Folders != null) foreach (TvFolder f in Folders) s.Folders.Add(new TvFolder(f.Path, f.Name));
            return s;
        }
    }

    internal static class TvNet
    {
        // Сервер отвечает только домашней сети: частные диапазоны, локальные адреса, CGNAT (Tailscale и т. п.).
        // Проброс порта на роутере не должен открыть папки всему интернету.
        public static bool IsLocal(IPAddress a)
        {
            if (a == null) return false;
            if (IPAddress.IsLoopback(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (a.IsIPv4MappedToIPv6) return IsLocal(a.MapToIPv4());
                if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal) return true;
                byte[] v6 = a.GetAddressBytes();
                return (v6[0] & 0xFE) == 0xFC;                         // fc00::/7 — уникальные локальные
            }
            byte[] b = a.GetAddressBytes();
            if (b.Length != 4) return false;
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            return false;
        }

        // Адрес, который вводят в телевизоре. Предпочтение — интерфейсу со шлюзом (настоящая домашняя сеть):
        // у виртуальных адаптеров WSL / Hyper-V шлюза нет, а адрес тоже частный.
        public static string LanAddress()
        {
            string fallback = null;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties p = ni.GetIPProperties();
                    bool gateway = false;
                    foreach (GatewayIPAddressInformation g in p.GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) gateway = true;
                    foreach (UnicastIPAddressInformation u in p.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || !IsLocal(u.Address)) continue;
                        if (IPAddress.IsLoopback(u.Address)) continue;
                        if (gateway) return u.Address.ToString();
                        if (fallback == null) fallback = u.Address.ToString();
                    }
                }
            }
            catch (NetworkInformationException) { }
            return fallback ?? "127.0.0.1";
        }

        // Короткое имя устройства по User-Agent: в списке «Сейчас смотрят» понятнее, чем голый IP.
        public static string DeviceName(string userAgent)
        {
            string ua = userAgent ?? "";
            if (ua.IndexOf("VIDAA", StringComparison.OrdinalIgnoreCase) >= 0 || ua.IndexOf("Hisense", StringComparison.OrdinalIgnoreCase) >= 0) return "Hisense (VIDAA)";
            if (ua.IndexOf("Tizen", StringComparison.OrdinalIgnoreCase) >= 0) return "Samsung (Tizen)";
            if (ua.IndexOf("Web0S", StringComparison.OrdinalIgnoreCase) >= 0 || ua.IndexOf("webOS", StringComparison.OrdinalIgnoreCase) >= 0) return "LG (webOS)";
            if (ua.IndexOf("Android", StringComparison.OrdinalIgnoreCase) >= 0) return "Android";
            if (ua.IndexOf("iPhone", StringComparison.OrdinalIgnoreCase) >= 0 || ua.IndexOf("iPad", StringComparison.OrdinalIgnoreCase) >= 0) return "iOS";
            if (ua.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0) return "Windows";
            if (ua.IndexOf("Mac OS", StringComparison.OrdinalIgnoreCase) >= 0) return "macOS";
            if (ua.IndexOf("Linux", StringComparison.OrdinalIgnoreCase) >= 0) return "Linux";
            return ua.Length == 0 ? "?" : (ua.Length > 24 ? ua.Substring(0, 24) + "…" : ua);
        }
    }

    // Запись JSON вручную: ответы маленькие и плоские, а сериализатор контрактов не умеет поля по условию.
    internal static class TvJson
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': sb.Append("\\u003c"); break;   // имя файла не закроет <script>, если JSON вставят в HTML
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
