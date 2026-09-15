// SysDeck — «Загрузки», торренты: кому отдавать (choke/unchoke).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Решение — чистая функция над снимком пиров: её легко проверить на построенном состоянии, а применяет её рой.
// Качаем: слоты получают те, кто быстрее всех отдаёт нам (tit-for-tat), тормозящие (snubbed) слотов не получают.
// Раздаём: круговая очередь — открытый слот держится RoundRobinMs, дальше уступает тем, кто ждёт дольше всех; среди
// равных — кому отдаём быстрее. Плюс один «оптимистичный» слот, который раз в 30 секунд переходит к давно ждущему пиру:
// так новый пир без кусков получает шанс начать обмен.
using System;
using System.Collections.Generic;

namespace SysDeck.Downloads
{
    internal sealed class BtChokeInfo
    {
        public object Key;
        public bool Interested;              // пир хочет от нас данные
        public bool IsSeed;                  // у пира всё есть — ему нечего отдавать
        public bool Snubbed;
        public bool Unchoked;                // сейчас открыт
        public bool Optimistic;              // сейчас открыт оптимистично
        public long DownBps;                 // от пира к нам
        public long UpBps;                   // от нас к пиру
        public long UnchokedAtMs;            // когда открыт в последний раз; 0 — никогда

        // Результат
        public bool Unchoke;
        public bool OptimisticNext;
    }

    internal static class BtChoker
    {
        public const int RechokeMs = 10000;
        public const int OptimisticMs = 30000;
        public const int RoundRobinMs = 30000;

        public static void Decide(List<BtChokeInfo> peers, int slots, bool seeding, long now, bool rotateOptimistic, Random rng)
        {
            slots = Math.Max(1, slots);
            List<BtChokeInfo> pool = new List<BtChokeInfo>();
            foreach (BtChokeInfo p in peers)
            {
                p.Unchoke = false;
                p.OptimisticNext = false;
                if (p.Interested && !p.IsSeed) pool.Add(p);
            }

            List<BtChokeInfo> regular = new List<BtChokeInfo>();
            if (!seeding)
            {
                foreach (BtChokeInfo p in pool) if (!p.Snubbed) regular.Add(p);
                regular.Sort(delegate(BtChokeInfo a, BtChokeInfo b)
                {
                    if (a.DownBps != b.DownBps) return b.DownBps.CompareTo(a.DownBps);
                    if (a.Unchoked != b.Unchoked) return a.Unchoked ? -1 : 1;   // равные — без лишнего дёрганья
                    return b.UpBps.CompareTo(a.UpBps);
                });
            }
            else
            {
                regular.AddRange(pool);
                regular.Sort(delegate(BtChokeInfo a, BtChokeInfo b)
                {
                    bool ka = a.Unchoked && !a.Optimistic && now - a.UnchokedAtMs < RoundRobinMs;
                    bool kb = b.Unchoked && !b.Optimistic && now - b.UnchokedAtMs < RoundRobinMs;
                    if (ka != kb) return ka ? -1 : 1;
                    if (ka) return b.UpBps.CompareTo(a.UpBps);
                    // Открытый сверх срока не «ждёт» вовсе: его очередь — после всех закрытых.
                    long wa = a.Unchoked ? now : a.UnchokedAtMs, wb = b.Unchoked ? now : b.UnchokedAtMs;
                    if (wa != wb) return wa.CompareTo(wb);
                    return b.UpBps.CompareTo(a.UpBps);
                });
            }
            for (int i = 0; i < regular.Count && i < slots; i++) regular[i].Unchoke = true;

            // Оптимистичный слот: прежний держится до ротации, иначе — дольше всех ждущий (при равенстве — случайный).
            BtChokeInfo keep = null;
            if (!rotateOptimistic)
                foreach (BtChokeInfo p in pool)
                    if (p.Optimistic && p.Unchoked && !p.Unchoke) { keep = p; break; }
            if (keep == null)
            {
                List<BtChokeInfo> waiting = new List<BtChokeInfo>();
                long oldest = long.MaxValue;
                foreach (BtChokeInfo p in pool)
                {
                    if (p.Unchoke) continue;
                    if (rotateOptimistic && p.Optimistic && pool.Count - CountUnchoke(pool) > 1) continue;
                    long wait = p.Unchoked ? now : p.UnchokedAtMs;       // открытый сейчас не ждёт
                    if (wait < oldest) { oldest = wait; waiting.Clear(); }
                    if (wait == oldest) waiting.Add(p);
                }
                if (waiting.Count > 0) keep = waiting[rng == null ? 0 : rng.Next(waiting.Count)];
            }
            if (keep != null)
            {
                keep.Unchoke = true;
                keep.OptimisticNext = true;
            }
        }

        private static int CountUnchoke(List<BtChokeInfo> pool)
        {
            int n = 0;
            foreach (BtChokeInfo p in pool) if (p.Unchoke) n++;
            return n;
        }
    }
}
