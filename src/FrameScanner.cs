using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace MPlot
{
    /// <summary>批打队列里的一张图。图框型和通用型共用这一个结构。</summary>
    public sealed class SheetItem
    {
        public int Seq { get; set; }                 // 扫描顺序
        public int Index { get; set; }               // 排序后的目录序号
        public string BlockName { get; set; }
        public FrameEntry Config { get; set; }
        public ObjectId BlockId { get; set; }
        public ObjectId LayoutId { get; set; }
        public string LayoutName { get; set; }

        public Extents2d WindowWcs { get; set; }
        public Extents2d WindowDcs { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public bool Rotated { get; set; }

        public string DrawNo { get; set; }
        public string Title { get; set; }
        public string Rev { get; set; }
        public string MetaSource { get; set; }

        public string FileStem { get; set; }
        public string PdfPath { get; set; }
        public bool Plotted { get; set; }
        public bool IsGeneralLayout { get; set; }
        public string Error { get; set; }
        public Point3d Center { get; set; }
        /// <summary>本张纸张是按实测尺寸自动识别的（库内纸张对不上时自动纠正），不是照抄库内配置。</summary>
        public bool PaperAutoDetected { get; set; }
        /// <summary>本张图号/图名的位置是自动定位到的（属性位置或标题栏文字位置）。</summary>
        public bool PosAutoDetected { get; set; }
    }

    /// <summary>手动框选的一个打印范围及其识别到的图号/图名，供批量打印使用。</summary>
    public sealed class ManualPickItem
    {
        public Extents2d WindowWcs { get; set; }
        public string DrawNo { get; set; }
        public string Title { get; set; }
        public string Rev { get; set; }
        public string FileStem { get; set; }
        public string MetaSource { get; set; }
        public ManualPickItem()
        {
            DrawNo = ""; Title = ""; Rev = ""; FileStem = ""; MetaSource = "";
        }
    }

    public sealed class ScanResult
    {
        public List<SheetItem> Sheets { get; set; }
        public List<string> UnmatchedBlockNames { get; set; }
        public List<string> Warnings { get; set; }
        public ScanResult()
        {
            Sheets = new List<SheetItem>();
            UnmatchedBlockNames = new List<string>();
            Warnings = new List<string>();
        }
    }

    /// <summary>
    /// 扫描模型空间（可选布局）里的图框，匹配图框库，产出批打队列。
    /// 覆盖：普通块 / 动态块 / 同块多实例 / 旋转图框 / 嵌套图框去重 /
    ///       图框不是块而是普通闭合矩形的老图 / 未录入块名提示。
    /// 每张图单独 Clone 一份图框配置：纸张按实测尺寸自动识别（库内纸张对不上时纠正，
    /// 标准幅面进预设、非标尺寸进自定义），图名/图号位置自动学习锚点；
    /// 库内原条目不动，手动点取校准（区域/锚点/Tag）优先级最高。
    /// </summary>
    public static class FrameScanner
    {
        /// <summary>
        /// 同批扫描内学到的锚点：某块名图框的图号/图名归一化位置（相对图框外框 0~1）。
        /// 第一张同名图框读到位置后记下来，后续同名图框直接按位置读，不用再打分猜。
        /// 只在当次扫描内有效，不写回图框库；要固化进库仍用「点取图号/图名范围」人工点选确认。
        /// </summary>
        private sealed class LearnedAnchor
        {
            public bool HasDn; public double DnX; public double DnY;
            public bool HasTt; public double TtX; public double TtY;
        }

        public static ScanResult Scan(Database db, FrameLibrary lib, GlobalConfig cfg)
        {
            ScanResult res = new ScanResult();
            if (db == null || lib == null) return res;

            double mmPerUnit = AcadUtil.MmPerUnit(db);
            // 留白改为「纸张不放大、内容按比例缩小」实现，打印窗口就用图框原尺寸，不再外扩。
            // （外扩会把 A1 撑过 841mm 导致跳到 A0，产生巨大白边；见 README / 工作日志）
            double marginUnits = 0;
            HashSet<string> unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int seq = 0;
            // 同批扫描内学到的图名/图号锚点（按块名），只在当次扫描内复用
            Dictionary<string, LearnedAnchor> learnedAnchors =
                new Dictionary<string, LearnedAnchor>(StringComparer.OrdinalIgnoreCase);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // ---- 收集要扫描的空间 ----
                List<Tuple<ObjectId, string>> spaces = new List<Tuple<ObjectId, string>>();
                BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (bt != null && bt.Has(BlockTableRecord.ModelSpace))
                    spaces.Add(new Tuple<ObjectId, string>(bt[BlockTableRecord.ModelSpace], BlockTableRecord.ModelSpace));

                if (cfg.ScanPaperSpace)
                {
                    try
                    {
                        DBDictionary lays = tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
                        if (lays != null)
                        {
                            foreach (DBDictionaryEntry de in lays)
                            {
                                Layout lo = tr.GetObject(de.Value, OpenMode.ForRead) as Layout;
                                if (lo == null || lo.ModelType) continue;
                                if (!lo.BlockTableRecordId.IsNull)
                                    spaces.Add(new Tuple<ObjectId, string>(lo.BlockTableRecordId, de.Key));
                            }
                        }
                    }
                    catch (Exception ex) { res.Warnings.Add("布局枚举失败：" + ex.Message); }
                }

                foreach (var space in spaces)
                {
                    BlockTableRecord btr = tr.GetObject(space.Item1, OpenMode.ForRead) as BlockTableRecord;
                    if (btr == null) continue;

                    foreach (ObjectId id in btr)
                    {
                        BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null) continue;

                        string name = AcadUtil.EffectiveBlockName(br, tr);
                        FrameEntry f = FindConfig(lib, name);
                        if (f == null) { if (!string.IsNullOrEmpty(name)) unmatched.Add(name); continue; }

                        // 外部参照里的图框默认不收
                        if (!cfg.ScanXrefFrames && IsFromXref(br, tr)) continue;

                        Extents3d? ex = AcadUtil.FrameExtents(br, tr);
                        if (!ex.HasValue)
                        {
                            res.Warnings.Add("图框「" + name + "」无法取得包围盒，已跳过（可能是天正代理对象）");
                            continue;
                        }
                        Extents3d e = ex.Value;
                        double x0 = e.MinPoint.X - marginUnits, y0 = e.MinPoint.Y - marginUnits;
                        double x1 = e.MaxPoint.X + marginUnits, y1 = e.MaxPoint.Y + marginUnits;
                        if (x1 - x0 <= 0 || y1 - y0 <= 0) continue;

                        Extents2d wcs = new Extents2d(x0, y0, x1, y1);
                        double wMm = (x1 - x0) * mmPerUnit;
                        double hMm = (y1 - y0) * mmPerUnit;

                        // 每张图单独一份配置：纸张按实测尺寸自动识别，锚点按本批学习结果补齐。
                        // 库内原条目不动；同一块名不同可见性尺寸不同时，各张按各自实测尺寸出纸。
                        FrameEntry fc = f.Clone();
                        bool paperAuto = false;
                        if (!PaperMatches(fc.Paper, wMm, hMm))
                        {
                            fc.Paper = PaperPresets.Guess(wMm, hMm);
                            paperAuto = true;
                        }
                        bool dnAnchorManual = f.HasDnAnchor, ttAnchorManual = f.HasTtAnchor;
                        LearnedAnchor learned;
                        if (learnedAnchors.TryGetValue(name, out learned))
                        {
                            if (!fc.HasDnAnchor && learned.HasDn) { fc.DnAnchorX = learned.DnX; fc.DnAnchorY = learned.DnY; }
                            if (!fc.HasTtAnchor && learned.HasTt) { fc.TtAnchorX = learned.TtX; fc.TtAnchorY = learned.TtY; }
                        }

                        bool rotated = NeedsRotation(wMm, hMm, fc.Paper, cfg.RotateToBestFit);

                        SheetMeta meta = AttrReader.Read(br, tr, fc, wcs, cfg.TitleBarTextFallback);
                        // 若图框库里点取过图号/图名范围，或本批已学到锚点，按每张图框的实际包围盒
                        // 换算出 WCS 区域读当前图的文字。区域/锚点读取优先于属性读取（位置是明确指定的），
                        // 仍读不到才保留属性值。人工锚点来源标「点选确认」，本批学到的锚点标「自动定位」。
                        if (fc.HasDrawNoRegion || fc.HasTitleRegion || fc.HasDnAnchor || fc.HasTtAnchor)
                        {
                            double fw = x1 - x0, fh = y1 - y0;
                            // 图号：优先按锚点找最近文字（最准），读不到再回退到区域内打分猜。
                            if (fc.HasDrawNoRegion || fc.HasDnAnchor)
                            {
                                string v = "";
                                bool fromAnchor = false;
                                if (fc.HasDnAnchor)
                                {
                                    // 有人工区域时按区域尺寸定容差；只有学到的锚点时取图框较大边的 3%
                                    double tol = fc.HasDrawNoRegion
                                        ? Math.Max(Math.Abs(fc.DnX1 - fc.DnX0) * fw,
                                                   Math.Abs(fc.DnY1 - fc.DnY0) * fh) * 1.5 + 50
                                        : Math.Max(fw, fh) * 0.03;
                                    v = AttrReader.ReadAtNearest(x0 + fc.DnAnchorX * fw,
                                                                 y0 + fc.DnAnchorY * fh, tol);
                                    fromAnchor = !string.IsNullOrEmpty(v);
                                }
                                if (string.IsNullOrEmpty(v) && fc.HasDrawNoRegion)
                                {
                                    Extents2d r = new Extents2d(x0 + fc.DnX0 * fw, y0 + fc.DnY0 * fh,
                                                                x0 + fc.DnX1 * fw, y0 + fc.DnY1 * fh);
                                    v = AttrReader.ReadRegionValue(r, "drawno");
                                }
                                if (!string.IsNullOrEmpty(v))
                                {
                                    meta.DrawNo = v;
                                    meta.Source = fromAnchor
                                        ? (dnAnchorManual ? "点选确认" : "自动定位")
                                        : (fc.HasDrawNoRegion ? "点选确认" : meta.Source);
                                    if (fromAnchor)
                                    {
                                        meta.DrawNoPos = new Point3d(x0 + fc.DnAnchorX * fw,
                                                                     y0 + fc.DnAnchorY * fh, 0);
                                        meta.DrawNoFrom = "锚点";
                                    }
                                    else if (fc.HasDrawNoRegion)
                                        meta.DrawNoPos = null; // 区域打分猜的，位置不确定，不拿来学习锚点
                                }
                            }
                            // 图名：同样优先锚点
                            if (fc.HasTitleRegion || fc.HasTtAnchor)
                            {
                                string v = "";
                                bool fromAnchor = false;
                                if (fc.HasTtAnchor)
                                {
                                    double tol = fc.HasTitleRegion
                                        ? Math.Max(Math.Abs(fc.TtX1 - fc.TtX0) * fw,
                                                   Math.Abs(fc.TtY1 - fc.TtY0) * fh) * 1.5 + 50
                                        : Math.Max(fw, fh) * 0.03;
                                    v = AttrReader.ReadAtNearest(x0 + fc.TtAnchorX * fw,
                                                                 y0 + fc.TtAnchorY * fh, tol);
                                    fromAnchor = !string.IsNullOrEmpty(v);
                                }
                                if (string.IsNullOrEmpty(v) && fc.HasTitleRegion)
                                {
                                    Extents2d r = new Extents2d(x0 + fc.TtX0 * fw, y0 + fc.TtY0 * fh,
                                                                x0 + fc.TtX1 * fw, y0 + fc.TtY1 * fh);
                                    v = AttrReader.ReadRegionValue(r, "title");
                                }
                                if (!string.IsNullOrEmpty(v))
                                {
                                    meta.Title = v;
                                    meta.Source = fromAnchor
                                        ? (ttAnchorManual ? "点选确认" : "自动定位")
                                        : (fc.HasTitleRegion ? "点选确认" : meta.Source);
                                    if (fromAnchor)
                                    {
                                        meta.TitlePos = new Point3d(x0 + fc.TtAnchorX * fw,
                                                                    y0 + fc.TtAnchorY * fh, 0);
                                        meta.TitleFrom = "锚点";
                                    }
                                    else if (fc.HasTitleRegion)
                                        meta.TitlePos = null;
                                }
                            }
                        }

                        // 学习锚点：本次读到的图号/图名位置（归一化到图框）记下来，供同批后续同名图框用。
                        // 同名图框位置理应一致，只记第一次读到的（后续不再覆盖，避免一张坏图带偏整批）。
                        if (meta.DrawNoPos.HasValue || meta.TitlePos.HasValue)
                        {
                            double fw = x1 - x0, fh = y1 - y0;
                            LearnedAnchor la;
                            if (!learnedAnchors.TryGetValue(name, out la)) la = new LearnedAnchor();
                            if (meta.DrawNoPos.HasValue && !la.HasDn)
                            {
                                la.HasDn = true;
                                la.DnX = (meta.DrawNoPos.Value.X - x0) / fw;
                                la.DnY = (meta.DrawNoPos.Value.Y - y0) / fh;
                            }
                            if (meta.TitlePos.HasValue && !la.HasTt)
                            {
                                la.HasTt = true;
                                la.TtX = (meta.TitlePos.Value.X - x0) / fw;
                                la.TtY = (meta.TitlePos.Value.Y - y0) / fh;
                            }
                            learnedAnchors[name] = la;
                        }
                        SheetItem item = new SheetItem
                        {
                            Seq = ++seq,
                            BlockName = name,
                            Config = fc,   // 每张图独立的配置：纸张已按实测尺寸自动识别，锚点已按本批学习结果补齐
                            BlockId = id,
                            LayoutId = GetLayoutId(btr, tr),
                            LayoutName = space.Item2,
                            WindowWcs = wcs,
                            WindowDcs = AcadUtil.WcsRectToDcs(wcs),
                            WidthMm = wMm,
                            HeightMm = hMm,
                            Rotated = rotated,
                            DrawNo = meta.DrawNo ?? "",
                            Title = meta.Title ?? "",
                            Rev = meta.Rev ?? "",
                            MetaSource = (meta.Source ?? "")
                                + (!string.IsNullOrEmpty(meta.DrawNo) && !string.IsNullOrEmpty(meta.DrawNoFrom) ? "；图号←" + meta.DrawNoFrom : "")
                                + (paperAuto ? "；纸张自动" : "")
                                + (paperAuto ? "；实测" + ((int)Math.Round(wMm)).ToString() + "×" + ((int)Math.Round(hMm)).ToString() + "mm" : "")
                                + ((meta.DrawNoPos.HasValue || meta.TitlePos.HasValue) ? "；位置自动" : ""),
                            PaperAutoDetected = paperAuto,
                            PosAutoDetected = meta.DrawNoPos.HasValue || meta.TitlePos.HasValue,
                            Center = new Point3d((x0 + x1) / 2, (y0 + y1) / 2, 0)
                        };
                        res.Sheets.Add(item);
                    }
                }
                tr.Commit();
            }

            // ---- 图框不是块时：闭合矩形兜底 ----
            if (cfg.RectFrameFallback)
            {
                int before = res.Sheets.Count;
                List<SheetItem> rects = ScanRectangleFrames(db, lib, cfg, mmPerUnit, marginUnits, ref seq);
                res.Sheets.AddRange(rects);
                if (rects.Count > 0 && before == 0)
                    res.Warnings.Add("未找到任何已录入的图框块，已用「矩形图框兜底」识别到 " + rects.Count + " 张；建议把图框做成块以启用图号图名属性。");
            }

            // ---- 嵌套 / 重叠图框去重 ----
            res.Sheets = Deduplicate(res.Sheets, cfg.DuplicateIouThreshold);

            // ---- 排序 ----
            res.Sheets = Sort(res.Sheets, cfg);

            // ---- 编号 + 文件名（序号 N 依赖排序后的位置）----
            for (int i = 0; i < res.Sheets.Count; i++)
            {
                SheetItem s = res.Sheets[i];
                s.Index = i + 1;
                s.FileStem = Naming.BuildFileName(s, cfg, i + 1);
            }

            res.UnmatchedBlockNames = unmatched.OrderBy(x => x).ToList();
            return res;
        }

        /// <summary>
        /// 图框型「框选打印范围」：先照常扫描图框，再只保留图框中心落在用户框选区域内者。
        /// 区域为空 / null = 不过滤（全图）。用于复刻参考插件「执行命令后选择要打印的图框」。
        /// </summary>
        public static ScanResult ScanInRegions(Database db, FrameLibrary lib, GlobalConfig cfg,
                                               List<Extents2d> regions)
        {
            ScanResult res = Scan(db, lib, cfg);
            if (res == null || regions == null || regions.Count == 0) return res;
            List<SheetItem> kept = new List<SheetItem>();
            foreach (SheetItem s in res.Sheets)
            {
                Point3d c = s.Center;
                bool hit = false;
                foreach (Extents2d r in regions)
                {
                    if (c.X >= r.MinPoint.X && c.X <= r.MaxPoint.X &&
                        c.Y >= r.MinPoint.Y && c.Y <= r.MaxPoint.Y) { hit = true; break; }
                }
                if (hit) kept.Add(s);
            }
            res.Sheets = kept;
            // 过滤后重排序号与文件名，避免序号跳号
            for (int i = 0; i < res.Sheets.Count; i++)
            {
                res.Sheets[i].Index = i + 1;
                res.Sheets[i].Seq = i + 1;
                res.Sheets[i].FileStem = Naming.BuildFileName(res.Sheets[i], cfg, i + 1);
            }
            return res;
        }

        // ---------------------------------------------------------------- 匹配
        public static FrameEntry FindConfig(FrameLibrary lib, string blockName)
        {
            if (lib == null || string.IsNullOrEmpty(blockName)) return null;
            string n = blockName.Trim();
            foreach (FrameEntry f in lib.Active)
            {
                string p = (f.BlockName ?? "").Trim();
                if (string.Equals(p, n, StringComparison.OrdinalIgnoreCase)) return f;
                if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0)
                {
                    string re = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    if (Regex.IsMatch(n, re, RegexOptions.IgnoreCase)) return f;
                }
            }
            return null;
        }

        private static bool IsFromXref(BlockReference br, Transaction tr)
        {
            try
            {
                ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                if (defId.IsNull) return false;
                BlockTableRecord def = tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
                return def != null && def.IsFromExternalReference;
            }
            catch { return false; }
        }

        private static ObjectId GetLayoutId(BlockTableRecord btr, Transaction tr)
        {
            try
            {
                if (btr.LayoutId.IsNull) return ObjectId.Null;
                return btr.LayoutId;
            }
            catch { return ObjectId.Null; }
        }

        /// <summary>库内纸张与实测尺寸是否一致（两边相对误差之和 &lt; 10% 算一致，含 90° 旋转；与 Guess 吸附同阈）。</summary>
        private static bool PaperMatches(PaperSpec p, double wMm, double hMm)
        {
            if (p == null || p.WidthMm <= 0 || p.HeightMm <= 0 || wMm <= 0 || hMm <= 0) return false;
            double e1 = Math.Abs(p.WidthMm - wMm) / p.WidthMm + Math.Abs(p.HeightMm - hMm) / p.HeightMm;
            double e2 = Math.Abs(p.HeightMm - wMm) / p.HeightMm + Math.Abs(p.WidthMm - hMm) / p.WidthMm;
            return Math.Min(e1, e2) < 0.10;
        }

        private static bool NeedsRotation(double wMm, double hMm, PaperSpec paper, bool enabled)
        {
            if (!enabled || paper == null || paper.HeightMm <= 0 || hMm <= 0) return false;
            bool framePortrait = wMm / hMm < 0.85;
            bool paperLandscape = paper.WidthMm / paper.HeightMm > 1.15;
            return framePortrait && paperLandscape;
        }

        // ---------------------------------------------------------------- 去重
        private static List<SheetItem> Deduplicate(List<SheetItem> items, double threshold)
        {
            List<SheetItem> result = new List<SheetItem>();
            foreach (SheetItem s in items.OrderBy(x => x.Config == null ? 999 : x.Config.Priority)
                                        .ThenByDescending(x => x.WidthMm * x.HeightMm))
            {
                bool dup = false;
                foreach (SheetItem k in result)
                {
                    double ratio = OverlapRatio(s.WindowWcs, k.WindowWcs);
                    if (ratio >= threshold) { dup = true; break; }
                }
                if (!dup) result.Add(s);
            }
            return result;
        }

        /// <summary>交叠面积 / 较小者面积 —— 用 min 而不是 union，才能抓到「大图框套小图框」。</summary>
        private static double OverlapRatio(Extents2d a, Extents2d b)
        {
            double iw = Math.Min(a.MaxPoint.X, b.MaxPoint.X) - Math.Max(a.MinPoint.X, b.MinPoint.X);
            double ih = Math.Min(a.MaxPoint.Y, b.MaxPoint.Y) - Math.Max(a.MinPoint.Y, b.MinPoint.Y);
            if (iw <= 0 || ih <= 0) return 0;
            double aa = (a.MaxPoint.X - a.MinPoint.X) * (a.MaxPoint.Y - a.MinPoint.Y);
            double ba = (b.MaxPoint.X - b.MinPoint.X) * (b.MaxPoint.Y - b.MinPoint.Y);
            double small = Math.Min(aa, ba);
            return small <= 0 ? 0 : (iw * ih) / small;
        }

        // ---------------------------------------------------------------- 排序
        public static List<SheetItem> Sort(List<SheetItem> items, GlobalConfig cfg)
        {
            List<SheetItem> sorted;
            switch (cfg.SortMode)
            {
                case SortMode.DrawNo:
                    // 数字感知的自然排序（A-2 排在 A-10 前）
                    sorted = NaturalSort(items, x => x.DrawNo ?? "");
                    break;
                case SortMode.LayoutOrder:
                    sorted = items.OrderBy(x => x.LayoutName ?? "").ThenBy(x => x.Seq).ToList();
                    break;
                case SortMode.BlockPosition:
                    sorted = items.OrderByDescending(x => x.Center.Y).ThenBy(x => x.Center.X).ToList();
                    break;
                default:
                    sorted = items.OrderBy(x => x.Config == null ? 999 : x.Config.Priority)
                                  .ThenBy(x => x.DrawNo ?? "").ThenBy(x => x.Seq).ToList();
                    break;
            }
            return ApplyDescending(sorted, cfg.SortDescending);
        }

        private static List<T> ApplyDescending<T>(List<T> list, bool desc)
        {
            if (!desc) return list;
            list.Reverse();
            return list;
        }

        /// <summary>按 key 做数字感知自然排序（把 "A-10" 拆成 A / 10 比较）。</summary>
        public static List<T> NaturalSort<T>(IEnumerable<T> src, Func<T, string> key)
        {
            return src.OrderBy(x => key(x) ?? "", new NaturalComparer()).ToList();
        }

        private sealed class NaturalComparer : IComparer<string>
        {
            private static readonly Regex Split = new Regex(@"(\d+)", RegexOptions.Compiled);
            public int Compare(string a, string b)
            {
                a = a ?? ""; b = b ?? "";
                string[] pa = Split.Split(a), pb = Split.Split(b);
                int n = Math.Min(pa.Length, pb.Length);
                for (int i = 0; i < n; i++)
                {
                    bool na = i % 2 == 1, nb = i % 2 == 1;
                    if (na && nb)
                    {
                        long va, vb;
                        if (long.TryParse(pa[i], out va) && long.TryParse(pb[i], out vb) && va != vb)
                            return va.CompareTo(vb);
                    }
                    int c = string.Compare(pa[i], pb[i], StringComparison.CurrentCultureIgnoreCase);
                    if (c != 0) return c;
                }
                return pa.Length.CompareTo(pb.Length);
            }
        }

        // ---------------------------------------------------------------- 矩形图框兜底
        /// <summary>
        /// 老图常见「图框就是一组线/一条闭合多段线」。这里只认尺寸严格等于
        /// 某张库内纸张 × 常见出图比例的闭合矩形，避免把门窗框、轴网框误收进来。
        /// </summary>
        private static List<SheetItem> ScanRectangleFrames(Database db, FrameLibrary lib, GlobalConfig cfg,
                                                            double mmPerUnit, double marginUnits, ref int seq)
        {
            List<SheetItem> result = new List<SheetItem>();
            List<FrameEntry> configs = lib.Active.ToList();
            if (configs.Count == 0) return result;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (bt == null || !bt.Has(BlockTableRecord.ModelSpace)) { tr.Commit(); return result; }
                BlockTableRecord ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                if (ms == null) { tr.Commit(); return result; }

                foreach (ObjectId id in ms)
                {
                    Polyline pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null || !pl.Closed || pl.NumberOfVertices < 4 || pl.NumberOfVertices > 8) continue;
                    Extents3d? ex = AcadUtil.TryExtents(pl);
                    if (!ex.HasValue) continue;
                    Extents3d e = ex.Value;
                    double w = (e.MaxPoint.X - e.MinPoint.X) * mmPerUnit;
                    double h = (e.MaxPoint.Y - e.MinPoint.Y) * mmPerUnit;
                    if (w < 180 || h < 180) continue;             // 排除小构件

                    foreach (FrameEntry f in configs)
                    {
                        if (f.Paper == null) continue;
                        double rx = w / f.Paper.WidthMm, ry = h / f.Paper.HeightMm;
                        if (rx <= 0 || ry <= 0) continue;
                        if (Math.Abs(rx - ry) / Math.Max(rx, ry) > 0.03) continue;
                        double ratio = (rx + ry) / 2;
                        double snapped = PaperMatcher.ComputeScaleDenominator(w, h, f, false);
                        if (Math.Abs(snapped - ratio) / ratio > 0.02) continue;   // 只认标准出图比例

                        Extents2d wcs = new Extents2d(e.MinPoint.X - marginUnits, e.MinPoint.Y - marginUnits,
                                                      e.MaxPoint.X + marginUnits, e.MaxPoint.Y + marginUnits);
                        SheetMeta meta = cfg.TitleBarTextFallback
                            ? AttrReader.ReadTitleBarTexts(wcs, f)
                            : new SheetMeta { DrawNo = "", Title = "", Rev = "" };
                        result.Add(new SheetItem
                        {
                            Seq = ++seq,
                            BlockName = "(矩形图框)",
                            Config = f,
                            LayoutId = ms.LayoutId,
                            LayoutName = BlockTableRecord.ModelSpace,
                            WindowWcs = wcs,
                            WindowDcs = AcadUtil.WcsRectToDcs(wcs),
                            WidthMm = w, HeightMm = h,
                            Rotated = NeedsRotation(w, h, f.Paper, cfg.RotateToBestFit),
                            DrawNo = meta.DrawNo ?? "", Title = meta.Title ?? "", Rev = meta.Rev ?? "",
                            MetaSource = (string.IsNullOrEmpty(meta.DrawNo + meta.Title) ? "" : "标题栏文字")
                                + (!string.IsNullOrEmpty(meta.DrawNo) && !string.IsNullOrEmpty(meta.DrawNoFrom) ? "；图号←" + meta.DrawNoFrom : "")
                                + ((meta.DrawNoPos.HasValue || meta.TitlePos.HasValue) ? "；位置自动" : ""),
                            PosAutoDetected = meta.DrawNoPos.HasValue || meta.TitlePos.HasValue,
                            Center = new Point3d((e.MinPoint.X + e.MaxPoint.X) / 2, (e.MinPoint.Y + e.MaxPoint.Y) / 2, 0)
                        });
                        break;
                    }
                }
                tr.Commit();
            }
            return result;
        }

        // ---------------------------------------------------------------- 通用型：布局清单
        public static List<SheetItem> ScanLayouts(Database db, GlobalConfig cfg)
        {
            List<SheetItem> result = new List<SheetItem>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    DBDictionary lays = tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
                    if (lays != null)
                    {
                        int i = 0;
                        foreach (DBDictionaryEntry de in lays)
                        {
                            Layout lo = tr.GetObject(de.Value, OpenMode.ForRead) as Layout;
                            if (lo == null || lo.ModelType) continue;
                            result.Add(new SheetItem
                            {
                                Seq = ++i,
                                IsGeneralLayout = true,
                                LayoutId = de.Value,
                                LayoutName = de.Key,
                                BlockName = de.Key,
                                Config = new FrameEntry { Priority = i, NameTemplate = "N_C" },
                                DrawNo = "", Title = de.Key, Rev = "",
                                Center = Point3d.Origin
                            });
                        }
                    }
                }
                catch (Exception ex) { Logger.Warn("布局扫描失败：" + ex.Message); }
                tr.Commit();
            }
            result = NaturalSort(result, x => x.LayoutName ?? "");
            for (int i = 0; i < result.Count; i++)
            {
                result[i].Index = i + 1;
                result[i].FileStem = AcadUtil.SafeFileName(result[i].LayoutName ?? ("布局" + (i + 1)));
            }
            return result;
        }
    }
}
