"""
MPlot 源码 / 安装包静态自检（零依赖，Python 3.8+）。

本机已按 AutoCAD 2014 R19.1 真实程序集**真编译通过**，这个脚本负责防止后续改动回退：
  1) csproj 里的 Compile 条目与磁盘文件一一对应
  2) 每个 .cs 的括号 / 预处理指令 / 命名空间完整
  3) 跨文件静态类调用没有明显拼错的方法名
  4) ★ 源码兼容系统自带 C# 5 编译器（一键安装器默认用它）
  5) ★ 部署包：PowerShell 脚本带 UTF-8 BOM、bat 纯 ASCII、XML 可解析、引用齐全
  6) ★ 按本机 2014 程序集实测结论，禁止写回那些「照官方文档写会编译失败」的 API

用法： python tools/static_check.py

注：真正的编译验证请用  tools/install.ps1 -CompileOnly（引用本机 AutoCAD DLL），
    API 存在性验证请用 tools/verify_acad_api.ps1（反射本机 DLL）。
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src")
CSPROJ = os.path.join(SRC, "MPlot.csproj")
NS = {"m": "http://schemas.microsoft.com/developer/msbuild/2003"}


def strip_code(src):
    """去掉注释与字符串字面量，只留语法骨架，避免中文/引号干扰括号统计。"""
    out = []
    i, n, state = 0, len(src), "code"
    while i < n:
        c = src[i]
        d = src[i + 1] if i + 1 < n else ""
        if state == "code":
            if c == "/" and d == "/":
                state, out, i = "line", out + ["  "], i + 2
                continue
            if c == "/" and d == "*":
                state, out, i = "block", out + ["  "], i + 2
                continue
            if c == "@" and d == '"':
                state, out, i = "verb", out + ["  "], i + 2
                continue
            if c == '"':
                state, out, i = "str", out + [" "], i + 1
                continue
            if c == "'":
                state, out, i = "char", out + [" "], i + 1
                continue
            out.append(c)
            i += 1
            continue
        if state == "line":
            if c == "\n":
                state = "code"
                out.append("\n")
            else:
                out.append(" ")
            i += 1
            continue
        if state == "block":
            if c == "*" and d == "/":
                state, out, i = "code", out + ["  "], i + 2
                continue
            out.append("\n" if c == "\n" else " ")
            i += 1
            continue
        if state in ("str", "verb", "char"):
            if c == "\\" and state != "verb" and d:
                out.append("  ")
                i += 2
                continue
            if state == "verb" and c == '"' and d == '"':
                out.append("  ")
                i += 2
                continue
            if (state == "char" and c == "'") or (state in ("str", "verb") and c == '"'):
                state = "code"
                out.append(" ")
                i += 1
                continue
            out.append("\n" if c == "\n" else " ")
            i += 1
    return "".join(out)


def collect_cs():
    result = []
    for dirpath, dirnames, filenames in os.walk(SRC):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for f in filenames:
            if f.endswith(".cs"):
                result.append(os.path.relpath(os.path.join(dirpath, f), SRC).replace("\\", "/"))
    return sorted(result)


# C# 6+ 语法：系统自带 .NET Framework 编译器只支持 C# 5。
CS6_PATTERNS = [
    ("字符串插值 $\"\"",        r'\$'),
    ("null 条件 ?.",            r'\?\.'),
    ("nameof",                  r'\bnameof\s*\('),
    ("using static",            r'\busing\s+static\b'),
    ("out var",                 r'\bout\s+var\b'),
    ("异常过滤器 when",         r'\bcatch\s*\([^)]*\)\s*when\b'),
    ("表达式体成员 => ...;",    r'\b(?:public|private|internal|protected)[^;{]*\)\s*=>'),
    ("自动属性初始化器",         r'\{\s*get;\s*set;\s*\}\s*='),
    ("只读自动属性 { get; }",   r'\{\s*get;\s*\}'),
    ("模式匹配 is T x",         r'\bis\s+[A-Z][A-Za-z0-9_<>]*\s+[a-z_]\w*\b'),
]


def check_cs5(code_map):
    bad = []
    for rel, code in code_map.items():
        for name, pattern in CS6_PATTERNS:
            for m in re.finditer(pattern, code):
                line = code.count("\n", 0, m.start()) + 1
                bad.append("%s:%d  %s  -> %s" % (rel, line, name, m.group(0)[:48].replace("\n", " ")))
    return bad


# ---------------------------------------------------------------------------
# 按本机 AutoCAD 2014 R19.1 程序集实测（tools/verify_acad_api.ps1）得出的硬规则。
# 每一条都对应一次真实编译错误，改回去会让「一键安装」在 2014 上直接失败。
# ---------------------------------------------------------------------------
ACAD2014_RULES_CODE = [
    ("acmgd.Application 没有 DocumentManager",
     r'\bAcApp\.DocumentManager\b', "改用 AcCore.DocumentManager（Core.Application / accoremgd.dll）"),
    ("acmgd.Application 没有 SetSystemVariable",
     r'\bAcApp\.SetSystemVariable\b', "改用 AcCore.SetSystemVariable"),
    ("acmgd.Application 没有 GetSystemVariable",
     r'\bAcApp\.GetSystemVariable\b', "改用 AcCore.GetSystemVariable"),
    ("acmgd.Application 没有 Idle 事件",
     r'\bAcApp\.Idle\b', "改用 AcCore.Idle"),
]
ACAD2014_RULES_RAW = [
    ("2014 枚举类型是 PlotPaperUnit（单数）",
     r'EnumVal\s*\(\s*"PlotPaperUnits"', 'EnumVal("PlotPaperUnits") 会拿到 null'),
    ("2014 validator 没有 SetPlotWithPlotStyles",
     r'TryValidator\s*\(\s*psv\s*,\s*"SetPlotWithPlotStyles"', "改设 PlotSettings.PlotPlotStyles 属性"),
    ("2014 validator 没有 SetPlotWithLineweights",
     r'TryValidator\s*\(\s*psv\s*,\s*"SetPlotWithLineweights"', "改设 PlotSettings.PrintLineweights 属性"),
]


def check_acad2014(code_map, raw_map):
    issues = []
    for rel, code in code_map.items():
        for name, pattern, fix in ACAD2014_RULES_CODE:
            for m in re.finditer(pattern, code):
                line = code.count("\n", 0, m.start()) + 1
                issues.append("%s:%d  %s -> %s" % (rel, line, name, fix))
    for rel, raw in raw_map.items():
        for name, pattern, fix in ACAD2014_RULES_RAW:
            for m in re.finditer(pattern, raw):
                line = raw.count("\n", 0, m.start()) + 1
                issues.append("%s:%d  %s -> %s" % (rel, line, name, fix))
        # Extents3d 只有 (Point3d,Point3d) / () 构造，六参调用会 CS1729
        for m in re.finditer(r'new\s+Extents3d\s*\(([^)]*)\)', raw):
            if m.group(1).count(",") >= 5:
                line = raw.count("\n", 0, m.start()) + 1
                issues.append("%s:%d  Extents3d 六参构造在 2014 不存在 -> 用 (Point3d,Point3d)" % (rel, line))
    return sorted(set(issues))


# FlowDirection 二义性（CS0104，2026-11-19 真机编译实证）：
# System.Windows.Forms 和 Autodesk.AutoCAD.DatabaseServices 都有 FlowDirection。
# 同文件整段 using 两者、又不给 FlowDirection 加别名时，任何一处裸 FlowDirection 都会编译失败。
# 修法：DatabaseServices 只用别名逐个引（Extents2d / ObjectId，如 FrameBatchForm.cs），
# 或加 using FlowDirection = System.Windows.Forms.FlowDirection;（FrameLibraryForm.cs 的做法）。
# 同理另两组已知冲突：Application（WinForms vs ApplicationServices）、
# Exception（System vs Autodesk.AutoCAD.Runtime，Commands.cs 已起别名）。
AMBIGUITY_PAIRS = [
    ("System.Windows.Forms", "Autodesk.AutoCAD.DatabaseServices", "FlowDirection"),
    ("System.Windows.Forms", "Autodesk.AutoCAD.ApplicationServices", "Application"),
    ("System", "Autodesk.AutoCAD.Runtime", "Exception"),
]


def check_flowdirection(code_map):
    issues = []
    for rel, code in code_map.items():
        for ns1, ns2, t in AMBIGUITY_PAIRS:
            bare1 = re.search(r'^\s*using\s+' + re.escape(ns1) + r'\s*;', code, re.M)
            bare2 = re.search(r'^\s*using\s+' + re.escape(ns2) + r'\s*;', code, re.M)
            alias = re.search(r'^\s*using\s+' + re.escape(t) + r'\s*=', code, re.M)
            if bare1 and bare2 and not alias:
                for m in re.finditer(r'(?<![\w.])' + re.escape(t) + r'(?![\w])', code):
                    line = code.count("\n", 0, m.start()) + 1
                    issues.append("%s:%d  %s 二义性（CS0104）：同文件整段 using %s 与 %s，又未加 %s 别名 -> 其中一个命名空间只用别名逐个引，或加 using %s = %s.%s;" % (rel, line, t, ns1, ns2, t, t, ns1, t))
                    break  # 每文件每组只报一次
    return issues


# 缺 using（CS0103，2026-11-19 真机编译实证：Plotter.cs 用 Encoding.ASCII 却没引 System.Text）。
# static_check 原来只查语法与 API 名，查不出「用了类型、没引命名空间」。
# 只认最稳妥的几种类型位置（new T( / T.静态成员 / catch (T / is|as T / typeof(T / : T 继承 / [T 特性 / T< 泛型），
# 属性名、方法名、枚举成员名（如 Label、Match、Color）不会误报；Padding 在 WinForms 不在 Drawing。
USING_RULES = [
    ("System.Text", ["Encoding", "StringBuilder", "Decoder", "Encoder", "UTF8Encoding"]),
    ("System.Text.RegularExpressions", ["Regex", "Match", "MatchCollection", "Group", "RegexOptions"]),
    ("System.Globalization", ["CultureInfo", "NumberStyles", "DateTimeFormatInfo"]),
    ("System.IO", ["File", "Directory", "Path", "FileStream", "StreamReader", "StreamWriter", "IOException", "FileInfo", "DirectoryInfo", "MemoryStream", "FileAccess", "FileMode", "FileShare", "SeekOrigin", "SearchOption"]),
    ("System.Threading", ["Thread", "ThreadStart", "ManualResetEvent", "AutoResetEvent", "Mutex"]),
    ("System.Reflection", ["Assembly", "MethodInfo", "BindingFlags", "PropertyInfo", "Type", "EventInfo"]),
    ("System.Collections", ["ArrayList", "Hashtable", "Queue", "Stack", "IComparer", "IDictionary", "DictionaryEntry"]),
    ("System.Collections.Generic", ["List", "Dictionary", "HashSet", "IEnumerable", "IList", "ICollection", "KeyValuePair", "Queue", "Stack", "IComparer", "IDictionary"]),
    ("System.Collections.Specialized", ["StringCollection", "NameValueCollection", "StringDictionary"]),
    ("System.Runtime.InteropServices", ["DllImport", "Marshal", "StructLayout", "GCHandle", "Guid", "CharSet", "CallingConvention"]),
    ("System.Diagnostics", ["Process", "Stopwatch", "Debug", "Trace", "ProcessStartInfo"]),
    ("Microsoft.Win32", ["Registry", "RegistryKey"]),
    ("System.Windows.Forms", ["MessageBox", "Form", "Button", "TextBox", "ComboBox", "CheckBox", "Label", "DataGridView", "FlowLayoutPanel", "Panel", "DialogResult", "OpenFileDialog", "SaveFileDialog", "FolderBrowserDialog", "ToolTip", "Padding", "DockStyle", "AnchorStyles", "DataGridViewCellStyle", "FormStartPosition", "FormBorderStyle", "Keys", "FlowDirection", "Application", "MessageBoxButtons", "MessageBoxIcon", "Control", "Cursors", "GroupBox", "NumericUpDown", "PictureBox", "TabControl", "TabPage", "TableLayoutPanel", "ListBox", "ProgressBar", "ComboBoxStyle", "FlatStyle", "BorderStyle", "AutoSizeMode", "ScrollBars", "DataGridViewAutoSizeColumnsMode", "DataGridViewCellEventArgs", "DataGridViewRow", "DataGridViewSelectionMode", "EventHandler", "FormClosedEventArgs", "ColumnStyle", "RowStyle", "SizeType", "SystemColors", "MessageBoxDefaultButton", "DataGridViewButtonColumn", "DataGridViewCell", "DataGridViewRowPostPaintEventArgs", "DataGridViewCheckBoxColumn", "DataGridViewColumnSortMode", "DataGridViewComboBoxColumn", "DataGridViewTextBoxColumn"]),
    ("System.Drawing", ["Color", "Font", "Size", "Point", "Bitmap", "Image", "Graphics", "Icon", "Rectangle"]),
    ("System", ["String", "Math", "DateTime", "Guid", "IDisposable", "EventArgs", "Exception", "Action", "Func", "Random", "Convert", "Environment", "Console", "TimeSpan", "Version", "Uri", "Array", "Enum", "Object", "StringComparison", "StringComparer", "StringSplitOptions", "Tuple", "IComparer", "IDictionary", "DictionaryEntry", "Nullable", "Activator", "FormatException", "InvalidDataException", "IntPtr", "Delegate", "EventInfo"]),
    # Autodesk（归属以 verify_acad_api.ps1 的真反射核对为准：
    # PlotSettings / PlotSettingsValidator 属于 DatabaseServices，不是 PlottingServices；
    # 2026-11-19 审计曾误判为缺 PlottingServices，已用 e0cf2ac 真机编译过的事实纠正）。
    ("Autodesk.AutoCAD.DatabaseServices", ["ObjectId", "ObjectIdCollection", "Transaction", "TransactionManager", "Database", "BlockReference", "BlockTable", "BlockTableRecord", "AttributeReference", "AttributeDefinition", "DBText", "MText", "DBPoint", "Line", "Polyline", "Polyline2d", "Circle", "Arc", "Ellipse", "Entity", "DBObject", "OpenMode", "Extents2d", "Extents3d", "PlotType", "PlotPaperUnit", "Layout", "LayoutManager", "LayerTable", "LayerTableRecord", "TextStyleTable", "DimStyleTable", "Viewport", "ViewTableRecord", "Tolerance", "Handle", "RXObject", "RXClass", "SymbolTable", "SymbolTableRecord", "Hatch", "Dimension", "Spline", "Region", "Solid3d", "SubentityId", "FullSubentityPath", "BlockBegin", "Mline", "Face", "DBDictionary", "Xrecord", "PlotSettings", "PlotSettingsValidator", "DxfCode", "ResultBuffer", "AttributeCollection", "DBDictionaryEntry", "DynamicBlockReferenceProperty", "DynamicBlockReferencePropertyCollection", "Table", "Cell", "CustomScale", "StdScaleType", "PlotRotation"]),
    ("Autodesk.AutoCAD.EditorInput", ["Editor", "PromptSelectionOptions", "PromptSelectionResult", "PromptStatus", "PromptEntityOptions", "PromptEntityResult", "PromptStringOptions", "PromptStringResult", "PromptIntegerOptions", "PromptIntegerResult", "PromptDoubleOptions", "PromptDoubleResult", "PromptKeywordOptions", "PromptPointOptions", "PromptPointResult", "SelectionSet", "SelectionFilter", "TypedValue", "PromptNestedEntityOptions", "PromptNestedEntityResult", "PromptAngleOptions", "PromptDistanceOptions", "DragStatus", "SelectedObject", "PromptCornerOptions"]),
    ("Autodesk.AutoCAD.Geometry", ["Point2d", "Point3d", "Vector2d", "Vector3d", "Matrix2d", "Matrix3d", "Scale3d", "LineSegment2d", "LineSegment3d", "Plane", "Tolerance", "Interval", "CircularArc2d", "CircularArc3d", "Curve3d"]),
    ("Autodesk.AutoCAD.PlottingServices", ["PlotFactory", "PlotEngine", "PlotInfo", "PlotInfoValidator", "PlotPageInfo", "PlotProgressDialog", "MatchingPolicy", "PlotConfigManager", "PlotConfig"]),
    ("Autodesk.AutoCAD.ApplicationServices", ["Application", "Document", "DocumentCollection", "DocumentManager", "DocumentEventArgs"]),
    ("Autodesk.AutoCAD.Runtime", ["Exception", "RXObject", "CommandMethodAttribute", "CommandClassAttribute", "ExtensionApplicationAttribute", "LispFunctionAttribute", "ErrorStatus", "Interop", "CommandMethod", "CommandClass", "ExtensionApplication", "IExtensionApplication"]),
]
GENERIC_TYPES = {"List", "Dictionary", "HashSet", "IEnumerable", "IList", "ICollection", "KeyValuePair", "Queue", "Stack", "IComparer", "IDictionary", "Nullable", "Func", "Action"}


def _type_used(code, t):
    pats = [
        r'new\s+' + t + r'\s*(?:<|[\(\{\[])',   # new T( / new T< / new T{
        r'(?<![\w.])' + t + r'\s*\.',           # T.静态成员
        r'catch\s*\(\s*' + t + r'\b',           # catch (T
        r'\b(?:is|as)\s+' + t + r'\b',          # is T / as T
        r'typeof\s*\(\s*' + t + r'\b',          # typeof(T
        r':\s*' + t + r'\b',                    # : T 继承 / 基类
        r'\[\s*' + t + r'\b',                   # [T 特性
    ]
    if t in GENERIC_TYPES:
        pats.append(r'(?<![\w.])' + t + r'\s*<')  # T< 泛型
    for p in pats:
        m = re.search(p, code)
        if m:
            return m
    return None


def check_usings(code_map):
    issues = []
    type_ns = {}
    for ns, types in USING_RULES:
        for t in types:
            type_ns.setdefault(t, set()).add(ns)
    for rel, code in code_map.items():
        imported = set(re.findall(r'^\s*using\s+([\w.]+)\s*;', code, re.M))
        aliased = set(re.findall(r'^\s*using\s+(\w+)\s*=', code, re.M))
        for t, nss in sorted(type_ns.items()):
            if t in aliased:
                continue
            if nss & imported:
                continue
            m = _type_used(code, t)
            if m:
                line = code.count("\n", 0, m.start()) + 1
                issues.append("%s:%d  缺 using（CS0103）：用到了 %s，却没引 %s" % (rel, line, t, " 或 ".join(sorted(nss))))
    return issues


def check_package():
    """安装包层面的硬检查：一条不满足，用户在 CAD 里就看不到菜单。"""
    issues = []
    checks = []

    # PowerShell 5.1 读无 BOM 的 UTF-8 脚本会按 GBK 解释，中文全乱
    for rel in ("tools/install.ps1", "tools/uninstall.ps1", "tools/verify_acad_api.ps1"):
        p = os.path.join(ROOT, rel)
        if not os.path.isfile(p):
            issues.append("缺少 " + rel)
            continue
        raw = open(p, "rb").read()
        has_bom = raw[:3] == b"\xef\xbb\xbf"
        checks.append("%s  UTF-8 BOM=%s" % (rel, has_bom))
        if not has_bom:
            issues.append(rel + " 必须带 UTF-8 BOM（PowerShell 5.1 中文会乱码）")

    # bat 全 ASCII，避免 cmd 代码页把中文提示变成乱码
    for rel in ("一键安装.bat", "卸载.bat"):
        p = os.path.join(ROOT, rel)
        if not os.path.isfile(p):
            issues.append("缺少 " + rel)
            continue
        raw = open(p, "rb").read()
        non_ascii = sum(1 for b in raw if b > 127)
        checks.append("%s  非ASCII字节=%d" % (rel, non_ascii))
        if non_ascii:
            issues.append(rel + " 内容必须是纯 ASCII（文件名可以是中文）")

    # XML 必须能被解析，且 SeriesMin 要覆盖本机 2014 R19.1
    xml_path = os.path.join(ROOT, "Bundle", "MPlot.bundle", "PackageContents.xml")
    if not os.path.isfile(xml_path):
        issues.append("缺少 PackageContents.xml")
    else:
        try:
            r = ET.parse(xml_path).getroot()
            checks.append("PackageContents.xml  root=%s" % r.tag)
            if r.tag != "ApplicationPackage":
                issues.append("PackageContents.xml 根节点必须是 ApplicationPackage")
            entries = r.findall(".//ComponentEntry")
            if len(entries) != 1 or "./Contents/MPlot.dll" not in [e.get("ModuleName") for e in entries]:
                issues.append("PackageContents.xml 的 ComponentEntry 必须指向 ./Contents/MPlot.dll")
            rr = r.find(".//RuntimeRequirements")
            mm = re.search(r'R(\d+)\.(\d+)', rr.get("SeriesMin", "") if rr is not None else "")
            if not mm or int(mm.group(1)) > 19:
                issues.append("静态模板 SeriesMin 必须 <= R19.0，否则 AutoCAD 2014 (R19.1) 不加载")
            else:
                checks.append("PackageContents.xml  SeriesMin=%s（覆盖 2014）" % rr.get("SeriesMin"))
        except Exception as ex:
            issues.append("PackageContents.xml 解析失败：%s" % ex)

    # 必需的引用、目标框架与零依赖开关
    proj = open(CSPROJ, encoding="utf-8-sig").read()
    for token in ("accoremgd", "acdbmgd", "acmgd", "Microsoft.CSharp", "BuiltInPdfMerge.cs"):
        if token not in proj:
            issues.append("MPlot.csproj 缺少 " + token)
    if not re.search(r'<TargetFrameworkVersion>v4\.', proj):
        issues.append("MPlot.csproj 目标框架必须是 .NET Framework 4.x（本机 acad.exe.config = v4.0）")
    if "Condition=\"Exists('$(AcadDir)\\accoremgd.dll')\"" in proj:
        issues.append("accoremgd 不能是条件引用：本机 2014 就有，且代码强依赖 Core.Application")
    if "<DefineConstants>TRACE;MPLOT_PDFSHARP" in proj:
        issues.append("默认编译不能启用 MPLOT_PDFSHARP（会重新引入 NuGet 依赖）")

    # 安装器：版本识别必须走程序集版本、必须有只编译模式、XML 必须写 BOM
    inst = open(os.path.join(ROOT, "tools", "install.ps1"), encoding="utf-8-sig").read()
    if "AssemblyName]::GetAssemblyName" not in inst and "AssemblyName::GetAssemblyName" not in inst:
        issues.append("install.ps1 必须按 acdbmgd.dll 程序集版本识别 CAD（FileMajorPart 会把 2014 读成 25）")
    if "CompileOnly" not in inst:
        issues.append("install.ps1 缺少 -CompileOnly 编译验证模式")
    if "$script:utf8bom" not in inst:
        issues.append("install.ps1 写 PackageContents.xml 必须用 UTF-8 BOM")
    if re.search(r'Get-ChildItem[^\n]*\s-File(\s|$)', inst) or re.search(r'Get-ChildItem[^\n]*\s-Directory(\s|$)', inst):
        issues.append("install.ps1 不能用 -File/-Directory 动态参数（通配符路径不存在时会报参数错误）")
    if "TRUSTEDPATHS" not in inst:
        issues.append("install.ps1 用户目录安装时必须自动写 TRUSTEDPATHS")

    for rel in ("README.md", "MPlot.sln", "src/MPlot.Net8.csproj"):
        if not os.path.isfile(os.path.join(ROOT, rel)):
            issues.append("缺少 " + rel)

    return checks, issues


# ------------------------------------------------------------------ Win7 / PowerShell 2.0 兼容
# Win7 SP1 自带 PowerShell 2.0（跑在 .NET 2.0 上），原生 csc.exe 是 C# 4 编译器。
WIN7_BANNED = [
    ("#requires -Version 3.0", "安装器要求 PS3，Win7 自带 PS2 会直接拒绝运行"),
    ("[pscustomobject]", "PS3 才有的类型加速器，Win7 的 PS2 不认"),
    ("[string]::IsNullOrWhiteSpace", ".NET 4 才有的方法，Win7 的 PS2 跑在 .NET 2.0 上"),
    ("Split-Path -Parent $PSScriptRoot", "把 PS3 才有的 $PSScriptRoot 当唯一路径来源"),
    ("Invoke-Build48 $csc '5'", "给 Win7 原生 C# 4 编译器传 /langversion:5"),
]
WIN7_REQUIRED = [
    ("tools/install.ps1", "#requires -Version 2.0", "install.ps1 必须允许 PS2"),
    ("tools/install.ps1", "function IsBlank", "缺少替代 IsNullOrWhiteSpace 的 PS2 函数"),
    ("tools/install.ps1", "function New-Obj", "缺少替代 [pscustomobject] 的 PS2 函数"),
    ("tools/install.ps1", "MyInvocation.MyCommand.Path", "缺少 $PSScriptRoot 的 PS2 兜底"),
    ("tools/install.ps1", r"Microsoft.NET\Framework\v4.0.30319", "缺少 32 位 Win7 的编译器回退"),
    ("tools/install.ps1", "Invoke-Build48 $csc ''", "未走默认语言版本（Win7 原生 C# 4）"),
    ("tools/uninstall.ps1", "#requires -Version 2.0", "uninstall.ps1 必须允许 PS2"),
]


def check_win7():
    checks, issues = [], []
    texts = {}
    for rel in ("tools/install.ps1", "tools/uninstall.ps1",
                "tools/verify_acad_api.ps1", "tools/selftest/run.ps1"):
        p = os.path.join(ROOT, rel)
        texts[rel] = open(p, encoding="utf-8-sig").read() if os.path.isfile(p) else ""
    allps = "\n".join(texts.values())
    for token, why in WIN7_BANNED:
        if token in allps:
            issues.append("Win7 不兼容：" + why + "（残留 " + token + "）")
    for rel, token, why in WIN7_REQUIRED:
        if token not in texts.get(rel, ""):
            issues.append("Win7 不兼容：" + why)
    for rel in ("Bundle/MPlot.bundle/PackageContents.xml", "tools/install.ps1"):
        p = os.path.join(ROOT, rel)
        if os.path.isfile(p) and 'OS="Win64"' in open(p, encoding="utf-8-sig").read():
            issues.append("Win7 不兼容：" + rel + " 限定 OS=Win64，32 位 Win7 会被 CAD 拒绝加载")
    for rel in ("一键安装.bat", "卸载.bat"):
        p = os.path.join(ROOT, rel)
        if os.path.isfile(p) and "chcp 65001" in open(p, encoding="ascii", errors="ignore").read():
            issues.append("Win7 不兼容：" + rel + " 强制 UTF-8 代码页（Win7 控制台会乱码）")
    if not issues:
        checks.append("Win7 SP1 / PowerShell 2.0 / 原生 C# 4 编译器兼容规则：全部通过")
    return checks, issues


# 菜单 4 项：通用型 / 图框型 / 打印设置 / 一键卸载…，名称与顺序逐字一致
MENU_EXPECT = [
    "批打到文件 (通用型)", "批打到文件 (图框型)", "打印设置", "一键卸载…",
]
# 用户明确不要的入口：既不能出现在菜单，也不能注册成 CAD 命令
MENU_REMOVED = [
    "MP_SINGLE", "MP_PAPER_NEW", "MP_PAPER_OLD", "MP_MISCSET",
    "MP_TOOLS", "MP_HELP", "MP_REG", "MP_ABOUT",
]


def method_body(text, name):
    m = re.search(r"public static string " + name + r"\(string s\)\s*\{(.*?)\n        \}", text, re.S)
    return re.sub(r"\s+", " ", m.group(1)).strip() if m else None


def sheet_fields(text):
    m = re.search(r"class SheetItem\s*\{(.*?)\n    \}", text, re.S)
    if not m:
        return set()
    return set(re.findall(r"public\s+[\w<>\[\],.]+\s+(\w+)\s*\{\s*get;\s*set;\s*\}", m.group(1)))


def check_behavior(raw_map):
    """行为层防回退：菜单逐字、命令覆盖、以及两个已实测修复的 PDF 合并 bug。"""
    issues, checks = [], []
    all_raw = "\n".join(raw_map.values())
    mb = raw_map.get("MenuBuilder.cs", "")
    pairs = re.findall(r'new MenuItemDef\(\s*"([^"]+)"\s*,\s*"([A-Z0-9_]+)"\s*\)', mb)
    labels, cmds = [p[0] for p in pairs], [p[1] for p in pairs]
    if labels != MENU_EXPECT:
        issues.append("菜单 %d 项名称/顺序与需求清单不一致：%s" % (len(MENU_EXPECT), labels))
    else:
        checks.append("菜单 %d 项名称与顺序：逐字一致" % len(MENU_EXPECT))
    registered = set(re.findall(r'\[CommandMethod\(\s*"([A-Z0-9_]+)"', all_raw))
    notreg = [c for c in cmds if c not in registered]
    if notreg:
        issues.append("菜单项调用了未注册命令：%s" % notreg)
    else:
        checks.append("菜单 %d 项 → 命令注册全部命中" % len(cmds))
    badreg = [c for c in MENU_REMOVED if c in registered]
    if badreg:
        issues.append("仍注册了用户要求移除的命令：%s" % badreg)
    else:
        checks.append("已移除 8 个不需要的命令注册")
    expected = set(cmds) | set(["MP_MENU"])
    if registered != expected:
        issues.append("命令注册与精简后的菜单不一致：多=%s 少=%s"
                      % (sorted(registered - expected), sorted(expected - registered)))
    else:
        checks.append("命令注册恰好为 5 个（4 项菜单 + MP_MENU）")
    if "MP_MENU" not in registered:
        issues.append("缺少 MP_MENU（菜单挂不上时的手工重建入口）")
    if "ForEachComItem" not in mb:
        issues.append("MenuBuilder 必须兼容 COM 集合 0-based / 1-based 双基准索引")
    # 实测：COM 菜单宏带 ^C^C_ 前缀时，AutoCAD 2014 + 天正把整串当命令名，
    # 点击报「未知命令 ^C^C_MP_FRAME」。宏必须是纯命令名 + 尾随空格。
    if '"^C^C_"' in mb or '^C^C_' + '" + ' in mb:
        issues.append("菜单宏仍带 ^C^C_ 前缀：CAD 会把整串当命令名报未知命令（见截图实证）")
    else:
        checks.append("菜单宏为纯命令名（无 ^C^C_ 前缀，实测可点）")
    # 菜单已挂在菜单栏时不能直接 return：旧 DLL 留下的错误宏不会刷新，
    # 必须在每次启动时重建条目。原「if (exists) return true」是真实 bug。
    if re.search(r'if\s*\(\s*exists\s*\)\s*\{\s*_ok\s*=\s*true', mb):
        issues.append("菜单已存在时直接返回，旧的 ^C^C_ 错误宏不会被刷新")
    else:
        checks.append("菜单每次启动重建条目，旧宏不会残留")

    if re.search(r'\basync\s+(?:void|Task)|\bawait\s+', all_raw):
        issues.append("使用了 async/await（C# 5 特性），Win7 原生 C# 4 编译器无法编译")
    else:
        checks.append("未使用 async/await（Win7 原生 C# 4 编译器可编译）")

    merge = raw_map.get("BuiltInPdfMerge.cs", "")
    if ('int endObjAt = _text.IndexOf("endobj"' not in merge or
            "Math.Min(endObjAt - bodyStart, 65536)" not in merge):
        issues.append("内置合并：stream 搜索必须限定在本对象 endobj 之前（否则无流对象被误判、合并必失败）")
    else:
        checks.append("内置合并：stream 搜索限定单对象范围（已实测修复）")
    if r"/Pages\s+(\d+)\s+\d+\s+R" not in merge or "CollectPages(pagesRoot" not in merge:
        issues.append("内置合并：必须沿 Catalog 的 /Pages 引用递归（否则收不到任何页面）")
    else:
        checks.append("内置合并：Catalog /Pages 递归存在（已实测修复）")

    # 纯逻辑实测资产：三个文件 + 脚本 BOM + 替身与生产实现一致
    st = os.path.join(ROOT, "tools", "selftest")
    for rel in ("Selftest.cs", "make_pdfs.py", "run.ps1"):
        if not os.path.isfile(os.path.join(st, rel)):
            issues.append("缺少纯逻辑实测文件 tools/selftest/" + rel)
    runp = os.path.join(st, "run.ps1")
    if os.path.isfile(runp):
        b = open(runp, "rb").read()
        if b[:3] != b"\xef\xbb\xbf":
            issues.append("tools/selftest/run.ps1 必须带 UTF-8 BOM")
        else:
            checks.append("纯逻辑实测：4 组断言 + 独立校验脚本齐备（run.ps1 编码正确）")
    selftest_cs = os.path.join(st, "Selftest.cs")
    if os.path.isfile(selftest_cs):
        stub = open(selftest_cs, encoding="utf-8").read()
        prod = raw_map.get("AcadUtil.cs", "")
        a, b = method_body(prod, "SafeFileName"), method_body(stub, "SafeFileName")
        if a is None or b is None or a != b:
            issues.append("测试替身 AcadUtil.SafeFileName 与生产实现不一致（测得的不算真实逻辑）")
        pf, sf = sheet_fields(raw_map.get("FrameScanner.cs", "")), sheet_fields(stub)
        if not pf or not sf or not sf.issubset(pf):
            issues.append("测试替身 SheetItem 字段与生产定义不一致：多出 %s" % sorted(sf - pf))

    # ---- 用户本轮新需求（不得回退）----
    naming = raw_map.get("Naming.cs", "")
    models = raw_map.get("Models.cs", "")
    forms = raw_map.get("UI/Forms.cs", "")
    scanner = raw_map.get("FrameScanner.cs", "")
    commands = raw_map.get("Commands.cs", "")
    # 1) 命名默认改为「图号_图名_图框大小」，且新增图框大小字段
    if 'DefaultTemplate = "{图号}_{图名}_{图框大小}"' not in naming:
        issues.append("默认命名模板不是「图号_图名_图框大小」")
    elif 'case "图框大小"' not in naming or 'Paper.Preset' not in naming:
        issues.append("Naming 缺「图框大小」字段：应从图框库的纸张代号取值")
    else:
        checks.append("默认命名=图号_图名_图框大小，且图框大小取纸张代号")
    if naming.count('"A_C_B"') > 0 or models.count('"A_C_B"') > 2:
        # Models 里允许迁移比较用的 'A_C_B'（最多 2 处：Load 迁移判断）
        issues.append("仍有旧默认模板 A_C_B 字面量残留（应在 Naming.DefaultTemplate 统一）")
    else:
        checks.append("旧默认模板 A_C_B 已从默认值移除")
    # 2) 点取图号/图名范围：FrameEntry 区域字段 + 批打按位置读
    for fld in ("DnX0", "DnY0", "DnX1", "DnY1", "TtX0", "TtY0", "TtX1", "TtY1"):
        if ("public double " + fld) not in models:
            issues.append("FrameEntry 缺点取区域字段 " + fld)
    if "ReadRegionValue" not in raw_map.get("AttrReader.cs", "") or "ReadRegionValue" not in scanner:
        issues.append("缺按区域读文字能力（录入预览与批打按位置读图号图名共用）")
    else:
        checks.append("图框录入点取图号/图名范围，批打按图框位置读取（区域字段齐全）")
    # 3) 手动框选批量：两个入口都能进，底层连续框选 + 批量流水线齐备
    if "PickRegionsWcs" not in raw_map.get("AcadUtil.cs", ""):
        issues.append("缺连续框选矩形能力 PickRegionsWcs")
    if "RunManualBatch" not in raw_map.get("BatchEngine.cs", "") or "class ManualPickForm" not in forms:
        issues.append("缺手动框选批量流水线或窗口")
    if commands.count("RunManualPick(") < 3:  # 1 定义 + 通用型 + 图框型
        issues.append("手动框选未同时接入通用型与图框型入口")
    if ("PickRegionsWcs" in raw_map.get("AcadUtil.cs", "") and
            "RunManualBatch" in raw_map.get("BatchEngine.cs", "") and
            "class ManualPickForm" in forms and commands.count("RunManualPick(") >= 3):
        checks.append("通用型/图框型均可手动框选，连续框多个+按区域读图号图名+纸张默认A3")

    # ---- 本轮：点选确认锚点（不同图框图号/图名位置不同）+ 缩略图 ----
    attr = raw_map.get("AttrReader.cs", "")
    edit = raw_map.get("UI/FrameLibraryForm.cs", "")
    plotter = raw_map.get("Plotter.cs", "")
    # 4) 人工点选确认：候选文字列表 + 按锚点找最近文字
    if "ListRegionTexts" not in attr or "ReadAtNearest" not in attr:
        issues.append("AttrReader 缺点选确认能力：ListRegionTexts / ReadAtNearest")
    else:
        checks.append("点选确认：列出框选范围候选文字 + 按锚点找最近文字")
    if not all(k in models for k in ("DnAnchorX", "DnAnchorY", "TtAnchorX", "TtAnchorY")):
        issues.append("FrameEntry 缺人工点选锚点字段")
    if "ReadAtNearest" not in scanner:
        issues.append("批打未按点选锚点读图号/图名（FrameScanner 缺 ReadAtNearest）")
    if "AskPickText" not in edit:
        issues.append("录入窗口缺点选确认候选窗 AskPickText")
    if ("_useDn" not in edit or "_useTt" not in edit or
            "启用点选图号" not in edit or "启用点选图名" not in edit):
        issues.append("录入窗口点取项缺启用复选框（参考图2）")
    if "图框对应的图块" not in edit or "CopyFromExisting" not in edit:
        issues.append("录入窗口缺『图框对应的图块』显示或从已录图框复制信息")
    # 5) 缩略图：PNG 驱动生成 + ManualPickForm 显示
    if "class ThumbnailMaker" not in plotter or "PublishToWeb PNG.pc3" not in plotter:
        issues.append("缺缩略图生成器 ThumbnailMaker / PublishToWeb PNG.pc3")
    if "RefreshThumbs" not in forms or "PictureBox" not in forms:
        issues.append("手动框选窗口缺缩略图显示区（RefreshThumbs/PictureBox）")
    if ("ListRegionTexts" in attr and "ReadAtNearest" in attr and
            "DnAnchorX" in models and "AskPickText" in edit and
            "ThumbnailMaker" in plotter and "RefreshThumbs" in forms):
        checks.append("点选确认锚点+缩略图预览：录入候选点选、批打按位置读、窗口下方显示图框小图")
    # 5b) 块文字读取（用户实测「框选区域里没有文字」）：必须递归块属性/块内文字/嵌套块 + 变换矩阵，
    #     不能再只用 SelectCrossingWindow("TEXT,MTEXT")——那选不中 ATTRIB / 块内文字。
    if ("CollectTextsRec" in attr and "AttributeCollection" in attr and
            "BlockTransform" in attr and "AttributeDefinition" in attr and
            "Dimension" in attr and "SelectCrossingWindow" not in attr.split("ScanTextsInRect")[1].split("GetAnyText")[0]):
        checks.append("块文字全类型读取：递归块属性/块内文字/嵌套块/天正/标注，矩阵换算WCS（修复点取报无文字）")
    else:
        issues.append("文字扫描仍是老式 TEXT/MTEXT 交叉窗口，读不到块属性/块内文字（点取会报没有文字）")

    # ---- 本轮：居中根因修复（原点覆盖居中）+ 四边留白 + 打印前预览 ----
    batch = raw_map.get("BatchEngine.cs", "")
    # 6) 居中根因：SetPlotOrigin 只能在「不居中」分支出现；居中必须走 ApplyCentered 回读校验。
    #    旧 bug：无条件 SetPlotOrigin(0,0) 把 SetPlotCentered 抹掉 → 图纸不居中（用户实测）。
    if "ApplyCentered" not in plotter or "PlotCentered" not in plotter:
        issues.append("Plotter 缺居中回读校验 ApplyCentered（居中可能被原点设置覆盖）")
    else:
        checks.append("居中修复：设置后回读 PlotCentered 并补设，确保最终态为居中")
    #    居中分支（if(req.Centered) ... else）里不得再调用 SetPlotOrigin（那会关掉居中）。
    center_block = ""
    m = re.search(r"if\s*\(req\.Centered\)\s*(.*?)\belse\b", plotter, re.S)
    if m: center_block = m.group(1)
    if "ApplyCentered" not in center_block or "SetPlotOrigin" in center_block:
        issues.append("居中分支仍在调用 SetPlotOrigin 或未走 ApplyCentered（会再次导致不居中）")
    else:
        checks.append("居中分支不再设打印原点（根因已修，原点与居中互斥）")
    # 7) 四边留白 v2：纸张不放大（负容差选纸防跳级）+ 内容按比例缩小 + 窗口不外扩。
    #    旧方案外扩窗口会把标称 A1 撑过 841mm 跳到 A0，白边巨大（用户实测）。
    paper_src = raw_map.get("PaperMatcher.cs", "")
    if ("ComputeScaleDenominatorWithMargin" in paper_src and
            "ComputeScaleDenominatorWithMargin" in batch and
            "-0.5" in batch and "四边留白 (mm)" in forms and
            "double marginUnits = 0" in scanner and "double marginUnits = 0" in batch):
        checks.append("四边留白v2：纸张不放大(负容差选纸防跳级)+内容按比例缩小留白+窗口不外扩")
    else:
        issues.append("四边留白 v2 要素缺失（纸张不放大/比例留白/窗口不外扩），可能退回产生巨大白边的旧方案")
    # 8) 打印前预览：配置开关 + 共用 BuildRequest + 预览闸门 + 预览窗 + 真渲染。
    if 'PreviewBeforePrint' not in models or 'PreviewBeforePrint = true' not in models:
        issues.append("全局配置缺打印前预览开关 PreviewBeforePrint（应默认开）")
    if "class PrintPreviewForm" not in forms or "RenderPreview" not in forms:
        issues.append("缺打印前预览窗口 PrintPreviewForm 或渲染调用")
    if "PreviewHook" not in batch or "BuildRequest" not in batch:
        issues.append("缺打印前预览闸门 PreviewHook 或共用打印参数 BuildRequest")
    if "RenderPreview" not in plotter or "PublishToWeb PNG.pc3" not in plotter:
        issues.append("缺所见即所得预览渲染 Plotter.RenderPreview")
    if ("PreviewBeforePrint" in models and "class PrintPreviewForm" in forms and
            "PreviewHook" in batch and "BuildRequest" in batch and "RenderPreview" in plotter):
        checks.append("打印前预览：共用打印参数渲染+缩略图点选放大+确认闸门，设置里可开关")

    # 9) 图框型完整复刻（参考 M-BatchPlot）：框选范围 + 可编辑清单 + 图号重排 + 纸张统计 + 导出目录 + 重复标红。
    fbform = raw_map.get("UI/FrameBatchForm.cs", "")
    if ("ScanInRegions" in scanner and "PickFrameRegions" in commands and
            "RunConfirmedSheets" in batch and "class FrameBatchForm" in fbform and
            "ExportCsv" in fbform and "Renumber" in fbform and "ShowStat" in fbform and
            "MarkDuplicates" in fbform and "FrameBatchForm.cs" in open(CSPROJ, encoding="utf-8").read()):
        checks.append("图框型完整复刻：框选范围只识别范围内图框+可编辑清单+预览/图号重排/纸张统计/导出CSV+重复图号标红")
    else:
        issues.append("图框型完整复刻要素缺失（框选范围/可编辑清单/统计导出/重复标红），可能仍是旧的全图直打")

    # 10) 显示名改为 K-批量打印（只改显示名，不改 DLL/命名空间/命令名 MPlot/MP_*）。
    all_raw_check = "\n".join(raw_map.values())
    menu_src = raw_map.get("MenuBuilder.cs", "")
    if "M - 批打印" in all_raw_check or "M-批打印" in all_raw_check:
        issues.append("仍存在旧显示名「M - 批打印」，应统一为「K-批量打印」")
    if 'MenuTitle = "K-批量打印"' not in menu_src:
        issues.append("菜单标题不是「K-批量打印」")
    else:
        checks.append("显示名统一为 K-批量打印（菜单/日志/弹窗/结果TXT/安装包；DLL/命名空间/命令仍为 MPlot/MP_*）")

    # 11) 录入新图框：点录入时隐藏图框库主窗口 + 弹窗可移动 + 修正模态父窗口(ShowDialog(owner))。
    libform = raw_map.get("UI/FrameLibraryForm.cs", "")
    if ("Hide();" not in libform or "private void AddFrame()" not in libform or
            "FormBorderStyle = FormBorderStyle.Sizable" not in libform or
            "f.ShowDialog(this)" not in libform):
        issues.append("录入新图框交互回退：应有隐藏主窗(Hide)/可移动(Sizable)/模态父窗(ShowDialog(this))")
    else:
        checks.append("录入交互：点录入隐藏图框库主窗+录入窗及弹窗可移动+修正模态父窗")

    # 12) 图框型清单行内删除按钮。
    if ("C_DEL" not in fbform or "DataGridViewButtonColumn" not in fbform or
            "e.ColumnIndex == C_DEL" not in fbform):
        issues.append("图框型清单缺行内删除按钮（DataGridViewButtonColumn / C_DEL 单击删行）")
    else:
        checks.append("图框型清单：每行有独立「删」按钮，单击即删无需选整行")

    # 13) 图纸目录含「图框大小」列（取纸张代号），表头与行、列宽一致。
    catalog_src = raw_map.get("Catalog.cs", "")
    if ('"图框大小"' not in catalog_src or "PaperCode" not in catalog_src or
            'colW = { 12, 45, 95, 18, 26, 120 }' not in catalog_src):
        issues.append("图纸目录缺「图框大小」列或列宽未同步（Headers/Row/colW 需一致）")
    else:
        checks.append("图纸目录含图框大小列（取纸张代号 A1/A2…），CSV 表头/行/CAD 表格列宽同步")

    # 14) 图框库「删除」列必须真正从库移除（不再是软标记），且带不可逆确认。
    if ("RemoveDeletedFrames" not in libform or
            "_lib.Frames.Remove(f)" not in libform or
            "RemoveDeletedFrames();" not in libform or
            "DialogResult.Yes" not in libform or
            "_lib.Renumber();\n            FillGrid();" not in libform):
        issues.append("图框库删除列仍是软标记：应有 RemoveDeletedFrames 真正移除库行 + 不可逆确认")
    else:
        checks.append("图框库删除：勾选后确定时真正从库移除+重排序号+刷新表格+不可逆确认")
    return checks, issues


# ------------------------------------------------------------------ 安装/卸载一致性
# 2026-11-19 安装链路审计的防回退规则（H1/H2/H3/H4/M5/L2/L3/L6/L7/L8/L9/L1）。
# 全是 grep 级不变量：模拟不了真机安装，但能拦住“改一处忘一处”的闭环断裂。
# 真机行为仍以 docs/交付自查清单.md 里的 V1-V5 为准。
def check_installer():
    checks, issues = [], []

    def read(rel):
        p = os.path.join(ROOT, rel)
        return open(p, encoding="utf-8-sig").read() if os.path.isfile(p) else ""

    inst = read("tools/install.ps1")
    uninst = read("tools/uninstall.ps1")
    cs = read("src/Uninstaller.cs")
    readme = read("README.md")

    # H1：安装写 TRUSTEDPATHS 的键，与卸载清的键，必须都覆盖 Variables 与 General；
    # 安装写的值（bundle 目录）与卸载匹配的值（bundle 目录 + 父目录）必须对应。
    if "PSChildName -eq 'Variables'" in inst and "PSChildName -eq 'General'" in inst:
        checks.append("安装器 TRUSTEDPATHS 写入覆盖 Variables 与 General 两个键")
    else:
        issues.append("H1 回退：安装器 TRUSTEDPATHS 写入未覆盖 Variables/General 两个键")
    if '"Variables"' in cs and '"General"' in cs and "targets" in cs:
        checks.append("卸载器 TRUSTEDPATHS 清理覆盖 Variables/General 两个键、bundle 目录与父目录两个值")
    else:
        issues.append("H1 回退：卸载器 CleanTrustedPaths 未覆盖两个键/两个值")
    if "$trustedRoot = $Target" in inst:
        checks.append("安装器 TRUSTEDPATHS 值用 bundle 目录本身（精确）")
    else:
        issues.append("H1 回退：安装器 TRUSTEDPATHS 值不是 bundle 目录（$trustedRoot 应为 $Target）")

    # H2：部署前必须检查另一位置的同名 bundle。
    if "另一位置已存在同名 MPlot.bundle" in inst:
        checks.append("部署前检查另一 ApplicationPlugins 位置的同名 MPlot.bundle，发现即询问")
    else:
        issues.append("H2 回退：安装器部署前未检查另一位置的同名 MPlot.bundle")

    # H3：读不到版本绝不能默认按 2024；多版本时必须问用户。
    if "$major = 24" in inst:
        issues.append("H3 回退：安装器读不到版本号时仍默认按 2024")
    else:
        checks.append("安装器读不到版本号时直接中止，不猜版本")
    if "选哪个版本安装" in inst:
        checks.append("多版本共存时安装器列出版本请用户选择（默认 2014）")
    else:
        issues.append("H3 回退：安装器多版本共存时未询问用户")

    # H4：编译失败必须打印全部 error 行，不再只打最后 30 行；Die 文案不得预设原因。
    if "Select-Object -Last 30" in inst:
        issues.append("H4 回退：编译失败仍只显示最后 30 行")
    else:
        checks.append("编译失败打印全部 error 行（两遍编译器）")
    if "AutoCAD 版本与源码 API 不匹配" in inst:
        issues.append("H4 回退：Die 文案仍预设编译失败原因是 API 不匹配")
    else:
        checks.append("编译失败 Die 文案不再预设原因，指向 install.log")

    # M5：Die 文案指向的 README 节必须存在。
    if "故障速查（安装失败怎么办）" in readme:
        checks.append("README 第 11 节标题与安装器 Die 文案的指引一致")
    else:
        issues.append("M5 回退：README 无「故障速查（安装失败怎么办）」一节，安装器 Die 文案成死指引")

    # L6：CHANGELOG.md 存在（安装器复制列表里有它）。
    if os.path.isfile(os.path.join(ROOT, "CHANGELOG.md")):
        checks.append("CHANGELOG.md 存在，安装器复制不再静默跳过")
    else:
        issues.append("L6 回退：仓库无 CHANGELOG.md")

    # L2：提权分叉不得 exit 0。
    if "exit 2" in inst:
        checks.append("UAC 提权分叉 exit 2，不再让 bat 误报 [OK]")
    else:
        issues.append("L2 回退：安装器提权分叉仍 exit 0")

    # L7：备份只保留最近 2 份。
    if "MPlot.bundle.backup-" in inst and "Select-Object -Skip 2" in inst:
        checks.append("旧 bundle 备份只保留最近 2 份")
    else:
        issues.append("L7 回退：备份保留上限缺失")

    # L8：磁盘扫描不得只写死 C:-F:（回退行里保留该字面量是允许的）。
    if "DriveInfo]::GetDrives" in inst and "foreach ($drv in @('C:', 'D:', 'E:', 'F:'))" not in inst:
        checks.append("Find-Acad 磁盘扫描枚举全部固定盘符")
    else:
        issues.append("L8 回退：Find-Acad 磁盘扫描仍只覆盖 C:-F:")

    # L3：uninstall.ps1 失败时不得 exit 0，且须提示残留。
    if "exit 1" in uninst and "TRUSTEDPATHS" in uninst:
        checks.append("uninstall.ps1 失败 exit 1 且提示 TRUSTEDPATHS 等残留")
    else:
        issues.append("L3 回退：uninstall.ps1 失败仍 exit 0 或无残留提示")

    # L1/L9：两个 bat 必须是纯 CRLF 行尾（Win7 教训），且显式调 64 位 powershell。
    for bat in ("一键安装.bat", "卸载.bat"):
        p = os.path.join(ROOT, bat)
        if not os.path.isfile(p):
            issues.append("缺失：" + bat)
            continue
        data = open(p, "rb").read()
        crlf = data.count(b"\r\n")
        lone = data.replace(b"\r\n", b"").count(b"\n")
        if crlf > 0 and lone == 0:
            checks.append(bat + " 为 CRLF 行尾（Win7 兼容）")
        else:
            issues.append("L1 回退：" + bat + " 不是纯 CRLF 行尾（CRLF=%d，孤立 LF=%d）" % (crlf, lone))
        try:
            text = data.decode("utf-8-sig")
        except Exception:
            text = ""
        if "Sysnative" in text and "System32" in text:
            checks.append(bat + " 显式调 64 位 powershell")
        else:
            issues.append("L9 回退：" + bat + " 未显式调 64 位 powershell")
    return checks, issues


def check_round5():
    """2026-11-19 第五轮（真机反馈：纸张 100 倍 / 批量纸张不联动文件名 / 图号 J /
    空白页 / 卡顿 / 卸载菜单复活）的防回退规则。grep 级别，与 check_installer() 同风格。"""
    checks, issues = [], []

    def read(rel):
        p = os.path.join(ROOT, rel)
        return open(p, encoding="utf-8-sig").read() if os.path.isfile(p) else ""

    models = read("src/Models.cs")
    scanner = read("src/FrameScanner.cs")
    picker = read("src/BlockPicker.cs")
    reader = read("src/AttrReader.cs")
    acad = read("src/AcadUtil.cs")
    batch = read("src/BatchEngine.cs")
    cmds = read("src/Commands.cs")
    menu = read("src/MenuBuilder.cs")
    uninst = read("src/Uninstaller.cs")
    form = read("src/UI/FrameBatchForm.cs")

    # 纸张：按插入比例还原（GuessScaled + InsertionScale），猜不到保留库纸张。
    if "GuessScaled" in models and "ScaleDivisors" in models and "150" in models:
        checks.append("纸张按插入比例还原：GuessScaled 含比例吸附阶梯（含 1:150）")
    else:
        issues.append("纸张回退：Models.cs 缺 GuessScaled / ScaleDivisors / 1:150 吸附")
    if "InsertionScale" in acad and "ScaleFactors" in acad:
        checks.append("插入比例取自 BlockReference.ScaleFactors（取不到/非法时回 1）")
    else:
        issues.append("纸张回退：AcadUtil 缺 InsertionScale / ScaleFactors")
    if "GuessScaled" in scanner and "InsertionScale" in scanner and "!g.IsCustom" in scanner:
        checks.append("Scan 按还原尺寸识别纸张，猜不到标准幅面时保留库纸张")
    else:
        issues.append("纸张回退：Scan 未走 GuessScaled，或猜不到时仍会拿自定义覆盖库纸张")
    if "GuessScaled" in picker and "InsertionScale" in picker and "!g.IsCustom" in picker:
        checks.append("BlockPicker 与 Scan 用同一套按比例还原的纸张识别")
    else:
        issues.append("纸张回退：BlockPicker 未走 GuessScaled / 仍会拿自定义覆盖库纸张")

    # 批量设纸张：必须同步重算文件名；选自定义保留本行实测尺寸。
    m = re.search(r"private void ApplyPaperToSelected\(\)(.*?)\n        private void ",
                  form, re.S)
    body = m.group(1) if m else ""
    if "BuildFileName" in body:
        checks.append("批量设置纸张后同步重算文件名（纸张列与文件名一起变）")
    else:
        issues.append("批量纸张回退：ApplyPaperToSelected 未重算 FileStem，文件名会留旧的 _自定义")
    if 'pt == "自定义"' in body and "_items[i].WidthMm" in body:
        checks.append("批量设纸张选「自定义」时保留本行实测尺寸，不重置成 841×594")
    else:
        issues.append("批量纸张回退：ApplyPaperToSelected 选自定义时未保留本行实测尺寸")

    # 图号 J：单字守卫 + 同行碎片拼接。
    if reader.count("v.Trim().Length < 2") >= 2:
        checks.append("图号/图名打分器拒绝单字（J 这类碎片打 0 分）")
    else:
        issues.append("图号回退：AttrReader 打分器未加单字守卫")
    if "MergeDrawNoLine" in reader and "tol, true" in scanner:
        checks.append("图号同行碎片拼接：区域读值与锚点读值都拼（J + 01 → J-01）")
    else:
        issues.append("图号回退：缺 MergeDrawNoLine，或图号锚点未开碎片拼接")
    if scanner.count("v.Trim().Length < 2") >= 4:
        checks.append("Scan 锚点/区域读值遇单字即清空，不覆盖已读值、不堵住区域回退")
    else:
        issues.append("图号回退：Scan 锚点/区域分支未加单字清空")

    # 空白页：导出瞬间按当前视图重算 DCS 打印窗口。
    if "WcsRectToDcs(s.WindowWcs)" in batch:
        checks.append("BuildRequest 导出时按当前视图重算 DCS 窗口（旧 DCS 在定位/预览改视图后会过期致白纸）")
    else:
        issues.append("空白页回退：BuildRequest 仍用扫描时存的旧 WindowDcs")

    # 卡顿：全图文字缓存、定义包围盒缓存、矩形兜底跳过、框选预过滤、扫描进度提示。
    if "BeginTextCache" in reader and "EndTextCache" in reader and "ScanTextsCore" in reader:
        checks.append("全图文字缓存：Scan 武装、第一次读值时建缓存、之后只筛缓存")
    else:
        issues.append("卡顿回退：AttrReader 缺文字缓存（Begin/EndTextCache/ScanTextsCore）")
    if "BeginTextCache" in scanner:
        checks.append("Scan 武装文字缓存")
    else:
        issues.append("卡顿回退：Scan 未武装文字缓存")
    if "defCache" in acad and "FrameExtents(br, tr, defCache)" in scanner and "FrameExtents(br, tr, defCache)" in picker:
        checks.append("定义包围盒缓存：同一图框定义同批只算一次（Scan + 点选都用）")
    else:
        issues.append("卡顿回退：FrameExtents 未按块定义缓存，同一图框每张都重算定义")
    if "if (cfg.RectFrameFallback && res.Sheets.Count == 0)" in scanner:
        checks.append("矩形兜底只在没有块图框时才跑（有块图时不全图扫多段线）")
    else:
        issues.append("卡顿回退：矩形兜底未跳过，块图存在时仍全图扫矩形")
    if "InAnyRegion" in scanner and "Scan(db, lib, cfg, regions)" in scanner:
        checks.append("框选预过滤：框外图框在算包围盒、读文字之前就跳过")
    else:
        issues.append("卡顿回退：Scan 未做框选预过滤（仍全图扫完再筛）")
    if "正在扫描图框" in cmds:
        checks.append("MP_FRAME 扫描前后命令行报进度与用时")
    else:
        issues.append("卡顿回退：MP_FRAME 扫描无进度提示")

    # 卸载菜单复活：已卸载标志屏蔽 Idle/切图/MP_MENU 重建。
    if "MarkUninstalled" in menu and "if (_uninstalled) return false" in menu:
        checks.append("MenuBuilder 有已卸载标志，EnsureMenu 在已卸载时拒绝重建")
    else:
        issues.append("卸载回退：MenuBuilder 缺已卸载标志，Idle 事件会把菜单建回来")
    if cmds.count("!MenuBuilder.Uninstalled") >= 2:
        checks.append("Idle 与切图事件重建菜单前都查已卸载标志")
    else:
        issues.append("卸载回退：Idle/切图重建菜单未查已卸载标志")
    if "MarkUninstalled" in uninst:
        checks.append("一键卸载先立已卸载标志再删菜单")
    else:
        issues.append("卸载回退：Uninstaller 未立已卸载标志")
    return checks, issues


def main():
    tree = ET.parse(CSPROJ)
    listed = sorted(e.attrib["Include"].replace("\\", "/") for e in tree.findall(".//m:Compile", NS))
    on_disk = collect_cs()

    print("csproj Compile 条目 : %d" % len(listed))
    print("磁盘 .cs 文件       : %d" % len(on_disk))
    missing = [f for f in listed if not os.path.isfile(os.path.join(SRC, f))]
    unlisted = [f for f in on_disk if f not in listed]
    print("csproj 指向但缺失   : %s" % (", ".join(missing) if missing else "无"))
    print("磁盘存在但未编译    : %s" % (", ".join(unlisted) if unlisted else "无"))

    errors, code_map, raw_map = [], {}, {}
    for rel in listed:
        path = os.path.join(SRC, rel)
        if not os.path.isfile(path):
            continue
        raw = open(path, encoding="utf-8-sig").read()
        raw_map[rel] = raw
        code = strip_code(raw)
        code_map[rel] = code
        stack, line, pairs = [], 1, {"}": "{", ")": "(", "]": "["}
        for ch in code:
            if ch == "\n":
                line += 1
            if ch in "{([":
                stack.append((ch, line))
            elif ch in "})]":
                if not stack or stack[-1][0] != pairs[ch]:
                    errors.append("%s:%d 括号不匹配 '%s'" % (rel, line, ch))
                    break
                stack.pop()
        if stack:
            errors.append("%s 括号未闭合 %s" % (rel, stack[-3:]))
        depth = 0
        for ln in raw.splitlines():
            t = ln.strip()
            if re.match(r"^#\s*if", t):
                depth += 1
            elif re.match(r"^#\s*endif", t):
                depth -= 1
        if depth != 0:
            errors.append("%s #if/#endif 不配对 (%d)" % (rel, depth))
        if "namespace MPlot" not in raw and "[assembly:" not in raw:
            errors.append("%s 缺少 namespace MPlot（且不是纯 AssemblyInfo）" % rel)

    print("\n--- 语法自检 ---")
    if errors:
        for e in errors:
            print("  [X] " + e)
    else:
        print("  括号 / 预处理指令 / 命名空间：全部通过")

    print("\n--- C# 5 兼容性（一键安装的默认编译器）---")
    cs5 = check_cs5(code_map)
    if cs5:
        for e in cs5[:30]:
            print("  [X] " + e)
    else:
        print("  未使用 C# 6+ 语法：系统自带编译器可直接编译")

    print("\n--- 本机 AutoCAD 2014 R19.1 API 防回退 ---")
    acad = check_acad2014(code_map, raw_map)
    if acad:
        for e in acad:
            print("  [X] " + e)
    else:
        print("  未写回任何在 2014 上不存在 / 冲突的 API")

    print("\n--- 类型二义性（CS0104）---")
    fd = check_flowdirection(code_map)
    if fd:
        for e in fd:
            print("  [X] " + e)
    else:
        print("  未发现已知的类型二义性冲突")

    print("\n--- 缺 using（CS0103）---")
    mu = check_usings(code_map)
    if mu:
        for e in mu:
            print("  [X] " + e)
    else:
        print("  常用类型都有对应 using")

    # 粗查跨文件静态调用：收集所有方法/属性定义
    defs = set()
    for rel in on_disk:
        code = strip_code(open(os.path.join(SRC, rel), encoding="utf-8-sig").read())
        for m in re.finditer(
            r"\b(?:public|internal|private|protected)\s+(?:static\s+|sealed\s+|readonly\s+|virtual\s+|override\s+|new\s+)*"
            r"(?:[\w<>\[\],.?\s]+?)\s+(\w+)\s*(?:<[^>{}]*>)?\s*\(", code
        ):
            defs.add(m.group(1))
        for m in re.finditer(
            r"\b(?:public|internal|private)\s+(?:static\s+)?(?:[\w<>\[\],.?]+)\s+(\w+)\s*\{\s*get", code
        ):
            defs.add(m.group(1))

    static_classes = [
        "AcadUtil", "Logger", "MiniJson", "Naming", "PaperMatcher", "FrameScanner",
        "PaperPresets", "AttrReader", "PdfMerger", "BuiltInPdfMerge", "Catalog",
        "BatchEngine", "Plotter", "MenuBuilder", "Commands", "InfoForm", "Ui",
        "FrameLibrary", "GlobalConfig",
    ]
    inherited = {"ToString", "Dispose", "Equals", "GetHashCode", "Show"}
    calls = {}
    for rel in on_disk:
        code = strip_code(open(os.path.join(SRC, rel), encoding="utf-8-sig").read())
        for cls in static_classes:
            for m in re.finditer(r"\b" + cls + r"\.(\w+)\s*\(", code):
                calls.setdefault(cls + "." + m.group(1), set()).add(rel)
    undef = []
    for key, where in calls.items():
        method = key.split(".", 1)[1]
        if method in defs or method in inherited:
            continue
        undef.append("%-38s <- %s" % (key, ",".join(sorted(where))))
    print("\n--- 跨文件方法引用 ---")
    if undef:
        for u in sorted(undef):
            print("  [?] " + u)
    else:
        print("  未发现明显未定义的静态类方法调用")

    print("\n--- 功能与防回退自检 ---")
    beh_checks, beh_issues = check_behavior(raw_map)
    for c in beh_checks:
        print("  " + c)
    for i in beh_issues:
        print("  [X] " + i)

    print("\n--- 安装包自检 ---")
    checks, pkg_issues = check_package()
    for c in checks:
        print("  " + c)
    for i in pkg_issues:
        print("  [X] " + i)

    print("\n--- Win7 兼容性自检 ---")
    w7_checks, w7_issues = check_win7()
    for c in w7_checks:
        print("  " + c)
    for i in w7_issues:
        print("  [X] " + i)

    print("\n--- 安装/卸载一致性自检 ---")
    inst_checks, inst_issues = check_installer()
    for c in inst_checks:
        print("  " + c)
    for i in inst_issues:
        print("  [X] " + i)

    print("\n--- 第五轮防回退自检（纸张比例 / 批量纸张 / 图号 J / 空白页 / 卡顿 / 卸载复活）---")
    r5_checks, r5_issues = check_round5()
    for c in r5_checks:
        print("  " + c)
    for i in r5_issues:
        print("  [X] " + i)

    ok = not (missing or unlisted or errors or cs5 or acad or fd or mu or undef or pkg_issues or beh_issues or w7_issues or inst_issues or r5_issues)
    print("\n结论：%s" % ("PASS（静态层面）" if ok else "存在待确认项"))
    print("真编译验证：tools/install.ps1 -CompileOnly     API 核对：tools/verify_acad_api.ps1")
    print("纯逻辑实测：tools/selftest/run.ps1（JSON 往返 / 命名模板 / PDF 合并 / 预检辅助）")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
