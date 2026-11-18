using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcCore = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MPlot
{
    /// <summary>
    /// 图纸目录：CSV（Excel 直接打开）+ 可选 CAD 原生 TABLE。
    /// 两种输出完全跟随批打排序后的队列，序号一致。
    /// </summary>
    public static class Catalog
    {
        public static readonly string[] Headers = { "序号", "图号", "图名", "版次", "图框大小", "PDF 文件名" };

        /// <summary>图框大小 = 纸张代号（A0/A1/A2/A3/A4…），命名模板里「图框大小」同源。</summary>
        private static string PaperCode(SheetItem s)
        {
            return (s != null && s.Config != null && s.Config.Paper != null)
                ? (s.Config.Paper.Preset ?? "") : "";
        }

        public static string[] Row(SheetItem s)
        {
            return new[] {
                s.Index.ToString(),
                s.DrawNo ?? "",
                s.Title ?? "",
                s.Rev ?? "",
                PaperCode(s),
                string.IsNullOrEmpty(s.PdfPath) ? (s.FileStem ?? "") + ".pdf" : Path.GetFileName(s.PdfPath)
            };
        }

        // ---------------------------------------------------------------- CSV
        /// <summary>UTF-8 with BOM：Excel 双击打开中文不乱码，这是硬要求。</summary>
        public static bool WriteCsv(List<SheetItem> sheets, string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(true)))
                {
                    w.WriteLine(string.Join(",", Headers.Select(CsvCell).ToArray()));
                    foreach (SheetItem s in sheets) w.WriteLine(string.Join(",", Row(s).Select(CsvCell).ToArray()));
                }
                Logger.Info("图纸目录 CSV：" + path + "（" + sheets.Count + " 行）");
                return true;
            }
            catch (Exception ex) { Logger.Error("目录 CSV 写入失败", ex); return false; }
        }

        private static string CsvCell(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        /// <summary>辅助功能里的「完整明细」：除标准 5 列外，带上块名/纸张/比例/识别来源/状态，方便排查。</summary>
        public static bool WriteDetailCsv(List<SheetItem> sheets, string path)
        {
            try
            {
                string[] h = { "序号", "图号", "图名", "版次", "PDF 文件名", "图框块名", "纸张",
                               "图框实宽mm", "图框实高mm", "比例分母", "信息来源", "布局", "状态" };
                using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(true)))
                {
                    w.WriteLine(string.Join(",", h.Select(CsvCell).ToArray()));
                    foreach (SheetItem s in sheets)
                    {
                        double den = (s.Config == null || s.Config.Paper == null || s.Config.Paper.WidthMm <= 0)
                            ? 0 : PaperMatcher.ComputeScaleDenominator(s.WidthMm, s.HeightMm, s.Config, s.Rotated);
                        string[] r = {
                            s.Index.ToString(), s.DrawNo ?? "", s.Title ?? "", s.Rev ?? "",
                            string.IsNullOrEmpty(s.PdfPath) ? (s.FileStem ?? "") + ".pdf" : Path.GetFileName(s.PdfPath),
                            s.BlockName ?? "", s.Config == null || s.Config.Paper == null ? "" : s.Config.Paper.ToString(),
                            s.WidthMm.ToString("0.##"), s.HeightMm.ToString("0.##"),
                            den <= 0 ? "" : "1:" + den.ToString("0.####"),
                            s.MetaSource ?? "", s.LayoutName ?? "",
                            s.Plotted ? "成功" : ("失败" + (string.IsNullOrEmpty(s.Error) ? "" : "：" + s.Error))
                        };
                        w.WriteLine(string.Join(",", r.Select(CsvCell).ToArray()));
                    }
                }
                return true;
            }
            catch (Exception ex) { Logger.Error("明细 CSV 写入失败", ex); return false; }
        }

        // ---------------------------------------------------------------- CAD 原生 TABLE
        /// <summary>在当前 DWG 里生成真正的 CAD TABLE 对象（不是块、不是炸开的线）。</summary>
        public static bool CreateTable(List<SheetItem> sheets, Point3d insertPoint, GlobalConfig cfg, out string error)
        {
            error = "";
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) { error = "没有打开的图纸"; return false; }
            Database db = doc.Database;

            // 图框按 1:100 放大画的图里，按 mm 做表格会小得看不见 → 跟着图框放大倍数走
            double mag = 1;
            SheetItem first = sheets.FirstOrDefault(s => s.Config != null && s.Config.Paper != null && s.Config.Paper.WidthMm > 0);
            if (first != null) mag = Math.Max(1, first.WidthMm / first.Config.Paper.WidthMm);
            double textH = Math.Max(cfg.TableTextHeightMm, 1.5) * mag;
            double rowH = textH * 1.8;
            double[] colW = { 12, 45, 95, 18, 26, 120 };
            for (int i = 0; i < colW.Length; i++) colW[i] *= mag;

            try
            {
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                    if (space == null) { error = "当前空间不可用"; return false; }

                    Table tb = new Table();
                    try
                    {
                        tb.SetSize(sheets.Count + 1, Headers.Length);
                        tb.Position = insertPoint;

                        for (int c = 0; c < Headers.Length; c++)
                            SetCell(tb, 0, c, Headers[c], textH, rowH, colW[c]);
                        for (int i = 0; i < sheets.Count; i++)
                        {
                            string[] row = Row(sheets[i]);
                            for (int c = 0; c < Headers.Length; c++)
                                SetCell(tb, i + 1, c, row[c], textH, rowH, colW[c]);
                        }

                        tb.GenerateLayout();
                        space.AppendEntity(tb);
                        tr.AddNewlyCreatedDBObject(tb, true);
                        tr.Commit();
                        Logger.Info("CAD 原生目录表格已生成，共 " + (sheets.Count + 1) + " 行");
                        return true;
                    }
                    finally { tb.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Logger.Error("生成 CAD 目录表格失败", ex);
                return false;
            }
        }

        private static void SetCell(Table tb, int row, int col, string text, double textH, double rowH, double colW)
        {
            try
            {
                Cell cell = tb.Cells[row, col];
                cell.TextString = text ?? "";
                TrySetProperty(cell, "TextHeight", textH);
            }
            catch (Exception ex) { Logger.Verbose("单元格写入跳过 [" + row + "," + col + "]：" + ex.Message); }
            TryCall(tb, "SetRowHeight", row, rowH);
            TryCall(tb, "SetColumnWidth", col, colW);
        }

        private static void TrySetProperty(object target, string name, object value)
        {
            try
            {
                PropertyInfo p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite) p.SetValue(target, value, null);
            }
            catch { }
        }

        private static void TryCall(object target, string method, params object[] args)
        {
            try
            {
                MethodInfo[] ms = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == method && m.GetParameters().Length == args.Length).ToArray();
                foreach (MethodInfo m in ms) { try { m.Invoke(target, args); return; } catch { } }
            }
            catch { }
        }
    }
}
