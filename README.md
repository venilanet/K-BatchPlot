# K-批量打印 · AutoCAD / 天正 图框型批量打印 PDF 插件

一句话：在 DWG 里扫图框 → 按图框库的纸张/比例/命名规则逐张出单页 PDF → 合成图纸合集 → 顺手出图纸目录。

> **开源说明**
> - 许可证：MIT（见仓库根目录 `LICENSE`），可自由使用与二次开发，保留版权声明即可。
> - 目标环境：Windows 7 + AutoCAD 2014（R19.1）+ 天正电气 T20 V3，**按本机真实 DLL 反射编译**，不是按官方文档猜 API。换其他版本能否用取决于 API 兼容性，仓库内自检脚本可现场核对。
> - 四个命令：`MP_FRAME`（图框型，核心）、`MP_GENERAL`（通用型）、`MP_PRINTSET`（打印设置）、`MP_UNINSTALL`（一键卸载）；顶部菜单栏为「K-批量打印」。
> - 零 NuGet / 零联网 / 零 Visual Studio 依赖，用系统自带 `csc.exe` 现场编译。
> - 下方「这台机器」章节是作者开发机的实测环境记录，换机安装请看「换一台电脑装：三步」。

---

## 0. 装完就能用

### 这台机器：已经装好了（实测，不是「按官方文档推测」）

| 项 | 实际值 |
|---|---|
| CAD | AutoCAD 2014 简体中文 **64 位 / R19.1**，`D:\Program Files (x86)\Autodesk\AutoCAD 2014`；`acad.exe.config` = `.NET Framework 4.0` |
| 天正 | **T20 天正电气 V3.0**，`D:\软件\TElecT20V3`（`sys19x64`，`CadVersion=R19.1\ACAD-D001:804`） |
| 编译 | ✅ 引用本机 `acdbmgd / acmgd / accoremgd`（19.1.0.0）**真编译通过**：`build\MPlot.dll` 169.5 KB，Windows 自带 C# 5 编译器 |
| API 核对 | ✅ `tools\verify_acad_api.ps1` 反射本机 DLL：**PASS 126 / FAIL 0** |
| 已安装 | `C:\Users\Lenovo\AppData\Roaming\Autodesk\ApplicationPlugins\MPlot.bundle`，`SeriesMin=R19.0 / SeriesMax=R24.3` |
| 受信任位置 | ✅ 已自动写入 3 个 CAD 配置的 `TRUSTEDPATHS`（原值记在 `install.log`，可回滚） |

**现在只差一步：完全关闭并重新打开天正**，顶部菜单栏出现「K-批量打印」。
想彻底免掉任何安全提示：右键 `一键安装.bat` → 以管理员身份运行一次（装到 `C:\Program Files\Autodesk\ApplicationPlugins`）。

**找不到菜单栏**：命令行敲 `MENUBAR` 回车输入 `1`；或敲 `APPAUTOLOAD` 确认是 `1`；或直接敲命令 `MP_FRAME`。

### 换一台电脑装：三步

1. 把整个 `MPlot` 文件夹拷到**装了 AutoCAD / 天正的那台**，双击 `一键安装.bat`
2. Windows 弹「是否允许此应用对你的设备进行更改」时点 **是**（一次 UAC，为写进 Autodesk 受信任目录）
3. **完全关闭并重新打开天正 / AutoCAD** → 菜单栏出现「K-批量打印」

不用 Visual Studio、不用 .NET SDK、不用联网、不用 NuGet、不用手工 NETLOAD。安装器自动找 CAD、用 Windows 自带 C# 编译器现场编译、部署到自动加载目录，全程写 `install.log`。

**Win7 已专门适配**：安装脚本只要 PowerShell 2.0（Win7 自带），不再依赖 `$PSScriptRoot`、`[pscustomobject]`、.NET 4 专属方法；编译器走默认 C# 4 语法（不传 `/langversion:5`），32 位 Win7 自动回退到 `Microsoft.NET\Framework\v4.0.30319\csc.exe`。菜单精简为「通用型 / 图框型 / 打印设置 / 一键卸载…」4 项，命令注册同步只留这 4 项加 `MP_MENU`。

第一次用：菜单第 3 项「**批打到文件 (图框型)**」→「录入新图框」→ 选「**框选图框（自动识别）**」→ 图上框一个图框，块名和图号/图名/版次 Tag 自动填好 →「开始批打 PDF」。

---

## 1. 为什么按本机 DLL 写，而不是照官方 API 文档

用 `tools\verify_acad_api.ps1` 反射本机 2014 的真实程序集后，发现并修掉 5 个「照文档写会直接编译失败」的地方：

| 官方 / 新版写法 | 本机 AutoCAD 2014 R19.1 实际 |
|---|---|
| `Application.DocumentManager` / `Idle` / `SetSystemVariable` | 在 `Core.Application`（**accoremgd.dll**），`acmgd.Application` 里没有 |
| `PlotPaperUnits`（复数） | 类型叫 **`PlotPaperUnit`** |
| validator 的 `SetPlotWithPlotStyles` / `SetPlotWithLineweights` | **根本不存在**；要写 `PlotSettings.PlotPlotStyles / PrintLineweights / ScaleLineweights` 属性 |
| `Extents3d(x1,y1,z1,x2,y2,z2)` 六参构造 | 只有 `(Point3d, Point3d)` 和无参构造 |
| `System.Exception` / WinForms `FlowDirection` | 与 `Autodesk.AutoCAD.Runtime.Exception` / `DatabaseServices.FlowDirection` 同名冲突，必须起别名 |

另外两个只在真编译/真安装时暴露的坑：

- `acad.exe` 的 `FileVersion` 是 `R19.1.18.0.0`，.NET 把 `FileMajorPart` 解析成 **25** → 安装器误判成 AutoCAD 2025，跑去要 .NET 8 SDK 直接失败。已改成按 **`acdbmgd.dll` 的程序集版本**（19.1.0.0）识别。
- `Get-ChildItem -Directory / -File` 是不存在的通配符路径上的动态参数，会报「找不到参数」；已改成 `PSIsContainer` 过滤。编译器还加了 `/utf8output`，中文报错不再乱码。

`tools\static_check.py` 已把这些结论写成防回退规则。

---

## 2. 安装位置与受信任位置

Autodesk 官方文档：**只有 `C:\Program Files\Autodesk\ApplicationPlugins` 默认受信任、不检查数字签名**；`%APPDATA%` 那个位置的未签名 DLL，在 `SECURELOAD=1` 会弹「文件加载 - 安全问题」，`=2` 会直接不加载。

安装器：

- 默认装 `C:\Program Files\Autodesk\ApplicationPlugins\MPlot.bundle`，自动申请管理员权限；
- 拿不到管理员权限时退回 `%APPDATA%\Autodesk\ApplicationPlugins\MPlot.bundle`，**并自动把该目录追加进当前用户所有 AutoCAD 配置的 `TRUSTEDPATHS`**（HKCU，不需要管理员，原值写入 `install.log` 可回滚），不用手工去「选项 → 受信任位置」；
- 也可主动指定用户目录：`一键安装.bat -UserInstall`。本机当前就是这种方式。

`APPAUTOLOAD` 为 0 时 AutoCAD 不会自动加载任何 bundle，命令行改成 1 再重启。

**停用 / 卸载**：双击 `卸载.bat`，输入 `Y` 确认。**只把文件夹改名，不删任何文件**，随时能改回来。也可以在 CAD 里点菜单「打印设置」下方的「一键卸载…」（或敲 `MP_UNINSTALL`）：立即移除菜单、停用自动加载、清理受信任位置与按需加载注册表项，被 CAD 占用的 DLL 安排在下次开机时删除；图框库、设置与日志（`%APPDATA%\MPlot`）保留，重装后可继续用。

---

## 3. 版本适配（安装器自动判断）

| AutoCAD | 内部版本 | .NET | 本插件 | 说明 |
|---|---|---|---|---|
| 2010–2012 | R18.x | 3.5 | ✗ | 明确拒绝，换用 2013+ 的 AutoCAD |
| **2013–2014** | **R19.x** | **4.0** | ✅ **本机实测** | 本机 2014 **有** `accoremgd.dll`；已按真实程序集核对并编译通过，AnyCPU 兼容 32/64 位 |
| **2015–2024** | **R20–R24.3** | **4.5–4.8** | ✅ 主支持 | 天正 T20/T30 主流区间，`SeriesMin=R19.0 / SeriesMax=R24.3` |
| 2025–2026 | R25+ | 8.0 / 10.0 | 实验 | 从 2025 起破坏 .NET 二进制兼容，需 `MPlot.Net8.csproj` + 本机 .NET 8 SDK |

版本判断依据是 `acdbmgd.dll` 的 `AssemblyVersion.Major`，据此动态生成 `PackageContents.xml`；静态模板的 `SeriesMin` 也已从 `R20.0` 修正为 `R19.0`（写 R20.0 会让 2014 直接拒绝加载）。

装了多个 AutoCAD 时会列全部并优先 20–24 区间最高版；天正依附的是另一个版本就指定目录：

```
一键安装.bat -AcadDir "D:\Program Files (x86)\Autodesk\AutoCAD 2014"
```

---

## 4. 验证与排错

### 完整自检（这轮实际跑过，全部 PASS）

| 检查 | 入口 | 结果 |
|---|---|---|
| 源码静态自检 | `python tools\static_check.py` | **PASS**：19 个 .cs、括号 / 预处理 / C# 4 兼容 / 2014 API 防回退 / 菜单 3 项逐字 / 命令覆盖 / 菜单宏无 `^C^C_` / 新命名模板 / 点选确认锚点 / 缩略图 / 手动框选 / 居中根因修复 / 四边留白v2 / 打印前预览 / 块文字全类型读取 / 图框型完整复刻 / **显示名 K-批量打印 / 录入隐藏主窗+可移动+模态父窗 / 行内删除按钮 / 目录含图框大小列 / **图框库真正删除+不可逆确认** / Win7 兼容 / 安装包编码与 XML |
| 本机 API 核对 | `tools\verify_acad_api.ps1` | **PASS 126 / FAIL 0**（反射本机 19.1.0.0 三支 DLL） |
| 真编译 | `tools\install.ps1 -CompileOnly` | **PASS**：`build\MPlot.dll` 约 214.5 KB，系统自带编译器（默认 C# 4，Win7 兼容） |
| 命令注册反射 | `mplot_reflect2`（元数据扫描） | **PASS**：恰好 5 个命令 `MP_GENERAL / MP_FRAME / MP_PRINTSET / MP_UNINSTALL / MP_MENU`，与 4 项菜单对应 |
| 纯逻辑实测 | `tools\selftest\run.ps1` | **PASS 22 / FAIL 0** + 独立校验 xref / 页树 / 内容流 |
| 部署一致性 | SHA256 比对 | **PASS**：已部署 DLL == build 产物，`SeriesMin=R19.0`、XML 可被 `XmlReader` 解析 |

纯逻辑实测把不依赖 CAD 的 `MiniJson / Models / Naming / BuiltInPdfMerge` 单独编译成 exe 真跑：JSON 往返（中文、引号、反斜杠、换行、枚举、Dictionary、缺字段兼容）、命名模板（**默认 `图号_图名_图框大小`** / 旧 `A_C_B` 兼容 / `A_C_T` / 花括号 / 非法字符 / 超长截断 / 空段丢弃）、PDF 合并（3 个标准单页 → 合集，逐条校验 xref 偏移、`/Count`、三页内容流）。

**这轮自检揪出并修掉的 3 个真 bug**（编译都发现不了）：

| # | 问题 | 后果 | 修复 |
|---|---|---|---|
| 1 | `BuiltInPdfMerge.ReadBody` 在后续 64KB 全局搜 `stream` | Catalog 等无流对象误命中后面对象的流 →「字典解析异常」，**内置 PDF 合并必失败** | 搜索窗口限制在本对象 `endobj` 之前 |
| 2 | `CollectPages` 只认 `/Kids` | PDF 的 Catalog 是 `/Pages 2 0 R`，收不到任何页 → **0 页** | 沿 `/Pages` 引用递归进页树 |
| 3 | 菜单 COM 双基准遍历用了 `dynamic` 形参 | 真编译报 **CS1977**（lambda 不能作为动态调度实参） | 形参改 `object`，内部再转 `dynamic` |
| 4 | 菜单宏写成 `^C^C_MP_FRAME`（`AddMenuItem` 第三个参数） | **点击菜单报「未知命令 `^C^C_MP_FRAME`」**，CAD 把整串 `^C^C_` 前缀当成命令名；菜单形同虚设 | 宏改成纯命令名 + 尾随空格（`MP_FRAME `），点菜单 = 命令行敲命令 |
| 5 | 菜单已挂菜单栏时 `EnsureMenu` 直接 `return` | 换了新 DLL 也不重建条目，**旧的错误宏一直残留**，修了代码也不生效 | 每次启动都重建条目刷新宏，只在未挂菜单栏时才插入（不重复加菜单） |
| 6 | 先 `SetPlotCentered(true)` 又无条件 `SetPlotOrigin(0,0)` | AutoCAD 中设原点会关掉居中 → **打印图纸不居中、贴边**（用户实测） | 居中时不设原点、原点只用于手动偏移；居中放最后并回读补设；两条打印路径都覆盖 |

顺带把菜单集合索引改成 0-based / 1-based 双基准重试（天正会加载自己的菜单组，原写法一次异常就整个跳过查找），清条目也做了双基准探测。以上规则（含「菜单宏不得带 `^C^C_`」「已存在不能直接 return」）已写进 `static_check.py`，改回去会直接报警。

**本轮又修掉 1 个隐患**：`ManualPickItem` 一度放进了无 CAD 依赖的 `Models.cs`，用了 `Autodesk...Extents2d` 全限定名 → 纯逻辑实测编译失败（`CS0246`，那份测试不引用 CAD 程序集）。已把该类挪回 `FrameScanner.cs`（与 `SheetItem` 同处有 CAD 依赖层），实测恢复 22/0。

### 本轮新增：图号/图名「点选确认」+ 缩略图预览（按用户参考图）

旧的「框区域 → 程序打分猜文字」识别不准。改成人工确认式，**不同图框各存各的位置，互不干扰**：

1. **录入窗口**按参考图改成左右两栏：左「1 基本信息」（选择图框 / 对应图块名 / 纸张 / 命名 / 排序 / 属性），右「2 信息框录入」（图号 A、图名 C 点取项，各带「启用」复选框）。底部加「从已录图框复制信息」「图框对应的图块」实时显示。
2. **点取图号/图名**：先【选择图框】→ 点【点取图号 (A) 范围】在图框上框出文字小区域 → 弹出候选窗列出框内**全部文字**，你点选哪段是图号/图名 → 程序记住这段文字的**精确中心点**（相对图框归一化锚点）+ 样例值。不再靠猜。
3. **批打时**：按每张图框的实际包围盒，把锚点换算回该图位置，找**离锚点最近的文字**取值（读不到才回退到区域内打分）。所以同一个图框库、多张图，图号图名各不相同也不撞名。
4. **缩略图预览**：手动框选批量打印窗口下方新增一排缩略图，框选后自动用 AutoCAD 自带 `PublishToWeb PNG.pc3` 把每个范围渲染成小图（点图可放大）。生成失败只显示「预览失败」占位，**不影响出图**。图框打印范围仍与缩略图一致。

边界：缩略图依赖 AutoCAD 自带 PNG 虚拟打印机；若目标机器精简安装删了它，缩略图区会显示占位但不影响 PDF 输出。点选确认涉及真实 CAD 交互，本环境只能编译 + 逻辑验证，无法代你在天正界面里点。

### 本轮新增：居中修复 + 四边留白 + 打印前预览（用户实测「图纸不居中」）

**1. 居中为什么失效（根因）**：旧代码先 `SetPlotCentered(true)`，紧接着又无条件 `SetPlotOrigin(0,0)`。在 AutoCAD 里**设置打印原点会关闭居中**——即使是 (0,0)，居中标志也被抹掉，配置里勾着「居中打印」实际却贴边。这是真实 bug，不是错觉。

修复：
- 居中开启时**绝不调用 SetPlotOrigin**；只有你关掉居中、改用手动 X/Y 偏移时才设原点（原点与居中二者互斥，只能留一个）。
- `SetPlotCentered` 移到所有打印设置之后 finally 生效；并经 `ApplyCentered` 做「validator 设 → 属性回读 → 不一致再补设」。本机 2014 的 `PlotSettings` 没有可读的 `PlotCentered` 属性，回读会安静跳过，**主手段是 `SetPlotCentered` validator（本机实测存在）**——回读只是新版上的附加校验，不是本机唯一依赖。
- 通用型（沿用布局页面设置）路径同样显式设置居中，不再受布局遗留原点影响。

**2. 四边留白（v2，修复「打印出来空白太多」）**：旧做法是把打印窗口对称外扩 2mm，但这会把标称 A1(841mm) 撑到 845mm、再加匹配余量 846.5mm → PC3 里 A1 装不下自己，**自动跳到 A0**，白边巨大。现改为：**纸张始终用标称尺寸**（选纸容差取负值，杜绝跳级），**内容按比例缩小**在纸内居中，四周正好留 margin（默认 2mm，可在「打印设置 → 四边留白 mm」改，设 0 则贴边）。配合居中，图落在纸张正中且不再有大白边。留白会产生约零点几个百分点的比例偏差（参考插件官方也是这个取舍）。

**3. 打印前预览**：三类批打入口（图框型 / 通用型 / 手动框选）在真正出图前，先用**与实打完全相同**的 `PlotRequest`（同一窗口、比例、居中、旋转、CTB）经 `Plotter.RenderPreview` 渲染出预览图，弹出统一预览窗：
- 左侧缩略图可点选，右侧大图放大查看，上一张 / 下一张翻页；
- 顶部显示每张的图号图名、比例、是否居中、是否旋转；
- 【确认打印】才开始批量输出，【取消】直接退出，一张 PDF 都不生成。
- 「打印设置」里新增开关**「打印前先预览」**（默认开）；关掉则一键直出。预览渲染失败只留占位提示，不阻断打印。

预览同样依赖 `PublishToWeb PNG.pc3`；预览图存在系统临时目录 `MPlotPrintPreview`，不写进你的输出目录。

### 本轮新增：块文字全类型读取 + 图框型完整复刻（用户实测反馈）

**1. 「框选区域里没有文字」根因与修复**：旧的文字扫描用 `SelectCrossingWindow("TEXT,MTEXT")`，只能选中模型空间顶层的单行/多行文字。而天正图框的图号/图名大量是**块属性（ATTRIB）、图块内嵌文字、动态块文字、天正自定义文字、标注文字**——一个都选不中，所以框在字上也报「没有文字」。现改为遍历 + 递归块定义：块属性直接读 `AttributeReference`，块内文字用 `BlockTransform` 矩阵换算到 WCS 坐标，嵌套块逐层递归（限深防循环），天正/标注用反射兜底取字。点取录入和批打识别走同一套扫描，块文字从此可读。

**2. 图框型完整复刻（对齐 M-BatchPlot「批量打印到文件(图框型)」）**：执行命令后流程变为：
1. **框选打印范围**——可连续框多个图框区域，只识别范围内的图框；直接回车 = 扫描全图。
2. **可编辑清单**——弹出列表窗，双击单元格改图号/图名/版次/纸张/比例/文件名；支持批量应用纸张与比例、上移/下移、删除选中。
3. **预览 / 图号重排 / 纸张统计 / 导出目录 CSV**（UTF-8 BOM，Excel 双击可开）、**重复图号与文件名自动标红**。
4. 【确认打印】才把修改后的清单交回打印流水线，沿用同一套居中/留白/CTB/合并逻辑。

命令仍是 `MP_FRAME`、菜单仍是 3 项，未新增命令或菜单项。

### 本轮新增：显示名改 K-批量打印 + 录入交互修复 + 行内删除 + 目录图框大小（用户实测反馈）

**1. 显示名改为「K-批量打印」**：只改显示层——菜单标题、启动/异常日志、弹窗标题、结果 TXT 抬头、安装/卸载提示、`PackageContents.xml` 的 `Name`/`AppDescription`、`AssemblyTitle` 全部同步；**DLL 名 `MPlot.dll`、bundle 目录 `MPlot.bundle`、命名空间 `MPlot`、命令 `MP_*` 一律不变**（只改显示名，不动内部标识，避免部署路径与命令失效）。

**2. 录入新图框交互修复**：图框库主窗口在 CAD 里是 `ShowModalDialog` 模态窗，点【录入新图框】时它仍挡在绘图区前面，导致「窗口不能移动、没法在图上选图框/框文字」。现改为：点录入先 `Hide()` 主窗口，录入结束（含取消/异常）在 `finally` 里 `Show()+BringToFront()` 恢复；录入窗及候选文字窗、复制窗、拾取 Tag 窗全部由 `FixedDialog` 改为 `Sizable`（可移动可缩放），并把 `ShowDialog()` 改为 `ShowDialog(this)` 修正模态父窗口，弹窗不会再跑到 CAD 主窗后面。

**3. 图框型清单行内删除**：确认清单每行新增独立「删」按钮（`DataGridViewButtonColumn` + `C_DEL`），单击即删该行，无需先选中整行；原「删除选中」按钮保留（可批量删）。

**4. 图纸目录含「图框大小」列**：目录 CSV 表头在「版次」后新增「图框大小」（取纸张代号 A0/A1/A2/A3/A4…，与命名模板的「图框大小」同源），`Catalog.Row` 与 CAD 原生 TABLE 列宽数组同步；图框型「导出目录CSV」的「纸张」列也统一改名为「图框大小」。

**5. 图框库「删除」列从软标记改为真正删除**：原先勾选只在 `Deleted` 上打标（`Active` 过滤不参与批打），条目仍留在库里、界面照常显示，越积越多分不清。现改为：勾选后点【确定/开始批打/生成图纸目录】，通过 `RemoveDeletedFrames()` **真正从 `_lib.Frames` 移除**、重排序号并刷新表格，界面不再显示；删除不可逆，弹确认框并列出将删的图框块名（前 8 条）供核对，默认选「否」防误删；点「取消勾选」则不删。表格上方说明同步改为新语义。

只编译不部署（不改动任何 CAD / 系统目录）、按本机 DLL 核对 API：

```
在 PowerShell 窗口执行：
    .\tools\install.ps1 -CompileOnly
    .\tools\verify_acad_api.ps1
    python .\tools\static_check.py
    .\tools\selftest\run.ps1
```

`src\MPlot.csproj` 里 `AcadDir` 已按本机填好，目标框架 `v4.0`、C# 5；VS 打开 `MPlot.sln` 可直接 Release 生成，`Bundle\deploy.bat` 也是等价入口。命令行等价：

```
msbuild MPlot.sln /p:Configuration=Release /p:AcadDir="D:\Program Files (x86)\Autodesk\AutoCAD 2014"
```

| 现象 | 原因 / 处理 |
|---|---|
| 「没找到 AutoCAD 安装目录」 | 注册表和常见盘符都没扫到 `acdbmgd.dll`。用 `-AcadDir` 指定 |
| UAC 点「否」/ 没有管理员权限 | 自动退回用户目录并自动写 `TRUSTEDPATHS`，无需手工补做 |
| 「复制 MPlot.dll 失败」 | CAD 正开着、旧 DLL 被占用。**完全退出天正/AutoCAD** 再重跑 |
| 「C# 编译失败」 | 完整报错在 `install.log`（已开 `/utf8output`，中文不乱码）。本机 2014 已编译通过；换版本若报 `error CS...`，先跑 `verify_acad_api.ps1` 看哪个 API 不存在 |
| 「需要 .NET 8 SDK」 | 只应在 AutoCAD 2025+ 出现。若 2014 出现这条，说明版本识别被改坏了，检查 `Get-Major` 是否仍用 `FileMajorPart` |
| 装完重启没有菜单 | ① `MENUBAR`→`1`；② `APPAUTOLOAD`→`1`；③ `MP_MENU` 重建或直接 `MP_FRAME`；④ 看 `%APPDATA%\MPlot\logs` |

---

## 5. 目录里有什么

```
MPlot/
├─ 一键安装.bat                    ★ 唯一需要双击的文件
├─ 卸载.bat                        停用（只改名，不删文件）
├─ install.log                     安装日志（UTF-8）
├─ MPlot.sln / README.md
├─ tools/
│  ├─ install.ps1                  ★ 安装器：找 CAD → 编译 → 部署 → 写信任位置
│  ├─ uninstall.ps1                停用逻辑
│  ├─ verify_acad_api.ps1          ★ 按本机 DLL 反射核对每个 API
│  ├─ static_check.py              零依赖静态自检（含防回退规则）
│  └─ selftest/                    ★ 纯逻辑实测（脱离 CAD 真跑）
│     ├─ run.ps1                    编排：生成 PDF → 编译 → 断言 → 校验
│     ├─ Selftest.cs                21 条断言 + 测试替身
│     └─ make_pdfs.py               标准单页 PDF 生成器 + 合并产物校验器
├─ Bundle/
│  ├─ deploy.bat                    等价入口，转发到 install.ps1
│  └─ MPlot.bundle/PackageContents.xml   手工部署参考模板（安装器按版本重写）
├─ build/MPlot.dll                 ★ 编译产物（本机已生成 169.5 KB）
└─ src/
   ├─ MPlot.csproj                 2013–2024 / .NET Framework 4.0 工程
   ├─ MPlot.Net8.csproj            2025+ / .NET 8 工程（实验）
   ├─ MiniJson.cs                  零依赖 JSON
   ├─ Models.cs                    配置 / 图框库 / 纸张预设 / 枚举
   ├─ Logger.cs                    %APPDATA%\MPlot\logs 日志
   ├─ AcadUtil.cs                  UCS→DCS、块范围、设备列表、文件名清洗
   ├─ AttrReader.cs                属性 Tag 启发式识别 + 标题栏文字兜底
   ├─ PaperMatcher.cs              PC3 纸张名解析 mm 尺寸 + 最优选纸 + 比例反算
   ├─ FrameScanner.cs              图框扫描 / 去重 / 排序 / 命名 / 矩形兜底
   ├─ Naming.cs                    命名模板（默认图号_图名_图框大小）/ 符号自定义 / 空值处理
   ├─ Plotter.cs                   PlotEngine 封装（按本机 2014 签名校准）
   ├─ BuiltInPdfMerge.cs           ★ 零第三方依赖的 PDF 合并器
   ├─ PdfMerger.cs                 内置合并 → Ghostscript →（可选）PdfSharp
   ├─ Catalog.cs                   CSV(UTF-8 BOM) + CAD 原生 TABLE
   ├─ BatchEngine.cs               通用型 / 图框型两条打印链路
   ├─ MenuBuilder.cs               顶部菜单栏（COM 后期绑定）
   ├─ Commands.cs                  IExtensionApplication + 12 个命令
   └─ UI/
      ├─ Forms.cs                  进度/结果/设置/辅助/帮助/纸张等窗口
      └─ FrameLibraryForm.cs       三标签页主窗口 + 录入新图框（框选识别）
```

配置落盘：`%APPDATA%\MPlot\frames.json`（图框库）、`config.json`（全局设置）、`logs\`。输出默认在 DWG 同目录 `PDF输出\`。

### 依赖清单

| 依赖 | 必需？ | 说明 |
|---|---|---|
| `acdbmgd.dll` / `acmgd.dll` / `accoremgd.dll` | **必需** | AutoCAD 自带。安装器只**引用**，绝不复制进 bundle（复制会让 CAD 加载两份 acdbmgd 直接崩） |
| .NET Framework 4.x 的 C# 编译器 | **必需** | Windows 自带；**Win7 SP1 原生 C# 4 编译器即可**，不需要 VS / SDK |
| Windows 7 SP1（32 / 64 位） | **支持** | 安装器只用 PowerShell 2.0 语法、自动回退 32 位 Framework 编译器；不写 `OS="Win64"`，32 位 CAD 也能加载 |
| Ghostscript | 可选 | 仅内置合并器碰到对象流/加密/增量更新 PDF 时才需要 |
| PdfSharp | **默认不用** | 可选通道，需在 VS 里手动启用 NuGet |
| Visual Studio / .NET SDK / 网络 | **不需要** | 2013–2024 全程离线 |

**PDF 合并默认零依赖**：`BuiltInPdfMerge.cs` 重建页树与交叉引用表、原样复制流字节，写完回读自检页数；不认识的结构明确报错并切 GS，所有通道失败时**单页 PDF 一个不丢**。

---

## 6. 菜单与命令（一字不差）

| # | 菜单项 | 命令 | 干了什么 |
|---|---|---|---|
| 1 | 批打到文件 (通用型) | `MP_GENERAL` | 遍历所有布局，沿用每个布局自己的页面设置 |
| 2 | **批打到文件 (图框型)** | `MP_FRAME` | **打开图框信息库管理窗口**（核心入口） |
| 3 | 打印设置 | `MP_PRINTSET` | PC3、CTB、颜色、偏移、线宽、居中、比例、默认打印机、份数 |
| 4 | 一键卸载… | `MP_UNINSTALL` | 移除菜单、停用自动加载、清理受信任位置，DLL 安排下次开机删除 |
| — | （附加） | `MP_MENU` | 重建顶部菜单，不显示在菜单里 |

菜单已按最终要求精简为 **4 项**：顶部「K-批量打印」，下拉只有通用型批打、图框型批打、打印设置、一键卸载…。单张打印、两种纸张批打、其余设置、辅助功能、帮助、注册、关于已从菜单**和命令注册**中一并移除。菜单万一没挂上（精简 CUI / 权限受限），4 个功能照常可用，直接敲命令或 `MP_MENU` 重建。

---

## 7. 主流程：从框选到出图

1. 打开 DWG → 菜单「批打到文件 (图框型)」。
2. **图框信息录入**页 →「录入新图框」：
   - 「**框选图框（自动识别）**」→ 回到 CAD 框选一个图框（`StartUserInteraction`，模态窗口不挡 CAD 交互）。
   - 自动抓有效块名（动态块也认）、列出全部属性及相对位置，按「Tag 关键词 + 值形态 + 是否在标题栏」打分，预填 `图号 (A)` / `版次 (B)` / `图名 (C)` 和标题栏方位。
   - 自动按「图框实测宽 ÷ 常见出图比例」反推标称幅面（1:100 画的 A1 图框也认成 A1）。
   - 置信度低会提示人工核对；双击属性行 Tag 可直接填到 A/B/C；也可纯手动输块名，支持 `TK_*` 通配。
3. 每条配置：纸张（A0–A4 / A0+1/4 … A3+3/4 加长 / 自定义宽高）、排序优先级、命名模板、三个 Tag、是否读取、比例覆盖、备注。
4. 底部按钮按清单保留 **录入新图框 / 导出设置 / 导入设置 / 确定 / 取消**，另补 **「开始批打 PDF」「生成图纸目录」**。整套库导出成 JSON，换项目导入复用。
5. 「开始批打 PDF」→ 扫描 → 排序 → 逐张打印 → 合并 → 目录 → 结果窗口。

**目录生成配置**页控制排序（优先级 / 图号 / 布局 / 位置）、CSV、DWG 内 TABLE、PDF 合并；**其它**页控制命名符号映射、扫描兜底、去重阈值、窗口外扩、单位换算。

命名模板默认 **`图号_图名_图框大小`**（图框大小取纸张代号 A1/A2/A3…），兼容老模板 `A_C_B` / `A_C_T`（版次）与 `{A}-{C}({B})` 花括号写法；空字段整段丢弃。CSV 字段固定 **序号、图号、图名、版次、PDF 文件名**，UTF-8 BOM，顺序与出图顺序一致。

### 本轮新增的三个能力（按你的要求）

1. **默认命名改成「图号_图名_图框大小」**：图框大小取图框库里「对应纸张」的代号（A1/A2/A3…）。老配置里还是 `A_C_B` 的会自动升级到新默认；你手动改过的自定义模板原样保留。老模板仍可用。
2. **图框录入支持「点取图号 / 图名范围」**：录入新图框窗口的「③-b」区，先点【框选图框】，再点【点取图号范围】/【点取图名范围】，在图上框出文字所在的小区域。区域按**相对图框的归一化坐标**存进图框库；批打时每张图按同一相对位置读当前图的文字——所以每张图纸号/图名各不相同、不会撞名。适用于块属性、普通 TEXT/MTEXT、天正自定义文字三种形式；读不到会提示核对位置。【清除范围】可撤销。
3. **通用型 / 图框型都能「手动框选」批量打印**：点这两个菜单项会先弹「选择批打方式」——选「手动框选」进入框选窗口：纸张默认 **A3**（可改成 A0–A4 / 加长 / 自定义），点【在图上框选范围】后可**连续框多个**矩形（每框一个回车确认，全部框完再回车结束），图号/图名自动从每个框选区域读出、可在表格里双击补填/修改，点【开始打印】一次出多张 PDF。选「自动扫描」则维持原来的按布局 / 图框库批打。

---

## 8. 几个真正容易翻车的地方（已处理）

| 坑 | 处理方式 |
|---|---|
| **天正图框在模型里按出图比例放大**：1:100 出 A1，实际 84100×59400 | 默认 `AutoFromFrame`：比例 = 图框实宽 ÷ 库内标称纸宽 → 自动 1:100；分母不用实际选中的 PC3 纸张，避免选到大一号纸把图放大 |
| `SetClosestMediaName()` 官方文档标 **not implemented** | 不用。读 PC3 canonical media 名（如 `ISO_A1_(841.00_x_594.00_MM)`）正则解析 mm，挑「能装下且余量最小」的纸，英寸自动换算 |
| `SetPlotWindowArea` 与 `SetPlotType` 顺序反了会报错 | 严格先窗口、后类型 |
| 框选坐标在 WCS，打印窗口要 **DCS** | P/Invoke `acedTrans`（本机 `acad.exe` 导出表已确认）做 WCS→DCS；不可用退回 WCS 并写日志 |
| `GeometricExtents` 把会签栏、框外附注算进窗口 | 优先「块定义几何 × 块变换」求外框；失败才退回参照 extents |
| 动态块 `Name` 是 `*U123` 匿名名 | 走 `DynamicBlockTableRecord` 拿真实块名 |
| AutoCAD 2015+ 默认 `MENUBAR=0` | 初始化设 `MENUBAR=1`；Initialize 阶段 COM 没就绪时，切图纸事件 + `Idle` 事件双重重试；按名字找 `ACAD` 主菜单组（天正会加载自己的组） |
| 未签名 DLL 装错目录不加载 | 装 Program Files 默认受信任位置；退到用户目录时自动写 `TRUSTEDPATHS` |
| 后台打印没落盘就合并，拿到 0 字节 PDF | 默认关 `BACKGROUNDPLOT`；逐张等文件 >0 字节，找不到就在目录里找刚生成的归位 |
| 图框不是块 / 属性被炸成散文字 | 闭合矩形兜底 + 标题栏区域文字兜底（含天正自定义对象反射取字） |
| 打印异常卡在「正在打印」 | `PlotEngine` finally 逆序收尾 |
| 图号含 `/ \ :` 或两张图同名 | 非法字符清洗 + `(2)(3)` 后缀；不用 `GetFileNameWithoutExtension` |
| 图框嵌套 / 重叠重复出图 | 交叠面积 ÷ 较小者面积去重，阈值可调 |
| 换版本时可选 validator 签名不同 | 旋转 / 原点 / 纸单位走反射调用，方法不存在安静跳过；核心链路强类型 |
| **本机 2014 真实 API 与文档不一致** | `verify_acad_api.ps1` 逐项核对（PASS 126 / FAIL 0），全部按实际程序集落地 |

---

## 9. 我替你补的东西（你说「还有我还没想到的」）

- **一键安装器**：自动找 CAD、按程序集版本识别、现场编译、自动提权、自动写受信任位置、旧版本先备份、全程日志。
- **按本机 DLL 的 API 核对脚本** + **只编译不部署的验证模式**，换版本能立刻定位哪个 API 不存在。
- **零依赖 PDF 合并**：不需要 Ghostscript / NuGet / 联网也能出「图纸合集.pdf」。
- 图框库导入/导出按项目复用，首次运行自带天正常见块名示例库。
- 表格补第 9 列「**图名 (C)**」——命名模板里有 C，不登记就没法出 `图号_图名_图框大小`。
- 颜色输出快捷预设（彩色 / 黑白 / 灰度），自动填 CTB。
- 目录可只生成不打 PDF；图框信息库「其它」页能**预演本图全部文件名**；完整明细 CSV（块名/纸张/实测尺寸/比例/信息来源/状态）。
- 逐张结果窗口 + `批打结果.txt` + 会话日志。
- 排序支持优先级/图号/布局/位置，按图号用数字感知排序（A-2 排在 A-10 前）。
- 通用型保留每个布局自己的页面设置；纸张旧版沿用旧设置，新版才用统一参数——两个菜单行为不同，不是空壳。
- CSV BOM、GS 临时响应文件、合并产物空文件校验、合并失败保留单页。
- 所有删除动作默认关闭；卸载只改名不删文件。

---

## 10. 已知边界（不藏着）

| 边界 | 现状 / 建议 |
|---|---|
| **编译与安装已在本机真验证** | 按本机 `acdbmgd/acmgd/accoremgd` 真编译通过（169.5 KB）、API 反射核对 PASS 126 / FAIL 0、已部署并自动加信任。**唯一没做的是打开天正窗口看菜单** —— 需要你在 GUI 里启动天正 |
| AutoCAD 2025+ | 破坏二进制兼容，net8 工程属实验通道，需本机 .NET 8 SDK |
| 旋转 90° 的图框 | 自动判竖向设 `PlotRotation.Degrees090`，但窗口仍是轴对齐包围盒；要求零误差的老图建议先转正 |
| PC3 里没有自定义/加长纸张 | 选最接近的大一号纸，内容居中留白，比例按标称尺寸算不失真 |
| 内置 PDF 合并 | 针对经典 xref 表、未加密、无对象流的 PDF（DWG To PDF.pc3 输出即是）；遇 xref stream / ObjStm / 加密会报错切 GS，不静默产出坏文件 |
| 天正代理对象 / 损坏实体 | 取包围盒失败会跳过并在结果里点名，不崩整批 |
| 菜单 COM 通道 | 极少数精简 CUI / 权限受限环境创建不了菜单，命令行仍可用；截图里的图标/分隔线待补 |
| 份数 | 纸张打印按份数循环调用打印引擎 |

---

## 11. 故障速查

| 现象 | 先看哪里 |
|---|---|
| 装完没有菜单 | `MENUBAR`→`1`；`APPAUTOLOAD`→`1`；`MP_MENU`；或直接 `MP_FRAME`；再看 `install.log` |
| 弹「文件加载 - 安全问题」 | 用户目录安装时已自动写 `TRUSTEDPATHS`，一般不会出现；万一弹出点一次【加载】，或以管理员重跑装到 Program Files |
| 一张图都没扫到 | 图框信息库「其它」→ 检查未录入图框，核对块名；开「矩形图框兜底」 |
| 图号/图名是空的 | 核对三个 Tag；开「标题栏文字兜底」；看「预演文件名」的信息来源列 |
| 出图比例不对 | 选「自动按图框反算比例」；看明细 CSV 的比例分母是不是 100 |
| PDF 内容偏小 / 留白多 | PC3 没对应纸张选了更大一号；给 PC3 加自定义纸张 |
| 合集没生成 | 看结果窗口失败原因；装 Ghostscript 并在「图框信息库 → 其它 → GS 路径」指定；单页一定还在 |
| 目录 CSV 中文乱码 | 不应发生（写了 UTF-8 BOM）；确认没被另存成 ANSI |
| 卡在「正在打印」 | 重启 CAD；日志找 `BeginDocument` 异常，finally 已做收尾 |

---

## 12. 安全

插件只读当前 DWG 与本机打印配置，不访问网络、不上传数据、不修改图纸已有对象（唯一写入是你勾选后新建的目录 TABLE）。配置与日志都在 `%APPDATA%\MPlot\`。插件不设授权锁，装好即用；项目信息在图框信息库「其它」页登记。

安装器只写入 `ApplicationPlugins\MPlot.bundle`、本工程目录（`build\` / `install.log`），以及用户目录安装时**追加**当前用户 CAD 配置的 `TRUSTEDPATHS`（原值写进 `install.log`，可回滚）；覆盖旧版本前先完整备份，卸载只改名不删除。
