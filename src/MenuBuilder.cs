using System;
using System.Collections.Generic;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace MPlot
{
    public sealed class MenuItemDef
    {
        public string Label { get; set; }
        public string Command { get; set; }
        public MenuItemDef(string label, string command) { Label = label; Command = command; }
    }

    /// <summary>
    /// 顶部菜单栏「K-批量打印」，共 4 项：
    ///   批打到文件 (通用型) / 批打到文件 (图框型) / 打印设置 / 一键卸载…。不加分隔符、不加图标。
    ///
    /// 为什么用 COM 的 dynamic 而不是生成 CUIX：
    ///   1) 老批打工具就是往当前菜单组 Add PopupMenu + InsertInMenuBar，行为一致；
    ///   2) 不需要引用 AcCui.dll，也不需要随包带一个二进制 .cuix，避开 AutoCAD 版本差异；
    ///   3) dynamic 走 IDispatch 后期绑定，Interop 类型库版本换了也不会编译失败。
    /// 菜单若因权限/精简 CUI 配置创建失败，4 个命令仍可直接在命令行调用（MP_MENU 可重建菜单）。
    /// </summary>
    public static class MenuBuilder
    {
        public const string MenuTitle = "K-批量打印";

        public static readonly MenuItemDef[] Items = {
            new MenuItemDef("批打到文件 (通用型)",  "MP_GENERAL"),
            new MenuItemDef("批打到文件 (图框型)",  "MP_FRAME"),
            new MenuItemDef("打印设置",             "MP_PRINTSET"),
            new MenuItemDef("一键卸载…",           "MP_UNINSTALL")
        };

        private static bool _ok;

        public static bool IsMenuCreated { get { return _ok; } }

        /// <summary>本会话已一键卸载：Idle/切图/MP_MENU 都不再重建菜单。
        /// 只屏蔽本次 CAD 会话；下次启动 CAD 时是新进程，标志归零（若 .bundle 还在则正常加载）。</summary>
        private static bool _uninstalled;

        public static bool Uninstalled { get { return _uninstalled; } }

        /// <summary>一键卸载时调用：记下本会话已卸载，并把菜单已创建标志清掉。</summary>
        public static void MarkUninstalled()
        {
            _uninstalled = true;
            _ok = false;
        }

        public static bool EnsureMenu()
        {
            if (_ok) return true;
            if (_uninstalled) return false;   // 已卸载：绝不自动重建（否则删完点确定又弹回来）
            try
            {
                AcadUtil.EnsureMenuBarVisible();

                dynamic acad = AcApp.AcadApplication;      // COM AcadApplication
                dynamic bar = acad.MenuBar;

                // 菜单可能已经挂在菜单栏上（CUI 会记住它）。但绝不能因为「已存在」就跳过：
                // 旧版本写入的宏是 ^C^C_MP_FRAME，在 AutoCAD 2014 + 天正下会被当成整串
                // 命令名报「未知命令」。所以每次启动都要用当前代码重建条目、刷新宏，
                // 只在「不在菜单栏」时才插入，避免每开一张图多一个菜单。
                bool inMenuBar = false;
                ForEachComItem((object)bar, delegate(object o)
                {
                    dynamic pop = o;
                    if (SameTitle((string)pop.Name)) inMenuBar = true;
                });

                // 天正会额外加载自己的菜单组，别盲用 Item(0)：先按名字找 ACAD 主组。
                dynamic group = null;
                ForEachComItem((object)acad.MenuGroups, delegate(object o)
                {
                    dynamic g = o;
                    if (group == null && string.Equals((string)g.Name, "ACAD", StringComparison.OrdinalIgnoreCase))
                        group = o;
                });
                if (group == null) group = acad.MenuGroups.Item(0);
                dynamic menus = group.Menus;

                // 同组里可能已有同名 PopupMenu（上次插入过但没挂上菜单栏）
                dynamic newPop = null;
                ForEachComItem((object)menus, delegate(object o)
                {
                    dynamic p = o;
                    if (newPop == null && SameTitle((string)p.Name)) newPop = o;
                });
                if (newPop == null) newPop = menus.Add(MenuTitle);

                // 清掉旧条目再按清单重建，保证旧的错误宏一条不残留。
                // COM 集合索引可能是 0-based 也可能是 1-based，两套都试。
                try
                {
                    int n = (int)newPop.Count;
                    if (ClearItems(newPop, n, 1)) { }
                    else ClearItems(newPop, n, 0);
                }
                catch { }

                // ★ 关键修复：菜单宏绝不能带 ^C^C_ 前缀。
                // AutoCAD 2014 + 天正里，通过 COM 写进去的宏若含 ^C^C_，
                // CAD 会把整串当成命令名，点击就报「未知命令 ^C^C_MP_FRAME」（实测截图）。
                // 直接写纯命令名（末尾空格用于提交），点菜单 = 命令行敲 MP_FRAME。
                for (int i = 0; i < Items.Length; i++)
                    newPop.AddMenuItem(i + 1, Items[i].Label, Items[i].Command + " ");

                if (!inMenuBar) newPop.InsertInMenuBar(bar.Count + 1);
                _ok = true;
                Logger.Info("菜单栏已挂载：" + MenuTitle);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("菜单栏自动创建失败（命令仍可直接输入）：" + ex.Message);
                return false;
            }
        }

        /// <summary>按指定基准删除 PopupMenu 的全部条目；成功返回 true，索引不兼容返回 false。</summary>
        private static bool ClearItems(dynamic pop, int count, int baseIndex)
        {
            try
            {
                for (int k = count - 1; k >= 0; k--) pop.Item(k + baseIndex).Delete();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// COM 集合的 Item 索引在不同 AutoCAD / 天正版本里可能是 0-based 也可能是 1-based。
        /// 两套都试一遍：原来只要第一次 Item 抛异常，整个查找就被 catch 吞掉，
        /// 结果要么重复插入菜单、要么找不到 ACAD 主菜单组。
        /// </summary>
        // 形参必须是 object 而不是 dynamic：dynamic 形参会让整个调用变成动态调度，
        // C# 5 下 lambda/匿名方法不能作为动态调度的实参（CS1977，真编译时抓到）。
        private static void ForEachComItem(object collectionObject, Action<object> action)
        {
            dynamic collection = collectionObject;
            for (int b = 0; b <= 1; b++)
            {
                try
                {
                    int count = (int)collection.Count;
                    for (int i = 0; i < count; i++) action((object)collection.Item(i + b));
                    return;
                }
                catch { /* 换另一套索引基准重试 */ }
            }
        }

        private static bool SameTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string a = s.Replace("&", "").Replace(" ", "");
            string b = MenuTitle.Replace(" ", "");
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>手动重建菜单（辅助功能里可用；命令行 MP_MENU 也能调）。</summary>
        public static bool RebuildMenu()
        {
            _ok = false;
            return EnsureMenu();
        }

        /// <summary>
        /// 一键卸载时从菜单栏与 ACAD 菜单组里彻底移除本插件的弹出菜单。
        /// 先找对象、再统一删除，避免边遍历边删导致 COM 集合索引错位。
        /// </summary>
        public static bool RemoveMenu()
        {
            try
            {
                dynamic acad = AcApp.AcadApplication;
                dynamic bar = acad.MenuBar;

                object barPop = null;
                ForEachComItem((object)bar, delegate(object o)
                {
                    try { if (barPop == null && SameTitle((string)((dynamic)o).Name)) barPop = o; } catch { }
                });
                if (barPop != null)
                {
                    try { ((dynamic)barPop).RemoveFromMenuBar(); } catch { }
                }

                dynamic group = null;
                ForEachComItem((object)acad.MenuGroups, delegate(object o)
                {
                    dynamic g = o;
                    if (group == null && string.Equals((string)g.Name, "ACAD", StringComparison.OrdinalIgnoreCase))
                        group = o;
                });
                if (group == null) group = acad.MenuGroups.Item(0);
                dynamic menus = group.Menus;

                object menuPop = null;
                ForEachComItem((object)menus, delegate(object o)
                {
                    try { if (menuPop == null && SameTitle((string)((dynamic)o).Name)) menuPop = o; } catch { }
                });
                if (menuPop != null)
                {
                    try { ((dynamic)menuPop).Delete(); } catch { }
                }

                _ok = false;
                Logger.Info("菜单栏已移除：" + MenuTitle);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("菜单栏移除失败：" + ex.Message);
                return false;
            }
        }

        public static string CommandListText()
        {
            List<string> lines = new List<string>();
            foreach (MenuItemDef d in Items) lines.Add("  " + d.Label.PadRight(24) + " → " + d.Command);
            lines.Add("  (重建菜单)                  → MP_MENU");
            return string.Join(Environment.NewLine, lines.ToArray());
        }
    }
}
