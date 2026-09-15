// SysDeck — вкладка «Оверлей»: дерево показателей, редактор пункта, пресеты.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Capture;

namespace SysDeck
{
    public partial class MainForm
    {
        // Узлы добавляются, как только источник назвал новый показатель (HWiNFO запустился, Afterburner появился);
        // уже построенные не перестраиваются — выделение и раскрытие не прыгают.
        private void OvSyncTree(HudFrame f)
        {
            bool first = _tvOv.Nodes.Count == 0, added = false;
            _ovSuppress = true;
            _tvOv.BeginUpdate();
            try
            {
                foreach (HudDef d in HudCatalog.BuiltIn) added |= OvEnsureNode(d);
                for (int i = 0; i < Environment.ProcessorCount; i++)
                {
                    added |= OvEnsureNode(HudCatalog.Dynamic(HudCatalog.CoreId(i, "load"), null));
                    added |= OvEnsureNode(HudCatalog.Dynamic(HudCatalog.CoreId(i, "mhz"), null));
                }
                if (f != null)
                {
                    List<string> keys = new List<string>(f.Values.Keys);
                    foreach (string id in keys)
                        if (!_ovNodes.ContainsKey(id)) added |= OvEnsureNode(HudCatalog.Dynamic(id, f.Get(id)));
                }
                foreach (HudItem it in _ovItems)
                    if (!_ovNodes.ContainsKey(it.Id)) added |= OvEnsureNode(HudCatalog.Dynamic(it.Id, null));
                if (added)
                {
                    foreach (TreeNode g in _tvOv.Nodes)
                    {
                        OvUpdateGroupCheck(g);
                        if (first && g.Checked == false && OvGroupHasChecked(g)) g.Expand();
                        else if (first && g.Checked) g.Expand();
                    }
                }
            }
            finally
            {
                _tvOv.EndUpdate();
                _ovSuppress = false;
            }
            if (first && _tvOv.Nodes.Count > 0 && _tvOv.SelectedNode == null) _tvOv.SelectedNode = _tvOv.Nodes[0];
        }

        private static bool OvGroupHasChecked(TreeNode g)
        {
            foreach (TreeNode c in g.Nodes) if (c.Checked) return true;
            return false;
        }

        private void OvUpdateGroupCheck(TreeNode g)
        {
            if (g == null) return;
            bool all = g.Nodes.Count > 0;
            foreach (TreeNode c in g.Nodes) if (!c.Checked) { all = false; break; }
            bool was = _ovSuppress;
            _ovSuppress = true;
            try { if (g.Checked != all) g.Checked = all; }
            finally { _ovSuppress = was; }
        }

        // У группы — «выбрано N из M» прямо в тексте узла (меняется редко). Значения строк в текст узла НЕ пишутся:
        // присваивание TreeNode.Text стоит ~15 мс, и на 300 датчиках такт страницы растягивался до 4 секунд.
        // Они лежат в _ovValues и дорисовываются в OvDrawNode; дерево перерисовывает только видимые узлы.
        private void OvUpdateNodeTexts(HudFrame f)
        {
            bool repaint = false;
            foreach (KeyValuePair<string, TreeNode> kv in _ovNodes)
            {
                string title;
                if (!_ovTitles.TryGetValue(kv.Key, out title)) continue;
                if (kv.Key.StartsWith(OvGroupTag, StringComparison.Ordinal))
                {
                    int on = 0;
                    foreach (TreeNode c in kv.Value.Nodes) if (c.Checked) on++;
                    string text = on == 0 ? title : title + "  ·  " + Tr.S("выбрано ", "selected ") + on.ToString(CultureInfo.InvariantCulture)
                                                     + Tr.S(" из ", " of ") + kv.Value.Nodes.Count.ToString(CultureInfo.InvariantCulture);
                    if (kv.Value.Text != text) kv.Value.Text = text;
                    continue;
                }
                HudValue v = f == null ? null : f.Get(kv.Key);
                string value = "  ·  " + (v == null ? Tr.S("нет данных", "no data") : HudFormat.Row(new HudItem(kv.Key), v).Value);
                string old;
                if (_ovValues.TryGetValue(kv.Key, out old) && old == value) continue;
                _ovValues[kv.Key] = value;
                if (kv.Value.IsVisible) repaint = true;
            }
            if (repaint) _tvOv.Invalidate();
        }

        private void OvDrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Node == null || _brSel == null) return;
            Rectangle r = e.Bounds;
            if (r.Width <= 0 || r.Height <= 0) return;
            bool sel = (e.State & TreeNodeStates.Selected) != 0;
            e.Graphics.FillRectangle(sel ? _brSel : _brSurface, r);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            string tag = e.Node.Tag as string, title, value;
            if (tag == null || !_ovTitles.TryGetValue(tag, out title) || !e.Node.Text.StartsWith(title, StringComparison.Ordinal)) title = e.Node.Text;
            string rest = e.Node.Text.Substring(title.Length);
            if (tag != null && _ovValues.TryGetValue(tag, out value)) rest += value;
            // Значение шире границ узла (они считаются по его тексту) — фон до правого края, чтобы от прежнего числа не оставалось хвоста.
            if (rest.Length > 0 && _tvOv.ClientSize.Width > r.Right)
                e.Graphics.FillRectangle(_brSurface, new Rectangle(r.Right, r.Y, _tvOv.ClientSize.Width - r.Right, r.Height));
            r = new Rectangle(r.X, r.Y, Math.Max(r.Width, _tvOv.ClientSize.Width - r.X), r.Height);
            int x = r.X + 2;
            int w = TextRenderer.MeasureText(e.Graphics, title, _tvOv.Font, new Size(int.MaxValue, r.Height), flags).Width;
            TextRenderer.DrawText(e.Graphics, title, _tvOv.Font, new Rectangle(x, r.Y, Math.Max(1, r.Right - x), r.Height), _theme.Text, flags);
            if (rest.Length > 0 && x + w < r.Right)
                TextRenderer.DrawText(e.Graphics, rest, _tvOv.Font, new Rectangle(x + w, r.Y, r.Right - x - w, r.Height), _theme.Subtle, flags);
        }

        private void OvAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_ovSuppress || _ovLoading || e.Node == null) return;
            string tag = e.Node.Tag as string;
            if (tag == null) return;
            _ovSuppress = true;
            try
            {
                if (tag.StartsWith(OvGroupTag, StringComparison.Ordinal))
                {
                    foreach (TreeNode c in e.Node.Nodes)
                    {
                        c.Checked = e.Node.Checked;
                        OvSetChecked(c.Tag as string, e.Node.Checked);
                    }
                }
                else
                {
                    OvSetChecked(tag, e.Node.Checked);
                    OvUpdateGroupCheck(e.Node.Parent);
                }
            }
            finally { _ovSuppress = false; }
            OvItemsChanged();
        }

        private void OvSetChecked(string id, bool on)
        {
            if (id == null) return;
            int at = _ovItems.FindIndex(delegate(HudItem x) { return x.Id == id; });
            if (on && at < 0) _ovItems.Add(new HudItem(id));
            else if (!on && at >= 0) _ovItems.RemoveAt(at);
        }

        private void OvCheckGroup(bool on)
        {
            TreeNode n = _tvOv.SelectedNode;
            if (n == null) return;
            if (n.Parent != null) n = n.Parent;
            _ovSuppress = true;
            try
            {
                foreach (TreeNode c in n.Nodes) { c.Checked = on; OvSetChecked(c.Tag as string, on); }
                OvUpdateGroupCheck(n);
            }
            finally { _ovSuppress = false; }
            OvItemsChanged();
        }

        // «Первые N ядер»: у отмеченных видов показателя ядра до N отмечаются, дальше — снимаются; неотмеченный вид снимается целиком.
        private void OvApplyCores()
        {
            int count = OvPick(OvCoreCounts, _cmbOvCoreCount, 4);
            if (count <= 0) count = int.MaxValue;
            _ovSuppress = true;
            try
            {
                for (int i = 0; i < Environment.ProcessorCount; i++)
                {
                    OvSetCore(HudCatalog.CoreId(i, "load"), _chkOvCoreLoad.Checked && i < count);
                    OvSetCore(HudCatalog.CoreId(i, "mhz"), _chkOvCoreMhz.Checked && i < count);
                }
                TreeNode g;
                if (_ovNodes.TryGetValue(OvGroupTag + HudGroups.Cores, out g)) { OvUpdateGroupCheck(g); g.Expand(); }
            }
            finally { _ovSuppress = false; }
            OvItemsChanged();
        }

        private void OvSetCore(string id, bool on)
        {
            TreeNode n;
            if (_ovNodes.TryGetValue(id, out n)) n.Checked = on;
            OvSetChecked(id, on);
        }

        // ---------- правка выбранной строки ----------

        private void OvShowEditor()
        {
            if (_tvOv == null) return;
            TreeNode n = _tvOv.SelectedNode;
            string tag = n == null ? null : n.Tag as string;
            bool group = tag != null && tag.StartsWith(OvGroupTag, StringComparison.Ordinal);
            bool item = tag != null && !group;
            bool was = _ovLoading;
            _ovLoading = true;
            try
            {
                string title;
                _lblOvTitle.Text = tag == null ? Tr.S("Выберите показатель или группу слева", "Pick a metric or a group on the left")
                                 : _ovTitles.TryGetValue(tag, out title) ? title : tag;
                _ovItemRow1.Visible = _ovItemRow2.Visible = _ovItemRow3.Visible = item;
                _ovGroupRow.Visible = group;
                _ovCoresRow.Visible = group && tag == OvGroupTag + HudGroups.Cores;
                if (item)
                {
                    HudItem it = OvFind(tag);
                    HudDef def = OvDef(tag);
                    bool on = it != null, numeric = def == null || def.Kind != HudKind.Text;
                    _chkOvText.Checked = it == null || it.Text;
                    _chkOvGraph.Checked = it != null && it.Graph && numeric;
                    _chkOvText.Enabled = on && numeric;
                    _chkOvGraph.Enabled = on && numeric;
                    _chkOvStats.Checked = it != null && it.Stats && numeric;
                    _chkOvStats.Enabled = on && numeric;
                    _cmbOvValueColor.SelectedIndex = OvColorIndex(it == null ? 0 : it.Color);
                    _cmbOvLabelColor.SelectedIndex = OvColorIndex(it == null ? 0 : it.LabelColor);
                    double warn, crit;
                    HudAlarm.Defaults(tag, def == null ? HudKind.Number : def.Kind, out warn, out crit);
                    _txtOvWarn.Text = it == null ? "" : OvThresholdText(it.Warn);
                    _txtOvCrit.Text = it == null ? "" : OvThresholdText(it.Crit);
                    _chkOvNoAlarm.Checked = it != null && it.NoAlarm;
                    foreach (Control c in _ovItemRow3.Controls) c.Enabled = on && numeric;
                    string unit = def != null && def.Kind == HudKind.Memory ? " %" : "";
                    _lblOvThresholds.Text = HudFormat.Valid(warn) || HudFormat.Valid(crit)
                        ? Tr.S("по умолчанию ", "default ") + (HudFormat.Valid(warn) ? OvThresholdText(warn) : "—") + " / " + (HudFormat.Valid(crit) ? OvThresholdText(crit) : "—") + unit
                          + (HudAlarm.LowIsBad(tag) ? Tr.S(" (плохо, когда ниже)", " (bad when lower)") : "")
                        : Tr.S("по умолчанию не подсвечивается", "not highlighted by default");
                    _cmbOvInterval.Items[0] = Tr.S("по умолчанию (", "default (") + OvMs(def == null ? 1000 : def.IntervalMs) + ")";
                    int idx = it == null ? 0 : Array.IndexOf(OvIntervals, it.IntervalMs);
                    _cmbOvInterval.SelectedIndex = idx < 0 ? 0 : idx;
                    _cmbOvInterval.Enabled = on;
                    foreach (Control c in _ovItemRow2.Controls) c.Enabled = on;
                    int pos = on ? _ovItems.IndexOf(it) : -1;
                    _btnOvUp.Enabled = pos > 0;
                    _btnOvDown.Enabled = pos >= 0 && pos < _ovItems.Count - 1;
                }
            }
            finally { _ovLoading = was; }
            HudFrame f;
            lock (_ovGate) f = _ovFrame;
            OvUpdateSource(f);
            _ovPreviewDirty = true;
        }

        private static string OvMs(int ms)
        {
            if (ms >= 60000 && ms % 60000 == 0) return (ms / 60000).ToString(CultureInfo.InvariantCulture) + Tr.S(" мин", " min");
            if (ms % 1000 == 0) return (ms / 1000).ToString(CultureInfo.InvariantCulture) + Tr.S(" с", " s");
            string s = (ms / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);
            return Tr.S(s.Replace('.', ',') + " с", s + " s");
        }

        // «Источник · сейчас · место в столбике» — чтобы было видно, откуда число и почему прочерк.
        private void OvUpdateSource(HudFrame f)
        {
            if (_lblOvSource == null || _tvOv == null) return;
            TreeNode n = _tvOv.SelectedNode;
            string tag = n == null ? null : n.Tag as string;
            string text;
            if (tag == null) text = Tr.S("Галочка — строка в столбике. Порядок строк — порядок, в котором их отмечали; меняется кнопками «Выше» и «Ниже».",
                                         "A tick puts the row into the column. Rows follow the order they were ticked in; change it with “Up” and “Down”.");
            else if (tag.StartsWith(OvGroupTag, StringComparison.Ordinal))
                text = Tr.S("Галочка у группы отмечает или снимает все её строки. Справа внизу — как выглядит группа целиком.",
                            "The group tick checks or unchecks all its rows. Bottom right shows the whole group.");
            else
            {
                HudDef def = OvDef(tag);
                HudValue v = f == null ? null : f.Get(tag);
                HudItem it = OvFind(tag);
                StringBuilder sb = new StringBuilder();
                sb.Append(Tr.S("Источник: ", "Source: ")).Append(def == null ? "?" : def.Source);
                sb.Append(Tr.S(" · сейчас: ", " · now: ")).Append(v == null ? Tr.S("нет данных", "no data") : HudFormat.Row(new HudItem(tag), v).Value);
                if (v != null && !string.IsNullOrEmpty(v.Note) && tag == "hot.max") sb.Append(Tr.S(" · датчик: ", " · sensor: ")).Append(v.Note);
                if (it == null) sb.Append(Tr.S(" · не в столбике — отметьте галочку", " · not in the column — tick it"));
                else sb.Append(Tr.S(" · строка ", " · row ")).Append((_ovItems.IndexOf(it) + 1).ToString(CultureInfo.InvariantCulture))
                       .Append(Tr.S(" из ", " of ")).Append(_ovItems.Count.ToString(CultureInfo.InvariantCulture));
                text = sb.ToString();
            }
            if (_lblOvSource.Text != text) _lblOvSource.Text = text;
            string help = tag == null ? "" : tag.StartsWith(OvGroupTag, StringComparison.Ordinal)
                ? HudHelp.ForGroup(tag.Substring(OvGroupTag.Length)) : HudHelp.ForId(tag);
            if (_lblOvHelp.Text != help) _lblOvHelp.Text = help;
            _lblOvHelp.Visible = help.Length > 0;
            int w = Math.Max(Px(300), _ovPreview.Width - Px(8));
            if (_lblOvSource.MaximumSize.Width != w) _lblOvSource.MaximumSize = new Size(w, 0);
            if (_lblOvHelp.MaximumSize.Width != w) _lblOvHelp.MaximumSize = new Size(w, 0);
        }

        private void OvMutateSelected(Action<HudItem> mutate)
        {
            TreeNode n = _tvOv.SelectedNode;
            HudItem it = n == null ? null : OvFind(n.Tag as string);
            if (it == null) return;
            mutate(it);
            OvItemsChanged();
        }

        private void OvMove(int delta)
        {
            TreeNode n = _tvOv.SelectedNode;
            HudItem it = n == null ? null : OvFind(n.Tag as string);
            if (it == null) return;
            int at = _ovItems.IndexOf(it), to = at + delta;
            if (at < 0 || to < 0 || to >= _ovItems.Count) return;
            _ovItems.RemoveAt(at);
            _ovItems.Insert(to, it);
            OvItemsChanged();
        }

        private void OvItemsChanged()
        {
            _ovDirty = true;
            _ovSaveTimer.Stop();
            _ovSaveTimer.Start();
            HudFrame f;
            lock (_ovGate) f = _ovFrame;
            OvUpdateNodeTexts(f);
            OvShowEditor();
            _ovPreviewDirty = true;
            if (f != null) OvRenderPreview();
        }

        private void OvShowPresets(Control anchor)
        {
            ContextMenu menu = new ContextMenu();
            foreach (KeyValuePair<string, string> p in HudPresets.All())
            {
                KeyValuePair<string, string> preset = p;
                menu.MenuItems.Add(new MenuItem(preset.Key, delegate { OvApplyPreset(preset.Key, preset.Value); }));
            }
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        // Строки текущего набора → готовый набор. Остальные настройки оверлея не трогаются.
        private void OvApplyPreset(string title, string items)
        {
            if (MessageBox.Show(this, Tr.S("Заменить строки набора ", "Replace the rows of set ") + (_ovScene + 1).ToString(CultureInfo.InvariantCulture)
                                      + Tr.S(" готовым набором «", " with the preset “") + title + Tr.S("»? Положение, размер и вид останутся.", "”? Position, size and look stay."),
                                Tr.S("Оверлей", "Overlay"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _ovItems = HudItem.ParseList(items);
            _ovSuppress = true;
            try
            {
                foreach (KeyValuePair<string, TreeNode> kv in _ovNodes)
                    if (!kv.Key.StartsWith(OvGroupTag, StringComparison.Ordinal)) kv.Value.Checked = OvFind(kv.Key) != null;
                foreach (TreeNode g in _tvOv.Nodes) OvUpdateGroupCheck(g);
            }
            finally { _ovSuppress = false; }
            OvItemsChanged();
        }

        private void OvControlsChanged(Control source)
        {
            if (_ovLoading || _closing || source == _chkOvElevated || source == _cmbOvCoreCount || source == _chkOvCoreLoad || source == _chkOvCoreMhz || source == _cmbOvNvFps) return;
            if (source == _cmbOvScene)
            {
                OvSwitchScene(_cmbOvScene.SelectedIndex);
                return;
            }
            if (source == _chkOvStats || source == _chkOvNoAlarm || source == _txtOvWarn || source == _txtOvCrit
                || source == _cmbOvValueColor || source == _cmbOvLabelColor)
            {
                TreeNode sel = _tvOv.SelectedNode;
                HudItem item = sel == null ? null : OvFind(sel.Tag as string);
                if (item == null) return;
                bool cancelled;
                if (source == _chkOvStats) item.Stats = _chkOvStats.Checked;
                else if (source == _chkOvNoAlarm) item.NoAlarm = _chkOvNoAlarm.Checked;
                else if (source == _txtOvWarn) item.Warn = OvParseThreshold(_txtOvWarn.Text);
                else if (source == _txtOvCrit) item.Crit = OvParseThreshold(_txtOvCrit.Text);
                else if (source == _cmbOvValueColor) { item.Color = OvColorFromCombo(_cmbOvValueColor, item.Color, out cancelled); if (cancelled) { OvShowEditor(); return; } }
                else { item.LabelColor = OvColorFromCombo(_cmbOvLabelColor, item.LabelColor, out cancelled); if (cancelled) { OvShowEditor(); return; } }
                // Поле ввода не перестраивается на каждый символ — иначе курсор прыгает в начало.
                if (source == _txtOvWarn || source == _txtOvCrit)
                {
                    _ovDirty = true;
                    _ovSaveTimer.Stop();
                    _ovSaveTimer.Start();
                    _ovPreviewDirty = true;
                    return;
                }
                OvItemsChanged();
                return;
            }
            if (source == _chkOvText || source == _chkOvGraph || source == _cmbOvInterval)
            {
                TreeNode n = _tvOv.SelectedNode;
                HudItem it = n == null ? null : OvFind(n.Tag as string);
                if (it == null) return;
                it.Text = _chkOvText.Checked;
                it.Graph = _chkOvGraph.Checked;
                if (!it.Text && !it.Graph)
                {
                    // Строка без числа и без графика — пустая: оставшаяся галочка возвращается.
                    it.Text = true;
                    _ovLoading = true;
                    try { _chkOvText.Checked = true; }
                    finally { _ovLoading = false; }
                }
                int ms = OvPick(OvIntervals, _cmbOvInterval, 0);
                it.IntervalMs = ms;
                OvItemsChanged();
                return;
            }
            _ovDirty = true;
            _ovSaveTimer.Stop();
            _ovSaveTimer.Start();
            _ovPreviewDirty = true;
        }
    }
}
