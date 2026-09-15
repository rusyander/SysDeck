// SysDeck — состав категории: ключи целей, выбор пользователя, пересчёт
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysDeck
{
    public partial class Engine
    {
        // ---------- Состав категории: что именно из неё удалять ----------
        // Пользователь может снять галочку с отдельной папки внутри категории. Выбор живёт
        // в Config.CleanUnchecked по стабильному ключу «категория|путь|маска», поэтому
        // переживает перезапуск и повторный анализ. Отключённые цели по-прежнему
        // обмеряются (в «Составе» виден их размер), но в сумму категории и в удаление
        // не попадают.
        private void ApplyTargetChoice(List<CleanCategory> cats)
        {
            foreach (CleanCategory c in cats)
            {
                foreach (CleanTarget t in c.Targets)
                {
                    t.Key = TargetKey(c, t);
                    t.Enabled = !IsTargetOff(t.Key);
                }
                c.BinEnabled = c.RecycleBin && !IsTargetOff(BinKey(c));
            }
        }

        public static string TargetKey(CleanCategory c, CleanTarget t)
        {
            return ((c.Id ?? c.Title ?? "") + "|" + (t.Path ?? "") + "|" + (t.Mask ?? "")).ToLowerInvariant();
        }

        public static string BinKey(CleanCategory c)
        {
            return ((c.Id ?? c.Title ?? "") + "|recyclebin").ToLowerInvariant();
        }

        // oemNN.inf меняется при переустановке драйвера, поэтому ключ — исходный inf + версия + поставщик
        public static string DriverKey(DriverPackage d)
        {
            return ("driverstore|" + (d.Original ?? "") + "|" + (d.Version ?? "") + "|" + (d.Provider ?? "")).ToLowerInvariant();
        }

        public bool IsTargetOff(string key)
        {
            List<string> off = Config.CleanUnchecked;
            if (off == null || string.IsNullOrEmpty(key)) return false;
            foreach (string k in off)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Меняет выбор в конфиге; сохранение — на вызывающем, один раз за диалог.
        public void SetTargetEnabled(string key, bool enabled)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (Config.CleanUnchecked == null) Config.CleanUnchecked = new List<string>();
            List<string> off = Config.CleanUnchecked;
            for (int i = off.Count - 1; i >= 0; i--)
                if (string.Equals(off[i], key, StringComparison.OrdinalIgnoreCase)) off.RemoveAt(i);
            if (!enabled) off.Add(key);
        }

        // Итоги категории из уже посчитанных целей — без повторного обхода диска.
        // Сумма по категориям без двойного счёта: цели вкладываются друг в друга
        // (Temp целиком в «Системном мусоре» и Temp\*.tmp у winapp2, сборки Playwright внутри
        // папки Playwright) и одна и та же папка встречается в двух категориях. Цель-дерево
        // (без маски, с подпапками) накрывает всё, что лежит под ней; точный дубль цели
        // считается один раз. Корзина — один раз на всё.
        public static long DistinctSize(IEnumerable<CleanCategory> cats)
        {
            long total = 0;
            bool binCounted = false;
            List<CleanTarget> plain = new List<CleanTarget>();
            foreach (CleanCategory c in cats)
            {
                if (c == null) continue;
                if (c.Kind != null) { total += c.Size; continue; }
                foreach (CleanTarget t in c.Targets)
                    if (t.Enabled && t.Analyzed && !t.Guarded && t.Size > 0) plain.Add(t);
                if (c.RecycleBin && c.BinEnabled && !binCounted) { total += c.BinSize; binCounted = true; }
            }
            plain.Sort(delegate(CleanTarget a, CleanTarget b) { return a.Path.Length.CompareTo(b.Path.Length); });
            List<string> trees = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            foreach (CleanTarget t in plain)
            {
                string pl = t.Path.TrimEnd('\\').ToLowerInvariant();
                string plSlash = pl + "\\";
                bool covered = false;
                foreach (string tree in trees)
                    if (plSlash.StartsWith(tree, StringComparison.Ordinal)) { covered = true; break; }
                if (covered) continue;
                string key = pl + "|" + (t.Mask ?? "") + "|" + t.Recurse + "|" + t.MinAgeMinutes + "|" + t.ContentsOnly;
                if (!seen.Add(key)) continue;
                total += t.Size;
                if (string.IsNullOrEmpty(t.Mask) && t.Recurse) trees.Add(plSlash);
            }
            return total;
        }

        public static void RecalcCategory(CleanCategory c)
        {
            long size = 0, off = 0; int files = 0, offN = 0;
            if (c.Kind == "driverstore")
            {
                if (c.Drivers != null)
                    foreach (DriverPackage d in c.Drivers)
                    {
                        if (d.Enabled) { size += d.Size; files++; }
                        else { off += d.Size; offN++; }
                    }
                c.Size = size; c.FileCount = files; c.SizeOff = off; c.TargetsOff = offN;
                return;
            }
            if (c.Kind != null) return;   // winsxs: размер даёт DISM, состава нет
            foreach (CleanTarget t in c.Targets)
            {
                if (t.Enabled) { size += t.Size; files += t.FileCount; }
                else { off += t.Size; offN++; }
            }
            if (c.RecycleBin)
            {
                if (c.BinEnabled) { size += c.BinSize; files += c.BinCount; }
                else { off += c.BinSize; offN++; }
            }
            c.Size = size; c.FileCount = files; c.SizeOff = off; c.TargetsOff = offN;
        }
    }
}
