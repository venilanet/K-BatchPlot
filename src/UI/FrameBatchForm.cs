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
// 注意：不要整段 using Autodesk.AutoCAD.DatabaseServices——它里面也有 FlowDirection，
// 会和 WinForms 的 FlowDirection 冲突（CS0104；2026-11-19 真机编译已踩过一次）。
// 需要的类型用别名逐个引（Extents2d / ObjectId）。
using ObjectId = Autodesk.AutoCAD.DatabaseServices.ObjectId;

namespace MPlot
{
    /// <summary>
    /// 图框型完整流程的确认/编辑窗口（复刻 M-BatchPlot「批量打印到文件(图框型)」的核心交互）。
    /// 扫描结果进来后：可在列表里改图号/图名/版次/纸张/比例/文件名，可定位、批量改、图号重排、
    /// 纸张统计、导出目录 CSV、重复图号/文件名标红；「点选图框」可在图上连续点选块追加进清单；「确认导出 PDF」前走统一导出前检查
    /// （BatchEngine.PreflightCheck）：非法字符自动替换为 _、重复文件名可一键自动修复、
    /// 目标 PDF 已存在时选覆盖/跳过/另存为，检查未通过只列问题、不导出。
    /// 新增：导出状态列、图纸方向列、完整 PDF 文件名、纸张实际尺寸、筛选、排序。
    /// </summary>
    public sealed class FrameBatchForm: Form
    {
        private readonly List<SheetItem> _items;
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _hint;
        private readonly ComboBox _paper = Ui.D(PaperPresets.Names, 170,
        PaperPresets.Names.Contains("A3")? 3: 0);
        private readonly TextBox _den = Ui.T("0", 70); // 0 = 用自动/已配置比例
        private readonly Label _stat = Ui.L("", 780);
        private readonly GlobalConfig _cfg = GlobalConfig.Current;
        private readonly FrameLibrary _lib;
        private readonly ComboBox _filter;
        private readonly ComboBox _sort;
        private readonly Button _confirmBtn;
        private HashSet<int> _errorRows = new HashSet<int>();

        public bool Confirmed { get; private set;}
        public List<SheetItem> Items { get { return _items;}}

        // 列索引
        private const int C_NO = 0, C_PREVIEW = 1, C_DN = 2, C_TITLE = 3, C_REV = 4,
        C_PAPER = 5, C_DEN = 6, C_FILE = 7, C_STATUS = 8, C_ORIENT = 9, C_DEL = 10;

        public FrameBatchForm(List<SheetItem> items, FrameLibrary lib)
        {
            _items = items?? new List<SheetItem>();
            _lib = lib ?? new FrameLibrary();
            Text = "批量导出 PDF (图框型) —— 确认图纸清单";
            Font = Ui.Font9;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1120, 700);
            MinimumSize = new Size(900, 560);

            _hint = Ui.L("纸张列点下拉可单张改（A0/A1/A2/A3/A4/自定义）；比例列显示导出时真正用的比例（自动=实测÷纸张，改纸张即变；双击可填分母，清空=自动；1:200 以上标橙色，打印后文字可能偏小）；点每行的「预览」可看这张的打印效果；重复图号、重复文件名、空文件名会标红。\r\n「定位选中图纸」直接回图上看选中图框（可平移/缩放，按回车返回清单）；「点选图框」可在图上连续点选图框块追加进清单（回车结束，Esc 取消）；点「确认导出 PDF」前会自动检查文件名、图框、纸张与输出目录。", 900);

            _filter = Ui.D(new[] { "全部图纸", "重复图号", "重复文件名", "文件名为空", "异常图纸"}, 130, 0);
            _filter.SelectedIndexChanged += delegate { ApplyFilter();};
            _sort = Ui.D(new[] { "默认顺序", "按图号排序", "按图名排序", "按纸张排序"}, 130, 0);
            _sort.SelectedIndexChanged += delegate { SortList();};

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.RowHeadersVisible = true;
            _grid.Font = Ui.Font9;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            AddColumns();
            _grid.CellEndEdit += delegate(object s, DataGridViewCellEventArgs e) { SyncRow(e.RowIndex);};
            _grid.CellClick += delegate(object s, DataGridViewCellEventArgs e) { OnCellClick(e);};
            // 纸张下拉框选完立即提交（否则要等单元格失焦才触发 CellValueChanged）
            _grid.CurrentCellDirtyStateChanged += delegate(object s, EventArgs e)
            {
                if (_grid.CurrentCell != null && _grid.CurrentCell.ColumnIndex == C_PAPER && _grid.IsCurrentCellDirty)
                    try { _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); } catch { }
            };
            _grid.CellValueChanged += delegate(object s, DataGridViewCellEventArgs e) { OnCellValueChanged(e);};
            // 纸张下拉框：格值不在下拉列表里时不弹报错框（没有纸张的行格值是空的）
            _grid.DataError += delegate(object s, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            _grid.RowPostPaint += delegate(object s, DataGridViewRowPostPaintEventArgs e) { PaintRowNo(e);};

            FlowLayoutPanel top = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Top,
                AutoSize = true, Padding = new Padding(10, 8, 10, 4)
            };
            top.Controls.Add(_hint);
            top.Controls.Add(Ui.Row(
            Ui.L("筛选", 40), _filter,
            Ui.L("排序", 40), _sort,
            Ui.L("「异常图纸」= 导出前检查会报错的行（文件名/窗口/尺寸/纸张/比例问题）", 420)));
            top.Controls.Add(Ui.Row(
            Ui.L("批量设置纸张", 96), _paper,
            Ui.L("比例分母(0=自动)", 120), _den,
            Ui.B("应用纸张", delegate { ApplyPaperToSelected();}, 96),
            Ui.B("应用比例", delegate { ApplyDenToSelected();}, 96),
            Ui.B("应用命名规则", delegate { ApplyNamingToSelected();}, 110),
            Ui.B("定位选中图纸", delegate { LocateSelected();}, 110),
            Ui.B("点选图框", delegate { PickBlocks();}, 96),
            Ui.B("图号重排", delegate { Renumber();}, 96)));
            top.Controls.Add(Ui.Row(
            Ui.B("删除选中", delegate { DeleteSelected();}, 96),
            Ui.B("清空清单", delegate { ClearList();}, 96),
            Ui.B("上移", delegate { MoveRow(-1);}, 70),
            Ui.B("下移", delegate { MoveRow(1);}, 70),
            Ui.B("纸张统计", delegate { ShowStat();}, 96),
            Ui.B("导出目录CSV", delegate { ExportCsv();}, 120),
            Ui.L("提示：选中多行后点可批量改；没选中时改全部可见行；纸张列点下拉可单张改；选中多行点上移/下移可整批移动。", 300)));

            _confirmBtn = Ui.B("确认导出 PDF (" + _items.Count + " 张)", delegate { Confirm();}, 190);
            FlowLayoutPanel bottom = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom,
                Height = 84, Padding = new Padding(10)
            };
            bottom.Controls.Add(_confirmBtn);
            bottom.Controls.Add(Ui.B("取消", delegate { Close();}, 90));
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
            // 行内预览按钮：每行一个「预览」，点一下按当前这行的纸张/比例渲染打印效果看
            // （备注列已删：来源信息用不上，纸张/比例/预览才是核对要用的）
            DataGridViewButtonColumn prev = new DataGridViewButtonColumn
            {
                Name = "cPreview", HeaderText = "预览", Text = "预览",
                UseColumnTextForButtonValue = true, Width = 54
            };
            _grid.Columns.Add(prev);
            _grid.Columns.Add("cDn", "图号 (A)");
            _grid.Columns["cDn"].Width = 150;
            _grid.Columns.Add("cTitle", "图名 (C)");
            _grid.Columns["cTitle"].Width = 220;
            _grid.Columns.Add("cRev", "版次 (B)");
            _grid.Columns["cRev"].Width = 70;
            // 纸张列改下拉框：每行点一下就能改纸张（A0/A1/A2/A3/A4/加长/自定义），不用再双击打字
            DataGridViewComboBoxColumn paperCol = new DataGridViewComboBoxColumn
            {
                Name = "cPaper", HeaderText = "纸张", Width = 110,
                FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
            };
            foreach (string n in PaperPresets.Names) paperCol.Items.Add(n);
            _grid.Columns.Add(paperCol);
            _grid.Columns.Add("cDen", "比例");
            _grid.Columns["cDen"].Width = 80;
            _grid.Columns.Add("cFile", "文件名");
            _grid.Columns["cFile"].Width = 300;
            _grid.Columns.Add("cStatus", "导出状态");
            _grid.Columns["cStatus"].Width = 76;
            _grid.Columns["cStatus"].ReadOnly = true;
            _grid.Columns.Add("cOrient", "方向");
            _grid.Columns["cOrient"].Width = 54;
            _grid.Columns["cOrient"].ReadOnly = true;
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

        /// <summary>纸张列显示实际尺寸：预设只显示名称，自定义显示「自定义 宽×高mm」。</summary>
        private static string PaperDisplay(SheetItem s)
        {
            if (s.Config == null || s.Config.Paper == null) return "";
            PaperSpec p = s.Config.Paper;
            if (p.IsCustom)
                return "自定义 " + p.WidthMm.ToString("0.#", CultureInfo.InvariantCulture)
            + "×" + p.HeightMm.ToString("0.#", CultureInfo.InvariantCulture) + "mm";
            return p.Preset?? "";
        }

        /// <summary>图纸方向：图框实测宽 ≥ 高为横向，否则纵向；通用布局图无实测尺寸时按纸张（已旋转则换向）。</summary>
        private static string OrientText(SheetItem s)
        {
            double w = s.WidthMm, h = s.HeightMm;
            if (w <= 0 || h <= 0)
            {
                if (s.Config == null || s.Config.Paper == null) return "";
                w = s.Config.Paper.WidthMm; h = s.Config.Paper.HeightMm;
                if (s.Rotated) { double t = w; w = h; h = t;}
            }
            if (w <= 0 || h <= 0) return "";
            return w >= h? "横向": "纵向";
        }

        /// <summary>纸张下拉框的格值：只给预设名（必须在下拉列表里）；没有纸张时给 null（空串会触发下拉框报错）。</summary>
        private static string PaperPresetName(SheetItem s)
        {
            if (s.Config == null || s.Config.Paper == null) return null;
            return s.Config.Paper.Preset;
        }

        /// <summary>标称有效比例分母（不含留白）：手动分母优先，其次全局自定义，再按实测÷纸张算；
        /// ScaleToFit/OneToOne 不用数字比例，返回 0。</summary>
        private double EffectiveDen(SheetItem s)
        {
            if (s.Config != null && s.Config.ScaleDenominator > 0)
                return s.Config.ScaleDenominator;
            if (_cfg == null) return 0;
            if (_cfg.ScaleMode == ScaleMode.Custom)
                return _cfg.CustomScaleDenominator > 0 ? _cfg.CustomScaleDenominator : 0;
            if (_cfg.ScaleMode == ScaleMode.AutoFromFrame)
            {
                if (s.Config == null || s.Config.Paper == null) return 0;
                try { return PaperMatcher.ComputeScaleDenominator(s.WidthMm, s.HeightMm, s.Config, s.Rotated); }
                catch { return 0; }
            }
            return 0;
        }

        /// <summary>比例列显示：导出时真正用的标称比例。自动=实测尺寸÷纸张（改纸张即变）；
        /// 手动填过分母的加 *（不随纸张变）；ScaleToFit 显示「适应」，1:1 显示「1:1」。
        /// 注意：显示的是标称比例（不含 2mm 留白），导出时实际比例会再大一点点（1:100→约1:100.5），
        /// 差不到 1%，不影响看字大小的判断；预览图是带留白的真实导出效果。</summary>
        private string ScaleText(SheetItem s)
        {
            if (s.Config != null && s.Config.ScaleDenominator > 0)
                return "1:" + s.Config.ScaleDenominator.ToString("0.####", CultureInfo.InvariantCulture) + "*";
            double den = EffectiveDen(s);
            if (den <= 0)
            {
                ScaleMode mode = _cfg == null ? ScaleMode.AutoFromFrame : _cfg.ScaleMode;
                if (mode == ScaleMode.ScaleToFit) return "适应";
                if (mode == ScaleMode.OneToOne) return "1:1";
                return "";
            }
            return "1:" + den.ToString("0.####", CultureInfo.InvariantCulture);
        }

        /// <summary>比例格悬停说明：自动还是手动，超 1:200 提醒字可能偏小。</summary>
        private string ScaleTip(SheetItem s)
        {
            if (s.Config != null && s.Config.ScaleDenominator > 0)
                return "手动比例 1:" + s.Config.ScaleDenominator.ToString("0.####", CultureInfo.InvariantCulture) + "，不随纸张变化（双击清空=自动）";
            double den = EffectiveDen(s);
            if (den <= 0) return "";
            string tip = "自动比例 1:" + den.ToString("0.####", CultureInfo.InvariantCulture) + "（实测÷纸张，改纸张即变）";
            if (den >= 200) tip += "；超过 1:200，打印后文字可能偏小，请核对";
            return tip;
        }

        /// <summary>解析比例格的输入：接受「1:100」「1:100*」「100」，清空=自动。</summary>
        private static bool ParseDenCell(string text, out double den)
        {
            den = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.StartsWith("1:", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            if (t.EndsWith("*")) t = t.Substring(0, t.Length - 1);
            t = t.Trim();
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out den) && den > 0;
        }

        private static double PaperArea(SheetItem s)
        {
            if (s.Config == null || s.Config.Paper == null) return 0;
            return s.Config.Paper.WidthMm * s.Config.Paper.HeightMm;
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                SheetItem s = _items[i];
                int ri = _grid.Rows.Add(
                (i + 1).ToString(),
                "预览",
                s.DrawNo?? "",
                s.Title?? "",
                s.Rev?? "",
                PaperPresetName(s),
                ScaleText(s),
                string.IsNullOrEmpty(s.FileStem)? "": Naming.FullFileName(s),
                SheetStatusNames.Text(s.Status),
                OrientText(s),
                "删");
                DataGridViewRow row = _grid.Rows[ri];
                // 纸张格悬停显示完整尺寸（下拉只显示预设名，自定义的尺寸放提示里）
                row.Cells[C_PAPER].ToolTipText = PaperDisplay(s);
                // 比例格悬停说明自动/手动；1:200 以上标橙色，提醒打印后文字可能偏小
                row.Cells[C_DEN].ToolTipText = ScaleTip(s);
                if (EffectiveDen(s) >= 200) row.Cells[C_DEN].Style.ForeColor = Color.DarkOrange;
                DataGridViewCell sc = row.Cells[C_STATUS];
                if (s.Status == SheetStatus.Success) sc.Style.ForeColor = Color.Green;
                else if (s.Status == SheetStatus.Failed) sc.Style.ForeColor = Color.Red;
                else if (s.Status == SheetStatus.Skipped) sc.Style.ForeColor = Color.Gray;
            }
            ComputeErrorRows();
            MarkDuplicates();
            ApplyFilter();
            UpdateStat();
            UpdateConfirmButton();
        }

        /// <summary>算出导出前检查会报错的行号集合（不含输出目录检查：只看单张图纸自身问题）。</summary>
        private void ComputeErrorRows()
        {
            _errorRows = new HashSet<int>();
            try
            {
                PreflightResult chk = BatchEngine.PreflightCheck(_items, "", _cfg);
                foreach (PreflightIssue iss in chk.Issues)
                    if (iss.Severity == PreflightSeverity.Error && iss.RowIndex >= 0)
                        _errorRows.Add(iss.RowIndex);
            }
            catch {}
        }

        /// <summary>重复图号集合与重复文件名集合（忽略大小写）。</summary>
        private void DupSets(out HashSet<string> dnDup, out HashSet<string> fileDup)
        {
            Dictionary<string, int> dn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> fl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (SheetItem s in _items)
            {
                string d = (s.DrawNo?? "").Trim();
                string f = (s.FileStem?? "").Trim();
                if (d.Length > 0) { if (!dn.ContainsKey(d)) dn[d] = 0; dn[d]++;}
                if (f.Length > 0) { if (!fl.ContainsKey(f)) fl[f] = 0; fl[f]++;}
            }
            dnDup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            fileDup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> kv in dn) if (kv.Value > 1) dnDup.Add(kv.Key);
            foreach (KeyValuePair<string, int> kv in fl) if (kv.Value > 1) fileDup.Add(kv.Key);
        }

        /// <summary>图号/文件名重复的以红色显示（参考插件「图号、文件名检错，重复标红」）；空文件名也标红。</summary>
        private void MarkDuplicates()
        {
            HashSet<string> dnDup, fileDup;
            DupSets(out dnDup, out fileDup);
            Color def = _grid.DefaultCellStyle.ForeColor;
            for (int i = 0; i < _items.Count && i < _grid.Rows.Count; i++)
            {
                SheetItem s = _items[i];
                DataGridViewRow row = _grid.Rows[i];
                string d = (s.DrawNo?? "").Trim();
                string f = (s.FileStem?? "").Trim();
                row.Cells[C_DN].Style.ForeColor =
                (d.Length > 0 && dnDup.Contains(d))? Color.Red: def;
                row.Cells[C_FILE].Style.ForeColor =
                (f.Length == 0 || (f.Length > 0 && fileDup.Contains(f)))? Color.Red: def;
            }
        }

        /// <summary>按筛选下拉框隐藏不匹配的行（行号与 _items 下标保持一一对应）。</summary>
        private void ApplyFilter()
        {
            if (_filter == null) return;
            HashSet<string> dnDup, fileDup;
            DupSets(out dnDup, out fileDup);
            int mode = _filter.SelectedIndex; // 0 全部 1 重复图号 2 重复文件名 3 文件名为空 4 异常图纸
            for (int i = 0; i < _items.Count && i < _grid.Rows.Count; i++)
            {
                SheetItem s = _items[i];
                string d = (s.DrawNo?? "").Trim();
                string f = (s.FileStem?? "").Trim();
                bool show = true;
                if (mode == 1) show = d.Length > 0 && dnDup.Contains(d);
                else if (mode == 2) show = f.Length > 0 && fileDup.Contains(f);
                else if (mode == 3) show = f.Length == 0;
                else if (mode == 4) show = _errorRows.Contains(i);
                _grid.Rows[i].Visible = show;
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
            // 文件名列显示完整 PDF 文件名（xxx.pdf），存回时去掉.pdf 后缀
            string fileRaw = Cell(gr, C_FILE);
            if (fileRaw.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                fileRaw = fileRaw.Substring(0, fileRaw.Length - 4);
            s.FileStem = fileRaw;
            // 纸张列是下拉框，只给预设名；同一预设不动（自定义的实测尺寸得以保留），
            // 下拉选「自定义」（不带尺寸）时用本行实测尺寸，不重置成 841×594
            string pn = Cell(gr, C_PAPER);
            if (!string.IsNullOrEmpty(pn))
            {
                string name = pn.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                PaperSpec cur = s.Config == null ? null : s.Config.Paper;
                if (cur != null && string.Equals(cur.Preset, name, StringComparison.OrdinalIgnoreCase))
                {
                    // 同一预设，不动
                }
                else if (string.Equals(name, "自定义", StringComparison.OrdinalIgnoreCase)
                    && pn.Trim() == "自定义")
                {
                    if (s.Config == null) s.Config = new FrameEntry();
                    s.Config.Paper = new PaperSpec
                    {
                        Preset = "自定义",
                        WidthMm = s.WidthMm,
                        HeightMm = s.HeightMm,
                        IsCustom = true
                    };
                }
                else
                {
                    PaperSpec ps;
                    if (PaperPresets.TryParseDisplay(pn, out ps))
                    {
                        if (s.Config == null) s.Config = new FrameEntry();
                        s.Config.Paper = ps;
                    }
                }
            }
            // 比例格接受「1:100」「1:100*」「100」，清空=自动
            string denRaw = Cell(gr, C_DEN);
            double den;
            if (ParseDenCell(denRaw, out den))
            {
                if (s.Config == null) s.Config = new FrameEntry();
                s.Config.ScaleNumerator = 1;
                s.Config.ScaleDenominator = den;
            }
            else if (s.Config!= null)
            {
                s.Config.ScaleNumerator = 0;
                s.Config.ScaleDenominator = 0;
            }
            // 实时刷新：重复标红、底部统计、确认按钮状态（不重新应用筛选，避免正在改的行突然被隐藏）
            MarkDuplicates();
            UpdateStat();
            UpdateConfirmButton();
        }

        private static string Cell(DataGridViewRow r, int c)
        {
            return r.Cells[c].Value == null? "": r.Cells[c].Value.ToString().Trim();
        }

        private void OnCellClick(DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _items.Count) return;
            // 行内预览按钮：点一下按当前这行的纸张/比例渲染打印效果看
            if (e.ColumnIndex == C_PREVIEW)
            {
                PreviewRow(e.RowIndex);
                return;
            }
            // 行内删除按钮：单击即删该行（无需先选中）
            if (e.ColumnIndex == C_DEL)
            {
                _items.RemoveAt(e.RowIndex);
                RefreshGrid();
                return;
            }
        }

        /// <summary>纸张下拉框改了值：写回该行并重算文件名，比例列跟着纸张变；刷新推迟到当前
        /// 单元格事件结束后再做，避免在提交中途清行。</summary>
        private void OnCellValueChanged(DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _items.Count) return;
            if (e.ColumnIndex != C_PAPER) return;
            SyncRow(e.RowIndex);
            SheetItem s = _items[e.RowIndex];
            s.FileStem = Naming.BuildFileName(s, _cfg, e.RowIndex + 1);
            int row = e.RowIndex;
            BeginInvoke(new Action(delegate
            {
                if (IsDisposed || !IsHandleCreated) return;
                RefreshGrid();
                if (row >= 0 && row < _grid.Rows.Count) _grid.Rows[row].Selected = true;
            }));
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

        private List<int> VisibleIndices()
        {
            List<int> v = new List<int>();
            for (int i = 0; i < _grid.Rows.Count; i++)
                if (_grid.Rows[i].Visible) v.Add(i);
            return v;
        }

        /// <summary>批量操作的目标行：选中了就只改选中行，没选中就改全部可见行（筛选后只改筛出来的行）。</summary>
        private List<int> TargetIndices()
        {
            List<int> idx = SelectedIndices();
            if (idx.Count > 0) return idx;
            return VisibleIndices();
        }

        private List<SheetItem> SelectedItems()
        {
            List<SheetItem> sel = new List<SheetItem>();
            foreach (int i in SelectedIndices()) sel.Add(_items[i]);
            return sel;
        }

        private void ApplyPaperToSelected()
        {
            List<int> idx = TargetIndices();
            if (idx.Count == 0) { Ui.Warn("清单为空"); return;}
            string pt = (_paper.Text ?? "").Trim();
            PaperSpec ps = PaperPresets.Get(pt);
            if (ps == null) return;
            foreach (int i in idx)
            {
                if (_items[i].Config == null) _items[i].Config = new FrameEntry();
                // 选「自定义」时保留本行实测尺寸，不要重置成 841×594
                if (pt == "自定义")
                    _items[i].Config.Paper = new PaperSpec
                    {
                        Preset = "自定义",
                        WidthMm = _items[i].WidthMm,
                        HeightMm = _items[i].HeightMm,
                        IsCustom = true
                    };
                else
                    _items[i].Config.Paper = ps;
                // 文件名里的「图框大小」取自纸张：改纸张必须同步重算文件名，
                // 否则纸张列变了、文件名还留着旧的 _自定义（2026-11-19 真机反馈）。
                _items[i].FileStem = Naming.BuildFileName(_items[i], _cfg, i + 1);
            }
            RefreshGrid();
        }

        private void ApplyDenToSelected()
        {
            List<int> idx = TargetIndices();
            if (idx.Count == 0) { Ui.Warn("清单为空"); return;}
            double den;
            if (!double.TryParse(_den.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out den) || den <= 0)
            { Ui.Warn("比例分母请填大于 0 的数字，例如 100"); return;}
            foreach (int i in idx)
            {
                if (_items[i].Config == null) _items[i].Config = new FrameEntry();
                _items[i].Config.ScaleNumerator = 1;
                _items[i].Config.ScaleDenominator = den;
            }
            RefreshGrid();
        }

        /// <summary>应用命名规则：按当前命名模板重新生成选中行（没选中则全部可见行）的文件名。</summary>
        private void ApplyNamingToSelected()
        {
            List<int> idx = TargetIndices();
            if (idx.Count == 0) { Ui.Warn("清单为空"); return;}
            foreach (int i in idx)
                _items[i].FileStem = Naming.BuildFileName(_items[i], _cfg, i + 1);
            RefreshGrid();
        }

        private void DeleteSelected()
        {
            List<int> idx = SelectedIndices();
            if (idx.Count == 0) { Ui.Warn("请先选中要删除的行"); return;}
            for (int i = idx.Count - 1; i >= 0; i--) _items.RemoveAt(idx[i]);
            RefreshGrid();
        }

        /// <summary>上移/下移：选中的多行一起移动一格。做法：按显示顺序扫一遍，选中的行只有当
        /// 相邻（上/下）一行没被选中时才跟它交换——这样连续选中的一块整体平移、相对顺序不变；
        /// 已到顶/到底的行不动，其余选中的行照常移动。移动后恢复全部选中。</summary>
        private void MoveRow(int delta)
        {
            List<SheetItem> sel = SelectedItems();
            if (sel.Count == 0) { Ui.Warn("请先选中要移动的行"); return;}
            HashSet<SheetItem> selSet = new HashSet<SheetItem>(sel);
            List<int> visible = VisibleIndices();
            if (delta < 0)
            {
                // 上移：从上往下扫；选中的行只有当上面一行没被选中时才上移一格
                for (int p = 1; p < visible.Count; p++)
                {
                    int cur = visible[p], above = visible[p - 1];
                    if (selSet.Contains(_items[cur]) && !selSet.Contains(_items[above]))
                    {
                        SheetItem tmp = _items[cur]; _items[cur] = _items[above]; _items[above] = tmp;
                        visible[p] = above; visible[p - 1] = cur;
                    }
                }
            }
            else
            {
                // 下移：从下往上扫；选中的行只有当下面一行没被选中时才下移一格
                for (int p = visible.Count - 2; p >= 0; p--)
                {
                    int cur = visible[p], below = visible[p + 1];
                    if (selSet.Contains(_items[cur]) && !selSet.Contains(_items[below]))
                    {
                        SheetItem tmp = _items[cur]; _items[cur] = _items[below]; _items[below] = tmp;
                        visible[p] = below; visible[p + 1] = cur;
                    }
                }
            }
            RefreshGrid();
            foreach (SheetItem s in sel)
            {
                int i = _items.IndexOf(s);
                if (i >= 0 && i < _grid.Rows.Count) _grid.Rows[i].Selected = true;
            }
        }

        /// <summary>排序：默认顺序 = 扫描顺序（Seq）；图号/图名用自然排序；纸张按面积再按名称。排序后恢复选中。</summary>
        private void SortList()
        {
            if (_items.Count <= 1) return;
            List<SheetItem> sel = SelectedItems();
            int mode = _sort.SelectedIndex;
            if (mode == 1)
            {
                List<SheetItem> sorted = FrameScanner.NaturalSort(_items, x => x.DrawNo?? "");
                _items.Clear(); _items.AddRange(sorted);
            }
            else if (mode == 2)
            {
                List<SheetItem> sorted = FrameScanner.NaturalSort(_items, x => x.Title?? "");
                _items.Clear(); _items.AddRange(sorted);
            }
            else if (mode == 3)
            {
                _items.Sort(delegate(SheetItem a, SheetItem b)
                {
                    int c = PaperArea(a).CompareTo(PaperArea(b));
                    if (c!= 0) return c;
                    return string.Compare(PaperDisplay(a), PaperDisplay(b), StringComparison.OrdinalIgnoreCase);
                });
            }
            else
            {
                _items.Sort(delegate(SheetItem a, SheetItem b) { return a.Seq.CompareTo(b.Seq);});
            }
            RefreshGrid();
            foreach (SheetItem s in sel)
            {
                int idx = _items.IndexOf(s);
                if (idx >= 0 && idx < _grid.Rows.Count) _grid.Rows[idx].Selected = true;
            }
        }

        /// <summary>图号重排：按当前顺序给目标行（选中行，没选就全部可见行）重新编号为 图号前缀+序号。</summary>
        private void Renumber()
        {
            if (_items.Count == 0) return;
            List<int> idx = TargetIndices();
            string prefix = "T-";
            if (DialogResult.OK!= InputBox("图号重排", "输入新图号前缀（序号自动从 1 递增）:", ref prefix)) return;
            int n = 1;
            foreach (int i in idx)
            {
                _items[i].DrawNo = prefix + n.ToString("00");
                n++;
            }
            RefreshGrid();
        }

        /// <summary>预览单张：点某行的「预览」，按当前这行的纸张/比例设置，用与导出完全相同的
        /// 请求渲染一张打印预览图，大图里看（可缩放），方便逐张核对打印效果——字会不会太小、
        /// 内容有没有被裁、边框有没有缺。渲染失败直接报原因，不静默。</summary>
        private void PreviewRow(int row)
        {
            if (row < 0 || row >= _items.Count) return;
            // 先把正在编辑的单元格提交回 _items，否则预览用的是旧纸张/旧比例
            try { _grid.EndEdit(); } catch { }
            for (int i = 0; i < _items.Count; i++) SyncRow(i);
            SheetItem s = _items[row];
            string err = "";
            string png = "";
            try
            {
                Cursor = Cursors.WaitCursor;
                PlotRequest req = BatchEngine.BuildSinglePreviewRequest(s, _cfg);
                if (req == null) err = "打印请求为空";
                else png = Plotter.RenderPreview(req, out err);
            }
            catch (Exception ex) { err = ex.Message; Logger.Verbose("单张预览失败：" + ex.Message); }
            finally { Cursor = Cursors.Default; }
            if (string.IsNullOrEmpty(png)) { Ui.Warn("预览生成失败：" + err); return; }
            string caption = (s.DrawNo ?? "") + " " + (s.Title ?? "") + " · " + PaperDisplay(s) + " · " + ScaleText(s);
            using (SingleSheetPreviewForm f = new SingleSheetPreviewForm(png, caption))
            {
                f.ShowDialog(this);
            }
            RefreshGrid();
        }

        /// <summary>
        /// 定位选中图纸：不在缩略图里看，直接回 CAD 图上看。
        /// WinForms 模态窗做不到无模式，但本仓库已有现成机制 Ui.StartInteraction
        /// （ManualPickForm「在图上框选范围」就是这么做的）：临时让出 CAD 交互权，
        /// Zoom 到选中图框 → 用户可平移 / 缩放查看 → 回车回到清单。
        /// 窗体本身不关闭、_items 是同一个对象，表格里已改的图号 / 版次 / 纸张 / 比例全部保留；
        /// 为防正在编辑的单元格还没提交，先 EndEdit 再全行 SyncRow。
        /// 旧实现（ThumbnailMaker 生成 PNG 缩略图）已废弃：它依赖 PublishToWeb PNG.pc3，
        /// 机器上没有就直接报「预览生成失败」，而且缩略图本来也看不清图面细节。
        /// </summary>
        private void LocateSelected()
        {
            List<int> idx = SelectedIndices();
            if (idx.Count == 0) { Ui.Warn("请先选中要定位的行"); return;}
            if (AcadUtil.Ed == null) { Ui.Warn("没有打开的图纸"); return;}

            // 先把正在编辑的单元格提交回 _items，否则返回后那格的修改会丢
            try { _grid.EndEdit();} catch {}
            for (int i = 0; i < _items.Count; i++) SyncRow(i);

            // 选中行的并集外框，留 5% 边距，别让图框贴着视口边
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (int i in idx)
            {
                Extents2d r = _items[i].WindowWcs;
                x0 = Math.Min(x0, r.MinPoint.X); y0 = Math.Min(y0, r.MinPoint.Y);
                x1 = Math.Max(x1, r.MaxPoint.X); y1 = Math.Max(y1, r.MaxPoint.Y);
            }
            if (x1 <= x0 || y1 <= y0) { Ui.Warn("选中行没有有效的图框范围"); return;}
            double mx = (x1 - x0) * 0.05, my = (y1 - y0) * 0.05;
            Extents2d view = new Extents2d(x0 - mx, y0 - my, x1 + mx, y1 + my);

            using (Ui.StartInteraction(this))
            {
                AcadUtil.ZoomToRect(view);
                AcadUtil.Say("已定位到选中图框（" + idx.Count + " 张），可平移 / 缩放查看；按回车返回清单");
                PromptStringOptions pso = new PromptStringOptions("\n已定位到选中图框，可平移/缩放查看，按回车返回清单: ");
                pso.AllowSpaces = true;
                try { AcadUtil.Ed.GetString(pso);}
                catch (Exception ex) { Logger.Verbose("定位返回失败：" + ex.Message);}
            }
            // 回到清单：数据都在 _items 里，刷新一次并恢复选中行
            RefreshGrid();
            foreach (int i in idx)
                if (i >= 0 && i < _grid.Rows.Count) _grid.Rows[i].Selected = true;
        }

        /// <summary>
        /// 点选图框：先把正在编辑的单元格提交回 _items，再临时让出 CAD 交互权，
        /// 在图上连续点选图框块（回车结束，Esc 取消），解析后追加进清单。
        /// 追加不覆盖：已在清单里的块不重复加入，用户已改的图号/纸张等原样保留。
        /// Esc 取消时清单原样不动；挑选中不碰数据库，解析完事务即关。
        /// </summary>
        private void PickBlocks()
        {
            if (AcadUtil.Ed == null) { Ui.Warn("没有打开的图纸"); return;}
            try { _grid.EndEdit();} catch { }
            for (int i = 0; i < _items.Count; i++) SyncRow(i);
            HashSet<ObjectId> existing = new HashSet<ObjectId>();
            int maxSeq = 0;
            foreach (SheetItem s in _items)
            {
                if (!s.BlockId.IsNull) existing.Add(s.BlockId);
                if (s.Seq > maxSeq) maxSeq = s.Seq;
            }
            BlockPicker.PickResult pr = null;
            using (Ui.StartInteraction(this))
            {
                pr = BlockPicker.PickAndBuildSheets(_lib, _cfg, existing, maxSeq + 1);
            }
            if (pr == null) { Ui.Warn("没有打开的图纸"); return;}
            if (pr.Canceled) return;   // Esc：清单原样不动
            foreach (SheetItem s in pr.Added) _items.Add(s);
            RefreshGrid();
            if (pr.Notes.Count > 0)
                MessageBox.Show(string.Join("\r\n", pr.Notes.ToArray()), "点选图框", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>清空清单：只清窗口里的清单，不改动图纸本身；先确认（清单里的修改会一起丢弃）。</summary>
        private void ClearList()
        {
            if (_items.Count == 0) return;
            DialogResult r = MessageBox.Show("确定要清空清单吗？共 " + _items.Count + " 张，清单里的修改也会一起丢弃（不会改动图纸本身）。",
            "清空清单", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            _items.Clear();
            RefreshGrid();
        }

        private void ShowStat()
        {
            Dictionary<string, int> byPaper = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach (SheetItem s in _items)
            {
                string p = PaperDisplay(s);
                if (string.IsNullOrEmpty(p)) p = "(未设)";
                if (!byPaper.ContainsKey(p)) byPaper[p] = 0;
                byPaper[p]++; total++;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("共 ").Append(total).Append(" 张；");
            foreach (KeyValuePair<string, int> kv in byPaper.OrderBy(x => x.Key))
                sb.Append(kv.Key).Append(" ").Append(kv.Value).Append(" 张 ");
            MessageBox.Show(sb.ToString(), "纸张统计", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>导出图纸目录 CSV（UTF-8 BOM，Excel 可直接双击打开）。</summary>
        private void ExportCsv()
        {
            if (_items.Count == 0) { Ui.Warn("列表为空，没有可导出的目录"); return;}
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = "图纸目录_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv"
            };
            if (DialogResult.OK!= sfd.ShowDialog(this)) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("\uFEFF"); // UTF-8 BOM，Excel 中文不乱码
                sb.Append("序号,图号,图名,版次,图框大小,比例,文件名,来源\r\n");
                for (int i = 0; i < _items.Count; i++)
                {
                    SheetItem s = _items[i];
                    sb.Append(Csv((i + 1).ToString(CultureInfo.InvariantCulture))).Append(',').Append(Csv(s.DrawNo)).Append(',')
                    .Append(Csv(s.Title)).Append(',').Append(Csv(s.Rev)).Append(',')
                    .Append(Csv(PaperDisplay(s))).Append(',')
                    .Append(Csv(ScaleText(s)))
                    .Append(',').Append(Csv(string.IsNullOrEmpty(s.FileStem)? "": Naming.FullFileName(s))).Append(',')
                    .Append(Csv(s.MetaSource)).Append("\r\n");
                }
                File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true));
                MessageBox.Show("目录已导出：" + sfd.FileName, "导出目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Ui.Warn("导出失败：" + ex.Message);}
        }

        private static string Csv(string s)
        {
            string v = (s?? "").Replace("\"", "\"\"");
            if (v.IndexOf(',') >= 0 || v.IndexOf('"') >= 0 || v.IndexOf('\n') >= 0)
                v = "\"" + v + "\"";
            return v;
        }

        private void UpdateStat()
        {
            HashSet<string> dnDup, fileDup;
            DupSets(out dnDup, out fileDup);
            int emptyFile = 0;
            foreach (SheetItem s in _items)
                if (string.IsNullOrEmpty((s.FileStem?? "").Trim())) emptyFile++;
            StringBuilder sb = new StringBuilder();
            sb.Append("共 ").Append(_items.Count).Append(" 张");
            sb.Append("；重复图号 ").Append(dnDup.Count).Append(" 种");
            sb.Append("；重复文件名 ").Append(fileDup.Count).Append(" 种");
            if (emptyFile > 0) sb.Append("；文件名为空 ").Append(emptyFile).Append(" 张");
            _stat.Text = sb.ToString();
        }

        private void UpdateConfirmButton()
        {
            _confirmBtn.Text = "确认导出 PDF (" + _items.Count + " 张)";
            _confirmBtn.Enabled = _items.Count > 0;
        }

        // ================= 导出前检查（规格 A 的清单端） =================

        private enum PreflightUiAction { BackToList, AutoFixDuplicates}
        private enum ExistingAction { Cancel, OverwriteAll, SkipAll, SaveAsAll}

        private void Confirm()
        {
            try { _grid.EndEdit();} catch {}
            for (int i = 0; i < _items.Count; i++) SyncRow(i);
            if (_items.Count == 0) { Ui.Warn("清单为空，没有可导出的图纸"); return;}
            if (!RunPreflightFlow()) return; // 用户选择回清单修改，或检查未通过
            Confirmed = true;
            Close();
        }

        /// <summary>
        /// 导出前检查流程（与引擎内 BatchEngine.Execute 的二次检查用同一套规则）：
        /// ① 非法字符自动替换为 _ 并直接显示在清单里；② 重复文件名可一键自动修复（_01/_02），
        /// 否则回清单；③ 目标 PDF 已存在：全部覆盖 / 全部跳过 / 全部另存为（三选一）；
        /// ④ 其余错误列出「第 N 行 · 图号 · 原因」，回清单修改；⑤ 重复图号只警告，可确认继续。
        /// 循环直到检查干净或用户回清单。
        /// </summary>
        private bool RunPreflightFlow()
        {
            string outDir = CurrentOutDir();
            while (true)
            {
                int fixedChars = Naming.SanitizeStems(_items);
                if (fixedChars > 0)
                {
                    RefreshGrid();
                    Logger.Verbose("导出前检查：已将 " + fixedChars + " 个文件名中的非法字符替换为 _");
                }
                PreflightResult chk = BatchEngine.PreflightCheck(_items, outDir, _cfg);

                if (chk.CountOf(PreflightIssueKind.DuplicateFileName) > 0)
                {
                    PreflightUiAction act = ShowIssuesDialog("文件名重复，请处理", chk, true);
                    if (act == PreflightUiAction.AutoFixDuplicates)
                    {
                        Naming.FixDuplicateFileNames(_items);
                        RefreshGrid();
                        continue; // 重新检查（修复后仍可能有其他问题）
                    }
                    return false;
                }

                List<PreflightIssue> exists = new List<PreflightIssue>();
                foreach (PreflightIssue iss in chk.Issues)
                    if (iss.Kind == PreflightIssueKind.FileExists) exists.Add(iss);
                if (exists.Count > 0)
                {
                    ExistingAction act = ShowExistingDialog(exists, outDir);
                    if (act == ExistingAction.Cancel) return false;
                    ApplyExistingChoice(act, exists, outDir);
                    RefreshGrid();
                    continue; // 重新检查（另存为可能产生新重复；跳过后不再检查这些行）
                }

                if (chk.HasErrors)
                {
                    ShowIssuesDialog("导出前检查未通过", chk, false);
                    return false;
                }

                int dupDn = chk.CountOf(PreflightIssueKind.DuplicateDrawNo);
                if (dupDn > 0)
                {
                    DialogResult r = MessageBox.Show(
                    "清单里有 " + dupDn + " 处重复图号（已标红），仍要继续导出吗？\r\n（重复图号不影响导出，每张图仍单独生成一页 PDF。）",
                    "重复图号提醒", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r!= DialogResult.Yes) return false;
                }
                return true;
            }
        }

        private string CurrentOutDir()
        {
            try
            {
                string docName = "";
                if (AcadUtil.Ed!= null && AcadUtil.Ed.Document!= null)
                    docName = AcadUtil.Ed.Document.Name?? "";
                return _cfg.EffectiveOutputDir(docName);
            }
            catch { return _cfg.EffectiveOutputDir("");}
        }

        /// <summary>问题列表窗：只列问题，不导出；重复文件名时可一键自动修复。</summary>
        private PreflightUiAction ShowIssuesDialog(string title, PreflightResult chk, bool canAutoFixDup)
        {
            StringBuilder sb = new StringBuilder();
            foreach (PreflightIssue iss in chk.Issues)
                sb.Append(iss.ToString()).Append("\r\n");
            Form f = new Form
            {
                Text = title, Font = Ui.Font9, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ClientSize = new Size(560, 320)
            };
            TextBox tb = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Text = sb.ToString(), Dock = DockStyle.Fill, Font = Ui.Font9
            };
            f.Controls.Add(tb);
            FlowLayoutPanel b = new FlowLayoutPanel
            { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8)};
            PreflightUiAction act = PreflightUiAction.BackToList;
            if (canAutoFixDup && chk.CountOf(PreflightIssueKind.DuplicateFileName) > 0)
                b.Controls.Add(Ui.B("自动修复重复文件名", delegate
            {
                act = PreflightUiAction.AutoFixDuplicates;
                f.DialogResult = DialogResult.OK; f.Close();
            }, 170));
            b.Controls.Add(Ui.B("返回清单修改", delegate
            {
                act = PreflightUiAction.BackToList;
                f.DialogResult = DialogResult.Cancel; f.Close();
            }, 130));
            f.Controls.Add(b);
            f.ShowDialog(this);
            using (f) {}
            return act;
        }

        /// <summary>目标 PDF 已存在：全部覆盖 / 全部跳过 / 全部另存为 / 取消。</summary>
        private ExistingAction ShowExistingDialog(List<PreflightIssue> exists, string outDir)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("以下 ").Append(exists.Count).Append(" 个目标 PDF 已经存在（").Append(outDir).Append("）：\r\n\r\n");
            foreach (PreflightIssue iss in exists)
                sb.Append(iss.ToString()).Append("\r\n");
            sb.Append("\r\n覆盖会替换原文件；跳过则这几张本次不导出；另存为会自动换一个不重名的文件名。");
            Form f = new Form
            {
                Text = "目标 PDF 已存在", Font = Ui.Font9, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ClientSize = new Size(560, 300)
            };
            TextBox tb = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Text = sb.ToString(), Dock = DockStyle.Fill, Font = Ui.Font9
            };
            f.Controls.Add(tb);
            FlowLayoutPanel b = new FlowLayoutPanel
            { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8)};
            ExistingAction act = ExistingAction.Cancel;
            b.Controls.Add(Ui.B("取消", delegate
            {
                act = ExistingAction.Cancel;
                f.DialogResult = DialogResult.Cancel; f.Close();
            }, 80));
            b.Controls.Add(Ui.B("全部另存为", delegate
            {
                act = ExistingAction.SaveAsAll;
                f.DialogResult = DialogResult.OK; f.Close();
            }, 110));
            b.Controls.Add(Ui.B("全部跳过", delegate
            {
                act = ExistingAction.SkipAll;
                f.DialogResult = DialogResult.OK; f.Close();
            }, 96));
            b.Controls.Add(Ui.B("全部覆盖", delegate
            {
                act = ExistingAction.OverwriteAll;
                f.DialogResult = DialogResult.OK; f.Close();
            }, 96));
            f.Controls.Add(b);
            f.ShowDialog(this);
            using (f) {}
            return act;
        }

        private void ApplyExistingChoice(ExistingAction act, List<PreflightIssue> exists, string outDir)
        {
            foreach (PreflightIssue iss in exists)
            {
                if (iss.RowIndex < 0 || iss.RowIndex >= _items.Count) continue;
                SheetItem s = _items[iss.RowIndex];
                if (act == ExistingAction.OverwriteAll) s.AllowOverwrite = true;
                else if (act == ExistingAction.SkipAll)
                {
                    s.Status = SheetStatus.Skipped;
                    s.Error = "目标 PDF 已存在，已跳过";
                }
            }
            if (act == ExistingAction.SaveAsAll) ApplySaveAs(exists, outDir);
        }

        /// <summary>全部另存为：每行换一个既不存在于磁盘、也不与清单内其他文件名冲突的新名。</summary>
        private void ApplySaveAs(List<PreflightIssue> exists, string outDir)
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SheetItem s in _items) taken.Add((s.FileStem?? "").Trim());
            foreach (PreflightIssue iss in exists)
            {
                if (iss.RowIndex < 0 || iss.RowIndex >= _items.Count) continue;
                SheetItem s = _items[iss.RowIndex];
                string stem = (s.FileStem?? "").Trim();
                taken.Remove(stem);
                string cand = BatchEngine.UniqueStemFor(outDir, stem);
                int n = 1;
                while (taken.Contains(cand)
                    || File.Exists(Path.Combine(outDir, cand + ".pdf"))
                || File.Exists(Path.Combine(outDir, cand + ".PDF")))
                {
                    cand = stem + "_" + n.ToString("00");
                    n++;
                }
                s.FileStem = cand;
                taken.Add(cand);
            }
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
            TextBox tb = new TextBox { Text = value, Width = 340, Location = new Point(20, 34), Font = Ui.Font9};
            f.Controls.Add(tb);
            FlowLayoutPanel b = new FlowLayoutPanel
            { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(8)};
            b.Controls.Add(Ui.B("确定", delegate { f.DialogResult = DialogResult.OK; f.Close();}, 80));
            b.Controls.Add(Ui.B("取消", delegate { f.DialogResult = DialogResult.Cancel; f.Close();}, 80));
            f.Controls.Add(b);
            f.AcceptButton = null;
            DialogResult r = f.ShowDialog();
            if (r == DialogResult.OK) value = tb.Text.Trim();
            using (f) {}
            return r;
        }
    }
}
