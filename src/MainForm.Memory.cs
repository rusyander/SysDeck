using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace WindowsProcessCleaner
{
    // Память выбора — одна на всё окно.
    //
    // Раньше каждая вкладка решала вопрос «что отметил пользователь» сама, а чаще не решала
    // вовсе: список перечитывался, и галочки возвращались к умолчанию — вместе с ними терялось
    // и то, что человек только что расставил руками. Здесь это сделано один раз и одинаково.
    //
    // Две полки:
    //  * постоянная (config.json, поле UiChecks) — для наборов с устойчивым именем: категории
    //    очистки, пункты «Windows: лишнее», пакеты обновлений, установленные программы,
    //    dev-порты, отдельные флажки и выбор в списке «Где искать». Их состав от запуска к
    //    запуску тот же, поэтому вернуть выбор безопасно и полезно;
    //  * сеансовая (словарь в памяти) — для находок конкретного сканирования: файлы на диске,
    //    процессы, закладки. Их состав меняется каждый раз, и вернуть галочку «удалить» на
    //    другой файл, случайно оказавшийся по тому же пути, — ровно тот несчастный случай, от
    //    которого приложение бережётся всем остальным своим устройством. В пределах сеанса
    //    выбор всё равно переживает и обновление списка, и уход на другую вкладку.
    //
    // Обе полки — словари. Список UiChecks существует только ради сериализации и собирается
    // заново перед записью: поиск перебором по нему обходился в тысячи сравнений строк на
    // каждую строку каждого списка, а «Все» по вкладке «Диск» — в миллионы, то есть ровно в
    // то подвисание, от которого избавлено всё остальное окно.
    //
    // Не запоминается сознательно: галочки на вкладке «Автозапуск» — там галочка не выбор, а
    // текущее состояние записи в Windows, и «вспомнить» его значило бы соврать про систему.
    public partial class MainForm
    {
        private const char MemSep = '\t';

        // Значение и порядковый номер последнего касания: по нему вытесняются самые старые
        // записи, когда постоянная полка упирается в потолок.
        private sealed class MemEntry
        {
            public string Value;
            public long Seq;
            public MemEntry(string value, long seq) { Value = value; Seq = seq; }
        }

        private readonly Dictionary<string, MemEntry> _memPersist = new Dictionary<string, MemEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _memSession = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _memLoaded;
        private bool _memDirty;      // есть несохранённые изменения постоянной полки
        private long _memSeq;
        // Идёт программное заполнение списка: события галочек в это время — не выбор
        // пользователя. Счётчик, а не флаг: заполнения вкладок умеют вкладываться друг в друга.
        private int _memFilling;
        private Timer _memSaveTimer;

        private static string MemId(string scope, string key)
        {
            return scope + MemSep + (key == null ? "" : key);
        }

        // Разбор config.json в словарь — один раз за запуск, при первом обращении.
        private void MemLoad()
        {
            if (_memLoaded) return;
            _memLoaded = true;
            List<string> all = _engine.Config.UiChecks;
            if (all == null) return;
            for (int i = 0; i < all.Count; i++)
            {
                string s = all[i];
                if (s == null) continue;
                int b = s.IndexOf(MemSep);
                if (b < 0) continue;
                int e = s.IndexOf(MemSep, b + 1);
                if (e < 0) continue;
                _memPersist[s.Substring(0, e)] = new MemEntry(s.Substring(e + 1), ++_memSeq);
            }
        }

        // Обратная сборка списка перед записью: самые давно не тронутые записи отбрасываются,
        // если их стало больше потолка.
        private void MemStore()
        {
            List<KeyValuePair<string, MemEntry>> ordered = new List<KeyValuePair<string, MemEntry>>(_memPersist);
            ordered.Sort(delegate(KeyValuePair<string, MemEntry> x, KeyValuePair<string, MemEntry> y)
            { return x.Value.Seq.CompareTo(y.Value.Seq); });

            int skip = ordered.Count > AppConfig.UiChecksMax ? ordered.Count - AppConfig.UiChecksMax : 0;
            List<string> all = new List<string>(ordered.Count - skip);
            for (int i = skip; i < ordered.Count; i++)
                all.Add(ordered[i].Key + MemSep + ordered[i].Value.Value);
            _engine.Config.UiChecks = all;
        }

        // ---------- значения ----------

        private string MemGet(string scope, string key, bool persist)
        {
            string id = MemId(scope, key);
            if (!persist)
            {
                string v;
                return _memSession.TryGetValue(id, out v) ? v : null;
            }
            MemLoad();
            MemEntry e;
            return _memPersist.TryGetValue(id, out e) ? e.Value : null;
        }

        private void MemSet(string scope, string key, string value, bool persist)
        {
            string id = MemId(scope, key);
            if (value == null) value = "";
            if (!persist) { _memSession[id] = value; return; }
            MemLoad();
            MemEntry e;
            if (_memPersist.TryGetValue(id, out e))
            {
                if (e.Value == value) return;      // ничего не изменилось — и записывать нечего
                e.Value = value;
                e.Seq = ++_memSeq;
            }
            else _memPersist[id] = new MemEntry(value, ++_memSeq);
            MemSaveSoon();
        }

        private bool MemBool(string scope, string key, bool def, bool persist)
        {
            string v = MemGet(scope, key, persist);
            if (v == null) return def;
            return v == "1";
        }

        private void MemSetBool(string scope, string key, bool on, bool persist)
        {
            MemSet(scope, key, on ? "1" : "0", persist);
        }

        // Щелчков бывает много подряд («Все» / «Ничего» по сотне строк) — конфиг пишем один раз.
        // Таймер взводится на первую несохранённую запись и больше не трогается до срабатывания:
        // Stop+Start у Forms.Timer обходится в пару миллисекунд (пересоздаётся системный таймер),
        // и на пятистах строках это была секунда с лишним неподвижного окна.
        private void MemSaveSoon()
        {
            _memDirty = true;
            if (_memSaveTimer == null)
            {
                _memSaveTimer = new Timer();
                _memSaveTimer.Interval = 400;
                _memSaveTimer.Tick += delegate { _memSaveTimer.Stop(); MemWrite(); };
            }
            if (!_memSaveTimer.Enabled) _memSaveTimer.Start();
        }

        private void MemWrite()
        {
            if (!_memDirty) return;
            _memDirty = false;
            try { MemStore(); _engine.SaveConfig(); } catch { }
        }

        // Конфиг пишется по таймеру — при закрытии окна ждать его уже некому.
        private void MemFlush()
        {
            if (_memSaveTimer != null) _memSaveTimer.Stop();
            MemWrite();
        }

        // ---------- списки ----------

        // Подписать список один раз: с этого момента щелчок пользователя сразу уходит в память.
        private void MemWatch(ListView lv, string scope, bool persist, Func<ListViewItem, string> key)
        {
            lv.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                if (_memFilling > 0) return;
                string k = key(e.Item);
                if (k != null) MemSetBool(scope, k, e.Item.Checked, persist);
            };
        }

        private void MemBeginFill() { _memFilling++; }

        // Закрыть заполнение, когда возвращать галочки списку целиком не нужно: строка
        // выставлена поштучно (обновление одной категории по ходу анализа).
        private void MemEndFillPlain() { if (_memFilling > 0) _memFilling--; }

        // Вернуть галочки после перезаполнения. def(item) — как строка выглядит, если про неё
        // ничего не помним (для очистки это «рекомендована и непустая», для «лишнего» — каталожное
        // умолчание). key(item) == null — строку не трогаем: это заголовок или пояснение.
        private void MemEndFill(ListView lv, string scope, bool persist,
                                Func<ListViewItem, string> key, Func<ListViewItem, bool> def)
        {
            try
            {
                foreach (ListViewItem it in lv.Items)
                {
                    string k = key(it);
                    if (k == null) continue;
                    bool want = MemBool(scope, k, def == null ? it.Checked : def(it), persist);
                    if (it.Checked != want) it.Checked = want;
                }
            }
            finally { if (_memFilling > 0) _memFilling--; }
        }

        // Строка без галочки (заголовок группы, пояснение) ключа не имеет.
        private static bool MemRowSkipped(ListViewItem it)
        {
            return it == null || it.Tag == null || ReferenceEquals(it.Tag, NoCheckTag);
        }
    }
}
