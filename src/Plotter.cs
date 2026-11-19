using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcCore = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MPlot
{
    public sealed class PlotRequest
    {
        public string Device { get; set; }
        public string MediaName { get; set; }
        /// <summary>输出文件「不含扩展名」的主干路径；DWG To PDF.pc3 会自动补 .pdf。</summary>
        public string OutputStem { get; set; }
        public ObjectId LayoutId { get; set; }
        /// <summary>DCS 坐标的打印窗口。null = 按布局打印（通用型）。</summary>
        public Extents2d? WindowDcs { get; set; }
        public ScaleMode ScaleMode { get; set; }
        public double ScaleNumerator { get; set; }
        public double ScaleDenominator { get; set; }
        public bool Centered { get; set; }
        public string StyleSheet { get; set; }
        public bool PlotWithPlotStyles { get; set; }
        public bool PlotWithLineweights { get; set; }
        /// <summary>线宽随打印比例缩放（PlotSettings.ScaleLineweights，AutoCAD 2014 实测可写）。</summary>
        public bool ScaleLineweights { get; set; }
        public bool Rotated90 { get; set; }
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public bool PlotToFile { get; set; }
        public bool ShowProgress { get; set; }
        /// <summary>通用型 / 批打纸张(旧版)：完全沿用布局自己的页面设置，只换输出设备。</summary>
        public bool PreserveLayoutSetup { get; set; }

        public PlotRequest()
        {
            PlotToFile = true;
            Centered = true;
            ScaleMode = ScaleMode.AutoFromFrame;
            ScaleNumerator = 1;
            ScaleDenominator = 1;
        }
    }

    /// <summary>
    /// PlotEngine 封装。所有「版本差异较大的可选设置」通过反射调用 validator，
    /// 这样换 AutoCAD / 天正版本不会因为某个方法签名变了直接编译失败。
    /// 核心链路（SetPlotWindowArea → SetPlotType → configuration → engine）按官方示例强类型调用。
    /// </summary>
    public static class Plotter
    {
        public static bool Plot(PlotRequest req, out string producedPath, out string error)
        {
            producedPath = "";
            error = "";
            if (req == null) { error = "打印请求为空"; return false; }

            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) { error = "没有打开的图纸"; return false; }
            Database db = doc.Database;
            DateTime started = DateTime.Now;

            bool plotBegun = false, docBegun = false, pageBegun = false;
            PlotEngine pe = null;
            PlotProgressDialog ppd = null;

            try
            {
                if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                {
                    Thread.Sleep(400);
                    if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                    { error = "AutoCAD 正在执行另一个打印任务，请稍后重试"; return false; }
                }

                ObjectId layoutId = req.LayoutId;
                if (layoutId.IsNull)
                {
                    using (Transaction t0 = db.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord cur = t0.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                        if (cur != null) layoutId = cur.LayoutId;
                        t0.Commit();
                    }
                }
                if (layoutId.IsNull) { error = "无法确定要打印的布局"; return false; }

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Layout lo = tr.GetObject(layoutId, OpenMode.ForRead) as Layout;
                    if (lo == null) { error = "布局对象无效"; return false; }

                    using (PlotSettings ps = new PlotSettings(lo.ModelType))
                    {
                        ps.CopyFrom(lo);
                        PlotSettingsValidator psv = PlotSettingsValidator.Current;
                        psv.RefreshLists(ps);

                        string device = req.Device;
                        if (string.IsNullOrWhiteSpace(device)) device = "DWG To PDF.pc3";

                        if (req.PreserveLayoutSetup)
                        {
                            // 通用型 / 旧版纸张批打：保留布局原有纸张、打印范围、比例，只换输出设备。
                            string keepMedia = TryGetProperty(ps, "CanonicalMediaName") as string;
                            psv.SetPlotConfigurationName(ps, device,
                                string.IsNullOrWhiteSpace(keepMedia) ? null : keepMedia);
                            if (!string.IsNullOrWhiteSpace(req.StyleSheet))
                                TryValidator(psv, "SetCurrentStyleSheet", ps, req.StyleSheet);
                            // 通用型同样保证居中：这里不碰打印原点，只显式设居中并回读校验。
                            ApplyCentered(ps, psv, req.Centered);
                            Logger.Verbose("沿用布局页面设置：" + (keepMedia ?? "(默认纸张)") + " / 居中=" + req.Centered);
                        }
                        else
                        {
                            string media = string.IsNullOrWhiteSpace(req.MediaName) ? null : req.MediaName;
                            psv.SetPlotConfigurationName(ps, device, media);

                            // ★ 官方示例明确：先设窗口范围，再设打印类型；顺序反了 AutoCAD 会报错。
                            if (req.WindowDcs.HasValue)
                            {
                                psv.SetPlotWindowArea(ps, req.WindowDcs.Value);
                                psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
                            }
                            else
                            {
                                psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                            }

                            // ---- 比例 ----
                            if (req.ScaleMode == ScaleMode.ScaleToFit)
                            {
                                psv.SetUseStandardScale(ps, true);
                                psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                            }
                            else
                            {
                                psv.SetUseStandardScale(ps, false);
                                double den = req.ScaleDenominator <= 0 ? 1 : req.ScaleDenominator;
                                double num = req.ScaleNumerator <= 0 ? 1 : req.ScaleNumerator;
                                psv.SetCustomPrintScale(ps, new CustomScale(num, den));
                            }

                            // ---- 可选设置（不同版本签名有差异，反射容错） ----
                            if (!string.IsNullOrWhiteSpace(req.StyleSheet))
                                TryValidator(psv, "SetCurrentStyleSheet", ps, req.StyleSheet);
                            // ★ 按本机 AutoCAD 2014 R19.1 程序集实测：
                            //   PlotSettingsValidator 里根本没有 SetPlotWithPlotStyles /
                            //   SetPlotWithLineweights；这些开关是 PlotSettings 上的可写属性
                            //   (PlotPlotStyles / PrintLineweights / ScaleLineweights)。
                            //   纸张单位枚举在本机叫 PlotPaperUnit（单数），不是 PlotPaperUnits。
                            TrySetProperty(ps, "PlotPlotStyles", req.PlotWithPlotStyles);
                            TrySetProperty(ps, "PrintLineweights", req.PlotWithLineweights);
                            TrySetProperty(ps, "ScaleLineweights", req.ScaleLineweights);
                            TryValidator(psv, "SetPlotPaperUnits", ps,
                                EnumVal("PlotPaperUnit", "Millimeters"));

                            // ★★ 居中失效的根因就在这里 ★★
                            // AutoCAD 里「设置打印原点」和「居中打印」互斥：任何一次 SetPlotOrigin
                            // 都会把居中标志清掉。旧代码无条件调用 SetPlotOrigin(0,0)，
                            // 于是前面刚设的居中又被抹掉，导致图纸贴边、不在纸张正中。
                            // 规则：居中开启时只用居中，绝不设原点；关闭居中时才用手动 X/Y 偏移。
                            if (req.Centered)
                                ApplyCentered(ps, psv, true);
                            else
                            {
                                TryValidator(psv, "SetPlotOrigin", ps, new Point2d(req.OriginX, req.OriginY));
                                TryValidator(psv, "SetPlotCentered", ps, false);
                            }
                            TryValidator(psv, "SetPlotRotation", ps,
                                EnumVal("PlotRotation", req.Rotated90 ? "Degrees090" : "Degrees000"));
                        }

                        PlotInfo pi = new PlotInfo();
                        pi.Layout = layoutId;
                        pi.OverrideSettings = ps;
                        PlotInfoValidator piv = new PlotInfoValidator();
                        piv.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                        piv.Validate(pi);

                        pe = PlotFactory.CreatePublishEngine();
                        ppd = new PlotProgressDialog(false, 1, req.ShowProgress);
                        ppd.IsVisible = req.ShowProgress;
                        ppd.LowerPlotProgressRange = 0;
                        ppd.UpperPlotProgressRange = 100;
                        ppd.PlotProgressPos = 0;
                        ppd.OnBeginPlot();

                        pe.BeginPlot(ppd, null);
                        plotBegun = true;

                        // 第 6 参：打印到文件时给「不带扩展名的输出路径」，PC3 补 .pdf；
                        // 打印到纸张时给 null。
                        string target = req.PlotToFile ? req.OutputStem : null;
                        pe.BeginDocument(pi, doc.Name, null, 1, req.PlotToFile, target);
                        docBegun = true;

                        ppd.OnBeginSheet();
                        PlotPageInfo ppi = new PlotPageInfo();
                        pe.BeginPage(ppi, pi, true, null);
                        pageBegun = true;
                        pe.BeginGenerateGraphics(null);
                        pe.EndGenerateGraphics(null);
                        pe.EndPage(null);
                        pageBegun = false;
                        ppd.OnEndSheet();
                        pe.EndDocument(null);
                        docBegun = false;
                        ppd.OnEndPlot();
                        pe.EndPlot(null);
                        plotBegun = false;
                    }
                    tr.Commit();
                }

                if (req.PlotToFile)
                {
                    string expected = req.OutputStem + ".pdf";
                    producedPath = ResolveProducedFile(req.OutputStem, expected, started);
                    if (string.IsNullOrEmpty(producedPath)) { error = "打印完成但没找到生成的 PDF"; return false; }
                }
                else
                {
                    producedPath = "(纸张)";
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Logger.Error("打印失败：" + req.OutputStem, ex);
                return false;
            }
            finally
            {
                // 任何异常都必须把 engine 状态收干净，否则 AutoCAD 会永远停在「正在打印」
                if (pe != null)
                {
                    try { if (pageBegun) pe.EndPage(null); } catch { }
                    try { if (docBegun) pe.EndDocument(null); } catch { }
                    try { if (plotBegun) { if (ppd != null) ppd.OnEndPlot(); pe.EndPlot(null); } } catch { }
                    try { pe.Dispose(); } catch { }
                }
                if (ppd != null) { try { ppd.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// 校验一份 PDF 是否完整可用（导出成功判定用，不只看「文件存在」）：
        /// 存在、大小 &gt; 0、文件头是 %PDF-、尾部能找到 %%EOF、且能独占打开（没被驱动继续写）。
        /// 校验失败返回 false + 具体原因；调用方不得把未通过校验的文件当成功结果。
        /// </summary>
        public static bool ValidatePdf(string path, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(path)) { error = "PDF 路径为空"; return false; }
            FileInfo fi;
            try { fi = new FileInfo(path); }
            catch (Exception ex) { error = "PDF 路径无效：" + ex.Message; return false; }
            if (!fi.Exists) { error = "PDF 文件不存在"; return false; }
            if (fi.Length <= 0) { error = "PDF 是空文件（0 字节）"; return false; }
            // 还在被写入的文件独占打开会失败：等一会儿再试，最多约 2 秒
            bool locked = false;
            for (int i = 0; i < 20; i++)
            {
                try { using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { } locked = false; break; }
                catch { locked = true; Thread.Sleep(100); }
            }
            if (locked) { error = "PDF 仍被占用、尚未完全写入"; return false; }
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < 5) { error = "PDF 文件过小"; return false; }
                    byte[] head = new byte[5];
                    if (fs.Read(head, 0, 5) != 5 || Encoding.ASCII.GetString(head) != "%PDF-")
                    { error = "PDF 文件头不是 %PDF-，不是有效 PDF"; return false; }
                    // 尾部 2KB 里找 %%EOF：没有就是没写完或被截断
                    long tailLen = Math.Min(2048, fs.Length);
                    byte[] tail = new byte[tailLen];
                    fs.Seek(fs.Length - tailLen, SeekOrigin.Begin);
                    if (fs.Read(tail, 0, (int)tailLen) != (int)tailLen) { error = "PDF 尾部读取失败"; return false; }
                    string tailText = Encoding.ASCII.GetString(tail);
                    if (tailText.IndexOf("%%EOF", StringComparison.Ordinal) < 0)
                    { error = "PDF 尾部缺少 %%EOF，文件未完全写入"; return false; }
                }
            }
            catch (Exception ex) { error = "PDF 校验失败：" + ex.Message; return false; }
            return true;
        }

        /// <summary>
        /// 输出文件名兜底：PC3 可能补 .pdf / .PDF，也可能在同名时改名字。
        /// 等文件真正落盘（&gt;0 字节）再返回；实在找不到就在目录里找刚生成的 PDF 挪过去。
        /// 安全红线：只认与期望主干同名的文件（驱动改名如 name(1).pdf 也认），
        /// 绝不把输出目录里任意一个最近修改的 PDF 当成当前任务的结果——
        /// 现在输出先落任务临时目录，目录里只可能是本次任务自己的文件。
        /// </summary>
        private static string ResolveProducedFile(string stem, string expected, DateTime started)
        {
            for (int i = 0; i < 25; i++)
            {
                if (File.Exists(expected)) { try { if (new FileInfo(expected).Length > 0) return expected; } catch { } }
                string upper = stem + ".PDF";
                if (File.Exists(upper)) return upper;
                if (File.Exists(stem)) return stem;   // 极少数驱动直接用主干名
                Thread.Sleep(100);
            }
            try
            {
                string dir = Path.GetDirectoryName(stem);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "";
                string wantName = Path.GetFileNameWithoutExtension(expected);
                FileInfo candidate = new DirectoryInfo(dir).GetFiles("*.pdf")
                    .Where(f => f.LastWriteTime >= started.AddSeconds(-2) && f.Length > 0
                        && f.Name.StartsWith(wantName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (candidate == null) return "";
                if (candidate.FullName.Equals(expected, StringComparison.OrdinalIgnoreCase)) return candidate.FullName;
                if (File.Exists(expected)) File.Delete(expected);
                File.Move(candidate.FullName, expected);
                Logger.Warn("PDF 输出名与预期不符，已归位：" + candidate.Name + " → " + Path.GetFileName(expected));
                return expected;
            }
            catch (Exception ex) { Logger.Warn("输出文件归位失败：" + ex.Message); return ""; }
        }

        private static object TryGetProperty(object target, string name)
        {
            try
            {
                PropertyInfo p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return p == null ? null : p.GetValue(target, null);
            }
            catch { return null; }
        }

        /// <summary>按属性名设置可选开关；版本间属性名不同时安静跳过，不影响打印主链路。</summary>
        private static bool TrySetProperty(object target, string name, object value)
        {
            try
            {
                PropertyInfo p = target.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return false;
                p.SetValue(target, value, null);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Verbose(name + " 属性设置跳过：" + ex.Message);
                return false;
            }
        }

        private static object EnumVal(string typeName, string valueName)
        {
            try
            {
                Type t = typeof(PlotSettings).Assembly.GetType("Autodesk.AutoCAD.DatabaseServices." + typeName, false);
                if (t == null || !t.IsEnum) return null;
                return Enum.Parse(t, valueName, true);
            }
            catch { return null; }
        }

        /// <summary>
        /// ★ 居中专用：设置后回读 PlotSettings.PlotCentered 校验，若为 false 再强制一次。
        /// 原因：AutoCAD 中任何一次 SetPlotOrigin（含 0,0）都会把居中关掉，且部分驱动/布局
        /// 在 CopyFrom 之后对居中标志的处理不一致。这里「设置 → 回读 → 补设」保证最终态一定是居中。
        /// 全程反射：新版属性名/类型不同就安静跳过，不影响打印主链路。
        /// </summary>
        private static void ApplyCentered(PlotSettings ps, PlotSettingsValidator psv, bool want)
        {
            bool validatorOk = TryValidator(psv, "SetPlotCentered", ps, want);
            TrySetProperty(ps, "PlotCentered", want);   // 有些版本属性可写，双保险

            object read = TryGetProperty(ps, "PlotCentered");
            if (read is bool)
            {
                if ((bool)read != want)
                {
                    // 回读不一致：再强制一轮（先属性后 validator，顺序与首次相反）
                    TrySetProperty(ps, "PlotCentered", want);
                    TryValidator(psv, "SetPlotCentered", ps, want);
                    object again = TryGetProperty(ps, "PlotCentered");
                    Logger.Verbose("居中回读不一致（" + read + "），已补设；补设后=" + again);
                }
                else
                {
                    Logger.Verbose("居中已确认=" + read + "（validator=" + validatorOk + "）");
                }
            }
            else
            {
                Logger.Verbose("PlotCentered 属性不可回读，按 validator=" + validatorOk + " 处理");
            }
        }

        /// <summary>
        /// 打印前预览：用与真实打印「完全相同」的窗口 / 比例 / 居中 / 旋转 / CTB，
        /// 只把输出设备换成 PNG，渲染成一张所见即所得的预览图。
        /// 输出到系统临时目录，绝不覆盖真实 PDF；失败返回空串 + 具体原因（调用方据此逐张记录，
        /// 不再用一句笼统的「预览失败」带过，更不许静默继续导出）。
        /// </summary>
        public static string RenderPreview(PlotRequest realReq)
        {
            string ignored;
            return RenderPreview(realReq, out ignored);
        }

        public static string RenderPreview(PlotRequest realReq, out string error)
        {
            error = "";
            if (realReq == null) { error = "预览请求为空"; return ""; }
            PlotEngine pe = null;
            PlotProgressDialog ppd = null;
            bool begun = false, docBegun = false, pageBegun = false;
            try
            {
                var doc = AcCore.DocumentManager.MdiActiveDocument;
                if (doc == null) { error = "没有打开的图纸"; return ""; }
                Database db = doc.Database;

                // 预览设备先行检查：本机没有 PublishToWeb PNG.pc3 时直接报具体原因，
                // 不要等渲染超时才给一句「预览生成失败」。
                try
                {
                    List<string> devices = AcadUtil.GetPlotDevices();
                    if (devices != null && devices.Count > 0
                        && !devices.Any(d => string.Equals(d, "PublishToWeb PNG.pc3",
                            StringComparison.OrdinalIgnoreCase)))
                    { error = "本机缺少 PublishToWeb PNG.pc3 预览设备，无法生成预览图"; return ""; }
                }
                catch { }

                string dir = Path.Combine(Path.GetTempPath(), "MPlotPrintPreview");
                try { Directory.CreateDirectory(dir); }
                catch { error = "预览临时目录创建失败"; return ""; }
                string stem = Path.Combine(dir, "preview_" + Guid.NewGuid().ToString("N"));

                ObjectId layoutId = realReq.LayoutId;
                if (layoutId.IsNull)
                {
                    using (Transaction t0 = db.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord cur = t0.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                        if (cur != null) layoutId = cur.LayoutId;
                        t0.Commit();
                    }
                }
                if (layoutId.IsNull) { error = "无法确定要打印的布局"; return ""; }

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Layout lo = tr.GetObject(layoutId, OpenMode.ForRead) as Layout;
                    if (lo == null) { error = "布局对象无效"; return ""; }
                    using (PlotSettings ps = new PlotSettings(lo.ModelType))
                    {
                        ps.CopyFrom(lo);
                        PlotSettingsValidator psv = PlotSettingsValidator.Current;
                        psv.RefreshLists(ps);

                        // 仅换设备与纸张：PNG 设备里挑一张宽高比最接近目标纸张的介质，
                        // 让预览的横竖、正文占比与最终 PDF 一致。
                        string pngMedia = PickPreviewMedia(psv, realReq.MediaName);
                        psv.SetPlotConfigurationName(ps, "PublishToWeb PNG.pc3", pngMedia);

                        if (realReq.WindowDcs.HasValue)
                        {
                            psv.SetPlotWindowArea(ps, realReq.WindowDcs.Value);
                            psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
                        }
                        else
                        {
                            psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                        }

                        if (realReq.ScaleMode == ScaleMode.ScaleToFit)
                        {
                            psv.SetUseStandardScale(ps, true);
                            psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                        }
                        else
                        {
                            psv.SetUseStandardScale(ps, false);
                            double den = realReq.ScaleDenominator <= 0 ? 1 : realReq.ScaleDenominator;
                            double num = realReq.ScaleNumerator <= 0 ? 1 : realReq.ScaleNumerator;
                            psv.SetCustomPrintScale(ps, new CustomScale(num, den));
                        }

                        if (!string.IsNullOrWhiteSpace(realReq.StyleSheet))
                            TryValidator(psv, "SetCurrentStyleSheet", ps, realReq.StyleSheet);
                        TrySetProperty(ps, "PlotPlotStyles", realReq.PlotWithPlotStyles);
                        TrySetProperty(ps, "PrintLineweights", realReq.PlotWithLineweights);
                        // 与真正出图保持一致：线宽随比例缩放也要同步，否则预览与成品线宽不一致
                        TrySetProperty(ps, "ScaleLineweights", realReq.ScaleLineweights);
                        TryValidator(psv, "SetPlotRotation", ps,
                            EnumVal("PlotRotation", realReq.Rotated90 ? "Degrees090" : "Degrees000"));
                        // 与真正出图同一个居中入口：回读校验，所见即所得。
                        if (realReq.Centered) ApplyCentered(ps, psv, true);
                        else
                        {
                            TryValidator(psv, "SetPlotOrigin", ps, new Point2d(realReq.OriginX, realReq.OriginY));
                            TryValidator(psv, "SetPlotCentered", ps, false);
                        }

                        PlotInfo pi = new PlotInfo();
                        pi.Layout = layoutId;
                        pi.OverrideSettings = ps;
                        PlotInfoValidator piv = new PlotInfoValidator();
                        piv.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                        piv.Validate(pi);

                        pe = PlotFactory.CreatePublishEngine();
                        ppd = new PlotProgressDialog(false, 1, false);
                        ppd.IsVisible = false;
                        ppd.LowerPlotProgressRange = 0; ppd.UpperPlotProgressRange = 100; ppd.PlotProgressPos = 0;
                        ppd.OnBeginPlot();
                        pe.BeginPlot(ppd, null); begun = true;
                        pe.BeginDocument(pi, doc.Name, null, 1, true, stem); docBegun = true;
                        ppd.OnBeginSheet();
                        pe.BeginPage(new PlotPageInfo(), pi, true, null); pageBegun = true;
                        pe.BeginGenerateGraphics(null);
                        pe.EndGenerateGraphics(null);
                        pe.EndPage(null); pageBegun = false;
                        ppd.OnEndSheet();
                        pe.EndDocument(null); docBegun = false;
                        pe.EndPlot(null); begun = false;
                    }
                    tr.Commit();
                }

                string[] candidates = { stem + ".png", stem + ".PNG", stem };
                for (int i = 0; i < 25; i++)
                {
                    foreach (string f in candidates)
                    {
                        if (File.Exists(f))
                        { try { if (new FileInfo(f).Length > 0) return f; } catch { } }
                    }
                    Thread.Sleep(100);
                }
                error = "预览图未生成（PNG 设备无输出）";
                return "";
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Logger.Verbose("预览渲染失败：" + ex.Message);
                return "";
            }
            finally
            {
                if (pe != null)
                {
                    try { if (pageBegun) pe.EndPage(null); } catch { }
                    try { if (docBegun) pe.EndDocument(null); } catch { }
                    try { if (begun) { if (ppd != null) ppd.OnEndPlot(); pe.EndPlot(null); } } catch { }
                    try { pe.Dispose(); } catch { }
                }
                if (ppd != null) { try { ppd.Dispose(); } catch { } }
            }
        }

        /// <summary>在 PNG 设备里挑一张宽高比最接近目标纸张的介质；挑不到返回 null（用默认）。</summary>
        private static string PickPreviewMedia(PlotSettingsValidator psv, string pdfMedia)
        {
            try
            {
                MediaInfo target = PaperMatcher.ParseMedia(pdfMedia);
                using (PlotSettings tmp = new PlotSettings(true))
                {
                    psv.SetPlotConfigurationName(tmp, "PublishToWeb PNG.pc3", null);
                    psv.RefreshLists(tmp);
                    System.Collections.Specialized.StringCollection names = psv.GetCanonicalMediaNameList(tmp);
                    if (names == null || names.Count == 0) return null;
                    if (target == null) return names[0];
                    double tr = target.WidthMm / Math.Max(1.0, target.HeightMm);
                    string best = names[0];
                    double bestErr = double.MaxValue;
                    foreach (string n in names)
                    {
                        MediaInfo m = PaperMatcher.ParseMedia(n);
                        if (m == null) continue;
                        double err = Math.Abs(Math.Log(m.WidthMm / Math.Max(1.0, m.HeightMm)) - Math.Log(tr));
                        if (err < bestErr) { bestErr = err; best = n; }
                    }
                    return best;
                }
            }
            catch { return null; }
        }

        /// <summary>反射调用 PlotSettingsValidator 的可选方法；方法不存在/签名不符就安静跳过。</summary>
        private static bool TryValidator(PlotSettingsValidator psv, string method, params object[] args)
        {
            if (psv == null || args.Any(a => a == null)) return false;
            try
            {
                MethodInfo[] methods = psv.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == method && m.GetParameters().Length == args.Length)
                    .ToArray();
                foreach (MethodInfo m in methods)
                {
                    try { m.Invoke(psv, args); return true; }
                    catch (Exception ex) { Logger.Verbose(method + " 调用跳过：" + (ex.InnerException ?? ex).Message); }
                }
            }
            catch (Exception ex) { Logger.Verbose(method + " 反射失败：" + ex.Message); }
            return false;
        }
    }

    /// <summary>
    /// 图框缩略图生成：用 AutoCAD 自带 PublishToWeb PNG.pc3 把每个框选范围渲染成 PNG。
    /// 只做展示，失败安静跳过，绝不阻断批打主流程。输出在系统临时目录 MPlotThumbs。
    /// </summary>
    public static class ThumbnailMaker
    {
        private static readonly string Dir = Path.Combine(Path.GetTempPath(), "MPlotThumbs");

        /// <summary>批量生成缩略图，返回与输入一一对应的 PNG 路径（生成失败的那项为 ""）。</summary>
        public static List<string> Generate(List<Extents2d> rectsWcs)
        {
            List<string> files = new List<string>();
            if (rectsWcs == null) return files;
            try { Directory.CreateDirectory(Dir); }
            catch (Exception ex) { Logger.Verbose("缩略图目录创建失败：" + ex.Message); return files; }
            // 清掉上一批的图
            try
            {
                foreach (string old in Directory.GetFiles(Dir, "mplot_thumb_*.png"))
                { try { File.Delete(old); } catch { } }
            }
            catch { }

            for (int i = 0; i < rectsWcs.Count; i++)
            {
                string stem = Path.Combine(Dir, "mplot_thumb_" + i.ToString("000"));
                files.Add(MakeOne(rectsWcs[i], stem) ? stem + ".png" : "");
            }
            return files;
        }

        private static bool MakeOne(Extents2d wcs, string stem)
        {
            PlotEngine pe = null;
            PlotProgressDialog ppd = null;
            bool begun = false, docBegun = false, pageBegun = false;
            DateTime started = DateTime.Now;
            try
            {
                var doc = AcCore.DocumentManager.MdiActiveDocument;
                if (doc == null) return false;
                Database db = doc.Database;

                ObjectId layoutId = ObjectId.Null;
                using (Transaction t0 = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord cur = t0.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                    if (cur != null) layoutId = cur.LayoutId;
                    t0.Commit();
                }
                if (layoutId.IsNull) return false;

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Layout lo = tr.GetObject(layoutId, OpenMode.ForRead) as Layout;
                    if (lo == null) return false;
                    using (PlotSettings ps = new PlotSettings(lo.ModelType))
                    {
                        ps.CopyFrom(lo);
                        PlotSettingsValidator psv = PlotSettingsValidator.Current;
                        psv.RefreshLists(ps);

                        // 图片打印机；本机 AutoCAD 2014 自带，找得到就用，找不到安静失败。
                        psv.SetPlotConfigurationName(ps, "PublishToWeb PNG.pc3", null);

                        Extents2d dcs = AcadUtil.WcsRectToDcs(wcs);
                        psv.SetPlotWindowArea(ps, dcs);
                        psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
                        psv.SetUseStandardScale(ps, true);
                        psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                        psv.SetPlotCentered(ps, true);

                        PlotInfo pi = new PlotInfo();
                        pi.Layout = layoutId;
                        pi.OverrideSettings = ps;
                        PlotInfoValidator piv = new PlotInfoValidator();
                        piv.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                        piv.Validate(pi);

                        pe = PlotFactory.CreatePublishEngine();
                        ppd = new PlotProgressDialog(false, 1, false);
                        ppd.IsVisible = false;
                        ppd.LowerPlotProgressRange = 0; ppd.UpperPlotProgressRange = 100; ppd.PlotProgressPos = 0;
                        ppd.OnBeginPlot();
                        pe.BeginPlot(ppd, null); begun = true;
                        pe.BeginDocument(pi, doc.Name, null, 1, true, stem); docBegun = true;
                        ppd.OnBeginSheet();
                        pe.BeginPage(new PlotPageInfo(), pi, true, null); pageBegun = true;
                        pe.BeginGenerateGraphics(null);
                        pe.EndGenerateGraphics(null);
                        pe.EndPage(null); pageBegun = false;
                        ppd.OnEndSheet();
                        pe.EndDocument(null); docBegun = false;
                        pe.EndPlot(null); begun = false;
                    }
                    tr.Commit();
                }

                // PNG 驱动可能补 .png / .PNG，等文件真正落盘
                string[] candidates = { stem + ".png", stem + ".PNG", stem };
                for (int i = 0; i < 25; i++)
                {
                    foreach (string f in candidates)
                    {
                        if (File.Exists(f))
                        { try { if (new FileInfo(f).Length > 0) return true; } catch { } }
                    }
                    Thread.Sleep(100);
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Verbose("缩略图生成跳过：" + ex.Message);
                return false;
            }
            finally
            {
                if (pe != null)
                {
                    try { if (pageBegun) pe.EndPage(null); } catch { }
                    try { if (docBegun) pe.EndDocument(null); } catch { }
                    try { if (begun) { if (ppd != null) ppd.OnEndPlot(); pe.EndPlot(null); } } catch { }
                    try { pe.Dispose(); } catch { }
                }
                if (ppd != null) { try { ppd.Dispose(); } catch { } }
            }
        }
    }
}
