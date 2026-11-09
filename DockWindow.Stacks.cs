using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;
using GdiIcon = System.Drawing.Icon;
using WinForms = System.Windows.Forms;
using Microsoft.Win32;
using VB = Microsoft.VisualBasic;


namespace Koi
{
    public partial class DockWindow
    {
        // ---------------- 文件堆叠 ----------------

        DockEntry dropMergeTarget; // 拖动悬停时可能合并到的目标文件
        System.Windows.Controls.Primitives.Popup stackPopup; // 文件堆的悬停选择列表
        DockEntry pullFromEntry;   // 正在从堆里往外拖的所属堆
        string pullPath;           // 正在拖出的文件路径
        Point pullStart;
        Point pullLast;            // 拖出过程中的最后光标位置（窗口坐标）
        bool pullActive;           // 已进入拖出状态
        UIElement pullCaptureOwner;

        // 只有"普通文件"可堆叠：排除目录、exe/lnk/url/bat/cmd（应用类）、Store 应用
        static bool ComputeStackable(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.StartsWith("shell:")) return false;
                if (Directory.Exists(path)) return false;
                if (!File.Exists(path)) return false;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                return ext != ".exe" && ext != ".lnk" && ext != ".url" && ext != ".bat" && ext != ".cmd";
            }
            catch { return false; }
        }

        bool IsStack(DockEntry en)
        {
            return StackService.IsStack(en.Cfg);
        }

        // 堆叠视觉里的"纸片"（描边圆角矩形，倾斜摆放）
        System.Windows.Shapes.Path MakeStackPaper(double size)
        {
            System.Windows.Shapes.Path p = new System.Windows.Shapes.Path();
            p.Data = new RectangleGeometry(new Rect(0, 0, size, size), 8, 8);
            p.Fill = new SolidColorBrush(Color.FromArgb(0x66, 0x2A, 0x2E, 0x36));
            p.Stroke = normalBorder;
            p.StrokeThickness = 1;
            p.Width = size;
            p.Height = size;
            p.Stretch = Stretch.None;
            p.IsHitTestVisible = false;
            return p;
        }

        // 按条目的堆叠状态切换"叠纸"显示
        void UpdateStackVisual(DockEntry en)
        {
            if (en.StackBack1 == null) return;
            bool isStack = IsStack(en);
            en.StackBack1.Visibility = isStack ? Visibility.Visible : Visibility.Collapsed;
            en.StackBack2.Visibility = isStack ? Visibility.Visible : Visibility.Collapsed;
        }

        // 把拖来的文件并入目标（目标可能是普通文件或已有文件堆）
        bool MergeIntoStack(DockEntry target, ItemCfg dragged)
        {
            if (!StackService.Merge(target.Cfg, dragged)) return false;
            if (target.Img.Opacity < 1) target.Img.Opacity = 1; // 撤掉拖动高亮
            UpdateStackVisual(target);
            return true;
        }

        void Unstack(DockEntry en)
        {
            List<ItemCfg> members = StackService.Unstack(en.Cfg);
            if (members.Count == 0) return;
            int at = entries.IndexOf(en);
            if (hoveredEntry == en)
            {
                hoveredEntry = null;
                nameLabel.Visibility = Visibility.Collapsed;
            }
            if (dragEntry == en) EndReorder(en);
            reorderSource = null;
            CancelStackPull();
            CloseFolderFlyout();
            row.Children.Remove(en.Host);
            entries.Remove(en);
            UpdateHint();
            for (int i = 0; i < members.Count; i++)
                AddEntry(members[i], true, at + i, false);
        }

        // 悬停文件堆弹出文件选择列表（点击打开对应文件）
        void ShowStackPopup(DockEntry en)
        {
            if (pullFromEntry != null) return; // 拖出跨过其他图标时不重建弹层或取消正在进行的手势
            List<ItemCfg> list = StackService.GetMembers(en.Cfg);
            if (list.Count == 0) return;
            CancelStackPull();
            CloseFolderFlyout();

            StackPanel listPanel = new StackPanel { MinWidth = 230 };
            foreach (ItemCfg member in list)
            {
                string p = member.Path; // 闭包捕获
                Border rowB = new Border
                {
                    Padding = new Thickness(8, 5, 10, 5),
                    CornerRadius = new CornerRadius(6),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand
                };
                Grid g = new Grid();
                ColumnDefinition c0 = new ColumnDefinition { Width = GridLength.Auto };
                ColumnDefinition c1 = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
                g.ColumnDefinitions.Add(c0);
                g.ColumnDefinitions.Add(c1);
                Image ic = new Image
                {
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(0, 0, 8, 0),
                    Source = ShellIcons.ResolveIcon(member) ?? ShellIcons.DefaultIcon()
                };
                Grid.SetColumn(ic, 0);
                TextBlock t = new TextBlock
                {
                    Text = StackService.DisplayName(member),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xE8, 0xEA, 0xED)),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(t, 1);
                g.Children.Add(ic);
                g.Children.Add(t);
                rowB.Child = g;
                rowB.MouseEnter += delegate { rowB.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)); };
                rowB.MouseLeave += delegate { rowB.Background = Brushes.Transparent; };
                // 按住行往外拖 = 把该文件从堆里取出（捕获鼠标保证拖出弹窗后仍能收尾）
                rowB.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs me)
                {
                    if (me.ChangedButton != MouseButton.Left) return;
                    CancelStackPull();
                    pullFromEntry = en;
                    pullPath = p;
                    pullStart = StackPullPosition(rowB, me);
                    pullLast = pullStart;
                    pullActive = false;
                    pullCaptureOwner = rowB;
                    if (!rowB.CaptureMouse()) CancelStackPull();
                    me.Handled = true;
                };
                rowB.MouseMove += delegate(object s, MouseEventArgs me)
                {
                    if (pullPath == null || pullFromEntry != en || me.LeftButton != MouseButtonState.Pressed) return;
                    Point sp = StackPullPosition(rowB, me);
                    pullLast = sp;
                    if (!pullActive && (Math.Abs(sp.X - pullStart.X) > 8 || Math.Abs(sp.Y - pullStart.Y) > 8))
                        pullActive = true; // 超过阈值：进入拖出状态（原地点击仍是打开）
                };
                rowB.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs me)
                {
                    if (pullPath == null || pullFromEntry != en) return;
                    bool wasPull = pullActive && pullPath != null && pullFromEntry == en;
                    Point winPt = StackPullPosition(rowB, me);
                    FinishPull(en, p, winPt, wasPull);
                    me.Handled = true;
                };
                // 弹窗关闭会强制释放捕获：此时用最后跟踪位置兜底完成拖出
                rowB.LostMouseCapture += delegate
                {
                    if (pullActive && pullPath != null && pullFromEntry == en)
                        FinishPull(pullFromEntry, pullPath, pullLast, true);
                    else if (pullCaptureOwner == rowB)
                        CancelStackPull();
                };
                listPanel.Children.Add(rowB);
            }
            ScrollViewer sv = new ScrollViewer
            {
                Content = listPanel,
                MaxHeight = 340,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Border bd = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x23, 0x28)),
                BorderBrush = normalBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6),
                Child = sv
            };
            System.Windows.Controls.Primitives.Popup popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = en.Host,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
                VerticalOffset = -6,
                StaysOpen = true, // 同文件夹弹层：不吞点击，鼠标离开弹层即收起
                AllowsTransparency = true,
                Child = bd
            };
            popup.MouseLeave += delegate
            {
                if (stackPopup != popup) return;
                // 先分离字段，关闭导致的 LostMouseCapture 可以安全完成一次拖出。
                stackPopup = null;
                popup.IsOpen = false;
            };
            stackPopup = popup;
            popup.IsOpen = true;
        }

        ContextMenu BuildItemMenu(DockEntry en)
        {
            ContextMenu m = new ContextMenu();
            if (IsStack(en))
            {
                // 文件堆菜单：打开最上层 / 就地列出全部文件 / 拆开堆叠
                List<ItemCfg> members = StackService.GetMembers(en.Cfg);
                m.Items.Add(Mi("打开（最上层文件）", delegate { Launch(members[0]); }));
                foreach (ItemCfg member in members)
                {
                    ItemCfg captured = member;
                    m.Items.Add(Mi("打开 " + StackService.DisplayName(member), delegate { Launch(captured); }));
                }
                m.Items.Add(new Separator());
                m.Items.Add(Mi("拆开堆叠（还原为独立图标）", delegate { Unstack(en); }));
                m.Items.Add(new Separator());
                m.Items.Add(BuildGroupMenu(en));
                m.Items.Add(Mi("重命名…", delegate { Rename(en); }));
                m.Items.Add(Mi("移除整个文件堆", delegate { Remove(en); }));
                return m;
            }
            m.Items.Add(Mi("打开", delegate { Launch(en.Cfg); }));
            m.Items.Add(Mi("打开文件位置", delegate { OpenLocation(en.Cfg); }));
            m.Items.Add(Mi("复制文件位置", delegate { CopyLocation(en.Cfg); }));

            // 文件夹专属：以该目录为工作目录打开终端 / Cursor
            if (en.IsFolder)
            {
                string dir = en.Cfg.Path;
                m.Items.Add(Mi("在此打开终端", delegate { OpenTerminalAt(dir); }));
                if (CursorAvailable())
                    m.Items.Add(Mi("用 Cursor 打开此目录", delegate { OpenCursorAt(dir); }));
            }

            m.Items.Add(new Separator());
            MenuItem kill = Mi("强制结束进程（卡死时用）", delegate { KillApp(en.Cfg); });
            string ext = null;
            try { ext = Path.GetExtension(en.Cfg.Path); } catch { }
            kill.IsEnabled = ext != null &&
                (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                 ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)); // 仅 exe/lnk 支持进程匹配
            m.Items.Add(kill);
            m.Items.Add(new Separator());

            m.Items.Add(BuildGroupMenu(en));
            m.Items.Add(new Separator());
            m.Items.Add(Mi("重命名…", delegate { Rename(en); }));
            m.Items.Add(Mi("从 Dock 移除", delegate { Remove(en); }));
            return m;
        }

        MenuItem BuildGroupMenu(DockEntry en)
        {
            // 移动到分组：控制该条目在哪个分组视图显示
            MenuItem grp = new MenuItem { Header = "移动到分组" };
            MenuItem gAll = Mi("未分组（仅“全部”视图显示）", delegate
            {
                StackService.MoveToGroup(en.Cfg, null);
                CloseFolderFlyout();
                ApplyGroupVisibility();
                ScheduleSave();
            });
            gAll.IsChecked = string.IsNullOrEmpty(en.Cfg.Group);
            grp.Items.Add(gAll);
            foreach (string gname in ExistingGroups())
            {
                string captured = gname;
                MenuItem gi = Mi(captured, delegate
                {
                    StackService.MoveToGroup(en.Cfg, captured);
                    CloseFolderFlyout();
                    ApplyGroupVisibility();
                    ScheduleSave();
                });
                gi.IsChecked = en.Cfg.Group == captured;
                grp.Items.Add(gi);
            }
            grp.Items.Add(new Separator());
            grp.Items.Add(Mi("新建分组并移入…", delegate
            {
                string gname = VB.Interaction.InputBox("分组名称（如：开发、论文、日常）：", "新建分组", "", -1, -1);
                if (string.IsNullOrEmpty(gname = gname.Trim())) return;
                if (gname == "全部")
                {
                    MessageBox.Show(this, "“全部”是保留的视图名称，请换一个分组名。", "Koi");
                    return;
                }
                StackService.MoveToGroup(en.Cfg, gname);
                CloseFolderFlyout();
                if (cfg.CurrentGroup != "全部" && cfg.CurrentGroup != gname) cfg.CurrentGroup = gname;
                ApplyGroupVisibility();
                ScheduleSave();
            }));
            return grp;
        }

        // 拖出收尾：关弹窗、清状态；wasPull=true 时把文件从堆中取出并落在光标附近的槽位，
        // 否则视为原地点击（打开该文件）
        void FinishPull(DockEntry from, string path, Point winPt, bool wasPull)
        {
            // Popup 关闭/释放捕获会同步触发 LostMouseCapture，必须先清状态避免递归取出。
            CancelStackPull();
            CloseFolderFlyout();
            if (wasPull && from != null && !string.IsNullOrEmpty(path))
            {
                try
                {
                    Point sp = PointToScreen(winPt);
                    PullOutFileFromStack(from, path, sp.X);
                }
                catch (Exception ex) { AppendErrorLog("从文件堆拖出失败：" + ex.Message); }
            }
            else if (!string.IsNullOrEmpty(path))
            {
                ItemCfg selected = null;
                if (from != null)
                    foreach (ItemCfg member in StackService.GetMembers(from.Cfg))
                        if (PathIdentity.Normalize(member.Path) == PathIdentity.Normalize(path)) { selected = member; break; }
                Launch(selected ?? new ItemCfg { Path = path, Name = PrettyName(path) });
            }
        }

        Point StackPullPosition(FrameworkElement source, MouseEventArgs e)
        {
            // Popup 使用独立窗口；先转屏幕再转 Dock 坐标，不能把 Popup 局部坐标当成 Dock 坐标。
            return PointFromScreen(source.PointToScreen(e.GetPosition(source)));
        }

        void CancelStackPull()
        {
            UIElement owner = pullCaptureOwner;
            pullCaptureOwner = null;
            pullFromEntry = null;
            pullPath = null;
            pullActive = false;
            if (owner != null && owner.IsMouseCaptured) owner.ReleaseMouseCapture();
        }

        // 把文件从堆中取出，作为独立条目插入屏幕坐标对应的槽位；堆按剩余数量自动退化
        void PullOutFileFromStack(DockEntry en, string path, double screenPx)
        {
            int insertAt = InsertIndexAtScreenX(screenPx);
            ItemCfg pulled = StackService.PullOut(en.Cfg, path);
            if (pulled == null) return;
            en.Caption.Text = StackService.DisplayName(en.Cfg);
            en.Img.Source = ShellIcons.ResolveIcon(en.Cfg) ?? ShellIcons.DefaultIcon();
            en.Stackable = ComputeStackable(en.Cfg.Path);
            UpdateStackVisual(en);
            AddEntry(pulled, true, insertAt, false);
            ApplyGroupVisibility();
            UpdateAutoSpacing();
            ScheduleSave();
        }

        // 屏幕像素 X → Dock 中最近的插入槽位下标
        int InsertIndexAtScreenX(double screenPx)
        {
            try
            {
                Point rp = row.PointFromScreen(new Point(screenPx, 0));
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Host.Visibility != Visibility.Visible) continue;
                    double c = entries[i].Host.TranslatePoint(new Point(entries[i].Host.ActualWidth / 2.0, 0), row).X;
                    if (rp.X < c) return i;
                }
                return entries.Count;
            }
            catch { return entries.Count; }
        }

    }
}
