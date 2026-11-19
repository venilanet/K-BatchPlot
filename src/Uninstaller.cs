using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MPlot
{
    /// <summary>
    /// CAD 内一键卸载（MP_UNINSTALL，菜单「打印设置」下方「一键卸载…」）：
    ///   1) 菜单立即从菜单栏与 ACAD 菜单组里移除；
    ///   2) .bundle 安装目录改名停用（下次启动 CAD 不再自动加载），与仓库自带卸载.bat 同一思路；
    ///   3) 安装器追加过的 TRUSTEDPATHS 与注册表按需加载项一并清理；
    ///   4) 被 CAD 占用的 DLL 删不掉，安排在下次开机时删除（MoveFileEx 延迟删除），
    ///      并写 RunOnce 清理批处理兜底；图框库/设置/日志（%APPDATA%\MPlot）是用户数据，不删。
    /// 本次 CAD 会话里命令仍可用，关闭 CAD 后彻底生效；若安装在 Program Files 下且权限不足，
    /// 改名一步会失败，报告里会提示改用卸载.bat（管理员身份运行）或手动删除。
    /// </summary>
    public static class Uninstaller
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);
        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        public static string Uninstall()
        {
            StringBuilder sb = new StringBuilder();

            // 1) 菜单立即移除
            bool menuOk = false;
            try { menuOk = MenuBuilder.RemoveMenu(); } catch { }
            sb.AppendLine(menuOk ? "· 菜单「K-批量打印」已移除。"
                                 : "· 菜单移除失败（下次启动 CAD 时也不会再出现）。");

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
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string disabledDir = bundleDir + ".disabled-" + stamp;
                bool renamed = false;
                try { Directory.Move(bundleDir, disabledDir); renamed = true; }
                catch (Exception ex) { Logger.Warn("卸载改名失败：" + ex.Message); }

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
                    WriteRunOnceCleanup(disabledDir);
                    sb.AppendLine(scheduled ? "· 主程序 DLL 已安排在下次开机时删除，残留目录由开机清理任务删除。"
                                            : "· DLL 重启删除登记失败，关闭 CAD 后请手动删除改名后的目录。");
                }
                else
                {
                    sb.AppendLine("· 安装目录改名失败（可能是 Program Files 权限不足）。");
                    sb.AppendLine("· 请关闭 CAD 后右键「卸载.bat」→ 以管理员身份运行，或手动删除：");
                    sb.AppendLine("  " + bundleDir);
                }

                // 3) 清理 TRUSTEDPATHS 与按需加载项
                int cleaned = CleanTrustedPaths(bundleDir);
                if (cleaned > 0) sb.AppendLine("· 已从 " + cleaned + " 个 CAD 配置的受信任位置里移除安装目录。");
                if (RemoveDemandLoadKey()) sb.AppendLine("· 已清理注册表按需加载项。");
            }

            sb.AppendLine("· 图框库、设置与日志（%APPDATA%\\MPlot）已保留，重装后可继续用。");
            sb.AppendLine("· 本次 CAD 会话里命令仍可用，关闭 CAD 后彻底生效。");
            return sb.ToString();
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

        /// <summary>写 %TEMP%\MPlot_cleanup.bat 并登记 RunOnce：下次登录时自动删掉残留目录（MoveFileEx 的兜底）。</summary>
        private static void WriteRunOnceCleanup(string disabledDir)
        {
            try
            {
                string bat = Path.Combine(Path.GetTempPath(), "MPlot_cleanup.bat");
                File.WriteAllText(bat,
                    "@echo off\r\n"
                    + "rmdir /s /q \"" + disabledDir + "\"\r\n"
                    + "del \"%~f0\"\r\n", new UTF8Encoding(false));
                using (RegistryKey runOnce = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\RunOnce", true))
                {
                    if (runOnce != null) runOnce.SetValue("MPlotCleanup", "\"" + bat + "\"");
                }
            }
            catch (Exception ex) { Logger.Warn("开机清理任务登记失败：" + ex.Message); }
        }

        /// <summary>
        /// 从当前用户全部 AutoCAD 配置的 TRUSTEDPATHS 里移除安装目录（安装器追加过的原样清掉）。
        /// 返回被改动过的配置数。
        /// </summary>
        private static int CleanTrustedPaths(string bundleDir)
        {
            int changed = 0;
            string target = bundleDir.TrimEnd('\\');
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
                                        using (RegistryKey g = profiles.OpenSubKey(profile + @"\General", true))
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
                                                if (string.Equals(t.TrimEnd('\\'), target,
                                                                  StringComparison.OrdinalIgnoreCase))
                                                { removed = true; continue; }
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
            catch { }
            return changed;
        }

        /// <summary>删除注册表按需加载项 HKCU\...\AutoCAD\&lt;版本&gt;\&lt;产品&gt;\Applications\MPlot（若有）。返回是否删掉过。</summary>
        private static bool RemoveDemandLoadKey()
        {
            bool removed = false;
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
                                        if (string.Equals(sub, "MPlot", StringComparison.OrdinalIgnoreCase))
                                        {
                                            try { apps.DeleteSubKeyTree(sub); removed = true; } catch { }
                                        }
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
