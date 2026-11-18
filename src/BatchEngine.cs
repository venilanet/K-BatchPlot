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
        public bool Canceled { get; set; }
        public string OutputDir { get; set; }
        public string MergedPath { get; set; }
        public string CsvPath { get; set; }
        public string TableNote { get; set; }
        public string SummaryFile { get; set; }
        public BatchResult()
        {
            Sheets = new List<SheetItem>();
            Warnings = new List<string>();
            OutputDir = ""; MergedPath = ""; CsvPath = ""; TableNote = ""; SummaryFile = "";
        }
    }

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
        /// 返回 true=确认打印 / false=取消。BatchEngine 不直接依赖 WinForms，保持可测。
        /// </summary>
        public static Func<List<PlotRequest>, List<SheetItem>, string, bool> PreviewHook;

        /// <summary>
        /// 由一张图纸 + 全局配置，算出真正下发 Plotter 的打印请求。
        /// 预览与实打共用本方法，保证「预览所见 = 实际所得」。
        /// </summary>
        private static PlotRequest BuildRequest(SheetItem s, int index, GlobalConfig cfg, string device,
                                                bool toFile, bool preserveLayout, string outDir,
                                                ScaleMode? forceScale, double forceDen)
        {
            string stem = toFile ? UniqueStem(Path.Combine(outDir, s.FileStem ?? ("图纸_" + (index + 1).ToString("00")))) : "";
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

            return new PlotRequest
            {
                Device = device,
                MediaName = media == null ? "" : media.CanonicalName,
                OutputStem = stem,
                LayoutId = s.LayoutId,
                WindowDcs = preserveLayout ? (Extents2d?)null : (s.IsGeneralLayout ? (Extents2d?)null : s.WindowDcs),
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
        private static BatchResult Execute(List<SheetItem> sheets, bool toFile, bool preserveLayout,
                                           string deviceOverride, Action<int, int, string> progress,
                                           Func<bool> canceled, Point3d? tablePoint, bool confirmCleanup,
                                           BatchResult res, ScaleMode? forceScale = null, double forceDen = 0)
        {
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) return res;
            GlobalConfig cfg = GlobalConfig.Current;
            res.Sheets = sheets ?? new List<SheetItem>();

            AcadUtil.DisableBackgroundPlot();
            string outDir = cfg.EffectiveOutputDir(doc.Name);
            if (toFile)
            {
                try { if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir); }
                catch (Exception ex) { res.Warnings.Add("输出目录创建失败：" + ex.Message); outDir = FrameLibrary.EnsureDir(); }
            }
            res.OutputDir = outDir;

            string device = string.IsNullOrWhiteSpace(deviceOverride)
                ? (toFile ? AcadUtil.ResolvePdfDevice(cfg.PlotDevice) : cfg.DefaultPrinter)
                : deviceOverride;
            if (toFile) PaperMatcher.InvalidateCache();
            Logger.Info("开始批打：共 " + sheets.Count + " 张 / 设备 " + device + " / 输出 " + outDir);

            int total = sheets.Count;

            // ---- 打印前预览闸门：用与实打完全相同的 PlotRequest 渲染，所见即所得 ----
            // 图纸多时渲染较慢，所以只在输出到文件、且配置开启、且 UI 注册了预览回调时才做。
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
                    if (progress != null) progress(0, total, "正在生成打印预览…");
                    string projectName = "";
                    try { projectName = FrameLibrary.Load().ProjectName ?? ""; } catch { }
                    bool go = PreviewHook(previewReqs, sheets, projectName);
                    if (!go) { res.Canceled = true; Logger.Info("用户在打印预览中取消"); return res; }
                }
                catch (Exception ex)
                {
                    // 预览失败绝不阻断批打，退回「直接打印」并记录告警
                    Logger.Warn("打印预览失败，改为直接打印：" + ex.Message);
                    res.Warnings.Add("打印预览失败，已直接打印：" + ex.Message);
                }
            }

            for (int i = 0; i < total; i++)
            {
                if (canceled != null && canceled()) { res.Canceled = true; break; }
                SheetItem s = sheets[i];
                string label = string.IsNullOrEmpty(s.DrawNo) ? (s.FileStem ?? ("第" + (i + 1) + "张")) : s.DrawNo;
                if (progress != null) progress(i + 1, total, "正在打印 " + (i + 1) + "/" + total + "：" + label);

                try
                {
                    PlotRequest req = BuildRequest(s, i, cfg, device, toFile, preserveLayout,
                                                   outDir, forceScale, forceDen);
                    req.ShowProgress = total == 1;

                    int copies = toFile ? 1 : Math.Max(1, cfg.PaperCopies);
                    string produced = "", err = "";
                    bool ok = false;
                    for (int c = 0; c < copies; c++)
                    {
                        ok = Plotter.Plot(req, out produced, out err);
                        if (!ok) break;
                    }
                    s.PdfPath = produced;
                    s.Plotted = ok;
                    s.Error = ok ? "" : err;
                    if (ok) { res.Success++; Logger.Verbose("OK " + label + " → " + produced); }
                    else { res.Failed++; Logger.Warn("FAIL " + label + "：" + err); }
                }
                catch (Exception ex)
                {
                    s.Plotted = false; s.Error = ex.Message;
                    res.Failed++;
                    Logger.Error("打印异常 " + label, ex);
                }
            }

            if (res.Canceled) Logger.Warn("用户取消了批打，已完成 " + res.Success + " 张");

            // ---- PDF 合并 ----
            if (toFile && cfg.MergePdf && res.Success > 0)
            {
                if (canceled != null && canceled()) { res.Canceled = true; }
                else
                {
                    if (progress != null) progress(total, total, "正在合并 PDF 合集…");
                    string merged = Path.Combine(outDir, string.IsNullOrWhiteSpace(cfg.MergedFileName) ? "图纸合集.pdf" : cfg.MergedFileName);
                    merged = AcadUtil.UniquePath(merged);
                    List<string> files = sheets.Where(s => s.Plotted && !string.IsNullOrEmpty(s.PdfPath)).Select(s => s.PdfPath).ToList();
                    string merr, channel;
                    if (PdfMerger.Merge(files, merged, cfg, out merr, out channel))
                    {
                        res.MergedPath = merged;
                        Logger.Info("合并完成（" + channel + "）：" + merged);
                        if (!cfg.KeepSinglePdfsAfterMerge && confirmCleanup)
                        {
                            int n = PdfMerger.CleanupSingles(files, merged);
                            res.Warnings.Add("已清理 " + n + " 个单页 PDF（目录里的 PDF 文件名仅作索引）");
                        }
                    }
                    else
                    {
                        res.Warnings.Add("PDF 合并失败，单页 PDF 已全部保留：" + merr);
                    }
                }
            }

            // ---- 图纸目录 ----
            if (res.Success > 0)
            {
                if (cfg.CatalogCsv)
                {
                    string csv = Path.Combine(outDir, string.IsNullOrWhiteSpace(cfg.CatalogCsvName) ? "图纸目录.csv" : cfg.CatalogCsvName);
                    if (Catalog.WriteCsv(sheets.Where(s => s.Plotted).ToList(), csv)) res.CsvPath = csv;
                    else res.Warnings.Add("图纸目录 CSV 写入失败，详见日志");
                }
                if (cfg.CatalogTable)
                {
                    Point3d pt = tablePoint ?? Point3d.Origin;
                    string terr;
                    if (Catalog.CreateTable(sheets.Where(s => s.Plotted).ToList(), pt, cfg, out terr))
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
                    sb.AppendLine("图纸: " + Path.GetFileName(doc.Name));
                    sb.AppendLine("输出: " + outDir);
                    sb.AppendLine("总数: " + total + "  成功: " + res.Success + "  失败: " + res.Failed);
                    sb.AppendLine("合并: " + (string.IsNullOrEmpty(res.MergedPath) ? "未生成" : Path.GetFileName(res.MergedPath)));
                    sb.AppendLine("目录: " + (string.IsNullOrEmpty(res.CsvPath) ? "未生成" : Path.GetFileName(res.CsvPath)));
                    sb.AppendLine("日志文件: " + Logger.SessionFile);
                    sb.AppendLine();
                    foreach (string w in res.Warnings) sb.AppendLine("[提示] " + w);
                    foreach (SheetItem s in sheets)
                        sb.AppendLine((s.Plotted ? "[OK]  " : "[FAIL]") + s.Index.ToString("00") + "  "
                            + (s.FileStem ?? "") + ".pdf  " + (s.Plotted ? "" : s.Error ?? ""));
                    File.WriteAllText(sum, sb.ToString(), new UTF8Encoding(true));
                    res.SummaryFile = sum;
                }
                catch (Exception ex) { Logger.Verbose("结果汇总写入失败：" + ex.Message); }
            }

            Logger.Info(string.Format("批打结束：成功 {0} / 失败 {1} / 取消 {2}", res.Success, res.Failed, res.Canceled));
            return res;
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
            if (GlobalConfig.Current.OverwriteExisting) return stem;
            string cand = stem;
            for (int i = 2; i < 10000; i++)
            {
                if (!File.Exists(cand + ".pdf") && !File.Exists(cand + ".PDF")) return cand;
                cand = stem + "(" + i + ")";
            }
            return stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }
    }
}
