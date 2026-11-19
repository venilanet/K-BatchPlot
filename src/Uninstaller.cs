using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MPlot
{
    /// <summary>
    /// CAD 内一键卸载（MP_UNINSTALL，菜单「打印设置」下方「一键卸载…」）：
    ///   1) 菜单立即从菜单栏与 ACAD 菜单组里移除（注意：菜单可能已保存在 CUI 文件里，
    ///      重启后可能复活，报告里会提醒用户在 CUI 里删除并保存工作空间）；
    ///   2) .bundle 安装目录改名停用（下次启动 CAD 不再自动加载），与仓库自带卸载.bat 同一思路；
    ///   3) 安装器追加过的 TRUSTEDPATHS 一并清理（两个键、两个值都查，见 CleanTrustedPaths）；
    ///      注册表按需加载项只有 LOADER 指向本 DLL 时才删（M2：本仓库安装器从不创建该键，
    ///      它只可能属于另一套 MPlot，不得越界删除）；
    ///   4) 被 CAD 占用的 DLL 删不掉，安排在下次开机时删除（MoveFileEx 延迟删除），
    ///      并写 RunOnce 清理批处理兜底；图框库/设置/日志（%APPDATA%\MPlot）是用户数据，不删。
    /// 本次 CAD 会话里命令仍可用，关闭 CAD 后彻底生效；若安装在 Program Files 下且权限不足，
    /// 改名一步会失败，报告里会提示改用卸载.bat（管理员身份运行）或手动删除。
    /// M6：MoveFileEx 重启删除与 RunOnce 清理在非提权 CAD 下基本走不通，流程开头会先判断
    /// 当前进程是否提权并如实报告，不给兑现不了的承诺。
    /// </summary>
    public static class Uninstaller
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);
        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        public static string Uninstall()
        {
            StringBuilder sb = new StringBuilder();

            // M6：先判断当前 CAD 进程是否提权。Program Files 下的卸载在非提权进程里
            // 改名、MoveFileEx、RunOnce 基本都走不通，先把话说在前面。
            bool elevated = false;
            try
            {
                System.Security.Principal.WindowsPrincipal wp =
                    new System.Security.Principal.WindowsPrincipal(
                        System.Security.Principal.WindowsIdentity.GetCurrent());
                elevated = wp.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { }

            // 先立已卸载标志：屏蔽 Idle/切图事件自动重建菜单，否则删完点确定又弹回来。
            MenuBuilder.MarkUninstalled();
            // 1) 菜单立即移除
            bool menuOk = false;
            try { menuOk = MenuBuilder.RemoveMenu(); } catch { }
            sb.AppendLine(menuOk ? "· 菜单「K-批量打印」已移除。"
                                 : "· 菜单移除失败，请关闭 CAD 后在 CUI 里删除「K-批量打印」菜单并保存工作空间。");
            if (menuOk)
            {
                sb.AppendLine("· 注意：菜单可能已保存在 CUI 文件里；若重启 CAD 后它又出现，请在 CUI 里删除并保存工作空间。");
                sb.AppendLine("· 本次会话内菜单不会再自动出现（已屏蔽自动重建，并已写入磁盘标记，重启 CAD 也不重建）。");
            }

            // 2) 定位本 DLL 与 .bundle 安装目录
            string dllPath = "";
            try { dllPath = Assembly.GetExecutingAssembly().Location; } catch { }
            string bundleDir = FindBundleDir(dllPath);
            if (string.IsNullOrEmpty(bundleDir))
            {
                sb.AppendLine("· 未找到 .bundle 安装目录（可能是 NETLOAD 临时加载）。");
                if (!string.IsNullOrEmpty(dllPath))
                    sb.AppendLine("· 请关闭 CAD 后手动删除：" + dllPath);
            }
            else
            {
                if (!elevated)
                {
                    string pf = "";
                    try { pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); } catch { }
                    if (!string.IsNullOrEmpty(pf) &&
                        bundleDir.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine("· 当前 CAD 未以管理员身份运行，Program Files 下的目录可能改名失败；"
                                      + "若失败请改用卸载.bat（管理员身份运行）。");
                }
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string disabledDir = bundleDir + ".disabled-" + stamp;
                bool renamed = false;
                try { Directory.Move(bundleDir, disabledDir); renamed = true; }
                catch (Exception ex) { Logger.Warn("卸载改名失败：" + ex.Message); }

                // 提权重试：Program Files 下的改名在非提权 CAD 里注定失败，改名失败是
                // 「重启后菜单又出现」的头号原因。写一个临时 bat，用 runas 提权跑一次
                // （会弹 UAC，跑之前先在命令行提醒用户点「是」），同步等它跑完再看结果。
                // CAD 已提权时改名失败不是权限问题，不弹无意义的 UAC。
                bool elevatedTried = false;
                if (!renamed && !elevated)
                {
                    elevatedTried = true;
                    AcadUtil.Say("卸载需要管理员权限：即将弹出 UAC 窗口，请点「是」以完成安装目录改名…");
                    if (TryElevatedRename(bundleDir, disabledDir))
                    {
                        renamed = true;
                        Logger.Info("卸载提权改名成功：" + disabledDir);
                    }
                    else
                    {
                        sb.AppendLine("· 提权改名未成功（可能点了 UAC 的「否」，或账号无管理员权限）。");
                    }
                }

                if (renamed)
                {
                    sb.AppendLine("· 安装目录已改名停用，下次启动 CAD 不再加载：");
                    sb.AppendLine("  " + disabledDir);
                    string dllInDisabled = Path.Combine(Path.Combine(disabledDir, "Contents"),
                                                        Path.GetFileName(dllPath));
                    DeleteExcept(disabledDir, dllInDisabled);
                    bool scheduled = false;
                    try { scheduled = MoveFileEx(dllInDisabled, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                    try { MoveFileEx(Path.Combine(disabledDir, "Contents"), null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                    try { MoveFileEx(disabledDir, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                    bool runOnceOk = WriteRunOnceCleanup(disabledDir);
                    sb.AppendLine(scheduled ? "· 主程序 DLL 已安排在下次开机时删除，残留目录由开机清理任务删除。"
                                            : "· DLL 重启删除登记失败，关闭 CAD 后请手动删除改名后的目录。");
                    if (scheduled && !runOnceOk)
                        sb.AppendLine("· 注意：开机清理任务登记失败，残留目录需关闭 CAD 后手动删除。");
                }
                else
                {
                    sb.AppendLine(elevatedTried
                        ? "· 安装目录改名失败（直接改名与提权改名都失败了）。"
                        : "· 安装目录改名失败（可能是 Program Files 权限不足）。");
                    sb.AppendLine("· 请关闭 CAD 后右键「卸载.bat」→ 以管理员身份运行，或手动删除：");
                    sb.AppendLine("  " + bundleDir);
                    sb.AppendLine("· 提示：磁盘已卸载标记仍在，重启 CAD 后菜单不会自动重建"
                                  + "（重装时安装器会自动清除该标记）。");
                }
            }

            // 3) 清理 TRUSTEDPATHS 与按需加载项：无论是否找到 bundle 都执行（L4 教训：
            //    以前只在找到 bundle 的分支里清，NETLOAD 临时加载时整个跳过）。
            //    TRUSTEDPATHS 只清与本次 bundle 相关的两个值；按需加载键只有 LOADER
            //    指向本 DLL 时才删（M2），不得删其他插件的配置。
            int cleaned = CleanTrustedPaths(bundleDir);
            if (cleaned > 0) sb.AppendLine("· 已从 " + cleaned + " 个 CAD 配置的受信任位置里移除安装目录。");
            if (RemoveDemandLoadKey(dllPath)) sb.AppendLine("· 已清理注册表按需加载项。");

            sb.AppendLine("· 图框库、设置与日志（%APPDATA%\\MPlot）已保留，重装后可继续用。");
            sb.AppendLine("· 本次 CAD 会话里命令仍可用，关闭 CAD 后彻底生效。");
            return sb.ToString();
        }

        /// <summary>提权重试改名：把 move 命令写进 %TEMP% 的临时 bat，用 runas 提权跑一次。
        /// 只在 CAD 未提权且直接改名失败时调用。同步等最多 60 秒（UAC 等用户点），
        /// 跑完看 bundle 目录是否还在——在就是失败（点了「否」/无管理员权限/超时），
        /// 不在就是成功。bat 用 GBK 写（路径可能含中文用户名），跑完自删。</summary>
        private static bool TryElevatedRename(string bundleDir, string disabledDir)
        {
            string bat = "";
            try
            {
                bat = Path.Combine(Path.GetTempPath(), "MPlot_elevated_uninstall.bat");
                Encoding batEnc;
                try { batEnc = Encoding.GetEncoding(936); }
                catch { batEnc = new UTF8Encoding(false); }
                File.WriteAllText(bat,
                    "@echo off\r\n"
                    + "move /Y \"" + bundleDir + "\" \"" + disabledDir + "\"\r\n"
                    + "del \"%~f0\"\r\n", batEnc);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c \"" + bat + "\"";
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                if (p != null) p.WaitForExit(60000);
                return !Directory.Exists(bundleDir);
            }
            catch (Exception ex)
            {
                Logger.Warn("提权改名失败：" + ex.Message);
                return false;
            }
            finally
            {
                try { if (!string.IsNullOrEmpty(bat) && File.Exists(bat)) File.Delete(bat); } catch { }
            }
        }

        /// <summary>从 DLL 所在位置向上找 *.bundle 目录（DLL 在 &lt;bundle&gt;\Contents\ 下）。找不到返回 ""。</summary>
        private static string FindBundleDir(string dllPath)
        {
            if (string.IsNullOrEmpty(dllPath)) return "";
            try
            {
                DirectoryInfo d = new DirectoryInfo(Path.GetDirectoryName(dllPath));
                while (d != null)
                {
                    if (d.Name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)) return d.FullName;
                    d = d.Parent;
                }
            }
            catch { }
            return "";
        }

        /// <summary>删掉目录下除 keepFile 外的所有文件与子目录，尽力而为，删不掉的跳过。</summary>
        private static void DeleteExcept(string dir, string keepFile)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(keepFile),
                                      StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(f); } catch { }
                }
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    try
                    {
                        if (Directory.GetFiles(sub, "*", SearchOption.AllDirectories).Length == 0)
                            Directory.Delete(sub, true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>写 %TEMP%\MPlot_cleanup.bat 并登记 RunOnce：下次登录时自动删掉残留目录（MoveFileEx 的兜底）。
        /// M1 教训：bat 内容含中文用户名的完整路径，必须用 GBK（936）写出；以前用 UTF-8 无 BOM，
        /// Win7 的 cmd 按系统 ANSI（GBK）读会把路径读成乱码，rmdir 失败后 bat 又自删，清理任务永久丢失。
        /// 936 不可用时回退 UTF-8 无 BOM。返回是否登记成功。</summary>
        private static bool WriteRunOnceCleanup(string disabledDir)
        {
            try
            {
                string bat = Path.Combine(Path.GetTempPath(), "MPlot_cleanup.bat");
                Encoding batEnc;
                try { batEnc = Encoding.GetEncoding(936); }
                catch { batEnc = new UTF8Encoding(false); }
                File.WriteAllText(bat,
                    "@echo off\r\n"
                    + "rmdir /s /q \"" + disabledDir + "\"\r\n"
                    + "del \"%~f0\"\r\n", batEnc);
                using (RegistryKey runOnce = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\RunOnce", true))
                {
                    if (runOnce != null) runOnce.SetValue("MPlotCleanup", "\"" + bat + "\"");
                }
                return true;
            }
            catch (Exception ex) { Logger.Warn("开机清理任务登记失败：" + ex.Message); return false; }
        }

        /// <summary>
        /// 从当前用户全部 AutoCAD 配置的 TRUSTEDPATHS 里移除安装目录（安装器追加过的原样清掉）。
        /// H1 教训：安装器曾把值写进 Profiles\<配置>\Variables 键、卸载器却去 General 键里找，
        /// 且值一个是父目录、一个是 bundle 目录，两边永远对不上。真机 ground truth（到底哪个键）
        /// 尚未确认（V1），因此两侧都按“两个键 × 两个值”处理：Variables 与 General 都查，
        /// bundle 目录本身（现行安装器写法）与父目录（历史安装器写法）都匹配，哪个键是对的都能清干净。
        /// bundleDir 为空（NETLOAD 临时加载）时直接返回 0，不碰共享的父目录条目（可能属于其他插件）。
        /// 返回被改动过的配置数。
        /// </summary>
        private static int CleanTrustedPaths(string bundleDir)
        {
            int changed = 0;
            string bundle = (bundleDir ?? "").TrimEnd('\\');
            if (string.IsNullOrEmpty(bundle)) return 0;
            string parent = "";
            try { parent = Path.GetDirectoryName(bundle) ?? ""; } catch { }
            parent = parent.TrimEnd('\\');
            string[] targets = (string.IsNullOrEmpty(parent) ||
                                string.Equals(parent, bundle, StringComparison.OrdinalIgnoreCase))
                ? new string[] { bundle }
                : new string[] { bundle, parent };
            try
            {
                using (RegistryKey acad = Registry.CurrentUser.OpenSubKey(@"Software\Autodesk\AutoCAD", true))
                {
                    if (acad == null) return 0;
                    foreach (string ver in acad.GetSubKeyNames())
                    {
                        using (RegistryKey vk = acad.OpenSubKey(ver, true))
                        {
                            if (vk == null) continue;
                            foreach (string prod in vk.GetSubKeyNames())
                            {
                                using (RegistryKey profiles = vk.OpenSubKey(prod + @"\Profiles", true))
                                {
                                    if (profiles == null) continue;
                                    foreach (string profile in profiles.GetSubKeyNames())
                                    {
                                        // 两个键都查：Variables（安装器现行写法）与 General（卸载器旧写法）。
                                        foreach (string sub in new string[] { @"Variables", @"General" })
                                        {
                                            using (RegistryKey g = profiles.OpenSubKey(profile + @"\" + sub, true))
                                            {
                                                if (g == null) continue;
                                                string cur = g.GetValue("TRUSTEDPATHS") as string;
                                                if (string.IsNullOrEmpty(cur)) continue;
                                                string[] parts = cur.Split(new char[] { ';' },
                                                                           StringSplitOptions.RemoveEmptyEntries);
                                                StringBuilder kept = new StringBuilder();
                                                bool removed = false;
                                                foreach (string p in parts)
                                                {
                                                    string t = p.Trim();
                                                    bool hit = false;
                                                    foreach (string tg in targets)
                                                    {
                                                        if (string.Equals(t.TrimEnd('\\'), tg,
                                                                          StringComparison.OrdinalIgnoreCase))
                                                        { hit = true; break; }
                                                    }
                                                    if (hit) { removed = true; continue; }
                                                    if (kept.Length > 0) kept.Append(';');
                                                    kept.Append(t);
                                                }
                                                if (removed)
                                                {
                                                    try { g.SetValue("TRUSTEDPATHS", kept.ToString()); changed++; }
                                                    catch { }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return changed;
        }

        /// <summary>删除注册表按需加载项 HKCU\...\AutoCAD\<版本>\<产品>\Applications\MPlot（若有）。
        /// M2 教训：本仓库安装器从不创建该键，它只可能属于另一套 MPlot；只有当 LOADER 指向本次
        /// 卸载的 DLL 时才删，否则不动（不得删其他插件的注册表配置）。ownDllPath 为空时一律不删。
        /// 返回是否删掉过。</summary>
        private static bool RemoveDemandLoadKey(string ownDllPath)
        {
            bool removed = false;
            string own = (ownDllPath ?? "").Trim();
            try
            {
                using (RegistryKey acad = Registry.CurrentUser.OpenSubKey(@"Software\Autodesk\AutoCAD", true))
                {
                    if (acad == null) return false;
                    foreach (string ver in acad.GetSubKeyNames())
                    {
                        using (RegistryKey vk = acad.OpenSubKey(ver, true))
                        {
                            if (vk == null) continue;
                            foreach (string prod in vk.GetSubKeyNames())
                            {
                                using (RegistryKey apps = vk.OpenSubKey(prod + @"\Applications", true))
                                {
                                    if (apps == null) continue;
                                    foreach (string sub in apps.GetSubKeyNames())
                                    {
                                        if (!string.Equals(sub, "MPlot", StringComparison.OrdinalIgnoreCase)) continue;
                                        bool mine = false;
                                        using (RegistryKey key = apps.OpenSubKey(sub, false))
                                        {
                                            string loader = key == null ? "" : (key.GetValue("LOADER") as string ?? "");
                                            mine = !string.IsNullOrEmpty(own) &&
                                                string.Equals(loader.Trim(), own, StringComparison.OrdinalIgnoreCase);
                                        }
                                        if (!mine) continue;
                                        try { apps.DeleteSubKeyTree(sub); removed = true; } catch { }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return removed;
        }
    }
}
