// SysDeck — окно: перенос задач Планировщика и правила брандмауэра со старого имени программы (Rebrand).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Папка данных и ключи Run переезжают сами при запуске, а задачам с наивысшими правами и брандмауэру нужны права
// администратора. Окно само запрос прав не показывает: оно спрашивает обычным вопросом, и окно UAC появляется только
// после «Да». «Нет» — старые задачи остаются как есть, вопрос повторится при следующем запуске.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck
{
    public partial class MainForm
    {
        private bool _rebrandAsked;

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && _ready) RebrandOfferOnce();   // окно, запущенное в трей, спрашивает, когда его впервые открыли
        }

        private void RebrandOfferOnce()
        {
            if (_rebrandAsked || _selfTest) return;
            _rebrandAsked = true;
            string exe = Application.ExecutablePath;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> tasks;
                bool firewall;
                try
                {
                    tasks = Rebrand.LegacyTasksPresent(exe);
                    firewall = Rebrand.LegacyFirewallRulePresent();
                }
                catch (Exception) { return; }
                if (tasks.Count == 0 && !firewall) return;
                try { BeginInvoke((MethodInvoker)delegate { RebrandAsk(tasks, firewall); }); }
                catch (InvalidOperationException) { }
            });
        }

        private void RebrandAsk(List<string> tasks, bool firewall)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Tr.S("Программа теперь называется SysDeck. Под старым именем остались и указывают на прежний файл:",
                               "The program is now called SysDeck. These still use the old name and point to the old file:"));
            sb.AppendLine();
            foreach (string t in tasks)
                sb.AppendLine("• " + Tr.S("задача Планировщика «", "Task Scheduler task “") + t + Tr.S("»", "”"));
            if (firewall) sb.AppendLine("• " + Tr.S("правило брандмауэра «", "firewall rule “") + Rebrand.LegacyFirewallRule + Tr.S("»", "”"));
            sb.AppendLine();
            sb.Append(Tr.S("Перенести на новое имя? Windows один раз спросит права администратора.",
                           "Move them to the new name? Windows will ask for administrator rights once."));
            if (MessageBox.Show(this, sb.ToString(), "SysDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;

            bool hud = tasks.Contains(Rebrand.LegacyName + " HUD");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                if (Elevated) error = Rebrand.MigrateElevated(_engine);
                else
                {
                    ElevJob job = new ElevJob();
                    job.Kind = "rebrand";
                    ElevResult r = Elevation.Run(_engine, job, null, null);
                    error = r.Ok ? null : r.Declined ? DeclinedNote() : (r.Message ?? "rebrand");
                }
                // Оверлей, поднятый без прав из-за отсутствия задачи, перезапускается уже через новую задачу.
                if (error == null && hud) SysDeck.Capture.HudLauncher.Restart();
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        MessageBox.Show(this, error == null ? Tr.S("Автозапуск перенесён на SysDeck.", "Autostart now uses SysDeck.")
                                                            : Tr.S("Перенести не удалось: ", "Could not move: ") + error,
                                        "SysDeck", MessageBoxButtons.OK, error == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    });
                }
                catch (InvalidOperationException) { }
            });
        }
    }
}
