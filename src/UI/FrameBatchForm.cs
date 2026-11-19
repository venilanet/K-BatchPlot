using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Extents2d = Autodesk.AutoCAD.DatabaseServices.Extents2d;

namespace MPlot
{
    /// <summary>
    /// 图框型完整流程的确认/编辑窗口（复刻 M-BatchPlot「批量打印到文件(图框型)」的核心交互）。
    /// 扫描结果进来后：可在列表里改图号/图名/版次/纸张/比例/文件名，可预览、批量改、图号重排、
    /// 纸张统计、导出目录 CSV、重复图号标红；确认才把修改后的 SheetItem 交回打印。
    /// </summary>
    public sealed class FrameBatchForm : Form
    {
        private readonly List<SheetItem> _items;
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _hint;
        private readonly ComboBox _paper = Ui.D(PaperPresets.Names, 170,
            PaperPresets.Names.Contains("A3") ? 3 : 0);
        private readonly TextBox _den = Ui.T("0", 70);   // 0 = 用自动/已配置比例
        private readonly Label _stat = Ui.L("", 780);
        private readonly GlobalConfig _cfg = GlobalConfig.Current;

        public bool Confirmed { get; private set; }
        public List<SheetItem> Items { get { return _items; } }

        // 列索引
        private const int C_NO = 0, C_REMARK = 1, C_DN = 2, C_TITLE = 3, C_REV = 4,
                          C_PAPER = 5, C_DEN = 6, C_FILE = 7, C_DEL = 8;

        public FrameBatchForm(List<SheetItem> items)
        {
            _items = items ?? new List<SheetItem>();
            Text = "批量打印到文件 (图框型) —— 确认图纸清单";
            Font = Ui.Font9;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1120, 700);
            MinimumSize = new Size(900, 560);

            _hint = Ui.L("双击单元格可直接修改图号/图名/版次/纸张/比例/文件名；重复图号会自动标红。\r\n「预览选中」直接回图上看选中图框（可平移/缩放，按回车返回清单），不用再看缩略图。", 900);

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.RowHeadersVisible = true;
            _grid.Font = Ui.Font9;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            AddColumns();
            _grid.CellEndEdit += delegate(object s, DataGridViewCellEventArgs e) { SyncRow(e.RowIndex); };
            _grid.CellClick += delegate(object s, DataGridViewCellEventArgs e) { OnCellClick(e); };
            _grid.RowPostPaint += delegate(object s, DataGridViewRowPostPaintEventArgs e) { PaintRowNo(e); };

            FlowLayoutPanel top = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Top,
                AutoSize = true, Padding = new Padding(10, 8, 10, 4)
            };
            top.Controls.Add(_hint);
            top.Controls.Add(Ui.Row(
                Ui.L("批量设置纸张", 96), _paper,
                Ui.L("比例分母(0=自动)", 120), _den,
                Ui.B("应用纸张", delegate { ApplyPaperToSelected(); }, 96),
                Ui.B("应用比例", delegate { ApplyDenToSelected(); }, 96),
                Ui.B("预览选中", delegate { PreviewSelected(); }, 96),
                Ui.B("图号重排", delegate { Renumber(); }, 96)));
            top.Controls.Add(Ui.Row(
                Ui.B("删除选中", delegate { DeleteSelected(); }, 96),
                Ui.B("上移", delegate { MoveRow(-1); }, 70),
                Ui.B("下移", delegate { MoveRow(1); }, 70),
                Ui.B("纸张统计", delegate { ShowStat(); }, 96),
                Ui.B("导出目录CSV", delegate { ExportCsv(); }, 120),
                Ui.L("提示：选中多行后点【应用纸张】可批量改纸张；纸张列双击可单张改", 300)));

            FlowLayoutPanel bottom = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom,
                Height = 84, Padding = new Padding(10)
            };
            bottom.Controls.Add(Ui.B("确认打印 (" + _items.Count + " 张)", delegate { Confirm(); }, 150));
            bottom.Controls.Add(Ui.B("取消", delegate { Close(); }, 90));
            bottom.Controls.Add(_stat);

            Controls.Add(_grid);
            Controls.Add(top);
            Controls.Add(bottom);
            RefreshGrid();
        }

        private void AddColumns()
        {
            _grid.Columns.Add("cNo", "序号");
            _grid.Columns["cNo"].Width = 46;
            _grid.Columns["cNo"].ReadOnly = true;
            _grid.Columns.Add("cRemark", "备注");
            _grid.Columns["cRemark"].Width = 90;
            _grid.Columns.Add("cDn", "图号 (A)");
            _grid.Columns["cDn"].Width = 150;
            _grid.Columns.Add("cTitle", "图名 (C)");
            _grid.Columns["cTitle"].Width = 220;
            _grid.Columns.Add("cRev", "版次 (B)");
            _grid.Columns["cRev"].Width = 70;
            _grid.Columns.Add("cPaper", "纸张");
            _grid.Columns["cPaper"].Width = 80;
            _grid.Columns.Add("cDen", "比例分母");
            _grid.Columns["cDen"].Width = 80;
            _grid.Columns.Add("cFile", "文件名");
            _grid.Columns["cFile"].Width = 300;
            // 行内删除按钮：每行一个「删」按钮，单击即删除该行，无需先选中整行
            DataGridViewButtonColumn del = new DataGridViewButtonColumn
            {
                Name = "cDel", HeaderText = "删除", Text = "删",
                UseColumnTextForButtonValue = true, Width = 44
            };
            _grid.Columns.Add(del);
            // 图号/图名列允许编辑（参考插件：这些是可编辑列）
            _grid.Columns["cDn"].ReadOnly = false;
            _grid.Columns["cTitle"].ReadOnly = false;
            _grid.Columns["cRev"].ReadOnly = false;
            _grid.Columns["cPaper"].ReadOnly = false;
            _grid.Columns["cDen"].ReadOnly = false;
            _grid.Columns["cFile"].ReadOnly = false;
        }

        private static string PaperName(SheetItem s)
        {
            if (s.Config == null || s.Config.Paper == null) return "";
            return s.Config.Paper.Preset ?? "";
        }

        private static double DenOf(SheetItem s)
        {
            if (s.Config != null && s.Config.ScaleDenominator > 0) return s.Config.ScaleDenominator;
            return 0;
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                SheetItem s = _items[i];
                _grid.Rows.Add(
                    (i + 1).ToString(),
                    s.MetaSource ?? "",
                    s.DrawNo ?? "",
                    s.Title ?? "",
                    s.Rev ?? "",
                    PaperName(s),
                    DenOf(s) > 0 ? DenOf(s).ToString("0.####", CultureInfo.InvariantCulture) : "",
                    s.FileStem ?? "",
                    "删");
            }
            MarkDuplicates();
            UpdateStat();
        }

        /// <summary>图号/文件名重复的以红色显示（参考插件「图号、文件名检错，重复标红」）。</summary>
        private void MarkDuplicates()
        {
            Dictionary<string, int> dnCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> fileCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (SheetItem s in _items)
            {
                string d = (s.DrawNo ?? "").Trim();
                string f = (s.FileStem ?? "").Trim();
                if (d.Length > 0) { if (!dnCount.ContainsKey(d)) dnCount[d] = 0; dnCount[d]++; }
                if (f.Length > 0) { if (!fileCount.ContainsKey(f)) fileCount[f] = 0; fileCount[f]++; }
            }
            for (int i = 0; i < _items.Count && i < _grid.Rows.Count; i++)
            {
                SheetItem s = _items[i];
                DataGridViewRow row = _grid.Rows[i];
                string d = (s.DrawNo ?? "").Trim();
                string f = (s.FileStem ?? "").Trim();
                bool dup = (d.Length > 0 && dnCount.ContainsKey(d) && dnCount[d] > 1) ||
                           (f.Length > 0 && fileCount.ContainsKey(f) && fileCount[f] > 1);
                if (dup)
                {
                    row.Cells[C_DN].Style.ForeColor = Color.Red;
                    row.Cells[C_FILE].Style.ForeColor = Color.Red;
                }
                else
                {
                    row.Cells[C_DN].Style.ForeColor = _grid.DefaultCellStyle.ForeColor;
                    row.Cells[C_FILE].Style.ForeColor = _grid.DefaultCellStyle.ForeColor;
                }
            }
        }

        private void SyncRow(int row)
        {
            if (row < 0 || row >= _items.Count || row >= _grid.Rows.Count) return;
            DataGridViewRow gr = _grid.Rows[row];
            SheetItem s = _items[row];
            s.DrawNo = Cell(gr, C_DN);
            s.Title = Cell(gr, C_TITLE);
            s.Rev = Cell(gr, C_REV);
            s.FileStem = Cell(gr, C_FILE);
            string pn = Cell(gr, C_PAPER);
            if (!string.IsNullOrEmpty(pn))
            {
                PaperSpec ps = PaperPresets.Get(pn);
                if (ps != null)
                {
                    if (s.Config == null) s.Config = new FrameEntry();
                    s.Config.Paper = ps;
                }
            }
            string denRaw = Cell(gr, C_DEN);
            double den;
            if (double.TryParse(denRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out den) && den > 0)
            {
                if (s.Config == null) s.Config = new FrameEntry();
                s.Config.ScaleNumerator = 1;
                s.Config.ScaleDenominator = den;
            }
            else if (s.Config != null)
            {
                s.Config.ScaleNumerator = 0;
                s.Config.ScaleDenominator = 0;
            }
            MarkDuplicates();
        }

        private static string Cell(DataGridViewRow r, int c)
        {
            return r.Cells[c].Value == null ? "" : r.Cells[c].Value.ToString().Trim();
        }

        private void OnCellClick(DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _items.Count) return;
            // 行内删除按钮：单击即删该行（无需先选中）
            if (e.ColumnIndex == C_DEL)
            {
                _items.RemoveAt(e.RowIndex);
                RefreshGrid();
                return;
            }
            // 点「纸张」列给出下拉提示（双击编辑单元格里填名字即可，这里仅提示可用纸张）
            if (e.ColumnIndex != C_PAPER) return;
        }

        private void PaintRowNo(DataGridViewRowPostPaintEventArgs e)
        {
            // 序号列已显示，这里不重复绘制；保留钩子以便将来加行号图标
        }

        private List<int> SelectedIndices()
        {
            List<int> idx = new List<int>();
            foreach (DataGridViewRow r in _grid.SelectedRows)
                if (r.Index >= 0 && r.Index < _items.Count) idx.Add(r.Index);
            idx.Sort();
            return idx;
        }

        private void ApplyPaperToSelected()
        {
            List<int> idx = SelectedIndices();
            if (idx.Count == 0) { Ui.Warn("请先选中要应用的多行（点最左侧行号）"); return; }
            PaperSpec ps = PaperPresets.Get(_paper.Text);
            if (ps == null) return;
            foreach (int i in idx)
            {
                if (_items[i].Config == null) _items[i].Config = new FrameEntry();
                _items[i].Config.Paper = ps;
            }
            RefreshGrid();
        }

        private void ApplyDenToSelected()
        {
            List<int> idx = SelectedIndices();
            if (idx.Count == 0) { Ui.Warn("请先选中要应用的多行（点最左侧行号）"); return; }
            double den;
            if (!double.TryParse(_den.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out den) || den <= 0)
            { Ui.Warn("比例分母请填大于 0 的数字，例如 100"); return; }
            foreach (int i in idx)
            {
                if (_items[i].Config == null) _items[i].Config = new FrameEntry();
                _items[i].Config.ScaleNumerator = 1;
                _items[i].Config.ScaleDenominator = den;
            }
            RefreshGrid();
        }

        private void DeleteSelected()
        {
            List<int> idx = SelectedIndices();
            if (idx.Count == 0) { Ui.Warn("请先选中要删除的行"); return; }
            for (int i = idx.Count - 1; i >= 0; i--) _items.RemoveAt(idx[i]);
            RefreshGrid();
        }

        private void MoveRow(int delta)
        {
            List<int> idx = SelectedIndices();
            if (idx.Count == 0) { Ui.Warn("请先选中一行"); return; }
            int i = idx[0];
            int j = i + delta;
            if (j < 0 || j >= _items.Count) return;
            SheetItem tmp = _items[i]; _items[i] = _items[j]; _items[j] = tmp;
            RefreshGrid();
            if (j >= 0 && j < _grid.Rows.Count) _grid.Rows[j].Selected = true;
        }

        /// <summary>图号重排：按当前顺序给选中行（没选就全部）重新编号为 图号前缀+序号。</summary>
        private void Renumber()
        {
            if (_items.Count == 0) return;
            List<int> idx = SelectedIndices();
            if (idx.Count == 0) for (int i = 0; i < _items.Count; i++) idx.Add(i);
            string prefix = "T-";
            if (DialogResult.OK != InputBox("图号重排", "输入新图号前缀（序号自动从 1 递增）:", ref prefix)) return;
            int n = 1;
            foreach (int i in idx)
            {
                _items[i].DrawNo = prefix + n.ToString("00");
                n++;
            }
            RefreshGrid();
        }

        /// <summary>
        /// 预览选中：不在缩略图里看，直接回 CAD 图上看。
        /// WinForms 模态窗做不到无模式，但本仓库已有现成机制 Ui.StartInteraction
        /// （ManualPickForm「在图上框选范围」就是这么做的）：临时让出 CAD 交互权，
        /// Zoom 到选中图框 → 用户可平移 / 缩放查看 → 回车回到清单。
        /// 窗体本身不关闭、_items 是同一个对象，表格里已改的图号 / 版次 / 纸张 / 比例全部保留；
        /// 为防正在编辑的单元格还没提交，先 EndEdit 再全行 SyncRow。
        /// 旧实现（ThumbnailMaker 生成 PNG 缩略图）已废弃：它依赖 PublishToWeb PNG.pc3，
        /// 机器上没有就直接报「预览生成失败」，而且缩略图本来也看不清图面细节。
        /// </summary>
        private void PreviewSelected()
        {
            List<int> idx = SelectedIndices();
            if (idx.Count == 0) { Ui.Warn("请先选中要预览的行"); return; }
            if (AcadUtil.Ed == null) { Ui.Warn("没有打开的图纸"); return; }

            // 先把正在编辑的单元格提交回 _items，否则返回后那格的修改会丢
            try { _grid.EndEdit(); } catch { }
            for (int i = 0; i < _items.Count; i++) SyncRow(i);

            // 选中行的并集外框，留 5% 边距，别让图框贴着视口边
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (int i in idx)
            {
                Extents2d r = _items[i].WindowWcs;
                x0 = Math.Min(x0, r.MinPoint.X); y0 = Math.Min(y0, r.MinPoint.Y);
                x1 = Math.Max(x1, r.MaxPoint.X); y1 = Math.Max(y1, r.MaxPoint.Y);
            }
            if (x1 <= x0 || y1 <= y0) { Ui.Warn("选中行没有有效的图框范围"); return; }
            double mx = (x1 - x0) * 0.05, my = (y1 - y0) * 0.05;
            Extents2d view = new Extents2d(x0 - mx, y0 - my, x1 + mx, y1 + my);

            using (Ui.StartInteraction(this))
            {
                AcadUtil.ZoomToRect(view);
                AcadUtil.Say("已定位到选中图框（" + idx.Count + " 张），可平移 / 缩放查看；按回车返回清单");
                PromptStringOptions pso = new PromptStringOptions("\n已定位到选中图框，可平移/缩放查看，按回车返回清单: ");
                pso.AllowSpaces = true;
                try { AcadUtil.Ed.GetString(pso); }
                catch (Exception ex) { Logger.Verbose("预览返回失败：" + ex.Message); }
            }
            // 回到清单：数据都在 _items 里，刷新一次并恢复选中行
            RefreshGrid();
            foreach (int i in idx)
                if (i >= 0 && i < _grid.Rows.Count) _grid.Rows[i].Selected = true;
        }

        private void ShowStat()
        {
            Dictionary<string, int> byPaper = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach (SheetItem s in _items)
            {
                string p = PaperName(s);
                if (string.IsNullOrEmpty(p)) p = "(未设)";
                if (!byPaper.ContainsKey(p)) byPaper[p] = 0;
                byPaper[p]++; total++;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("共 ").Append(total).Append(" 张；");
            foreach (KeyValuePair<string, int> kv in byPaper.OrderBy(x => x.Key))
                sb.Append(kv.Key).Append(" ").Append(kv.Value).Append(" 张  ");
            MessageBox.Show(sb.ToString(), "纸张统计", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>导出图纸目录 CSV（UTF-8 BOM，Excel 可直接双击打开）。</summary>
        private void ExportCsv()
        {
            if (_items.Count == 0) { Ui.Warn("列表为空，没有可导出的目录"); return; }
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = "图纸目录_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv"
            };
            if (DialogResult.OK != sfd.ShowDialog(this)) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("\uFEFF");   // UTF-8 BOM，Excel 中文不乱码
                sb.Append("序号,图号,图名,版次,图框大小,比例分母,文件名,来源\r\n");
                for (int i = 0; i < _items.Count; i++)
                {
                    SheetItem s = _items[i];
                    sb.Append(Csv((i + 1).ToString(CultureInfo.InvariantCulture))).Append(',').Append(Csv(s.DrawNo)).Append(',')
                      .Append(Csv(s.Title)).Append(',').Append(Csv(s.Rev)).Append(',')
                      .Append(Csv(PaperName(s))).Append(',')
                      .Append(Csv(DenOf(s) > 0 ? DenOf(s).ToString("0.####", CultureInfo.InvariantCulture) : ""))
                      .Append(',').Append(Csv(s.FileStem)).Append(',')
                      .Append(Csv(s.MetaSource)).Append("\r\n");
                }
                File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true));
                MessageBox.Show("目录已导出：" + sfd.FileName, "导出目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Ui.Warn("导出失败：" + ex.Message); }
        }

        private static string Csv(string s)
        {
            string v = (s ?? "").Replace("\"", "\"\"");
            if (v.IndexOf(',') >= 0 || v.IndexOf('"') >= 0 || v.IndexOf('\n') >= 0)
                v = "\"" + v + "\"";
            return v;
        }

        private void UpdateStat()
        {
            int dup = 0;
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (SheetItem s in _items)
            {
                string d = (s.DrawNo ?? "").Trim();
                if (d.Length == 0) continue;
                if (!seen.ContainsKey(d)) seen[d] = 0;
                seen[d]++;
            }
            foreach (KeyValuePair<string, int> kv in seen) if (kv.Value > 1) dup++;
            _stat.Text = "共 " + _items.Count + " 张" + (dup > 0 ? "；重复图号 " + dup + " 种（已标红）" : "；图号无重复");
        }

        private void Confirm()
        {
            for (int i = 0; i < _items.Count; i++) SyncRow(i);
            if (_items.Count == 0) { Ui.Warn("列表为空，没有可打印的图纸"); return; }
            Confirmed = true;
            Close();
        }

        /// <summary>简易输入窗（WinForms 自带，无外部依赖）。</summary>
        private static DialogResult InputBox(string title, string prompt, ref string value)
        {
            Form f = new Form
            {
                Text = title, Font = Ui.Font9, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ClientSize = new Size(380, 130)
            };
            f.Controls.Add(Ui.L(prompt, 360));
            TextBox tb = new TextBox { Text = value, Width = 340, Location = new Point(20, 34), Font = Ui.Font9 };
            f.Controls.Add(tb);
            FlowLayoutPanel b = new FlowLayoutPanel
            { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(8) };
            b.Controls.Add(Ui.B("确定", delegate { f.DialogResult = DialogResult.OK; f.Close(); }, 80));
            b.Controls.Add(Ui.B("取消", delegate { f.DialogResult = DialogResult.Cancel; f.Close(); }, 80));
            f.Controls.Add(b);
            f.AcceptButton = null;
            DialogResult r = f.ShowDialog();
            if (r == DialogResult.OK) value = tb.Text.Trim();
            using (f) { }
            return r;
        }
    }
}
