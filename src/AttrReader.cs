using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace MPlot
{
    public sealed class AttrInfo
    {
        public string Tag { get; set; }
        public string Value { get; set; }
        public Point3d Position { get; set; }
        public bool FromDefinition { get; set; }
        /// <summary>当前可见性状态下是否隐藏（Invisible=1）。隐藏属性图面上不显示，显示模式下不读。</summary>
        public bool Invisible { get; set; }
        /// <summary>值来自动态块实时参数（显示值），不是 ATTRIB 存储值。</summary>
        public bool FromDynamic { get; set; }
    }

    public sealed class TagGuess
    {
        public string TagDrawNo { get; set; }
        public string TagTitle { get; set; }
        public string TagRev { get; set; }
        public double Confidence { get; set; }
        public List<AttrInfo> All { get; set; }
    }

    public sealed class SheetMeta
    {
        public string DrawNo { get; set; }
        public string Title { get; set; }
        public string Rev { get; set; }
        public string Source { get; set; }   // 属性 / 标题栏文字 / 空
        /// <summary>图号 / 图名 / 版次文字在图中的实际位置（WCS），供「自动定位锚点」学习用；读不到时为 null。</summary>
        public Point3d? DrawNoPos { get; set; }
        public Point3d? TitlePos { get; set; }
        public Point3d? RevPos { get; set; }
    }

    /// <summary>
    /// 图框属性读取 + Tag 自动识别。
    /// 面对的现实：天正图框里 Tag 可能是「图号」「TNR」「DRAWING_NO」「图纸编号」甚至纯拼音，
    /// 还有大量老图根本没有块属性、图号图名是标题栏里散着的单行文字/天正图名标注对象。
    /// 所以这里做三层：精确 Tag → 启发式猜 Tag → 标题栏区域文字兜底。
    ///
    /// 动态块特别处理（显示模式，默认）：天正动态图框切换可见性后，图面显示的
    /// 经常不是 ATTRIB 存储值——AttributeReference.TextString 读到的是底层存储值，
    /// 同图框换个拾取点（落在不同子块上）结果还不一样。所以显示模式下：
    /// ① 优先读动态块实时参数（DynamicBlockReferencePropertyCollection）覆盖同名 Tag；
    /// ② 只读顶层块引用自身的属性，不读嵌套子块的属性；
    /// ③ 过滤隐藏属性（Invisible=1）；④ 同 TAG 多条时按策略只留一条。
    /// 原始模式（AttrReadMode.RawAttribute）则只读 ATTRIB 存储值，不过滤、不覆盖。
    /// </summary>
    public static class AttrReader
    {
        private static readonly string[] DrawNoKeys = {
            "图号","图纸编号","图别","编号","工程编号","图 号","图号:","图幅编号",
            "dwgno","drawingno","drawing_no","drawno","drawingnumber","sheetno","sheet_no",
            "no","num","number","tnr","dh","tzh"
        };
        private static readonly string[] TitleKeys = {
            "图名","图纸名称","名称","标题","工程名称","项目名称","图 名","图名:","设计内容",
            "title","name","dwgname","drawingtitle","drawing_name","subject","tm"
        };
        private static readonly string[] RevKeys = {
            "版次","版本","修订","修改版","修改","版本号","版 次","第几版",
            "rev","revision","ver","version","edition","ed","date","日期","出图日期"
        };

        private static readonly Regex Cjk = new Regex(@"[\u4e00-\u9fa5]", RegexOptions.Compiled);
        private static readonly Regex CodeLike = new Regex(@"^[A-Za-z0-9\-_.()（）/+#\s]{2,32}$", RegexOptions.Compiled);
        private static readonly Regex MTextCodes = new Regex(@"\\[A-Za-z][^;]*;|[{]|[}]|\\P|\\~", RegexOptions.Compiled);

        // ---------------------------------------------------------------- 列出属性
        /// <summary>块参照上的属性 + 块定义里的属性定义（属性值可能为空，但 Tag 要让用户看到）。</summary>
        public static List<AttrInfo> ListAttributes(BlockReference br, Transaction tr)
        {
            return ListAttributes(br, tr, null);
        }

        /// <summary>
        /// 块参照上的属性 + 块定义里的属性定义。只读顶层块引用自身的属性，不递归子块 / 嵌套块。
        /// 显示模式（默认）：① 动态块实时参数覆盖同名 Tag；② 过滤隐藏属性（Invisible=1）；
        /// ③ 同 TAG 多条时按策略只留一条。原始模式：不过滤、不覆盖，只做同 TAG 去重。
        /// frameWcs 供同 TAG 去重时判断「是否落在标题栏区域」用，可为空。
        /// </summary>
        public static List<AttrInfo> ListAttributes(BlockReference br, Transaction tr, Extents2d? frameWcs)
        {
            List<AttrInfo> list = new List<AttrInfo>();
            if (br == null || tr == null) return list;
            bool displayMode = GlobalConfig.Current.AttrReadMode == AttrReadMode.DisplayText;

            try
            {
                AttributeCollection col = br.AttributeCollection;
                if (col != null)
                {
                    foreach (ObjectId id in col)
                    {
                        AttributeReference ar = tr.GetObject(id, OpenMode.ForRead) as AttributeReference;
                        if (ar == null) continue;
                        bool invisible = false;
                        try { invisible = ar.Invisible; } catch { }
                        if (displayMode && invisible) continue;   // 隐藏属性当前不显示，不读
                        list.Add(new AttrInfo
                        {
                            Tag = ar.Tag ?? "",
                            Value = CleanText(ar.TextString),
                            Position = ar.Position,
                            FromDefinition = false,
                            Invisible = invisible
                        });
                    }
                }
            }
            catch (Exception ex) { Logger.Verbose("读取块属性失败：" + ex.Message); }

            // 动态块实时参数：同名 Tag 时覆盖属性值；无同名属性时作为一条新属性加入
            if (displayMode)
            {
                try
                {
                    Dictionary<string, string> dyn = ReadDynamicProperties(br);
                    foreach (KeyValuePair<string, string> kv in dyn)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                        AttrInfo hit = null;
                        foreach (AttrInfo a in list)
                            if (string.Equals(a.Tag, kv.Key, StringComparison.OrdinalIgnoreCase)) { hit = a; break; }
                        if (hit != null)
                        {
                            hit.Value = kv.Value;
                            hit.FromDynamic = true;
                        }
                        else
                        {
                            list.Add(new AttrInfo
                            {
                                Tag = kv.Key, Value = kv.Value,
                                Position = br.Position, FromDefinition = false, FromDynamic = true
                            });
                        }
                    }
                }
                catch (Exception ex) { Logger.Verbose("动态参数合并失败：" + ex.Message); }
            }

            try
            {
                ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                if (!defId.IsNull)
                {
                    BlockTableRecord def = tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
                    if (def != null && def.HasAttributeDefinitions)
                    {
                        foreach (ObjectId id in def)
                        {
                            AttributeDefinition ad = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                            if (ad == null) continue;
                            string tag = ad.Tag ?? "";
                            if (list.Any(a => string.Equals(a.Tag, tag, StringComparison.OrdinalIgnoreCase))) continue;
                            list.Add(new AttrInfo
                            {
                                Tag = tag,
                                Value = CleanText(ad.TextString),
                                Position = ad.Position,
                                FromDefinition = true
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Verbose("读取块属性定义失败：" + ex.Message); }
            return DedupeAttributes(list, frameWcs);
        }

        /// <summary>
        /// 动态块实时生效参数（GetDynamicBlockProperties 的 .NET 等价物）。
        /// 天正动态图框切换可见性后，图面显示的是动态参数值，不是 ATTRIB 原始存储值，
        /// 所以显示模式下先读这里：参数名与 Tag 同名时，动态值优先。
        /// 只读顶层块引用自身的动态参数，不进子块。非动态块返回空表。
        /// </summary>
        public static Dictionary<string, string> ReadDynamicProperties(BlockReference br)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (br == null || !br.IsDynamicBlock) return map;
            try
            {
                DynamicBlockReferencePropertyCollection props = br.DynamicBlockReferencePropertyCollection;
                if (props == null) return map;
                foreach (DynamicBlockReferenceProperty p in props)
                {
                    if (p == null) continue;
                    string name = (p.PropertyName ?? "").Trim();
                    if (name.Length == 0) continue;
                    object v = null;
                    try { v = p.Value; } catch { }
                    string s = "";
                    if (v != null)
                    {
                        if (v is double) s = ((double)v).ToString("0.####", CultureInfo.InvariantCulture);
                        else if (v is float) s = ((float)v).ToString("0.####", CultureInfo.InvariantCulture);
                        else s = Convert.ToString(v);
                    }
                    if (!map.ContainsKey(name)) map[name] = CleanText(s);
                }
            }
            catch (Exception ex) { Logger.Verbose("读取动态块参数失败：" + ex.Message); }
            return map;
        }

        /// <summary>
        /// 同 TAG 多属性去重（动态块里一个 Tag 常对应多个 ATTRIB，取第一条是随机的）。
        /// 策略按优先级：① 可见的（Invisible=0，当前可见性状态下真正显示的）；
        /// ② 值非空的；③ 来自动态参数的（显示值）；④ 位置落在标题栏区域的；
        /// ⑤ 仍并列时取集合里第一条。被丢掉的记 Verbose 日志，方便对账。
        /// </summary>
        private static List<AttrInfo> DedupeAttributes(List<AttrInfo> list, Extents2d? frameWcs)
        {
            Dictionary<string, List<AttrInfo>> groups = new Dictionary<string, List<AttrInfo>>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();
            foreach (AttrInfo a in list)
            {
                string key = (a.Tag ?? "").Trim();
                if (key.Length == 0) continue;
                if (!groups.ContainsKey(key)) { groups[key] = new List<AttrInfo>(); order.Add(key); }
                groups[key].Add(a);
            }
            List<AttrInfo> result = new List<AttrInfo>();
            foreach (string key in order)
            {
                List<AttrInfo> g = groups[key];
                if (g.Count == 1) { result.Add(g[0]); continue; }
                AttrInfo best = g[0];
                int bestScore = ScoreAttr(best, frameWcs);
                for (int i = 1; i < g.Count; i++)
                {
                    int s = ScoreAttr(g[i], frameWcs);
                    if (s > bestScore) { bestScore = s; best = g[i]; }
                }
                Logger.Verbose("同 TAG 去重：" + key + " 共 " + g.Count + " 条，取「" + best.Value + "」"
                    + (best.FromDynamic ? "（动态参数）" : (best.FromDefinition ? "（定义）" : "（属性）")));
                result.Add(best);
            }
            return result;
        }

        private static int ScoreAttr(AttrInfo a, Extents2d? frame)
        {
            int s = 0;
            if (!a.Invisible) s += 8;
            if (!string.IsNullOrWhiteSpace(a.Value)) s += 4;
            if (a.FromDynamic) s += 2;
            if (frame.HasValue && InTitleBar(a.Position, frame.Value)) s += 1;
            if (a.FromDefinition) s -= 2;   // 只有定义、没有实例值时垫底
            return s;
        }

        public static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = MTextCodes.Replace(s, " ");
            s = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Trim().Trim('_', '-', ' ');
        }

        // ---------------------------------------------------------------- 自动识别 Tag
        /// <summary>框选录入的核心：扫一遍该图框块的所有属性，猜出哪个是图号 / 图名 / 版次。</summary>
        public static TagGuess GuessTags(List<AttrInfo> attrs, Extents2d? frameWcs)
        {
            TagGuess g = new TagGuess { All = attrs, Confidence = 0, TagDrawNo = "", TagTitle = "", TagRev = "" };
            if (attrs == null || attrs.Count == 0) return g;

            // 候选：(类别, 属性, 分数)
            List<Tuple<int, AttrInfo, double>> cand = new List<Tuple<int, AttrInfo, double>>();
            foreach (AttrInfo a in attrs)
            {
                cand.Add(new Tuple<int, AttrInfo, double>(0, a, ScoreTag(a, DrawNoKeys, frameWcs) + ScoreValueAsDrawNo(a.Value)));
                cand.Add(new Tuple<int, AttrInfo, double>(1, a, ScoreTag(a, TitleKeys, frameWcs) + ScoreValueAsTitle(a.Value)));
                cand.Add(new Tuple<int, AttrInfo, double>(2, a, ScoreTag(a, RevKeys, frameWcs) + ScoreValueAsRev(a.Value)));
            }

            bool[] catUsed = new bool[3];
            HashSet<string> tagUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double total = 0;
            foreach (var c in cand.OrderByDescending(x => x.Item3))
            {
                if (c.Item3 < 5) break;                       // 低于阈值宁可留空让人填
                if (catUsed[c.Item1] || tagUsed.Contains(c.Item2.Tag)) continue;
                catUsed[c.Item1] = true;
                tagUsed.Add(c.Item2.Tag);
                total += c.Item3;
                if (c.Item1 == 0) g.TagDrawNo = c.Item2.Tag;
                else if (c.Item1 == 1) g.TagTitle = c.Item2.Tag;
                else g.TagRev = c.Item2.Tag;
            }
            g.Confidence = total;
            return g;
        }

        private static double ScoreTag(AttrInfo a, string[] keys, Extents2d? frame)
        {
            string tag = (a.Tag ?? "").Replace(" ", "").Replace("_", "").ToLowerInvariant();
            if (tag.Length == 0) return 0;
            double s = 0;
            foreach (string k in keys)
            {
                string kk = k.Replace(" ", "").Replace("_", "").ToLowerInvariant();
                if (tag == kk) { s = Math.Max(s, 12); break; }
                if (tag.Contains(kk)) s = Math.Max(s, kk.Length <= 2 ? 5 : 8);
            }
            // 标题栏里的属性更可能是这三个字段
            if (frame.HasValue && InTitleBar(a.Position, frame.Value)) s += 1.5;
            return s;
        }

        private static bool InTitleBar(Point3d p, Extents2d f)
        {
            double w = f.MaxPoint.X - f.MinPoint.X, h = f.MaxPoint.Y - f.MinPoint.Y;
            if (w <= 0 || h <= 0) return false;
            double rx = (p.X - f.MinPoint.X) / w, ry = (p.Y - f.MinPoint.Y) / h;
            return rx > 0.45 && ry < 0.35;
        }

        private static double ScoreValueAsDrawNo(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            double cn = Cjk.Matches(v).Count;
            if (cn >= 2) return -4;                        // 中文多 → 更像图名
            if (!CodeLike.IsMatch(v)) return 0;
            double s = 6;
            if (v.Length >= 4 && v.Length <= 18) s += 2;
            if (Regex.IsMatch(v, @"\d")) s += 1;
            return s;
        }

        private static double ScoreValueAsTitle(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            int cn = Cjk.Matches(v).Count;
            double s = cn >= 2 ? 6 : 0;
            if (v.Length >= 2 && v.Length <= 40) s += 2;
            if (!Regex.IsMatch(v, @"^\d+$")) s += 1;
            if (cn == 0 && v.Length > 20) s -= 2;
            return s;
        }

        private static double ScoreValueAsRev(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            string t = v.Trim();
            if (t.Length > 12) return -2;
            if (Regex.IsMatch(t, @"^[A-Za-z0-9]{1,3}$")) return 7;
            if (Regex.IsMatch(t, @"^第?[0-9一二三四五六七八九十]+版?$")) return 6;
            if (Regex.IsMatch(t, @"^\d{4}[-/.]\d{1,2}[-/.]\d{1,2}$")) return 4;
            if (Regex.IsMatch(t, @"^[A-Z]?\d{1,2}$")) return 5;
            return 0;
        }

        // ---------------------------------------------------------------- 按配置读值
        public static SheetMeta Read(BlockReference br, Transaction tr, FrameEntry cfg,
                                     Extents2d? frameWcs, bool allowTextFallback)
        {
            SheetMeta m = new SheetMeta { DrawNo = "", Title = "", Rev = "", Source = "" };
            List<AttrInfo> attrs = ListAttributes(br, tr, frameWcs);
            TagGuess guess = null;
            if (string.IsNullOrWhiteSpace(cfg.TagDrawNo) || string.IsNullOrWhiteSpace(cfg.TagTitle) || string.IsNullOrWhiteSpace(cfg.TagRev))
                guess = GuessTags(attrs, frameWcs);

            AttrInfo dn = cfg.ReadDrawNo ? PickInfo(attrs, cfg.TagDrawNo, guess == null ? "" : guess.TagDrawNo) : null;
            AttrInfo tt = cfg.ReadTitle ? PickInfo(attrs, cfg.TagTitle, guess == null ? "" : guess.TagTitle) : null;
            AttrInfo rv = cfg.ReadRev ? PickInfo(attrs, cfg.TagRev, guess == null ? "" : guess.TagRev) : null;
            m.DrawNo = dn == null ? "" : dn.Value;
            m.Title = tt == null ? "" : tt.Value;
            m.Rev = rv == null ? "" : rv.Value;
            // 位置只记实例属性的（定义里的 Position 是块定义局部坐标，不能当 WCS 锚点）
            if (dn != null && !dn.FromDefinition) m.DrawNoPos = dn.Position;
            if (tt != null && !tt.FromDefinition) m.TitlePos = tt.Position;
            if (rv != null && !rv.FromDefinition) m.RevPos = rv.Position;
            if (!string.IsNullOrEmpty(m.DrawNo) || !string.IsNullOrEmpty(m.Title))
                m.Source = ((dn != null && dn.FromDynamic) || (tt != null && tt.FromDynamic) || (rv != null && rv.FromDynamic))
                    ? "动态块显示值" : "块属性";

            bool empty = string.IsNullOrEmpty(m.DrawNo) && string.IsNullOrEmpty(m.Title);
            if (empty && allowTextFallback && frameWcs.HasValue)
            {
                SheetMeta fromText = ReadTitleBarTexts(frameWcs.Value, cfg);
                if (!string.IsNullOrEmpty(fromText.DrawNo) || !string.IsNullOrEmpty(fromText.Title))
                {
                    m = fromText;
                    m.Source = "标题栏文字";
                }
            }
            return m;
        }

        /// <summary>Tag 配置支持 "图号|DRAWING_NO|A" 多候选、忽略大小写、支持 * 通配。</summary>
        private static string Pick(List<AttrInfo> attrs, string configured, string guessed)
        {
            AttrInfo a = PickInfo(attrs, configured, guessed);
            return a == null ? "" : a.Value;
        }

        /// <summary>Pick 的取条目版：返回命中的 AttrInfo（带 FromDynamic / Invisible 标记），没命中返回 null。</summary>
        private static AttrInfo PickInfo(List<AttrInfo> attrs, string configured, string guessed)
        {
            string[] keys = (configured ?? "").Split(new[] { '|', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> all = new List<string>(keys);
            if (!string.IsNullOrEmpty(guessed)) all.Add(guessed);

            foreach (string k0 in all)
            {
                string k = k0.Trim();
                if (k.Length == 0) continue;
                foreach (AttrInfo a in attrs)
                {
                    bool hit = k.Contains("*")
                        ? MatchWild(a.Tag, k)
                        : string.Equals(a.Tag, k, StringComparison.OrdinalIgnoreCase);
                    if (hit && !string.IsNullOrWhiteSpace(a.Value)) return a;   // 值进列表时已 CleanText 过，无需再 Trim
                }
            }
            // 配置为空且没猜到时，最后按值形态捞一次
            return null;
        }

        private static bool MatchWild(string s, string pattern)
        {
            string re = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(s ?? "", re, RegexOptions.IgnoreCase);
        }

        // ---------------------------------------------------------------- 标题栏文字兜底
        /// <summary>
        /// 图框不是块属性（天正「图名标注」、 exploded 图框、纯文字标题栏）时，
        /// 在图框右下标题栏区域做一次交叉窗口选择，按位置和字形猜图号/图名/版次。
        /// </summary>
        public static SheetMeta ReadTitleBarTexts(Extents2d frame, FrameEntry cfg)
        {
            SheetMeta m = new SheetMeta { DrawNo = "", Title = "", Rev = "", Source = "" };
            Editor ed = AcadUtil.Ed;
            Database db = AcadUtil.Db;
            if (ed == null || db == null) return m;

            double w = frame.MaxPoint.X - frame.MinPoint.X;
            double h = frame.MaxPoint.Y - frame.MinPoint.Y;
            if (w <= 0 || h <= 0) return m;

            string hint = (cfg.TitleBarHint ?? "auto").ToLowerInvariant();
            double x0 = frame.MaxPoint.X - w * 0.55, x1 = frame.MaxPoint.X + w * 0.01;
            double y0 = frame.MinPoint.Y - h * 0.01, y1 = frame.MinPoint.Y + h * 0.30;
            if (hint.Contains("left")) { x0 = frame.MinPoint.X - w * 0.01; x1 = frame.MinPoint.X + w * 0.55; }
            if (hint.Contains("top")) { y0 = frame.MaxPoint.Y - h * 0.30; y1 = frame.MaxPoint.Y + h * 0.01; }

            List<Tuple<string, Point3d>> texts = new List<Tuple<string, Point3d>>();
            try
            {
                Point3d p1 = new Point3d(x0, y0, 0), p2 = new Point3d(x1, y1, 0);
                TypedValue[] filter = { new TypedValue((int)DxfCode.Start, "TEXT,MTEXT") };
                PromptSelectionResult r = ed.SelectCrossingWindow(p1, p2, new SelectionFilter(filter));
                if (r.Status == PromptStatus.OK && r.Value != null)
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        foreach (SelectedObject so in r.Value)
                        {
                            Entity en = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                            if (en == null) continue;
                            string s = GetAnyText(en);
                            if (string.IsNullOrWhiteSpace(s)) continue;
                            Extents3d? ex = AcadUtil.TryExtents(en);
                            Point3d pos = ex.HasValue
                                ? new Point3d((ex.Value.MinPoint.X + ex.Value.MaxPoint.X) / 2,
                                              (ex.Value.MinPoint.Y + ex.Value.MaxPoint.Y) / 2, 0)
                                : Point3d.Origin;
                            texts.Add(new Tuple<string, Point3d>(CleanText(s), pos));
                        }
                        tr.Commit();
                    }
                }
            }
            catch (Exception ex) { Logger.Verbose("标题栏文字扫描失败：" + ex.Message); }

            // 天正自定义文字对象不认 TEXT/MTEXT 过滤器时，反射补一次（只在小区域内）
            if (texts.Count == 0)
            {
                try
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord btr = tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                        if (btr != null)
                        {
                            foreach (ObjectId id in btr)
                            {
                                Entity en = tr.GetObject(id, OpenMode.ForRead) as Entity;
                                if (en == null || en is DBText || en is MText) continue;
                                string cls = en.GetType().Name ?? "";
                                if (cls.IndexOf("Text", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    cls.IndexOf("TCH", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                Extents3d? ex = AcadUtil.TryExtents(en);
                                if (!ex.HasValue) continue;
                                Point3d c = new Point3d((ex.Value.MinPoint.X + ex.Value.MaxPoint.X) / 2,
                                                        (ex.Value.MinPoint.Y + ex.Value.MaxPoint.Y) / 2, 0);
                                if (c.X < x0 || c.X > x1 || c.Y < y0 || c.Y > y1) continue;
                                string s = GetAnyText(en);
                                if (!string.IsNullOrWhiteSpace(s)) texts.Add(new Tuple<string, Point3d>(CleanText(s), c));
                                if (texts.Count > 60) break;
                            }
                        }
                        tr.Commit();
                    }
                }
                catch (Exception ex) { Logger.Verbose("天正文字对象扫描失败：" + ex.Message); }
            }

            // 按「从右到左、从上到下」排，标题栏里图名通常在最显眼的一行
            // 胜出的文字同时记下中心点，供「自动定位锚点」学习用
            double bestTitle = -99, bestNo = -99, bestRev = -99;
            Point3d bestTitlePos = Point3d.Origin, bestNoPos = Point3d.Origin, bestRevPos = Point3d.Origin;
            foreach (var t in texts)
            {
                string s = t.Item1;
                if (s.Length == 0 || s.Length > 60) continue;
                double st = ScoreValueAsTitle(s) + (s.Length >= 4 ? 1 : 0);
                if (st > bestTitle) { bestTitle = st; m.Title = s; bestTitlePos = t.Item2; }
                double sn = ScoreValueAsDrawNo(s) + (Cjk.Matches(s).Count == 0 ? 1 : 0);
                if (sn > bestNo && s != m.Title) { bestNo = sn; m.DrawNo = s; bestNoPos = t.Item2; }
                double sr = ScoreValueAsRev(s);
                if (sr > bestRev && s != m.Title && s != m.DrawNo) { bestRev = sr; m.Rev = s; bestRevPos = t.Item2; }
            }
            if (bestTitle < 5) m.Title = "";
            else m.TitlePos = bestTitlePos;
            if (bestNo < 5) m.DrawNo = "";
            else m.DrawNoPos = bestNoPos;
            if (bestRev < 5) m.Rev = "";
            else m.RevPos = bestRevPos;
            return m;
        }

        /// <summary>
        /// 「点取图号/图名范围」的核心：读给定 WCS 矩形内的文字，按 prefer 挑出最像的一个值。
        /// prefer: drawno / title / rev / auto。同时覆盖普通 TEXT/MTEXT 与天正自定义文字对象。
        /// 返回清洗后的单个值，读不到返回空串。
        /// </summary>
        public static string ReadRegionValue(Extents2d region, string prefer)
        {
            List<Tuple<string, Point3d>> texts = ScanTextsInRect(
                region.MinPoint.X, region.MinPoint.Y, region.MaxPoint.X, region.MaxPoint.Y);
            if (texts.Count == 0) return "";

            string p = (prefer ?? "auto").ToLowerInvariant();
            string best = ""; double bestScore = -999;
            foreach (var t in texts)
            {
                string s = t.Item1;
                if (string.IsNullOrEmpty(s) || s.Length > 60) continue;
                double score;
                if (p == "drawno") score = ScoreValueAsDrawNo(s) + (Cjk.Matches(s).Count == 0 ? 2 : 0);
                else if (p == "title") score = ScoreValueAsTitle(s) + (s.Length >= 4 ? 1 : 0);
                else if (p == "rev") score = ScoreValueAsRev(s);
                else score = Math.Max(ScoreValueAsDrawNo(s), ScoreValueAsTitle(s));  // auto
                if (score > bestScore) { bestScore = score; best = s; }
            }
            return bestScore < 3 ? "" : best;
        }

        /// <summary>
        /// 列出指定 WCS 矩形内的全部文字及中心点，供「点取范围后人工点选确认哪段是图号/图名」用。
        /// 返回列表按位置从上到下、从左到右排序，方便在候选窗里对号入座。
        /// </summary>
        public static List<Tuple<string, Point3d>> ListRegionTexts(Extents2d region)
        {
            List<Tuple<string, Point3d>> list = ScanTextsInRect(
                region.MinPoint.X, region.MinPoint.Y, region.MaxPoint.X, region.MaxPoint.Y);
            list.Sort(delegate(Tuple<string, Point3d> a, Tuple<string, Point3d> b)
            {
                double dy = b.Item2.Y - a.Item2.Y;
                if (Math.Abs(dy) > 1) return dy > 0 ? 1 : -1;   // 先按 Y 从上到下
                return a.Item2.X < b.Item2.X ? -1 : 1;          // 同一行按 X 从左到右
            });
            return list;
        }

        /// <summary>
        /// 按「人工点选确认过的锚点」读文字：在锚点附近找中心点最近的文字返回其值。
        /// 录入时用户点选了某段文字 → 存它的精确中心点；批打时每张图按同一相对位置找最近文字。
        /// 比打分猜更准，且不同图框各存各的位置，互不干扰。读不到返回空串。
        /// </summary>
        public static string ReadAtNearest(double x, double y, double tol)
        {
            if (tol <= 0) tol = 500;
            List<Tuple<string, Point3d>> texts = ScanTextsInRect(x - tol, y - tol, x + tol, y + tol);
            string best = ""; double bestDist = double.MaxValue;
            foreach (Tuple<string, Point3d> t in texts)
            {
                if (string.IsNullOrEmpty(t.Item1)) continue;
                double dx = t.Item2.X - x, dy = t.Item2.Y - y;
                double d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; best = t.Item1; }
            }
            return best;
        }

        // 扫描深度/数量上限：防止嵌套块或超大模型拖慢点取交互
        private const int MaxScanDepth = 6;
        private const int MaxScanTexts = 200;

        /// <summary>
        /// 扫描 WCS 矩形内的「所有文字」，返回 (文字, 中心点 WCS)。
        /// 关键修复：旧版用 SelectCrossingWindow("TEXT,MTEXT") 只能选中模型空间顶层单行/多行文字，
        /// 图框里的图号图名大量是【块属性 ATTRIB / 图块内嵌文字 / 天正自定义文字 / 标注文字】，全都读不到，
        /// 导致点取时报「框选区域里没有文字」。这里改为遍历 + 递归块定义 + 变换矩阵到 WCS 的统一扫描。
        /// </summary>
        private static List<Tuple<string, Point3d>> ScanTextsInRect(double x0, double y0, double x1, double y1)
        {
            List<Tuple<string, Point3d>> texts = new List<Tuple<string, Point3d>>();
            Database db = AcadUtil.Db;
            if (db == null) return texts;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    if (bt == null) { tr.Commit(); return texts; }

                    HashSet<ObjectId> visited = new HashSet<ObjectId>();
                    // 模型空间 + 所有布局空间都扫，保证在哪个空间点取都能读到
                    List<ObjectId> spaces = new List<ObjectId>();
                    if (bt.Has(BlockTableRecord.ModelSpace)) spaces.Add(bt[BlockTableRecord.ModelSpace]);
                    try
                    {
                        DBDictionary lays = tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
                        if (lays != null)
                            foreach (DBDictionaryEntry de in lays)
                            {
                                Layout lo = tr.GetObject(de.Value, OpenMode.ForRead) as Layout;
                                if (lo != null && !lo.ModelType && !lo.BlockTableRecordId.IsNull)
                                    spaces.Add(lo.BlockTableRecordId);
                            }
                    }
                    catch { }

                    foreach (ObjectId spaceId in spaces)
                    {
                        BlockTableRecord btr = tr.GetObject(spaceId, OpenMode.ForRead) as BlockTableRecord;
                        if (btr == null) continue;
                        foreach (ObjectId id in btr)
                        {
                            if (texts.Count >= MaxScanTexts) break;
                            Entity en = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (en == null) continue;
                            CollectTextsRec(en, tr, Matrix3d.Identity, 0, x0, y0, x1, y1, texts, visited);
                        }
                    }
                    tr.Commit();
                }
            }
            catch (Exception exx) { Logger.Verbose("区域文字扫描失败：" + exx.Message); }
            return texts;
        }

        /// <summary>
        /// 递归收集一个实体里可读取的文字。xform = 从该实体所在坐标系到 WCS 的累计变换。
        /// 覆盖：普通文字 / 多行文字 / 块属性 / 块内文字与嵌套块 / 天正自定义文字 / 标注文字。
        /// 只保留中心点落在目标矩形内的结果（点取哪段就列哪段）。
        /// </summary>
        private static void CollectTextsRec(Entity en, Transaction tr, Matrix3d xform, int depth,
                                            double x0, double y0, double x1, double y1,
                                            List<Tuple<string, Point3d>> texts, HashSet<ObjectId> visited)
        {
            if (en == null || depth > MaxScanDepth || texts.Count >= MaxScanTexts) return;

            // ---- 1) 块参照：先读块属性，再递归块定义里的文字 ----
            // 只读顶层块引用的属性：嵌套子块的属性不读（depth > 0 时跳过 1a），
            // 否则「同图框不同拾取点抓到不同子块的属性」。块定义里的普通文字仍继续扫（1b），
            // 那是图框自身的显示文字，不是嵌套块属性。
            BlockReference br = en as BlockReference;
            if (br != null)
            {
                // 1a) 块属性（ATTRIB）：图号图名最常见的存放方式。Position 已是 WCS，不用再变换。
                if (depth == 0)
                {
                    bool displayMode = GlobalConfig.Current.AttrReadMode == AttrReadMode.DisplayText;
                    try
                    {
                        AttributeCollection ac = br.AttributeCollection;
                        if (ac != null)
                            foreach (ObjectId aid in ac)
                            {
                                if (texts.Count >= MaxScanTexts) break;
                                AttributeReference ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                                if (ar == null) continue;
                                bool invisible = false;
                                try { invisible = ar.Invisible; } catch { }
                                if (displayMode && invisible) continue;   // 隐藏属性图面不显示
                                string s = CleanText(ar.TextString);
                                if (string.IsNullOrWhiteSpace(s)) continue;
                                Point3d pos = AttributeCenter(ar);
                                if (InRect(pos, x0, y0, x1, y1)) texts.Add(new Tuple<string, Point3d>(s, pos));
                            }
                    }
                    catch (Exception exa) { Logger.Verbose("块属性读取跳过：" + exa.Message); }
                }

                // 1b) 块定义里的内嵌文字：用块变换把局部坐标换算到 WCS
                try
                {
                    ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                    if (!defId.IsNull && !visited.Contains(defId))
                    {
                        visited.Add(defId);
                        BlockTableRecord def = tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
                        if (def != null)
                        {
                            Matrix3d next = Multiply(xform, br.BlockTransform);
                            foreach (ObjectId cid in def)
                            {
                                if (texts.Count >= MaxScanTexts) break;
                                Entity child = tr.GetObject(cid, OpenMode.ForRead) as Entity;
                                if (child == null) continue;
                                CollectTextsRec(child, tr, next, depth + 1, x0, y0, x1, y1, texts, visited);
                            }
                        }
                        visited.Remove(defId);   // 同一块定义被多处引用时都要扫（各实例变换不同）
                    }
                }
                catch (Exception exb) { Logger.Verbose("块内文字读取跳过：" + exb.Message); }
                return;
            }

            // ---- 2) 普通文字 / 多行文字 / 天正自定义文字 / 标注 ----
            string txt = GetAnyText(en);
            if (string.IsNullOrWhiteSpace(txt)) return;
            Extents3d? ex = AcadUtil.TryExtents(en);
            Point3d local;
            if (ex.HasValue)
                local = new Point3d((ex.Value.MinPoint.X + ex.Value.MaxPoint.X) / 2,
                                    (ex.Value.MinPoint.Y + ex.Value.MaxPoint.Y) / 2, ex.Value.MinPoint.Z);
            else
            {
                Point3d? p = TryEntityPosition(en);
                if (!p.HasValue) return;
                local = p.Value;
            }
            Point3d wcs = Transform(local, xform);
            if (InRect(wcs, x0, y0, x1, y1))
                texts.Add(new Tuple<string, Point3d>(CleanText(txt), wcs));
        }

        private static bool InRect(Point3d p, double x0, double y0, double x1, double y1)
        {
            return p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;
        }

        /// <summary>属性文字中心：优先几何包围盒，失败退回 Position，再乘块变换。</summary>
        private static Point3d AttributeCenter(AttributeReference ar)
        {
            try
            {
                Extents3d e = ar.GeometricExtents;
                if (!double.IsNaN(e.MinPoint.X) && !double.IsInfinity(e.MinPoint.X))
                    return new Point3d((e.MinPoint.X + e.MaxPoint.X) / 2,
                                       (e.MinPoint.Y + e.MaxPoint.Y) / 2, e.MinPoint.Z);
            }
            catch { }
            return ar.Position;
        }

        /// <summary>取实体可能的插入点（天正文字/标注反射用）。</summary>
        private static Point3d? TryEntityPosition(Entity en)
        {
            try
            {
                PropertyInfo p = en.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanRead && p.GetValue(en, null) is Point3d)
                    return (Point3d)p.GetValue(en, null);
            }
            catch { }
            return null;
        }

        /// <summary>矩阵串联：先应用 first，再应用 second（AutoCAD .NET 为左乘）。</summary>
        private static Matrix3d Multiply(Matrix3d first, Matrix3d second)
        {
            try { return first.PreMultiplyBy(second); }
            catch { return second; }
        }

        private static Point3d Transform(Point3d p, Matrix3d m)
        {
            try { return p.TransformBy(m); }
            catch { return p; }
        }

        /// <summary>
        /// 统一取文字：DBText / MText / AttributeDefinition / 标注 / 天正自定义对象（反射兜底）。
        /// 反射覆盖常见属性名，兼容不同版本天正（TCH_TEXT / TCTEXT 等）。
        /// </summary>
        public static string GetAnyText(Entity en)
        {
            if (en == null) return "";
            DBText dt = en as DBText;
            if (dt != null) return dt.TextString;
            MText mt = en as MText;
            if (mt != null) return mt.Text;
            AttributeDefinition ad = en as AttributeDefinition;
            if (ad != null) return ad.TextString;
            // 标注（Dimension）的替换文字：2014 没有公开的 TextOverride 属性，全部走反射兜底
            if (en is Dimension)
            {
                try
                {
                    string[] dn = { "TextOverride", "DimensionText", "Text" };
                    foreach (string nm in dn)
                    {
                        PropertyInfo dp = en.GetType().GetProperty(nm, BindingFlags.Public | BindingFlags.Instance);
                        if (dp != null && dp.CanRead)
                        {
                            string s = Convert.ToString(dp.GetValue(en, null));
                            if (!string.IsNullOrWhiteSpace(s)) return s.Replace("<>", "");
                        }
                    }
                }
                catch { }
            }
            try
            {
                string[] names = { "TextString", "Text", "TextValue", "Content", "Caption" };
                foreach (string nm in names)
                {
                    PropertyInfo p = en.GetType().GetProperty(nm, BindingFlags.Public | BindingFlags.Instance);
                    if (p != null && p.CanRead)
                    {
                        object v = p.GetValue(en, null);
                        string s = Convert.ToString(v);
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch { }
            return "";
        }
    }
}
