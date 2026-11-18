using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace MPlot
{
    /// <summary>
    /// 零第三方依赖的极简 JSON 读写器。
    /// 只服务于「配置 / 图框库」这类小型强类型数据：支持缩进导出（人可读）、
    /// 缺字段容错（旧版本配置也能读）、POCO / List / Dictionary / Nullable / 枚举。
    /// 不实现流式解析、不处理循环引用 —— 配置数据里不会出现。
    /// 存在的理由：AutoCAD 插件多带一个 Newtonsoft.dll 就多一份版本冲突与部署负担。
    /// </summary>
    public static class MiniJson
    {
        // ---------------------------------------------------------------- 写入
        public static string Stringify(object obj)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(obj, sb, 0);
            return sb.ToString();
        }

        private static void WriteValue(object v, StringBuilder sb, int depth)
        {
            if (v == null) { sb.Append("null"); return; }

            Type t = v.GetType();

            if (v is string) { WriteString((string)v, sb); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is char) { WriteString(v.ToString(), sb); return; }
            if (v is DateTime) { WriteString(((DateTime)v).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), sb); return; }
            if (v is Enum) { WriteString(v.ToString(), sb); return; }
            if (v is double) { sb.Append(((double)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is float) { sb.Append(((float)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is decimal) { sb.Append(((decimal)v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is byte || v is sbyte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong)
            { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }

            if (v is IDictionary)
            {
                IDictionary d = (IDictionary)v;
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in d)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('\n').Append(Indent(depth + 1));
                    WriteString(Convert.ToString(e.Key, CultureInfo.InvariantCulture), sb);
                    sb.Append(": ");
                    WriteValue(e.Value, sb, depth + 1);
                }
                if (!first) sb.Append('\n').Append(Indent(depth));
                sb.Append('}');
                return;
            }

            if (v is IEnumerable)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in (IEnumerable)v)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('\n').Append(Indent(depth + 1));
                    WriteValue(item, sb, depth + 1);
                }
                if (!first) sb.Append('\n').Append(Indent(depth));
                sb.Append(']');
                return;
            }

            // POCO：只序列化可读的公共实例属性
            PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            sb.Append('{');
            bool f = true;
            foreach (PropertyInfo p in props)
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                object val;
                try { val = p.GetValue(v, null); }
                catch { continue; }
                if (!f) sb.Append(',');
                f = false;
                sb.Append('\n').Append(Indent(depth + 1));
                WriteString(p.Name, sb);
                sb.Append(": ");
                WriteValue(val, sb, depth + 1);
            }
            if (!f) sb.Append('\n').Append(Indent(depth));
            sb.Append('}');
        }

        private static string Indent(int depth) { return new string(' ', depth * 2); }

        private static void WriteString(string s, StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);   // 中文原样输出，文件按 UTF-8 写
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------------------------------------------------------- 解析
        public static object Parse(string json)
        {
            if (json == null) return null;
            Parser p = new Parser(json);
            object result = p.ReadValue();
            p.SkipWs();
            return result;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            internal Parser(string s) { _s = s; _i = 0; }

            internal void SkipWs()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
                // 容忍 UTF-8 BOM
                if (_i < _s.Length && _s[_i] == '\uFEFF') _i++;
            }

            internal object ReadValue()
            {
                SkipWs();
                if (_i >= _s.Length) return null;
                char c = _s[_i];
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return ReadString();
                if (c == 't' && Match("true")) return true;
                if (c == 'f' && Match("false")) return false;
                if (c == 'n' && Match("null")) return null;
                if (c == '-' || char.IsDigit(c)) return ReadNumber();
                throw new FormatException("JSON 解析失败，位置 " + _i + " 附近字符 '" + c + "'");
            }

            private bool Match(string word)
            {
                if (_i + word.Length > _s.Length) return false;
                for (int k = 0; k < word.Length; k++) if (_s[_i + k] != word[k]) return false;
                _i += word.Length;
                return true;
            }

            private Dictionary<string, object> ReadObject()
            {
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                _i++; // {
                SkipWs();
                if (_i < _s.Length && _s[_i] == '}') { _i++; return d; }
                while (_i < _s.Length)
                {
                    SkipWs();
                    if (_s[_i] != '"') throw new FormatException("JSON 对象键必须是字符串，位置 " + _i);
                    string key = ReadString();
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != ':') throw new FormatException("JSON 缺少 ':'，位置 " + _i);
                    _i++;
                    d[key] = ReadValue();
                    SkipWs();
                    if (_i >= _s.Length) break;
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; break; }
                    throw new FormatException("JSON 对象格式错误，位置 " + _i);
                }
                return d;
            }

            private List<object> ReadArray()
            {
                List<object> a = new List<object>();
                _i++; // [
                SkipWs();
                if (_i < _s.Length && _s[_i] == ']') { _i++; return a; }
                while (_i < _s.Length)
                {
                    a.Add(ReadValue());
                    SkipWs();
                    if (_i >= _s.Length) break;
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; break; }
                    throw new FormatException("JSON 数组格式错误，位置 " + _i);
                }
                return a;
            }

            private string ReadString()
            {
                StringBuilder sb = new StringBuilder();
                _i++; // opening quote
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (_i >= _s.Length) break;
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 <= _s.Length)
                            {
                                int code;
                                if (int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                { sb.Append((char)code); _i += 4; }
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                return sb.ToString();
            }

            private double ReadNumber()
            {
                int start = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '-' || _s[_i] == '+' || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E')) _i++;
                double n;
                double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out n);
                return n;
            }
        }

        // ---------------------------------------------------------------- 反序列化到 POCO
        public static T ToObject<T>(string json)
        {
            object node = Parse(json);
            object o = ConvertNode(node, typeof(T));
            return o == null ? default(T) : (T)o;
        }

        public static void Populate<T>(string json, T target)
        {
            object node = Parse(json);
            FillObject(node, target, typeof(T));
        }

        private static void FillObject(object node, object target, Type t)
        {
            Dictionary<string, object> d = node as Dictionary<string, object>;
            if (d == null || target == null) return;
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                object raw;
                if (!d.TryGetValue(p.Name, out raw)) continue;   // 缺字段 → 保留默认值（向后兼容）
                try { p.SetValue(target, ConvertNode(raw, p.PropertyType), null); }
                catch { /* 单个字段类型不兼容不影响整体 */ }
            }
        }

        private static object ConvertNode(object node, Type t)
        {
            if (node == null) return t.IsValueType ? Activator.CreateInstance(t) : null;

            Type real = Nullable.GetUnderlyingType(t) ?? t;
            if (real == typeof(object)) return node;
            if (real.IsInstanceOfType(node)) return node;

            if (real.IsEnum)
            {
                if (node is string) { try { return Enum.Parse(real, (string)node, true); } catch { return Activator.CreateInstance(real); } }
                try { return Enum.ToObject(real, Convert.ToInt64(node, CultureInfo.InvariantCulture)); }
                catch { return Activator.CreateInstance(real); }
            }
            if (real == typeof(string)) return node is string ? node : Convert.ToString(node, CultureInfo.InvariantCulture);
            if (real == typeof(bool)) return ToBool(node);
            if (real == typeof(double)) return ToDouble(node);
            if (real == typeof(float)) return (float)ToDouble(node);
            if (real == typeof(int)) return (int)ToDouble(node);
            if (real == typeof(long)) return (long)ToDouble(node);
            if (real == typeof(short)) return (short)ToDouble(node);
            if (real == typeof(byte)) return (byte)ToDouble(node);
            if (real == typeof(decimal)) return (decimal)ToDouble(node);

            if (real.IsArray)
            {
                List<object> src = node as List<object>;
                if (src == null) return null;
                Type et = real.GetElementType();
                Array arr = Array.CreateInstance(et, src.Count);
                for (int i = 0; i < src.Count; i++) arr.SetValue(ConvertNode(src[i], et), i);
                return arr;
            }

            if (real.IsGenericType)
            {
                Type def = real.GetGenericTypeDefinition();
                Type[] args = real.GetGenericArguments();

                if (def == typeof(List<>))
                {
                    IList list = (IList)Activator.CreateInstance(real);
                    List<object> src = node as List<object>;
                    if (src != null) foreach (object o in src) list.Add(ConvertNode(o, args[0]));
                    return list;
                }
                if (def == typeof(Dictionary<,>) && args[0] == typeof(string))
                {
                    IDictionary dict = (IDictionary)Activator.CreateInstance(real);
                    Dictionary<string, object> src = node as Dictionary<string, object>;
                    if (src != null) foreach (KeyValuePair<string, object> kv in src) dict[kv.Key] = ConvertNode(kv.Value, args[1]);
                    return dict;
                }
            }

            // POCO
            if (real.IsClass && !real.IsAbstract)
            {
                object inst;
                try { inst = Activator.CreateInstance(real); }
                catch { return null; }
                FillObject(node, inst, real);
                return inst;
            }
            return null;
        }

        private static bool ToBool(object n)
        {
            if (n is bool) return (bool)n;
            if (n is string) return string.Equals((string)n, "true", StringComparison.OrdinalIgnoreCase) || (string)n == "1";
            return ToDouble(n) != 0;
        }

        private static double ToDouble(object n)
        {
            if (n is double) return (double)n;
            if (n is string) { double d; double.TryParse((string)n, NumberStyles.Float, CultureInfo.InvariantCulture, out d); return d; }
            try { return Convert.ToDouble(n, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
    }
}
