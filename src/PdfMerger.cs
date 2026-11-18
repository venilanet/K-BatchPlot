using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
#if MPLOT_PDFSHARP
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
#endif

namespace MPlot
{
    /// <summary>
    /// 单页 PDF → 一份图纸合集。
    /// 三通道，任一失败自动切下一个，全部失败时保留全部单页 PDF 并在日志里说明，绝不丢文件：
    ///   1) 内置合并（默认，纯 C# 零依赖，装完就能用，不需要 GS / NuGet / 联网）
    ///   2) Ghostscript（碰到对象流 / 加密 / 增量更新的 PDF 时兜底，压缩和字体处理最稳）
    ///   3) PdfSharp（仅在编译期显式启用 MPLOT_PDFSHARP 时才存在）
    /// </summary>
    public static class PdfMerger
    {
        public static bool Merge(List<string> files, string outputPath, GlobalConfig cfg,
                                 out string error, out string channel)
        {
            error = ""; channel = "";
            List<string> valid = (files ?? new List<string>())
                .Where(f => !string.IsNullOrEmpty(f) && File.Exists(f) && new FileInfo(f).Length > 0)
                .ToList();
            if (valid.Count == 0) { error = "没有可合并的 PDF"; return false; }
            if (valid.Count == 1)
            {
                try { File.Copy(valid[0], outputPath, true); channel = "单页直接复制"; return true; }
                catch (Exception ex) { error = "单页复制失败：" + ex.Message; return false; }
            }

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            bool gsFirst = cfg.MergeMode == MergeMode.GhostscriptFirst;
            List<string> errors = new List<string>();
            string[] order = gsFirst ? new[] { "gs", "builtin" } : new[] { "builtin", "gs" };
            foreach (string c in order)
            {
                if (cfg.MergeMode == MergeMode.GhostscriptOnly && c == "builtin") continue;
                string e;
                bool ok = c == "gs"
                    ? MergeWithGhostscript(valid, outputPath, cfg, out e)
                    : MergeWithBuiltInOrPdfSharp(valid, outputPath, out e, out channel);
                if (ok)
                {
                    // 校验产物不是 0 字节
                    if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    {
                        if (c == "gs") channel = "Ghostscript";
                        else if (string.IsNullOrEmpty(channel)) channel = "内置合并";
                        return true;
                    }
                    e = "合并产物为空文件";
                }
                channel = "";
                errors.Add((c == "gs" ? "Ghostscript" : "内置合并") + "：" + e);
                TryDeletePartial(outputPath);
            }
            error = string.Join(" | ", errors.ToArray());
            Logger.Warn("PDF 合并失败（单页文件已保留）：" + error);
            return false;
        }

        /// <summary>先用零依赖内置合并；遇到不支持的 PDF 结构时，若编译启用了 PdfSharp 再试它。</summary>
        private static bool MergeWithBuiltInOrPdfSharp(List<string> files, string outputPath,
                                                       out string error, out string channel)
        {
            channel = "";
            string eBuiltIn;
            if (BuiltInPdfMerge.Merge(files, outputPath, out eBuiltIn))
            { channel = "内置合并"; error = ""; return true; }

#if MPLOT_PDFSHARP
            string eSharp;
            if (MergeWithPdfSharp(files, outputPath, out eSharp))
            { channel = "PdfSharp"; error = ""; return true; }
            error = "内置合并：" + eBuiltIn + "；PdfSharp：" + eSharp;
#else
            error = eBuiltIn + "（可安装 Ghostscript 并在「图框信息库 → 其它 → GS 路径」指定以合并这类 PDF）";
#endif
            return false;
        }

        private static void TryDeletePartial(string p)
        {
            try { if (File.Exists(p) && new FileInfo(p).Length == 0) File.Delete(p); } catch { }
        }

        // ---------------------------------------------------------------- Ghostscript
        public static string FindGhostscript(GlobalConfig cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.GhostscriptPath) && File.Exists(cfg.GhostscriptPath))
                return cfg.GhostscriptPath;
            string[] roots = {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                @"C:\Program Files", @"D:\Program Files", @"D:\gs"
            };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string gsRoot = Path.Combine(root, "gs");
                if (!Directory.Exists(gsRoot)) continue;
                foreach (string sub in Directory.GetDirectories(gsRoot).OrderByDescending(x => x))
                {
                    foreach (string exe in new[] { "gswin64c.exe", "gswin32c.exe" })
                    {
                        string p = Path.Combine(sub, "bin", exe);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            return "";
        }

        private static bool MergeWithGhostscript(List<string> files, string outputPath, GlobalConfig cfg, out string error)
        {
            error = "";
            string exe = FindGhostscript(cfg);
            if (string.IsNullOrEmpty(exe))
            { error = "未找到 gswin64c.exe，请在「图框信息库 → 其它 → GS 路径」里指定"; return false; }

            // GS 支持 @responsefile，避免图纸多时命令行超 32K 上限
            string rsp = Path.Combine(Path.GetTempPath(), "mplot_gs_" + Guid.NewGuid().ToString("N") + ".rsp");
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("-q");
                sb.AppendLine("-dNOPAUSE");
                sb.AppendLine("-dBATCH");
                sb.AppendLine("-dSAFER");
                sb.AppendLine("-sDEVICE=pdfwrite");
                sb.AppendLine("-dPDFSETTINGS=/prepress");
                sb.AppendLine("-dCompatibilityLevel=1.5");
                sb.AppendLine("-sOutputFile=" + Quote(outputPath));
                sb.AppendLine("-f");
                foreach (string f in files) sb.AppendLine(Quote(f));
                File.WriteAllText(rsp, sb.ToString(), Encoding.UTF8);

                ProcessStartInfo psi = new ProcessStartInfo(exe, "@" + Quote(rsp));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } error = "Ghostscript 超时"; return false; }
                    if (p.ExitCode != 0) { error = "GS 退出码 " + p.ExitCode + "：" + Truncate(se + so); return false; }
                }
                return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { try { if (File.Exists(rsp)) File.Delete(rsp); } catch { } }
        }

        // Windows 路径里的反斜杠原样保留（GS 不做 C 风格转义，多加一杠反而找不到文件）
        private static string Quote(string s) { return "\"" + (s ?? "") + "\""; }
        private static string Truncate(string s) { s = s ?? ""; return s.Length > 400 ? s.Substring(0, 400) : s; }

        // ---------------------------------------------------------------- PdfSharp
        private static bool MergeWithPdfSharp(List<string> files, string outputPath, out string error)
        {
#if MPLOT_PDFSHARP
            error = "";
            PdfDocument outDoc = null;
            try
            {
                outDoc = new PdfDocument();
                foreach (string f in files)
                {
                    using (PdfDocument src = PdfReader.Open(f, PdfDocumentOpenMode.Import))
                    {
                        for (int i = 0; i < src.PageCount; i++) outDoc.AddPage(src.Pages[i]);
                    }
                }
                outDoc.Save(outputPath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Logger.Verbose("PdfSharp 合并失败（CAD PDF 常见对象流不兼容，会自动切 GS）：" + ex);
                return false;
            }
            finally { if (outDoc != null) { try { outDoc.Dispose(); } catch { } } }
#else
            error = "编译时未启用 MPLOT_PDFSHARP，请配置 GS 路径启用 Ghostscript 合并";
            return false;
#endif
        }

        /// <summary>合并成功后按配置清理单页 PDF。只删本次生成的、且必须在 UI 里二次确认过。</summary>
        public static int CleanupSingles(List<string> files, string keepMergedPath)
        {
            int n = 0;
            foreach (string f in files)
            {
                if (string.IsNullOrEmpty(f) || !File.Exists(f)) continue;
                if (string.Equals(f, keepMergedPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); n++; }
                catch (Exception ex) { Logger.Warn("单页 PDF 清理失败：" + f + " / " + ex.Message); }
            }
            return n;
        }
    }
}
