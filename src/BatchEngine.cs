using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcCore = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MPlot
{
    public sealed class BatchResult
    {
        public List<SheetItem> Sheets { get; set; }
        public List<string> Warnings { get; set; }
        public int Success { get; set; }
        public int Failed { get; set; }
        /// <summary>本次任务里被跳过的张数（目标文件已存在且用户选了跳过，或检查未通过被跳过）。</summary>
        public int Skipped { get; set; }
        public bool Canceled { get; set; }
        public string OutputDir { get; set; }
        public string MergedPath { get; set; }
        /// <summary>合并是否失败（失败时单页 PDF 全部保留，可只重跑合并）。</summary>
        public bool MergeFailed { get; set; }
        public string MergeError { get; set; }
        public string CsvPath { get; set; }
        public string TableNote { get; set; }
        public string SummaryFile { get; set; }
        /// <summary>本次批量任务的唯一 ID（时间 + 随机串），临时目录与日志据此归属。</summary>
        public string TaskId { get; set; }
        /// <summary>本次任务的独立临时工作目录（单页 PDF 先落这里，成功后才移到输出目录）。</summary>
        public string TempDir { get; set; }
        /// <summary>预览生成失败的图纸（"第 N 张 · 图号 图名：原因"），不再只给一句笼统错误。</summary>
        public List<string> PreviewFailed { get; set; }
        public BatchResult()
        {
            Sheets = new List<SheetItem>();
            Warnings = new List<string>();
            OutputDir = ""; MergedPath = ""; MergeError = ""; CsvPath = ""; TableNote = ""; SummaryFile = "";
            TaskId = ""; TempDir = "";
            PreviewFailed = new List<string>();
        }
    }

    /// <summary>打印前预览闸门的返回决定：确认导出 / 取消任务 / 放弃预览并明确确认后直接导出。</summary>
    public enum PreviewDecision { Confirmed, Canceled, ContinueWithoutPreview }

    /// <summary>把扫描、命名、打印、合并、目录串成一条流水线。所有菜单入口最终都落到这里。</summary>
    public static class BatchEngine
    {
        public static BatchResult RunFrameBatch(FrameLibrary lib, Action<int, int, string> progress,
                                                Func<bool> canceled, Point3d? tablePoint, bool confirmCleanup)
        {
            BatchResult res = new BatchResult();
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) { res.Warnings.Add("没有打开的图纸"); return res; }
            GlobalConfig cfg = GlobalConfig.Current;

            ScanResult scan = FrameScanner.Scan(doc.Database, lib, cfg);
            res.Warnings.AddRange(scan.Warnings);
            if (scan.UnmatchedBlockNames.Count > 0)
                res.Warnings.Add("模型里有 " + scan.UnmatchedBlockNames.Count + " 种未录入的图框块："
                    + string.Join("、", scan.UnmatchedBlockNames.Take(12).ToArray())
                    + (scan.UnmatchedBlockNames.Count > 12 ? " …" : "") + "（可用「图框信息库 → 其它 → 检查未录入图框」查看全部）");
            if (string.IsNullOrWhiteSpace(cfg.StyleSheet) == false && !cfg.PlotWithPlotStyles)
                res.Warnings.Add("已关闭「按打印样式打印」，CTB 选择不会生效");

            return Execute(scan.Sheets, true, false, "", progress, canceled, tablePoint, confirmCleanup, res);
        }

        public static BatchResult RunGeneralBatch(Action<int, int, string> progress, Func<bool> canceled, Point3d? tablePoint)
        {
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            BatchResult res = new BatchResult();
            if (doc == null) { res.Warnings.Add("没有打开的图纸"); return res; }
            List<SheetItem> sheets = FrameScanner.ScanLayouts(doc.Database, GlobalConfig.Current);
            if (sheets.Count == 0) res.Warnings.Add("当前 DWG 没有可打印的布局（模型空间不计入通用型）");
            // 通用型保留每个布局自己的页面设置，这才是「批量打印布局」的语义
            return Execute(sheets, true, true, "", progress, canceled, tablePoint, false, res);
        }

        public static BatchResult RunPaperBatch(bool newVersion, Action<int, int, string> progress, Func<bool> canceled)
        {
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            BatchResult res = new BatchResult();
            if (doc == null) { res.Warnings.Add("没有打开的图纸"); return res; }
            GlobalConfig cfg = GlobalConfig.Current;

            string printer = cfg.DefaultPrinter;
            if (string.IsNullOrWhiteSpace(printer))
            {
                printer = AcadUtil.GetPlotDevices().FirstOrDefault(d =>
                    d.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) < 0 &&
                    d.IndexOf("DWF", StringComparison.OrdinalIgnoreCase) < 0);
            }
            if (string.IsNullOrWhiteSpace(printer))
            {
                res.Warnings.Add("没有可用的物理打印机，请到「打印设置 → 默认纸张打印机」里选一台");
                return res;
            }

            List<SheetItem> sheets;
            bool preserve;
            if (newVersion)
            {
                // 新版：图框窗口 + 统一打印参数
                sheets = FrameScanner.Scan(doc.Database, FrameLibrary.Load(), cfg).Sheets;
                preserve = false;
            }
            else
            {
                // 旧版：布局队列 + 每张布局自己的页面设置（不改任何参数，兼容老出图流程）
                sheets = FrameScanner.ScanLayouts(doc.Database, cfg);
                preserve = true;
            }
            if (sheets.Count == 0) res.Warnings.Add("没有找到可打印的图纸");
            return Execute(sheets, false, preserve, printer, progress, canceled, null, false, res);
        }

        /// <summary>
        /// 手动框选批量打印：用户在模型空间连续框选多个 WCS 窗口，每个窗口一张 PDF。
        /// 图号/图名由调用方从框选区域读出来（读不到的留空，由界面补填）。
        /// </summary>
        public static BatchResult RunManualBatch(List<ManualPickItem> picks, PaperSpec paper,
                                                 Action<int, int, string> progress, Func<bool> canceled)
        {
            BatchResult res = new BatchResult();
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) { res.Warnings.Add("没有打开的图纸"); return res; }
            if (picks == null || picks.Count == 0) { res.Warnings.Add("没有框选任何打印范围"); return res; }
            GlobalConfig cfg = GlobalConfig.Current;
            PaperSpec pm = paper ?? PaperPresets.Get("A3");
            double mmPer = AcadUtil.MmPerUnit(doc.Database);
            // 四边留白：纸张不放大、内容按比例缩小实现（见 BuildRequest）。
            // 打印窗口用框选原尺寸，不再对称外扩——外扩会把纸张撑大一号，白边反而更多。
            double marginUnits = 0;
            List<SheetItem> sheets = new List<SheetItem>();
            for (int i = 0; i < picks.Count; i++)
            {
                ManualPickItem pk = picks[i];
                Extents2d raw = pk.WindowWcs;
                Extents2d wcs = new Extents2d(raw.MinPoint.X - marginUnits, raw.MinPoint.Y - marginUnits,
                                              raw.MaxPoint.X + marginUnits, raw.MaxPoint.Y + marginUnits);
                double wMm = (wcs.MaxPoint.X - wcs.MinPoint.X) * mmPer;
                double hMm = (wcs.MaxPoint.Y - wcs.MinPoint.Y) * mmPer;
                string stem = string.IsNullOrWhiteSpace(pk.FileStem)
                    ? Naming.BuildFileName(new SheetItem
                      {
                          DrawNo = pk.DrawNo, Title = pk.Title, Rev = pk.Rev,
                          Config = new FrameEntry { Paper = pm }, LayoutName = BlockTableRecordName()
                      }, cfg, i + 1)
                    : pk.FileStem;
                sheets.Add(new SheetItem
                {
                    Seq = i + 1, Index = i + 1,
                    BlockName = "手动框选",
                    Config = new FrameEntry { Paper = pm, Priority = i + 1 },
                    LayoutName = BlockTableRecordName(),
                    WindowWcs = wcs,
                    WindowDcs = AcadUtil.WcsRectToDcs(wcs),
                    WidthMm = wMm, HeightMm = hMm,
                    Rotated = false,
                    DrawNo = pk.DrawNo ?? "", Title = pk.Title ?? "", Rev = pk.Rev ?? "",
                    MetaSource = pk.MetaSource ?? "手动框选",
                    Center = new Point3d((wcs.MinPoint.X + wcs.MaxPoint.X) / 2,
                                         (wcs.MinPoint.Y + wcs.MaxPoint.Y) / 2, 0),
                    FileStem = stem
                });
            }
            return Execute(sheets, true, false, "", progress, canceled, null, false, res);
        }

        /// <summary>
        /// 图框型完整流程用：打印一批已在列表窗里确认/编辑过的图纸。
        /// 与 RunFrameBatch 走同一条 Execute 流水线，只是数据来自 UI 而非直接扫描。
        /// </summary>
        public static BatchResult RunConfirmedSheets(List<SheetItem> sheets, Point3d? tablePoint,
                                                     Action<int, int, string> progress, Func<bool> canceled)
        {
            BatchResult res = new BatchResult();
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) { res.Warnings.Add("没有打开的图纸"); return res; }
            if (sheets == null || sheets.Count == 0) { res.Warnings.Add("列表里没有可打印的图纸"); return res; }
            return Execute(sheets, true, false, "", progress, canceled, tablePoint, true, res);
        }

        /// <summary>单张打印到文件：手动窗口 + 指定纸张 + 指定文件名。</summary>
        public static BatchResult PlotSingle(Extents2d windowDcs, PaperSpec paper, string fileStem,
                                             ScaleMode scaleMode, double scaleDen)
        {
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            BatchResult res = new BatchResult();
            if (doc == null) { res.Warnings.Add("没有打开的图纸"); return res; }
            GlobalConfig cfg = GlobalConfig.Current;
            SheetItem s = new SheetItem
            {
                Index = 1, Seq = 1,
                FileStem = string.IsNullOrWhiteSpace(fileStem) ? "单张图纸" : fileStem,
                Config = new FrameEntry { Paper = paper ?? PaperPresets.Get("A1"), Priority = 1 },
                LayoutName = BlockTableRecordName(),
                DrawNo = "", Title = "", Rev = ""
            };
            s.Config.Paper = paper ?? PaperPresets.Get("A1");
            s.WindowDcs = windowDcs;
            // 导出前检查要用到图纸实宽/实高：单张打印的窗口尺寸也按 DWG 单位换算出来
            double mmPer = AcadUtil.MmPerUnit(doc.Database);
            s.WidthMm = (windowDcs.MaxPoint.X - windowDcs.MinPoint.X) * mmPer;
            s.HeightMm = (windowDcs.MaxPoint.Y - windowDcs.MinPoint.Y) * mmPer;
            return Execute(new List<SheetItem> { s }, true, false, "", null, null, null, false, res,
                           (ScaleMode?)scaleMode, scaleDen);
        }

        private static string BlockTableRecordName()
        {
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            return doc == null ? "" : Path.GetFileNameWithoutExtension(doc.Name ?? "");
        }

        // ---------------------------------------------------------------- 打印前预览闸门
        /// <summary>
        /// 由 UI 层注册的打印前预览回调：参数 = (逐张打印请求, 对应图纸, 标题)。
        /// 返回 Confirmed=确认导出 / Canceled=取消任务 / ContinueWithoutPreview=放弃预览并明确确认后直接导出。
        /// 预览窗口自身的异常由注册方在回调内转成决定，BatchEngine 收到异常时不再静默继续导出。
        /// </summary>
        public static Func<List<PlotRequest>, List<SheetItem>, string, PreviewDecision> PreviewHook;

        /// <summary>最近一次预览里生成失败的图纸标签（由 UI 层在回调返回前写入，供 Execute 记入结果）。</summary>
        public static List<string> PreviewFailedLabels = new List<string>();

        // ---------------------------------------------------------------- 导出前检查
        /// <summary>
        /// 统一的导出前检查（PreflightCheck）：UI 确认导出前调一次，Execute 真正出图前再调一次，
        /// 只做界面提示不够——检查失败绝不进入批量导出流程。
        /// Error 阻断导出；Warning 只提醒（重复图号、目标文件已存在由 UI 层请用户定夺）。
        /// 通用型布局（IsGeneralLayout）沿用布局自己的页面设置，不查窗口/尺寸/纸张。
        /// </summary>
        public static PreflightResult PreflightCheck(List<SheetItem> sheets, string outDir, GlobalConfig cfg)
        {
            PreflightResult r = new PreflightResult();
            if (sheets == null) return r;
            if (cfg == null) cfg = GlobalConfig.Current;

            Dictionary<string, int> dnCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> fileCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (SheetItem s in sheets)
            {
                string d = (s.DrawNo ?? "").Trim();
                string f = (s.FileStem ?? "").Trim();
                if (d.Length > 0) { if (!dnCount.ContainsKey(d)) dnCount[d] = 0; dnCount[d]++; }
                if (f.Length > 0) { if (!fileCount.ContainsKey(f)) fileCount[f] = 0; fileCount[f]++; }
            }

            for (int i = 0; i < sheets.Count; i++)
            {
                SheetItem s = sheets[i];
                string dn = (s.DrawNo ?? "").Trim();
                string stem = (s.FileStem ?? "").Trim();

                if (s.Status == SheetStatus.Skipped) continue;   // 已跳过的图不参与检查

                if (stem.Length == 0)
                {
                    r.Issues.Add(new PreflightIssue
                    {
                        RowIndex = i, DrawNo = dn, Kind = PreflightIssueKind.EmptyFileName,
                        Severity = PreflightSeverity.Error, Message = "文件名为空"
                    });
                }
                else
                {
                    if (Naming.HasIllegalFileNameChars(stem))
                        r.Issues.Add(new PreflightIssue
                        {
                            RowIndex = i, DrawNo = dn, Kind = PreflightIssueKind.IllegalFileName,
                            Severity = PreflightSeverity.Error, Message = "文件名含 Windows 非法字符",
                            AutoFixable = true
                        });
                    if (Naming.IsReservedFileName(stem))
                        r.Issues.Add(new PreflightIssue
                        {
                            RowIndex = i, DrawNo = dn, Kind = PreflightIssueKind.ReservedFileName,
                            Severity = PreflightSeverity.Error, Message = "文件名是系统保留名称（CON / PRN / AUX / NUL / COMn / LPTn）"
                        });
                    if (fileCount.ContainsKey(stem) && fileCount[stem] > 1)
                        r.Issues.Add(new PreflightIssue
                        {
                            RowIndex = i, DrawNo = dn, Kind = PreflightIssueKind.DuplicateFileName,
                            Severity = PreflightSeverity.Error, Message = "文件名重复（忽略大小写）",
                            AutoFixable = true
                        });
                }

                if (dn.Length > 0 && dnCount.ContainsKey(dn) && dnCount[dn] > 1)
                    r.Issues.Add(new PreflightIssue
                    {
                        RowIndex = i, DrawNo = dn, Kind = PreflightIssueKind.DuplicateDrawNo,
                        Severity = PreflightSeverity.Warning, Message = "图号重复"
                    });

                if (!s.IsGeneralLayout)
                {
                    bool winOk = (s.WindowWcs.MaxPoint.X > s.WindowWcs.MinPoint.X
                                  && s.WindowWcs.MaxPoint.Y > s.WindowWcs.MinPoint.Y)
                              || (s.WindowDcs.MaxPoint.X > s.WindowDcs.MinPoint.X
                                  && s.WindowDcs.MaxPoint.Y > s.WindowDcs.MinPoint.Y);
                    if (!winOk)
                        r.Issues.Add(new PreflightIssue
                        {
                            RowIndex = i, DrawNo = dn, Kind = PreflightIssueKind.InvalidWindow,
                            Severity = PreflightSeverity.Error, Message = "图纸打印范围无效（窗口为空或反向）"
                        });
                    if (s.WidthMm <= 0 || s.HeightMm <= 0)
                        r.Issues.Add(new PreflightIssue
                        {
                            RowIndex = i, DrawNo = dn, Kind = PreflightIssueKind.InvalidSize,
                            Severity = PreflightSeverity.Error, Message = "图纸宽度/高度必须大于 0"
                        });
                    if (s.Config == null || s.Config.Paper == null
                        || s.Config.Paper.WidthMm <= 0 || s.Config.Paper.HeightMm <= 0)
                        r.Issues.Add(new PreflightIssue
                        {
                            RowIndex = i, DrawNo = dn, Kind = PreflightIssueKind.InvalidPaper,
                            Severity = PreflightSeverity.Error, Message = "纸张尺寸无效（未设置或宽高不大于 0）"
                        });
                    double den, num;
                    if (!TryEffectiveScale(s, cfg, out num, out den))
                        r.Issues.Add(new PreflightIssue
                        {
                            RowIndex = i, DrawNo = dn, Kind = PreflightIssueKind.InvalidScale,
                            Severity = PreflightSeverity.Error, Message = "比例参数不合理（分母必须为大于 0 的有限数）"
                        });
                }
            }

            // 输出目录必须可创建且可写
            if (!string.IsNullOrEmpty(outDir))
            {
                try
                {
                    if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                    string probe = Path.Combine(outDir, ".mplot_write_test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                }
                catch (Exception ex)
                {
                    r.Issues.Add(new PreflightIssue
                    {
                        RowIndex = -1, DrawNo = "", Kind = PreflightIssueKind.OutputDirNotWritable,
                        Severity = PreflightSeverity.Error, Message = "输出目录不可创建或不可写：" + ex.Message
                    });
                }
            }

            // 目标 PDF 已存在：必须明确提示（UI 层请用户选覆盖/跳过/另存为），默认不覆盖
            if (!string.IsNullOrEmpty(outDir))
            {
                for (int i = 0; i < sheets.Count; i++)
                {
                    SheetItem s = sheets[i];
                    if (s.Status == SheetStatus.Skipped) continue;
                    string stem = (s.FileStem ?? "").Trim();
                    if (stem.Length == 0) continue;
                    if (s.AllowOverwrite || cfg.OverwriteExisting) continue;
                    string target = Path.Combine(outDir, stem + ".pdf");
                    if (File.Exists(target) || File.Exists(Path.Combine(outDir, stem + ".PDF")))
                        r.Issues.Add(new PreflightIssue
                        {
                            RowIndex = i, DrawNo = (s.DrawNo ?? "").Trim(),
                            Kind = PreflightIssueKind.FileExists,
                            Severity = PreflightSeverity.Warning,
                            Message = "目标 PDF 已存在：" + stem + ".pdf"
                        });
                }
            }
            return r;
        }

        /// <summary>与 BuildRequest 同一套有效比例算法：算不出或不合理返回 false。</summary>
        private static bool TryEffectiveScale(SheetItem s, GlobalConfig cfg, out double num, out double den)
        {
            num = 1; den = 1;
            try
            {
                ScaleMode mode = cfg.ScaleMode;
                if (s.Config != null && s.Config.ScaleDenominator > 0)
                {
                    num = s.Config.ScaleNumerator > 0 ? s.Config.ScaleNumerator : 1;
                    den = s.Config.ScaleDenominator;
                    mode = ScaleMode.Custom;
                }
                else if (mode == ScaleMode.Custom)
                {
                    num = cfg.CustomScaleNumerator > 0 ? cfg.CustomScaleNumerator : 1;
                    den = cfg.CustomScaleDenominator > 0 ? cfg.CustomScaleDenominator : 1;
                }
                else if (mode == ScaleMode.AutoFromFrame)
                {
                    den = PaperMatcher.ComputeScaleDenominatorWithMargin(s.WidthMm, s.HeightMm,
                              s.Config, s.Rotated, cfg.WindowMarginMm);
                }
                else
                {
                    return true;   // ScaleToFit / OneToOne 不用数字比例
                }
                if (double.IsNaN(den) || double.IsInfinity(den) || double.IsNaN(num) || double.IsInfinity(num))
                    return false;
                return num > 0 && den > 0 && den <= 100000;
            }
            catch { return false; }
        }

        /// <summary>
        /// 由一张图纸 + 全局配置，算出真正下发 Plotter 的打印请求。
        /// 预览与实打共用本方法，保证「预览所见 = 实际所得」。
        /// </summary>
        private static PlotRequest BuildRequest(SheetItem s, int index, GlobalConfig cfg, string device,
                                                bool toFile, bool preserveLayout, string outDir,
                                                ScaleMode? forceScale, double forceDen)
        {
            string stem = toFile ? UniqueStem(Path.Combine(outDir, s.FileStem ?? ("图纸_" + (index + 1).ToString("00"))),
                                               s.AllowOverwrite || cfg.OverwriteExisting) : "";
            MediaInfo media = null;
            if (s.Config != null && s.Config.Paper != null && !preserveLayout)
            {
                PaperSpec p = s.Config.Paper;
                // 用标称尺寸选纸，且容差取负值：否则「841 + 1.5」装不下 A1 自己会跳到 A0，白边巨大。
                // 留白由比例缩小实现（见下方 ComputeScaleDenominatorWithMargin），纸张始终不放大。
                media = PaperMatcher.Match(device,
                    s.Rotated ? p.HeightMm : p.WidthMm,
                    s.Rotated ? p.WidthMm : p.HeightMm, false, -0.5);
            }

            double num = 1, den = 1;
            ScaleMode mode = forceScale ?? cfg.ScaleMode;
            if (mode == ScaleMode.Custom)
            {
                num = cfg.CustomScaleNumerator > 0 ? cfg.CustomScaleNumerator : 1;
                den = cfg.CustomScaleDenominator > 0 ? cfg.CustomScaleDenominator : 1;
                if (forceScale.HasValue && forceDen > 0) den = forceDen;
            }
            else if (mode == ScaleMode.AutoFromFrame)
            {
                // 四边留白：纸张不放大，改为按比例缩小内容留白（避免大一号纸的白边）
                den = PaperMatcher.ComputeScaleDenominatorWithMargin(s.WidthMm, s.HeightMm,
                          s.Config, s.Rotated, cfg.WindowMarginMm);
                if (forceDen > 0) den = forceDen;
            }
            if (s.Config != null && s.Config.ScaleDenominator > 0)
            {
                num = s.Config.ScaleNumerator > 0 ? s.Config.ScaleNumerator : 1;
                den = s.Config.ScaleDenominator;
                mode = ScaleMode.Custom;
            }
            if (preserveLayout) mode = ScaleMode.OneToOne;

            // 打印窗口在导出瞬间按当前视图重算 DCS：DCS 依赖当前视图，扫描时存的 DCS 在用户
            // 用「定位选中图纸」缩放查看、或预览改动视图后就过期了，拿旧值打印会打到空地方（白纸）。
            // WindowWcs 是模型坐标、与视图无关，是唯一可信的源；单张打印（PlotSingle）
            // 只给了 DCS 窗口且是刚框选的，仍用原值。
            Extents2d? windowDcs = null;
            if (!preserveLayout && !s.IsGeneralLayout)
            {
                if (s.WindowWcs.MaxPoint.X > s.WindowWcs.MinPoint.X
                    && s.WindowWcs.MaxPoint.Y > s.WindowWcs.MinPoint.Y)
                    windowDcs = AcadUtil.WcsRectToDcs(s.WindowWcs);
                else if (s.WindowDcs.HasValue)
                    windowDcs = s.WindowDcs;
            }

            return new PlotRequest
            {
                Device = device,
                MediaName = media == null ? "" : media.CanonicalName,
                OutputStem = stem,
                LayoutId = s.LayoutId,
                WindowDcs = windowDcs,
                ScaleMode = mode,
                ScaleNumerator = num,
                ScaleDenominator = den,
                Centered = cfg.PlotCentered,
                StyleSheet = EffectiveStyleSheet(cfg),
                PlotWithPlotStyles = cfg.PlotWithPlotStyles,
                PlotWithLineweights = cfg.PlotWithLineweights,
                Rotated90 = s.Rotated && cfg.RotateToBestFit,
                OriginX = cfg.PlotOffsetX,
                OriginY = cfg.PlotOffsetY,
                PlotToFile = toFile,
                PreserveLayoutSetup = preserveLayout,
                ShowProgress = false
            };
        }

        // ---------------------------------------------------------------- 核心流水线
        /// <summary>
        /// 批量导出入口（所有菜单入口最终都落到这里）。
        /// 本方法只做任务级安全保障，真正的流水线在 ExecuteCore：
        /// ① 每次任务一个独立 TaskId + 临时工作目录；② 任务期间 BACKGROUNDPLOT 等系统变量
        ///    改动在 finally 里恢复；③ 任务结束（成功/失败/取消）都清掉本次的临时目录。
        /// </summary>
        private static BatchResult Execute(List<SheetItem> sheets, bool toFile, bool preserveLayout,
                                           string deviceOverride, Action<int, int, string> progress,
                                           Func<bool> canceled, Point3d? tablePoint, bool confirmCleanup,
                                           BatchResult res, ScaleMode? forceScale = null, double forceDen = 0,
                                           List<SheetItem> fullScope = null, bool skipCatalogTable = false)
        {
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) return res;
            GlobalConfig cfg = GlobalConfig.Current;
            res.Sheets = sheets ?? new List<SheetItem>();

            // 任务 ID + 独立临时目录（本次任务单页 PDF 的唯一落点）
            res.TaskId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-"
                + Guid.NewGuid().ToString("N").Substring(0, 8);
            res.TempDir = Path.Combine(Path.GetTempPath(), "MPlotTask_" + res.TaskId);
            try { Directory.CreateDirectory(res.TempDir); }
            catch (Exception ex)
            {
                res.Warnings.Add("任务临时目录创建失败，改用输出目录直出：" + ex.Message);
                res.TempDir = "";
            }

            int bgOld = AcadUtil.SaveBackgroundPlot();
            AcadUtil.DisableBackgroundPlot();
            try
            {
                return ExecuteCore(sheets, toFile, preserveLayout, deviceOverride, progress, canceled,
                                   tablePoint, confirmCleanup, res, forceScale, forceDen, fullScope, skipCatalogTable);
            }
            finally
            {
                // 系统变量恢复 + 临时目录清理：成功/失败/取消/异常都走这里
                AcadUtil.RestoreBackgroundPlot(bgOld);
                DeleteTaskTempDir(res.TempDir);
            }
        }

        /// <summary>删除本次任务的临时工作目录（只删该目录内的东西，目录本身就是本次任务独占的）。</summary>
        private static void DeleteTaskTempDir(string tempDir)
        {
            if (string.IsNullOrEmpty(tempDir)) return;
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
            catch (Exception ex) { Logger.Warn("任务临时目录清理失败（不影响已出图）：" + ex.Message); }
        }

        private static BatchResult ExecuteCore(List<SheetItem> sheets, bool toFile, bool preserveLayout,
                                           string deviceOverride, Action<int, int, string> progress,
                                           Func<bool> canceled, Point3d? tablePoint, bool confirmCleanup,
                                           BatchResult res, ScaleMode? forceScale = null, double forceDen = 0,
                                           List<SheetItem> fullScope = null, bool skipCatalogTable = false)
        {
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) return res;
            GlobalConfig cfg = GlobalConfig.Current;
            res.Sheets = sheets ?? new List<SheetItem>();

            string outDir = cfg.EffectiveOutputDir(doc.Name);
            if (toFile)
            {
                try { if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir); }
                catch (Exception ex) { res.Warnings.Add("输出目录创建失败：" + ex.Message); outDir = FrameLibrary.EnsureDir(); }
            }
            res.OutputDir = outDir;

            // ---- 导出前二次校验：UI 层已经查过一次，这里是真正出图前的最后一道关 ----
            // 只对「导出到文件」的任务做；打到纸张的任务不走文件检查。
            if (toFile)
            {
                PreflightResult chk = PreflightCheck(sheets, outDir, cfg);
                if (chk.HasErrors)
                {
                    foreach (PreflightIssue iss in chk.Issues)
                    {
                        if (iss.Severity != PreflightSeverity.Error) continue;
                        if (iss.RowIndex >= 0 && iss.RowIndex < sheets.Count)
                        {
                            SheetItem bad = sheets[iss.RowIndex];
                            bad.Status = SheetStatus.Failed;
                            bad.Plotted = false;
                            bad.Error = iss.Message;
                        }
                        res.Warnings.Add("导出前检查未通过：" + iss.ToString());
                    }
                    Recount(res, fullScope ?? sheets);
                    Logger.Warn("导出前检查未通过，任务未开始导出");
                    return res;
                }
                foreach (PreflightIssue iss in chk.Issues)
                    if (iss.Severity == PreflightSeverity.Warning)
                        res.Warnings.Add("导出前检查提醒：" + iss.ToString());
            }


            string device = string.IsNullOrWhiteSpace(deviceOverride)
                ? (toFile ? AcadUtil.ResolvePdfDevice(cfg.PlotDevice) : cfg.DefaultPrinter)
                : deviceOverride;
            if (toFile) PaperMatcher.InvalidateCache();
            Logger.Info("开始批打：共 " + sheets.Count + " 张 / 设备 " + device + " / 输出 " + outDir);

            int total = sheets.Count;

            // ---- 打印前预览闸门：用与实打完全相同的 PlotRequest 渲染，所见即所得 ----
            // 图纸多时渲染较慢，所以只在输出到文件、且配置开启、且 UI 注册了预览回调时才做。
            // 预览是正式导出的前置确认步骤：预览失败绝不静默继续导出——用户必须在预览窗里
            // 选「重新生成预览」「放弃预览并确认直接导出」或「取消任务」；预览窗本身打不开时，
            // 回调方会先请用户定夺，这里收到异常只做最后一道防线：取消任务。
            if (toFile && cfg.PreviewBeforePrint && total > 0 && PreviewHook != null)
            {
                try
                {
                    List<PlotRequest> previewReqs = new List<PlotRequest>();
                    for (int i = 0; i < total; i++)
                    {
                        previewReqs.Add(BuildRequest(sheets[i], i, cfg, device, toFile, preserveLayout,
                                                     outDir, forceScale, forceDen));
                    }
                    if (progress != null) progress(0, total, "正在生成导出预览…");
                    string projectName = "";
                    try { projectName = FrameLibrary.Load().ProjectName ?? ""; } catch { }
                    PreviewDecision decision = PreviewHook(previewReqs, sheets, projectName);
                    if (decision == PreviewDecision.Canceled)
                    {
                        res.Canceled = true;
                        Logger.Info("用户在导出预览中取消，未生成任何 PDF");
                        return res;
                    }
                    if (decision == PreviewDecision.ContinueWithoutPreview)
                    {
                        res.Warnings.Add("用户在预览中选择了「不预览，直接导出」");
                        Logger.Warn("用户放弃预览、确认直接导出");
                    }
                    if (PreviewFailedLabels != null && PreviewFailedLabels.Count > 0)
                    {
                        res.PreviewFailed.AddRange(PreviewFailedLabels);
                        foreach (string f in PreviewFailedLabels)
                            res.Warnings.Add("预览生成失败：" + f);
                    }
                    PreviewFailedLabels = new List<string>();
                }
                catch (Exception ex)
                {
                    // 预览回调异常：不再退回「直接打印」——取消本次任务，绝不静默出图
                    res.Canceled = true;
                    res.Warnings.Add("导出预览失败，任务已取消（未生成 PDF）：" + ex.Message);
                    Logger.Warn("导出预览异常，任务取消：" + ex.Message);
                    return res;
                }
            }

            for (int i = 0; i < total; i++)
            {
                if (canceled != null && canceled()) { res.Canceled = true; break; }
                SheetItem s = sheets[i];
                if (s.Status == SheetStatus.Skipped)
                {
                    res.Skipped++;
                    Logger.Verbose("跳过 " + (s.DrawNo ?? s.FileStem) + "：" + (s.Error ?? ""));
                    continue;
                }
                string label = string.IsNullOrEmpty(s.DrawNo) ? (s.FileStem ?? ("第" + (i + 1) + "张")) : s.DrawNo;
                if (progress != null) progress(i + 1, total,
                    (toFile ? "正在导出 PDF " : "正在打印 ") + (i + 1) + "/" + total + "：" + label);

                s.Status = SheetStatus.Exporting;
                try
                {
                    PlotRequest req = BuildRequest(s, i, cfg, device, toFile, preserveLayout,
                                                   outDir, forceScale, forceDen);
                    req.ShowProgress = total == 1;

                    // 单张先落任务临时目录，校验通过后再移到最终文件名：
                    // 失败/取消都不会在输出目录里留下半成品，单张失败也不污染其他已成功的 PDF。
                    string finalStem = req.OutputStem;
                    string tempStem = "";
                    bool useTemp = toFile && !string.IsNullOrEmpty(res.TempDir);
                    if (useTemp)
                    {
                        tempStem = Path.Combine(res.TempDir, Path.GetFileName(finalStem));
                        req.OutputStem = tempStem;
                        s.TempPdfPath = tempStem + ".pdf";
                    }

                    int copies = toFile ? 1 : Math.Max(1, cfg.PaperCopies);
                    string produced = "", err = "";
                    bool ok = false;
                    for (int c = 0; c < copies; c++)
                    {
                        ok = Plotter.Plot(req, out produced, out err);
                        if (!ok) break;
                    }

                    if (ok && toFile)
                    {
                        // PDF 尚未完全写入时不能提前判定成功：校验文件头/尾部/大小/独占打开
                        string verr;
                        if (!Plotter.ValidatePdf(produced, out verr))
                        {
                            ok = false;
                            err = verr;
                            Logger.Warn("PDF 校验失败 " + label + "：" + verr);
                        }
                        else
                        {
                            string finalPdf = finalStem + ".pdf";
                            try
                            {
                                // 临时目录出图时才需要归位；临时目录不可用时的直出模式 produced 已是最终路径
                                if (!string.Equals(produced, finalPdf, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (File.Exists(finalPdf)) File.Delete(finalPdf);  // 已预留名或用户明确覆盖
                                    File.Move(produced, finalPdf);
                                    produced = finalPdf;
                                }
                                s.TempPdfPath = "";
                            }
                            catch (Exception ex)
                            {
                                ok = false;
                                err = "移到最终文件名失败：" + ex.Message;
                                Logger.Warn("PDF 归位失败 " + label + "：" + ex.Message);
                            }
                        }
                    }

                    s.PdfPath = ok ? produced : "";
                    s.Plotted = ok;
                    s.Error = ok ? "" : err;
                    s.Status = ok ? SheetStatus.Success : SheetStatus.Failed;
                    if (ok) { res.Success++; Logger.Verbose("OK " + label + " → " + produced); }
                    else
                    {
                        res.Failed++;
                        Logger.Warn("FAIL " + label + "：" + err);
                        // 失败的临时文件就地删掉，不留半成品，也不进输出目录
                        try { if (useTemp && File.Exists(tempStem + ".pdf")) File.Delete(tempStem + ".pdf"); } catch { }
                        try { if (useTemp && File.Exists(tempStem + ".PDF")) File.Delete(tempStem + ".PDF"); } catch { }
                        s.TempPdfPath = "";
                    }
                }
                catch (Exception ex)
                {
                    s.Plotted = false; s.Error = ex.Message;
                    s.Status = SheetStatus.Failed;
                    s.PdfPath = "";
                    s.TempPdfPath = "";
                    res.Failed++;
                    Logger.Error("导出异常 " + label, ex);
                }
            }

            if (res.Canceled) Logger.Warn("用户取消了批打，已完成 " + res.Success + " 张");

            // 按最终状态重算一次计数（重试时全量范围也要对得上）
            Recount(res, fullScope ?? sheets);

            // ---- PDF 合并 ----
            if (toFile && cfg.MergePdf && res.Success > 0)
            {
                if (canceled != null && canceled()) { res.Canceled = true; }
                else TryMerge(res, fullScope ?? sheets, outDir, cfg, progress, confirmCleanup);
            }

            // ---- 图纸目录（重试时用全量范围出目录，只是不再往 DWG 里插第二张表）----
            List<SheetItem> scope = fullScope ?? sheets;
            if (res.Success > 0)
            {
                if (cfg.CatalogCsv)
                {
                    string csv = Path.Combine(outDir, string.IsNullOrWhiteSpace(cfg.CatalogCsvName) ? "图纸目录.csv" : cfg.CatalogCsvName);
                    if (Catalog.WriteCsv(scope.Where(s => s.Plotted).ToList(), csv)) res.CsvPath = csv;
                    else res.Warnings.Add("图纸目录 CSV 写入失败，详见日志");
                }
                if (cfg.CatalogTable && !skipCatalogTable)
                {
                    Point3d pt = tablePoint ?? Point3d.Origin;
                    string terr;
                    if (Catalog.CreateTable(scope.Where(s => s.Plotted).ToList(), pt, cfg, out terr))
                        res.TableNote = "已在 DWG 内生成 CAD 原生目录表格";
                    else
                        res.Warnings.Add("CAD 原生目录表格生成失败：" + terr);
                }
            }

            // ---- 结果汇总文件（便于交付/复核）----
            if (toFile)
            {
                try
                {
                    string sum = Path.Combine(outDir, "批打结果.txt");
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("K-批量打印  批打结果");
                    sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    sb.AppendLine("任务: " + res.TaskId);
                    sb.AppendLine("图纸: " + Path.GetFileName(doc.Name));
                    sb.AppendLine("输出: " + outDir);
                    sb.AppendLine("总数: " + scope.Count + "  成功: " + res.Success + "  失败: " + res.Failed
                        + "  跳过: " + res.Skipped + "  取消: " + (res.Canceled ? "是" : "否"));
                    sb.AppendLine("合并: " + (string.IsNullOrEmpty(res.MergedPath) ? "未生成" : Path.GetFileName(res.MergedPath))
                        + (res.MergeFailed ? "（合并失败：" + res.MergeError + "）" : ""));
                    sb.AppendLine("目录: " + (string.IsNullOrEmpty(res.CsvPath) ? "未生成" : Path.GetFileName(res.CsvPath)));
                    sb.AppendLine("日志文件: " + Logger.SessionFile);
                    sb.AppendLine();
                    foreach (string w in res.Warnings) sb.AppendLine("[提示] " + w);
                    foreach (SheetItem s in scope)
                        sb.AppendLine("[" + SheetStatusNames.Text(s.Status) + "]" + s.Index.ToString("00") + "  "
                            + (s.FileStem ?? "") + ".pdf  " + (s.DrawNo ?? "") + "  " + (s.Title ?? "")
                            + (s.Status == SheetStatus.Failed || s.Status == SheetStatus.Skipped ? "  → " + (s.Error ?? "") : ""));
                    File.WriteAllText(sum, sb.ToString(), new UTF8Encoding(true));
                    res.SummaryFile = sum;
                }
                catch (Exception ex) { Logger.Verbose("结果汇总写入失败：" + ex.Message); }
            }

            Logger.Info(string.Format("批打结束：成功 {0} / 失败 {1} / 跳过 {2} / 取消 {3}",
                res.Success, res.Failed, res.Skipped, res.Canceled));
            return res;
        }

        /// <summary>按图纸最终状态重算成功/失败/跳过计数（重试时全量范围也要对得上）。</summary>
        private static void Recount(BatchResult res, List<SheetItem> scope)
        {
            int ok = 0, fail = 0, skip = 0;
            if (scope != null)
            {
                foreach (SheetItem s in scope)
                {
                    if (s.Status == SheetStatus.Success) ok++;
                    else if (s.Status == SheetStatus.Failed) fail++;
                    else if (s.Status == SheetStatus.Skipped) skip++;
                }
            }
            res.Success = ok; res.Failed = fail; res.Skipped = skip;
        }

        /// <summary>
        /// PDF 合并（内置 → GS 兜底，见 PdfMerger）。合并失败时单页 PDF 全部保留，
        /// 只记 MergeFailed / MergeError，不丢文件、不静默。合并成功且用户明确选了清理时，
        /// 只删本次输出目录内的单页文件（归属校验在 CleanupSingles 里）。
        /// </summary>
        private static bool TryMerge(BatchResult res, List<SheetItem> scope, string outDir, GlobalConfig cfg,
                                     Action<int, int, string> progress, bool confirmCleanup)
        {
            if (progress != null) progress(scope == null ? 0 : scope.Count, scope == null ? 0 : scope.Count, "正在合并 PDF 合集…");
            string merged = Path.Combine(outDir, string.IsNullOrWhiteSpace(cfg.MergedFileName) ? "图纸合集.pdf" : cfg.MergedFileName);
            merged = AcadUtil.UniquePath(merged);
            List<string> files = scope.Where(s => s.Plotted && !string.IsNullOrEmpty(s.PdfPath)).Select(s => s.PdfPath).ToList();
            string merr, channel;
            if (PdfMerger.Merge(files, merged, cfg, out merr, out channel))
            {
                res.MergedPath = merged;
                res.MergeFailed = false;
                res.MergeError = "";
                Logger.Info("合并完成（" + channel + "）：" + merged);
                if (!cfg.KeepSinglePdfsAfterMerge && confirmCleanup)
                {
                    int n = PdfMerger.CleanupSingles(files, merged, outDir);
                    res.Warnings.Add("已清理 " + n + " 个单页 PDF（目录里的 PDF 文件名仅作索引）");
                }
                return true;
            }
            res.MergeFailed = true;
            res.MergeError = merr;
            res.MergedPath = "";
            res.Warnings.Add("PDF 合并失败，单页 PDF 已全部保留：" + merr);
            Logger.Warn("PDF 合并失败（单页文件已保留）：" + merr);
            return false;
        }

        /// <summary>
        /// 只重试失败项：只对上次结果里状态为「失败」的图纸重新导出，成功的 PDF 一律不动。
        /// 复用同一个 SheetItem 对象（打印范围、图号/图名、文件名、纸张、比例、方向、CTB 全保留），
        /// 不新建任务、不丢用户在清单里改过的参数。重试前先检查目标 PDF：
        /// 若目标文件就是本张图上次失败留下的输出，先删掉再重出（归属明确，不会误覆盖他人文件）；
        /// 其他情况沿用预检 + 唯一命名规则。合并失败时不重出单页，只重跑合并的走 RetryMergeOnly。
        /// </summary>
        public static BatchResult RetrySheets(BatchResult previous, Action<int, int, string> progress, Func<bool> canceled)
        {
            BatchResult res = new BatchResult();
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) { res.Warnings.Add("没有打开的图纸"); return res; }
            if (previous == null || previous.Sheets == null)
            { res.Warnings.Add("没有可重试的任务结果"); return res; }
            List<SheetItem> failed = previous.Sheets.Where(s => s.Status == SheetStatus.Failed).ToList();
            if (failed.Count == 0) { res.Warnings.Add("没有失败的图纸可重试"); res.Sheets = previous.Sheets; return previous; }

            GlobalConfig cfg = GlobalConfig.Current;
            string outDir = previous.OutputDir;
            if (string.IsNullOrEmpty(outDir)) outDir = cfg.EffectiveOutputDir(doc.Name);

            foreach (SheetItem s in failed)
            {
                // 重试前再查目标 PDF：只删「本张图上次失败的输出」，其他文件一律不动
                string target = Path.Combine(outDir, (s.FileStem ?? "").Trim() + ".pdf");
                if (!string.IsNullOrEmpty(s.PdfPath)
                    && string.Equals(s.PdfPath, target, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(target))
                {
                    try { File.Delete(target); Logger.Verbose("重试前清理上次失败输出：" + target); } catch { }
                }
                s.Status = SheetStatus.Pending;
                s.Error = "";
                s.Plotted = false;
                s.PdfPath = "";
                s.TempPdfPath = "";
                s.AllowOverwrite = true;   // 同名重出是本次重试的本意，不是误覆盖
            }
            res.OutputDir = outDir;
            BatchResult r = Execute(failed, true, false, "", progress, canceled, null, true, res, null, 0,
                           previous.Sheets, true);
            r.Sheets = previous.Sheets;   // 结果窗看到的是整批任务，不只是重试的几张
            Recount(r, previous.Sheets);
            return r;
        }

        /// <summary>
        /// 只重跑合并：单页 PDF 全部成功、只有合集失败时用。不重新导出任何单页 PDF，
        /// 直接用上次结果里已成功的单页文件再合一次。
        /// </summary>
        public static BatchResult RetryMergeOnly(BatchResult previous, Action<int, int, string> progress, Func<bool> canceled)
        {
            if (previous == null) return previous;
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) { previous.Warnings.Add("没有打开的图纸"); return previous; }
            GlobalConfig cfg = GlobalConfig.Current;
            string outDir = previous.OutputDir;
            if (string.IsNullOrEmpty(outDir)) outDir = cfg.EffectiveOutputDir(doc.Name);
            if (canceled != null && canceled()) { previous.Canceled = true; return previous; }
            if (progress != null) progress(0, 1, "正在重新合并 PDF 合集…");
            TryMerge(previous, previous.Sheets, outDir, cfg, progress, true);
            return previous;
        }

        /// <summary>颜色快捷预设优先于手工 CTB；Custom 时完全尊重下拉选择。</summary>
        private static string EffectiveStyleSheet(GlobalConfig cfg)
        {
            switch (cfg.ColorMode)
            {
                case ColorMode.Color: return "";
                case ColorMode.Monochrome: return "monochrome.ctb";
                case ColorMode.Grayscale: return "Grayscale.ctb";
                default: return cfg.StyleSheet;
            }
        }

        /// <summary>不覆盖同名文件；按 stem 加 (2)(3)… 不用 GetFileNameWithoutExtension，避免图号里的点被截掉。</summary>
        private static string UniqueStem(string stem)
        {
            return UniqueStem(stem, GlobalConfig.Current.OverwriteExisting);
        }

        /// <summary>同上，但允许某张图纸单独覆盖（用户在检查里明确选了「覆盖」时）。</summary>
        private static string UniqueStem(string stem, bool allowOverwrite)
        {
            if (allowOverwrite) return stem;
            string cand = stem;
            for (int i = 2; i < 10000; i++)
            {
                if (!File.Exists(cand + ".pdf") && !File.Exists(cand + ".PDF")) return cand;
                cand = stem + "(" + i + ")";
            }
            return stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        /// <summary>给「目标文件已存在 → 另存为」用的唯一文件名：返回不带扩展名、与输出目录现有文件不冲突的主干。</summary>
        public static string UniqueStemFor(string outDir, string stem)
        {
            string full = UniqueStem(Path.Combine(outDir, stem));
            return Path.GetFileNameWithoutExtension(full);
        }
    }
}
