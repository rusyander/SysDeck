// SysDeck — палитра светлой и тёмной темы
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
    // ------------------------------------------------------------------ //
    //  Тема оформления (светлая / тёмная / по системе)
    // ------------------------------------------------------------------ //
    public class Theme
    {
        public bool Dark;
        public Color Bg;          // фон окна
        public Color Surface;     // фон полей/списков
        public Color Text;        // основной текст
        public Color Subtle;      // приглушённый текст
        public Color Accent;      // акцент (кнопки, выделение)
        public Color AccentText;  // текст на акценте
        public Color Border;      // границы
        public Color CandidateBg; // строка-кандидат
        public Color WhiteBg;     // строка из белого списка
        public Color Header;      // фон заголовков колонок

        public static Theme Light()
        {
            Theme t = new Theme();
            t.Dark = false;
            t.Bg = Color.FromArgb(243, 244, 246);
            t.Surface = Color.FromArgb(255, 255, 255);
            t.Text = Color.FromArgb(28, 30, 34);
            t.Subtle = Color.FromArgb(110, 116, 124);
            t.Accent = Color.FromArgb(37, 99, 235);
            t.AccentText = Color.White;
            t.Border = Color.FromArgb(227, 230, 235);
            t.CandidateBg = Color.FromArgb(255, 243, 214);
            t.WhiteBg = Color.FromArgb(226, 240, 228);
            t.Header = Color.FromArgb(233, 236, 240);
            return t;
        }

        public static Theme DarkTheme()
        {
            Theme t = new Theme();
            t.Dark = true;
            t.Bg = Color.FromArgb(24, 25, 28);
            t.Surface = Color.FromArgb(37, 39, 44);
            t.Text = Color.FromArgb(228, 230, 234);
            t.Subtle = Color.FromArgb(150, 156, 164);
            t.Accent = Color.FromArgb(59, 130, 246);
            t.AccentText = Color.White;
            t.Border = Color.FromArgb(50, 53, 60);
            t.CandidateBg = Color.FromArgb(74, 60, 30);
            t.WhiteBg = Color.FromArgb(38, 54, 40);
            t.Header = Color.FromArgb(45, 47, 53);
            return t;
        }

        // Разрешить "system" через реестр Windows.
        public static Theme Resolve(string mode)
        {
            if (mode == "light") return Light();
            if (mode == "dark") return DarkTheme();
            // system
            return SystemIsLight() ? Light() : DarkTheme();
        }

        public static bool SystemIsLight()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) return ((int)v) != 0;
                    }
                }
            }
            catch { }
            return true; // по умолчанию светлая
        }
    }
}
