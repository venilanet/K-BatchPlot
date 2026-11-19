using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using Extents2d = Autodesk.AutoCAD.DatabaseServices.Extents2d;
using AcCore = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MPlot
{
    // ================================================================ 控件小工厂 / CAD 交互
    internal static class Ui
    {
        public static readonly Font Font9 = new Font("Microsoft YaHei", 9F);

        public static Label L(string text, int w = 0)
        {
            Label l = new Label { Text = text, AutoSize = w == 0, Font = Font9, Padding = new Padding(0, 6, 4, 0) };
            if (w > 0) { l.AutoSize = false; l.Width = w; }
            return l;
        }
        public static TextBox T(string value, int w = 160)
        {
            return new TextBox { Text = value ?? "", Width = w, Font = Font9 };
        }
        public static CheckBox C(string text, bool v)
        {
            return new CheckBox { Text = text, Checked = v, AutoSize = true, Font = Font9, Padding = new Padding(0, 4, 8, 0) };
        }
        public static ComboBox D(IEnumerable<string> items, int w = 180, int selected = 0)
        {
            ComboBox c = new ComboBox { Width = w, Font = Font9, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (string s in items) c.Items.Add(s);
            if (c.Items.Count > 0) c.SelectedIndex = Math.Max(0, Math.Min(selected, c.Items.Count - 1));
            return c;
        }
        public static NumericUpDown N(double value, double min, double max, int decimals = 1, int w = 90)
        {
            NumericUpDown n = new NumericUpDown
            {
                Width = w, Font = Font9, DecimalPlaces = decimals,
                Minimum = (decimal)min, Maximum = (decimal)max,
                Increment = decimals == 0 ? 1 : (decimal)Math.Pow(10, -decimals),
                Value = (decimal)Math.Max(min, Math.Min(max, value))
            };
            return n;
        }
        public static Button B(string text, EventHandler click, int w = 100)
        {
            Button b = new Button { Text = text, Width = w, Height = 30, Font = Font9 };
            if (click != null) b.Click += click;
            return b;
        }
        public static FlowLayoutPanel Row(params Control[] items)
        {
            FlowLayoutPanel p = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 2, 0, 2), Font = Font9
            };
            foreach (Control c in items) p.Controls.Add(c);
            return p;
        }
        public static GroupBox G(string title)
        {
            return new GroupBox { Text = title, Font = Font9, AutoSize = true, Padding = new Padding(8) };
        }

        /// <summary>
        /// 模态对话框里要回 CAD 点选对象/框选时，必须用 Editor.StartUserInteraction：
        /// 它临时让出 CAD 交互权、结束后自动恢复窗体。直接 Hide/Show 在 ShowDialog 的消息循环里会卡住。
        /// </summary>
        public static IDisposable StartInteraction(Form f)
        {
            try
            {
                Editor ed = AcadUtil.Ed;
                if (ed != null)
                {
                    MethodInfo m = ed.GetType().GetMethod("StartUserInteraction", new[] { typeof(Form) });
                    if (m != null)
                    {
                        object o = m.Invoke(ed, new object[] { f });
                        IDisposable d = o as IDisposable;
                        if (d != null) return d;
                    }
                }
            }
            catch (Exception ex) { Logger.Verbose("StartUserInteraction 不可用，改用 Hide/Show：" + ex.Message); }
            f.Hide();
            return new HideRestore(f);
        }

        private sealed class HideRestore : IDisposable
        {
            private readonly Form _f;
            internal HideRestore(Form f) { _f = f; }
            public void Dispose() { try { _f.Show(); _f.BringToFront(); } catch { } }
        }

        public static void Info(string msg, string title = "K-批量打印")
        { MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        public static void Warn(string msg, string title = "K-批量打印")
        { MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // ================================================================ 进度
    public sealed class ProgressForm : Form
    {
        private readonly Label _msg = Ui.L("准备中…", 0);
        private readonly ProgressBar _bar = new ProgressBar { Width = 480, Height = 20 };
        public bool CancelRequested { get; private set; }

        /// <summary>结果窗里点了「重试失败项」/「重新合并」要再跑一轮时，先把上轮的取消状态清掉。</summary>
        public void ResetCancel() { CancelRequested = false; }

        public ProgressForm(string title)
        {
            Text = title; Font = Ui.Font9;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(520, 130);
            _msg.AutoSize = false; _msg.Width = 490; _msg.Height = 36; _msg.Padding = new Padding(6);
            Button cancel = Ui.B("取消", delegate { CancelRequested = true; }, 90);
            FlowLayoutPanel p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(12) };
            p.Controls.Add(_msg); p.Controls.Add(_bar); p.Controls.Add(cancel);
            Controls.Add(p);
        }
        public void SetProgress(int cur, int total, string msg)
        {
            _msg.Text = msg ?? "";
            _bar.Maximum = Math.Max(1, total);
            _bar.Value = Math.Min(_bar.Maximum, Math.Max(0, cur));
        }
    }

    // ================================================================ 结果
    /// <summary>关闭结果窗后调用方要做的事：无 / 只重试失败项 / 只重跑合并。</summary>
    public enum ResultAction { None, RetryFailed, Remerge }

    // ================================================================ 结果
    public sealed class ResultForm : Form
    {
        private readonly BatchResult _r;
        private readonly DataGridView _grid = new DataGridView();
        private readonly ComboBox _filter;
        public ResultAction Action { get; private set; }

        public ResultForm(BatchResult r, string title)
        {
            _r = r;
            Action = ResultAction.None;
            Text = title + " · 结果"; Font = Ui.Font9;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(880, 520);
            MinimizeBox = false;

            Label head = Ui.L("", 0);
            head.AutoSize = false; head.Width = 860; head.Height = 66; head.Padding = new Padding(10, 8, 10, 0);
            string h = string.Format("成功 {0} 张 / 失败 {1} 张 / 跳过 {2} 张{3}\r\n输出目录：{4}",
                r.Success, r.Failed, r.Skipped, r.Canceled ? "（已取消）" : "", r.OutputDir);
            if (!string.IsNullOrEmpty(r.MergedPath)) h += "\r\nPDF 合集：" + Path.GetFileName(r.MergedPath);
            else if (r.MergeFailed) h += "\r\nPDF 合集：合并失败（单页 PDF 已保留，可点「重新合并」）";
            if (!string.IsNullOrEmpty(r.CsvPath)) h += "　　图纸目录：" + Path.GetFileName(r.CsvPath);
            if (!string.IsNullOrEmpty(r.SummaryFile)) h += "　　结果清单：" + Path.GetFileName(r.SummaryFile);
            head.Text = h;

            _filter = Ui.D(new[] { "全部", "成功", "失败", "已跳过", "待导出" }, 110, 0);
            _filter.SelectedIndexChanged += delegate { ApplyFilter(); };

            FlowLayoutPanel top = new FlowLayoutPanel
            { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 2) };
            top.Controls.Add(head);
            top.Controls.Add(Ui.Row(Ui.L("按状态筛选", 80), _filter,
                Ui.L("提示：失败项可点下方「重试失败项」，只重出失败的几张；合集失败可点「重新合并」。", 560)));

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.RowHeadersVisible = false;
            _grid.Font = Ui.Font9;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.ReadOnly = true;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.Columns.Add("cNo", "序号"); _grid.Columns["cNo"].Width = 46;
            _grid.Columns.Add("cDn", "图号"); _grid.Columns["cDn"].Width = 130;
            _grid.Columns.Add("cTitle", "图名"); _grid.Columns["cTitle"].Width = 200;
            _grid.Columns.Add("cFile", "文件名"); _grid.Columns["cFile"].Width = 240;
            _grid.Columns.Add("cStatus", "状态"); _grid.Columns["cStatus"].Width = 70;
            _grid.Columns.Add("cErr", "错误信息 / 备注"); _grid.Columns["cErr"].Width = 180;
            FillGrid();

            TextBox warn = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Bottom, Height = 76, Font = Ui.Font9, WordWrap = true
            };
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string w in r.Warnings) sb.AppendLine("[提示] " + w);
            foreach (string f in r.PreviewFailed) sb.AppendLine("[预览失败] " + f);
            warn.Text = sb.ToString();

            FlowLayoutPanel bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            Button retry = Ui.B("重试失败项", delegate { Action = ResultAction.RetryFailed; Close(); }, 120);
            retry.Enabled = r.Failed > 0;
            Button remerge = Ui.B("重新合并", delegate { Action = ResultAction.Remerge; Close(); }, 100);
            remerge.Enabled = r.MergeFailed;
            Button csv = Ui.B("导出CSV结果清单", delegate { ExportResultCsv(); }, 150);
            Button close = Ui.B("关闭", delegate { Close(); });
            Button open = Ui.B("打开输出目录", delegate
            {
                try { if (!string.IsNullOrEmpty(r.OutputDir) && Directory.Exists(r.OutputDir)) Process.Start(r.OutputDir); }
                catch (Exception ex) { Ui.Warn(ex.Message); }
            }, 130);
            Button log = Ui.B("查看日志", delegate
            {
                try { if (File.Exists(Logger.SessionFile)) Process.Start(Logger.SessionFile); }
                catch (Exception ex) { Ui.Warn(ex.Message); }
            });
            bottom.Controls.Add(close); bottom.Controls.Add(csv); bottom.Controls.Add(remerge);
            bottom.Controls.Add(retry); bottom.Controls.Add(open); bottom.Controls.Add(log);
            Controls.Add(bottom); Controls.Add(warn); Controls.Add(top); Controls.Add(_grid);
        }

        private void FillGrid()
        {
            _grid.Rows.Clear();
            foreach (SheetItem s in _r.Sheets)
            {
                int row = _grid.Rows.Add(
                    s.Index.ToString("00"),
                    s.DrawNo ?? "",
                    s.Title ?? "",
                    Naming.FullFileName(s),
                    SheetStatusNames.Text(s.Status),
                    s.Status == SheetStatus.Failed || s.Status == SheetStatus.Skipped ? (s.Error ?? "") : "");
                DataGridViewRow gr = _grid.Rows[row];
                gr.Tag = s.Status;
                if (s.Status == SheetStatus.Success) gr.Cells[4].Style.ForeColor = Color.Green;
                else if (s.Status == SheetStatus.Failed) gr.Cells[4].Style.ForeColor = Color.Red;
                else if (s.Status == SheetStatus.Skipped) gr.Cells[4].Style.ForeColor = Color.Gray;
            }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string f = _filter == null ? "全部" : _filter.Text;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                SheetStatus st = row.Tag is SheetStatus ? (SheetStatus)row.Tag : SheetStatus.Pending;
                bool show = f == "全部"
                    || (f == "成功" && st == SheetStatus.Success)
                    || (f == "失败" && st == SheetStatus.Failed)
                    || (f == "已跳过" && st == SheetStatus.Skipped)
                    || (f == "待导出" && (st == SheetStatus.Pending || st == SheetStatus.Exporting));
                row.Visible = show;
            }
        }

        private void ExportResultCsv()
        {
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = "导出结果_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv"
            };
            if (DialogResult.OK != sfd.ShowDialog(this)) return;
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("\uFEFF");
                sb.Append("序号,图号,图名,文件名,状态,错误信息,PDF路径\r\n");
                foreach (SheetItem s in _r.Sheets)
                {
                    sb.Append(Csv(s.Index.ToString("00"))).Append(',').Append(Csv(s.DrawNo)).Append(',')
                      .Append(Csv(s.Title)).Append(',').Append(Csv(Naming.FullFileName(s))).Append(',')
                      .Append(Csv(SheetStatusNames.Text(s.Status))).Append(',').Append(Csv(s.Error)).Append(',')
                      .Append(Csv(s.PdfPath)).Append("\r\n");
                }
                File.WriteAllText(sfd.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
                MessageBox.Show("结果清单已导出：" + sfd.FileName, "导出CSV结果清单", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
    }

    // ================================================================ 单张打印
    public sealed class SinglePlotForm : Form
    {
        private readonly TextBox _stem = Ui.T("", 260);
        private readonly ComboBox _paper;
        private readonly NumericUpDown _w, _h;
        private readonly ComboBox _scale;
        private readonly NumericUpDown _den;
        public bool Confirmed { get; private set; }
        public string FileStem { get { return _stem.Text.Trim(); } }
        public ScaleMode Mode { get; private set; }
        public double ScaleDenominator { get { return (double)_den.Value; } }
        public PaperSpec Paper { get; private set; }

        public SinglePlotForm()
        {
            Text = "单张打印到文件"; Font = Ui.Font9;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(470, 340);

            _stem.Text = "单张图纸_" + DateTime.Now.ToString("yyyyMMdd_HHmm");
            _paper = Ui.D(PaperPresets.Names, 200, 1);
            _paper.SelectedIndexChanged += delegate
            {
                PaperSpec spec = PaperPresets.Get(_paper.Text);
                _w.Value = (decimal)spec.WidthMm; _h.Value = (decimal)spec.HeightMm;
                _w.Enabled = _h.Enabled = spec.IsCustom;
            };
            _w = Ui.N(841, 1, 10000, 1); _h = Ui.N(594, 1, 10000, 1);
            _w.Enabled = _h.Enabled = false;
            _scale = Ui.D(new[] { "自动按图框比例", "布满纸张 (ScaleToFit)", "1:1", "自定义比例" }, 200, 0);
            _den = Ui.N(100, 0.01, 100000, 2);

            FlowLayoutPanel p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(12) };
            p.Controls.Add(Ui.L("点【确定】后，会在 CAD 里请你框选两角点作为打印窗口。", 440));
            p.Controls.Add(Ui.Row(Ui.L("文件名", 70), _stem));
            p.Controls.Add(Ui.Row(Ui.L("纸张", 70), _paper, Ui.L("宽mm"), _w, Ui.L("高mm"), _h));
            p.Controls.Add(Ui.Row(Ui.L("比例", 70), _scale, Ui.L("1 :"), _den));
            p.Controls.Add(Ui.L("提示：图框在模型里放大画时，选「布满纸张」最省事；", 440));
            p.Controls.Add(Ui.L("      想保持精确出图比例，用「自定义比例」填 1:100 之类。", 440));
            FlowLayoutPanel b = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(6) };
            b.Controls.Add(Ui.B("确定", delegate { Accept(); }));
            b.Controls.Add(Ui.B("取消", delegate { Close(); }));
            Controls.Add(p); Controls.Add(b);
        }

        private void Accept()
        {
            Paper = PaperPresets.Get(_paper.Text);
            if (Paper.IsCustom) { Paper.WidthMm = (double)_w.Value; Paper.HeightMm = (double)_h.Value; }
            switch (_scale.SelectedIndex)
            {
                case 1: Mode = ScaleMode.ScaleToFit; break;
                case 2: Mode = ScaleMode.OneToOne; break;
                case 3: Mode = ScaleMode.Custom; break;
                default: Mode = ScaleMode.AutoFromFrame; break;
            }
            Confirmed = true;
            Close();
        }
    }

    // ================================================================ 打印设置
    public sealed class SettingsForm : Form
    {
        private readonly ComboBox _dev, _ctb, _colorMode, _scale, _printer;
        private readonly CheckBox _styles, _lw, _center, _rot;
        private readonly NumericUpDown _ox, _oy, _margin, _num, _den, _copies;

        public SettingsForm()
        {
            Text = "打印设置"; Font = Ui.Font9; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 470); MinimizeBox = false;
            GlobalConfig c = GlobalConfig.Current;

            List<string> devices = AcadUtil.GetPlotDevices();
            if (!devices.Contains(c.PlotDevice)) devices.Insert(0, c.PlotDevice);
            _dev = Ui.D(devices, 330, Math.Max(0, devices.IndexOf(c.PlotDevice)));
            List<string> ctbs = AcadUtil.GetPlotStyleSheets();
            if (!ctbs.Contains(c.StyleSheet)) ctbs.Insert(0, c.StyleSheet);
            _ctb = Ui.D(ctbs, 230, Math.Max(0, ctbs.IndexOf(c.StyleSheet)));
            _colorMode = Ui.D(new[] { "按 CTB 自行选择", "彩色（不指定 CTB）", "黑白 monochrome.ctb", "灰度 Grayscale.ctb" },
                              260, (int)c.ColorMode);
            _colorMode.SelectedIndexChanged += delegate
            {
                if (_colorMode.SelectedIndex == 1) SelectCtb("");
                else if (_colorMode.SelectedIndex == 2) SelectCtb("monochrome.ctb");
                else if (_colorMode.SelectedIndex == 3) SelectCtb("Grayscale.ctb");
            };
            List<string> printers = AcadUtil.GetPlotDevices();
            printers.Insert(0, "");
            _printer = Ui.D(printers, 300, Math.Max(0, printers.IndexOf(c.DefaultPrinter ?? "")));

            _styles = Ui.C("按打印样式打印（CTB 生效）", c.PlotWithPlotStyles);
            _lw = Ui.C("打印线宽", c.PlotWithLineweights);
            _center = Ui.C("居中打印", c.PlotCentered);
            _rot = Ui.C("图框旋转 90° 时自动旋转纸张", c.RotateToBestFit);
            _ox = Ui.N(c.PlotOffsetX, -500, 500, 1); _oy = Ui.N(c.PlotOffsetY, -500, 500, 1);
            _margin = Ui.N(c.WindowMarginMm, 0, 100, 1);
            _num = Ui.N(c.CustomScaleNumerator, 0.01, 10000, 2);
            _den = Ui.N(c.CustomScaleDenominator, 0.01, 100000, 2);
            _copies = Ui.N(c.PaperCopies, 1, 99, 0);
            // 顺序必须与 ScaleMode 枚举一致：AutoFromFrame / OneToOne / ScaleToFit / Custom
            _scale = Ui.D(new[] { "自动按图框反算比例（推荐）", "强制 1:1", "布满纸张 ScaleToFit", "自定义比例" },
                          260, (int)c.ScaleMode);

            FlowLayoutPanel p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true };
            p.Controls.Add(Ui.Row(Ui.L("PDF 打印机(PC3)", 120), _dev));
            p.Controls.Add(Ui.Row(Ui.L("打印样式 CTB", 120), _ctb));
            p.Controls.Add(Ui.Row(Ui.L("颜色输出", 120), _colorMode,
                                  Ui.L("快捷预设，选完可直接改 CTB")));
            p.Controls.Add(Ui.Row(_styles, _lw, _center));
            p.Controls.Add(Ui.Row(Ui.L("打印偏移 X/Y (mm)", 120), _ox, _oy));
            p.Controls.Add(Ui.Row(Ui.L("四边留白 (mm)", 120), _margin,
                                  Ui.L("纸张不变、内容缩小留白，默认 2mm；设 0 则贴边")));
            p.Controls.Add(Ui.Row(Ui.L("出图比例", 120), _scale));
            p.Controls.Add(Ui.Row(Ui.L("自定义比例", 120), Ui.L("分子"), _num, Ui.L("分母"), _den));
            p.Controls.Add(Ui.Row(_rot));
            p.Controls.Add(Ui.L("── 批打到纸张 ──"));
            p.Controls.Add(Ui.Row(Ui.L("默认打印机", 120), _printer));
            p.Controls.Add(Ui.Row(Ui.L("打印份数", 120), _copies));
            p.Controls.Add(Ui.L("纸张尺寸由图框库每条的「对应纸张」决定；PC3 里没有该尺寸时会选最接近的大一号纸。"));

            FlowLayoutPanel b = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(6) };
            b.Controls.Add(Ui.B("保存", delegate { Save(); }));
            b.Controls.Add(Ui.B("取消", delegate { Close(); }));
            b.Controls.Add(Ui.B("刷新设备列表", delegate { PaperMatcher.InvalidateCache(); Ui.Info("已刷新，重开本窗口生效"); }));
            Controls.Add(p); Controls.Add(b);
        }

        private void SelectCtb(string name)
        {
            for (int i = 0; i < _ctb.Items.Count; i++)
                if (string.Equals(_ctb.Items[i].ToString(), name, StringComparison.OrdinalIgnoreCase))
                { _ctb.SelectedIndex = i; return; }
            if (name == "") { _ctb.Text = ""; return; }
            _ctb.Items.Insert(0, name); _ctb.SelectedIndex = 0;
        }

        private void Save()
        {
            GlobalConfig c = GlobalConfig.Current;
            c.PlotDevice = _dev.Text; c.StyleSheet = _ctb.Text;
            c.ColorMode = (ColorMode)Math.Max(0, _colorMode.SelectedIndex);
            c.PlotWithPlotStyles = _styles.Checked; c.PlotWithLineweights = _lw.Checked;
            c.PlotCentered = _center.Checked; c.RotateToBestFit = _rot.Checked;
            c.PlotOffsetX = (double)_ox.Value; c.PlotOffsetY = (double)_oy.Value;
            c.WindowMarginMm = (double)_margin.Value;
            c.ScaleMode = (ScaleMode)_scale.SelectedIndex;
            c.CustomScaleNumerator = (double)_num.Value; c.CustomScaleDenominator = (double)_den.Value;
            c.DefaultPrinter = _printer.Text; c.PaperCopies = (int)_copies.Value;
            c.Save(); PaperMatcher.InvalidateCache();
            Ui.Info("打印设置已保存"); Close();
        }
    }

    // ================================================================ 其余设置
    public sealed class MiscSettingsForm : Form
    {
        private readonly TextBox _out, _gs, _merged, _csv, _prefix, _date;
        private readonly CheckBox _merge, _overwrite, _keep, _bg, _csvOn, _table, _askPt, _desc;
        private readonly ComboBox _mergeMode, _sort;
        private readonly NumericUpDown _textH;

        public MiscSettingsForm()
        {
            Text = "其余设置"; Font = Ui.Font9; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 560); MinimizeBox = false;
            GlobalConfig c = GlobalConfig.Current;

            _out = Ui.T(c.OutputDir, 330);
            _gs = Ui.T(c.GhostscriptPath, 330);
            _merged = Ui.T(c.MergedFileName, 200);
            _csv = Ui.T(c.CatalogCsvName, 200);
            _prefix = Ui.T(c.ProjectPrefix, 160);
            _date = Ui.T(c.DateFormat, 120);
            _merge = Ui.C("自动合并为一份图纸合集 PDF", c.MergePdf);
            _overwrite = Ui.C("同名文件直接覆盖", c.OverwriteExisting);
            _keep = Ui.C("合并后保留单页 PDF（推荐）", c.KeepSinglePdfsAfterMerge);
            _bg = Ui.C("关闭后台打印（推荐，保证合并前文件已落盘）", c.DisableBackgroundPlot);
            _csvOn = Ui.C("生成图纸目录 CSV（Excel 直接打开）", c.CatalogCsv);
            _table = Ui.C("在当前 DWG 内生成 CAD 原生 TABLE 目录", c.CatalogTable);
            _askPt = Ui.C("生成 TABLE 时询问插入点", c.TableAskForInsertPoint);
            _desc = Ui.C("目录降序", c.SortDescending);
            _textH = Ui.N(c.TableTextHeightMm, 1, 50, 1);
            _mergeMode = Ui.D(new[] { "GS 优先（失败切内置合并）", "内置合并优先（零依赖，推荐）", "只用 Ghostscript" },
                              280, (int)c.MergeMode);
            _sort = Ui.D(new[] { "按排序优先级", "按图号（数字感知）", "按布局顺序", "按图框位置（从上到下）" },
                         220, (int)c.SortMode);

            FlowLayoutPanel p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true };
            p.Controls.Add(Ui.L("── 输出 ──"));
            p.Controls.Add(Ui.Row(Ui.L("全局输出目录", 110), _out, Ui.B("浏览", delegate { BrowseFolder(_out); }, 70)));
            p.Controls.Add(Ui.L("留空 = 当前 DWG 同目录下的「PDF输出」文件夹"));
            p.Controls.Add(Ui.Row(_overwrite, _bg));
            p.Controls.Add(Ui.L("── PDF 合并 ──"));
            p.Controls.Add(Ui.Row(Ui.L("Ghostscript", 110), _gs, Ui.B("浏览", delegate { BrowseExe(_gs); }, 70),
                                  Ui.B("自动探测", delegate { DetectGs(); }, 90)));
            p.Controls.Add(Ui.Row(_merge, Ui.L("合集文件名"), _merged));
            p.Controls.Add(Ui.Row(Ui.L("合并通道", 110), _mergeMode));
            p.Controls.Add(Ui.Row(_keep));
            p.Controls.Add(Ui.L("没有 GS 也能合并（内置零依赖合并器）；所有通道都失败时单页 PDF 全部保留。"));
            p.Controls.Add(Ui.L("── 图纸目录 ──"));
            p.Controls.Add(Ui.Row(Ui.L("排序方式", 110), _sort, _desc));
            p.Controls.Add(Ui.Row(_csvOn, Ui.L("CSV 文件名"), _csv));
            p.Controls.Add(Ui.Row(_table, _askPt, Ui.L("字高mm"), _textH));
            p.Controls.Add(Ui.L("── 命名 ──"));
            p.Controls.Add(Ui.Row(Ui.L("项目前缀 P", 110), _prefix, Ui.L("日期格式 D"), _date));

            FlowLayoutPanel b = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(6) };
            b.Controls.Add(Ui.B("保存", delegate { Save(); }));
            b.Controls.Add(Ui.B("取消", delegate { Close(); }));
            Controls.Add(p); Controls.Add(b);
        }

        private void BrowseFolder(TextBox t)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
                if (d.ShowDialog() == DialogResult.OK) t.Text = d.SelectedPath;
        }
        private void BrowseExe(TextBox t)
        {
            using (OpenFileDialog d = new OpenFileDialog { Filter = "gswin64c.exe|gswin64c.exe;gswin32c.exe|所有 EXE|*.exe" })
                if (d.ShowDialog() == DialogResult.OK) t.Text = d.FileName;
        }
        private void DetectGs()
        {
            string p = PdfMerger.FindGhostscript(GlobalConfig.Current);
            if (string.IsNullOrEmpty(p)) Ui.Warn("没找到 Ghostscript，可手动指定，或直接使用内置 PdfSharp 合并通道。");
            else { _gs.Text = p; Ui.Info("已找到：" + p); }
        }
        private void Save()
        {
            GlobalConfig c = GlobalConfig.Current;
            c.OutputDir = _out.Text.Trim(); c.GhostscriptPath = _gs.Text.Trim();
            c.MergePdf = _merge.Checked; c.MergedFileName = _merged.Text.Trim();
            c.MergeMode = (MergeMode)Math.Min(2, _mergeMode.SelectedIndex);
            c.OverwriteExisting = _overwrite.Checked; c.KeepSinglePdfsAfterMerge = _keep.Checked;
            c.DisableBackgroundPlot = _bg.Checked;
            c.CatalogCsv = _csvOn.Checked; c.CatalogCsvName = _csv.Text.Trim();
            c.CatalogTable = _table.Checked; c.TableAskForInsertPoint = _askPt.Checked;
            c.TableTextHeightMm = (double)_textH.Value;
            c.SortMode = (SortMode)_sort.SelectedIndex; c.SortDescending = _desc.Checked;
            c.ProjectPrefix = _prefix.Text; c.DateFormat = _date.Text.Trim();
            c.Save(); Ui.Info("其余设置已保存"); Close();
        }
    }

    // ================================================================ 辅助功能
    public sealed class ToolsForm : Form
    {
        private readonly TextBox _out = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            Width = 540, Height = 300, Font = new Font("Consolas", 9F), WordWrap = false
        };

        public ToolsForm()
        {
            Text = "辅助功能"; Font = Ui.Font9; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(590, 450); MinimizeBox = false;
            FlowLayoutPanel p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(10), AutoScroll = true };
            p.Controls.Add(Ui.Row(
                Ui.B("检查未录入图框", delegate { CheckUnmatched(); }, 130),
                Ui.B("预演文件名", delegate { PreviewNames(); }, 110),
                Ui.B("导出完整明细", delegate { ExportDetail(); }, 120)));
            p.Controls.Add(Ui.Row(
                Ui.B("重建菜单", delegate { MenuBuilder.RebuildMenu(); Ui.Info("菜单已尝试重建"); }, 100),
                Ui.B("打开配置目录", delegate { OpenDir(); }, 120),
                Ui.B("查看日志", delegate { OpenLog(); }, 100)));
            p.Controls.Add(Ui.Row(
                Ui.B("刷新设备/纸张缓存", delegate { PaperMatcher.InvalidateCache(); Ui.Info("已刷新"); }, 160),
                Ui.B("清理临时响应文件", delegate { CleanTemp(); }, 150)));
            p.Controls.Add(Ui.L("说明：以上操作均为只读取/只生成本地文件，不会删除任何 PDF 或图纸对象。"));
            p.Controls.Add(_out);
            Controls.Add(p);
        }

        private void CheckUnmatched()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) { Ui.Warn("请先打开 DWG"); return; }
            ScanResult r = FrameScanner.Scan(doc.Database, FrameLibrary.Load(), GlobalConfig.Current);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("已录入图框匹配到 " + r.Sheets.Count + " 张");
            sb.AppendLine("未录入的图框块（" + r.UnmatchedBlockNames.Count + " 种）：");
            foreach (string s in r.UnmatchedBlockNames) sb.AppendLine("  " + s);
            foreach (string w in r.Warnings) sb.AppendLine("[提示] " + w);
            _out.Text = sb.ToString();
        }

        private void PreviewNames()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) { Ui.Warn("请先打开 DWG"); return; }
            GlobalConfig cfg = GlobalConfig.Current;
            ScanResult r = FrameScanner.Scan(doc.Database, FrameLibrary.Load(), cfg);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("序号  图号        图名                  PDF 文件名                        信息来源");
            foreach (SheetItem s in r.Sheets)
                sb.AppendLine(s.Index.ToString("00") + "    " + Pad(s.DrawNo, 11) + Pad(s.Title, 20)
                    + Pad((s.FileStem ?? "") + ".pdf", 32) + (s.MetaSource ?? ""));
            sb.AppendLine("\r\n共 " + r.Sheets.Count + " 张；这一步只扫描和命名，不打印、不写 PDF。");
            _out.Text = sb.ToString();
        }
        private static string Pad(string s, int n)
        {
            s = s ?? ""; return s.Length >= n ? s.Substring(0, n) : s + new string(' ', n - s.Length);
        }

        private void ExportDetail()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) { Ui.Warn("请先打开 DWG"); return; }
            ScanResult r = FrameScanner.Scan(doc.Database, FrameLibrary.Load(), GlobalConfig.Current);
            string path = Path.Combine(GlobalConfig.Current.EffectiveOutputDir(doc.Name), "批打明细.csv");
            if (!Directory.Exists(Path.GetDirectoryName(path))) Directory.CreateDirectory(Path.GetDirectoryName(path));
            bool ok = Catalog.WriteDetailCsv(r.Sheets, path);
            _out.Text = ok ? "明细已导出：" + path : "导出失败，详见日志";
        }

        private void OpenDir()
        { try { Process.Start(FrameLibrary.EnsureDir()); } catch (Exception ex) { Ui.Warn(ex.Message); } }
        private void OpenLog()
        { try { if (File.Exists(Logger.SessionFile)) Process.Start(Logger.SessionFile); } catch (Exception ex) { Ui.Warn(ex.Message); } }

        /// <summary>只列出，不删除；删除必须用户自己确认（本插件不擅自删用户文件）。</summary>
        private void CleanTemp()
        {
            string[] files = Directory.GetFiles(Path.GetTempPath(), "mplot_gs_*.rsp");
            _out.Text = "GS 临时响应文件 " + files.Length + " 个（未删除）：\r\n"
                + string.Join("\r\n", files) + "\r\n\r\n确认要删的话，请到 " + Path.GetTempPath() + " 手动清理。";
        }
    }

    // ================================================================ 说明窗口
    public sealed class InfoForm : Form
    {
        // 不叫 Show：避免与 Form.Show() 同名产生隐藏警告
        public static void ShowInfo(string title, string text, bool showCommands = false)
        {
            using (InfoForm f = new InfoForm(title, text, showCommands)) f.ShowDialog();
        }
        private InfoForm(string title, string text, bool showCommands)
        {
            Text = title; Font = Ui.Font9; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(650, 500); MinimizeBox = false;
            TextBox tb = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei", 10F), Text = text
            };
            FlowLayoutPanel b = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            b.Controls.Add(Ui.B("关闭", delegate { Close(); }));
            if (showCommands)
                b.Controls.Add(Ui.B("查看全部命令", delegate { Ui.Info(MenuBuilder.CommandListText()); }, 130));
            Controls.Add(tb); Controls.Add(b);
        }
    }

    // ================================================================ 纸张编辑
    public sealed class PaperEditForm : Form
    {
        private readonly ComboBox _preset;
        private readonly NumericUpDown _w, _h;
        public PaperSpec Result { get; private set; }

        public PaperEditForm(PaperSpec current)
        {
            Text = "对应纸张"; Font = Ui.Font9; StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(420, 200);
            PaperSpec c = current ?? PaperPresets.Get("A1");
            _preset = Ui.D(PaperPresets.Names, 200, Math.Max(0, Array.IndexOf(PaperPresets.Names, c.Preset ?? "A1")));
            _w = Ui.N(c.WidthMm, 1, 10000, 1); _h = Ui.N(c.HeightMm, 1, 10000, 1);
            _w.Enabled = _h.Enabled = c.IsCustom;
            _preset.SelectedIndexChanged += delegate
            {
                PaperSpec spec = PaperPresets.Get(_preset.Text);
                _w.Value = (decimal)spec.WidthMm; _h.Value = (decimal)spec.HeightMm;
                _w.Enabled = _h.Enabled = spec.IsCustom;
            };
            FlowLayoutPanel p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(12) };
            p.Controls.Add(Ui.Row(Ui.L("规格", 70), _preset));
            p.Controls.Add(Ui.Row(Ui.L("宽 mm", 70), _w, Ui.L("高 mm"), _h));
            p.Controls.Add(Ui.L("加长规格按 GB/T 14689：沿长边按短边 1/4 递增。"));
            p.Controls.Add(Ui.L("自定义尺寸若在 PC3 里不存在，会选最接近的大一号纸，比例仍按标称尺寸反算。"));
            FlowLayoutPanel b = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(6) };
            b.Controls.Add(Ui.B("确定", delegate { Accept(); }));
            b.Controls.Add(Ui.B("取消", delegate { Close(); }));
            Controls.Add(p); Controls.Add(b);
        }
        private void Accept()
        {
            Result = PaperPresets.Get(_preset.Text);
            Result.WidthMm = (double)_w.Value; Result.HeightMm = (double)_h.Value;
            DialogResult = DialogResult.OK; Close();
        }
    }

    // ================================================================ 打印前预览
    /// <summary>单张打印预览：清单里点某行的「预览」，按该行当前纸张/比例渲染一张图。
    /// 可自由放大看：滚轮以鼠标为中心缩放（10%~800%），左键拖动平移，双击在
    /// 「适合窗口」与「实际大小（100%）」之间切换；底部有放大/缩小/适合窗口/
    /// 实际大小按钮与缩放百分比。只看不导出；关闭即删临时 PNG。</summary>
    public sealed class SingleSheetPreviewForm : Form
    {
        private readonly string _png;
        private Image _img;
        private double _zoom = 1.0;
        private readonly Panel _canvas = new Panel
        { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, TabStop = true };
        private readonly PictureBox _pic = new PictureBox
        { SizeMode = PictureBoxSizeMode.StretchImage, BackColor = Color.White };
        private readonly Label _zoomLabel = new Label { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
        private bool _dragging;
        private Point _dragStart;
        private Point _scrollStart;

        public SingleSheetPreviewForm(string png, string caption)
        {
            _png = png;
            Text = "打印预览 · " + caption;
            Font = Ui.Font9;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 700);
            MinimumSize = new Size(640, 480);
            ShowIcon = false;
            try
            {
                if (!string.IsNullOrEmpty(_png) && File.Exists(_png))
                    _img = Image.FromFile(_png);
            }
            catch (Exception ex) { Logger.Verbose("预览图加载失败：" + ex.Message); }
            if (_img != null) _pic.Image = _img;

            Label info = Ui.L(caption + "（所见即导出时的打印效果）", 0);
            info.Dock = DockStyle.Top;
            info.Height = 26;
            info.Padding = new Padding(8, 5, 8, 0);
            if (_img == null) info.Text = caption + "（预览图加载失败）";

            _canvas.Controls.Add(_pic);
            _canvas.MouseWheel += Canvas_MouseWheel;
            _canvas.MouseEnter += delegate { try { _canvas.Focus(); } catch { } };
            _canvas.SizeChanged += delegate { LayoutPic(); };
            _pic.MouseWheel += Canvas_MouseWheel;
            _pic.MouseDown += Pic_MouseDown;
            _pic.MouseMove += Pic_MouseMove;
            _pic.MouseUp += Pic_MouseUp;
            _pic.DoubleClick += delegate
            {
                if (_img == null) return;
                if (Math.Abs(_zoom - 1.0) < 0.01) FitZoom();
                else SetZoom(1.0, CenterOfCanvas());
            };

            FlowLayoutPanel bottom = new FlowLayoutPanel
            { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            bottom.Controls.Add(Ui.B("关闭", delegate { Close(); }, 90));
            bottom.Controls.Add(Ui.B("实际大小 (100%)", delegate { SetZoom(1.0, CenterOfCanvas()); }, 130));
            bottom.Controls.Add(Ui.B("适合窗口", delegate { FitZoom(); }, 90));
            bottom.Controls.Add(Ui.B("缩小 (-)", delegate { SetZoom(_zoom / 1.25, CenterOfCanvas()); }, 80));
            bottom.Controls.Add(Ui.B("放大 (+)", delegate { SetZoom(_zoom * 1.25, CenterOfCanvas()); }, 80));
            bottom.Controls.Add(_zoomLabel);

            Controls.Add(_canvas);
            Controls.Add(info);
            Controls.Add(bottom);
            Shown += delegate { FitZoom(); };
        }

        private Point CenterOfCanvas()
        {
            return new Point(Math.Max(0, _canvas.ClientSize.Width / 2),
                             Math.Max(0, _canvas.ClientSize.Height / 2));
        }

        /// <summary>以画布上某点为中心缩放（滚轮/按钮都走这里）；缩放后同一图像点仍在鼠标下。</summary>
        private void SetZoom(double z, Point anchorCanvas)
        {
            if (_img == null) return;
            z = Math.Max(0.1, Math.Min(8.0, z));
            // 鼠标在图上的位置（图像像素坐标）：先换算到 _pic 坐标，再除以当前缩放
            Point picMouse = new Point(anchorCanvas.X - _pic.Location.X,
                                       anchorCanvas.Y - _pic.Location.Y);
            double ix = picMouse.X / _zoom;
            double iy = picMouse.Y / _zoom;
            _zoom = z;
            _pic.Size = new Size(
                Math.Max(1, (int)Math.Round(_img.Width * _zoom)),
                Math.Max(1, (int)Math.Round(_img.Height * _zoom)));
            LayoutPic();
            // 新的滚动偏移：让同一图像点回到鼠标下
            int nx = (int)Math.Round(ix * _zoom + _pic.Location.X - anchorCanvas.X);
            int ny = (int)Math.Round(iy * _zoom + _pic.Location.Y - anchorCanvas.Y);
            _canvas.AutoScrollPosition = new Point(Math.Max(0, nx), Math.Max(0, ny));
            UpdateZoomLabel();
        }

        /// <summary>适合窗口：整张图完整显示；图比窗口小时居中。</summary>
        private void FitZoom()
        {
            if (_img == null) return;
            double zx = (double)_canvas.ClientSize.Width / _img.Width;
            double zy = (double)_canvas.ClientSize.Height / _img.Height;
            double z = Math.Min(zx, zy);
            if (z <= 0 || double.IsNaN(z) || double.IsInfinity(z)) z = 1.0;
            z = Math.Max(0.05, Math.Min(8.0, z));
            _zoom = z;
            _pic.Size = new Size(
                Math.Max(1, (int)Math.Round(_img.Width * _zoom)),
                Math.Max(1, (int)Math.Round(_img.Height * _zoom)));
            _canvas.AutoScrollPosition = new Point(0, 0);
            UpdateZoomLabel();
            LayoutPic();
        }

        private void UpdateZoomLabel()
        {
            if (_img == null) { _zoomLabel.Text = ""; return; }
            _zoomLabel.Text = ((int)Math.Round(_zoom * 100)).ToString() + "%"
                + "  (" + _img.Width + "×" + _img.Height + " 像素)";
        }

        /// <summary>图比窗口小时居中显示；图比窗口大时顶在左上角（靠滚动条看）。</summary>
        private void LayoutPic()
        {
            if (_pic == null || _canvas == null) return;
            int x = _pic.Width < _canvas.ClientSize.Width ? (_canvas.ClientSize.Width - _pic.Width) / 2 : 0;
            int y = _pic.Height < _canvas.ClientSize.Height ? (_canvas.ClientSize.Height - _pic.Height) / 2 : 0;
            _pic.Location = new Point(Math.Max(0, x), Math.Max(0, y));
        }

        private void Canvas_MouseWheel(object sender, MouseEventArgs e)
        {
            if (_img == null) return;
            // 滚轮以鼠标为中心缩放：事件可能来自画布或图本身，统一换算到画布坐标
            Control src = sender as Control;
            Point anchor = src == _pic
                ? new Point(e.X + _pic.Location.X, e.Y + _pic.Location.Y)
                : new Point(e.X, e.Y);
            double f = e.Delta > 0 ? 1.25 : 1.0 / 1.25;
            SetZoom(_zoom * f, anchor);
        }

        private void Pic_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragStart = e.Location;
            _scrollStart = _canvas.AutoScrollPosition;
            _pic.Cursor = Cursors.SizeAll;
        }

        private void Pic_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            int nx = -_scrollStart.X - (e.X - _dragStart.X);
            int ny = -_scrollStart.Y - (e.Y - _dragStart.Y);
            _canvas.AutoScrollPosition = new Point(Math.Max(0, nx), Math.Max(0, ny));
        }

        private void Pic_MouseUp(object sender, MouseEventArgs e)
        {
            _dragging = false;
            _pic.Cursor = Cursors.Default;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (_pic.Image != null) { _pic.Image.Dispose(); _pic.Image = null; } } catch { }
            try { if (_img != null) { _img.Dispose(); _img = null; } } catch { }
            try { if (!string.IsNullOrEmpty(_png) && File.Exists(_png)) File.Delete(_png); } catch { }
            base.OnFormClosed(e);
        }
    }

    /// <summary>
    /// 打印前统一预览窗口：用与真实打印完全相同的参数渲染每张图，
    /// 左侧缩略图可点选、右侧放大查看，确认后才开始批量打印。
    /// </summary>
    public sealed class PrintPreviewForm : Form
    {
        private readonly List<PlotRequest> _reqs;
        private readonly List<SheetItem> _sheets;
        private readonly List<string> _pngs = new List<string>();
        private readonly List<int> _failed = new List<int>();
        private readonly List<string> _failedLabels = new List<string>();
        private readonly List<string> _pngErrs = new List<string>();
        private readonly FlowLayoutPanel _list = new FlowLayoutPanel
        { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Left, Width = 190,
          AutoScroll = true, Padding = new Padding(6), BackColor = Color.FromArgb(245, 245, 245) };
        private readonly PictureBox _big = new PictureBox
        { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
        private readonly Label _info = Ui.L("正在生成预览…", 0);
        private readonly Label _page = Ui.L("", 90);
        private readonly ProgressBar _prog = new ProgressBar { Width = 900, Height = 14, Dock = DockStyle.Top };
        private int _cur = 0;
        /// <summary>用户在预览窗里的决定：确认导出 / 取消任务 / 放弃预览并明确确认后直接导出。默认取消。</summary>
        public PreviewDecision Decision { get; private set; }
        /// <summary>预览生成失败的图纸（"第 N 张 · 图号 图名：原因"），供引擎记入结果。</summary>
        public List<string> FailedLabels { get { return _failedLabels; } }
        public int FailedCount { get { return _failed.Count; } }

        public PrintPreviewForm(List<PlotRequest> reqs, List<SheetItem> sheets, string project)
        {
            _reqs = reqs ?? new List<PlotRequest>();
            _sheets = sheets ?? new List<SheetItem>();
            Decision = PreviewDecision.Canceled;

            Text = "导出前预览" + (string.IsNullOrEmpty(project) ? "" : " · " + project)
                   + "（共 " + _reqs.Count + " 张）";
            Font = Ui.Font9;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 700);
            MinimumSize = new Size(760, 520);
            ShowIcon = false;

            Panel center = new Panel { Dock = DockStyle.Fill };
            _info.AutoSize = false; _info.Width = 900; _info.Height = 24;
            _info.Dock = DockStyle.Top; _info.Padding = new Padding(6, 4, 6, 0);
            center.Controls.Add(_big);
            center.Controls.Add(_info);
            center.Controls.Add(_prog);
            _big.BringToFront();

            FlowLayoutPanel bottom = new FlowLayoutPanel
            { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            Button ok = Ui.B("确认导出 PDF (" + _reqs.Count + " 张)", delegate { ConfirmExport(); }, 160);
            ok.BackColor = Color.FromArgb(28, 92, 156); ok.ForeColor = Color.White; ok.FlatStyle = FlatStyle.Flat;
            bottom.Controls.Add(ok);
            bottom.Controls.Add(Ui.B("不预览，直接导出", delegate { SkipPreview(); }, 130));
            bottom.Controls.Add(Ui.B("重新生成预览", delegate { BuildPreviews(); }, 120));
            bottom.Controls.Add(Ui.B("取消", delegate { Decision = PreviewDecision.Canceled; Close(); }, 80));
            bottom.Controls.Add(Ui.B("下一张 ›", delegate { Go(_cur + 1); }, 90));
            bottom.Controls.Add(Ui.B("‹ 上一张", delegate { Go(_cur - 1); }, 90));
            bottom.Controls.Add(_page);

            Controls.Add(center);
            Controls.Add(bottom);
            Controls.Add(_list);

            Shown += delegate { BuildPreviews(); };
        }

        /// <summary>「确认导出 PDF」：有预览失败时先请用户明确确认（列出失败图纸），再导出。</summary>
        private void ConfirmExport()
        {
            if (_failed.Count > 0)
            {
                string list = string.Join("\r\n", _failedLabels.Take(5).ToArray());
                if (_failed.Count > 5) list += "\r\n…等共 " + _failed.Count + " 张";
                DialogResult r = MessageBox.Show(
                    "有 " + _failed.Count + " 张图纸预览生成失败：\r\n" + list
                    + "\r\n\r\n确定不等这几张的预览、直接导出吗？",
                    "预览未完成", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            Decision = PreviewDecision.Confirmed;
            Close();
        }

        /// <summary>「不预览，直接导出」：用户明确放弃预览并确认后才继续导出。</summary>
        private void SkipPreview()
        {
            string tip = _failed.Count > 0
                ? "有 " + _failed.Count + " 张图纸预览生成失败，确定放弃预览、直接导出吗？"
                : "确定不检查预览、直接导出 " + _reqs.Count + " 张图纸吗？";
            DialogResult r = MessageBox.Show(tip + "\r\n（导出后请在输出目录里抽查 PDF。）",
                "放弃预览", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            Decision = PreviewDecision.ContinueWithoutPreview;
            Close();
        }

        /// <summary>释放预览图：先释放 PictureBox 持有的图片，再删临时 PNG 文件。</summary>
        private void DisposeImages()
        {
            try { if (_big.Image != null) { _big.Image.Dispose(); _big.Image = null; } } catch { }
            try
            {
                foreach (Control c in _list.Controls)
                    foreach (Control cc in c.Controls)
                    {
                        PictureBox pb = cc as PictureBox;
                        if (pb != null && pb.Image != null) { pb.Image.Dispose(); pb.Image = null; }
                    }
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            DisposeImages();
            foreach (string f in _pngs)
            { try { if (!string.IsNullOrEmpty(f) && File.Exists(f)) File.Delete(f); } catch { } }
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "MPlotPrintPreview");
                if (Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0) Directory.Delete(dir);
            }
            catch { }
            base.OnFormClosed(e);
        }

        private void BuildPreviews()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                DisposeImages();
                _pngs.Clear(); _pngErrs.Clear(); _failed.Clear(); _failedLabels.Clear();
                _list.Controls.Clear();
                _prog.Maximum = Math.Max(1, _reqs.Count);
                _prog.Value = 0;
                for (int i = 0; i < _reqs.Count; i++)
                {
                    _info.Text = "正在生成预览 " + (i + 1) + " / " + _reqs.Count + "：" + SheetLabel(i);
                    _prog.Value = i + 1;
                    Application.DoEvents();
                    string err = "";
                    string png = "";
                    try { png = Plotter.RenderPreview(_reqs[i], out err); }
                    catch (Exception ex) { err = ex.Message; Logger.Verbose("预览第 " + (i + 1) + " 张失败：" + ex.Message); }
                    if (string.IsNullOrEmpty(png))
                    {
                        _failed.Add(i);
                        _failedLabels.Add("第 " + (i + 1) + " 张 · " + SheetLabel(i) + "：" + err);
                        _pngErrs.Add(err);
                    }
                    else _pngErrs.Add("");
                    _pngs.Add(png);
                    AddThumb(i, png);
                    Application.DoEvents();
                }
                _info.Text = _failed.Count == 0
                    ? "预览已生成，共 " + _reqs.Count + " 张；确认无误后点「确认导出 PDF」"
                    : "预览生成失败 " + _failed.Count + " 张（左侧红字），可点「重新生成预览」，或确认后直接导出";
                Go(0);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void AddThumb(int i, string png)
        {
            string label = SheetLabel(i);
            Panel cell = new Panel { Width = 166, Height = 120, Margin = new Padding(3),
                                     BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
            PictureBox pb = new PictureBox { Width = 156, Height = 84, Location = new Point(4, 4),
                SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand, Tag = i };
            if (!string.IsNullOrEmpty(png) && File.Exists(png))
            {
                try { using (FileStream fs = new FileStream(png, FileMode.Open, FileAccess.Read))
                        pb.Image = Image.FromStream(fs); }
                catch { }
            }
            pb.Click += delegate(object s, EventArgs e)
            {
                PictureBox me = s as PictureBox;
                if (me != null && me.Tag is int) Go((int)me.Tag);
            };
            Label cap = Ui.L((i + 1) + ". " + Trunc(label, 12), 158);
            cap.Location = new Point(4, 90);
            if (_failed.Contains(i))
            {
                cap.Text = (i + 1) + ". " + Trunc(label, 9) + "（预览失败）";
                cap.ForeColor = Color.Red;
                cell.BorderStyle = BorderStyle.FixedSingle;
            }
            cell.Controls.Add(pb); cell.Controls.Add(cap);
            _list.Controls.Add(cell);
        }

        private static string Trunc(string s, int n)
        {
            s = s ?? "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private string SheetLabel(int i)
        {
            if (i < 0 || i >= _sheets.Count) return "第" + (i + 1) + "张";
            SheetItem s = _sheets[i];
            string n = string.IsNullOrEmpty(s.DrawNo) ? ("第" + (i + 1) + "张") : s.DrawNo;
            if (!string.IsNullOrEmpty(s.Title)) n += " " + s.Title;
            return n;
        }

        private void Go(int idx)
        {
            if (_reqs.Count == 0) { _info.Text = "没有可预览的图纸"; _page.Text = ""; return; }
            _cur = Math.Max(0, Math.Min(_reqs.Count - 1, idx));
            string png = _cur < _pngs.Count ? _pngs[_cur] : "";
            try { if (_big.Image != null) { _big.Image.Dispose(); _big.Image = null; } } catch { }
            if (!string.IsNullOrEmpty(png) && File.Exists(png))
            {
                try { using (FileStream fs = new FileStream(png, FileMode.Open, FileAccess.Read))
                        _big.Image = Image.FromStream(fs); }
                catch { }
            }
            PlotRequest r = _reqs[_cur];
            string scale = r.ScaleMode == ScaleMode.ScaleToFit ? "布满纸张"
                : (r.ScaleMode == ScaleMode.OneToOne ? "1:1" : "1:" + r.ScaleDenominator.ToString("0.##"));
            string paper, orient;
            PaperInfo(_cur, out paper, out orient);
            SheetItem s = _cur < _sheets.Count ? _sheets[_cur] : null;
            _info.Text = "第 " + (_cur + 1) + " / " + _reqs.Count + " 张　|　"
                       + SheetLabel(_cur) + "　|　" + Naming.FullFileName(s) + "　|　"
                       + paper + "　|　" + scale + "　|　" + orient + "　|　"
                       + (r.Centered ? "居中" : "手动偏移") + "　|　"
                       + (r.Rotated90 ? "横向旋转90°" : "正向");
            if (string.IsNullOrEmpty(png))
            {
                string reason = _cur < _pngErrs.Count ? _pngErrs[_cur] : "";
                _info.Text += "　（此张预览生成失败" + (string.IsNullOrEmpty(reason) ? "" : "：" + reason) + "）";
            }
            _page.Text = (_cur + 1) + " / " + _reqs.Count;
        }

        /// <summary>预览窗里显示的纸张尺寸与图纸方向（与正式导出同一套纸张/旋转参数）。</summary>
        private void PaperInfo(int i, out string paper, out string orient)
        {
            paper = "纸张未定"; orient = "方向未定";
            SheetItem s = i < _sheets.Count ? _sheets[i] : null;
            double w = 0, h = 0;
            if (s != null && s.Config != null && s.Config.Paper != null)
            { w = s.Config.Paper.WidthMm; h = s.Config.Paper.HeightMm; }
            else if (i < _reqs.Count)
            {
                MediaInfo m = PaperMatcher.ParseMedia(_reqs[i].MediaName);
                if (m != null) { w = m.WidthMm; h = m.HeightMm; }
            }
            if (w <= 0 || h <= 0) return;
            if (s != null && s.Rotated) { double t = w; w = h; h = t; }
            string preset = PaperPresets.Guess(w, h).Preset;
            paper = preset + " " + w.ToString("0.#") + "×" + h.ToString("0.#") + "mm";
            orient = w >= h ? "横向" : "纵向";
        }
    }

    // ================================================================ 手动框选批量打印
    /// <summary>
    /// 手动框选模式：用户可连续框选任意多个矩形范围，每个范围一张 PDF。
    /// 图号/图名自动从框选区域读（可编辑补填），纸张默认 A3。
    /// 通用型 / 图框型两个入口共用本窗口。
    /// </summary>
    public sealed class ManualPickForm : Form
    {
        private readonly ComboBox _paper;
        private readonly NumericUpDown _w, _h;
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _hint = Ui.L("还没框选。点【在图上框选范围】后可连续框多个，回车结束。", 700);
        private readonly List<ManualPickItem> _items = new List<ManualPickItem>();
        private readonly List<Extents2d> _rects = new List<Extents2d>();
        private readonly GlobalConfig _cfg = GlobalConfig.Current;
        private readonly FlowLayoutPanel _thumbs = new FlowLayoutPanel
        { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Bottom, Height = 132,
          AutoScroll = true, Padding = new Padding(6), WrapContents = false };
        private readonly Label _thumbTip = Ui.L("缩略图：框选后自动生成，点图可放大（失败不影响出图）", 460);

        public bool PrintRequested { get; private set; }
        public PaperSpec Paper { get; private set; }
        public List<ManualPickItem> Items { get { return _items; } }

        public ManualPickForm()
        {
            Text = "手动框选批量打印"; Font = Ui.Font9;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(860, 600); MinimumSize = new Size(720, 500);

            _paper = Ui.D(PaperPresets.Names, 200, PaperPresets.Names.Contains("A3") ? 3 : 0);
            _paper.SelectedIndexChanged += delegate
            {
                PaperSpec spec = PaperPresets.Get(_paper.Text);
                _w.Value = (decimal)Math.Min(10000, Math.Max(1, spec.WidthMm));
                _h.Value = (decimal)Math.Min(10000, Math.Max(1, spec.HeightMm));
                _w.Enabled = _h.Enabled = spec.IsCustom;
            };
            _w = Ui.N(420, 1, 10000, 1); _h = Ui.N(297, 1, 10000, 1);
            _w.Enabled = _h.Enabled = false;

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false; _grid.RowHeadersVisible = false;
            _grid.Font = Ui.Font9; _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.Columns.Add("colNo", "序号");
            _grid.Columns.Add("colDn", "图号 (A)");
            _grid.Columns.Add("colTitle", "图名 (C)");
            _grid.Columns.Add("colRev", "版次 (B)");
            _grid.Columns.Add("colSize", "范围尺寸mm");
            _grid.Columns.Add("colSrc", "来源");
            _grid.Width = 820; _grid.Height = 380;
            _grid.CellEndEdit += delegate(object s, DataGridViewCellEventArgs e) { SyncRow(e.RowIndex); };

            FlowLayoutPanel p = new FlowLayoutPanel
            { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10) };
            p.Controls.Add(Ui.L("① 选纸张（默认 A3）与比例后，点【在图上框选范围】。可连续框多个，每个回车确认一个，全部框完再按回车结束。"));
            p.Controls.Add(Ui.Row(Ui.L("纸张", 60), _paper, Ui.L("宽mm"), _w, Ui.L("高mm"), _h,
                                  Ui.L("比例"), Ui.L(ScaleLabel(), 220)));
            p.Controls.Add(Ui.Row(Ui.B("在图上框选范围（可连续）", delegate { PickRanges(); }, 220),
                                  Ui.B("删除选中", delegate { DeleteSelected(); }, 100),
                                  Ui.B("清空", delegate { ClearAll(); }, 80)));
            p.Controls.Add(_hint);
            p.Controls.Add(_grid);

            FlowLayoutPanel b = new FlowLayoutPanel
            { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
            b.Controls.Add(Ui.B("开始打印", delegate { Print(); }, 110));
            b.Controls.Add(Ui.B("取消", delegate { Close(); }, 90));
            Controls.Add(p); Controls.Add(_thumbs); Controls.Add(b);
            Paper = PaperPresets.Get("A3");
        }

        private string ScaleLabel()
        {
            if (_cfg.ScaleMode == ScaleMode.Custom)
                return "1:" + (_cfg.CustomScaleDenominator > 0 ? _cfg.CustomScaleDenominator.ToString("0.##") : "100");
            if (_cfg.ScaleMode == ScaleMode.OneToOne) return "1:1";
            if (_cfg.ScaleMode == ScaleMode.ScaleToFit) return "布满纸张";
            return "自动按范围反算（推荐）";
        }

        private void PickRanges()
        {
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) { Ui.Warn("请先打开 DWG"); return; }
            List<Extents2d> got;
            using (Ui.StartInteraction(this))
            {
                AcadUtil.Say("开始框选打印范围：框一个范围后，可继续框下一个；全部框完按回车结束");
                got = AcadUtil.PickRegionsWcs(
                    "框选第 " + (_rects.Count + 1) + " 个范围 · 第一角点（回车结束）: ",
                    "框选下一范围 · 第一角点（回车结束）: ", true);
            }
            if (got == null || got.Count == 0) { _hint.Text = "本次没有框选到范围（已取消）。"; return; }
            double mmPer = AcadUtil.MmPerUnit(doc.Database);
            foreach (Extents2d r in got)
            {
                ManualPickItem it = new ManualPickItem { WindowWcs = r, MetaSource = "手动框选" };
                // 从框选区域读图号 / 图名；读不到就留空，用户在表格里补填
                it.DrawNo = AttrReader.ReadRegionValue(r, "drawno");
                it.Title = AttrReader.ReadRegionValue(r, "title");
                _rects.Add(r); _items.Add(it);
            }
            RefreshGrid();
            RefreshThumbs();
            _hint.Text = "已框选 " + _items.Count + " 个范围。图号/图名自动读到的可直接改；读不到的请双击单元格补填。";
        }

        /// <summary>为已框选范围生成缩略图并显示在一排；生成失败只跳过，不影响出图。</summary>
        private void RefreshThumbs()
        {
            _thumbs.Controls.Clear();
            if (_rects.Count == 0) return;
            List<string> pngs = new List<string>();
            try { pngs = ThumbnailMaker.Generate(_rects); }
            catch (Exception ex) { Logger.Verbose("缩略图生成异常：" + ex.Message); }

            for (int i = 0; i < _rects.Count; i++)
            {
                string png = i < pngs.Count ? pngs[i] : "";
                if (string.IsNullOrEmpty(png) || !File.Exists(png))
                {
                    _thumbs.Controls.Add(Ui.L("第" + (i + 1) + "张\n(预览失败)", 92));
                    continue;
                }
                PictureBox box = new PictureBox
                {
                    Width = 104, Height = 92, SizeMode = PictureBoxSizeMode.Zoom,
                    BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand, Tag = png
                };
                try
                {
                    // 用副本载入，避免锁定文件导致下次覆盖失败
                    using (FileStream fs = new FileStream(png, FileMode.Open, FileAccess.Read))
                        box.Image = Image.FromStream(fs);
                }
                catch { }
                box.Click += delegate(object s, EventArgs e)
                {
                    PictureBox me = s as PictureBox;
                    if (me == null || me.Tag == null) return;
                    ShowBigImage(me.Tag.ToString());
                };
                _thumbs.Controls.Add(box);
            }
        }

        private static void ShowBigImage(string png)
        {
            if (string.IsNullOrEmpty(png) || !File.Exists(png)) return;
            Form f = new Form
            {
                Text = "图框预览", Font = Ui.Font9, StartPosition = FormStartPosition.CenterScreen,
                ClientSize = new Size(900, 700)
            };
            PictureBox pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
            try
            {
                using (FileStream fs = new FileStream(png, FileMode.Open, FileAccess.Read))
                    pic.Image = Image.FromStream(fs);
            }
            catch { }
            f.Controls.Add(pic);
            using (f) f.ShowDialog();
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();
            double mmPer = AcadUtil.MmPerUnit(AcadUtil.Db);
            for (int i = 0; i < _items.Count; i++)
            {
                ManualPickItem it = _items[i];
                double w = (it.WindowWcs.MaxPoint.X - it.WindowWcs.MinPoint.X) * mmPer;
                double h = (it.WindowWcs.MaxPoint.Y - it.WindowWcs.MinPoint.Y) * mmPer;
                _grid.Rows.Add((i + 1).ToString(), it.DrawNo, it.Title, it.Rev,
                               w.ToString("0") + "×" + h.ToString("0"), it.MetaSource);
            }
        }

        private void SyncRow(int row)
        {
            if (row < 0 || row >= _items.Count || _grid.Rows.Count <= row) return;
            DataGridViewRow gr = _grid.Rows[row];
            ManualPickItem it = _items[row];
            it.DrawNo = Cell(gr, 1); it.Title = Cell(gr, 2); it.Rev = Cell(gr, 3);
        }
        private static string Cell(DataGridViewRow r, int c)
        {
            return r.Cells[c].Value == null ? "" : r.Cells[c].Value.ToString().Trim();
        }

        private void DeleteSelected()
        {
            if (_grid.SelectedRows.Count == 0) { Ui.Warn("请先选中一行"); return; }
            for (int i = _grid.SelectedRows.Count - 1; i >= 0; i--)
            {
                int idx = _grid.SelectedRows[i].Index;
                if (idx >= 0 && idx < _items.Count) { _items.RemoveAt(idx); _rects.RemoveAt(idx); }
            }
            RefreshGrid(); RefreshThumbs();
        }
        private void ClearAll()
        {
            _items.Clear(); _rects.Clear(); RefreshGrid(); RefreshThumbs();
            _hint.Text = "已清空。点【在图上框选范围】重新框选。";
        }

        private void Print()
        {
            for (int i = 0; i < _items.Count; i++) SyncRow(i);
            if (_items.Count == 0) { Ui.Warn("还没框选任何范围"); return; }
            Paper = PaperPresets.Get(_paper.Text);
            if (Paper.IsCustom) { Paper.WidthMm = (double)_w.Value; Paper.HeightMm = (double)_h.Value; }
            PrintRequested = true;
            Close();
        }
    }
}
