// Windows Process Cleaner — «Загрузки», торренты: сборка клиента из частей.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Части не знают друг о друге (Torrent.Contracts.cs): сессия берёт трекеры, DHT, LSD, расширения BEP 10 и шифрование
// из BtFactories. Install заполняет их один раз до первой BtSession. Порядок расширений — их номера в рукопожатии:
// ut_metadata = 1, ut_pex = 2 (номер снимается при создании торрента, пустой слот свой номер сохраняет).
using System;
using System.Collections.Generic;

namespace WindowsProcessCleaner.Downloads
{
    internal static class BtWiring
    {
        private static readonly object Gate = new object();
        private static bool _installed;

        public static void Install()
        {
            lock (Gate)
            {
                if (_installed) return;
                _installed = true;
                BtFactories.Trackers = delegate(IBtSwarm swarm, BtContext ctx, IList<List<string>> tiers) { return new BtTrackers(swarm, ctx, tiers); };
                BtFactories.Dht = delegate(BtContext ctx) { return new BtDht(ctx); };
                BtFactories.Lsd = delegate(BtContext ctx) { return new BtLsd(ctx); };
                BtFactories.PortMapper = delegate { return new BtPortMapper(); };
                BtFactories.Extensions.Add(delegate(IBtSwarm swarm, BtContext ctx) { return new BtMetadataExt(swarm, ctx); });
                BtFactories.Extensions.Add(Pex);
                BtFactories.MseOutgoing = BtMse.Outgoing;
                BtFactories.MseIncoming = BtMse.Incoming;
            }
        }

        // PEX выключен в настройках сессии — расширения нет и в рукопожатии; частному торренту его не создаёт сама фабрика.
        private static IBtExtension Pex(IBtSwarm swarm, BtContext ctx)
        {
            BtTorrent t = swarm as BtTorrent;
            if (t != null && !t.Session.Options.EnablePex) return null;
            return BtPexExt.Create(swarm, ctx);
        }
    }
}
