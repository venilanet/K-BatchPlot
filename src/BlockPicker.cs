using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace MPlot
{
    /// <summary>
    /// 点选图框块批量导出：用户在图上连续点选多个块参照，回车结束、Esc 取消；
    /// 只处理用户明确选中的顶层 BlockReference，不扫描整图、不猜矩形/多段线/嵌套块。
    ///
    /// 解析复用 Scan 同一套机制：AcadUtil.FrameExtents 取框、AttrReader.Read 读图号/图名
    /// （动态块显示值优先、只读顶层属性、过滤隐藏属性、同 TAG 打分去重）、
    /// 库内 FrameEntry 的 TAG 映射 + 标题栏文字兜底；纸张按实测尺寸自动识别。
    ///
    /// 动态块边界风险（未在真机验证前不承诺）：FrameExtents 读的是动态块定义里的全部实体
    /// （含当前可见性状态下隐藏的实体），算出的框可能比图上实际显示的大。
    /// 所以每个动态块都拿参照自身的显示范围（GeometricExtents）交叉核对：
    /// 定义法面积超过显示法 2 倍就只标「边界异常」不丢弃，窗口仍用定义法的大框
    /// （宁可多留白边，也绝不静默裁切），等真机实测后再定要不要收紧。
    /// 点取范围/锚点细化是 Scan 路径的机制，本路径暂不套用——点选行仍可在清单里手动改。
    /// </summary>
    public static class BlockPicker
    {
        /// <summary>一次点选的结果：新加入清单的图纸 + 给用户的一次性说明。</summary>
        public sealed class PickResult
        {
            public List<SheetItem> Added = new List<SheetItem>();
            public List<string> Notes = new List<string>();
            public bool Canceled;   // Esc：整批取消，清单原样不动
        }

        /// <summary>
        /// 点选并解析。先一次 GetSelection 拿全部 ObjectId（挑选中不碰数据库），
        /// 再开一个只读事务逐个解析；解析完事务即关，不跨用户交互持有。
        /// existingIds 是清单里已有的 BlockId（去重用，重复点选不重复加入、不覆盖用户已改数据）；
        /// startSeq 是新行 Seq 的起始值（一般取清单现有最大 Seq + 1）。
        /// 返回 null 表示没有打开的图纸。
        /// </summary>
        public static PickResult PickAndBuildSheets(FrameLibrary lib, GlobalConfig cfg,
            HashSet<ObjectId> existingIds, int startSeq)
        {
            if (AcadUtil.Ed == null || AcadUtil.Db == null) return null;
            if (lib == null) lib = FrameLibrary.Load();
            if (cfg == null) cfg = GlobalConfig.Current;
            if (existingIds == null) existingIds = new HashSet<ObjectId>();

            PickResult res = new PickResult();
            List<ObjectId> ids = AcadUtil.PickBlockReferences();
            if (ids == null) { res.Canceled = true; return res; }   // Esc：整批取消
            if (ids.Count == 0) { res.Notes.Add("没有选中任何对象。"); return res; }

            Database db = AcadUtil.Db;
            double mmPerUnit = AcadUtil.MmPerUnit(db);
            int skippedDup = 0, skippedNonBlock = 0, noMeta = 0, abnormal = 0;
            int seq = startSeq;
            HashSet<ObjectId> seenInPick = new HashSet<ObjectId>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    if (!seenInPick.Add(id)) { skippedDup++; continue; }   // 本次点选内重复
                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) { skippedNonBlock++; continue; }        // 不是块参照
                    if (existingIds.Contains(id)) { skippedDup++; continue; }   // 已在清单里

                    string name = AcadUtil.EffectiveBlockName(br, tr);
                    FrameEntry f = FrameScanner.FindConfig(lib, name);
                    FrameEntry fc;
                    if (f != null)
                    {
                        fc = f.Clone();   // 每张图独立一份配置，库内原条目不动
                    }
                    else
                    {
                        // 未知块：临时条目，不猜 TAG、不自动入库；纸张置空，走实测自动识别。
                        fc = new FrameEntry { BlockName = name };
                        fc.Paper = null;
                    }

                    Extents3d? ex = AcadUtil.FrameExtents(br, tr);
                    bool boundaryAbnormal = false;
                    Extents2d wcs;
                    double wMm = 0, hMm = 0;
                    if (!ex.HasValue)
                    {
                        // 取不到框：行照样保留（不丢弃用户明确选中的图框），窗口留空，
                        // 导出前检查会报「图纸打印范围无效」，请用户删掉或处理该行。
                        wcs = new Extents2d(0, 0, 0, 0);
                        boundaryAbnormal = true;
                    }
                    else
                    {
                        Extents3d e = ex.Value;
                        double x0 = e.MinPoint.X, y0 = e.MinPoint.Y;
                        double x1 = e.MaxPoint.X, y1 = e.MaxPoint.Y;
                        if (x1 - x0 <= 0 || y1 - y0 <= 0)
                        {
                            wcs = new Extents2d(0, 0, 0, 0);
                            boundaryAbnormal = true;
                        }
                        else
                        {
                            wcs = new Extents2d(x0, y0, x1, y1);
                            wMm = (x1 - x0) * mmPerUnit;
                            hMm = (y1 - y0) * mmPerUnit;
                            boundaryAbnormal = IsBoundaryAbnormal(ex.Value, br);
                        }
                    }

                    SheetMeta meta = AttrReader.Read(br, tr, fc, wcs, cfg.TitleBarTextFallback);

                    bool paperAuto = false;
                    // 只有实测尺寸有效时才按尺寸定纸张；取不到框的行保持原纸张（临时条目为空），
                    // 不拿 0×0 去猜一张假纸张。
                    if (wMm > 0 && hMm > 0 && !FrameScanner.PaperMatches(fc.Paper, wMm, hMm))
                    {
                        fc.Paper = PaperPresets.Guess(wMm, hMm);
                        paperAuto = true;
                    }
                    bool rotated = FrameScanner.NeedsRotation(wMm, hMm, fc.Paper, cfg.RotateToBestFit);

                    ObjectId layoutId = ObjectId.Null;
                    string layoutName = "";
                    try
                    {
                        BlockTableRecord btr = tr.GetObject(br.OwnerId, OpenMode.ForRead) as BlockTableRecord;
                        if (btr != null)
                        {
                            layoutId = FrameScanner.GetLayoutId(btr, tr);
                            layoutName = btr.Name ?? "";
                        }
                    }
                    catch { }

                    SheetItem item = new SheetItem
                    {
                        Seq = seq,
                        Index = seq,
                        BlockName = name,
                        Config = fc,
                        BlockId = id,
                        LayoutId = layoutId,
                        LayoutName = layoutName,
                        WindowWcs = wcs,
                        WindowDcs = AcadUtil.WcsRectToDcs(wcs),
                        WidthMm = wMm,
                        HeightMm = hMm,
                        Rotated = rotated,
                        DrawNo = meta.DrawNo ?? "",
                        Title = meta.Title ?? "",
                        Rev = meta.Rev ?? "",
                        MetaSource = BuildMetaSource(meta, paperAuto, wMm, hMm, boundaryAbnormal),
                        PaperAutoDetected = paperAuto,
                        PosAutoDetected = meta.DrawNoPos.HasValue || meta.TitlePos.HasValue,
                        Center = new Point3d(
                            (wcs.MinPoint.X + wcs.MaxPoint.X) / 2,
                            (wcs.MinPoint.Y + wcs.MaxPoint.Y) / 2, 0)
                    };
                    item.FileStem = Naming.BuildFileName(item, cfg, seq);
                    res.Added.Add(item);
                    existingIds.Add(id);
                    seq++;

                    if (string.IsNullOrEmpty(item.DrawNo) && string.IsNullOrEmpty(item.Title)) noMeta++;
                    if (boundaryAbnormal) abnormal++;
                }
                tr.Commit();
            }

            if (res.Added.Count > 0)
                res.Notes.Add("已加入 " + res.Added.Count + " 张图框。");
            if (skippedDup > 0)
                res.Notes.Add("已跳过 " + skippedDup + " 个重复选择（已在清单里，不重复加入、不覆盖已改数据）。");
            if (skippedNonBlock > 0)
                res.Notes.Add("已跳过 " + skippedNonBlock + " 个非块对象（该对象不是块参照）。");
            if (noMeta > 0)
                res.Notes.Add("其中 " + noMeta + " 张没有读到图号/图名，已标「待补充」，可在清单里手动填写。");
            if (abnormal > 0)
                res.Notes.Add("其中 " + abnormal + " 张边界异常（取不到框，或动态块显示范围与块定义范围差异大），请核对后再导出。");
            if (res.Added.Count == 0 && skippedDup == 0 && skippedNonBlock == 0)
                res.Notes.Add("没有加入任何图框。");
            return res;
        }

        /// <summary>
        /// 边界交叉核对：定义法（FrameExtents，含隐藏实体）面积超过显示法
        /// （参照自身 GeometricExtents）2 倍，即认为定义法不可信，标边界异常。
        /// 显示法取不到时无法核对，不标（FrameExtents 本身已成功，Scan 同样会用它）。
        /// 反方向（显示比定义大）是常态：属性文字在框外，不算异常。
        /// </summary>
        private static bool IsBoundaryAbnormal(Extents3d frameEx, BlockReference br)
        {
            try
            {
                Extents3d? geo = AcadUtil.TryExtents(br);
                if (!geo.HasValue) return false;
                double areaF = (frameEx.MaxPoint.X - frameEx.MinPoint.X)
                             * (frameEx.MaxPoint.Y - frameEx.MinPoint.Y);
                double areaG = (geo.Value.MaxPoint.X - geo.Value.MinPoint.X)
                             * (geo.Value.MaxPoint.Y - geo.Value.MinPoint.Y);
                if (areaG <= 0) return false;
                return areaF > areaG * 2.0;
            }
            catch { return false; }
        }

        /// <summary>点选行的备注：来源「点选」+ 图号来源 + 纸张自动/实测 + 位置自动 + 待补充/边界异常。</summary>
        private static string BuildMetaSource(SheetMeta meta, bool paperAuto, double wMm, double hMm, bool boundaryAbnormal)
        {
            string src = "点选";
            if (!string.IsNullOrEmpty(meta.Source)) src += "·" + meta.Source;
            if (!string.IsNullOrEmpty(meta.DrawNo) && !string.IsNullOrEmpty(meta.DrawNoFrom))
                src += "；图号←" + meta.DrawNoFrom;
            if (paperAuto)
                src += "；纸张自动；实测" + ((int)Math.Round(wMm)).ToString()
                     + "×" + ((int)Math.Round(hMm)).ToString() + "mm";
            if (meta.DrawNoPos.HasValue || meta.TitlePos.HasValue)
                src += "；位置自动";
            if (string.IsNullOrEmpty(meta.DrawNo) && string.IsNullOrEmpty(meta.Title))
                src += "；待补充";
            if (boundaryAbnormal)
                src += "；边界异常";
            return src;
        }
    }
}
