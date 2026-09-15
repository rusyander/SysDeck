// SysDeck — «Загрузки»: ограничение скорости (ведро токенов) и замер скорости.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Два уровня: общее ведро на весь движок и своё у каждой загрузки. Поток сегмента берёт токены сначала у загрузки,
// затем у общего ведра, и читает из сети ровно столько, сколько получил. Ведро стартует пустым: иначе первые
// полсекунды шли бы без ограничения и замер «сколько за 3 секунды» врал бы на запас ведра.
using System;
using System.Diagnostics;
using System.Threading;

namespace SysDeck.Downloads
{
    internal sealed class DlTokenBucket
    {
        private readonly object _gate = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _rate;          // байт в секунду; 0 — без ограничения
        private double _tokens;
        private long _lastTicks;

        public DlTokenBucket(long bytesPerSecond) { _rate = Math.Max(0, bytesPerSecond); }

        public long Rate
        {
            get { lock (_gate) return _rate; }
            set
            {
                lock (_gate)
                {
                    long v = Math.Max(0, value);
                    if (v == _rate) return;
                    Refill();
                    _rate = v;
                    if (_tokens > Burst) _tokens = Burst;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        // Запас — четверть секунды, но не меньше 16 КБ: иначе при низком лимите чтение шло бы по байту.
        private double Burst { get { return Math.Max(16 * 1024, _rate / 4.0); } }

        private void Refill()
        {
            long now = _clock.ElapsedTicks;
            if (_rate > 0)
            {
                _tokens += (now - _lastTicks) * (double)_rate / Stopwatch.Frequency;
                if (_tokens > Burst) _tokens = Burst;
            }
            _lastTicks = now;
        }

        // Сколько байт можно прочитать: от 1 до want. Ждёт, пока появится хоть что-то; cancel — вернуть 0 сразу.
        public int Take(int want, Func<bool> cancel)
        {
            if (want <= 0) return 0;
            lock (_gate)
            {
                while (true)
                {
                    if (_rate == 0) return want;
                    Refill();
                    if (_tokens >= 1)
                    {
                        int granted = (int)Math.Min(want, Math.Floor(_tokens));
                        _tokens -= granted;
                        return granted;
                    }
                    if (cancel != null && cancel()) return 0;
                    double missing = 1024 - _tokens;
                    int waitMs = (int)Math.Max(1, Math.Min(100, missing * 1000 / _rate));
                    Monitor.Wait(_gate, waitMs);
                }
            }
        }

        // Без ожидания: сколько байт можно прямо сейчас (0 — ничего). Для потока реактора торрентов, который ждать не может.
        public int TryTake(int want)
        {
            if (want <= 0) return 0;
            lock (_gate)
            {
                if (_rate == 0) return want;
                Refill();
                if (_tokens < 1) return 0;
                int granted = (int)Math.Min(want, Math.Floor(_tokens));
                _tokens -= granted;
                return granted;
            }
        }

        // Вернуть неиспользованное (прочитали меньше, чем взяли).
        public void Return(int bytes)
        {
            if (bytes <= 0) return;
            lock (_gate)
            {
                if (_rate == 0) return;
                _tokens = Math.Min(Burst, _tokens + bytes);
                Monitor.PulseAll(_gate);
            }
        }
    }

    // Скорость по окну последних секунд: байты складываются потоками, движок снимает показания раз в полсекунды.
    internal sealed class DlSpeedMeter
    {
        private long _bytes;
        private readonly long[] _samples = new long[8];     // 8 × 0,5 с = окно 4 с
        private readonly long[] _ticks = new long[8];
        private int _at, _count;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public void Add(long bytes) { Interlocked.Add(ref _bytes, bytes); }

        public long Total { get { return Interlocked.Read(ref _bytes); } }

        // Вызывается одним потоком (таймер движка).
        public long Sample()
        {
            _samples[_at] = Total;
            _ticks[_at] = _clock.ElapsedTicks;
            int oldest = _count < _samples.Length ? 0 : (_at + 1) % _samples.Length;
            int newest = _at;
            _at = (_at + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;
            if (_count < 2) return 0;
            double seconds = (_ticks[newest] - _ticks[oldest]) / (double)Stopwatch.Frequency;
            return seconds <= 0 ? 0 : (long)((_samples[newest] - _samples[oldest]) / seconds);
        }

        public void Reset()
        {
            _count = 0;
            _at = 0;
        }
    }
}
