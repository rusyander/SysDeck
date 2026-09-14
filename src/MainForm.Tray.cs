// Windows Process Cleaner — трей, выход, перезапуск от администратора, автоочистка по таймеру
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

namespace WindowsProcessCleaner
{
    public partial class MainForm
    {
        // ---------- Трей ----------
        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = _iconIdle;
            _tray.Text = "Windows Process Cleaner";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowWindow(); };

            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add(new MenuItem(Tr.S("Открыть", "Open"), delegate { ShowWindow(); }));
            menu.MenuItems.Add(new MenuItem(Tr.S("Сканировать сейчас", "Scan now"), delegate { ShowWindow(); DoScan(); }));
            menu.MenuItems.Add(new MenuItem(Tr.S("Очистить сейчас", "Clean now"), delegate { RunAutoClean(true); }));
            menu.MenuItems.Add(new MenuItem(Tr.S("Очистить Standby Memory", "Purge Standby Memory"), delegate { DoPurgeOnly(); }));
            menu.MenuItems.Add(new MenuItem(Tr.S("⚡ Ускорить", "⚡ Boost"), delegate { ShowWindow(); ShowPage(PageHome); DoBoost(); }));
            _miAuto = new MenuItem(Tr.S("Автоочистка по таймеру", "Auto-clean timer"), delegate { ToggleAuto(); });
            menu.MenuItems.Add(_miAuto);
            menu.MenuItems.Add(CapTrayMenu());
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(new MenuItem(Tr.S("Перезапустить от администратора", "Restart as administrator"), delegate { RestartAsAdmin(); }));
            menu.MenuItems.Add(new MenuItem(Tr.S("Выход", "Exit"), delegate { ExitApp(); }));
            _tray.ContextMenu = menu;
        }

        public void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            if (_currentPage == PageHome) HomeEnter();
        }

        private void ExitApp()
        {
            if (!ConfirmExitDuringWrite()) return;
            ExitNow();
        }

        private void ExitNow()
        {
            _reallyExit = true;
            _tray.Visible = false;
            Application.Exit();
        }

        private void ToggleAuto()
        {
            _engine.Config.AutoEnabled = !_engine.Config.AutoEnabled;
            _engine.SaveConfig();
            LoadSettingsToUi();
            RescheduleAuto();
        }

        private void RestartAsAdmin()
        {
            if (!ConfirmExitDuringWrite()) return;   // спросить ДО запуска второй копии
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                ExitNow();
            }
            catch { /* пользователь отклонил UAC */ }
        }

        // ---------- Автоочистка по расписанию ----------
        private void RescheduleAuto()
        {
            if (_engine.Config.AutoEnabled)
                _nextAuto = DateTime.Now.AddHours(_engine.Config.AutoIntervalHours);
            else
                _nextAuto = DateTime.MaxValue;
        }

        private void CheckAutoSchedule()
        {
            if (!_engine.Config.AutoEnabled) return;
            if (DateTime.Now >= _nextAuto)
            {
                RunAutoClean(false);
                _nextAuto = DateTime.Now.AddHours(_engine.Config.AutoIntervalHours);
            }
        }

        // ---------- Трей: индикация ----------
        private void UpdateTrayState()
        {
            int candidates = 0;
            foreach (ProcInfo p in _lastScan) if (p.IsCandidate) candidates++;
            if (_tray == null) return;
            if (candidates > 0)
            {
                _tray.Icon = _iconActive;
                _tray.Text = Tr.S("Process Cleaner · кандидатов: ", "Process Cleaner · candidates: ") + candidates;
            }
            else
            {
                _tray.Icon = _iconIdle;
                _tray.Text = "Windows Process Cleaner";
            }
        }

        private static string YesNo(bool v) { return v ? Tr.S("да", "yes") : Tr.S("нет", "no"); }

        private static string FormatSpan(TimeSpan t)
        {
            string s = Tr.S("с", "s"), m = Tr.S("м", "m"), h = Tr.S("ч", "h");
            if (t.TotalSeconds < 1) return "-";
            if (t.TotalMinutes < 1) return (int)t.TotalSeconds + s;
            if (t.TotalHours < 1) return (int)t.TotalMinutes + m;
            return (int)t.TotalHours + h + " " + t.Minutes + m;
        }
    }
}
