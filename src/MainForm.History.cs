// SysDeck — вкладка «История»
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
    public partial class MainForm
    {
        private Control BuildHistoryTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);
            FlowLayoutPanel bar = MkToolbar();
            Button refresh = MkFlowButton(Tr.S("Обновить", "Refresh"), 110, false);
            refresh.Click += delegate { RefreshHistory(); };
            Label hint = MkFlowLabel(Tr.S("Каждая очистка процессов: сколько завершено и сколько памяти освобождено",
                                          "Every process cleanup: how many were terminated and how much memory was freed"), true);
            bar.Controls.Add(refresh);
            bar.Controls.Add(hint);

            _lvHistory = new FastListView();
            _lvHistory.Dock = DockStyle.Fill;
            _lvHistory.View = View.Details;
            _lvHistory.FullRowSelect = true;
            _lvHistory.Columns.Add(Tr.S("Дата и время", "Date and time"), 175);
            _lvHistory.Columns.Add(Tr.S("Завершено", "Terminated"), 110);
            _lvHistory.Columns.Add(Tr.S("Освобождено", "Freed"), 130);
            _lvHistory.Columns.Add(Tr.S("Процессы", "Processes"), 460);
            SetupOwnerDraw(_lvHistory);

            tab.Controls.Add(_lvHistory);
            tab.Controls.Add(bar);
            return tab;
        }

        private void RefreshHistory()
        {
            HistoryFile h = _engine.LoadHistory();
            _lvHistory.BeginUpdate();
            try
            {
                _lvHistory.Items.Clear();
                List<ListViewItem> rows = new List<ListViewItem>();
                foreach (HistoryEntry e in h.Entries)
                {
                    ListViewItem it = new ListViewItem(e.DateTime);
                    it.SubItems.Add(e.TerminatedCount.ToString());
                    it.SubItems.Add(Engine.FormatBytes(e.FreedBytes));
                    it.SubItems.Add(e.Processes != null ? string.Join(", ", e.Processes.ToArray()) : "");
                    rows.Add(it);
                }
                _lvHistory.Items.AddRange(rows.ToArray());
            }
            finally { _lvHistory.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvHistory);
        }
    }
}
