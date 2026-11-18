using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;

namespace MPlot
{
    public sealed class MediaInfo
    {
        public string CanonicalName { get; set; }
        public string LocaleName { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public override string ToString()
        {
            return string.Format("{0}  {1:0.#}×{2:0.#}mm", LocaleName ?? CanonicalName, WidthMm, HeightMm);
        }
    }

    public sealed class MediaChoice
    {
        public MediaInfo Media { get; set; }
        public bool Rotated { get; set; }
        public double ScaleDenominator { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// 纸张匹配。
    /// 为什么不调用 PlotSettingsValidator.SetClosestMediaName()：Autodesk 官方文档在该方法的说明里
    /// 直接写着「This function is not implemented」，调用它等于没调。
    /// 所以这里改为读 PC3 的 canonical media 列表，从名字（ISO_A1_(841.00_x_594.00_MM)）
    /// 里解析真实毫米尺寸，再自己挑最合适的纸张。顺带支持加长规格和自定义宽高。
    /// </summary>
    public static class PaperMatcher
    {
        private static readonly Regex StrictSize = new Regex(
            @"\(\s*(\d+(?:\.\d+)?)\s*_?x_?\s*(\d+(?:\.\d+)?)\s*_?(MM|Inches|Mils)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LooseSize = new Regex(
            @"(\d+(?:\.\d+)?)\s*_?x_?\s*(\d+(?:\.\d+)?)\s*_?(MM|Inches|Mils)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, MediaInfo> _cache = new Dictionary<string, MediaInfo>(StringComparer.OrdinalIgnoreCase);
        private static List<MediaInfo> _cachedList;
        private static string _cachedDevice = "";
        private static readonly object _lock = new object();

        public static void InvalidateCache()
        {
            lock (_lock) { _cachedList = null; _cachedDevice = ""; _cache.Clear(); }
        }

        /// <summary>列出指定 PC3 支持的全部纸张（含从名字解析出的 mm 尺寸）。</summary>
        public static List<MediaInfo> ListMedia(string device)
        {
            lock (_lock)
            {
                if (_cachedList != null && string.Equals(_cachedDevice, device, StringComparison.OrdinalIgnoreCase))
                    return new List<MediaInfo>(_cachedList);

                List<MediaInfo> result = new List<MediaInfo>();
                try
                {
                    PlotSettingsValidator psv = PlotSettingsValidator.Current;
                    using (PlotSettings tmp = new PlotSettings(true))
                    {
                        psv.SetPlotConfigurationName(tmp, device, null);
                        psv.RefreshLists(tmp);
                        StringCollection names = psv.GetCanonicalMediaNameList(tmp);
                        if (names != null)
                        {
                            foreach (string n in names)
                            {
                                if (string.IsNullOrWhiteSpace(n)) continue;
                                MediaInfo mi = ParseMedia(n);
                                if (mi == null) continue;         // 无尺寸的自定纸张名不参与自动匹配
                                mi.LocaleName = TryLocale(psv, tmp, n);
                                if (result.Any(x => x.CanonicalName == n)) continue;
                                result.Add(mi);
                            }
                        }
                    }
                }
                catch (Exception ex) { Logger.Warn("读取纸张列表失败：" + ex.Message); }

                result = result.OrderBy(x => x.WidthMm * x.HeightMm).ToList();
                _cachedList = result;
                _cachedDevice = device;
                return new List<MediaInfo>(result);
            }
        }

        private static string TryLocale(PlotSettingsValidator psv, PlotSettings ps, string canonical)
        {
            try
            {
                MethodInfo mi = psv.GetType().GetMethod("GetLocaleMediaName",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(PlotSettings), typeof(string) }, null);
                if (mi != null)
                {
                    object o = mi.Invoke(psv, new object[] { ps, canonical });
                    if (o != null && !string.IsNullOrWhiteSpace(o.ToString())) return o.ToString();
                }
            }
            catch { }
            return canonical;
        }

        public static MediaInfo ParseMedia(string canonical)
        {
            if (string.IsNullOrEmpty(canonical)) return null;
            Match m = StrictSize.Match(canonical);
            if (!m.Success) m = LooseSize.Match(canonical);
            if (!m.Success) return null;

            double w, h;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out w)) return null;
            if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out h)) return null;
            string unit = m.Groups[3].Value.ToLowerInvariant();
            double factor = unit.StartsWith("inch") ? 25.4 : (unit == "mils" ? 0.0254 : 1.0);
            w *= factor; h *= factor;
            if (w <= 0 || h <= 0 || w > 10000 || h > 10000) return null;
            return new MediaInfo { CanonicalName = canonical, LocaleName = canonical, WidthMm = w, HeightMm = h };
        }

        /// <summary>
        /// 为「已按 mm 换算的图框外框」挑一张 PC3 里真实存在的纸。
        /// 原则：优先选宽高都能装下、余量最小的；实在装不下就选最大的（宁可留白不裁图）。
        /// </summary>
        public static MediaInfo Match(string device, double needWMm, double needHMm, bool allowRotate)
        {
            return Match(device, needWMm, needHMm, allowRotate, 1.5);
        }

        /// <summary>
        /// pad 可指定：默认 1.5mm 防浮点误差；留白模式下必须传 ≤0 的容差，
        /// 否则「标称 A1=841mm + 1.5mm」会装不下 A1 自己而跳级到 A0，打印出来白边巨大。
        /// </summary>
        public static MediaInfo Match(string device, double needWMm, double needHMm, bool allowRotate, double pad)
        {
            List<MediaInfo> all = ListMedia(device);
            if (all.Count == 0) return null;

            double nw = needWMm + pad, nh = needHMm + pad;
            List<MediaInfo> candidates = new List<MediaInfo>(all);
            if (allowRotate)
            {
                foreach (MediaInfo m in all)
                    candidates.Add(new MediaInfo
                    {
                        CanonicalName = m.CanonicalName, LocaleName = m.LocaleName + " (旋转90°)",
                        WidthMm = m.HeightMm, HeightMm = m.WidthMm
                    });
            }

            MediaInfo fit = candidates
                .Where(m => m.WidthMm >= nw && m.HeightMm >= nh)
                .OrderBy(m => (m.WidthMm - nw) + (m.HeightMm - nh))
                .ThenBy(m => m.WidthMm * m.HeightMm)
                .FirstOrDefault();
            if (fit != null) return fit;

            // 装不下：取面积最接近且尽量大的一张，并在日志里明确说明会留白/缩放
            MediaInfo largest = candidates.OrderByDescending(m => m.WidthMm * m.HeightMm).FirstOrDefault();
            Logger.Warn(string.Format("PC3 里没有能容纳 {0:0.#}×{1:0.#}mm 的纸张，改用最大纸张 {2}；建议给该 PC3 添加自定义图纸尺寸。",
                needWMm, needHMm, largest == null ? "(无)" : largest.CanonicalName));
            return largest;
        }

        /// <summary>
        /// 出图比例分母。核心公式：图框在模型空间的实际毫米宽 ÷ 库里登记的标称纸张宽。
        /// 1:1 画的 A1 图框 → 841/841 = 1；按 1:100 放大的 A1 图框 → 84100/841 = 100。
        /// 注意分母用的是「标称纸张」而不是「实际选中的 PC3 纸张」，否则选到大一号纸会把图放大，比例就错了。
        /// </summary>
        public static double ComputeScaleDenominator(double bboxWMm, double bboxHMm, FrameEntry cfg, bool rotated)
        {
            if (cfg == null || cfg.Paper == null) return 1;
            double paperW = cfg.Paper.WidthMm, paperH = cfg.Paper.HeightMm;
            if (paperW <= 0 || paperH <= 0) return 1;

            double modelW = rotated ? bboxHMm : bboxWMm;
            double modelH = rotated ? bboxWMm : bboxHMm;
            double rx = modelW / paperW;
            double ry = modelH / paperH;
            double r = Math.Max(rx, ry);        // 取较保守的一个，保证四周都能装下
            if (r < 0.01) r = 1;

            // 吸附到常见出图比例，避免 1:97.31 这种因为图框画得不够精确产生的怪值
            double[] common = { 1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50,
                                75, 100, 125, 150, 200, 250, 300, 400, 500, 600, 750, 1000, 1500, 2000, 5000 };
            foreach (double c in common)
                if (Math.Abs(r - c) / c < 0.015) return c;
            return Math.Round(r, 4);
        }

        /// <summary>
        /// 「四边留白」专用比例分母：纸张保持标称尺寸不变，把内容按比例缩小，
        /// 使图框四周在纸上各留 marginMm。避免传统做法「外扩打印窗口」导致纸张跳级、白边巨大。
        /// 公式：可用幅面 = 标称纸张 - 2×留白；比例 = 图框实际尺寸 ÷ 可用幅面。
        /// 与 ComputeScaleDenominator 一样吸附常见比例，避免 1:97.3 这种怪值。
        /// </summary>
        public static double ComputeScaleDenominatorWithMargin(double bboxWMm, double bboxHMm,
                                                               FrameEntry cfg, bool rotated, double marginMm)
        {
            double baseDen = ComputeScaleDenominator(bboxWMm, bboxHMm, cfg, rotated);
            if (cfg == null || cfg.Paper == null || marginMm <= 0) return baseDen;
            double paperW = rotated ? cfg.Paper.HeightMm : cfg.Paper.WidthMm;
            double paperH = rotated ? cfg.Paper.WidthMm : cfg.Paper.HeightMm;
            if (paperW <= 2 * marginMm || paperH <= 2 * marginMm) return baseDen;

            // 内容必须缩到 (纸张-2margin) 内：在原有比例基础上再放大 纸张/(纸张-2margin) 倍
            double fx = paperW / (paperW - 2 * marginMm);
            double fy = paperH / (paperH - 2 * marginMm);
            double den = baseDen * Math.Max(fx, fy);
            if (den < 0.01) den = 1;

            // 只有接近标准比例时才吸附；留白本来就会产生零点几的非标偏差，不强吸附到整数
            double[] common = { 1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50,
                                75, 100, 125, 150, 200, 250, 300, 400, 500, 600, 750, 1000, 1500, 2000, 5000 };
            foreach (double c in common)
                if (Math.Abs(den - c) / c < 0.004) return c;
            return Math.Round(den, 4);
        }

        /// <summary>给设置面板用：把库里登记的纸张转成 PC3 里可用的 canonical 名。</summary>
        public static string ResolveFor(string device, PaperSpec paper, bool rotated)
        {
            double w = rotated ? paper.HeightMm : paper.WidthMm;
            double h = rotated ? paper.WidthMm : paper.HeightMm;
            MediaInfo m = Match(device, w, h, false);
            return m == null ? "" : m.CanonicalName;
        }
    }
}
