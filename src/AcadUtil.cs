using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcCore = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MPlot
{
    /// <summary>
    /// AutoCAD 侧公共工具。所有「容易被天正自定义对象 / UCS / 动态块坑到」的地方都集中在这里兜底。
    /// </summary>
    public static class AcadUtil
    {
        private static bool _transAvailable = true;

        #region ---- 文档 ----
        public static Autodesk.AutoCAD.ApplicationServices.Document Doc
        {
            get { return AcCore.DocumentManager.MdiActiveDocument; }
        }
        public static Editor Ed { get { return Doc == null ? null : Doc.Editor; } }
        public static Database Db { get { return Doc == null ? null : Doc.Database; } }

        public static void Say(string msg)
        {
            Editor ed = Ed;
            if (ed != null) ed.WriteMessage("\n" + msg);
        }

        /// <summary>把经典菜单栏显示出来。AutoCAD 2015+ 默认 MENUBAR=0，不设这一下菜单根本看不见。</summary>
        public static void EnsureMenuBarVisible()
        {
            try { AcCore.SetSystemVariable("MENUBAR", 1); }
            catch (Exception ex) { Logger.Verbose("MENUBAR 设置跳过：" + ex.Message); }
        }

        /// <summary>关闭后台打印，否则文件还没落盘就去合并，会拿到 0 字节 PDF。</summary>
        public static void DisableBackgroundPlot()
        {
            if (!GlobalConfig.Current.DisableBackgroundPlot) return;
            try { AcCore.SetSystemVariable("BACKGROUNDPLOT", 0); }
            catch (Exception ex) { Logger.Verbose("BACKGROUNDPLOT 设置跳过：" + ex.Message); }
        }

        /// <summary>
        /// 任务开始前记下 BACKGROUNDPLOT 的当前值，任务结束时由 RestoreBackgroundPlot 恢复。
        /// 以前只关不恢复，跑完一批任务会在用户会话里永久留下改动；现在成对使用。
        /// 返回 -1 表示读不到（不恢复）。
        /// </summary>
        public static int SaveBackgroundPlot()
        {
            try
            {
                object v = AcCore.GetSystemVariable("BACKGROUNDPLOT");
                if (v == null) return -1;
                return Convert.ToInt32(v);
            }
            catch (Exception ex) { Logger.Verbose("BACKGROUNDPLOT 读取跳过：" + ex.Message); return -1; }
        }

        public static void RestoreBackgroundPlot(int oldValue)
        {
            if (oldValue < 0) return;
            try { AcCore.SetSystemVariable("BACKGROUNDPLOT", oldValue); }
            catch (Exception ex) { Logger.Verbose("BACKGROUNDPLOT 恢复跳过：" + ex.Message); }
        }
        #endregion

        #region ---- UCS / WCS / DCS ----
        // Kean Walmsley 的 window plot 示例：SetPlotWindowArea 要求 DCS 坐标。
        // acedTrans 没有托管等价物，只能 P/Invoke；失败时退回 WCS（标准俯视下二者一致）。
        [DllImport("acad.exe", CallingConvention = CallingConvention.Cdecl, EntryPoint = "acedTrans")]
        private static extern int AcedTrans(double[] point, IntPtr fromRb, IntPtr toRb, int disp, double[] result);

        /// <summary>WCS(0) → DCS(2)。disp=0 表示点不是位移向量。</summary>
        public static Point2d WcsToDcs(Point2d wcs)
        {
            if (!_transAvailable) return wcs;
            try
            {
                ResultBuffer from = new ResultBuffer(new TypedValue(5003, 0));
                ResultBuffer to = new ResultBuffer(new TypedValue(5003, 2));
                double[] p = { wcs.X, wcs.Y, 0.0 };
                double[] r = { 0.0, 0.0, 0.0 };
                int ok = AcedTrans(p, from.UnmanagedObject, to.UnmanagedObject, 0, r);
                from.Dispose(); to.Dispose();
                if (ok != 0) return new Point2d(r[0], r[1]);
            }
            catch (Exception ex)
            {
                _transAvailable = false;
                Logger.Warn("acedTrans 不可用，打印窗口按 WCS 处理（俯视 WCS 下结果一致）：" + ex.Message);
            }
            return wcs;
        }

        /// <summary>UCS(1) → DCS(2)：用于用户手动框选的两点。</summary>
        public static Point2d UcsToDcs(Point3d ucs)
        {
            if (!_transAvailable) return new Point2d(ucs.X, ucs.Y);
            try
            {
                ResultBuffer from = new ResultBuffer(new TypedValue(5003, 1));
                ResultBuffer to = new ResultBuffer(new TypedValue(5003, 2));
                double[] p = { ucs.X, ucs.Y, ucs.Z };
                double[] r = { 0.0, 0.0, 0.0 };
                int ok = AcedTrans(p, from.UnmanagedObject, to.UnmanagedObject, 0, r);
                from.Dispose(); to.Dispose();
                if (ok != 0) return new Point2d(r[0], r[1]);
            }
            catch (Exception ex)
            {
                _transAvailable = false;
                Logger.Warn("acedTrans 不可用，框选窗口按 UCS 处理：" + ex.Message);
            }
            return new Point2d(ucs.X, ucs.Y);
        }

        /// <summary>把 WCS 矩形变换到 DCS 后重算轴对齐包围盒（视图被旋转/twist 时必需）。</summary>
        public static Extents2d WcsRectToDcs(Extents2d r)
        {
            Point2d[] corners = {
                new Point2d(r.MinPoint.X, r.MinPoint.Y),
                new Point2d(r.MaxPoint.X, r.MinPoint.Y),
                new Point2d(r.MinPoint.X, r.MaxPoint.Y),
                new Point2d(r.MaxPoint.X, r.MaxPoint.Y)
            };
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (Point2d c in corners)
            {
                Point2d p = WcsToDcs(c);
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
            return new Extents2d(minX, minY, maxX, maxY);
        }
        #endregion

        #region ---- 单位 ----
        /// <summary>1 图形单位 = 多少毫米。配置里显式填了就优先用，否则读 INSUNITS。</summary>
        public static double MmPerUnit(Database db)
        {
            double forced = GlobalConfig.Current.MmPerDrawingUnit;
            if (forced > 0) return forced;
            if (db == null) return 1.0;
            string u;
            try { u = db.Insunits.ToString(); }
            catch { return 1.0; }
            switch (u)
            {
                case "Millimeters": return 1.0;
                case "Centimeters": return 10.0;
                case "Decimeters": return 100.0;
                case "Meters": return 1000.0;
                case "Kilometers": return 1000000.0;
                case "Inches": return 25.4;
                case "Feet": return 304.8;
                case "Yards": return 914.4;
                case "Mils": return 0.0254;
                case "Microinches": return 0.0000254;
                case "Microns": return 0.001;
                default: return 1.0;   // Unitless 等：按 mm 处理，建筑图绝大多数如此
            }
        }
        #endregion

        #region ---- 块 / 范围 ----
        /// <summary>
        /// 动态块的 BlockReference.Name 会是 *U123 之类的匿名名，
        /// 必须走 DynamicBlockTableRecord 才能拿到库里登记的真实块名。
        /// </summary>
        public static string EffectiveBlockName(BlockReference br, Transaction tr)
        {
            if (br == null || tr == null) return "";
            try
            {
                ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                if (defId.IsNull) return br.Name ?? "";
                BlockTableRecord def = tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
                if (def != null && !string.IsNullOrEmpty(def.Name)) return def.Name;
            }
            catch { }
            return br.Name ?? "";
        }

        /// <summary>天正自定义实体/损坏代理对象访问 GeometricExtents 会抛异常，统一包一层。</summary>
        public static Extents3d? TryExtents(Entity ent)
        {
            try
            {
                Extents3d e = ent.GeometricExtents;
                if (double.IsNaN(e.MinPoint.X) || double.IsInfinity(e.MinPoint.X)) return null;
                return e;
            }
            catch { return null; }
        }

        /// <summary>
        /// 图框外框范围。优先用「块定义几何 × 块变换」而不是 BlockReference.GeometricExtents：
        /// 后者会把属性文字（会签栏、外部附注）算进去，导致打印窗口偏大、图框被缩放。
        /// 块定义法失败（天正自定义对象、xref、代理）时退回参照自身的 extents。
        /// </summary>
        public static Extents3d? FrameExtents(BlockReference br, Transaction tr)
        {
            if (br == null || tr == null) return null;
            try
            {
                ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                if (!defId.IsNull)
                {
                    BlockTableRecord def = tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
                    if (def != null && !def.IsFromExternalReference)
                    {
                        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                        bool any = false;
                        foreach (ObjectId id in def)
                        {
                            Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (e == null || e is AttributeDefinition) continue;   // 属性定义不计入外框
                            Extents3d? ex = TryExtents(e);
                            if (ex == null) continue;
                            Extents3d v = ex.Value;
                            minX = Math.Min(minX, v.MinPoint.X); minY = Math.Min(minY, v.MinPoint.Y);
                            maxX = Math.Max(maxX, v.MaxPoint.X); maxY = Math.Max(maxY, v.MaxPoint.Y);
                            any = true;
                        }
                        if (any)
                        {
                            Matrix3d m = br.BlockTransform;
                            Point3d[] pts = {
                                new Point3d(minX, minY, 0), new Point3d(maxX, minY, 0),
                                new Point3d(minX, maxY, 0), new Point3d(maxX, maxY, 0)
                            };
                            for (int i = 0; i < pts.Length; i++) pts[i] = pts[i].TransformBy(m);
                            double ax = double.MaxValue, ay = double.MaxValue, bx = double.MinValue, by = double.MinValue;
                            foreach (Point3d p in pts)
                            {
                                ax = Math.Min(ax, p.X); ay = Math.Min(ay, p.Y);
                                bx = Math.Max(bx, p.X); by = Math.Max(by, p.Y);
                            }
                            // AutoCAD 2014 R19.1 实测：Extents3d 只有 (Point3d,Point3d) 与 () 两个构造，没有 6 参重载。
                            return new Extents3d(new Point3d(ax, ay, 0), new Point3d(bx, by, 0));
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Verbose("块定义范围计算失败，改用参照范围：" + ex.Message); }
            return TryExtents(br);
        }

        /// <summary>
        /// 一次 GetSelection：用户连续点选，回车结束、Esc 取消。
        /// insertOnly=true 只认 INSERT（PickFrameInsert 用）；false 不过滤，由调用方分类
        /// （点选图框用：这样才能把「点了不是块的东西」单独报出来，而不是让过滤器悄悄吞掉）。
        /// 返回 null = Esc 取消；空列表 = 回车时一个没选。挑选中不碰数据库。
        /// </summary>
        private static List<ObjectId> PickObjects(bool insertOnly, string prompt)
        {
            Editor ed = Ed;
            if (ed == null) return null;
            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = prompt;
            pso.MessageForRemoval = "\n移除误选对象: ";
            pso.AllowDuplicates = false;
            PromptSelectionResult psr;
            if (insertOnly)
            {
                TypedValue[] filter = { new TypedValue((int)DxfCode.Start, "INSERT") };
                psr = ed.GetSelection(pso, new SelectionFilter(filter));
            }
            else
            {
                psr = ed.GetSelection(pso);
            }
            if (psr.Status == PromptStatus.Cancel) return null;   // Esc：整批取消
            if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0)
                return new List<ObjectId>();   // 回车时一个没选
            List<ObjectId> ids = new List<ObjectId>();
            foreach (SelectedObject so in psr.Value)
                if (so != null && !so.ObjectId.IsNull) ids.Add(so.ObjectId);
            return ids;
        }

        /// <summary>
        /// 框选录入：只允许选 INSERT。窗选会同时选中图框块、嵌套子块和其他块，
        /// 直接取第一条会「同图框不同拾取点抓到不同块」——所以多选时按优先级挑：
        /// ① 块名已在图框库里登记的；② 否则几何包围盒面积最大的（图框外框一定比里面的子块大）。
        /// 单选 / ESC / 空选时行为不变（单选直接返回，空选返回 Null）。
        /// </summary>
        public static ObjectId PickFrameInsert()
        {
            List<ObjectId> ids = PickObjects(true, "\n框选模型里的图框（支持窗选 / 多选，只认图块）: ");
            if (ids == null || ids.Count == 0) return ObjectId.Null;
            if (ids.Count == 1) return ids[0];

            FrameLibrary lib = null;
            try { lib = FrameLibrary.Load(); } catch { }
            ObjectId best = ObjectId.Null;
            double bestArea = -1;
            try
            {
                Database db = Db;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null) continue;
                        string name = EffectiveBlockName(br, tr);
                        if (lib != null && FrameScanner.FindConfig(lib, name) != null) return id;  // 库里有的，直接要
                        Extents3d? ex = TryExtents(br);
                        double area = 0;
                        if (ex.HasValue)
                            area = (ex.Value.MaxPoint.X - ex.Value.MinPoint.X)
                                 * (ex.Value.MaxPoint.Y - ex.Value.MinPoint.Y);
                        if (area > bestArea) { bestArea = area; best = id; }
                    }
                    tr.Commit();
                }
            }
            catch (Exception ex) { Logger.Verbose("框选图框挑块失败，退回第一条：" + ex.Message); return ids[0]; }
            return best.IsNull ? ids[0] : best;
        }

        /// <summary>
        /// 点选图框：一次选择拿回全部 ObjectId（null = Esc 取消），不过滤 INSERT，
        /// 由 BlockPicker 再分类——非块对象单独计数回报，不悄悄吞掉。
        /// </summary>
        public static List<ObjectId> PickBlockReferences()
        {
            return PickObjects(false, "\n点选图框块（可点选多个，回车结束，Esc 取消）: ");
        }

        /// <summary>
        /// 视图缩放到指定 WCS 矩形（「预览选中」用）。
        /// Editor.ZoomWindow 在 2014 是公开 API，但本仓库对版本敏感 API 一律走反射兜底
        /// （与 ShowModalDialog / StartUserInteraction 同款做法）；
        /// 反射失败时改走 ViewTableRecord 改中心点 + 高度；再失败也不抛错，
        /// 用户仍可在让出的交互里自己平移 / 缩放查看。
        /// </summary>
        public static void ZoomToRect(Extents2d r)
        {
            Editor ed = Ed;
            if (ed == null) return;
            double w = r.MaxPoint.X - r.MinPoint.X, h = r.MaxPoint.Y - r.MinPoint.Y;
            if (w <= 0 || h <= 0) return;
            try
            {
                MethodInfo m = ed.GetType().GetMethod("ZoomWindow",
                    new[] { typeof(Point3d), typeof(Point3d) });
                if (m != null)
                {
                    m.Invoke(ed, new object[]
                    {
                        new Point3d(r.MinPoint.X, r.MinPoint.Y, 0),
                        new Point3d(r.MaxPoint.X, r.MaxPoint.Y, 0)
                    });
                    try { ed.UpdateScreen(); } catch { }
                    return;
                }
            }
            catch (Exception ex) { Logger.Verbose("ZoomWindow 不可用，改走视图记录：" + ex.Message); }
            try
            {
                using (ViewTableRecord v = ed.GetCurrentView())
                {
                    v.CenterPoint = new Point2d((r.MinPoint.X + r.MaxPoint.X) / 2,
                                                (r.MinPoint.Y + r.MaxPoint.Y) / 2);
                    v.Height = h;
                    v.Width = w;
                    ed.SetCurrentView(v);
                    ed.UpdateScreen();
                }
            }
            catch (Exception ex) { Logger.Verbose("视图缩放失败（不影响返回清单）：" + ex.Message); }
        }

        /// <summary>让用户点一个插入点（生成 CAD 原生 TABLE 目录时用）。</summary>
        public static Point3d? PickPoint(string prompt)
        {
            Editor ed = Ed;
            if (ed == null) return null;
            PromptPointOptions ppo = new PromptPointOptions("\n" + prompt);
            ppo.AllowNone = true;
            PromptPointResult r = ed.GetPoint(ppo);
            if (r.Status == PromptStatus.OK) return r.Value;
            return null;
        }

        /// <summary>UCS(1) → WCS(0)：SelectCrossingWindow 要求 WCS 坐标，而 GetPoint 给的是 UCS。</summary>
        public static Point3d UcsToWcs(Point3d ucs)
        {
            if (!_transAvailable) return ucs;
            try
            {
                ResultBuffer from = new ResultBuffer(new TypedValue(5003, 1));
                ResultBuffer to = new ResultBuffer(new TypedValue(5003, 0));
                double[] p = { ucs.X, ucs.Y, ucs.Z };
                double[] r = { 0.0, 0.0, 0.0 };
                int ok = AcedTrans(p, from.UnmanagedObject, to.UnmanagedObject, 0, r);
                from.Dispose(); to.Dispose();
                if (ok != 0) return new Point3d(r[0], r[1], r[2]);
            }
            catch (Exception ex)
            {
                _transAvailable = false;
                Logger.Warn("acedTrans(UCS→WCS) 不可用，按原坐标处理：" + ex.Message);
            }
            return ucs;
        }

        /// <summary>
        /// 在模型空间连续框选矩形范围，返回 WCS 的 Extents2d 列表。回车 / ESC 结束。
        /// 手动框选批量打印、录入图框时点取区域都走这里。allowMany=false 时只取一个。
        /// </summary>
        public static List<Extents2d> PickRegionsWcs(string firstPrompt, string nextPrompt, bool allowMany)
        {
            List<Extents2d> res = new List<Extents2d>();
            Editor ed = Ed;
            if (ed == null) return res;
            while (true)
            {
                PromptPointOptions ppo = new PromptPointOptions("\n" + (res.Count == 0 ? firstPrompt : nextPrompt));
                ppo.AllowNone = true;
                PromptPointResult p1 = ed.GetPoint(ppo);
                if (p1.Status != PromptStatus.OK) break;     // 回车 / ESC 结束
                PromptCornerOptions pco = new PromptCornerOptions("\n指定另一角点（回车取消本次）: ", p1.Value);
                PromptPointResult p2 = ed.GetCorner(pco);
                if (p2.Status != PromptStatus.OK) break;
                Point3d a = UcsToWcs(p1.Value), b = UcsToWcs(p2.Value);
                double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
                double y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
                if (x1 - x0 > 1e-9 && y1 - y0 > 1e-9) res.Add(new Extents2d(x0, y0, x1, y1));
                if (!allowMany) break;
            }
            return res;
        }

        /// <summary>手动框选一个矩形窗口（单张打印），返回 DCS 的 Extents2d。</summary>
        public static Extents2d? PickWindowUcs()
        {
            Editor ed = Ed;
            if (ed == null) return null;
            PromptPointOptions ppo = new PromptPointOptions("\n指定打印窗口第一角点: ");
            ppo.AllowNone = false;
            PromptPointResult p1 = ed.GetPoint(ppo);
            if (p1.Status != PromptStatus.OK) return null;
            PromptCornerOptions pco = new PromptCornerOptions("\n指定打印窗口另一角点: ", p1.Value);
            PromptPointResult p2 = ed.GetCorner(pco);
            if (p2.Status != PromptStatus.OK) return null;
            Point2d a = UcsToDcs(p1.Value);
            Point2d b = UcsToDcs(p2.Value);
            return new Extents2d(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }
        #endregion

        #region ---- 打印机 / 打印样式 ----
        public static List<string> GetPlotDevices()
        {
            List<string> result = new List<string>();
            try
            {
                var psv = PlotSettingsValidator.Current;
                PlotSettings tmp = new PlotSettings(true);
                using (tmp)
                {
                    psv.RefreshLists(tmp);
                    StringCollection list = psv.GetPlotDeviceList();
                    foreach (string s in list) if (!string.IsNullOrEmpty(s)) result.Add(s);
                }
            }
            catch (Exception ex) { Logger.Warn("读取打印机列表失败：" + ex.Message); }
            return result;
        }

        public static List<string> GetPlotStyleSheets()
        {
            List<string> result = new List<string>();
            try
            {
                var psv = PlotSettingsValidator.Current;
                PlotSettings tmp = new PlotSettings(true);
                using (tmp)
                {
                    psv.RefreshLists(tmp);
                    IEnumerable list = psv.GetPlotStyleSheetList() as IEnumerable;
                    if (list != null) foreach (object o in list) if (o != null) result.Add(o.ToString());
                }
            }
            catch (Exception ex) { Logger.Warn("读取 CTB 列表失败：" + ex.Message); }
            if (!result.Contains("monochrome.ctb")) result.Insert(0, "monochrome.ctb");
            if (!result.Contains("Grayscale.ctb")) result.Insert(0, "Grayscale.ctb");
            return result;
        }

        /// <summary>按名称找 PC3；找不到时返回系统里第一个 pdf 类设备。</summary>
        public static string ResolvePdfDevice(string preferred)
        {
            List<string> devices = GetPlotDevices();
            foreach (string d in devices)
                if (string.Equals(d, preferred, StringComparison.OrdinalIgnoreCase)) return d;
            foreach (string d in devices)
                if (d.IndexOf("DWG To PDF", StringComparison.OrdinalIgnoreCase) >= 0) return d;
            foreach (string d in devices)
                if (d.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0) return d;
            return preferred;   // 交给 PlotInfoValidator 去报更准确的错
        }
        #endregion

        #region ---- 小工具 ----
        public static string SafeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Replace('/', '_').Replace('\\', '_').Replace(':', '_')
                 .Replace('*', '_').Replace('?', '_').Replace('"', '_')
                 .Replace('<', '_').Replace('>', '_').Replace('|', '_');
            s = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim().TrimEnd('.');
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }

        /// <summary>避免同名覆盖：foo.pdf → foo(2).pdf …</summary>
        public static string UniquePath(string fullPath)
        {
            if (!File.Exists(fullPath)) return fullPath;
            if (GlobalConfig.Current.OverwriteExisting) return fullPath;
            string dir = Path.GetDirectoryName(fullPath);
            string name = Path.GetFileNameWithoutExtension(fullPath);
            string ext = Path.GetExtension(fullPath);
            for (int i = 2; i < 10000; i++)
            {
                string p = Path.Combine(dir, name + "(" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
            return Path.Combine(dir, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
        }
        #endregion
    }
}
