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
    public sealed class ResultForm : Form
    {
        public ResultForm(BatchResult r, string title)
        {
            Text = title + " · 结果"; Font = Ui.Font9;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 430);
            MinimizeBox = false;

            string head = string.Format("成功 {0} 张 / 失败 {1} 张{2}\r\n输出目录：{3}\r\n",
                r.Success, r.Failed, r.Canceled ? "（已取消）" : "", r.OutputDir);
            if (!string.IsNullOrEmpty(r.MergedPath)) head += "PDF 合集：" + Path.GetFileName(r.MergedPath) + "\r\n";
            if (!string.IsNullOrEmpty(r.CsvPath)) head += "图纸目录：" + Path.GetFileName(r.CsvPath) + "\r\n";
            if (!string.IsNullOrEmpty(r.TableNote)) head += r.TableNote + "\r\n";
            if (!string.IsNullOrEmpty(r.SummaryFile)) head += "结果清单：" + Path.GetFileName(r.SummaryFile) + "\r\n";

            TextBox tb = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                Dock = DockStyle.Fill, Font = new Font("Consolas", 9F), WordWrap = false
            };
            System.Text.StringBuilder sb = new System.Text.StringBuilder(head);
            foreach (string w in r.Warnings) sb.AppendLine("\r\n[提示] " + w);
            sb.AppendLine("\r\n---- 逐张结果 ----");
            foreach (SheetItem s in r.Sheets)
                sb.AppendLine((s.Plotted ? "[OK]   " : "[FAIL] ") + s.Index.ToString("00") + "  "
                    + (s.FileStem ?? "") + ".pdf  " + (s.DrawNo ?? "") + "  " + (s.Title ?? "")
                    + (s.Plotted ? "" : "  → " + (s.Error ?? "")));
            tb.Text = sb.ToString();

            FlowLayoutPanel bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
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
            bottom.Controls.Add(close); bottom.Controls.Add(open); bottom.Controls.Add(log);
            Controls.Add(tb); Controls.Add(bottom);
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
        private readonly CheckBox _styles, _lw, _center, _rot, _preview;
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
            _preview = Ui.C("打印前先预览（可逐张确认，关掉则一键直出）", c.PreviewBeforePrint);
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
            p.Controls.Add(Ui.Row(_preview));
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
            c.PreviewBeforePrint = _preview.Checked;
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
    /// <summary>
    /// 打印前统一预览窗口：用与真实打印完全相同的参数渲染每张图，
    /// 左侧缩略图可点选、右侧放大查看，确认后才开始批量打印。
    /// </summary>
    public sealed class PrintPreviewForm : Form
    {
        private readonly List<PlotRequest> _reqs;
        private readonly List<SheetItem> _sheets;
        private readonly List<string> _pngs = new List<string>();
        private readonly FlowLayoutPanel _list = new FlowLayoutPanel
        { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Left, Width = 190,
          AutoScroll = true, Padding = new Padding(6), BackColor = Color.FromArgb(245, 245, 245) };
        private readonly PictureBox _big = new PictureBox
        { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
        private readonly Label _info = Ui.L("正在生成预览…", 0);
        private readonly Label _page = Ui.L("", 90);
        private int _cur = 0;
        public bool Confirmed { get; private set; }

        public PrintPreviewForm(List<PlotRequest> reqs, List<SheetItem> sheets, string project)
        {
            _reqs = reqs ?? new List<PlotRequest>();
            _sheets = sheets ?? new List<SheetItem>();

            Text = "打印前预览" + (string.IsNullOrEmpty(project) ? "" : " · " + project)
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
            _big.BringToFront();

            FlowLayoutPanel bottom = new FlowLayoutPanel
            { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            Button ok = Ui.B("确认打印 (" + _reqs.Count + " 张)", delegate { Confirmed = true; Close(); }, 140);
            ok.BackColor = Color.FromArgb(28, 92, 156); ok.ForeColor = Color.White; ok.FlatStyle = FlatStyle.Flat;
            bottom.Controls.Add(ok);
            bottom.Controls.Add(Ui.B("取消", delegate { Confirmed = false; Close(); }, 80));
            bottom.Controls.Add(Ui.B("下一张 ›", delegate { Go(_cur + 1); }, 90));
            bottom.Controls.Add(Ui.B("‹ 上一张", delegate { Go(_cur - 1); }, 90));
            bottom.Controls.Add(_page);

            Controls.Add(center);
            Controls.Add(bottom);
            Controls.Add(_list);

            Shown += delegate { BuildPreviews(); };
        }

        private void BuildPreviews()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                for (int i = 0; i < _reqs.Count; i++)
                {
                    string png = "";
                    try { png = Plotter.RenderPreview(_reqs[i]); }
                    catch (Exception ex) { Logger.Verbose("预览第 " + (i + 1) + " 张失败：" + ex.Message); }
                    _pngs.Add(png);
                    AddThumb(i, png);
                    Application.DoEvents();
                }
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
            _big.Image = null;
            if (!string.IsNullOrEmpty(png) && File.Exists(png))
            {
                try { using (FileStream fs = new FileStream(png, FileMode.Open, FileAccess.Read))
                        _big.Image = Image.FromStream(fs); }
                catch { }
            }
            PlotRequest r = _reqs[_cur];
            string scale = r.ScaleMode == ScaleMode.ScaleToFit ? "布满纸张"
                : (r.ScaleMode == ScaleMode.OneToOne ? "1:1" : "1:" + r.ScaleDenominator.ToString("0.##"));
            _info.Text = "第 " + (_cur + 1) + " / " + _reqs.Count + " 张　|　"
                       + SheetLabel(_cur) + "　|　" + scale + "　|　"
                       + (r.Centered ? "居中" : "手动偏移") + "　|　"
                       + (r.Rotated90 ? "横向旋转90°" : "正向");
            if (string.IsNullOrEmpty(png))
                _info.Text += "　（此张预览渲染失败，不影响实际打印）";
            _page.Text = (_cur + 1) + " / " + _reqs.Count;
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
