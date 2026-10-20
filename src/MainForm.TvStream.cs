// SysDeck — страница «ТВ-трансляция»: пункт в меню слева, кнопка на вкладке «Инструменты», пункт трея.
// Содержимое страницы — то же окно, что у отдельного PcStream (Tv.Form.cs), встроенное без рамки (TvForm.Embed).
// Сервер живёт в процессе SysDeck, пока тот работает в трее; уход со страницы трансляцию не выключает.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SysDeck.TvStream;

namespace SysDeck
{
    public partial class MainForm
    {
        private Panel _tvPage;
        private TvForm _tvView;
        private MenuItem _miTv;

        // Из конструктора: настройки и ffmpeg — сразу, сервер — только если пользователь включил автозапуск и есть папки.
        private void TvInit()
        {
            try
            {
                TvHost.Init(Path.Combine(Engine.DefaultDataDir(), "tvstream"));
                TvHost.Changed += delegate { TvUpdateTray(); };
                TvHost.AutoStartIfWanted();
            }
            catch (Exception ex) { TvHost.LastError = ex.Message; }
        }

        // До Application.Exit: встроенное окно уничтожается раньше перебора OpenForms (см. ExitNow), сервер гасит ffmpeg.
        private void TvShutdown()
        {
            if (_tvView != null && !_tvView.IsDisposed) _tvView.Dispose();
            _tvView = null;
            TvHost.Shutdown();
        }

        // Пустая страница: окно трансляции строится при первом входе — запуск SysDeck не платит за невидимые карточки.
        private Control BuildTvTab()
        {
            _tvPage = new Panel();
            return _tvPage;
        }

        private void TvEnter()
        {
            if (!TvHost.Ready) TvInit();
            if (_tvView == null || _tvView.IsDisposed)
            {
                _tvView = new TvForm(_theme, "SysDeck");
                _tvView.Embed();
                _tvPage.Controls.Add(_tvView);
                _tvView.Show();
            }
            _tvView.Opened();
        }

        private void TvOpen()
        {
            ShowWindow();
            ShowPage(PageTv);
        }

        private void TvRetheme()
        {
            if (_tvView != null && !_tvView.IsDisposed) _tvView.ApplyTheme(_theme);
        }

        private MenuItem TvTrayMenu()
        {
            _miTv = new MenuItem(Tr.S("📺 ТВ-трансляция", "📺 TV streaming"), delegate { TvOpen(); });
            TvUpdateTray();
            return _miTv;
        }

        private void TvUpdateTray()
        {
            if (_miTv == null) return;
            string text = TvHost.Running ? Tr.S("📺 ТВ-трансляция (включена)", "📺 TV streaming (on)") : Tr.S("📺 ТВ-трансляция", "📺 TV streaming");
            if (InvokeRequired) { try { BeginInvoke((MethodInvoker)delegate { _miTv.Text = text; }); } catch (InvalidOperationException) { } }
            else _miTv.Text = text;
        }

        // Секция «Медиа» на вкладке «Инструменты» — ведёт на страницу «ТВ-трансляция»
        // (секции идут снизу вверх: вызывается первой — оказывается внизу).
        private void AddTvToolSection(Panel body)
        {
            FlowLayoutPanel flow = MkToolbar();
            flow.Padding = new Padding(0, 4, 0, 6);
            Button b = MkFlowButton(Tr.S("📺 ТВ-трансляция", "📺 TV streaming"), 200, false);
            string desc = Tr.S("Смотреть видео с этого компьютера на телевизоре (Hisense VIDAA, Media Station X, браузер ТВ): выбор папок, адрес, кто смотрит.",
                               "Watch videos from this PC on the TV (Hisense VIDAA, Media Station X, TV browser): folders, address, who is watching.");
            b.Click += delegate { TvOpen(); };
            b.MouseEnter += delegate { _lblToolsInfo.Text = b.Text + " — " + desc; };
            _toolTips.SetToolTip(b, desc);
            flow.Controls.Add(b);
            Label head = new Label();
            head.Text = Tr.S("Медиа", "Media");
            head.Name = "section";
            head.Dock = DockStyle.Top;
            head.Height = 30;
            head.TextAlign = ContentAlignment.BottomLeft;
            head.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            body.Controls.Add(flow);
            body.Controls.Add(head);
            _toolSections.Add(new System.Collections.Generic.KeyValuePair<Label, FlowLayoutPanel>(head, flow));
        }
    }
}
