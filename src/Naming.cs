using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MPlot
{
    /// <summary>
    /// 文件命名模板。
    /// 默认「图号_图名_图框大小」：图框大小取纸张代号（A1/A2/A3…）。
    /// 老模板 A_C_B（图号_图名_版次）/ A_C_T 仍完全兼容，用户口径自由。
    /// 符号 → 字段的映射放在「其它」页可改（GlobalConfig.TokenMap），所以模板符号完全自定义。
    /// 另外支持 {A} 花括号写法，用来表达「图号-图名(版次)」这类非下划线的复杂模板。
    /// </summary>
    public static class Naming
    {
        /// <summary>默认模板常量，集中一处避免各处字面量漂移。图框大小用花括号按字段名取值。</summary>
        public const string DefaultTemplate = "{图号}_{图名}_{图框大小}";

        public static readonly string[] FieldNames = {
            "图号", "图名", "版次", "图框大小", "序号", "日期", "项目前缀", "块名", "布局名", "优先级"
        };

        private static readonly Regex BraceToken = new Regex(@"\{([^{}]+)\}", RegexOptions.Compiled);

        public static string BuildFileName(SheetItem s, GlobalConfig cfg, int index)
        {
            return BuildFileName(s, cfg, index, s == null ? "" : s.Config == null ? "" : s.Config.NameTemplate);
        }

        public static string BuildFileName(SheetItem s, GlobalConfig cfg, int index, string template)
        {
            if (string.IsNullOrWhiteSpace(template)) template = DefaultTemplate;
            Dictionary<string, string> map = cfg.TokenMap ?? new Dictionary<string, string>();
            string result = template;

            // 1) 花括号语法 {A}_{C}_{B} 或 {A}-{C}({B})
            result = BraceToken.Replace(result, m => FieldValue(m.Groups[1].Value, s, cfg, index) ?? "");

            // 2) 裸符号语法：按 "_" 切段，整段是符号就替换，否则当字面量保留
            string[] parts = result.Split('_');
            List<string> kept = new List<string>();
            foreach (string p0 in parts)
            {
                string p = p0.Trim();
                if (p.Length == 0) continue;
                string field;
                if (map.TryGetValue(p, out field))
                {
                    string v = FieldValue(field, s, cfg, index);
                    if (!string.IsNullOrWhiteSpace(v)) kept.Add(AcadUtil.SafeFileName(v));
                    // 空值整段丢弃，避免出现 "图号__版次" 或尾随下划线
                }
                else
                {
                    kept.Add(AcadUtil.SafeFileName(p));
                }
            }
            result = string.Join("_", kept.Where(x => x.Length > 0).ToArray());
            result = Regex.Replace(result, "_+", "_").Trim('_', '-', ' ', '.');

            if (string.IsNullOrWhiteSpace(result))
            {
                string fallback = s == null ? "" : (string.IsNullOrEmpty(s.LayoutName) ? "" : s.LayoutName);
                if (string.IsNullOrEmpty(fallback)) fallback = "图纸";
                result = fallback + "_" + index.ToString("00");
            }

            result = AcadUtil.SafeFileName(result);
            if (result.Length > 110) result = result.Substring(0, 110).TrimEnd('_', '-', ' ');
            return result.Length == 0 ? "图纸_" + index.ToString("00") : result;
        }

        /// <summary>字段名兼容中英文别名，方便 TokenMap 里自由填。</summary>
        private static string FieldValue(string field, SheetItem s, GlobalConfig cfg, int index)
        {
            if (string.IsNullOrWhiteSpace(field)) return "";
            string f = field.Trim().ToLowerInvariant();
            switch (f)
            {
                case "图号": case "a": case "drawno": case "draw_no": case "number":
                    return s == null ? "" : s.DrawNo ?? "";
                case "图名": case "c": case "title": case "name":
                    return s == null ? "" : s.Title ?? "";
                case "版次": case "b": case "t": case "rev": case "revision":
                    return s == null ? "" : s.Rev ?? "";
                case "图框大小": case "纸张": case "幅面": case "size": case "paper":
                    return s == null || s.Config == null || s.Config.Paper == null
                        ? "" : (s.Config.Paper.Preset ?? "");
                case "序号": case "n": case "index": case "no":
                    return index.ToString("00");
                case "日期": case "d": case "date":
                    return DateTime.Now.ToString(string.IsNullOrWhiteSpace(cfg.DateFormat) ? "yyyyMMdd" : cfg.DateFormat);
                case "项目前缀": case "p": case "project": case "prefix":
                    return cfg.ProjectPrefix ?? "";
                case "块名": case "block": case "blockname":
                    return s == null ? "" : s.BlockName ?? "";
                case "布局名": case "layout": case "layoutname":
                    return s == null ? "" : s.LayoutName ?? "";
                case "优先级": case "priority":
                    return s == null || s.Config == null ? "" : s.Config.Priority.ToString();
                default:
                    return "";   // 未知字段按空处理，不让配置文件里的笔误污染文件名
            }
        }

        /// <summary>给「录入新图框」窗口做模板实时预览。</summary>
        public static string Preview(string template, GlobalConfig cfg)
        {
            SheetItem demo = new SheetItem
            {
                DrawNo = "JZ-01", Title = "一层平面图", Rev = "A",
                BlockName = "A1图框", LayoutName = "模型",
                Config = new FrameEntry { Priority = 10, Paper = PaperPresets.Get("A1") }
            };
            return BuildFileName(demo, cfg, 1, template) + ".pdf";
        }

        /// <summary>校验模板里是否有无法识别的裸符号（给 UI 提示，不阻断）。</summary>
        public static string ValidateTemplate(string template, GlobalConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(template)) return "模板为空，将回落到默认模板（图号_图名_图框大小）";
            Dictionary<string, string> map = cfg.TokenMap ?? new Dictionary<string, string>();
            string bare = BraceToken.Replace(template, "");
            foreach (string p in bare.Split('_'))
            {
                string t = p.Trim();
                if (t.Length == 0 || t.Length > 2) continue;      // 长串当字面量
                if (!map.ContainsKey(t)) return "符号 " + t + " 未在「其它 → 命名符号映射」里定义，会被当成普通文字";
            }
            return "";
        }
    }
}
