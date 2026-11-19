using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
// AutoCAD 2014 的 DatabaseServices 里也有 FlowDirection，不别名会与 WinForms 冲突（CS0104）。
using FlowDirection = System.Windows.Forms.FlowDirection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcCore = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MPlot
{
    /// <summary>
    /// 图框信息库管理窗口（菜单第 3 项「批打到文件 (图框型)」打开的就是它）。
    /// 三标签页：图框信息录入 | 目录生成配置 | 其它
    /// 表格前 8 列与需求清单一致，第 9 列「图名 (C)」是补的 —— 命名模板里有 C，没有它没法登记图名 Tag。
    /// 底部 5 个按钮一字不差；另补「开始批打 PDF」「生成图纸目录」两个执行入口，否则确定之后无法出图。
    /// </summary>
    public sealed class FrameLibraryForm : Form
    {
        private FrameLibrary _lib;
        private readonly TabControl _tabs = new TabControl { Dock = DockStyle.Fill, Font = Ui.Font9 };
        private readonly DataGridView _grid = new DataGridView();
        private readonly TextBox _project = Ui.T("", 220);

        // 目录生成配置
        private ComboBox _sort, _mergeMode;
        private CheckBox _desc, _csvOn, _table, _askPt, _merge, _overwrite, _keep;
        private TextBox _csvName, _mergedName, _outDir;
        private NumericUpDown _textH;
        private Label _preview;

        // 其它
        private TextBox _prefix, _date;
        private DataGridView _tokenGrid;
        private CheckBox _scanPs, _scanXref, _rectFallback, _textFallback, _bgPlot;
        private ComboBox _attrMode;
        private NumericUpDown _iou, _margin, _mmPerUnit;

        public bool StartBatchRequested { get; private set; }
        public bool CatalogOnlyRequested { get; private set; }
        public FrameLibrary Library { get { return _lib; } }

        public FrameLibraryForm(FrameLibrary lib)
        {
            _lib = lib ?? FrameLibrary.Load();
            Text = "批打到文件 (图框型) · 图框信息库管理"; Font = Ui.Font9;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1060, 660);
            MinimumSize = new Size(900, 560);

            BuildGrid();
            BuildTab1();
            BuildTab2();
            BuildTab3();

            // WinForms Dock 顺序：后添加的先占外圈。必须 Fill 最先、Top 次之、Bottom 最后。
            Controls.Add(_tabs);
            Panel top = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 8, 10, 0) };
            top.Controls.Add(Ui.Row(Ui.L("项目名称"), _project,
                Ui.L("框选录入会自动抓块名 + 识别图号 / 图名 / 版次 Tag，识别结果可人工改。")));
            Controls.Add(top);
            BuildButtons();
            FillGrid();
        }

        // ================================================================ 表格
        private void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.ColumnHeadersHeight = 30;
            _grid.Font = Ui.Font9;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.CellDoubleClick += Grid_CellDoubleClick;

            AddCol("序号", 55, true);
            AddCol("图框对应的图块", 170, false);
            AddCol("对应纸张", 190, true);
            AddCol("排序优先级别", 100, false);
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            { HeaderText = "删除", Width = 55, Name = "colDel" });
            AddCol("文件命名规则", 140, false);
            AddCol("图号 (A)", 120, false);
            AddCol("版次 (B)", 110, false);
            AddCol("图名 (C)", 120, false);   // 补的一列
        }

        private void AddCol(string header, int width, bool readOnly)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn
            { HeaderText = header, Width = width, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (readOnly) c.DefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
            _grid.Columns.Add(c);
        }

        private void FillGrid()
        {
            _grid.Rows.Clear();
            foreach (FrameEntry f in _lib.Frames)
            {
                int i = _grid.Rows.Add(
                    f.Seq, f.BlockName ?? "", f.Paper == null ? "" : f.Paper.ToString(),
                    f.Priority, f.Deleted, f.NameTemplate ?? "",
                    f.TagDrawNo ?? "", f.TagRev ?? "", f.TagTitle ?? "");
                _grid.Rows[i].Tag = f;
            }
        }

        private void SyncFromGrid()
        {
            for (int i = 0; i < _grid.Rows.Count && i < _lib.Frames.Count; i++)
            {
                DataGridViewRow r = _grid.Rows[i];
                FrameEntry f = r.Tag as FrameEntry ?? _lib.Frames[i];
                f.BlockName = Cell(r, 1).Trim();
                f.Priority = IntCell(r, 3, f.Priority);
                f.Deleted = BoolCell(r, 4);
                f.NameTemplate = Cell(r, 5).Trim();
                f.TagDrawNo = Cell(r, 6).Trim();
                f.TagRev = Cell(r, 7).Trim();
                f.TagTitle = Cell(r, 8).Trim();
            }
            _lib.Renumber();
        }
        private static string Cell(DataGridViewRow r, int i)
        {
            object v = r.Cells[i].Value; return v == null ? "" : v.ToString();
        }
        private static bool BoolCell(DataGridViewRow r, int i)
        {
            object v = r.Cells[i].Value; return v != null && v is bool && (bool)v;
        }
        private static int IntCell(DataGridViewRow r, int i, int fallback)
        {
            int n; return int.TryParse(Cell(r, i), out n) ? n : fallback;
        }

        private void Grid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex != 2) return;   // 纸张列
            FrameEntry f = _grid.Rows[e.RowIndex].Tag as FrameEntry;
            if (f == null) return;
            using (PaperEditForm pf = new PaperEditForm(f.Paper))
            {
                if (pf.ShowDialog(this) == DialogResult.OK && pf.Result != null)
                {
                    f.Paper = pf.Result;
                    _grid.Rows[e.RowIndex].Cells[2].Value = f.Paper.ToString();
                }
            }
        }

        // ================================================================ 标签页 1
        private void BuildTab1()
        {
            TabPage tp = new TabPage("图框信息录入") { Padding = new Padding(8) };
            Panel host = new Panel { Dock = DockStyle.Fill };
            Label hint = new Label
            {
                Dock = DockStyle.Top, Height = 46, Font = Ui.Font9,
                Text = "一行 = 一种图框块。纸张双击单元格可改（支持 A0~A4、加长规格、自定义宽高）。\r\n"
                     + "「删除」列勾选后，点【确定/开始批打】会彻底从库中移除、不再显示（不可恢复）；取消勾选则不删。"
            };
            host.Controls.Add(_grid);
            host.Controls.Add(hint);
            tp.Controls.Add(host);
            _tabs.TabPages.Add(tp);
        }

        // ================================================================ 标签页 2
        private void BuildTab2()
        {
            TabPage tp = new TabPage("目录生成配置") { Padding = new Padding(10) };
            GlobalConfig c = GlobalConfig.Current;

            _sort = Ui.D(new[] { "按排序优先级", "按图号（数字感知）", "按布局顺序", "按图框位置（从上到下）" },
                         220, (int)c.SortMode);
            _desc = Ui.C("降序", c.SortDescending);
            _outDir = Ui.T(c.OutputDir, 330);
            _csvOn = Ui.C("生成图纸目录 CSV（Excel 直接打开）", c.CatalogCsv);
            _csvName = Ui.T(c.CatalogCsvName, 190);
            _table = Ui.C("在当前 DWG 内生成 CAD 原生 TABLE 目录", c.CatalogTable);
            _askPt = Ui.C("生成 TABLE 时询问插入点", c.TableAskForInsertPoint);
            _textH = Ui.N(c.TableTextHeightMm, 1, 50, 1);
            _merge = Ui.C("自动合并为一份图纸合集 PDF", c.MergePdf);
            _mergedName = Ui.T(c.MergedFileName, 190);
            _mergeMode = Ui.D(new[] { "GS 优先（失败切内置合并）", "内置合并优先（零依赖，推荐）", "只用 Ghostscript" },
                              260, (int)c.MergeMode);
            _overwrite = Ui.C("同名直接覆盖", c.OverwriteExisting);
            _keep = Ui.C("合并后保留单页 PDF（推荐）", c.KeepSinglePdfsAfterMerge);
            _preview = Ui.L("", 800);
            _preview.Height = 34; _preview.AutoSize = false;

            FlowLayoutPanel p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoScroll = true };
            p.Controls.Add(Ui.L("── 排序 ──"));
            p.Controls.Add(Ui.Row(Ui.L("目录 / 批打排序", 120), _sort, _desc));
            p.Controls.Add(Ui.L("目录序号与 PDF 生成顺序完全一致；按图号时用数字感知排序（A-2 在 A-10 前）。"));
            p.Controls.Add(Ui.L("── 输出 ──"));
            p.Controls.Add(Ui.Row(Ui.L("全局输出目录", 120), _outDir,
                                  Ui.B("浏览", delegate { Browse(_outDir); }, 70)));
            p.Controls.Add(Ui.L("留空 = 当前 DWG 同目录下的「PDF输出」文件夹"));
            p.Controls.Add(Ui.Row(_overwrite));
            p.Controls.Add(Ui.L("── 图纸目录 ──"));
            p.Controls.Add(Ui.Row(_csvOn, Ui.L("CSV 文件名"), _csvName));
            p.Controls.Add(Ui.Row(_table, _askPt, Ui.L("字高mm"), _textH));
            p.Controls.Add(Ui.L("CSV 字段固定：序号 / 图号 / 图名 / 版次 / PDF 文件名；UTF-8 BOM，Excel 双击不乱码。"));
            p.Controls.Add(Ui.L("── PDF 合并 ──"));
            p.Controls.Add(Ui.Row(_merge, Ui.L("合集文件名"), _mergedName));
            p.Controls.Add(Ui.Row(Ui.L("合并通道", 120), _mergeMode));
            p.Controls.Add(Ui.Row(_keep));
            p.Controls.Add(Ui.L("── 命名模板预览 ──"));
            p.Controls.Add(_preview);
            p.Controls.Add(Ui.Row(Ui.T(Naming.DefaultTemplate, 260), Ui.B("试算这个模板", delegate { TryPreview(); }, 130),
                                  Ui.B("预演本图全部文件名", delegate { PreviewAll(); }, 170)));
            p.Controls.Add(Ui.L("默认「图号_图名_图框大小」；也可用 A=图号 C=图名 B=版次，T=版次别名，N=序号 D=日期 P=项目前缀，「其它」页可改符号映射。"));
            tp.Controls.Add(p);
            _tabs.TabPages.Add(tp);
            TryPreview();
        }

        private void Browse(TextBox t)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
                if (d.ShowDialog() == DialogResult.OK) t.Text = d.SelectedPath;
        }

        private void TryPreview()
        {
            FlowLayoutPanel p = _preview.Parent as FlowLayoutPanel;
            TextBox tpl = null;
            if (p != null)
            {
                foreach (Control ctl in p.Controls)
                {
                    FlowLayoutPanel row = ctl as FlowLayoutPanel;
                    if (row == null || row.Controls.Count < 1) continue;
                    if (row.Controls[0] is TextBox && ((TextBox)row.Controls[0]).Text.Contains("_")) tpl = (TextBox)row.Controls[0];
                }
            }
            GlobalConfig c = GlobalConfig.Current;
            string s = tpl == null ? Naming.DefaultTemplate : tpl.Text.Trim();
            _preview.Text = "示例：" + Naming.Preview(s, c) + "    " + Naming.ValidateTemplate(s, c);
        }

        private void PreviewAll()
        {
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) { Ui.Warn("请先打开 DWG"); return; }
            SyncFromGrid();
            ScanResult r = FrameScanner.Scan(doc.Database, _lib, GlobalConfig.Current);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (SheetItem s in r.Sheets) sb.AppendLine(s.Index.ToString("00") + "  " + (s.FileStem ?? "") + ".pdf");
            Ui.Info("共 " + r.Sheets.Count + " 张（只扫描命名，不打印）：\r\n\r\n" + sb, "预演文件名");
        }

        // ================================================================ 标签页 3
        private void BuildTab3()
        {
            TabPage tp = new TabPage("其它") { Padding = new Padding(10) };
            GlobalConfig c = GlobalConfig.Current;

            _prefix = Ui.T(c.ProjectPrefix, 160);
            _date = Ui.T(c.DateFormat, 120);
            _scanPs = Ui.C("同时扫描图纸空间布局里的图框", c.ScanPaperSpace);
            _scanXref = Ui.C("扫描外部参照(Xref)里的图框", c.ScanXrefFrames);
            _rectFallback = Ui.C("图框是普通闭合矩形时也识别（老图兜底）", c.RectFrameFallback);
            _textFallback = Ui.C("块无属性时按标题栏位置找图号 / 图名 / 版次", c.TitleBarTextFallback);
            _attrMode = Ui.D(new[] { "读取动态块显示文本（推荐）", "读取原始属性值（ATTRIB 存储值）" },
                             320, (int)c.AttrReadMode);
            _bgPlot = Ui.C("关闭后台打印（推荐）", c.DisableBackgroundPlot);
            _iou = Ui.N(c.DuplicateIouThreshold, 0.1, 1.0, 2);
            _margin = Ui.N(c.WindowMarginMm, 0, 100, 1);
            _mmPerUnit = Ui.N(c.MmPerDrawingUnit, 0, 10000, 3);

            _tokenGrid = new DataGridView
            {
                Width = 420, Height = 220, AllowUserToAddRows = true, AllowUserToDeleteRows = true,
                RowHeadersVisible = false, Font = Ui.Font9, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            _tokenGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "模板符号", Width = 110 });
            DataGridViewComboBoxColumn field = new DataGridViewComboBoxColumn { HeaderText = "对应字段", Width = 260 };
            foreach (string fn in Naming.FieldNames) field.Items.Add(fn);
            _tokenGrid.Columns.Add(field);
            foreach (var kv in c.TokenMap ?? new Dictionary<string, string>())
                _tokenGrid.Rows.Add(kv.Key, kv.Value ?? "");

            FlowLayoutPanel p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoScroll = true };
            p.Controls.Add(Ui.L("── 项目 / 命名 ──"));
            p.Controls.Add(Ui.L("项目名称在窗口顶部填写（与整个图框库一起导出导入）"));
            p.Controls.Add(Ui.Row(Ui.L("项目前缀 P", 110), _prefix, Ui.L("日期格式 D"), _date));
            p.Controls.Add(Ui.L("── 文件命名符号映射（可增删改）──"));
            p.Controls.Add(_tokenGrid);
            p.Controls.Add(Ui.L("模板按 _ 分段，整段等于某符号就替换成对应字段；空值整段丢弃，不留空下划线。"));
            p.Controls.Add(Ui.L("也支持花括号写法，例如 {A}-{C}({B})、{P}_{N}_{C}。"));
            p.Controls.Add(Ui.L("── 扫描 / 识别 ──"));
            p.Controls.Add(Ui.Row(_scanPs, _scanXref));
            p.Controls.Add(Ui.Row(_rectFallback, _textFallback));
            p.Controls.Add(Ui.Row(Ui.L("图框属性读取", 150), _attrMode));
            p.Controls.Add(Ui.L("天正动态图框切换可见性后，图面显示的常不是 ATTRIB 存储值；默认读显示文本（动态参数优先、隐藏属性不读、同 Tag 按策略取一条）。切到原始值时，不读动态参数、不过滤隐藏属性。"));
            p.Controls.Add(Ui.Row(Ui.L("嵌套图框去重阈值", 150), _iou));
            p.Controls.Add(Ui.Row(Ui.L("四边留白 mm", 150), _margin, Ui.L("纸张不变、内容缩小留白；0=贴边"), _mmPerUnit));
            p.Controls.Add(Ui.Row(_bgPlot));
            p.Controls.Add(Ui.L("── 配置 ──"));
            p.Controls.Add(Ui.Row(
                Ui.B("打开配置目录", delegate { try { System.Diagnostics.Process.Start(FrameLibrary.EnsureDir()); } catch { } }, 130),
                Ui.B("查看日志", delegate { try { if (File.Exists(Logger.SessionFile)) System.Diagnostics.Process.Start(Logger.SessionFile); } catch { } }, 100),
                Ui.B("重置为默认库", delegate { ResetLibrary(); }, 130)));
            p.Controls.Add(Ui.L("图框库存 %APPDATA%\\MPlot\\frames.json；【导出设置 / 导入设置】可整包复制到别的项目复用。"));
            tp.Controls.Add(p);
            _tabs.TabPages.Add(tp);
        }

        private void ResetLibrary()
        {
            if (MessageBox.Show("用示例库替换当前图框库？当前条目会被覆盖（可先导出备份）。", "重置",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _lib = FrameLibrary.CreateDefault();
            FillGrid();
            _project.Text = _lib.ProjectName;
        }

        // ================================================================ 底部按钮
        private void BuildButtons()
        {
            Panel bar = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8, 8, 8, 8) };
            FlowLayoutPanel left = new FlowLayoutPanel
            { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Left, Width = 560, AutoSize = false };
            left.Controls.Add(Ui.B("录入新图框", delegate { AddFrame(); }, 110));
            left.Controls.Add(Ui.B("导出设置", delegate { Export(); }, 95));
            left.Controls.Add(Ui.B("导入设置", delegate { Import(); }, 95));
            left.Controls.Add(Ui.B("确定", delegate { Ok(); }, 85));
            left.Controls.Add(Ui.B("取消", delegate { Close(); }, 85));

            FlowLayoutPanel right = new FlowLayoutPanel
            { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, Width = 380, AutoSize = false };
            Button go = Ui.B("开始批打 PDF", delegate { StartBatch(); }, 150);
            go.BackColor = Color.FromArgb(28, 92, 156);
            go.ForeColor = Color.White;
            go.FlatStyle = FlatStyle.Flat;
            right.Controls.Add(go);
            right.Controls.Add(Ui.B("生成图纸目录", delegate { CatalogOnly(); }, 140));
            bar.Controls.Add(left); bar.Controls.Add(right);
            Controls.Add(bar);
        }

        /// <summary>
        /// 勾选了「删除」的图框：真正从库中移除（不再是软标记），确定/开始批打/生成目录时生效。
        /// 删除不可逆 → 弹确认；取消勾选的行不受影响；删完 Renumber 重排序号并刷新表格。
        /// </summary>
        private void RemoveDeletedFrames()
        {
            List<FrameEntry> doomed = new List<FrameEntry>();
            foreach (FrameEntry f in _lib.Frames)
                if (f != null && f.Deleted) doomed.Add(f);
            if (doomed.Count == 0) return;

            System.Text.StringBuilder names = new System.Text.StringBuilder();
            int show = Math.Min(8, doomed.Count);
            for (int i = 0; i < show; i++)
                names.Append("  · ").Append(string.IsNullOrWhiteSpace(doomed[i].BlockName)
                    ? ("(第" + doomed[i].Seq + "行)") : doomed[i].BlockName).Append("\r\n");
            if (doomed.Count > show) names.Append("  … 以及另外 ").Append(doomed.Count - show).Append(" 条\r\n");

            if (DialogResult.Yes != MessageBox.Show(
                    "已勾选 " + doomed.Count + " 条图框的「删除」，将彻底从库中移除、不再显示（不可恢复）：\r\n\r\n"
                    + names + "\r\n确定要删除吗？",
                    "K-批量打印 · 删除图框", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2))
                return;   // 用户取消：保留这些行（下次可再取消勾选）

            foreach (FrameEntry f in doomed) _lib.Frames.Remove(f);
            _lib.Renumber();
            FillGrid();
            Logger.Info("图框库彻底删除 " + doomed.Count + " 条");
        }

        private void SaveAll()
        {
            SyncFromGrid();
            RemoveDeletedFrames();   // 勾选删除的行真正移除库（含确认）
            GlobalConfig c = GlobalConfig.Current;
            c.SortMode = (SortMode)_sort.SelectedIndex;
            c.SortDescending = _desc.Checked;
            c.OutputDir = _outDir.Text.Trim();
            c.CatalogCsv = _csvOn.Checked; c.CatalogCsvName = _csvName.Text.Trim();
            c.CatalogTable = _table.Checked; c.TableAskForInsertPoint = _askPt.Checked;
            c.TableTextHeightMm = (double)_textH.Value;
            c.MergePdf = _merge.Checked; c.MergedFileName = _mergedName.Text.Trim();
            c.MergeMode = (MergeMode)Math.Min(2, _mergeMode.SelectedIndex);
            c.OverwriteExisting = _overwrite.Checked; c.KeepSinglePdfsAfterMerge = _keep.Checked;
            c.ProjectPrefix = _prefix.Text; c.DateFormat = _date.Text.Trim();
            c.ScanPaperSpace = _scanPs.Checked; c.ScanXrefFrames = _scanXref.Checked;
            c.RectFrameFallback = _rectFallback.Checked; c.TitleBarTextFallback = _textFallback.Checked;
            c.AttrReadMode = (AttrReadMode)Math.Max(0, Math.Min(1, _attrMode.SelectedIndex));
            c.DisableBackgroundPlot = _bgPlot.Checked;
            c.DuplicateIouThreshold = (double)_iou.Value;
            c.WindowMarginMm = (double)_margin.Value;
            c.MmPerDrawingUnit = (double)_mmPerUnit.Value;

            Dictionary<string, string> map = new Dictionary<string, string>();
            foreach (DataGridViewRow r in _tokenGrid.Rows)
            {
                if (r.IsNewRow) continue;
                object k = r.Cells[0].Value, v = r.Cells[1].Value;
                if (k == null || v == null) continue;
                string ks = k.ToString().Trim(), vs = v.ToString().Trim();
                if (ks.Length > 0 && vs.Length > 0) map[ks] = vs;
            }
            if (map.Count > 0) c.TokenMap = map;
            c.Save();
            _lib.ProjectName = _project.Text.Trim();
            _lib.Save();
            PaperMatcher.InvalidateCache();
        }

        private void AddFrame()
        {
            // 图框库主窗口在 CAD 里是 ShowModalDialog 模态窗，会挡住绘图区。
            // 录入新图框要在 CAD 图上点选图框/框文字，必须先隐藏主窗口，否则「没法选择窗口内容」。
            // 录入结束（含取消/异常）后恢复显示。录入窗不再以被隐藏的主窗为父，改用 CAD 当前活动窗口。
            Hide();
            try
            {
                using (FrameEditForm f = new FrameEditForm(null))
                {
                    if (f.ShowDialog() != DialogResult.OK || f.Result == null) return;
                    FrameEntry e = f.Result;
                    e.Seq = _lib.Frames.Count + 1;
                    _lib.Frames.Add(e);
                    FillGrid();
                }
            }
            finally
            {
                Show();
                BringToFront();
            }
        }

        private void Export()
        {
            SaveAll();
            using (SaveFileDialog d = new SaveFileDialog
            { Filter = "图框库 JSON|*.json", FileName = "图框库_" + DateTime.Now.ToString("yyyyMMdd") + ".json" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                try { _lib.ExportTo(d.FileName); Ui.Info("已导出：" + d.FileName); }
                catch (Exception ex) { Ui.Warn("导出失败：" + ex.Message); }
            }
        }

        private void Import()
        {
            using (OpenFileDialog d = new OpenFileDialog { Filter = "图框库 JSON|*.json" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                DialogResult merge = MessageBox.Show("是 = 按块名合并进当前库\r\n否 = 整包覆盖当前库\r\n取消 = 不导入",
                    "导入设置", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (merge == DialogResult.Cancel) return;
                try
                {
                    _lib = FrameLibrary.ImportFrom(d.FileName, _lib, merge == DialogResult.Yes);
                    FillGrid(); _project.Text = _lib.ProjectName;
                    Ui.Info("导入完成，共 " + _lib.Frames.Count + " 条");
                }
                catch (Exception ex) { Ui.Warn("导入失败：" + ex.Message); }
            }
        }

        private void Ok() { SaveAll(); DialogResult = DialogResult.OK; Close(); }
        private void StartBatch() { SaveAll(); StartBatchRequested = true; Close(); }
        private void CatalogOnly() { SaveAll(); CatalogOnlyRequested = true; Close(); }
    }

    // ==================================================================== 录入新图框
    /// <summary>
    /// 两种录入：手动输入块名 / 【框选图框】。
    /// 框选后自动抓块名、扫描该块全部属性、按「Tag 关键词 + 值形态 + 标题栏位置」打分，
    /// 自动识别图号 / 图名 / 版次对应的 Tag 与所在位置，预填进表单，全部可人工改。
    /// </summary>
    public sealed class FrameEditForm : Form
    {
        private readonly TextBox _block = Ui.T("", 220);
        private readonly TextBox _tagA = Ui.T("", 150), _tagB = Ui.T("", 150), _tagC = Ui.T("", 150);
        private readonly CheckBox _readA, _readB, _readC;
        private readonly ComboBox _paper, _hint;
        private readonly NumericUpDown _w, _h, _prio, _scaleNum, _scaleDen;
        private readonly TextBox _tpl = Ui.T(Naming.DefaultTemplate, 260);
        private readonly TextBox _note = Ui.T("", 260);
        private readonly Label _preview = Ui.L("", 430);
        private readonly Label _conf = Ui.L("尚未框选", 430);
        private readonly Label _regionInfo = Ui.L("图号范围：未点取    图名范围：未点取", 640);
        private readonly Label _blockShown = Ui.L("图框对应的图块：（未框选）", 430);
        // 归一化区域，默认 -1 = 未点取；与 FrameEntry 字段一一对应
        private double _dnX0 = -1, _dnY0 = -1, _dnX1 = -1, _dnY1 = -1;
        private double _ttX0 = -1, _ttY0 = -1, _ttX1 = -1, _ttY1 = -1;
        // 人工点选确认的锚点（归一化）+ 样例值
        private double _dnAx = -1, _dnAy = -1, _ttAx = -1, _ttAy = -1;
        private string _dnSample = "", _ttSample = "";
        // 是否启用点选识别（参考图2每个点取项后的复选框）
        private readonly CheckBox _useDn, _useTt;
        private readonly DataGridView _attrs = new DataGridView();
        private List<AttrInfo> _pickedAttrs = new List<AttrInfo>();
        private Extents2d? _pickedWcs;

        public FrameEntry Result { get; private set; }

        public FrameEditForm(FrameEntry existing)
        {
            Text = "录入新图框"; Font = Ui.Font9; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 660); MinimumSize = new Size(720, 600);
            // 可移动 + 可缩放：录入时要对照 CAD 图上的文字，允许拖到合适位置
            FormBorderStyle = FormBorderStyle.Sizable;

            _readA = Ui.C("读取图号", true); _readB = Ui.C("读取版次", true); _readC = Ui.C("读取图名", true);
            _useDn = Ui.C("启用点选图号", false); _useTt = Ui.C("启用点选图名", false);
            _paper = Ui.D(PaperPresets.Names, 200, 1);
            _w = Ui.N(841, 1, 10000, 1); _h = Ui.N(594, 1, 10000, 1);
            _w.Enabled = _h.Enabled = false;
            _paper.SelectedIndexChanged += delegate
            {
                PaperSpec spec = PaperPresets.Get(_paper.Text);
                _w.Value = (decimal)Math.Min(10000, Math.Max(1, spec.WidthMm));
                _h.Value = (decimal)Math.Min(10000, Math.Max(1, spec.HeightMm));
                _w.Enabled = _h.Enabled = spec.IsCustom;
            };
            _hint = Ui.D(new[] { "auto", "bottom-right", "bottom-left", "top-right", "top-left" }, 180, 0);
            _prio = Ui.N(10, -9999, 9999, 0);
            _scaleNum = Ui.N(0, 0, 10000, 2); _scaleDen = Ui.N(0, 0, 100000, 2);
            _tpl.TextChanged += delegate { UpdatePreview(); };
            _preview.AutoSize = false; _preview.Height = 26;
            _conf.AutoSize = false; _conf.Height = 26;

            _attrs.Width = 700; _attrs.Height = 190;
            _attrs.AllowUserToAddRows = false; _attrs.AllowUserToDeleteRows = false;
            _attrs.RowHeadersVisible = false; _attrs.ReadOnly = true;
            _attrs.Font = Ui.Font9; _attrs.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _attrs.Columns.Add("colTag", "属性 Tag");
            _attrs.Columns.Add("colVal", "当前值");
            _attrs.Columns.Add("colJudge", "自动判定");
            _attrs.Columns.Add("colPos", "相对位置");
            _attrs.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
                object v = _attrs.Rows[e.RowIndex].Cells[0].Value;
                if (v == null) return;
                string tag = v.ToString();
                string target = AskTagTarget();
                if (target == "A") _tagA.Text = tag;
                else if (target == "B") _tagB.Text = tag;
                else if (target == "C") _tagC.Text = tag;
            };

            if (existing != null) LoadFrom(existing);

            // 参考图2：左栏「1 基本信息（必填项）」，右栏「2 信息框录入（非必录）」
            TableLayoutPanel tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(8)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            FlowLayoutPanel left = new FlowLayoutPanel
            { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6) };
            left.Controls.Add(Ui.L("1、基本信息（必填项）"));
            left.Controls.Add(Ui.Row(Ui.B("选择图框（自动识别）", delegate { Pick(); }, 180)));
            left.Controls.Add(_blockShown);
            left.Controls.Add(Ui.L("图框块名（可手填，支持 * 通配，如 TK_*）：", 400));
            left.Controls.Add(_block);
            left.Controls.Add(_conf);
            left.Controls.Add(Ui.L("对应纸张 / 命名规则 / 排序优先级："));
            left.Controls.Add(Ui.Row(Ui.L("对应纸张", 80), _paper, Ui.L("宽mm"), _w, Ui.L("高mm"), _h));
            left.Controls.Add(Ui.Row(Ui.L("排序优先级", 80), _prio, Ui.L("数字小的先打")));
            left.Controls.Add(Ui.Row(Ui.L("命名模板", 80), _tpl));
            left.Controls.Add(_preview);
            left.Controls.Add(Ui.L("── 自动识别到的属性（双击 Tag 填到字段）──"));
            _attrs.Width = 340; _attrs.Height = 150;
            left.Controls.Add(_attrs);
            left.Controls.Add(Ui.Row(Ui.B("重新自动识别", delegate { ReGuess(); }, 130)));
            left.Controls.Add(Ui.L("── 版次 (B) 属性 ──"));
            left.Controls.Add(Ui.Row(Ui.L("版次Tag", 70), _tagB, _readB));
            left.Controls.Add(Ui.Row(Ui.L("比例覆盖", 80), _scaleNum, _scaleDen, Ui.L("0=自动")));
            left.Controls.Add(Ui.Row(Ui.L("备注", 70), _note));
            left.Controls.Add(Ui.L("标题栏方位", 80));
            left.Controls.Add(_hint);

            FlowLayoutPanel right = new FlowLayoutPanel
            { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6) };
            right.Controls.Add(Ui.L("2、信息框录入（非必录）"));
            right.Controls.Add(Ui.L("先点左栏【选择图框】，再点下列按钮在图框上框出文字小区域；"));
            right.Controls.Add(Ui.L("框出后会列出候选文字，你点选确认为图号/图名，程序记住该文字位置。"));
            right.Controls.Add(Ui.Row(Ui.B("点取图号 (A) 范围", delegate { PickRegion("drawno"); }, 150), _useDn));
            right.Controls.Add(Ui.Row(Ui.B("点取图名 (C) 范围", delegate { PickRegion("title"); }, 150), _useTt));
            right.Controls.Add(Ui.Row(Ui.B("清除范围", delegate { ClearRegions(); }, 100)));
            right.Controls.Add(_regionInfo);
            right.Controls.Add(Ui.L("── 图号 / 图名属性 Tag（可不用，靠上面点选更准）──"));
            right.Controls.Add(Ui.Row(Ui.L("图号 (A) Tag", 100), _tagA, _readA));
            right.Controls.Add(Ui.Row(Ui.L("图名 (C) Tag", 100), _tagC, _readC));
            right.Controls.Add(Ui.L("Tag 可填多个候选，用 | 分隔，例如 图号|DRAWING_NO|NO"));

            tbl.Controls.Add(left, 0, 0);
            tbl.Controls.Add(right, 1, 0);

            FlowLayoutPanel b = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
            b.Controls.Add(Ui.B("从已录图框复制信息", delegate { CopyFromExisting(); }, 160));
            b.Controls.Add(Ui.B("确定", delegate { Accept(); }, 80));
            b.Controls.Add(Ui.B("取消", delegate { Close(); }, 80));
            Controls.Add(tbl); Controls.Add(b);
            UpdatePreview();
        }

        private void LoadFrom(FrameEntry f)
        {
            _block.Text = f.BlockName ?? "";
            _tagA.Text = f.TagDrawNo ?? ""; _tagB.Text = f.TagRev ?? ""; _tagC.Text = f.TagTitle ?? "";
            _readA.Checked = f.ReadDrawNo; _readB.Checked = f.ReadRev; _readC.Checked = f.ReadTitle;
            if (f.Paper != null)
            {
                int i = Array.IndexOf(PaperPresets.Names, f.Paper.Preset ?? "A1");
                if (i >= 0) _paper.SelectedIndex = i;
                _w.Value = (decimal)Math.Min(10000, Math.Max(1, f.Paper.WidthMm));
                _h.Value = (decimal)Math.Min(10000, Math.Max(1, f.Paper.HeightMm));
            }
            _prio.Value = (decimal)Math.Max(-9999, Math.Min(9999, f.Priority));
            _tpl.Text = string.IsNullOrWhiteSpace(f.NameTemplate) ? Naming.DefaultTemplate : f.NameTemplate;
            _scaleNum.Value = (decimal)Math.Max(0, Math.Min(10000, f.ScaleNumerator));
            _scaleDen.Value = (decimal)Math.Max(0, Math.Min(100000, f.ScaleDenominator));
            int hi = Array.IndexOf(new[] { "auto", "bottom-right", "bottom-left", "top-right", "top-left" }, f.TitleBarHint ?? "auto");
            _hint.SelectedIndex = hi < 0 ? 0 : hi;
            _dnX0 = f.DnX0; _dnY0 = f.DnY0; _dnX1 = f.DnX1; _dnY1 = f.DnY1;
            _ttX0 = f.TtX0; _ttY0 = f.TtY0; _ttX1 = f.TtX1; _ttY1 = f.TtY1;
            _dnAx = f.DnAnchorX; _dnAy = f.DnAnchorY; _ttAx = f.TtAnchorX; _ttAy = f.TtAnchorY;
            _dnSample = f.DnSample ?? ""; _ttSample = f.TtSample ?? "";
            _useDn.Checked = f.HasDnAnchor; _useTt.Checked = f.HasTtAnchor;
            _blockShown.Text = "图框对应的图块：" + (f.BlockName ?? "（未填）");
            UpdateRegionInfo();
            _note.Text = f.Note ?? "";
        }

        // ---------------------------------------------------------------- 框选
        private void Pick()
        {
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) { Ui.Warn("请先打开 DWG"); return; }
            ObjectId id = ObjectId.Null;
            using (Ui.StartInteraction(this))
            {
                AcadUtil.Say("框选模型里的一个图框（窗选 / 点选均可）");
                id = AcadUtil.PickFrameInsert();
            }
            if (id.IsNull) { _conf.Text = "已取消框选"; return; }

            try
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) { Ui.Warn("选中的不是块参照"); return; }
                    string name = AcadUtil.EffectiveBlockName(br, tr);
                    _block.Text = name;
                    _blockShown.Text = "图框对应的图块：" + name;
                    Extents3d? ex = AcadUtil.FrameExtents(br, tr);
                    _pickedWcs = ex.HasValue
                        ? (Extents2d?)new Extents2d(ex.Value.MinPoint.X, ex.Value.MinPoint.Y,
                                                    ex.Value.MaxPoint.X, ex.Value.MaxPoint.Y)
                        : null;
                    // 先算出图框范围再列属性：同 TAG 去重时要判断谁落在标题栏里
                    _pickedAttrs = AttrReader.ListAttributes(br, tr, _pickedWcs);
                    tr.Commit();
                }
                ReGuess();
                GuessPaper();
            }
            catch (Exception ex) { Ui.Warn("框选识别失败：" + ex.Message); Logger.Error("框选识别失败", ex); }
        }

        private void ReGuess()
        {
            if (_pickedAttrs.Count == 0) { _conf.Text = "这个块没有属性；批打时会走「标题栏文字兜底」读图号图名"; return; }
            TagGuess g = AttrReader.GuessTags(_pickedAttrs, _pickedWcs);
            _tagA.Text = g.TagDrawNo ?? "";
            _tagB.Text = g.TagRev ?? "";
            _tagC.Text = g.TagTitle ?? "";
            _conf.Text = "块名：" + _block.Text + "    共 " + _pickedAttrs.Count + " 个属性"
                + "    自动识别置信度：" + g.Confidence.ToString("0")
                + (g.Confidence < 25 ? "（偏低，请人工核对三个 Tag）" : "");

            double mmPer = AcadUtil.MmPerUnit(AcCore.DocumentManager.MdiActiveDocument.Database);
            _attrs.Rows.Clear();
            foreach (AttrInfo a in _pickedAttrs)
            {
                string judge = a.Tag == g.TagDrawNo ? "图号 (A)"
                    : a.Tag == g.TagRev ? "版次 (B)"
                    : a.Tag == g.TagTitle ? "图名 (C)" : "—";
                if (a.FromDynamic) judge += " · 动态";   // 值来自动态块实时参数，不是 ATTRIB 存储值
                if (a.FromDefinition) judge += " · 定义";
                string pos = "—";
                if (_pickedWcs.HasValue)
                {
                    Extents2d f = _pickedWcs.Value;
                    double ww = f.MaxPoint.X - f.MinPoint.X, hh = f.MaxPoint.Y - f.MinPoint.Y;
                    if (ww > 0 && hh > 0)
                    {
                        double rx = (a.Position.X - f.MinPoint.X) / ww, ry = (a.Position.Y - f.MinPoint.Y) / hh;
                        pos = (ry < 0.35 ? "下" : ry > 0.65 ? "上" : "中") + (rx < 0.35 ? "左" : rx > 0.65 ? "右" : "中")
                            + string.Format("  ({0:0.#},{1:0.#})mm", (a.Position.X - f.MinPoint.X) * mmPer,
                                                                        (a.Position.Y - f.MinPoint.Y) * mmPer);
                        if (ry < 0.35 && rx > 0.45 && _hint.SelectedIndex == 0) _hint.SelectedIndex = 1; // bottom-right
                    }
                }
                _attrs.Rows.Add(a.Tag, a.Value, judge, pos);
            }
        }

        /// <summary>
        /// 框选到的图框常被放大 100 倍画（1:100 出 A1 → 模型里 84100×59400）。
        /// 所以不能拿实测毫米宽直接当纸张，要按「实测宽 ÷ 常见出图比例」反推标称幅面。
        /// </summary>
        private void GuessPaper()
        {
            if (!_pickedWcs.HasValue) return;
            Extents2d f = _pickedWcs.Value;
            double mmPer = AcadUtil.MmPerUnit(AcCore.DocumentManager.MdiActiveDocument.Database);
            double w = (f.MaxPoint.X - f.MinPoint.X) * mmPer;
            double h = (f.MaxPoint.Y - f.MinPoint.Y) * mmPer;
            if (w <= 0 || h <= 0) return;

            string best = ""; double bestErr = double.MaxValue;
            foreach (string n in PaperPresets.Names)
            {
                if (n == "自定义") continue;
                PaperSpec p = PaperPresets.Get(n);
                double rw = w / p.WidthMm, rh = h / p.HeightMm;
                if (Math.Abs(rw - rh) / Math.Max(rw, rh) > 0.04) continue;
                double r = (rw + rh) / 2;
                double snapped = PaperMatcher.ComputeScaleDenominator(w, h,
                    new FrameEntry { Paper = p }, false);
                double err = Math.Abs(snapped - r) / r + Math.Abs(rw - rh) / r;
                if (err < bestErr) { bestErr = err; best = n; }
            }
            if (best != "" && bestErr < 0.03)
            {
                int i = Array.IndexOf(PaperPresets.Names, best);
                _paper.SelectedIndex = i;
                _conf.Text += "    纸张自动判为 " + best + "（出图比例约 1:" +
                    PaperMatcher.ComputeScaleDenominator(w, h,
                        new FrameEntry { Paper = PaperPresets.Get(best) }, false).ToString("0.##") + "）";
            }
            else
            {
                _paper.SelectedIndex = PaperPresets.Names.Length - 1;  // 自定义
                _w.Value = (decimal)Math.Min(10000, Math.Max(1, w));
                _h.Value = (decimal)Math.Min(10000, Math.Max(1, h));
                _conf.Text += "    幅面不标准，已填为自定义尺寸，请核对";
            }
        }

        /// <summary>
        /// 在图上框一个矩形 → 列出区域内全部候选文字 → 用户点选确认哪段是图号/图名 →
        /// 记住「那段文字的中心点」作为相对图框的归一化锚点。批打时按同一位置到每张图找最近文字。
        /// </summary>
        private void PickRegion(string field)
        {
            if (!_pickedWcs.HasValue)
            { Ui.Warn("请先点【选择图框】选中一个图框，再点取范围。"); return; }
            Extents2d frame = _pickedWcs.Value;
            double fw = frame.MaxPoint.X - frame.MinPoint.X, fh = frame.MaxPoint.Y - frame.MinPoint.Y;
            if (fw <= 0 || fh <= 0) { Ui.Warn("图框范围无效，请重新选择图框。"); return; }

            string label = field == "drawno" ? "图号 (A)" : "图名 (C)";
            List<Extents2d> rects;
            using (Ui.StartInteraction(this))
            {
                AcadUtil.Say("请框出【" + label + "】文字所在的小区域（尽量只框一行）");
                rects = AcadUtil.PickRegionsWcs("点取区域第一角点: ", "点取区域另一角点: ", false);
            }
            if (rects == null || rects.Count == 0) { _conf.Text = "已取消点取范围"; return; }
            Extents2d r = rects[0];

            // 列出候选文字让用户点选确认，返回所选文字的中心点
            List<Tuple<string, Point3d>> cands = AttrReader.ListRegionTexts(r);
            if (cands.Count == 0)
            {
                _conf.Text = "该区域未读到文字，请重新框选（要框住文字本身）";
                Ui.Warn("框选区域里没有文字。请把小区域框在文字上再试。");
                return;
            }
            Point3d chosen;
            string val = AskPickText(label, cands, out chosen);
            if (val == null) { _conf.Text = "已取消点选"; return; }   // 用户取消

            double x0 = (r.MinPoint.X - frame.MinPoint.X) / fw;
            double y0 = (r.MinPoint.Y - frame.MinPoint.Y) / fh;
            double x1 = (r.MaxPoint.X - frame.MinPoint.X) / fw;
            double y1 = (r.MaxPoint.Y - frame.MinPoint.Y) / fh;
            x0 = Math.Max(0, Math.Min(1, x0)); x1 = Math.Max(0, Math.Min(1, x1));
            y0 = Math.Max(0, Math.Min(1, y0)); y1 = Math.Max(0, Math.Min(1, y1));
            double ax = (chosen.X - frame.MinPoint.X) / fw;
            double ay = (chosen.Y - frame.MinPoint.Y) / fh;
            ax = Math.Max(0, Math.Min(1, ax)); ay = Math.Max(0, Math.Min(1, ay));

            if (field == "drawno")
            {
                _dnX0 = x0; _dnY0 = y0; _dnX1 = x1; _dnY1 = y1;
                _dnAx = ax; _dnAy = ay; _dnSample = val; _useDn.Checked = true;
            }
            else
            {
                _ttX0 = x0; _ttY0 = y0; _ttX1 = x1; _ttY1 = y1;
                _ttAx = ax; _ttAy = ay; _ttSample = val; _useTt.Checked = true;
            }
            UpdateRegionInfo();
            _conf.Text = "已确认" + label + "：\"" + val + "\"，位置已记住（批打时按同一位置识别每张图）";
        }

        /// <summary>候选文字点选窗：列出框选区域内的全部文字，用户点一个确认，返回其文字与中心点。</summary>
        private string AskPickText(string label, List<Tuple<string, Point3d>> cands, out Point3d chosen)
        {
            chosen = Point3d.Origin;
            Form f = new Form
            {
                Text = "确认 " + label, Font = Ui.Font9,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                ClientSize = new Size(460, 300)
            };
            ListBox lb = new ListBox { Dock = DockStyle.Fill, Font = Ui.Font9 };
            foreach (Tuple<string, Point3d> t in cands) lb.Items.Add(t);
            if (lb.Items.Count > 0) lb.SelectedIndex = 0;
            string result = null;
            int picked = -1;
            FlowLayoutPanel p = new FlowLayoutPanel
            { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(10) };
            p.Controls.Add(Ui.L("框选区域内读到 " + cands.Count + " 段文字，点选哪一段是【" + label + "】？"));
            p.Controls.Add(lb);
            FlowLayoutPanel b = new FlowLayoutPanel
            { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            b.Controls.Add(Ui.B("取消", delegate { f.Close(); }, 80));
            b.Controls.Add(Ui.B("确认", delegate
            {
                if (lb.SelectedIndex >= 0) picked = lb.SelectedIndex;
                f.Close();
            }, 80));
            f.Controls.Add(p); f.Controls.Add(b);
            using (f) f.ShowDialog(this);
            if (picked < 0) return null;
            chosen = ((Tuple<string, Point3d>)lb.Items[picked]).Item2;
            result = ((Tuple<string, Point3d>)lb.Items[picked]).Item1;
            return result;
        }

        private void ClearRegions()
        {
            _dnX0 = _dnY0 = _dnX1 = _dnY1 = -1;
            _ttX0 = _ttY0 = _ttX1 = _ttY1 = -1;
            _dnAx = _dnAy = _ttAx = _ttAy = -1;
            _dnSample = _ttSample = "";
            _useDn.Checked = false; _useTt.Checked = false;
            UpdateRegionInfo();
        }

        private void UpdateRegionInfo()
        {
            _regionInfo.Text = "图号 (A)：" + (HasDn ? ("已确认 \"" + _dnSample + "\"  位置(" + _dnAx.ToString("0.00") + "," + _dnAy.ToString("0.00") + ")") : "未点取")
                + "    图名 (C)：" + (HasTt ? ("已确认 \"" + _ttSample + "\"  位置(" + _ttAx.ToString("0.00") + "," + _ttAy.ToString("0.00") + ")") : "未点取");
        }

        private bool HasDn { get { return _useDn.Checked && _dnAx >= 0 && _dnX0 >= 0; } }
        private bool HasTt { get { return _useTt.Checked && _ttAx >= 0 && _ttX0 >= 0; } }

        /// <summary>从已录图框复制信息（图2底部按钮）：弹窗选一个已录图框，把它的配置载入当前表单。</summary>
        private void CopyFromExisting()
        {
            FrameLibrary lib = FrameLibrary.Load();
            if (lib == null || lib.Frames == null || lib.Frames.Count == 0)
            { Ui.Warn("还没有已录入的图框可复制"); return; }
            Form f = new Form
            {
                Text = "从已录图框复制", Font = Ui.Font9,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                ClientSize = new Size(420, 280)
            };
            ListBox lb = new ListBox { Dock = DockStyle.Fill, Font = Ui.Font9 };
            foreach (FrameEntry fe in lib.Frames)
                lb.Items.Add(fe);
            FrameEntry picked = null;
            FlowLayoutPanel p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(10) };
            p.Controls.Add(Ui.L("选一个已录图框，复制它的全部配置（块名会保留，记得改）："));
            p.Controls.Add(lb);
            FlowLayoutPanel b = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            b.Controls.Add(Ui.B("取消", delegate { f.Close(); }, 80));
            b.Controls.Add(Ui.B("复制", delegate
            {
                if (lb.SelectedIndex >= 0) picked = lb.SelectedItem as FrameEntry;
                f.Close();
            }, 80));
            f.Controls.Add(p); f.Controls.Add(b);
            using (f) f.ShowDialog(this);
            if (picked == null) return;
            LoadFrom(picked);
            _conf.Text = "已从图框 \"" + picked.BlockName + "\" 复制配置，请核对块名";
        }

        private void UpdatePreview()
        {
            if (_preview == null) return;
            GlobalConfig c = GlobalConfig.Current;
            _preview.Text = "示例文件名：" + Naming.Preview(_tpl.Text, c) + "    " + Naming.ValidateTemplate(_tpl.Text, c);
        }

        /// <summary>双击属性列表里的 Tag，把它预填到 A / B / C 之一。不用 VB InputBox，免多引一个程序集。</summary>
        private string AskTagTarget()
        {
            Form f = new Form
            {
                Text = "拾取属性 Tag", Font = Ui.Font9,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                ClientSize = new Size(340, 150)
            };
            ComboBox cb = Ui.D(new[] { "图号 (A)", "版次 (B)", "图名 (C)" }, 200, 0);
            string result = "";
            FlowLayoutPanel p = new FlowLayoutPanel
            { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(12) };
            p.Controls.Add(Ui.L("把这个 Tag 填到哪个字段？"));
            p.Controls.Add(cb);
            FlowLayoutPanel b = new FlowLayoutPanel
            { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            b.Controls.Add(Ui.B("确定", delegate
            {
                result = new[] { "A", "B", "C" }[Math.Max(0, Math.Min(2, cb.SelectedIndex))];
                f.Close();
            }));
            b.Controls.Add(Ui.B("取消", delegate { f.Close(); }));
            f.Controls.Add(p); f.Controls.Add(b);
            using (f) f.ShowDialog(this);
            return result;
        }

        private void Accept()
        {
            if (string.IsNullOrWhiteSpace(_block.Text))
            { Ui.Warn("请填图框块名，或点【框选图框】自动拾取"); return; }
            PaperSpec paper = PaperPresets.Get(_paper.Text);
            paper.WidthMm = (double)_w.Value; paper.HeightMm = (double)_h.Value;
            Result = new FrameEntry
            {
                BlockName = _block.Text.Trim(),
                Paper = paper,
                Priority = (int)_prio.Value,
                NameTemplate = string.IsNullOrWhiteSpace(_tpl.Text) ? Naming.DefaultTemplate : _tpl.Text.Trim(),
                TagDrawNo = _tagA.Text.Trim(), TagRev = _tagB.Text.Trim(), TagTitle = _tagC.Text.Trim(),
                ReadDrawNo = _readA.Checked, ReadRev = _readB.Checked, ReadTitle = _readC.Checked,
                ScaleNumerator = (double)_scaleNum.Value, ScaleDenominator = (double)_scaleDen.Value,
                TitleBarHint = _hint.Text, Note = _note.Text.Trim(),
                DnX0 = HasDn ? _dnX0 : -1, DnY0 = HasDn ? _dnY0 : -1,
                DnX1 = HasDn ? _dnX1 : -1, DnY1 = HasDn ? _dnY1 : -1,
                TtX0 = HasTt ? _ttX0 : -1, TtY0 = HasTt ? _ttY0 : -1,
                TtX1 = HasTt ? _ttX1 : -1, TtY1 = HasTt ? _ttY1 : -1,
                DnAnchorX = HasDn ? _dnAx : -1, DnAnchorY = HasDn ? _dnAy : -1,
                TtAnchorX = HasTt ? _ttAx : -1, TtAnchorY = HasTt ? _ttAy : -1,
                DnSample = HasDn ? _dnSample : "", TtSample = HasTt ? _ttSample : ""
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
