using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MPlot
{
    // ================================================================ 枚举
    public enum SortMode { Priority, DrawNo, LayoutOrder, BlockPosition }
    /// <summary>AutoFromFrame 是默认项：图纸单位量到的图框实宽 / 纸张宽 → 自动反算出图比例（1:100 之类）。</summary>
    public enum ScaleMode { AutoFromFrame, OneToOne, ScaleToFit, Custom }
    /// <summary>合并通道优先级。内置合并零依赖，默认优先；GS 用于兜底特殊 PDF 结构。</summary>
    public enum MergeMode { GhostscriptFirst, BuiltInFirst, GhostscriptOnly }
    /// <summary>
    /// 图框属性读取模式。DisplayText（默认）：读动态块当前显示值——动态参数优先、
    /// 隐藏属性不读；RawAttribute：只读 ATTRIB 原始存储值，不过滤、不覆盖。
    /// 天正动态图框切换可见性后，图面显示的经常不是 ATTRIB 存储值，所以默认用显示文本。
    /// </summary>
    public enum AttrReadMode { DisplayText, RawAttribute }
    /// <summary>颜色输出快捷预设；选 Custom 时完全以 CTB 下拉为准。</summary>
    public enum ColorMode { Custom, Color, Monochrome, Grayscale }
    /// <summary>批量导出里一张图纸的独立状态（重试/结果窗口/清单状态列共用）。</summary>
    public enum SheetStatus { Pending, Exporting, Success, Failed, Skipped }
    /// <summary>一张图纸状态的中文显示（清单、结果窗口共用）。</summary>
    public static class SheetStatusNames
    {
        public static string Text(SheetStatus s)
        {
            switch (s)
            {
                case SheetStatus.Pending: return "待导出";
                case SheetStatus.Exporting: return "正在导出";
                case SheetStatus.Success: return "成功";
                case SheetStatus.Failed: return "失败";
                case SheetStatus.Skipped: return "已跳过";
                default: return "";
            }
        }
    }
    /// <summary>导出前检查里一项问题的严重程度：Error 阻断导出，Warning 只提醒、可确认继续。</summary>
    public enum PreflightSeverity { Error, Warning }
    /// <summary>导出前检查的问题种类。</summary>
    public enum PreflightIssueKind
    {
        EmptyFileName, IllegalFileName, ReservedFileName, DuplicateFileName, DuplicateDrawNo,
        InvalidWindow, InvalidSize, InvalidPaper, InvalidScale, OutputDirNotWritable, FileExists
    }
    /// <summary>导出前检查发现的一项问题：行号（从 0 计）+ 图号 + 原因；可修复的标 AutoFixable。</summary>
    public sealed class PreflightIssue
    {
        public int RowIndex { get; set; }
        public string DrawNo { get; set; }
        public PreflightIssueKind Kind { get; set; }
        public PreflightSeverity Severity { get; set; }
        public string Message { get; set; }
        public bool AutoFixable { get; set; }
        public PreflightIssue()
        {
            RowIndex = -1; DrawNo = ""; Message = "";
        }
        public override string ToString()
        {
            return "第 " + (RowIndex + 1) + " 行" + (string.IsNullOrEmpty(DrawNo) ? "" : " · 图号 " + DrawNo)
                + " · " + Message;
        }
    }
    /// <summary>一次导出前检查的完整结果。</summary>
    public sealed class PreflightResult
    {
        public List<PreflightIssue> Issues { get; set; }
        public PreflightResult() { Issues = new List<PreflightIssue>(); }
        public bool HasErrors
        {
            get
            {
                foreach (PreflightIssue i in Issues)
                    if (i.Severity == PreflightSeverity.Error) return true;
                return false;
            }
        }
        public bool HasWarnings
        {
            get
            {
                foreach (PreflightIssue i in Issues)
                    if (i.Severity == PreflightSeverity.Warning) return true;
                return false;
            }
        }
        public int CountOf(PreflightIssueKind kind)
        {
            int n = 0;
            foreach (PreflightIssue i in Issues) if (i.Kind == kind) n++;
            return n;
        }
    }

    // ================================================================ 纸张
    /// <summary>纸张定义。Preset 仅作显示/回显，真正参与匹配的是 WidthMm / HeightMm。</summary>
    public sealed class PaperSpec
    {
        public string Preset { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public bool IsCustom { get; set; }

        public PaperSpec() { Preset = "A1"; WidthMm = 841; HeightMm = 594; }

        public override string ToString()
        {
            return string.Format("{0} ({1:0.#}×{2:0.#}mm)", Preset, WidthMm, HeightMm);
        }

        public PaperSpec Clone()
        {
            return new PaperSpec { Preset = Preset, WidthMm = WidthMm, HeightMm = HeightMm, IsCustom = IsCustom };
        }
    }

    /// <summary>
    /// 标准幅面 + 国标加长（GB/T 14689：沿长边按短边的 1/4 递增）。
    /// 注意：这里给的是「图框外框尺寸」，不是可打印区；匹配 PC3 时会留边距。
    /// </summary>
    public static class PaperPresets
    {
        public static readonly string[] Names = {
            "A0", "A1", "A2", "A3", "A4",
            "A0+1/4", "A0+1/2", "A1+1/4", "A1+1/2", "A1+3/4", "A2+1/4", "A2+1/2", "A2+3/4", "A3+1/2", "A3+3/4",
            "自定义"
        };

        /// <summary>按名称返回 宽×高（mm，横向）。A1+1/4 = 841+594/4 → 989.5×594。</summary>
        public static PaperSpec Get(string name)
        {
            double w, h;
            switch (name)
            {
                case "A0": w = 1189; h = 841; break;
                case "A1": w = 841; h = 594; break;
                case "A2": w = 594; h = 420; break;
                case "A3": w = 420; h = 297; break;
                case "A4": w = 297; h = 210; break;
                case "A0+1/4": w = 1189 + 841.0 / 4; h = 841; break;
                case "A0+1/2": w = 1189 + 841.0 / 2; h = 841; break;
                case "A1+1/4": w = 841 + 594.0 / 4; h = 594; break;
                case "A1+1/2": w = 841 + 594.0 / 2; h = 594; break;
                case "A1+3/4": w = 841 + 594.0 * 3 / 4; h = 594; break;
                case "A2+1/4": w = 594 + 420.0 / 4; h = 420; break;
                case "A2+1/2": w = 594 + 420.0 / 2; h = 420; break;
                case "A2+3/4": w = 594 + 420.0 * 3 / 4; h = 420; break;
                case "A3+1/2": w = 420 + 297.0 / 2; h = 297; break;
                case "A3+3/4": w = 420 + 297.0 * 3 / 4; h = 297; break;
                case "自定义": w = 841; h = 594; break;
                default: w = 841; h = 594; name = "A1"; break;
            }
            return new PaperSpec { Preset = name, WidthMm = w, HeightMm = h, IsCustom = name == "自定义" };
        }

        /// <summary>解析清单纸张列里的显示文字回纸张定义：接受 "A1"、"自定义"、"自定义 880×640mm"、"A1+1/2"。</summary>
        public static bool TryParseDisplay(string text, out PaperSpec spec)
        {
            spec = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            // "自定义 880×640mm"：按实测尺寸还原自定义纸张
            if (t.StartsWith("自定义"))
            {
                string rest = t.Substring("自定义".Length).Trim();
                if (rest.Length == 0) { spec = Get("自定义"); return true; }
                rest = rest.Replace("mm", "").Replace("MM", "").Trim();
                string[] wh = rest.Split(new char[] { '×', 'x', 'X' }, StringSplitOptions.RemoveEmptyEntries);
                double w, h;
                if (wh.Length == 2
                    && double.TryParse(wh[0].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out w)
                    && double.TryParse(wh[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out h)
                    && w > 0 && h > 0)
                {
                    spec = new PaperSpec { Preset = "自定义", WidthMm = w, HeightMm = h, IsCustom = true };
                    return true;
                }
                return false;
            }
            // 标准预设名：取第一个空格前的部分（"A1 841×594mm" 这类也认）
            string name = t.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
            foreach (string n in Names)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                { spec = Get(n); return true; }
            }
            return false;
        }

        /// <summary>按尺寸反查最接近的预设名，给「框选图框」自动识别纸张用。</summary>
        public static PaperSpec Guess(double widthMm, double heightMm)
        {
            string best = "自定义";
            double bestErr = double.MaxValue;
            foreach (string n in Names)
            {
                if (n == "自定义") continue;
                PaperSpec p = Get(n);
                double err = Math.Abs(p.WidthMm - widthMm) + Math.Abs(p.HeightMm - heightMm);
                // 也允许旋转 90° 后匹配
                double errR = Math.Abs(p.HeightMm - widthMm) + Math.Abs(p.WidthMm - heightMm);
                if (Math.Min(err, errR) < bestErr) { bestErr = Math.Min(err, errR); best = n; }
            }
            // 吸附放宽到 10%：图框包围盒常比标称纸多几毫米（外框线、图签伸出），
            // 6% 时 A1 实测 870×620 这类只大 3% 左右的也会掉进自定义；10% 仍远小于相邻幅面差距。
            PaperSpec r = bestErr < Math.Max(widthMm, heightMm) * 0.10
                ? Get(best)
                : new PaperSpec { Preset = "自定义", WidthMm = widthMm, HeightMm = heightMm, IsCustom = true };
            return r;
        }

        /// <summary>常见出图比例（1:2 … 1:1000）。同一图框在 CAD 里常以 1:100、1:150 插入，
        /// 实测尺寸是纸张 × 比例；只按实测尺寸查会掉进自定义。按从小到大试，还原后命中标准幅面即认。</summary>
        private static readonly double[] ScaleDivisors = {
            1, 2, 2.5, 4, 5, 10, 20, 25, 30, 50, 60, 75, 100, 150, 200, 250, 500, 1000
        };

        /// <summary>按插入比例还原后的尺寸反查纸张。sx/sy 是块参照的插入比例（X/Y）；
        /// 旋转 90° 时 X/Y 对调，两种配对都试。prefer 为库内已设的纸张名：同一尺寸可对应
        /// 多个「幅面×比例」组合时（如 84000×59400 既是 A1×100 也是 A3×200），优先拼出库内纸张。
        /// 猜不到标准幅面时返回自定义（调用方应保留库纸张，不要拿它覆盖）。</summary>
        public static PaperSpec GuessScaled(double widthMm, double heightMm, double sx, double sy, string prefer)
        {
            if (sx <= 0 || double.IsNaN(sx) || double.IsInfinity(sx)) sx = 1;
            if (sy <= 0 || double.IsNaN(sy) || double.IsInfinity(sy)) sy = 1;
            // 第一遍：优先拼出库内已设的纸张（有歧义时以库为准）
            if (!string.IsNullOrEmpty(prefer))
            {
                foreach (double d in ScaleDivisors)
                {
                    if (MatchesPreset(widthMm / sx / d, heightMm / sy / d, prefer)) return Get(prefer);
                    if (MatchesPreset(widthMm / sy / d, heightMm / sx / d, prefer)) return Get(prefer);
                }
            }
            // 第二遍：没有库纸张可优先时，全量找「相对误差最小」的（幅面，比例）组合。
            // 不能按比例从小到大取第一个就中的——小比例下大一号纸也可能落在 10% 吸附带内
            // （如 1:150 的 A1 在 1:100 下会误吸成 A0），必须比完全局误差。
            string bestName = ""; double bestRel = double.MaxValue;
            foreach (double d in ScaleDivisors)
            {
                foreach (string n in Names)
                {
                    if (n == "自定义") continue;
                    PaperSpec p = Get(n);
                    double r1 = RelErr(widthMm / sx / d, heightMm / sy / d, p);
                    double r2 = RelErr(widthMm / sy / d, heightMm / sx / d, p);
                    double r = Math.Min(r1, r2);
                    if (r < bestRel) { bestRel = r; bestName = n; }
                }
            }
            if (bestName != "" && bestRel < 0.10) return Get(bestName);
            return Guess(widthMm, heightMm);
        }

        /// <summary>尺寸与预设的相对误差之和（双向都算，取小值；用于比例吸附的全局比较）。</summary>
        private static double RelErr(double w, double h, PaperSpec p)
        {
            if (w <= 0 || h <= 0) return double.MaxValue;
            double e1 = Math.Abs(p.WidthMm - w) / p.WidthMm + Math.Abs(p.HeightMm - h) / p.HeightMm;
            double e2 = Math.Abs(p.HeightMm - w) / p.HeightMm + Math.Abs(p.WidthMm - h) / p.WidthMm;
            return Math.Min(e1, e2);
        }

        /// <summary>尺寸是否与指定预设吻合（相对误差之和 &lt; 10%，含 90° 旋转；与 Guess 吸附同阈）。</summary>
        private static bool MatchesPreset(double w, double h, string name)
        {
            if (w <= 0 || h <= 0) return false;
            PaperSpec p = Get(name);
            if (p.IsCustom) return false;
            double e1 = Math.Abs(p.WidthMm - w) / p.WidthMm + Math.Abs(p.HeightMm - h) / p.HeightMm;
            double e2 = Math.Abs(p.HeightMm - w) / p.HeightMm + Math.Abs(p.WidthMm - h) / p.WidthMm;
            return Math.Min(e1, e2) < 0.10;
        }
    }

    // ================================================================ 单条图框配置
    /// <summary>
    /// 图框库的一行。表格列对应关系：
    /// 序号=Seq｜图框对应的图块=BlockName｜对应纸张=Paper｜排序优先级别=Priority｜删除=Deleted
    /// 文件命名规则=NameTemplate｜图号(A)=TagDrawNo｜版次(B)=TagRev｜【补】图名(C)=TagTitle
    /// Tag* 里存的是「该图框块中对应的属性 Tag 名」，不是图号本身。
    /// </summary>
    public sealed class FrameEntry
    {
        public int Seq { get; set; }
        public string BlockName { get; set; }
        public PaperSpec Paper { get; set; }
        public int Priority { get; set; }
        public bool Deleted { get; set; }
        public string NameTemplate { get; set; }

        public string TagDrawNo { get; set; }
        public string TagTitle { get; set; }
        public string TagRev { get; set; }

        public bool ReadDrawNo { get; set; }
        public bool ReadTitle { get; set; }
        public bool ReadRev { get; set; }

        /// <summary>打印比例分子（图纸单位）；0 = 跟随全局设置。用于「以 1:100 出 A1」这类强制比例。</summary>
        public double ScaleNumerator { get; set; }
        /// <summary>打印比例分母（图纸单位）；0 = 跟随全局设置。</summary>
        public double ScaleDenominator { get; set; }
        /// <summary>标题栏所在方位，用于无属性块时按位置猜文字：bottom-right / bottom-left / top-right / top-left / auto</summary>
        public string TitleBarHint { get; set; }

        /// <summary>
        /// 「点取图号 / 图名范围」存的区域，用相对图框外框的归一化坐标 (0~1)：
        /// (DnX0,DnY0)-(DnX1,DnY1)=图号，(TtX0,TtY0)-(TtX1,TtY1)=图名。
        /// -1 表示未点取。批打时按每张图框的实际包围盒换算回 WCS 区域读当前图的文字，
        /// 所以每张图纸号/图名各不相同、不会撞名。同时兼容块属性和普通文字两种形式。
        /// </summary>
        public double DnX0 { get; set; }
        public double DnY0 { get; set; }
        public double DnX1 { get; set; }
        public double DnY1 { get; set; }
        public double TtX0 { get; set; }
        public double TtY0 { get; set; }
        public double TtX1 { get; set; }
        public double TtY1 { get; set; }

        /// <summary>
        /// 「人工点选确认」的锚点（相对图框外框归一化 0~1）：用户框出范围后，从候选文字里点选了
        /// 哪段是图号/图名，就记住那段文字的中心点。批打时按同一相对位置找最近的文字，
        /// 比「区域内打分猜」更准。-1 表示未点选确认。
        /// </summary>
        public double DnAnchorX { get; set; }
        public double DnAnchorY { get; set; }
        public double TtAnchorX { get; set; }
        public double TtAnchorY { get; set; }
        /// <summary>录入时人工点选确认的样例值，仅用于界面提示与核对，不参与批打取值。</summary>
        public string DnSample { get; set; }
        public string TtSample { get; set; }

        /// <summary>备注（不参与逻辑，方便项目复用时写说明）</summary>
        public string Note { get; set; }

        public bool HasDrawNoRegion { get { return DnX0 >= 0 && DnX1 > DnX0 && DnY1 > DnY0; } }
        public bool HasTitleRegion { get { return TtX0 >= 0 && TtX1 > TtX0 && TtY1 > TtY0; } }
        public bool HasDnAnchor { get { return DnAnchorX >= 0 && DnAnchorY >= 0; } }
        public bool HasTtAnchor { get { return TtAnchorX >= 0 && TtAnchorY >= 0; } }

        public FrameEntry()
        {
            BlockName = "";
            Paper = new PaperSpec();
            Priority = 10;
            NameTemplate = Naming.DefaultTemplate;
            TagDrawNo = "";
            TagTitle = "";
            TagRev = "";
            ReadDrawNo = true;
            ReadTitle = true;
            ReadRev = true;
            TitleBarHint = "auto";
            DnX0 = DnY0 = DnX1 = DnY1 = -1;
            TtX0 = TtY0 = TtX1 = TtY1 = -1;
            DnAnchorX = DnAnchorY = -1;
            TtAnchorX = TtAnchorY = -1;
            DnSample = "";
            TtSample = "";
            Note = "";
        }

        public FrameEntry Clone()
        {
            FrameEntry f = (FrameEntry)MemberwiseClone();
            f.Paper = Paper == null ? new PaperSpec() : Paper.Clone();
            return f;
        }
    }

    // ================================================================ 图框库
    public sealed class FrameLibrary
    {
        public int Version { get; set; }
        public string ProjectName { get; set; }
        public List<FrameEntry> Frames { get; set; }

        public FrameLibrary() { Version = 1; ProjectName = ""; Frames = new List<FrameEntry>(); }

        private static string StoreDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MPlot"); }
        }
        public static string DefaultPath { get { return Path.Combine(StoreDir, "frames.json"); } }

        public static string EnsureDir()
        {
            if (!Directory.Exists(StoreDir)) Directory.CreateDirectory(StoreDir);
            return StoreDir;
        }

        public static FrameLibrary Load()
        {
            try
            {
                if (File.Exists(DefaultPath))
                {
                    string json = File.ReadAllText(DefaultPath, Encoding.UTF8);
                    FrameLibrary lib = MiniJson.ToObject<FrameLibrary>(json);
                    if (lib != null && lib.Frames != null)
                    {
                        for (int i = 0; i < lib.Frames.Count; i++)
                        {
                            if (lib.Frames[i].Paper == null) lib.Frames[i].Paper = PaperPresets.Get("A1");
                            // 存量升级：空模板或老默认 A_C_B → 新默认（图号_图名_图框大小）；用户自定义模板原样保留
                            string t = lib.Frames[i].NameTemplate;
                            if (string.IsNullOrWhiteSpace(t) || t == "A_C_B" || t == "A_C_T")
                                lib.Frames[i].NameTemplate = Naming.DefaultTemplate;
                        }
                        return lib;
                    }
                }
            }
            catch (Exception ex) { Logger.Warn("图框库读取失败，已回落默认库：" + ex.Message); }
            return CreateDefault();
        }

        public void Save()
        {
            EnsureDir();
            Renumber();
            File.WriteAllText(DefaultPath, MiniJson.Stringify(this), new UTF8Encoding(false));
        }

        /// <summary>导出整套项目图框库配置（含版本、项目名、全部条目），供项目复用。</summary>
        public void ExportTo(string path)
        {
            File.WriteAllText(path, MiniJson.Stringify(this), new UTF8Encoding(false));
        }

        /// <summary>导入；mode=replace 覆盖 / merge 按块名合并（同块名以导入文件为准）。</summary>
        public static FrameLibrary ImportFrom(string path, FrameLibrary current, bool merge)
        {
            FrameLibrary incoming = MiniJson.ToObject<FrameLibrary>(File.ReadAllText(path, Encoding.UTF8));
            if (incoming == null || incoming.Frames == null) throw new InvalidDataException("不是有效的图框库文件");
            foreach (FrameEntry f in incoming.Frames) if (f.Paper == null) f.Paper = PaperPresets.Get("A1");
            if (!merge) { incoming.Save(); return incoming; }

            FrameLibrary result = current ?? new FrameLibrary();
            result.ProjectName = incoming.ProjectName;
            foreach (FrameEntry f in incoming.Frames)
            {
                FrameEntry old = result.Frames.FirstOrDefault(x => string.Equals(x.BlockName, f.BlockName, StringComparison.OrdinalIgnoreCase));
                if (old == null) result.Frames.Add(f);
                else
                {
                    int seq = old.Seq;
                    int idx = result.Frames.IndexOf(old);
                    result.Frames[idx] = f;
                    result.Frames[idx].Seq = seq;
                }
            }
            result.Save();
            return result;
        }

        public void Renumber()
        {
            for (int i = 0; i < Frames.Count; i++) Frames[i].Seq = i + 1;
        }

        public IEnumerable<FrameEntry> Active { get { return Frames.Where(f => !f.Deleted && !string.IsNullOrWhiteSpace(f.BlockName)); } }

        /// <summary>首次运行的示例库：天正 / 常见设计院图框块名，用户可直接改或删。</summary>
        public static FrameLibrary CreateDefault()
        {
            FrameLibrary lib = new FrameLibrary { ProjectName = "默认项目" };
            string[] demo = { "A1图框", "A2图框", "A3图框", "TK_A1", "TK_A2", "图框_A1", "TCH_TITLE_A1" };
            int i = 0;
            foreach (string n in demo)
            {
                string sz = n.Contains("A2") ? "A2" : (n.Contains("A3") ? "A3" : "A1");
                lib.Frames.Add(new FrameEntry
                {
                    Seq = ++i,
                    BlockName = n,
                    Paper = PaperPresets.Get(sz),
                    Priority = 10 + i,
                    NameTemplate = Naming.DefaultTemplate,
                    TagDrawNo = "图号",
                    TagTitle = "图名",
                    TagRev = "版次",
                    Note = "示例条目，按你的图框实际块名修改"
                });
            }
            return lib;
        }
    }

    // ================================================================ 全局配置
    /// <summary>打印设置 / 其余设置 两个面板共用的持久化配置。</summary>
    public sealed class GlobalConfig
    {
        // ---- 打印设置 ----
        public string PlotDevice { get; set; }
        public string StyleSheet { get; set; }
        public ColorMode ColorMode { get; set; }
        public bool PlotWithPlotStyles { get; set; }
        public bool PlotWithLineweights { get; set; }
        public bool PlotCentered { get; set; }
        public double PlotOffsetX { get; set; }           // mm，打印原点偏移
        public double PlotOffsetY { get; set; }
        public double WindowMarginMm { get; set; }        // 图框窗口外扩，防止边框线被裁掉
        public ScaleMode ScaleMode { get; set; }
        public double CustomScaleNumerator { get; set; }
        public double CustomScaleDenominator { get; set; }
        /// <summary>1 个图形单位 = 多少 mm。null/0 = 从 DWG 的 INSUNITS 自动推断；建筑图一般 mm=1。</summary>
        public double MmPerDrawingUnit { get; set; }
        public bool RotateToBestFit { get; set; }         // 图框旋转 90° 时自动把纸张转过来
        public bool HideSystemPrinters { get; set; }
        /// <summary>打印前先弹出预览窗口（默认开，可在打印设置里关掉以一键直出）。</summary>
        public bool PreviewBeforePrint { get; set; }

        // ---- 其余设置 ----
        public string OutputDir { get; set; }             // 空 = 当前 DWG 同目录 \PDF输出
        public string GhostscriptPath { get; set; }
        public bool MergePdf { get; set; }
        public string MergedFileName { get; set; }
        public MergeMode MergeMode { get; set; }
        public bool OverwriteExisting { get; set; }
        public bool KeepSinglePdfsAfterMerge { get; set; }

        public bool CatalogCsv { get; set; }
        public string CatalogCsvName { get; set; }
        public bool CatalogTable { get; set; }
        public double TableTextHeightMm { get; set; }
        public double TableColumnWidthsMm { get; set; }
        public bool TableAskForInsertPoint { get; set; }

        public SortMode SortMode { get; set; }
        public bool SortDescending { get; set; }
        public string ProjectPrefix { get; set; }
        public string DateFormat { get; set; }
        /// <summary>命名模板符号 → 字段。默认 A=图号 B=版次 C=图名 T=版次(兼容别名) N=序号 D=日期 P=项目前缀</summary>
        public Dictionary<string, string> TokenMap { get; set; }

        public bool ScanPaperSpace { get; set; }
        public bool ScanXrefFrames { get; set; }
        public bool RectFrameFallback { get; set; }       // 图框是普通矩形多段线时的兜底
        public bool TitleBarTextFallback { get; set; }    // 块无属性时，按标题栏位置找文字
        /// <summary>图框属性读取模式：DisplayText 读动态块显示值（默认），RawAttribute 只读 ATTRIB 存储值。</summary>
        public AttrReadMode AttrReadMode { get; set; }
        public bool IgnoreEmptyNames { get; set; }
        public double DuplicateIouThreshold { get; set; } // 嵌套/重叠图框去重阈值
        public bool DisableBackgroundPlot { get; set; }
        public string DefaultPrinter { get; set; }        // 批打到纸张用
        public int PaperCopies { get; set; }

        private static GlobalConfig _current;
        public static GlobalConfig Current
        {
            get { return _current ?? (_current = Load()); }
        }

        public static string ConfigPath { get { return Path.Combine(FrameLibrary.EnsureDir(), "config.json"); } }

        public static GlobalConfig Load()
        {
            GlobalConfig c = new GlobalConfig();
            c.SetDefaults();
            try
            {
                if (File.Exists(ConfigPath)) MiniJson.Populate(File.ReadAllText(ConfigPath, Encoding.UTF8), c);
            }
            catch (Exception ex) { Logger.Warn("配置读取失败，使用默认值：" + ex.Message); }
            return c;
        }

        public void Save()
        {
            FrameLibrary.EnsureDir();
            File.WriteAllText(ConfigPath, MiniJson.Stringify(this), new UTF8Encoding(false));
        }

        private void SetDefaults()
        {
            PlotDevice = "DWG To PDF.pc3";
            StyleSheet = "monochrome.ctb";
            ColorMode = ColorMode.Custom;
            PlotWithPlotStyles = true;
            PlotWithLineweights = true;
            PlotCentered = true;
            PlotOffsetX = 0; PlotOffsetY = 0;
            WindowMarginMm = 2.0;
            ScaleMode = ScaleMode.AutoFromFrame;
            CustomScaleNumerator = 1; CustomScaleDenominator = 100;
            MmPerDrawingUnit = 0;   // 0 = 读 INSUNITS 自动推断
            RotateToBestFit = true;
            HideSystemPrinters = true;
            PreviewBeforePrint = true;

            OutputDir = "";
            GhostscriptPath = "";
            MergePdf = true;
            MergedFileName = "图纸合集.pdf";
            MergeMode = MergeMode.BuiltInFirst;
            OverwriteExisting = false;
            KeepSinglePdfsAfterMerge = true;

            CatalogCsv = true;
            CatalogCsvName = "图纸目录.csv";
            CatalogTable = false;
            TableTextHeightMm = 3.5;
            TableColumnWidthsMm = 40;
            TableAskForInsertPoint = true;

            SortMode = SortMode.Priority;
            SortDescending = false;
            ProjectPrefix = "";
            DateFormat = "yyyyMMdd";
            TokenMap = new Dictionary<string, string>
            {
                { "A", "图号" }, { "B", "版次" }, { "C", "图名" },
                { "T", "版次" }, { "N", "序号" }, { "D", "日期" }, { "P", "项目前缀" }
            };

            ScanPaperSpace = false;
            ScanXrefFrames = false;
            RectFrameFallback = true;
            TitleBarTextFallback = true;
            AttrReadMode = AttrReadMode.DisplayText;
            IgnoreEmptyNames = false;
            DuplicateIouThreshold = 0.90;
            DisableBackgroundPlot = true;
            DefaultPrinter = "";
            PaperCopies = 1;
        }

        public string EffectiveOutputDir(string dwgPath)
        {
            if (!string.IsNullOrWhiteSpace(OutputDir)) return OutputDir;
            string dir = string.IsNullOrWhiteSpace(dwgPath) ? FrameLibrary.EnsureDir() : Path.GetDirectoryName(dwgPath);
            if (string.IsNullOrEmpty(dir)) dir = FrameLibrary.EnsureDir();
            return Path.Combine(dir, "PDF输出");
        }
    }
}
