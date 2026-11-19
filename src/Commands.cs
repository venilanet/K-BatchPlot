using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
// 本机 AutoCAD 2014 的 acdbmgd 里也有 Autodesk.AutoCAD.Runtime.Exception，
// 不给 System.Exception 起别名会触发 CS0104；Extents2d 只在这一处用，用别名避免引入整个命名空间。
using Exception = System.Exception;
using Extents2d = Autodesk.AutoCAD.DatabaseServices.Extents2d;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcCore = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(MPlot.Plugin))]
[assembly: CommandClass(typeof(MPlot.Commands))]

namespace MPlot
{
    /// <summary>.bundle 自动加载入口：建配置、挂菜单、提示命令。</summary>
    public sealed class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            try
            {
                FrameLibrary.EnsureDir();
                if (!File.Exists(FrameLibrary.DefaultPath)) FrameLibrary.CreateDefault().Save();
                if (!File.Exists(GlobalConfig.ConfigPath)) GlobalConfig.Current.Save();

                AcadUtil.EnsureMenuBarVisible();
                bool menu = MenuBuilder.EnsureMenu();

                // 打印前预览闸门：由 BatchEngine 在真正出图前回调，用同一套 PlotRequest 渲染。
                BatchEngine.PreviewHook = delegate(List<PlotRequest> reqs, List<SheetItem> sheets, string project)
                {
                    try
                    {
                        using (PrintPreviewForm f = new PrintPreviewForm(reqs, sheets, project))
                        {
                            Commands.ShowForm(f);
                            return f.Confirmed;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("预览窗口异常：" + ex.Message + "（改为直接打印）");
                        return true;
                    }
                };

                // 有些单位用精简 CUI / 权限受限，Initialize 阶段 COM 菜单接口还没就绪，
                // 首次创建会失败。切图纸时重试一次；再挂一个 Idle 兜底，保证「装完重启就有菜单」。
                // lambda 让编译器自行推断委托类型，避免不同版本委托签名差异导致编译失败。
                AcCore.DocumentManager.DocumentActivated += (s, e) =>
                {
                    try { if (!MenuBuilder.IsMenuCreated) MenuBuilder.EnsureMenu(); }
                    catch { }
                };
                try
                {
                    EventInfo idle = typeof(AcCore).GetEvent("Idle");
                    MethodInfo onIdle = typeof(Plugin).GetMethod("OnIdle",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (idle != null && idle.EventHandlerType != null && onIdle != null)
                    {
                        Delegate h = Delegate.CreateDelegate(idle.EventHandlerType, onIdle);
                        idle.AddEventHandler(null, h);
                    }
                }
                catch (Exception ex) { Logger.Verbose("Idle 事件不可用（不影响命令）：" + ex.Message); }

                Logger.Info("K-批量打印 v1.0.0 已加载；菜单=" + menu);
                AcadUtil.Say("K-批量打印 已加载。MP_FRAME 图框型批打 / MP_GENERAL 通用型批打 / MP_PRINTSET 打印设置。");
                if (!menu)
                    AcadUtil.Say("菜单未自动挂上（不影响功能），输入 MP_MENU 可重建，或直接敲命令。");
            }
            catch (Exception ex)
            {
                Logger.Error("插件初始化异常", ex);
                AcadUtil.Say("K-批量打印 初始化异常：" + ex.Message);
            }
        }

        /// <summary>CAD 空闲后重试挂菜单；已挂上就直接短路，不重复插入。</summary>
        private static void OnIdle(object sender, EventArgs e)
        {
            try { if (!MenuBuilder.IsMenuCreated) MenuBuilder.EnsureMenu(); }
            catch { }
        }

        public void Terminate()
        {
            // 进程即将退出，无状态需要释放；菜单保留在 CUI 里，下次启动直接可见。
        }
    }

    /// <summary>菜单里的 4 项 + 1 个重建菜单命令（MP_MENU，不显示在菜单上）。所有入口都可用命令行直接调用。</summary>
    public static class Commands
    {
        // ---------------------------------------------------------------- 对话框工具
        /// <summary>优先用 AutoCAD 的 ShowModalDialog（有正确父窗口），版本差异用反射兜底。</summary>
        public static void ShowForm(Form f)
        {
            f.StartPosition = FormStartPosition.CenterScreen;
            try
            {
                MethodInfo m = typeof(AcApp).GetMethod("ShowModalDialog",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Form) }, null);
                if (m != null) { m.Invoke(null, new object[] { f }); return; }
            }
            catch (Exception ex) { Logger.Verbose("ShowModalDialog 不可用，改用 WinForms：" + ex.Message); }
            f.ShowDialog();
        }

        /// <summary>
        /// AutoCAD .NET API 只能在主线程调用，所以批打同步执行、进度条靠 DoEvents 刷新。
        /// 不 Task.Run / 不 BackgroundWorker —— 那样会在跨线程访问 Database 时直接崩。
        /// </summary>
        public static BatchResult RunWithProgress(string title,
            Func<Action<int, int, string>, Func<bool>, BatchResult> work)
        {
            ProgressForm pf = new ProgressForm(title);
            try
            {
                pf.Show();
                System.Windows.Forms.Application.DoEvents();
                Action<int, int, string> prog = delegate(int cur, int total, string msg)
                {
                    pf.SetProgress(cur, total, msg);
                    System.Windows.Forms.Application.DoEvents();
                };
                Func<bool> canceled = delegate { return pf.CancelRequested; };
                BatchResult r = work(prog, canceled);
                pf.Hide();
                System.Windows.Forms.Application.DoEvents();
                ShowResult(r, title);
                return r;
            }
            finally { pf.Dispose(); }
        }

        private static void ShowResult(BatchResult r, string title)
        {
            if (r == null) return;
            try { using (ResultForm f = new ResultForm(r, title)) ShowForm(f); }
            catch (Exception ex) { Logger.Warn("结果窗口显示失败：" + ex.Message); }
        }

        /// <summary>需要往 DWG 里插入原生 TABLE 时，先在 CAD 里点一个位置（必须在窗口关闭后）。</summary>
        private static Point3d? AskTablePoint()
        {
            GlobalConfig cfg = GlobalConfig.Current;
            if (!cfg.CatalogTable) return null;
            if (!cfg.TableAskForInsertPoint) return Point3d.Origin;
            AcadUtil.Say("请在图纸上点选「图纸目录表格」的插入位置（回车 = 原点 0,0）");
            return AcadUtil.PickPoint("指定图纸目录表格插入点（回车用原点）: ");
        }

        /// <summary>
        /// 询问用自动扫描还是手动框选。返回 true=手动框选，false=自动扫描，null=用户取消。
        /// 通用型 / 图框型两个入口都先过这里，满足「两种都要能手动框选」。
        /// </summary>
        private static bool? AskManualMode()
        {
            Form f = new Form
            {
                Text = "选择批打方式", Font = new System.Drawing.Font("Microsoft YaHei", 9F),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false,
                ClientSize = new System.Drawing.Size(430, 190)
            };
            bool? result = null;
            FlowLayoutPanel p = new FlowLayoutPanel
            { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(14) };
            p.Controls.Add(new Label
            {
                Text = "请选择打印范围来源：", AutoSize = true,
                Font = new System.Drawing.Font("Microsoft YaHei", 9F)
            });
            p.Controls.Add(new Label
            {
                Text = "自动扫描：按布局 / 已录入图框批量打印", AutoSize = true,
                Font = new System.Drawing.Font("Microsoft YaHei", 9F), ForeColor = System.Drawing.Color.Gray
            });
            p.Controls.Add(new Label
            {
                Text = "手动框选：在图上连续框选范围，每张一个 PDF", AutoSize = true,
                Font = new System.Drawing.Font("Microsoft YaHei", 9F), ForeColor = System.Drawing.Color.Gray
            });
            FlowLayoutPanel b = new FlowLayoutPanel
            { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            b.Controls.Add(Ui.B("取消", delegate { f.Close(); }, 80));
            b.Controls.Add(Ui.B("手动框选", delegate { result = true; f.Close(); }, 100));
            b.Controls.Add(Ui.B("自动扫描", delegate { result = false; f.Close(); }, 100));
            f.Controls.Add(p); f.Controls.Add(b);
            using (f) ShowForm(f);
            return result;
        }

        /// <summary>手动框选批量打印的公共流程（通用型 / 图框型共用）。</summary>
        private static void RunManualPick(string title)
        {
            using (ManualPickForm f = new ManualPickForm())
            {
                ShowForm(f);
                if (!f.PrintRequested) return;
                System.Collections.Generic.List<ManualPickItem> picks = f.Items;
                PaperSpec paper = f.Paper;
                RunWithProgress(title, delegate(Action<int, int, string> p, Func<bool> c)
                {
                    return BatchEngine.RunManualBatch(picks, paper, p, c);
                });
            }
        }

        // ================================================================ 1 批打到文件 (通用型)
        [CommandMethod("MP_GENERAL")]
        public static void General()
        {
            try
            {
                bool? manual = AskManualMode();
                if (manual == null) return;
                if (manual.Value) { RunManualPick("批打到文件 (通用型) · 手动框选"); return; }
                Point3d? pt = AskTablePoint();
                RunWithProgress("批打到文件 (通用型)", delegate(Action<int, int, string> p, Func<bool> c)
                {
                    return BatchEngine.RunGeneralBatch(p, c, pt);
                });
            }
            catch (Exception ex) { Logger.Error("通用型批打失败", ex); MessageBox.Show("通用型批打失败：" + ex.Message); }
        }

        /// <summary>
        /// 图框型：让用户框选要打印的图框范围（可连续框多个）。直接回车 = 不框选，扫描全图。
        /// 复刻参考插件「执行命令后选择要打印的图框」。
        /// </summary>
        private static List<Extents2d> PickFrameRegions()
        {
            List<Extents2d> regions = new List<Extents2d>();
            try
            {
                AcadUtil.Say("图框型：请框选要打印的图框范围（可连续框多个，全部框完按回车；直接回车=扫描全图）");
                List<Extents2d> got = AcadUtil.PickRegionsWcs(
                    "框选图框范围 · 第一角点（回车=扫描全图）: ",
                    "框选下一范围 · 第一角点（回车结束）: ", true);
                if (got != null) regions.AddRange(got);
            }
            catch (Exception ex) { Logger.Verbose("框选图框范围已结束：" + ex.Message); }
            return regions;
        }

        // ================================================================ 2 批打到文件 (图框型) ★核心
        [CommandMethod("MP_FRAME")]
        public static void Frame()
        {
            try
            {
                bool? manual = AskManualMode();
                if (manual == null) return;
                if (manual.Value) { RunManualPick("批打到文件 (图框型) · 手动框选"); return; }

                bool startBatch = false, catalogOnly = false;
                FrameLibrary lib = FrameLibrary.Load();
                using (FrameLibraryForm f = new FrameLibraryForm(lib))
                {
                    ShowForm(f);
                    startBatch = f.StartBatchRequested;
                    catalogOnly = f.CatalogOnlyRequested;
                    lib = f.Library;
                }
                if (!startBatch && !catalogOnly) return;   // 取消 / 只保存配置

                lib.Renumber();
                lib.Save();

                if (catalogOnly)
                {
                    GenerateCatalogOnly(lib);
                    return;
                }

                // ---- 复刻参考插件图框型：框选范围 → 扫描范围内图框 → 可编辑清单确认 → 打印 ----
                List<Extents2d> regions = PickFrameRegions();
                var acDoc = AcCore.DocumentManager.MdiActiveDocument;
                if (acDoc == null) return;
                ScanResult scan = FrameScanner.ScanInRegions(acDoc.Database, lib, GlobalConfig.Current, regions);
                if (scan.Warnings.Count > 0)
                    Logger.Warn("图框扫描提示：" + string.Join("；", scan.Warnings.ToArray()));
                if (scan.Sheets.Count == 0)
                {
                    MessageBox.Show("没有找到可打印的图框。\n请确认：①已录入图框信息；②框选范围内确有图框；③或直接回车扫描全图。",
                                    "K-批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                List<SheetItem> confirmed;
                using (FrameBatchForm ff = new FrameBatchForm(scan.Sheets))
                {
                    ShowForm(ff);
                    if (!ff.Confirmed) return;
                    confirmed = ff.Items;
                }

                Point3d? pt = AskTablePoint();
                RunWithProgress("批打到文件 (图框型)", delegate(Action<int, int, string> p, Func<bool> c)
                {
                    return BatchEngine.RunConfirmedSheets(confirmed, pt, p, c);
                });
            }
            catch (Exception ex) { Logger.Error("图框型批打失败", ex); MessageBox.Show("图框型批打失败：" + ex.Message); }
        }

        /// <summary>「生成图纸目录」按钮：只扫描/排序/出目录，不打印 PDF。</summary>
        private static void GenerateCatalogOnly(FrameLibrary lib)
        {
            var doc = AcCore.DocumentManager.MdiActiveDocument;
            if (doc == null) { MessageBox.Show("请先打开一张 DWG"); return; }
            GlobalConfig cfg = GlobalConfig.Current;
            ScanResult scan = FrameScanner.Scan(doc.Database, lib, cfg);
            string dir = cfg.EffectiveOutputDir(doc.Name);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string csv = Path.Combine(dir, string.IsNullOrWhiteSpace(cfg.CatalogCsvName) ? "图纸目录.csv" : cfg.CatalogCsvName);
            bool ok = Catalog.WriteCsv(scan.Sheets, csv);
            string msg = "扫描到 " + scan.Sheets.Count + " 张图纸\r\n目录：" + csv + (ok ? "（已生成）" : "（生成失败）");
            if (cfg.CatalogTable)
            {
                Point3d? pt = AskTablePoint();
                string err;
                bool t = Catalog.CreateTable(scan.Sheets, pt ?? Point3d.Origin, cfg, out err);
                msg += "\r\nDWG 内表格：" + (t ? "已生成" : "失败 " + err);
            }
            if (scan.UnmatchedBlockNames.Count > 0)
                msg += "\r\n\r\n未录入的图框块：" + string.Join("、", scan.UnmatchedBlockNames.ToArray());
            MessageBox.Show(msg, "生成图纸目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ================================================================ 3 打印设置
        [CommandMethod("MP_PRINTSET")]
        public static void PrintSettings()
        {
            try { using (SettingsForm f = new SettingsForm()) ShowForm(f); }
            catch (Exception ex) { Logger.Error("打印设置窗口失败", ex); }
        }

        // ================================================================ 附：重建菜单
        [CommandMethod("MP_MENU")]
        public static void RebuildMenu()
        {
            bool ok = MenuBuilder.RebuildMenu();
            MessageBox.Show(ok ? "菜单已重建：" + MenuBuilder.MenuTitle
                               : "菜单创建失败，可直接输入 MP_GENERAL / MP_FRAME / MP_PRINTSET 使用。",
                            "K-批量打印", MessageBoxButtons.OK,
                            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        // ================================================================ 附：一键卸载
        [CommandMethod("MP_UNINSTALL")]
        public static void Uninstall()
        {
            try
            {
                DialogResult r = MessageBox.Show(
                    "确定要一键卸载「K-批量打印」吗？\n\n"
                    + "将执行：移除顶部菜单；停用自动加载（下次启动 CAD 不再加载）；\n"
                    + "清理受信任位置与按需加载注册表项；主程序 DLL 安排在下次开机时删除。\n"
                    + "图框库、设置与日志（%APPDATA%\\MPlot）会保留，重装后可继续用。\n"
                    + "本次 CAD 会话里命令仍可用，关闭 CAD 后彻底生效。",
                    "一键卸载 K-批量打印", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
                string report = Uninstaller.Uninstall();
                MessageBox.Show(report, "一键卸载 K-批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Logger.Error("一键卸载失败", ex); }
        }
    }
}
