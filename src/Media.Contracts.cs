// SysDeck — «Загрузки», видео: общие типы и точки сборки этапа 6 (HLS, DASH, yt-dlp, склейка).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Контракт между тремя частями, которые друг на друга напрямую не ссылаются:
//   ядро (Media.Ts/Mux/Mkv/Subs.cs)         — разбор MPEG-TS, запись MP4 через Media Foundation, склейка WebM, субтитры;
//   движок (Downloads.Hls/Dash/Media.cs)    — плейлисты, сегменты, трансляции, пауза и продолжение после перезапуска;
//   yt-dlp (Downloads.Ytdlp.cs)             — установка yt-dlp и Deno, извлечение форматов со страниц сайтов.
// Движок зовёт склейку и повторное извлечение только через MdHooks; заполняет их Downloads.MediaWiring.cs.
// DRM (Widevine, PlayReady, FairPlay, SAMPLE-AES) не скачивается никогда: разборщик ставит причину в MdManifest.Refused.
using System;
using System.Collections.Generic;
using System.Globalization;

// Поля типов-посредников заполняют разные части; в сборке без какой-то части компилятор счёл бы их «никогда не присваиваемыми».
#pragma warning disable 649

namespace SysDeck.Downloads
{
    internal enum MdTrackKind { Muxed, Video, Audio, Subtitles }

    // Как лежат байты дорожки на диске после загрузки (сегменты уже склеены подряд, расшифрованы).
    internal enum MdLayout { Unknown, Ts, Fmp4, Mp4, WebM, Adts, Mp3, Vtt, Ttml }

    // Во что собрать. Auto: MP4, если все дорожки в него ложатся без перекодирования, иначе WebM.
    internal enum MdOutput { Auto, Mp4, WebM, M4a, Mp3, Ts }

    internal enum MdSource { Direct, Hls, Dash, Ytdlp }

    // ------------------------------------------------------------------ //
    //  Склейка (ядро): вход — файлы дорожек, выход — один файл
    // ------------------------------------------------------------------ //
    internal sealed class MdInput
    {
        public string Path = "";
        public MdTrackKind Kind = MdTrackKind.Muxed;
        public MdLayout Layout = MdLayout.Unknown;   // Unknown — ядро определяет по сигнатуре
        public string Codec = "";                    // RFC 6381 из плейлиста («avc1.64001f», «mp4a.40.2»), пусто — неизвестен
        public string Language = "";
        public string Name = "";
    }

    internal sealed class MdMuxJob
    {
        public readonly List<MdInput> Inputs = new List<MdInput>();
        public string OutPath = "";                  // полный путь результата; ядро пишет во временный рядом и переименовывает
        public MdOutput Output = MdOutput.Auto;
        public bool AllowTruncated;                  // оборванная трансляция или сбой: взять целые кадры, хвост отбросить
        public Func<bool> Cancel;                    // может быть null
        public Action<double> Progress;              // 0..1, может быть null; вызывается не чаще раза в 200 мс
    }

    internal sealed class MdMuxResult
    {
        public bool Ok;
        public string Error = "";                    // Tr.S, понятная пользователю причина
        public string OutPath = "";                  // фактический путь (расширение по итоговому контейнеру)
        public MdOutput Output = MdOutput.Auto;
        public long DurationMs;
        public int VideoFrames;
        public int AudioFrames;
        public readonly List<string> SubtitleFiles = new List<string>();   // .vtt/.srt, записанные рядом с OutPath
    }

    // ------------------------------------------------------------------ //
    //  Описание потока (движок и yt-dlp заполняют, окно добавления показывает)
    // ------------------------------------------------------------------ //
    internal sealed class MdKey
    {
        public string Method = "NONE";               // NONE | AES-128 (SAMPLE-AES и прочее — отказ)
        public string Uri = "";
        public byte[] Iv;                            // null — IV из номера сегмента (RFC 8216 §5.2)
    }

    internal sealed class MdSegment
    {
        public string Url = "";
        public long Offset = -1;                     // BYTERANGE / mediaRange; -1 — весь ресурс
        public long Length = -1;
        public double Duration;
        public long Sequence;                        // HLS media sequence / DASH $Number$; для трансляций — ключ без повторов
        public bool Discontinuity;
        public MdKey Key;                            // null — без шифрования
        public string InitUrl = "";                  // EXT-X-MAP / Initialization; пусто — нет
        public long InitOffset = -1;
        public long InitLength = -1;
    }

    internal sealed class MdTrack
    {
        public string Id = "";                       // стабилен между повторными разборами одного адреса
        public MdTrackKind Kind = MdTrackKind.Muxed;
        public MdLayout Layout = MdLayout.Unknown;
        public string Codec = "";
        public string Language = "";
        public string Name = "";
        public string GroupId = "";
        public bool Default;
        public long Bandwidth;
        public int Width, Height;
        public double Fps;
        public string Url = "";                      // HLS media playlist / DASH MPD / прямая ссылка yt-dlp
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public long ChunkBytes;                      // yt-dlp http_chunk_size: запрос не длиннее этого; 0 — без ограничения
        public string FormatId = "";                 // yt-dlp format_id
        public long SizeHint = -1;
        public readonly List<MdSegment> Segments = new List<MdSegment>();   // пусто, пока плейлист дорожки не разобран
        public bool Live;
        public double TargetDuration;
    }

    internal sealed class MdVariant
    {
        public string Id = "";
        public string Label = "";                    // «1080p · 30 к/с · 4,2 Мбит/с»
        public long Bandwidth;
        public int Width, Height;
        public double Fps;
        public string Codecs = "";
        public MdTrack Main;                         // видео или видео со звуком
        public string AudioGroup = "";               // пусто — звук внутри Main
        public string SubtitleGroup = "";
    }

    internal sealed class MdManifest
    {
        public MdSource Source = MdSource.Direct;
        public string Url = "";
        public string PageUrl = "";
        public string Title = "";
        public bool Live;
        public double DurationSec;                   // 0 — неизвестна (трансляция)
        public string Refused = "";                  // не пусто — скачать нельзя (DRM и т. п.), текст для пользователя
        public DateTime ExpiresUtc = DateTime.MinValue;   // ссылки yt-dlp устаревают: после этого — повторное извлечение
        public readonly List<MdVariant> Variants = new List<MdVariant>();
        public readonly List<MdTrack> Audio = new List<MdTrack>();
        public readonly List<MdTrack> Subtitles = new List<MdTrack>();
    }

    // ------------------------------------------------------------------ //
    //  Состояние видео-загрузки в записи DlItem (Kind = "media")
    // ------------------------------------------------------------------ //
    // В JSON записи — только выбор и счётчики. Список сегментов и что скачано — в журнале папки частей (формат движка).
    // Cookie и Authorization в Headers не сохраняются никогда: они живут в DlItem.Cookies, только в памяти.
    internal sealed class DlMedia
    {
        public MdSource Source = MdSource.Direct;
        public string ManifestUrl = "";
        public string Title = "";
        public string VariantId = "";
        public string AudioId = "";
        public readonly List<string> SubtitleIds = new List<string>();
        public MdOutput Output = MdOutput.Auto;
        public bool Live;
        public bool StopLive;                        // пользователь нажал «Остановить запись»: докачать начатое и собрать
        public bool Watch;                           // вести растущий файл для просмотра во время загрузки
        public string FormatIds = "";                // yt-dlp: «137+140»
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public DateTime ExtractedUtc = DateTime.MinValue;
        public string PartsDir = "";                 // <цель>.wpcmedia
        public string PreviewPath = "";              // растущий файл для просмотра; пусто — нет
        public string Phase = "";                    // segments | mux | verify — для карточки
        public int SegmentsTotal = -1;               // -1 — неизвестно (трансляция)
        public int SegmentsDone;
        public double SecondsDone;

        public static bool SensitiveHeader(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            return n == "cookie" || n == "authorization" || n == "proxy-authorization" || n.StartsWith("x-auth", StringComparison.Ordinal);
        }

        public JVal ToJson()
        {
            JVal o = JVal.NewObj();
            o.Set("Source", DlJson.S(Source.ToString()));
            o.Set("ManifestUrl", DlJson.S(ManifestUrl));
            o.Set("Title", DlJson.S(Title));
            o.Set("VariantId", DlJson.S(VariantId));
            o.Set("AudioId", DlJson.S(AudioId));
            o.Set("SubtitleIds", DlJson.Strings(SubtitleIds));
            o.Set("Output", DlJson.S(Output.ToString()));
            o.Set("Live", DlJson.B(Live));
            o.Set("StopLive", DlJson.B(StopLive));
            o.Set("Watch", DlJson.B(Watch));
            o.Set("FormatIds", DlJson.S(FormatIds));
            JVal h = JVal.NewObj();
            foreach (KeyValuePair<string, string> kv in Headers)
                if (!SensitiveHeader(kv.Key)) h.Set(kv.Key, DlJson.S(kv.Value));
            o.Set("Headers", h);
            o.Set("Extracted", DlJson.D(ExtractedUtc));
            o.Set("PartsDir", DlJson.S(PartsDir));
            o.Set("PreviewPath", DlJson.S(PreviewPath));
            o.Set("Phase", DlJson.S(Phase));
            o.Set("SegmentsTotal", DlJson.N(SegmentsTotal));
            o.Set("SegmentsDone", DlJson.N(SegmentsDone));
            o.Set("SecondsDone", JVal.NewNum(SecondsDone.ToString("0.###", CultureInfo.InvariantCulture)));
            return o;
        }

        public static DlMedia FromJson(JVal o)
        {
            DlMedia m = new DlMedia();
            if (o == null || o.Kind != JKind.Obj) return m;
            m.Source = DlJson.EnumOr(o, "Source", MdSource.Direct);
            m.ManifestUrl = DlJson.Str(o, "ManifestUrl", "");
            m.Title = DlJson.Str(o, "Title", "");
            m.VariantId = DlJson.Str(o, "VariantId", "");
            m.AudioId = DlJson.Str(o, "AudioId", "");
            JVal subs = o.Get("SubtitleIds");
            if (subs != null && subs.Kind == JKind.Arr)
                foreach (JVal s in subs.V) if (s.Kind == JKind.Str && s.Raw.Length > 0) m.SubtitleIds.Add(s.Raw);
            m.Output = DlJson.EnumOr(o, "Output", MdOutput.Auto);
            m.Live = DlJson.Bool(o, "Live", false);
            m.StopLive = DlJson.Bool(o, "StopLive", false);
            m.Watch = DlJson.Bool(o, "Watch", false);
            m.FormatIds = DlJson.Str(o, "FormatIds", "");
            JVal h = o.Get("Headers");
            if (h != null && h.Kind == JKind.Obj)
                for (int i = 0; i < h.K.Count; i++)
                    if (h.V[i].Kind == JKind.Str && !SensitiveHeader(h.K[i])) m.Headers[h.K[i]] = h.V[i].Raw;
            m.ExtractedUtc = DlJson.Date(o, "Extracted");
            m.PartsDir = DlJson.Str(o, "PartsDir", "");
            m.PreviewPath = DlJson.Str(o, "PreviewPath", "");
            m.Phase = DlJson.Str(o, "Phase", "");
            m.SegmentsTotal = DlJson.Int(o, "SegmentsTotal", -1);
            m.SegmentsDone = Math.Max(0, DlJson.Int(o, "SegmentsDone", 0));
            double sec;
            JVal sd = o.Get("SecondsDone");
            m.SecondsDone = sd != null && double.TryParse(sd.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out sec) && sec >= 0 ? sec : 0;
            return m;
        }
    }

    // ------------------------------------------------------------------ //
    //  Запуск загрузки как единица очереди (HTTP-файл или видео)
    // ------------------------------------------------------------------ //
    internal interface IDlRun
    {
        DlItem Item { get; }
        DlTokenBucket Bucket { get; }
        DlSpeedMeter Meter { get; }
        bool Finished { get; }
        bool StopRequested { get; }
        DlFailure Failure { get; }
        void Start();
        void RequestStop();
        bool Join(int timeoutMs);
    }

    // ------------------------------------------------------------------ //
    //  Точки сборки. Заполняет Downloads.MediaWiring.cs; тесты части ставят свои и восстанавливают в finally.
    // ------------------------------------------------------------------ //
    internal static class MdHooks
    {
        // Движок очереди: создать запуск для записи Kind = "media". null — видео-загрузки недоступны.
        public static Func<DlItem, DlSettings, DlTokenBucket, IDlEnvironment, IDlTransferHost, IDlRun> CreateRun;

        // Ядро: собрать дорожки в один файл. Вызывается из потока загрузки, может идти минуты.
        public static Func<MdMuxJob, MdMuxResult> Mux;

        // yt-dlp: извлечь форматы заново (ссылки устарели, 403). Аргументы: адрес страницы, отмена; ошибка — в out.
        public delegate MdManifest ExtractFn(string pageUrl, Func<bool> cancel, out string error);
        public static ExtractFn Extract;

        // Удалить папку частей записи (удаление, «заново»). Возвращает причину отказа или null.
        public static Func<DlItem, string> DeleteParts;
    }
}
