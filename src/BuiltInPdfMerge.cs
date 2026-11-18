using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MPlot
{
    /// <summary>
    /// 零第三方依赖的 PDF 合并器 —— 让「开箱即用」不依赖 Ghostscript / NuGet。
    ///
    /// 适用范围：AutoCAD「DWG To PDF.pc3」以及绝大多数虚拟打印机输出的单页 PDF，
    /// 即：经典 xref 表 + 未加密 + 无对象流 + 对象代为 0 + 无增量更新。
    /// 遇到不认识的结构（xref stream / ObjStm / 加密 / 前次版本链）会**明确报错**，
    /// 由 PdfMerger 自动切到 Ghostscript，绝不输出一个看起来成功其实损坏的合集。
    ///
    /// 做法：把每个源文件的对象整体复制并重新编号，重写字典里的间接引用，
    /// 新建一个 /Pages 根把所有 /Page 挂上去，重建 xref / trailer。
    /// 压缩流、字体、图像字节一律原样复制，不做任何重新编码。
    /// 合并后会用同一个解析器重新读回产物自检页数，自检不过就删掉产物。
    /// </summary>
    public static class BuiltInPdfMerge
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591); // 字节 ↔ 字符 1:1
        private static readonly Regex RefRx = new Regex(@"(?<![/\w])(\d+)\s+(\d+)\s+R\b");
        private static readonly Regex KidsRx = new Regex(@"/Kids\s*\[([^\]]*)\]");
        private static readonly Regex RootRx = new Regex(@"/Root\s+(\d+)\s+(\d+)\s+R");
        private static readonly Regex PageRx = new Regex(@"/Type\s*/Page(?![sA-Za-z])");
        private static readonly Regex PagesRx = new Regex(@"/Type\s*/Pages\b");
        private static readonly Regex ParentRx = new Regex(@"/Parent\s+\d+\s+\d+\s+R");
        private static readonly Regex PagesRefRx = new Regex(@"/Pages\s+\d+\s+\d+\s+R");

        // ================================================================ 对外入口
        public static bool Merge(List<string> files, string outputPath, out string error)
        {
            error = "";
            if (files == null || files.Count < 2) { error = "至少需要两个 PDF"; return false; }

            List<SrcDoc> docs = new List<SrcDoc>();
            int pageTotal = 0;
            foreach (string f in files)
            {
                SrcDoc d;
                string e;
                if (!SrcDoc.Load(f, out d, out e))
                { error = "内置合并无法读取 " + Path.GetFileName(f) + "：" + e; return false; }
                if (d.PageNums.Count == 0)
                { error = "内置合并未找到页面：" + Path.GetFileName(f); return false; }
                docs.Add(d);
                pageTotal += d.PageNums.Count;
            }

            // ---- 分配新对象号段：每个源文件一段连续区间，old → old + Base ----
            int next = 1;
            foreach (SrcDoc d in docs)
            {
                d.Base = next;
                next += d.MaxObjNum + 1;
            }
            int catalogNum = next;
            int pagesNum = next + 1;
            int size = next + 2;

            string tmp = outputPath + ".mplot.tmp";
            try
            {
                string dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (MemoryStream ms = new MemoryStream())
                {
                    long[] objOffset = new long[size];
                    WriteAscii(ms, "%PDF-" + HighestVersion(docs) + "\n");
                    // 二进制注释：告诉阅读器本文件含二进制流
                    ms.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);

                    List<int> allPages = new List<int>();
                    foreach (SrcDoc d in docs)
                    {
                        List<int> nums = new List<int>(d.Bodies.Keys);
                        nums.Sort();
                        foreach (int oldNum in nums)
                        {
                            int newNum = d.Base + oldNum;
                            string body = d.Bodies[oldNum];
                            bool isPage = d.PageNums.Contains(oldNum);

                            // 资源继承：/Page 自己没写 /Resources 时，从 /Pages 祖先借一份
                            if (isPage && !body.Contains("/Resources"))
                            {
                                string res = d.FindInheritedResources(oldNum);
                                if (!string.IsNullOrEmpty(res)) body = InsertIntoDict(body, "\n/Resources " + res + "\n");
                            }

                            body = RewriteRefs(body, d);
                            if (isPage)
                                body = ParentRx.Replace(body, "/Parent " + pagesNum.ToString(CultureInfo.InvariantCulture)
                                                              + " 0 R", 1);
                            // 旧页树根节点清空，避免同一个 /Page 同时挂在两棵树上
                            if (PagesRx.IsMatch(DictPart(body)))
                            {
                                body = ReplaceInDict(body, KidsRx, "/Kids []");
                                body = ReplaceInDict(body, new Regex(@"/Count\s+\d+"), "/Count 0");
                            }

                            objOffset[newNum] = ms.Position;
                            WriteAscii(ms, newNum.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
                            WriteAscii(ms, body);
                            WriteAscii(ms, "\nendobj\n");
                        }
                        foreach (int p in d.PageNums) allPages.Add(d.Base + p);
                    }

                    // ---- 新 Catalog：复制首个文件的根字典，把 /Pages 指到新页树 ----
                    SrcDoc first = docs[0];
                    string catBody;
                    if (!first.Bodies.TryGetValue(first.RootNum, out catBody))
                    { error = "首个 PDF 缺少根对象"; return false; }
                    catBody = RewriteRefs(catBody, first);
                    if (!PagesRefRx.IsMatch(DictPart(catBody)))
                    { error = "根对象没有 /Pages 引用，内置合并放弃"; return false; }
                    catBody = ReplaceInDict(catBody, PagesRefRx, "/Pages "
                        + pagesNum.ToString(CultureInfo.InvariantCulture) + " 0 R");

                    objOffset[catalogNum] = ms.Position;
                    WriteAscii(ms, catalogNum.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
                    WriteAscii(ms, catBody);
                    WriteAscii(ms, "\nendobj\n");

                    // ---- 新页树根 ----
                    StringBuilder kids = new StringBuilder();
                    foreach (int p in allPages)
                        kids.Append(p.ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
                    objOffset[pagesNum] = ms.Position;
                    WriteAscii(ms, pagesNum.ToString(CultureInfo.InvariantCulture) + " 0 obj\n<< /Type /Pages /Count "
                        + allPages.Count.ToString(CultureInfo.InvariantCulture) + " /Kids [ " + kids.ToString() + "] >>\nendobj\n");

                    // ---- xref + trailer ----
                    long xrefPos = ms.Position;
                    WriteAscii(ms, "xref\n0 " + size.ToString(CultureInfo.InvariantCulture) + "\n");
                    WriteAscii(ms, "0000000000 65535 f \r\n");
                    for (int i = 1; i < size; i++)
                    {
                        if (objOffset[i] > 0)
                            WriteAscii(ms, objOffset[i].ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \r\n");
                        else
                            WriteAscii(ms, "0000000000 65535 f \r\n");
                    }
                    WriteAscii(ms, "trailer\n<< /Size " + size.ToString(CultureInfo.InvariantCulture)
                        + " /Root " + catalogNum.ToString(CultureInfo.InvariantCulture) + " 0 R >>\nstartxref\n"
                        + xrefPos.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

                    File.WriteAllBytes(tmp, ms.ToArray());
                }

                // ---- 自检：用同一套解析器读回，页数必须对得上 ----
                SrcDoc chk;
                string cerr;
                if (!SrcDoc.Load(tmp, out chk, out cerr) || chk.PageNums.Count != pageTotal)
                {
                    error = "合并产物自检失败（" + cerr + "），已放弃，单页 PDF 全部保留";
                    TryDelete(tmp);
                    return false;
                }

                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tmp, outputPath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                TryDelete(tmp);
                return false;
            }
        }

        private static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        private static string HighestVersion(List<SrcDoc> docs)
        {
            string v = "1.4";
            foreach (SrcDoc d in docs)
                if (string.Compare(d.Version, v, StringComparison.Ordinal) > 0) v = d.Version;
            return v;
        }

        private static void WriteAscii(Stream s, string text)
        {
            byte[] b = Latin1.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        /// <summary>只返回对象的字典部分（stream 之前），避免把压缩数据里的字节误当引用改写。</summary>
        private static string DictPart(string body)
        {
            Match m = Regex.Match(body, @"(?:\r\n|\r|\n)stream(?:\r\n|\r|\n)");
            if (!m.Success) return body;
            return body.Substring(0, m.Index);
        }

        /// <summary>只在字典范围内做一次正则替换。</summary>
        private static string ReplaceInDict(string body, Regex rx, string replacement)
        {
            string dict = DictPart(body);
            string newDict = rx.Replace(dict, replacement);
            if (string.Equals(newDict, dict, StringComparison.Ordinal)) return body;
            return newDict + body.Substring(dict.Length);
        }

        /// <summary>在对象字典最后一个 &gt;&gt; 之前插入内容（用于补 /Resources）。</summary>
        private static string InsertIntoDict(string body, string insert)
        {
            string dict = DictPart(body);
            int i = dict.LastIndexOf(">>", StringComparison.Ordinal);
            if (i < 0) return body;
            return dict.Substring(0, i) + insert + dict.Substring(i) + body.Substring(dict.Length);
        }

        /// <summary>重写字典里所有间接引用，加上该源文件的编号偏移；流数据字节不动。</summary>
        private static string RewriteRefs(string body, SrcDoc d)
        {
            string dict = DictPart(body);
            string changed = RefRx.Replace(dict, delegate(Match m)
            {
                int num, gen;
                if (!int.TryParse(m.Groups[1].Value, out num) || !int.TryParse(m.Groups[2].Value, out gen)) return m.Value;
                if (gen != 0) return m.Value;                 // 非 0 代的对象 Load 阶段已拒绝
                if (num > d.MaxObjNum) return m.Value;        // 悬空引用保持原样
                return (d.Base + num).ToString(CultureInfo.InvariantCulture) + " 0 R";
            });
            return changed + body.Substring(dict.Length);
        }

        // ================================================================ 源 PDF 解析
        private sealed class SrcDoc
        {
            public string Version;
            public int RootNum;
            public int MaxObjNum;
            public int Base;
            public readonly Dictionary<int, long> Offsets = new Dictionary<int, long>();
            public readonly Dictionary<int, string> Bodies = new Dictionary<int, string>();
            public readonly List<int> PageNums = new List<int>();
            private string _text;

            public static bool Load(string path, out SrcDoc doc, out string error)
            {
                doc = null; error = "";
                try
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    if (bytes.Length < 128) { error = "文件过小，不是有效 PDF"; return false; }
                    string text = Latin1.GetString(bytes);
                    if (!text.StartsWith("%PDF-")) { error = "缺少 %PDF 头"; return false; }

                    SrcDoc d = new SrcDoc();
                    d._text = text;
                    Match vm = Regex.Match(text.Substring(0, Math.Min(32, text.Length)), @"%PDF-(\d\.\d)");
                    d.Version = vm.Success ? vm.Groups[1].Value : "1.4";

                    int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
                    if (sx < 0) { error = "找不到 startxref"; return false; }
                    Match om = Regex.Match(text.Substring(sx, Math.Min(80, text.Length - sx)), @"startxref\s+(\d+)");
                    if (!om.Success) { error = "startxref 偏移不可解析"; return false; }
                    long xrefOff;
                    if (!long.TryParse(om.Groups[1].Value, out xrefOff) || xrefOff <= 0 || xrefOff >= text.Length)
                    { error = "startxref 偏移越界"; return false; }

                    int p = (int)xrefOff;
                    if (!Starts(text, p, "xref"))
                    { error = "使用交叉引用流（PDF 1.5+ 对象流），请用 Ghostscript 通道"; return false; }

                    // ---- xref 表 ----
                    p += 4;
                    int guard = 0;
                    while (p < text.Length && guard++ < 100000)
                    {
                        string line = ReadLine(text, ref p);
                        if (line.TrimStart().StartsWith("trailer")) break;
                        Match sm = Regex.Match(line, @"^\s*(\d+)\s+(\d+)\s*$");
                        if (!sm.Success) continue;
                        int start = int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture);
                        int count = int.Parse(sm.Groups[2].Value, CultureInfo.InvariantCulture);
                        if (count < 0 || count > text.Length) { error = "xref 段计数异常"; return false; }
                        for (int i = 0; i < count; i++)
                        {
                            string e = ReadLine(text, ref p);
                            Match em = Regex.Match(e, @"^\s*(\d+)\s+(\d+)\s+([nf])");
                            if (!em.Success) { error = "xref 条目格式异常"; return false; }
                            int num = start + i;
                            if (num > d.MaxObjNum) d.MaxObjNum = num;
                            if (em.Groups[3].Value == "n")
                                d.Offsets[num] = long.Parse(em.Groups[1].Value, CultureInfo.InvariantCulture);
                        }
                    }

                    // ---- trailer ----
                    int tEnd = text.IndexOf("startxref", p, StringComparison.Ordinal);
                    if (tEnd < 0) tEnd = Math.Min(text.Length, p + 4096);
                    string trailer = text.Substring(p, tEnd - p);
                    if (Regex.IsMatch(trailer, @"/Encrypt\b")) { error = "PDF 已加密"; return false; }
                    if (Regex.IsMatch(trailer, @"/Prev\b")) { error = "PDF 含增量更新，请用 Ghostscript 通道"; return false; }
                    if (Regex.IsMatch(trailer, @"/XRefStm\b")) { error = "PDF 含混合 xref 流"; return false; }
                    Match rm = RootRx.Match(trailer);
                    if (!rm.Success) { error = "trailer 缺少 /Root"; return false; }
                    d.RootNum = int.Parse(rm.Groups[1].Value, CultureInfo.InvariantCulture);

                    // ---- 逐个对象取字典 / 流 ----
                    foreach (KeyValuePair<int, long> kv in d.Offsets)
                    {
                        string body;
                        if (!d.ReadBody(kv.Key, kv.Value, out body, out error)) return false;
                        d.Bodies[kv.Key] = body;
                        // 只看字典区：压缩流里的随机字节也可能碰巧出现 "/ObjStm"
                        if (DictPart(body).IndexOf("/ObjStm", StringComparison.Ordinal) >= 0)
                        { error = "PDF 含压缩对象流（ObjStm），请用 Ghostscript 通道"; return false; }
                    }

                    // ---- 沿页树收集页面 ----
                    HashSet<int> seen = new HashSet<int>();
                    if (!d.CollectPages(d.RootNum, seen, 0, out error)) return false;
                    if (d.PageNums.Count == 0) { error = "页树里没有页面"; return false; }

                    doc = d;
                    return true;
                }
                catch (Exception ex) { error = ex.Message; return false; }
            }

            private bool ReadBody(int num, long off, out string body, out string error)
            {
                body = ""; error = "";
                if (off <= 0 || off >= _text.Length) { error = "对象 " + num + " 偏移越界"; return false; }
                int p = (int)off;
                int headLen = Math.Min(80, _text.Length - p);
                Match hm = Regex.Match(_text.Substring(p, headLen), @"^\s*(\d+)\s+(\d+)\s+obj\b");
                if (!hm.Success || int.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture) != num)
                { error = "对象 " + num + " 头不匹配"; return false; }
                if (hm.Groups[2].Value != "0")
                { error = "对象 " + num + " 代不为 0，内置合并不支持"; return false; }
                int bodyStart = p + hm.Index + hm.Length;

                // ★ 只在「本对象」范围内找 stream。
                //   若对全文件后续 64KB 搜，Catalog/Pages/Page 这类没有流的对象会
                //   误命中后面某个 Contents 对象的 stream，字典里于是出现别的对象的
                //   endobj，被误判成「字典解析异常」，内置合并永远失败。
                int endObjAt = _text.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
                if (endObjAt < 0) { error = "对象 " + num + " 缺少 endobj"; return false; }
                int span = Math.Min(endObjAt - bodyStart, 65536);
                if (span < 0) { error = "对象 " + num + " 结构异常"; return false; }

                Match sm = Regex.Match(_text.Substring(bodyStart, span),
                                       @"(?:\r\n|\r|\n)stream(?:\r\n|\r|\n)");
                if (sm.Success)
                {
                    // 只在字典区里找 stream 关键字，避免命中流数据
                    int kw = bodyStart + sm.Index;
                    string dict = _text.Substring(bodyStart, kw - bodyStart);
                    if (dict.IndexOf("endobj", StringComparison.Ordinal) >= 0)
                    { error = "对象 " + num + " 字典解析异常"; return false; }
                    int dataStart = bodyStart + sm.Index + sm.Length;
                    // 先判间接引用，再判直接数字：顺序反了会把 "123 0 R" 误读成长度 123。
                    int end;
                    Match lr = Regex.Match(dict, @"/Length\s+(\d+)\s+\d+\s+R");
                    Match lm = Regex.Match(dict, @"/Length\s+(\d+)");
                    if (lr.Success)
                    {
                        int lenObj = int.Parse(lr.Groups[1].Value, CultureInfo.InvariantCulture);
                        string lb;
                        string le;
                        long lo;
                        if (!Offsets.TryGetValue(lenObj, out lo) || !ReadBody(lenObj, lo, out lb, out le))
                        { error = "对象 " + num + " 的 /Length 间接引用无法解析"; return false; }
                        Match ln = Regex.Match(lb, @"^\s*(\d+)");
                        if (!ln.Success) { error = "对象 " + num + " 的 /Length 值异常"; return false; }
                        end = dataStart + int.Parse(ln.Groups[1].Value, CultureInfo.InvariantCulture);
                    }
                    else if (lm.Success)
                    {
                        int len = int.Parse(lm.Groups[1].Value, CultureInfo.InvariantCulture);
                        end = dataStart + len;
                    }
                    else
                    {
                        end = _text.IndexOf("endstream", dataStart, StringComparison.Ordinal);
                        if (end < 0) { error = "对象 " + num + " 缺少 endstream"; return false; }
                    }
                    int es = _text.IndexOf("endstream", Math.Max(dataStart, end), StringComparison.Ordinal);
                    if (es < 0 || es > end + 64) { error = "对象 " + num + " 流长度与 endstream 不符"; return false; }
                    body = _text.Substring(bodyStart, es + "endstream".Length - bodyStart);
                }
                else
                {
                    body = _text.Substring(bodyStart, endObjAt - bodyStart);
                }
                return true;
            }

            private bool CollectPages(int objNum, HashSet<int> seen, int depth, out string error)
            {
                error = "";
                if (depth > 64 || !seen.Add(objNum)) return true;
                string body;
                if (!Bodies.TryGetValue(objNum, out body)) { error = "页树引用了缺失对象 " + objNum; return false; }
                string dict = DictPart(body);
                if (PageRx.IsMatch(dict)) { PageNums.Add(objNum); return true; }

                // ★ Catalog 对象没有 /Kids，只有 /Pages N 0 R。
                //   只认 /Kids 的话，从 /Root 开始递归会立刻返回、一页也收不到。
                Match pr = Regex.Match(dict, @"/Pages\s+(\d+)\s+\d+\s+R");
                if (pr.Success)
                {
                    int pagesRoot;
                    if (int.TryParse(pr.Groups[1].Value, out pagesRoot) &&
                        !CollectPages(pagesRoot, seen, depth + 1, out error)) return false;
                }

                Match km = KidsRx.Match(dict);
                if (!km.Success) return true;
                foreach (Match r in RefRx.Matches(km.Groups[1].Value))
                {
                    int child;
                    if (int.TryParse(r.Groups[1].Value, out child) && !CollectPages(child, seen, depth + 1, out error)) return false;
                }
                return true;
            }

            /// <summary>页对象缺 /Resources 时，沿 /Parent 向上找，返回可直接插入字典的文本。</summary>
            public string FindInheritedResources(int pageNum)
            {
                HashSet<int> seen = new HashSet<int>();
                int cur = pageNum;
                for (int i = 0; i < 32; i++)
                {
                    string body;
                    if (!Bodies.TryGetValue(cur, out body) || !seen.Add(cur)) return "";
                    string dict = DictPart(body);
                    int r = dict.IndexOf("/Resources", StringComparison.Ordinal);
                    if (r >= 0)
                    {
                        string tail = dict.Substring(r + "/Resources".Length);
                        Match rm = Regex.Match(tail, @"^\s*(\d+\s+\d+\s+R)");
                        if (rm.Success) return rm.Groups[1].Value;
                        int s = tail.IndexOf("<<", StringComparison.Ordinal);
                        if (s >= 0)
                        {
                            int lvl = 0, e = -1;
                            for (int k = s; k < tail.Length - 1; k++)
                            {
                                if (tail[k] == '<' && tail[k + 1] == '<') { lvl++; k++; }
                                else if (tail[k] == '>' && tail[k + 1] == '>') { lvl--; k++; if (lvl == 0) { e = k + 1; break; } }
                            }
                            if (e > s) return tail.Substring(s, e - s);
                        }
                        return "";
                    }
                    Match pm = Regex.Match(dict, @"/Parent\s+(\d+)\s+\d+\s+R");
                    if (!pm.Success) return "";
                    int parent;
                    if (!int.TryParse(pm.Groups[1].Value, out parent)) return "";
                    cur = parent;
                }
                return "";
            }
        }

        private static bool Starts(string s, int p, string v)
        {
            if (p < 0 || p + v.Length > s.Length) return false;
            return string.CompareOrdinal(s, p, v, 0, v.Length) == 0;
        }

        /// <summary>按 PDF 允许的三种行结束符读一行，pos 移到下一行开头。</summary>
        private static string ReadLine(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && s[pos] != '\r' && s[pos] != '\n') pos++;
            string line = s.Substring(start, pos - start);
            if (pos < s.Length && s[pos] == '\r' && pos + 1 < s.Length && s[pos + 1] == '\n') pos += 2;
            else if (pos < s.Length) pos++;
            return line;
        }
    }
}
