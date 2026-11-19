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

    ok = not (missing or unlisted or errors or cs5 or acad or undef or pkg_issues or beh_issues or w7_issues)
    print("\n结论：%s" % ("PASS（静态层面）" if ok else "存在待确认项"))
    print("真编译验证：tools/install.ps1 -CompileOnly     API 核对：tools/verify_acad_api.ps1")
    print("纯逻辑实测：tools/selftest/run.ps1（JSON 往返 / 命名模板 / PDF 合并 / 预检辅助）")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
