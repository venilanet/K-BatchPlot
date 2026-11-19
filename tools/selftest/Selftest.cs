using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MPlot
{
    // ================================================================
    // 测试替身：让「纯逻辑源文件」脱离 AutoCAD 单独编译运行。
    // 这些类在生产代码里分别位于 AcadUtil.cs / Logger.cs，
    // 那三个文件带 CAD 引用，无法脱离 CAD 编译。
    // AcadUtil.SafeFileName 的方法体与生产实现逐字一致（static_check.py 会校验）。
    // ================================================================
    // SheetItem 定义在 FrameScanner.cs（带 CAD 引用）。这里只保留纯数据字段，
    // 字段名与生产类一致；static_check.py 会校验替身字段不超出生产定义。
    public sealed class SheetItem
    {
        public int Seq { get; set; }
        public int Index { get; set; }
        public string BlockName { get; set; }
        public FrameEntry Config { get; set; }
        public string LayoutName { get; set; }
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
    }

    internal static class AcadUtil
    {
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
    }

    internal static class Logger
    {
        public static void Warn(string s) { Console.WriteLine("    [stub-logger] " + s); }
        public static void Info(string s) { }
    }

    // PaperPresets / PaperSpec 本来就定义在 Models.cs 里（无 CAD 依赖），直接用真实实现。

    internal static class Program
    {
        private static int _pass, _fail;

        // GlobalConfig.SetDefaults() 是 private，测试里用反射调用它，不改生产代码。
        private static void InitDefaults(GlobalConfig c)
        {
            typeof(GlobalConfig).GetMethod("SetDefaults",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(c, null);
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("PASS  " + name + (detail == "" ? "" : "  |  " + detail)); }
            else { _fail++; Console.WriteLine("FAIL  " + name + "  |  " + detail); }
        }

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            string dir = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
            Console.WriteLine("SELFTEST_DIR=" + dir);

            // ------------------------------------------------ 1. MiniJson 往返
            Console.WriteLine("--- 1. MiniJson ---");
            try
            {
                GlobalConfig c = new GlobalConfig();
                InitDefaults(c);
                c.ProjectPrefix = "中文项目\"引号\"\\反斜杠\r\n换行\t制表";
                c.MergePdf = true;
                c.PaperCopies = 3;
                c.WindowMarginMm = 2.5;
                c.TokenMap["X"] = "图名";

                string json = MiniJson.Stringify(c);
                GlobalConfig back = MiniJson.ToObject<GlobalConfig>(json);
                Check("GlobalConfig 往返：字符串转义", back.ProjectPrefix == c.ProjectPrefix,
                      "len=" + back.ProjectPrefix.Length);
                Check("GlobalConfig 往返：bool / int / double",
                      back.MergePdf && back.PaperCopies == 3 && Math.Abs(back.WindowMarginMm - 2.5) < 1e-9,
                      "copies=" + back.PaperCopies);
                Check("GlobalConfig 往返：枚举", back.MergeMode == c.MergeMode && back.ScaleMode == c.ScaleMode,
                      back.MergeMode + " / " + back.ScaleMode);
                Check("GlobalConfig 往返：Dictionary", back.TokenMap.ContainsKey("X") && back.TokenMap["X"] == "图名",
                      "keys=" + back.TokenMap.Count);
                Check("字符串里的换行被正确转义为 \\n", json.Contains("\\n") && !json.Contains("\r\n"),
                      "json len=" + json.Length);

                FrameLibrary lib = FrameLibrary.CreateDefault();
                lib.Frames[0].Note = "A1/图框:\\路径?\"含特殊字符*<>";
                string libJson = MiniJson.Stringify(lib);
                FrameLibrary lib2 = MiniJson.ToObject<FrameLibrary>(libJson);
                Check("FrameLibrary 往返：条目数 / 块名 / 纸张",
                      lib2.Frames.Count == lib.Frames.Count &&
                      lib2.Frames[0].BlockName == lib.Frames[0].BlockName &&
                      lib2.Frames[0].Paper.WidthMm == lib.Frames[0].Paper.WidthMm,
                      "frames=" + lib2.Frames.Count);
                Check("FrameLibrary 往返：特殊字符备注", lib2.Frames[0].Note == lib.Frames[0].Note, lib2.Frames[0].Note);

                // Populate：缺字段必须保留原值（配置文件向后兼容）
                GlobalConfig target = new GlobalConfig(); InitDefaults(target);
                string oldDevice = target.PlotDevice;
                MiniJson.Populate("{\"PaperCopies\":9,\"未知字段\":123}", target);
                Check("Populate 缺字段保留默认值", target.PaperCopies == 9 && target.PlotDevice == oldDevice,
                      "copies=" + target.PaperCopies + " device=" + target.PlotDevice);
            }
            catch (Exception ex) { Check("MiniJson 组", false, ex.GetType().Name + ": " + ex.Message); }

            // ------------------------------------------------ 2. 命名模板
            Console.WriteLine("--- 2. Naming ---");
            try
            {
                GlobalConfig cfg = new GlobalConfig(); InitDefaults(cfg);
                SheetItem s = new SheetItem
                {
                    DrawNo = "JZ-01", Title = "一层平面图", Rev = "A",
                    BlockName = "A1图框", LayoutName = "模型",
                    Config = new FrameEntry { Priority = 10, Paper = PaperPresets.Get("A1"), NameTemplate = Naming.DefaultTemplate }
                };
                Check("默认模板=图号_图名_图框大小",
                      Naming.BuildFileName(s, cfg, 1) == "JZ-01_一层平面图_A1", Naming.BuildFileName(s, cfg, 1));
                Check("旧模板 A_C_B 仍兼容（图号_图名_版次）",
                      Naming.BuildFileName(s, cfg, 1, "A_C_B") == "JZ-01_一层平面图_A", Naming.BuildFileName(s, cfg, 1, "A_C_B"));
                Check("兼容用户口径 A_C_T",
                      Naming.BuildFileName(s, cfg, 1, "A_C_T") == "JZ-01_一层平面图_A",
                      Naming.BuildFileName(s, cfg, 1, "A_C_T"));
                Check("花括号语法 {A}-{C}({B})",
                      Naming.BuildFileName(s, cfg, 1, "{A}-{C}({B})") == "JZ-01-一层平面图(A)",
                      Naming.BuildFileName(s, cfg, 1, "{A}-{C}({B})"));
                string npa = Naming.BuildFileName(s, cfg, 7, "N_P_A");
                Check("序号 / 项目前缀符号", npa.StartsWith("07_") && npa.Contains("JZ-01") && !npa.Contains("__"), npa);
                s.Rev = ""; s.Title = "";
                Check("空字段整段丢弃，不产生双下划线",
                      Naming.BuildFileName(s, cfg, 2, "A_C_B") == "JZ-01", Naming.BuildFileName(s, cfg, 2, "A_C_B"));
                s.Title = "A/B:C*?\"<>|\\名";
                string cleaned = Naming.BuildFileName(s, cfg, 3, "C");
                Check("非法文件名字符全部清洗",
                      cleaned.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0, cleaned);
                s.Title = new string('图', 200);
                Check("超长文件名截断到 110 字", Naming.BuildFileName(s, cfg, 4, "C").Length <= 110,
                      Naming.BuildFileName(s, cfg, 4, "C").Length.ToString());
                Check("Preview 产出 .pdf 预览", Naming.Preview("A_C_B", cfg).EndsWith(".pdf"), Naming.Preview("A_C_B", cfg));
                Check("ValidateTemplate 能报未定义符号",
                      Naming.ValidateTemplate("A_Z_B", cfg).Contains("Z"), Naming.ValidateTemplate("A_Z_B", cfg));
            }
            catch (Exception ex) { Check("Naming 组", false, ex.GetType().Name + ": " + ex.Message); }

            // ------------------------------------------------ 3. 内置 PDF 合并
            Console.WriteLine("--- 3. BuiltInPdfMerge ---");
            try
            {
                List<string> pdfs = new List<string>();
                for (int i = 1; i <= 3; i++) pdfs.Add(Path.Combine(dir, "page" + i + ".pdf"));
                foreach (string p in pdfs)
                    if (!File.Exists(p)) throw new FileNotFoundException("测试 PDF 未生成: " + p);
                string outPdf = Path.Combine(dir, "merged.pdf");
                string err;
                bool ok = BuiltInPdfMerge.Merge(pdfs, outPdf, out err);
                Check("合并 3 个单页 PDF 返回 true", ok, "err=" + err);
                if (ok)
                {
                    FileInfo fi = new FileInfo(outPdf);
                    Check("产物非空", fi.Length > 1000, fi.Length + " bytes");
                    string all = File.ReadAllText(outPdf, Encoding.GetEncoding(28591));
                    Check("产物含 /Count 3", all.Contains("/Count 3"), "");
                    Check("产物 xref + trailer + EOF 齐全",
                          all.Contains("xref") && all.Contains("trailer") && all.TrimEnd().EndsWith("%%EOF"), "");
                }
            }
            catch (Exception ex) { Check("BuiltInPdfMerge 组", false, ex.GetType().Name + ": " + ex.Message); }

            // ------------------------------------------------ 4. 导出前检查辅助
            Console.WriteLine("--- 4. Preflight helpers ---");
            try
            {
                Check("保留名 CON / PRN / AUX / NUL",
                      Naming.IsReservedFileName("CON") && Naming.IsReservedFileName("prn")
                      && Naming.IsReservedFileName("AUX") && Naming.IsReservedFileName("nul"), "");
                Check("保留名带扩展名也认（CON.txt）", Naming.IsReservedFileName("CON.txt"), "");
                Check("COM1 / COM9 / LPT1 是保留名，COM10 不是",
                      Naming.IsReservedFileName("COM1") && Naming.IsReservedFileName("COM9")
                      && Naming.IsReservedFileName("LPT1") && !Naming.IsReservedFileName("COM10"), "");
                Check("正常图号不是保留名（D-XT1）", !Naming.IsReservedFileName("D-XT1")
                      && !Naming.IsReservedFileName("") && !Naming.IsReservedFileName(null), "");
                Check("非法字符：斜杠 / 冒号 / 星号 / 控制符",
                      Naming.HasIllegalFileNameChars("A/B") && Naming.HasIllegalFileNameChars("A:B")
                      && Naming.HasIllegalFileNameChars("A*B") && Naming.HasIllegalFileNameChars("A\tB"), "");
                Check("非法字符：以点或空格结尾也算",
                      Naming.HasIllegalFileNameChars("A.") && Naming.HasIllegalFileNameChars("A "), "");
                Check("正常文件名无非法字符", !Naming.HasIllegalFileNameChars("D-XT1_配电系统图一")
                      && !Naming.HasIllegalFileNameChars(""), "");

                List<SheetItem> fix1 = new List<SheetItem>
                {
                    new SheetItem { FileStem = "A/B" },
                    new SheetItem { FileStem = "正常名" },
                    new SheetItem { FileStem = "C:D" }
                };
                int n1 = Naming.SanitizeStems(fix1);
                Check("SanitizeStems：非法字符替换为 _，返回改动张数",
                      n1 == 2 && fix1[0].FileStem == "A_B" && fix1[1].FileStem == "正常名" && fix1[2].FileStem == "C_D",
                      "n=" + n1 + " " + fix1[0].FileStem + " / " + fix1[2].FileStem);

                List<SheetItem> fix2 = new List<SheetItem>
                {
                    new SheetItem { FileStem = "A" },
                    new SheetItem { FileStem = "a" },
                    new SheetItem { FileStem = "B" },
                    new SheetItem { FileStem = "A" },
                    new SheetItem { FileStem = "" }
                };
                int n2 = Naming.FixDuplicateFileNames(fix2);
                Check("FixDuplicateFileNames：忽略大小写，首次保留原名，后面 _01/_02",
                      n2 == 2 && fix2[0].FileStem == "A" && fix2[1].FileStem == "a_01"
                      && fix2[2].FileStem == "B" && fix2[3].FileStem == "A_01" && fix2[4].FileStem == "",
                      "n=" + n2 + " " + string.Join(",", fix2.ConvertAll(x => x.FileStem).ToArray()));
                Check("FullFileName 补 .pdf 后缀",
                      Naming.FullFileName(fix2[0]) == "A.pdf" && Naming.FullFileName(null) == ".pdf", "");

                PaperSpec ps;
                Check("TryParseDisplay：A1", PaperPresets.TryParseDisplay("A1", out ps)
                      && ps.WidthMm == 841 && ps.HeightMm == 594, "");
                Check("TryParseDisplay：A1+1/2", PaperPresets.TryParseDisplay("A1+1/2", out ps)
                      && ps.Preset == "A1+1/2", "");
                Check("TryParseDisplay：A1 841×594mm（带尺寸后缀也认）",
                      PaperPresets.TryParseDisplay("A1 841×594mm", out ps) && ps.Preset == "A1", "");
                Check("TryParseDisplay：自定义 880×640mm 还原实测尺寸",
                      PaperPresets.TryParseDisplay("自定义 880×640mm", out ps)
                      && ps.IsCustom && ps.WidthMm == 880 && ps.HeightMm == 640, "");
                Check("TryParseDisplay：裸「自定义」回默认 A1 尺寸",
                      PaperPresets.TryParseDisplay("自定义", out ps) && ps.IsCustom, "");
                Check("TryParseDisplay：未知名 / 坏尺寸返回 false",
                      !PaperPresets.TryParseDisplay("ZZZ", out ps)
                      && !PaperPresets.TryParseDisplay("自定义 abc", out ps)
                      && !PaperPresets.TryParseDisplay("", out ps), "");
            }
            catch (Exception ex) { Check("Preflight helpers 组", false, ex.GetType().Name + ": " + ex.Message); }

            Console.WriteLine("RESULT PASS=" + _pass + " FAIL=" + _fail);
            return _fail == 0 ? 0 : 1;
        }
    }
}
