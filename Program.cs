// =====================================================================
//  Koi（锦鲤坞）- Mac 风格的 Windows 悬浮启动坞
//  名字取自锦鲤：鼠标划过时图标如锦鲤聚拢——Dock 放大效果的学名正是「鱼眼 fisheye」
//  - 悬浮在桌面边缘的半透明圆角 Dock 栏，总在最前
//  - 图标悬停时像 Mac 一样放大（鱼眼效果）
//  - 滚轮直接调节图标大小（32~112px），Ctrl+滚轮调节背景透明度（25%~100%）
//  - 拖拽 .exe / 快捷方式 / 文件夹到 Dock 上即可添加
//  - 右键图标：打开 / 打开文件位置 / 复制文件位置 / 强制结束进程 / 重命名 / 移除
//  - 右键背景：添加程序 / 调整图标 / 总在最前 / 开机自启 / 退出
//  - 位置、大小、项目列表保存在 %APPDATA%\Koi\config.xml
//  编译（系统自带 .NET Framework，无需安装任何东西）：
//    csc /target:winexe /r:... Program.cs
// =====================================================================
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

[assembly: System.Reflection.AssemblyVersion("1.8.3.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.8.3.0")]

namespace Koi
{
    public class ItemCfg
    {
        public string Name;
        public string Path;
        public string Icon; // 可选：自定义图标文件（png/jpg/ico），如 Store 应用无法自动取图标时使用
        public List<string> Stack; // 文件堆叠：非空且 ≥2 时该条目是"文件堆"，Path 为最上层文件
        public string Group;       // 所属分组（空 = 未分组，仅显示在"全部"视图）
    }

    public class WorkflowCfg
    {
        public string Name;
        public List<string> Paths = new List<string>(); // 逐行：文件夹/程序/文件/网址，一键依次打开
    }

    public class Config
    {
        public double IconSize = 64;
        public double Opacity = 0.72; // Dock 背景不透明度 0.25~1
        public double? Left;
        public double? Top;
        public bool Topmost = true;
        public bool AutoStart = false;
        public bool ShowNames = true;          // 图标下方常显名称（旧配置缺省视为 true）
        [XmlIgnore]
        public bool ShowNamesSpecified;        // XmlSerializer 开关：旧配置无此字段时保持 false
        public string CurrentGroup = "全部";   // 当前显示的分组
        public List<string> Recent = new List<string>();               // 最近打开（新→旧，最多 8 条）
        public List<WorkflowCfg> Workflows = new List<WorkflowCfg>(); // 一键打开的工作组合
        public List<ItemCfg> Items = new List<ItemCfg>();
    }

    public class DockEntry
    {
        public ItemCfg Cfg;
        public Image Img;
        public TextBlock Caption;  // 图标下方常显名称
        public StackPanel Host;    // 图标+名称 的纵向组合，鱼眼 ZIndex 作用在它上面
        public ScaleTransform Scale;
        public Grid ImgWrap;       // 图标容器：堆叠时在其下垫"叠纸"
        public System.Windows.Shapes.Path StackBack1, StackBack2; // 堆叠视觉的底层纸片
        public bool Stackable;     // 是否可参与堆叠（普通文件，非 exe/lnk/目录/Store 应用）
    }

    public class DockWindow : Window
    {
        // 鱼眼放大幅度：中心图标缩放为 1+FisheyeAmp 倍。
        // 顶部留白必须用同一常量计算（留白 ≥ FisheyeAmp×图标高），否则放大后图标会被视口裁掉
        internal const double FisheyeAmp = 0.9;

        internal const string AppVersion = "1.8.3"; // 发布时由 release.ps1 自动递增

        static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Koi");
        static readonly string ConfigFile = Path.Combine(ConfigDir, "config.xml");

        readonly Config cfg = new Config();
        readonly List<DockEntry> entries = new List<DockEntry>();
        readonly Brush normalBorder = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));

        Border bg;
        StackPanel row;
        TextBlock hint;
        Grid root;
        Border nameLabel;          // 悬停时显示在图标上方的名称标签
        DockEntry hoveredEntry;
        DockEntry reorderSource;   // 按住的图标（排序候选）
        DockEntry dragEntry;       // 拖动中的图标
        Point reorderStart;
        bool dragStarted;          // 已进入拖动状态（图标跟随光标）
        TranslateTransform dragTranslate;
        TextBlock plusText;        // 左上角"+"按钮的文字
        Border plusButton;         // 左上角"+"按钮
        Grid scrollInner;          // 滚动内容（含放大顶部留白）
        ScrollViewer scroller;     // 图标超宽时的横向滚动容器
        static bool configLoadFailed; // 损坏配置归档失败时暂停保存，保护原文件
        DispatcherTimer saveTimer;

        public DockWindow()
        {
            LoadConfig();
            cfg.AutoStart = IsAutoStartRegistered(); // 以注册表实际状态为准，菜单勾选不再"虚亮"

            Title = "Koi 锦鲤坞";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = cfg.Topmost;
            SizeToContent = SizeToContent.WidthAndHeight;

            BuildUi();
            foreach (ItemCfg c in cfg.Items) AddEntry(c, false);
            UpdateHint();

            SourceInitialized += delegate { HideFromAltTab(); };
            Deactivated += delegate { if (dragEntry != null) EndReorder(dragEntry); }; // 窗口失活时结束拖动，防止状态残留
            LocationChanged += delegate
            {
                ScheduleSave();
                HandleScreenChanged(); // 拖到不同分辨率/缩放的屏幕时刷新宽度上限
            };
            SizeChanged += delegate { OnWindowSizeChanged(); }; // 内容变化后统一做屏幕约束

            // 内存空闲瘦身：有交互刷新时间戳，闲置 5 分钟自动回收裁剪
            MouseMove += delegate { lastActivity = DateTime.Now; };
            KeyDown += delegate { lastActivity = DateTime.Now; };
            MouseLeftButtonUp += delegate { lastActivity = DateTime.Now; };
            trimTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            trimTimer.Tick += delegate
            {
                if ((DateTime.Now - lastActivity).TotalMinutes >= 5) TrimNow();
            };
            trimTimer.Start();
            Loaded += delegate
            {
                if (cfg.Left.HasValue && cfg.Top.HasValue)
                {
                    Left = cfg.Left.Value;
                    Top = cfg.Top.Value;
                }
                else
                {
                    // 首次运行：主屏底部居中，落在任务栏正上方
                    Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - ActualWidth) / 2;
                    Top = SystemParameters.WorkArea.Bottom - ActualHeight - 10;
                }
                // 按窗口实际所在显示器的工作区精确约束（避免多屏排列空隙、换分辨率后出界）
                OnWindowSizeChanged();
                ApplyGroupVisibility(); // 恢复上次使用的分组视图
            };
        }

        double IconSizePx() { return cfg.IconSize; }

        // ---------------------------------------------------------------- UI

        void BuildUi()
        {
            root = new Grid();
            root.Margin = new Thickness(18, 6, 18, 12);

            RowDefinition overflow = new RowDefinition();
            overflow.Height = new GridLength(FisheyeAmp * IconSizePx() + 6, GridUnitType.Pixel); // 图标放大时向上凸出的空间（与 FisheyeAmp 同参）
            RowDefinition body = new RowDefinition();
            body.Height = new GridLength(IconSizePx() + 44, GridUnitType.Pixel); // 图标+两行名称

            root.RowDefinitions.Add(overflow);
            root.RowDefinitions.Add(body);

            bg = new Border();
            bg.CornerRadius = new CornerRadius(16);
            bg.Background = MakeBgBrush();
            bg.BorderBrush = normalBorder;
            bg.BorderThickness = new Thickness(1);
            bg.Padding = new Thickness(10, 8, 10, 10);
            bg.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Opacity = 0.5,
                Color = Colors.Black
            };

            row = new StackPanel();
            row.Orientation = Orientation.Horizontal;

            // 左上角"+"：快捷添加程序 / 文件夹
            plusText = new TextBlock();
            plusText.Text = "＋";
            plusText.FontSize = Math.Max(16, Math.Round(IconSizePx() * 0.30));
            plusText.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xD3, 0xD7, 0xDC));
            plusText.TextAlignment = TextAlignment.Center;
            Border plus = new Border();
            plus.CornerRadius = new CornerRadius(10);
            plus.Padding = new Thickness(9, 2, 9, 5);
            plus.Margin = new Thickness(0, 0, 6, 0);
            plus.VerticalAlignment = VerticalAlignment.Center;
            plus.Cursor = Cursors.Hand;
            plus.Background = Brushes.Transparent;
            plus.Child = plusText;
            plus.MouseEnter += delegate { plus.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)); };
            plus.MouseLeave += delegate { plus.Background = Brushes.Transparent; };
            plus.MouseLeftButtonUp += delegate
            {
                ContextMenu m = new ContextMenu();
                m.Items.Add(Mi("通过路径添加…", delegate { AddByPath(); }));
                m.Items.Add(Mi("添加程序…", delegate { BrowseAdd(); }));
                m.Items.Add(Mi("添加文件夹…", delegate { BrowseAddFolder(); }));
                m.Items.Add(Mi("添加文件…", delegate { BrowseAddFile(); }));
                m.PlacementTarget = plus;
                m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                m.IsOpen = true;
            };

            // 图标行放进横向滚动容器：图标缩到 32px 仍放不下时可横向滚动访问。
            // 放大所需的顶部留白放在滚动内容内部（scrollInner 上边距=0.8×图标），鱼眼放大部分不会被视口裁掉
            scrollInner = new Grid();
            // 左右也要放大幅度一半的留白：首个图标放大后向左膨胀不会被视口裁剪
            double sidePad = Math.Round(FisheyeAmp * IconSizePx() / 2);
            scrollInner.Margin = new Thickness(sidePad, Math.Round(FisheyeAmp * IconSizePx() + 6), sidePad + 12, 10);
            scrollInner.Children.Add(row);

            // 滚动视口本身让开＋按钮的宽度：内容滚动时图标不会滑到＋按钮底下被遮挡
            scroller = new ScrollViewer();
            scroller.Margin = new Thickness(PlusWidth() + 10, 0, 0, 0);
            scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroller.Content = scrollInner;

            hint = new TextBlock();
            hint.Text = "把程序 / 快捷方式拖到这里，或点左侧 ＋ 添加";
            hint.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xE8, 0xEA, 0xED));
            hint.FontSize = 13;
            hint.Margin = new Thickness(12, 4, 12, 0);
            row.Children.Add(hint);

            // 分层：底层暗色背景条(row1) → 中层滚动图标区(跨两行，含放大留白) → 顶层＋按钮/名称标签
            Grid.SetRow(bg, 1);
            root.Children.Add(bg);
            Grid.SetRow(scroller, 0);
            Grid.SetRowSpan(scroller, 2);
            root.Children.Add(scroller);
            plusButton = plus;
            Grid.SetRow(plusButton, 1);
            plusButton.HorizontalAlignment = HorizontalAlignment.Left;
            plusButton.VerticalAlignment = VerticalAlignment.Center;
            plusButton.Margin = new Thickness(8, 0, 0, 0);
            root.Children.Add(plusButton);

            // 悬停名称标签：悬浮在图标上方、跟随鱼眼位置，替代原生 tooltip
            nameLabel = new Border();
            nameLabel.CornerRadius = new CornerRadius(8);
            nameLabel.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x23, 0x28));
            nameLabel.BorderBrush = normalBorder;
            nameLabel.BorderThickness = new Thickness(1);
            nameLabel.Padding = new Thickness(10, 4, 10, 5);
            nameLabel.HorizontalAlignment = HorizontalAlignment.Left;
            nameLabel.VerticalAlignment = VerticalAlignment.Top;
            nameLabel.IsHitTestVisible = false;
            nameLabel.Visibility = Visibility.Collapsed;
            TextBlock nameText = new TextBlock();
            nameText.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xEC, 0xEE, 0xF1));
            nameText.FontSize = 12.5;
            nameLabel.Child = nameText;
            Grid.SetRowSpan(nameLabel, 2);
            root.Children.Add(nameLabel);

            Content = root;

            PreviewMouseLeftButtonDown += OnWindowPreviewDown; // 窗口级判定：按在空白处=拖动窗口
            // 右键打开时按当前数据重建全局菜单（最近/分组/工作组合是动态内容）
            bg.ContextMenuOpening += delegate(object s, ContextMenuEventArgs ce)
            {
                ContextMenu mm = BuildBgMenu();
                mm.PlacementTarget = bg;
                mm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                bg.ContextMenu = mm;
                mm.IsOpen = true;
                ce.Handled = true;
            };
            scroller.ContextMenuOpening += delegate(object s, ContextMenuEventArgs ce)
            {
                ContextMenu mm = BuildBgMenu();
                mm.PlacementTarget = scroller;
                mm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                scroller.ContextMenu = mm;
                mm.IsOpen = true;
                ce.Handled = true;
            };

            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragOver += OnDragOver;
            DragLeave += OnDragLeave;
            Drop += OnDrop;
            PreviewMouseWheel += OnWheel;

            row.MouseMove += OnFisheye;
            row.MouseLeave += OnFisheyeLeave;
        }

        void AddEntry(ItemCfg c, bool save, int index = -1)
        {
            DockEntry en = new DockEntry();
            en.Cfg = c;
            en.Stackable = ComputeStackable(c.Path);
            // 分组视图内"用户新增"：默认归入当前组，保证"看得见"与"归属"一致。
            // 仅限 save=true（用户主动添加）；启动恢复配置走 save=false，绝不能改写原归属
            if (save && cfg.CurrentGroup != "全部" && string.IsNullOrEmpty(c.Group)) c.Group = cfg.CurrentGroup;

            Image img = new Image();
            img.Source = ShellIcons.ResolveIcon(c);
            if (img.Source == null) img.Source = ShellIcons.DefaultIcon();
            img.Width = IconSizePx();
            img.Height = IconSizePx();
            img.Stretch = Stretch.Uniform;
            img.RenderTransformOrigin = new Point(0.5, 1.0); // 从底部向上放大，凸出 Dock 背景
            en.Scale = new ScaleTransform(1, 1);
            img.RenderTransform = en.Scale;
            img.Margin = new Thickness(0, 0, 0, 0);
            img.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
            img.MouseEnter += delegate
            {
                if (IsStack(en)) { ShowStackPopup(en); return; } // 文件堆：悬停弹出文件选择列表
                bool isFolder = false;
                try { isFolder = Directory.Exists(en.Cfg.Path); } catch { }
                if (isFolder) { ShowFolderFlyout(en); return; }  // 文件夹：悬停就地展开子目录与最近文件
                hoveredEntry = en;
                ShowNameLabel(en);
            };
            img.MouseLeave += delegate
            {
                if (hoveredEntry == en) { hoveredEntry = null; nameLabel.Visibility = Visibility.Collapsed; }
                // 离开文件夹图标且未进入弹层：1.6 秒后自动收起
                bool wasFolder = false;
                try { wasFolder = Directory.Exists(en.Cfg.Path); } catch { }
                if (wasFolder && folderFlyoutCloseTimer != null)
                {
                    folderFlyoutCloseTimer.Stop();
                    folderFlyoutCloseTimer.Start();
                }
            };
            img.MouseLeftButtonUp += delegate
            {
                bool wasDrag = dragStarted;
                DockEntry mergeT = wasDrag ? dropMergeTarget : null;
                if (wasDrag && mergeT != null)
                {
                    // 拖放合并：拖起的文件并入目标文件堆（目标可能是普通文件或已有堆）
                    dragStarted = false;
                    if (en.Img.IsMouseCaptured) en.Img.ReleaseMouseCapture();
                    en.Scale.ScaleX = 1; en.Scale.ScaleY = 1;
                    en.Host.RenderTransform = null;
                    Panel.SetZIndex(en.Host, 0);
                    row.Children.Remove(en.Host);
                    entries.Remove(en);
                    dragEntry = null; reorderSource = null; dropMergeTarget = null;
                    nameLabel.Visibility = Visibility.Collapsed; hoveredEntry = null;
                    MergeIntoStack(mergeT, en.Cfg.Path);
                    UpdateAutoSpacing();
                    ScheduleSave();
                    return;
                }
                EndReorder(en);
                if (!wasDrag) { if (IsStack(en)) ShowStackPopup(en); else Launch(c); }
            };
            img.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            // 右键打开时重建条目菜单（移动到分组列表是动态内容）。
            // Handled=true 必须设置：否则事件冒泡到滚动区会被全局菜单接管
            img.ContextMenuOpening += delegate(object s, ContextMenuEventArgs ce)
            {
                ce.Handled = true;
                ContextMenu mm = BuildItemMenu(en);
                mm.PlacementTarget = img;
                img.ContextMenu = mm;
                mm.IsOpen = true;
            };
            en.Img = img;

            // 堆叠视觉：图标下垫两张微旋转的"纸片"，仅文件堆显示
            Grid imgWrap = new Grid();
            System.Windows.Shapes.Path back1 = MakeStackPaper(img.Width);
            System.Windows.Shapes.Path back2 = MakeStackPaper(img.Width);
            back1.RenderTransform = new TransformGroup
            {
                Children = { new RotateTransform(-5), new TranslateTransform(-4, 0) }
            };
            back2.RenderTransform = new TransformGroup
            {
                Children = { new RotateTransform(6), new TranslateTransform(5, -1) }
            };
            back1.Visibility = Visibility.Collapsed;
            back2.Visibility = Visibility.Collapsed;
            en.StackBack1 = back1;
            en.StackBack2 = back2;
            imgWrap.Children.Add(back2);
            imgWrap.Children.Add(back1);
            imgWrap.Children.Add(img);
            en.ImgWrap = imgWrap;

            // Mac 式拖动排序：按住移动即抬起跟随光标，实时换位，松手回弹落位
            img.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e2)
            {
                reorderSource = en;
                reorderStart = e2.GetPosition(row);
            };
            img.MouseMove += OnItemMouseMove;
            img.LostMouseCapture += delegate { if (dragEntry == en) EndReorder(en); }; // 系统抢占捕获等中断场景

            // 名称常显在图标下方，可读性优先：最多两行，超出再省略（悬停胶囊看全名）
            TextBlock cap = new TextBlock();
            cap.Text = c.Name;
            cap.FontSize = 11;
            cap.LineHeight = 14;
            cap.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            cap.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xD3, 0xD7, 0xDC));
            cap.TextAlignment = TextAlignment.Center;
            cap.TextWrapping = TextWrapping.Wrap;
            cap.TextTrimming = TextTrimming.CharacterEllipsis;
            cap.MaxWidth = Math.Max(IconSizePx() + 10, 76); // 名称宽度下限：避免英文单词被拦腰换行
            cap.MaxHeight = 30; // 两行封顶
            cap.Margin = new Thickness(0, 2, 0, 0);
            cap.Visibility = cfg.ShowNames ? Visibility.Visible : Visibility.Collapsed;
            cap.MouseLeftButtonUp += delegate
            {
                if (IsStack(en)) { ShowStackPopup(en); return; }
                Launch(c);
            };
            cap.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            cap.MouseEnter += delegate
            {
                if (IsStack(en)) { ShowStackPopup(en); return; }
                hoveredEntry = en;
                ShowNameLabel(en);
            }; // 悬停名称同样弹出全名胶囊
            cap.MouseLeave += delegate { if (hoveredEntry == en) { hoveredEntry = null; nameLabel.Visibility = Visibility.Collapsed; } };
            cap.Tag = "cap"; // OnWindowPreviewDown 用它区分"点在名称上"
            cap.ContextMenuOpening += delegate(object s, ContextMenuEventArgs ce)
            {
                ce.Handled = true;
                ContextMenu mm = BuildItemMenu(en);
                mm.PlacementTarget = cap;
                cap.ContextMenu = mm;
                mm.IsOpen = true;
            }; // 名称右键 = 图标右键，统一操作（打开时重建）
            en.Caption = cap;

            StackPanel host = new StackPanel();
            host.Orientation = Orientation.Vertical;
            double m = Math.Round(IconSizePx() * 0.18); // 间距随图标尺寸自动调整
            host.Margin = new Thickness(m, 0, m, 0);
            host.Children.Add(imgWrap);
            host.Children.Add(cap);
            en.Host = host;
            host.Visibility = (cfg.CurrentGroup == "全部" || c.Group == cfg.CurrentGroup)
                ? Visibility.Visible : Visibility.Collapsed; // 与分组过滤保持一致

            if (index >= 0 && index <= entries.Count)
            {
                entries.Insert(index, en);
                row.Children.Insert(index + 1, host); // row[0] 是提示文字
            }
            else
            {
                entries.Add(en);
                row.Children.Add(host);
            }
            UpdateStackVisual(en);
            UpdateHint();
            UpdateAutoSpacing(); // 新增后间距重排（布局未刷新时函数内部会跳过，由 SizeChanged 兜底）
            if (save) ScheduleSave();
        }

        void UpdateHint()
        {
            hint.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------- 文件堆叠 ----------------

        DockEntry dropMergeTarget; // 拖动悬停时可能合并到的目标文件
        System.Windows.Controls.Primitives.Popup stackPopup; // 文件堆的悬停选择列表
        DockEntry pullFromEntry;   // 正在从堆里往外拖的所属堆
        string pullPath;           // 正在拖出的文件路径
        Point pullStart;
        Point pullLast;            // 拖出过程中的最后光标位置（窗口坐标）
        bool pullActive;           // 已进入拖出状态

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
            return en.Cfg.Stack != null && en.Cfg.Stack.Count >= 2;
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
        void MergeIntoStack(DockEntry target, string draggedPath)
        {
            if (target.Cfg.Stack == null) target.Cfg.Stack = new List<string>();
            if (!target.Cfg.Stack.Contains(target.Cfg.Path))
                target.Cfg.Stack.Insert(0, target.Cfg.Path); // 首位 = 最上层文件
            if (!target.Cfg.Stack.Contains(draggedPath))
                target.Cfg.Stack.Add(draggedPath);
            if (target.Img.Opacity < 1) target.Img.Opacity = 1; // 撤掉拖动高亮
            UpdateStackVisual(target);
        }

        // 拆开文件堆：原地还原为独立条目
        // 文件夹悬停就地展开：子目录 + 最近修改的文件 + 终端/编辑器直达
        DispatcherTimer folderFlyoutCloseTimer;

        void ShowFolderFlyout(DockEntry en)
        {
            if (stackPopup != null) { stackPopup.IsOpen = false; stackPopup = null; }
            string dir = en.Cfg.Path;

            StackPanel listPanel = new StackPanel { MinWidth = 260 };

            // 头部动作：打开文件夹 / 在此打开终端 / 用 Cursor 打开
            listPanel.Children.Add(FlyoutRow("📂 打开文件夹", delegate { RunPath(dir); }));
            listPanel.Children.Add(FlyoutRow("⌨ 在此打开终端", delegate { OpenTerminalAt(dir); }));
            if (CursorAvailable())
                listPanel.Children.Add(FlyoutRow("✎ 用 Cursor 打开", delegate { OpenCursorAt(dir); }));
            listPanel.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(4, 4, 4, 4)
            });

            // 子目录
            try
            {
                List<string> dirs = new List<string>(Directory.GetDirectories(dir));
                dirs.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string d in dirs)
                {
                    string captured = d;
                    listPanel.Children.Add(FlyoutRow("📁 " + PrettyName(d), delegate
                    {
                        CloseFolderFlyout();
                        Process.Start("explorer.exe", "\"" + captured + "\"");
                    }));
                }
            }
            catch { }

            // 最近修改的文件（最多 6 个）
            try
            {
                List<FileInfo> files = new List<FileInfo>();
                foreach (string f in Directory.GetFiles(dir))
                {
                    try { files.Add(new FileInfo(f)); } catch { }
                }
                files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                int shown = 0;
                foreach (FileInfo fi in files)
                {
                    if (shown >= 6) break;
                    FileInfo capturedF = fi;
                    listPanel.Children.Add(FlyoutRow("📄 " + PrettyName(fi.FullName), delegate
                    {
                        CloseFolderFlyout();
                        Launch(new ItemCfg { Path = capturedF.FullName, Name = PrettyName(capturedF.FullName) });
                    }));
                    shown++;
                }
            }
            catch { }

            ScrollViewer sv = new ScrollViewer
            {
                Content = listPanel,
                MaxHeight = 380,
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
                StaysOpen = false,
                AllowsTransparency = true,
                Child = bd
            };
            popup.MouseEnter += delegate { if (folderFlyoutCloseTimer != null) folderFlyoutCloseTimer.Stop(); };
            popup.MouseLeave += delegate { CloseFolderFlyout(); };
            bd.MouseEnter += delegate { if (folderFlyoutCloseTimer != null) folderFlyoutCloseTimer.Stop(); };
            if (folderFlyoutCloseTimer == null)
            {
                folderFlyoutCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
                folderFlyoutCloseTimer.Tick += delegate
                {
                    folderFlyoutCloseTimer.Stop();
                    CloseFolderFlyout();
                };
            }
            stackPopup = popup; // 复用同一管理字段：新弹层会顶掉旧弹层
            popup.IsOpen = true;
        }

        Border FlyoutRow(string text, System.Action onClick)
        {
            Border rowB = new Border
            {
                Padding = new Thickness(8, 5, 10, 5),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            TextBlock t = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xE8, 0xEA, 0xED)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            rowB.Child = t;
            rowB.MouseEnter += delegate { rowB.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)); };
            rowB.MouseLeave += delegate { rowB.Background = Brushes.Transparent; };
            rowB.MouseLeftButtonUp += delegate { onClick(); };
            return rowB;
        }

        void CloseFolderFlyout()
        {
            if (folderFlyoutCloseTimer != null) folderFlyoutCloseTimer.Stop();
            if (stackPopup != null) { stackPopup.IsOpen = false; stackPopup = null; }
        }

        void Unstack(DockEntry en)
        {
            List<string> paths = en.Cfg.Stack;
            if (paths == null || paths.Count == 0) return;
            int at = entries.IndexOf(en);
            if (hoveredEntry == en)
            {
                hoveredEntry = null;
                nameLabel.Visibility = Visibility.Collapsed;
            }
            if (dragEntry == en) EndReorder(en);
            reorderSource = null;
            if (stackPopup != null) { stackPopup.IsOpen = false; stackPopup = null; }
            row.Children.Remove(en.Host);
            entries.Remove(en);
            UpdateHint();
            for (int i = 0; i < paths.Count; i++)
            {
                ItemCfg c = new ItemCfg();
                c.Path = paths[i];
                c.Name = PrettyName(paths[i]);
                AddEntry(c, true, at + i);
            }
        }

        // 悬停文件堆弹出文件选择列表（点击打开对应文件）
        void ShowStackPopup(DockEntry en)
        {
            List<string> list = en.Cfg.Stack;
            if (list == null || list.Count == 0) return;
            if (stackPopup != null) { stackPopup.IsOpen = false; stackPopup = null; }

            StackPanel listPanel = new StackPanel { MinWidth = 230 };
            foreach (string path in list)
            {
                string p = path; // 闭包捕获
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
                    Source = ShellIcons.ResolveIcon(new ItemCfg { Path = p }) ?? ShellIcons.DefaultIcon()
                };
                Grid.SetColumn(ic, 0);
                TextBlock t = new TextBlock
                {
                    Text = PrettyName(p),
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
                    rowB.CaptureMouse();
                    pullFromEntry = en;
                    pullPath = p;
                    pullStart = me.GetPosition(null);
                    pullLast = pullStart;
                    pullActive = false;
                };
                rowB.MouseMove += delegate(object s, MouseEventArgs me)
                {
                    if (pullPath == null || pullFromEntry != en || me.LeftButton != MouseButtonState.Pressed) return;
                    Point sp = me.GetPosition(null);
                    pullLast = sp;
                    if (!pullActive && (Math.Abs(sp.X - pullStart.X) > 8 || Math.Abs(sp.Y - pullStart.Y) > 8))
                        pullActive = true; // 超过阈值：进入拖出状态（原地点击仍是打开）
                };
                rowB.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs me)
                {
                    bool wasPull = pullActive && pullPath != null && pullFromEntry == en;
                    Point winPt = me.GetPosition(null);
                    FinishPull(en, p, winPt, wasPull);
                };
                // 弹窗关闭会强制释放捕获：此时用最后跟踪位置兜底完成拖出
                rowB.LostMouseCapture += delegate
                {
                    if (pullActive && pullPath != null && pullFromEntry == en)
                        FinishPull(pullFromEntry, pullPath, pullLast, true);
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
                StaysOpen = false,
                AllowsTransparency = true,
                Child = bd
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
                m.Items.Add(Mi("打开（最上层文件）", delegate { Launch(new ItemCfg { Path = en.Cfg.Stack[0] }); }));
                foreach (string p in en.Cfg.Stack)
                {
                    string captured = p;
                    m.Items.Add(Mi("打开 " + PrettyName(p), delegate { Launch(new ItemCfg { Path = captured }); }));
                }
                m.Items.Add(new Separator());
                m.Items.Add(Mi("拆开堆叠（还原为独立图标）", delegate { Unstack(en); }));
                m.Items.Add(new Separator());
                m.Items.Add(Mi("重命名…", delegate { Rename(en); }));
                m.Items.Add(Mi("移除整个文件堆", delegate { Remove(en); }));
                return m;
            }
            m.Items.Add(Mi("打开", delegate { Launch(en.Cfg); }));
            m.Items.Add(Mi("打开文件位置", delegate { OpenLocation(en.Cfg); }));
            m.Items.Add(Mi("复制文件位置", delegate { CopyLocation(en.Cfg); }));

            // 文件夹专属：以该目录为工作目录打开终端 / Cursor
            bool isDir = false;
            try { isDir = Directory.Exists(en.Cfg.Path); } catch { }
            if (isDir)
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

            // 移动到分组：控制该条目在哪个分组视图显示
            MenuItem grp = new MenuItem { Header = "移动到分组" };
            MenuItem gAll = Mi("未分组（仅“全部”视图显示）", delegate
            {
                en.Cfg.Group = null;
                ApplyGroupVisibility();
                ScheduleSave();
            });
            gAll.IsChecked = string.IsNullOrEmpty(en.Cfg.Group);
            grp.Items.Add(gAll);
            foreach (string gname in ExistingGroups())
            {
                if (gname == en.Cfg.Group) continue;
                string captured = gname;
                MenuItem gi = Mi(captured, delegate
                {
                    en.Cfg.Group = captured;
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
                en.Cfg.Group = gname;
                if (cfg.CurrentGroup != "全部" && cfg.CurrentGroup != gname) cfg.CurrentGroup = gname;
                ApplyGroupVisibility();
                ScheduleSave();
            }));
            m.Items.Add(grp);

            m.Items.Add(new Separator());
            m.Items.Add(Mi("重命名…", delegate { Rename(en); }));
            m.Items.Add(Mi("从 Dock 移除", delegate { Remove(en); }));
            return m;
        }

        ContextMenu BuildBgMenu()
        {
            ContextMenu m = new ContextMenu();
            MenuItem ver = Mi("Koi 锦鲤坞 v" + AppVersion, null);
            ver.IsEnabled = false;
            m.Items.Add(ver);
            m.Items.Add(new Separator());

            // 最近打开（新→旧，最多 8 条）
            MenuItem recent = new MenuItem { Header = "最近打开" };
            if (cfg.Recent.Count == 0)
            {
                MenuItem none = Mi("（暂无）", null);
                none.IsEnabled = false;
                recent.Items.Add(none);
            }
            else
            {
                foreach (string r in cfg.Recent)
                {
                    string captured = r;
                    recent.Items.Add(Mi(PrettyName(r), delegate { RunPath(captured); }));
                }
            }
            m.Items.Add(recent);

            // 分组：只显示当前组的相关入口（顺序位置原样保留）
            MenuItem groups = new MenuItem { Header = "分组" };
            List<string> allList = new List<string> { "全部" };
            allList.AddRange(ExistingGroups());
            string[] all = allList.ToArray();
            foreach (string gname in all)
            {
                string g = gname;
                MenuItem gi = Mi(g, delegate
                {
                    cfg.CurrentGroup = g;
                    ApplyGroupVisibility();
                    ScheduleSave();
                });
                gi.IsCheckable = true;
                gi.IsChecked = cfg.CurrentGroup == g;
                groups.Items.Add(gi);
            }
            if (groups.Items.Count == 1)
            {
                MenuItem hint = Mi("（右键图标 → 移动到分组）", null);
                hint.IsEnabled = false;
                groups.Items.Add(hint);
            }
            m.Items.Add(groups);

            // 工作组合：一键依次打开一组目录/程序/网址
            MenuItem work = new MenuItem { Header = "工作组合" };
            if (cfg.Workflows.Count == 0)
            {
                MenuItem none2 = Mi("（暂无，可新建）", null);
                none2.IsEnabled = false;
                work.Items.Add(none2);
            }
            else
            {
                foreach (WorkflowCfg wf in cfg.Workflows)
                {
                    WorkflowCfg captured = wf;
                    work.Items.Add(Mi("▶ " + wf.Name, delegate
                    {
                        foreach (string p in captured.Paths) RunPath(p);
                    }));
                }
            }
            work.Items.Add(new Separator());
            work.Items.Add(Mi("新建工作组合…", delegate
            {
                string wfName = VB.Interaction.InputBox("工作组合名称（如：开发、论文、日常）：", "新建工作组合", "", -1, -1);
                if (string.IsNullOrEmpty(wfName = wfName.Trim())) return;
                string[] lines = ShowLinesInput("工作组合：" + wfName,
                    "每行一个路径（文件夹 / 程序 / 文件 / 网址），确定后保存：");
                if (lines == null) return;
                WorkflowCfg wf = new WorkflowCfg { Name = wfName };
                wf.Paths.AddRange(lines);
                cfg.Workflows.Add(wf);
                ScheduleSave();
            }));
            if (cfg.Workflows.Count > 0)
            {
                MenuItem del = new MenuItem { Header = "删除工作组合…" };
                foreach (WorkflowCfg wf in cfg.Workflows.ToArray())
                {
                    WorkflowCfg captured = wf;
                    del.Items.Add(Mi("删除 " + wf.Name, delegate
                    {
                        cfg.Workflows.Remove(captured);
                        ScheduleSave();
                    }));
                }
                work.Items.Add(del);
            }
            m.Items.Add(work);

            m.Items.Add(new Separator());
            m.Items.Add(Mi("添加程序…", delegate { BrowseAdd(); }));
            m.Items.Add(Mi("添加文件夹…", delegate { BrowseAddFolder(); }));
            m.Items.Add(new Separator());
            m.Items.Add(Mi("图标增大（滚轮 ↑）", delegate { SetIconSize(IconSizePx() + 6); }));
            m.Items.Add(Mi("图标减小（滚轮 ↓）", delegate { SetIconSize(IconSizePx() - 6); }));
            m.Items.Add(Mi("背景更透明（Ctrl+滚轮 ↓）", delegate { SetOpacity(cfg.Opacity - 0.08); }));
            m.Items.Add(Mi("背景更不透明（Ctrl+滚轮 ↑）", delegate { SetOpacity(cfg.Opacity + 0.08); }));
            m.Items.Add(Mi("恢复默认透明度", delegate { SetOpacity(0.72); }));
            m.Items.Add(Mi("立即释放内存", delegate { TrimNow(); }));

            MenuItem names = Mi("显示图标名称", null);
            names.IsCheckable = true;
            names.IsChecked = cfg.ShowNames;
            names.Click += delegate
            {
                cfg.ShowNames = !cfg.ShowNames;
                names.IsChecked = cfg.ShowNames;
                foreach (DockEntry en in entries)
                    en.Caption.Visibility = cfg.ShowNames ? Visibility.Visible : Visibility.Collapsed;
                ScheduleSave();
            };
            m.Items.Add(names);
            m.Items.Add(new Separator());

            MenuItem top = Mi("总在最前", null);
            top.IsCheckable = true;
            top.IsChecked = Topmost;
            top.Click += delegate
            {
                Topmost = !Topmost;
                top.IsChecked = Topmost;
                cfg.Topmost = Topmost;
                ScheduleSave();
            };
            m.Items.Add(top);

            MenuItem auto = Mi("开机自启动", null);
            auto.IsCheckable = true;
            auto.IsChecked = cfg.AutoStart;
            auto.Click += delegate
            {
                bool target = !cfg.AutoStart;
                if (ApplyAutoStart(target))
                {
                    cfg.AutoStart = target;
                    auto.IsChecked = target;
                    ScheduleSave();
                }
                else
                {
                    auto.IsChecked = cfg.AutoStart; // 写入失败，勾选回弹
                    MessageBox.Show(this, "写入开机启动项失败，请检查权限后重试。", "Koi");
                }
            };
            m.Items.Add(auto);

            m.Items.Add(new Separator());
            m.Items.Add(Mi("退出", delegate { Close(); }));
            return m;
        }

        static MenuItem Mi(string header, RoutedEventHandler onClick)
        {
            MenuItem mi = new MenuItem();
            mi.Header = header;
            if (onClick != null) mi.Click += onClick;
            return mi;
        }

        // ---------------------------------------------------------------- 交互

        // 窗口级按下判定：按在空白处 = 拖动窗口；按在图标/名称/＋按钮上 = 交给各自逻辑
        void OnWindowPreviewDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject d = e.OriginalSource as DependencyObject;
            while (d != null)
            {
                if (d is Image) return;
                if (d == plusButton) return;
                TextBlock tb = d as TextBlock;
                if (tb != null && (tb.Tag as string) == "cap") return;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            DragMove();
        }

        // ---------------- 拖动排序（Mac 式实时跟随） ----------------

        void OnItemMouseMove(object sender, MouseEventArgs e)
        {
            if (reorderSource == null || e.LeftButton != MouseButtonState.Pressed) return;
            DockEntry src = reorderSource;
            Point p = e.GetPosition(row);
            if (!dragStarted)
            {
                if (Math.Abs(p.X - reorderStart.X) < 4 && Math.Abs(p.Y - reorderStart.Y) < 4) return;
                dragStarted = true;
                dragEntry = src;
                hoveredEntry = null;
                nameLabel.Visibility = Visibility.Collapsed;
                dragTranslate = new TranslateTransform();
                src.Host.RenderTransform = dragTranslate;
                Panel.SetZIndex(src.Host, 20);          // 抬到所有图标之上
                src.Scale.ScaleX = 1.12; src.Scale.ScaleY = 1.12; // 轻微放大表示"拿起"
                src.Img.CaptureMouse();
            }
            // 先清零平移量再量取槽位基准，避免把旧平移算进中心产生来回跳动
            dragTranslate.X = 0;
            double cx = src.Host.TranslatePoint(new Point(src.Host.ActualWidth / 2.0, 0), row).X;
            dragTranslate.X = p.X - cx;
            // 拖到可视区边缘时自动横向滚动（超宽滚动场景）
            if (scroller != null && scroller.ScrollableWidth > 0)
            {
                double xIn = e.GetPosition(scroller).X;
                if (xIn < 30) scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - 10);
                else if (xIn > scroller.ViewportWidth - 30) scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + 10);
            }
            // 光标越过一个可见槽位宽度即换位：分组视图下只对可见项排序，
            // 再映射回完整列表插入位置，其他分组的相对顺序原样保留
            List<DockEntry> vis = new List<DockEntry>();
            foreach (DockEntry en2 in entries)
                if (en2.Host.Visibility == Visibility.Visible) vis.Add(en2);
            int vi = vis.IndexOf(src);
            if (vi < 0) return;
            double pitch = Math.Max(1, src.Host.ActualWidth + src.Host.Margin.Left + src.Host.Margin.Right);
            int vt = vi + (int)Math.Round((p.X - cx) / pitch);
            if (vt < 0) vt = 0;
            if (vt > vis.Count - 1) vt = vis.Count - 1;
            if (vt != vi)
            {
                // 锚点 = 目标最终插入位置上的可见项（vt 按移除自身后的序列计算，右移不减一）
                vis.RemoveAt(vi);
                DockEntry anchor = vt < vis.Count ? vis[vt] : null;

                entries.Remove(src);
                row.Children.Remove(src.Host);
                int fi = anchor != null ? entries.IndexOf(anchor) : entries.Count;
                entries.Insert(fi, src);
                int ri = anchor != null ? row.Children.IndexOf(anchor.Host) : row.Children.Count;
                row.Children.Insert(ri, src.Host);
                // Insert 只改布局树，坐标要等布局刷新才是新槽位的——强制同步刷新
                row.UpdateLayout();
                // 重新落基准并贴回光标，消除"新槽位＋旧位移"的跳位
                dragTranslate.X = 0;
                double ncx = src.Host.TranslatePoint(new Point(src.Host.ActualWidth / 2.0, 0), row).X;
                dragTranslate.X = p.X - ncx;
                // 刷新过程中捕获可能已被系统剥夺：收尾，防止图标悬空
                if (!src.Img.IsMouseCaptured) { EndReorder(src); return; }
            }

            // 文件堆叠：拖"普通文件"悬停到另一个"文件/文件堆"上时高亮提示，松手合并
            DockEntry newMergeTarget = null;
            if (src.Stackable && (src.Cfg.Stack == null || src.Cfg.Stack.Count == 0))
            {
                foreach (DockEntry en in entries)
                {
                    if (en == src || !en.Stackable) continue;
                    double ecx = en.Img.TranslatePoint(new Point(en.Img.ActualWidth / 2.0, 0), row).X;
                    if (Math.Abs(p.X - ecx) < en.Img.ActualWidth * 0.45) { newMergeTarget = en; break; }
                }
            }
            if (newMergeTarget != dropMergeTarget)
            {
                if (dropMergeTarget != null) dropMergeTarget.Img.Opacity = 1; // 撤销旧目标高亮
                dropMergeTarget = newMergeTarget;
                if (dropMergeTarget != null)
                {
                    dropMergeTarget.Img.Opacity = 0.5;
                    hoveredEntry = dropMergeTarget;
                    ((TextBlock)nameLabel.Child).Text = "松手叠加到「" + dropMergeTarget.Cfg.Name + "」";
                    nameLabel.Visibility = Visibility.Visible;
                    PositionNameLabel(dropMergeTarget);
                }
                else
                {
                    nameLabel.Visibility = Visibility.Collapsed;
                    hoveredEntry = null;
                }
            }
        }

        void EndReorder(DockEntry en)
        {
            if (dragStarted)
            {
                dragStarted = false;
                if (en.Img.IsMouseCaptured) en.Img.ReleaseMouseCapture();
                en.Scale.ScaleX = 1; en.Scale.ScaleY = 1;
                TranslateTransform tt = en.Host.RenderTransform as TranslateTransform;
                if (tt != null)
                {
                    // 回弹落位动画，动画结束再归还变换与层级
                    DoubleAnimation back = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
                    back.Completed += delegate
                    {
                        en.Host.RenderTransform = null;
                        Panel.SetZIndex(en.Host, 0);
                    };
                    tt.BeginAnimation(TranslateTransform.XProperty, back);
                }
                else
                {
                    en.Host.RenderTransform = null;
                    Panel.SetZIndex(en.Host, 0);
                }
                ScheduleSave();
            }
            // 清理拖动过程中的合并目标高亮与提示
            if (dropMergeTarget != null)
            {
                if (dropMergeTarget.Img.Opacity < 1) dropMergeTarget.Img.Opacity = 1;
                dropMergeTarget = null;
            }
            nameLabel.Visibility = Visibility.Collapsed;
            dragEntry = null;
            reorderSource = null;
        }

        void OnFisheye(object sender, MouseEventArgs e)
        {
            if (dragStarted) return; // 拖动中不叠加鱼眼效果
            double mx = e.GetPosition(row).X;
            double sigma = IconSizePx() * 1.35;
            const double amp = FisheyeAmp; // 与顶部留白同参
            foreach (DockEntry en in entries)
            {
                if (en.Img.ActualWidth <= 0) continue;
                double cx = en.Img.TranslatePoint(new Point(en.Img.ActualWidth / 2.0, 0), row).X;
                double d = mx - cx;
                double s = 1.0 + amp * Math.Exp(-(d * d) / (2.0 * sigma * sigma));
                en.Scale.ScaleX = s;
                en.Scale.ScaleY = s;
                Panel.SetZIndex(en.Host, s > 1.2 ? 10 : 0); // 放大的图标盖在邻居上面，同 Mac
                if (en == hoveredEntry) PositionNameLabel(en);
            }
        }

        void ShowNameLabel(DockEntry en)
        {
            ((TextBlock)nameLabel.Child).Text = en.Cfg.Name;
            nameLabel.Visibility = Visibility.Visible;
            PositionNameLabel(en);
        }

        // 标签水平居中于当前图标、贴在 Dock 上沿之上
        void PositionNameLabel(DockEntry en)
        {
            if (en.Img.ActualWidth <= 0) return;
            nameLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = nameLabel.ActualWidth > 0 ? nameLabel.ActualWidth : nameLabel.DesiredSize.Width;
            double cx = en.Img.TranslatePoint(new Point(en.Img.ActualWidth / 2.0, 0), root).X;
            double left = cx - w / 2.0;
            left = Math.Max(2, Math.Min(left, Math.Max(2, root.ActualWidth - w - 2)));
            nameLabel.Margin = new Thickness(left, 0, 0, 0);
        }

        void OnFisheyeLeave(object sender, MouseEventArgs e)
        {
            if (dragStarted) return; // 拖动中不改缩放
            hoveredEntry = null;
            nameLabel.Visibility = Visibility.Collapsed;
            foreach (DockEntry en in entries)
            {
                en.Scale.ScaleX = 1.0;
                en.Scale.ScaleY = 1.0;
                Panel.SetZIndex(en.Host, 0);
            }
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            // 滚轮：图标大小；Ctrl+滚轮：Dock 背景透明度；Shift+滚轮：横向滚动（超宽时）
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                if (scroller != null && scroller.ScrollableWidth > 0)
                {
                    scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
                    e.Handled = true;
                }
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                SetOpacity(cfg.Opacity + (e.Delta > 0 ? 0.06 : -0.06));
            else
                SetIconSize(IconSizePx() + (e.Delta > 0 ? 4 : -4));
        }

        // 依据窗口当前所在显示器的工作区约束位置（WinForms 像素 ↔ WPF DIP 换算）
        void ClampToScreen()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;
                System.Windows.Forms.Screen s = System.Windows.Forms.Screen.FromHandle(h);
                if (s == null) return;
                double dx = 1, dy = 1;
                PresentationSource ps = PresentationSource.FromVisual(this);
                if (ps != null && ps.CompositionTarget != null)
                {
                    Matrix m = ps.CompositionTarget.TransformToDevice;
                    dx = m.M11; dy = m.M22;
                }
                if (dx <= 0) dx = 1;
                if (dy <= 0) dy = 1;
                if (ActualWidth <= 0 || ActualHeight <= 0) return;
                double l = s.WorkingArea.Left / dx;
                double t = s.WorkingArea.Top / dy;
                double r = s.WorkingArea.Right / dx;
                double b = s.WorkingArea.Bottom / dy;
                Left = Math.Max(l, Math.Min(Left, r - ActualWidth));
                Top = Math.Max(t, Math.Min(Top, b - ActualHeight));
            }
            catch { }
        }

        // 所在显示器工作区允许的最大 Dock 宽度（像素换算为 DIP）
        double MaxDockWidth()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return double.MaxValue;
                System.Windows.Forms.Screen s = System.Windows.Forms.Screen.FromHandle(h);
                if (s == null) return double.MaxValue;
                double dx = 1;
                PresentationSource ps = PresentationSource.FromVisual(this);
                if (ps != null && ps.CompositionTarget != null)
                    dx = ps.CompositionTarget.TransformToDevice.M11;
                if (dx <= 0) dx = 1;
                return (s.WorkingArea.Width / dx) * 0.92;
            }
            catch { return double.MaxValue; }
        }

        bool fitting; // 防止 SizeChanged → 缩图标 → SizeChanged 递归

        // 自动调整项目间距：未占满可用宽度时均匀铺开（有上限防过分稀疏），
        // 放不下时收紧到最小间距并启用横向滚动。名称可读性始终优先。
        void UpdateAutoSpacing()
        {
            try
            {
                int n = 0;
                double hostW = 0;
                foreach (DockEntry en in entries)
                {
                    if (en.Host.Visibility != Visibility.Visible) continue; // 隐藏的分组项不占宽
                    n++;
                    hostW = Math.Max(hostW, en.Host.ActualWidth);
                }
                if (n == 0 || hostW <= 0) return; // 尚未布局，等 SizeChanged 再来
                double avail = MaxDockWidth() - (PlusWidth() + 10) - 12 - 24; // 扣除＋按钮区/边距/安全余量
                double mMin = 4;
                double mMax = Math.Max(12, Math.Round(IconSizePx() * 0.6));
                double m = (avail - n * hostW) / (2 * n);
                m = Math.Max(mMin, Math.Min(mMax, Math.Round(m)));
                DockEntry firstVisible = null;
                foreach (DockEntry en in entries)
                    if (en.Host.Visibility == Visibility.Visible) { firstVisible = en; break; }
                if (firstVisible == null) return;
                if (Math.Abs(firstVisible.Host.Margin.Left - m) < 0.5) return; // 无变化不折腾
                fitting = true;
                try
                {
                    foreach (DockEntry en in entries)
                    {
                        if (en.Host.Visibility != Visibility.Visible) continue;
                        en.Host.Margin = new Thickness(m, 0, m, 0);
                    }
                    UpdateLayout();
                }
                finally { fitting = false; }
                ClampToScreen();
            }
            catch { }
        }

        string lastScreenKey;

        // 所在显示器变化（拖动跨屏、改分辨率）时重算宽度上限并重新约束
        void HandleScreenChanged()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;
                System.Windows.Forms.Screen s = System.Windows.Forms.Screen.FromHandle(h);
                if (s == null) return;
                string key = s.DeviceName + "|" + s.WorkingArea.Left + "," + s.WorkingArea.Top + ","
                           + s.WorkingArea.Width + "," + s.WorkingArea.Height;
                if (key == lastScreenKey) return;
                lastScreenKey = key;
                OnWindowSizeChanged();
            }
            catch { }
        }

        // 窗口尺寸随内容变化后：更新宽度上限并约束位置。
        // 不自动缩小图标——名称可读性优先，放不下就走横向滚动
        void OnWindowSizeChanged()
        {
            if (fitting) return;
            fitting = true;
            try
            {
                MaxWidth = MaxDockWidth();
                UpdateAutoSpacing(); // 宽度上限/所在屏幕变化时间距跟着重排
                ClampToScreen();
            }
            finally { fitting = false; }
        }

        void SetIconSize(double requested)
        {
            double v = Math.Max(32, Math.Min(112, Math.Round(requested)));
            if (v == IconSizePx()) return;
            double bottom = Top + ActualHeight; // 底边锚定：变大向上生长
            fitting = true;
            try { ApplyIconSize(v); }
            finally { fitting = false; }
            Top = bottom - ActualHeight;
            ClampToScreen();
            ScheduleSave();
        }

        void ApplyIconSize(double v)
        {
            cfg.IconSize = v;
            if (plusText != null) plusText.FontSize = Math.Max(16, Math.Round(v * 0.30));
            foreach (DockEntry en in entries)
            {
                en.Img.Width = v;
                en.Img.Height = v;
                en.Host.Margin = new Thickness(Math.Round(v * 0.18), 0, Math.Round(v * 0.18), 0); // 基准间距，随后 UpdateAutoSpacing 微调
                if (en.Caption != null) en.Caption.MaxWidth = Math.Max(v + 10, 76);
            }
            root.RowDefinitions[0].Height = new GridLength(FisheyeAmp * v + 6, GridUnitType.Pixel);
            root.RowDefinitions[1].Height = new GridLength(v + 44, GridUnitType.Pixel); // 图标+两行名称
            if (scrollInner != null) scrollInner.Margin = new Thickness(Math.Round(FisheyeAmp * v / 2), Math.Round(FisheyeAmp * v + 6), Math.Round(FisheyeAmp * v / 2) + 12, 10);
            if (scroller != null) scroller.Margin = new Thickness(PlusWidth() + 10, 0, 0, 0);
            UpdateLayout();
            UpdateAutoSpacing(); // 图标尺寸变化后间距重排
        }

        double PlusWidth()
        {
            return plusButton != null && plusButton.ActualWidth > 0 ? plusButton.ActualWidth : 40;
        }

        static readonly Color BaseBg = Color.FromRgb(0x16, 0x19, 0x1E);

        Brush MakeBgBrush()
        {
            byte a = (byte)Math.Round(255 * cfg.Opacity);
            return new SolidColorBrush(Color.FromArgb(a, BaseBg.R, BaseBg.G, BaseBg.B));
        }

        void SetOpacity(double v)
        {
            v = Math.Max(0.25, Math.Min(1.0, Math.Round(v, 2)));
            if (v == cfg.Opacity) return;
            cfg.Opacity = v;
            bg.Background = MakeBgBrush();
            ScheduleSave();
        }

        void Launch(ItemCfg c)
        {
            AddRecent(c.Path); // 记录最近访问（无论启动还是聚焦）
            if (TryActivateRunning(c.Path)) return; // 已在运行：聚焦已有窗口，不重复启动
            try
            {
                Process.Start(new ProcessStartInfo(c.Path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法启动「" + c.Name + "」：" + ex.Message, "Koi");
            }
        }

        // ---------------- 最近访问 / 分组 / 工作组合 ----------------

        void AddRecent(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.StartsWith("shell:")) return;
                string key = NormalizePathKey(path);
                cfg.Recent.RemoveAll(delegate(string r) { return NormalizePathKey(r) == key; });
                cfg.Recent.Insert(0, path);
                while (cfg.Recent.Count > 8) cfg.Recent.RemoveAt(cfg.Recent.Count - 1);
                ScheduleSave();
            }
            catch { }
        }

        // 按当前分组切换各条目可见性（顺序与位置原样保留）
        void ApplyGroupVisibility()
        {
            foreach (DockEntry en in entries)
            {
                bool show = cfg.CurrentGroup == "全部" || en.Cfg.Group == cfg.CurrentGroup;
                en.Host.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateAutoSpacing();
            ClampToScreen();
        }

        // 当前 Dock 中实际出现的分组名（去重）
        List<string> ExistingGroups()
        {
            List<string> g = new List<string>();
            foreach (DockEntry en in entries)
            {
                string gr = en.Cfg.Group;
                if (!string.IsNullOrEmpty(gr) && !g.Contains(gr)) g.Add(gr);
            }
            return g;
        }

        static readonly string CursorExePath =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Programs\\cursor\\Cursor.exe";

        static bool CursorAvailable()
        {
            try { return File.Exists(CursorExePath); } catch { return false; }
        }

        // 在指定目录打开终端：优先 Windows Terminal，回退 PowerShell
        static void OpenTerminalAt(string dir)
        {
            try
            {
                string wt = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft\\WindowsApps\\wt.exe");
                if (File.Exists(wt))
                {
                    Process.Start(new ProcessStartInfo(wt, "-d \"" + dir + "\"") { UseShellExecute = true });
                    return;
                }
            }
            catch { }
            try
            {
                Process.Start(new ProcessStartInfo(
                    "powershell.exe",
                    "-NoExit -Command \"Set-Location -LiteralPath '" + dir + "'\"")
                { UseShellExecute = true });
            }
            catch { }
        }

        static void OpenCursorAt(string dir)
        {
            try
            {
                if (CursorAvailable())
                    Process.Start(new ProcessStartInfo(CursorExePath, "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch { }
        }

        // 通用打开：网址走默认浏览器、目录走资源管理器、其余走系统关联
        static void RunPath(string p)
        {
            try
            {
                if (string.IsNullOrEmpty(p)) return;
                if (Directory.Exists(p)) { Process.Start("explorer.exe", "\"" + p + "\""); return; }
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
            }
            catch (Exception ex) { AppendErrorLog("工作组合打开失败 " + p + "：" + ex.Message); }
        }

        // 多行输入框（工作组合用）：确定返回各行数组，取消返回 null
        string[] ShowLinesInput(string title, string label)
        {
            Window w = new Window();
            w.Title = title;
            w.Owner = this;
            w.Width = 560;
            w.SizeToContent = SizeToContent.Height;
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            StackPanel sp = new StackPanel { Margin = new Thickness(16) };
            TextBlock lb = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            TextBox tb = new TextBox { AcceptsReturn = true, Height = 140, FontSize = 12.5, TextWrapping = TextWrapping.NoWrap };
            StackPanel btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            string[] result = null;
            Button ok = new Button { Content = "确定", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            Button cancel = new Button { Content = "取消", Width = 80 };
            ok.Click += delegate
            {
                result = tb.Text.Split('\n');
                List<string> clean = new List<string>();
                foreach (string line in result)
                {
                    string s = line.Trim().Trim('"');
                    if (s.Length > 0) clean.Add(s);
                }
                result = clean.Count > 0 ? clean.ToArray() : null;
                w.DialogResult = true;
                w.Close();
            };
            cancel.Click += delegate { w.DialogResult = false; w.Close(); };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            sp.Children.Add(lb);
            sp.Children.Add(tb);
            sp.Children.Add(btns);
            w.Content = sp;
            bool? ok2 = w.ShowDialog();
            return (ok2 == true) ? result : null;
        }

        // 点击时若目标程序已在运行，激活它的主窗口（Mac Dock 行为）。
        // 仅对 .exe 条目生效；lnk/url/shell: 条目交由系统启动（Store 应用自身会单实例聚焦）。
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);
        const int SW_RESTORE = 9;

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll")]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int maxCount);
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        struct RECT { public int Left, Top, Right, Bottom; }

        class WinCand { public IntPtr H; public bool Visible; public long Area; }

        // "启动器外壳 + 子目录真身"布局的应用名单：点击这些应用允许目录级进程匹配
        static readonly HashSet<string> LauncherApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "doubao",
        };

        // 解析 .lnk 快捷方式的真实目标 exe（WScript.Shell COM 反射调用，免额外引用）
        static string ResolveLnkTarget(string lnkPath)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (lnk == null) return null;
                return t.InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, lnk, null) as string;
            }
            catch { return null; }
        }

        // 收集某配置路径对应应用的全部进程 Id：
        // .exe 精确匹配进程 exe 与配置路径；.lnk 先解析目标再按上述规则匹配；
        // 已知"启动器外壳 + 子目录真身"布局的应用
        // （见 LauncherApps，如豆包）额外匹配配置 exe 所在目录内的进程。
        HashSet<int> FindAppProcessIds(string path)
        {
            HashSet<int> pids = new HashSet<int>();
            try
            {
                if (string.IsNullOrEmpty(path)) return pids;
                string ext = Path.GetExtension(path);
                bool isExe = string.Compare(ext, ".exe", true) == 0;
                bool isLnk = string.Compare(ext, ".lnk", true) == 0;
                if (!isExe && !isLnk) return pids; // Store 应用/文件夹等不支持进程级匹配
                string target = path;
                if (isLnk)
                {
                    target = ResolveLnkTarget(path);
                    if (string.IsNullOrEmpty(target)) return pids; // 解析不出目标：视为无匹配
                }
                string name = Path.GetFileNameWithoutExtension(target);
                if (string.IsNullOrEmpty(name)) return pids;
                string baseDir = null;
                bool allowDirTree = LauncherApps.Contains(name);
                if (allowDirTree)
                {
                    try { baseDir = Path.GetDirectoryName(target.Trim()); } catch { }
                }
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        try
                        {
                            string exe = p.MainModule != null ? p.MainModule.FileName : null;
                            if (exe == null) continue;
                            exe = exe.Trim();
                            if (string.Compare(exe, target.Trim(), true) == 0) { pids.Add(p.Id); continue; }
                            if (!allowDirTree || baseDir == null) continue;
                            string dir = Path.GetDirectoryName(exe);
                            if (dir == null) continue;
                            if (dir.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
                                && (dir.Length == baseDir.Length
                                    || dir[baseDir.Length] == Path.DirectorySeparatorChar
                                    || dir[baseDir.Length] == Path.AltDirectorySeparatorChar))
                                pids.Add(p.Id);
                        }
                        catch { } // 权限不足拿不到路径的进程不参与匹配
                    }
                }
            }
            catch { }
            return pids;
        }

        bool TryActivateRunning(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                if (string.Compare(Path.GetExtension(path), ".exe", true) != 0) return false;
                HashSet<int> pids = FindAppProcessIds(path);
                if (pids.Count == 0) return false;

                // 枚举该进程组的顶层窗口，挑"有标题且足够大"的真界面。
                // 不能用 Process.MainWindowHandle：Chromium 系应用常指向 52x52 的
                // 无标题辅助窗（如豆包），聚焦它毫无效果。
                List<WinCand> cands = new List<WinCand>();
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    int pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (!pids.Contains(pid)) return true;
                    StringBuilder sb = new StringBuilder(256);
                    GetWindowText(h, sb, 256);
                    if (sb.Length == 0) return true;
                    RECT r;
                    GetWindowRect(h, out r);
                    long area = (long)Math.Abs(r.Right - r.Left) * Math.Abs(r.Bottom - r.Top);
                    if (area < 200L * 120) return true; // 过滤托盘图标、提示气泡等小窗
                    WinCand c = new WinCand();
                    c.H = h;
                    c.Visible = IsWindowVisible(h);
                    c.Area = area;
                    cands.Add(c);
                    return true;
                }, IntPtr.Zero);
                if (cands.Count == 0) return false;

                WinCand best = null;
                foreach (WinCand c in cands)
                    if (c.Visible && (best == null || c.Area > best.Area)) best = c;
                if (best == null)
                    foreach (WinCand c in cands)
                        if (best == null || c.Area > best.Area) best = c;

                // 隐藏/最小化则恢复显示，再前置
                if (!IsWindowVisible(best.H) || IsIconic(best.H)) ShowWindow(best.H, SW_RESTORE);
                SetForegroundWindow(best.H);
                return true;
            }
            catch { }
            return false;
        }

        void OpenLocation(ItemCfg c)
        {
            try { Process.Start("explorer.exe", "/select,\"" + c.Path + "\""); }
            catch { }
        }

        // 复制条目的绝对路径到剪贴板（.lnk 复制原链接路径；Store 应用为其 shell: 形式）。
        // 不走 WPF 的 Clipboard（OLE 层竞争多、易 CLIPBRD_E_CANT_OPEN），直接用 Win32 API 写。
        // 失败时查出占用剪贴板的程序并告知用户；DispatcherTimer 重试（200ms×最多3秒，不阻塞 UI）；
        // 最终失败弹"路径已全选"的小窗，按 Ctrl+C 立即复制。
        DispatcherTimer copyRetryTimer;
        string copyPendingText;
        int copyRetryCount;
        string copyOwnerInfo;
        const int CopyRetryIntervalMs = 200;
        const int CopyMaxRetries = 15;

        // 内存空闲瘦身：常驻工具闲置 5 分钟后回收并裁剪工作集（WPF 常规做法）
        DispatcherTimer trimTimer;
        DateTime lastActivity = DateTime.Now;
        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")]
        static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
        [DllImport("user32.dll")]
        static extern IntPtr GetOpenClipboardOwner();
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")]
        static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr hMem);
        const uint CF_UNICODETEXT = 13;
        const uint GMEM_MOVEABLE = 0x0002;

        // Win32 直写剪贴板（hwnd 必须是有效窗口句柄：OpenClipboard(NULL) + EmptyClipboard
        // 会把所有者置空导致 SetClipboardData 必败——微软文档明确的行为）。
        // 先备好内存再开剪贴板，尽量缩短占用时间；成功返回 null，失败返回带步骤与错误码的描述。
        static string SetClipboardTextWin32(IntPtr hwnd, string text)
        {
            byte[] b = System.Text.Encoding.Unicode.GetBytes(text + "\0");
            IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)b.Length);
            if (hMem == IntPtr.Zero)
                return "GlobalAlloc 失败（Win32 错误码 " + Marshal.GetLastWin32Error() + "）";
            IntPtr p = GlobalLock(hMem);
            if (p == IntPtr.Zero)
            {
                int e = Marshal.GetLastWin32Error();
                GlobalFree(hMem);
                return "GlobalLock 失败（Win32 错误码 " + e + "）";
            }
            Marshal.Copy(b, 0, p, b.Length);
            GlobalUnlock(hMem);

            if (!OpenClipboard(hwnd))
            {
                int e = Marshal.GetLastWin32Error();
                GlobalFree(hMem);
                return "OpenClipboard 失败（Win32 错误码 " + e + "，剪贴板正被其他窗口占用）";
            }
            try
            {
                if (!EmptyClipboard())
                {
                    int e = Marshal.GetLastWin32Error();
                    GlobalFree(hMem);
                    return "EmptyClipboard 失败（Win32 错误码 " + e + "）";
                }
                if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                {
                    int e = Marshal.GetLastWin32Error();
                    GlobalFree(hMem); // 数据未交接成功，仍归我们，需释放
                    return "SetClipboardData 失败（Win32 错误码 " + e + "）";
                }
                return null; // 成功：hMem 所有权已移交系统，勿再释放
            }
            finally
            {
                CloseClipboard();
            }
        }

        // 查当前占用剪贴板的窗口标题与进程名（诊断"是谁占着"）
        static string ClipboardOwnerInfo()
        {
            try
            {
                IntPtr h = GetOpenClipboardOwner();
                if (h == IntPtr.Zero) return null;
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                int pid;
                GetWindowThreadProcessId(h, out pid);
                string proc = null;
                try { using (Process p = Process.GetProcessById(pid)) proc = p.ProcessName; }
                catch { }
                string title = sb.ToString();
                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(proc))
                    return title + "（进程 " + proc + "）";
                if (!string.IsNullOrEmpty(proc)) return "进程 " + proc;
                return title.Length > 0 ? title : null;
            }
            catch { return null; }
        }

        void CopyLocation(ItemCfg c)
        {
            copyPendingText = c.Path;  // 新请求覆盖旧请求，等于取消进行中的重试
            copyRetryCount = 0;
            copyOwnerInfo = null;
            StopCopyRetryTimer();
            TryCopyNow();
        }

        void TryCopyNow()
        {
            string text = copyPendingText;
            if (text == null) return;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                copyPendingText = null;
                ShowCopyError(text, null, copyRetryCount + 1, "窗口句柄未就绪");
                return;
            }

            string err = SetClipboardTextWin32(hwnd, text);
            if (err == null)
            {
                copyPendingText = null; // 成功：静默结束
                return;
            }

            // 只有"剪贴板被其他窗口占用"（OpenClipboard 步骤）值得重试；
            // 内存/数据步骤失败立即报告，附带具体步骤与 Win32 错误码
            bool retryable = err.StartsWith("OpenClipboard");
            copyRetryCount++;
            if (retryable && copyOwnerInfo == null) copyOwnerInfo = ClipboardOwnerInfo();
            if (retryable && copyRetryCount < CopyMaxRetries)
            {
                StartCopyRetryTimer();
                return;
            }
            copyPendingText = null;
            ShowCopyError(text, retryable ? copyOwnerInfo : null, copyRetryCount, err);
        }

        void StartCopyRetryTimer()
        {
            StopCopyRetryTimer();
            copyRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CopyRetryIntervalMs) };
            copyRetryTimer.Tick += delegate
            {
                copyRetryTimer.Stop();
                TryCopyNow();
            };
            copyRetryTimer.Start();
        }

        // 内存瘦身：完整 GC + 把工作集换出（任务管理器中的占用会立刻下降）
        void TrimNow()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try
            {
                SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle,
                    (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }

        void StopCopyRetryTimer()
        {
            if (copyRetryTimer != null) copyRetryTimer.Stop();
        }

        // 兜底小窗：路径文本已全选，按 Ctrl+C 立即复制；挂 Owner 随主窗一起关闭
        void ShowCopyError(string text, string ownerInfo, int attempts, string err)
        {
            Window w = new Window();
            w.Title = "复制失败 - 按 Ctrl+C 直接复制路径";
            w.Owner = this; // 必须挂 Owner：否则 OnLastWindowClose 下该窗口会阻止进程退出
            w.Width = 560;
            w.SizeToContent = SizeToContent.Height;
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Topmost = true;
            w.ShowInTaskbar = false;
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(16);
            TextBlock tip = new TextBlock();
            tip.TextWrapping = TextWrapping.Wrap;
            tip.FontSize = 12.5;
            tip.Margin = new Thickness(0, 0, 0, 10);
            string head = "剪贴板写入失败（已尝试 " + attempts + " 次）";
            if (!string.IsNullOrEmpty(ownerInfo)) head += "，当前占用者：" + ownerInfo;
            if (!string.IsNullOrEmpty(err)) head += "\n失败步骤：" + err;
            tip.Text = head + "\n\n下方路径已全选，按 Ctrl+C 即可复制：";
            sp.Children.Add(tip);
            TextBox tb = new TextBox();
            tb.Text = text;
            tb.IsReadOnly = true;
            tb.FontSize = 13;
            tb.Padding = new Thickness(6, 4, 6, 4);
            sp.Children.Add(tb);
            w.Content = sp;
            w.Loaded += delegate
            {
                tb.Focus();
                tb.SelectAll();
            };
            w.Show();
        }

        // 强制结束应用的全部进程（程序卡死时用）：
        // 先 CloseMainWindow 优雅关闭给 2 秒保存机会，仍未退出的再 Kill。
        // 等待/强杀在后台线程执行，Dock 界面全程不卡；结果回 UI 线程提示。
        void KillApp(ItemCfg c)
        {
            MessageBoxResult r = MessageBox.Show(this,
                "强制结束「" + c.Name + "」的全部进程？\n未保存的数据将会丢失。",
                "Koi", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;

            ThreadPool.QueueUserWorkItem(delegate { DoKillApp(c); });
        }

        void DoKillApp(ItemCfg c)
        {
            string name = c.Name;
            List<Process> created = new List<Process>(); // 所有打开的进程对象，finally 统一释放
            string failReason = null;
            int total = 0, uncertain = 0;
            try
            {
                HashSet<int> pids = FindAppProcessIds(c.Path);
                if (pids.Count == 0)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        MessageBox.Show(this, "「" + name + "」当前没有正在运行的进程。", "Koi");
                    }));
                    return;
                }
                total = pids.Count;

                List<Process> alive = new List<Process>();
                foreach (int id in pids)
                {
                    Process p = null;
                    try
                    {
                        p = Process.GetProcessById(id);
                        created.Add(p);           // 对象一旦创建就必须纳入统一释放
                        if (p.HasExited) continue; // 已不存在：确认跳过
                        alive.Add(p);
                    }
                    catch (Exception ex)
                    {
                        // 拿不到进程或检查失败（权限等）：不确定状态，不能计入成功
                        if (p == null && ex is ArgumentException) continue; // 系统里已无此进程，确认跳过
                        uncertain++;
                        if (failReason == null) failReason = ex.Message;
                    }
                }

                // 第一阶段：优雅关闭
                foreach (Process p in alive)
                {
                    try { p.CloseMainWindow(); } catch { }
                }
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < deadline)
                {
                    alive.RemoveAll(delegate(Process p)
                    {
                        try { return p.HasExited; } // 确认退出
                        catch { return false; }     // 检查失败（权限等）：保留待查，不能当成已退出
                    });
                    if (alive.Count == 0) break;
                    Thread.Sleep(100);
                }

                // 第二阶段：强杀残余
                foreach (Process p in alive)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                }
                deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    alive.RemoveAll(delegate(Process p)
                    {
                        try { return p.HasExited; }
                        catch (Exception ex) { failReason = ex.Message; return false; } // 保留并记录原因
                    });
                    if (alive.Count == 0) break;
                    Thread.Sleep(100);
                }
                uncertain += alive.Count; // 初始化阶段的不确定数 + 强杀后仍未确认的，合并统计
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
            }
            finally
            {
                foreach (Process p in created) try { p.Dispose(); } catch { }
            }

            string msg;
            System.Windows.MessageBoxImage icon = System.Windows.MessageBoxImage.Information;
            if (failReason != null)
            {
                msg = "结束「" + name + "」时出现错误，无法确认进程状态：" + failReason;
                icon = System.Windows.MessageBoxImage.Warning;
            }
            else if (uncertain > 0)
            {
                msg = "「" + name + "」仍有 " + uncertain + " / " + total + " 个进程未能结束（可能需要管理员权限）。";
                icon = System.Windows.MessageBoxImage.Warning;
            }
            else
            {
                msg = "已结束「" + name + "」的 " + total + " 个进程。";
            }
            Dispatcher.BeginInvoke(new Action(delegate
            {
                MessageBox.Show(this, msg, "Koi", MessageBoxButton.OK, icon);
            }));
        }

        // 拖出收尾：关弹窗、清状态；wasPull=true 时把文件从堆中取出并落在光标附近的槽位，
        // 否则视为原地点击（打开该文件）
        void FinishPull(DockEntry from, string path, Point winPt, bool wasPull)
        {
            if (stackPopup != null) { stackPopup.IsOpen = false; }
            stackPopup = null;
            pullFromEntry = null;
            pullPath = null;
            pullActive = false;
            if (wasPull && from != null && !string.IsNullOrEmpty(path))
            {
                try
                {
                    Point sp = PointToScreen(winPt); // 窗口坐标 → 屏幕像素
                    PullOutFileFromStack(from, path, sp.X);
                }
                catch { }
            }
            else if (!string.IsNullOrEmpty(path))
            {
                Launch(new ItemCfg { Path = path });
            }
        }

        // 把文件从堆中取出，作为独立条目插入屏幕坐标对应的槽位；堆按剩余数量自动退化
        void PullOutFileFromStack(DockEntry en, string path, double screenPx)
        {
            List<string> st = en.Cfg.Stack;
            if (st == null || !st.Contains(path)) return;
            st.Remove(path);
            int insertAt = InsertIndexAtScreenX(screenPx);

            if (st.Count == 0)
            {
                // 堆空了：原条目直接变成被拖出的这个文件
                en.Cfg.Path = path;
                en.Cfg.Stack = null;
                en.Cfg.Name = PrettyName(path);
                en.Caption.Text = en.Cfg.Name;
                en.Img.Source = ShellIcons.ResolveIcon(en.Cfg) ?? ShellIcons.DefaultIcon();
                UpdateStackVisual(en);
                ScheduleSave();
                return;
            }
            if (st.Count == 1)
            {
                // 只剩一个：堆退化为普通文件
                en.Cfg.Stack = null;
                en.Cfg.Path = st[0];
            }
            else if (!st.Contains(en.Cfg.Path))
            {
                en.Cfg.Path = st[0]; // 代表图标始终 = 列表首位
            }
            en.Cfg.Name = PrettyName(en.Cfg.Path);
            en.Caption.Text = en.Cfg.Name;
            en.Img.Source = ShellIcons.ResolveIcon(en.Cfg) ?? ShellIcons.DefaultIcon();
            UpdateStackVisual(en);

            ItemCfg c = new ItemCfg { Path = path, Name = PrettyName(path) };
            AddEntry(c, true, insertAt);
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
                    double c = entries[i].Host.TranslatePoint(new Point(entries[i].Host.ActualWidth / 2.0, 0), row).X;
                    if (rp.X < c) return i;
                }
                return entries.Count;
            }
            catch { return entries.Count; }
        }

        void Rename(DockEntry en)
        {
            string s = VB.Interaction.InputBox("输入新名称：", "重命名", en.Cfg.Name, -1, -1);
            if (!string.IsNullOrEmpty(s) && s.Trim() != en.Cfg.Name)
            {
                en.Cfg.Name = s.Trim();
                if (hoveredEntry == en) ((TextBlock)nameLabel.Child).Text = en.Cfg.Name;
                if (en.Caption != null) en.Caption.Text = en.Cfg.Name;
                ScheduleSave();
            }
        }

        void Remove(DockEntry en)
        {
            MessageBoxResult r = MessageBox.Show(this,
                "把「" + en.Cfg.Name + "」从 Dock 移除？（不会删除原文件）",
                "Koi", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
            // 清理悬停标签与拖动状态，避免残留
            if (hoveredEntry == en)
            {
                hoveredEntry = null;
                nameLabel.Visibility = Visibility.Collapsed;
            }
            if (dragEntry == en) EndReorder(en);
            reorderSource = null;
            row.Children.Remove(en.Host); // Host = 图标+名称组合（此前误删旧的单图标元素导致移除不生效）
            entries.Remove(en);
            UpdateHint();
            UpdateAutoSpacing(); // 移除后剩余项目自动铺开
            ScheduleSave();
        }

        // 路径归一化（小写、去结尾分隔符、展开环境变量），用于去重比较
        static string NormalizePathKey(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            string s = p;
            try
            {
                // GetFullPath 统一分隔符方向、消解 "." ".." 段、补全为绝对路径，
                // 并保留盘符根目录语义（"C:\" 的结尾反斜杠不会被去掉）。
                s = Environment.ExpandEnvironmentVariables(p).Trim().Trim('"');
                return Path.GetFullPath(s).ToLowerInvariant();
            }
            catch
            {
                // GetFullPath 失败（非法字符/设备路径等）：退回简单归一化
                try
                {
                    return s.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .ToLowerInvariant();
                }
                catch { return s.ToLowerInvariant(); }
            }
        }

        // 已存在同路径条目时提示用户；返回 true 表示重复（调用方应跳过添加）
        bool WarnIfDuplicate(string path)
        {
            string key = NormalizePathKey(path);
            if (key.Length == 0) return false;
            foreach (DockEntry en in entries)
            {
                if (NormalizePathKey(en.Cfg.Path) == key)
                {
                    MessageBox.Show(this,
                        "「" + en.Cfg.Name + "」已经在 Dock 里了，无需重复添加。",
                        "Koi", MessageBoxButton.OK, MessageBoxImage.Information);
                    return true;
                }
            }
            return false;
        }

        void BrowseAdd()
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择程序或快捷方式";
            dlg.Filter = "程序/快捷方式|*.exe;*.lnk;*.url;*.bat;*.cmd|所有文件|*.*";
            dlg.Multiselect = true;
            if (dlg.ShowDialog(this) == true)
            {
                foreach (string f in dlg.FileNames)
                {
                    if (WarnIfDuplicate(f)) continue;
                    ItemCfg c = new ItemCfg();
                    c.Path = f;
                    c.Name = PrettyName(f);
                    AddEntry(c, true);
                }
            }
        }

        // 添加普通文件（文档等）：点击时用系统关联程序打开（同资源管理器双击）
        void BrowseAddFile()
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择文件（点击图标时用默认程序打开）";
            dlg.Filter =
                "常用文档|*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.pdf;*.txt;*.md;*.csv;*.one;*.xmind|所有文件|*.*";
            dlg.Multiselect = true;
            if (dlg.ShowDialog(this) == true)
            {
                foreach (string f in dlg.FileNames)
                {
                    if (WarnIfDuplicate(f)) continue;
                    ItemCfg c = new ItemCfg();
                    c.Path = f;
                    c.Name = PrettyName(f);
                    AddEntry(c, true);
                }
            }
        }

        // 通过粘贴绝对路径添加：自动去引号、展开环境变量（%APPDATA% 等），
        // 识别文件夹/文件后按类型命名并加入 Dock
        void AddByPath()
        {
            string s = VB.Interaction.InputBox(
                "粘贴程序、快捷方式或文件夹的绝对路径：\n（支持带引号与环境变量，如 %APPDATA%）",
                "通过路径添加", "", -1, -1);
            if (string.IsNullOrEmpty(s)) return;
            s = s.Trim().Trim('"').Trim();
            if (s.Length == 0) return;
            try { s = Environment.ExpandEnvironmentVariables(s); } catch { }
            try { s = Path.GetFullPath(s); } catch { } // 入库前转成规范绝对路径，去重与进程匹配都受益
            if (WarnIfDuplicate(s)) return;
            if (Directory.Exists(s) || File.Exists(s))
            {
                ItemCfg c = new ItemCfg();
                c.Path = s;
                c.Name = PrettyName(s);
                AddEntry(c, true);
            }
            else
            {
                MessageBox.Show(this, "路径不存在：\n" + s, "Koi", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void BrowseAddFolder()
        {
            WinForms.FolderBrowserDialog dlg = new WinForms.FolderBrowserDialog();
            dlg.Description = "选择要加入 Dock 的文件夹";
            dlg.ShowNewFolderButton = false;
            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            {
                if (WarnIfDuplicate(dlg.SelectedPath)) return;
                ItemCfg c = new ItemCfg();
                c.Path = dlg.SelectedPath;
                c.Name = PrettyName(c.Path);
                AddEntry(c, true);
            }
        }

        static string PrettyName(string path)
        {
            try
            {
                // 文件夹名称不能去掉"扩展名"：项目.v1、项目.v2 是两个不同文件夹
                if (Directory.Exists(path))
                {
                    string d = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    return string.IsNullOrEmpty(d) ? path : d;
                }
                string n = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(n)) n = Path.GetFileName(path);
                if (string.IsNullOrEmpty(n)) n = path;
                return n;
            }
            catch { return path; }
        }

        // ---------------------------------------------------------------- 拖放

        bool HasFileDrop(DragEventArgs e)
        {
            return e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (HasFileDrop(e)) bg.BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x7A, 0xB8, 0xFF));
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDragLeave(object sender, DragEventArgs e)
        {
            bg.BorderBrush = normalBorder;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            bg.BorderBrush = normalBorder;
            if (!HasFileDrop(e)) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;
            foreach (string f in files)
            {
                if (WarnIfDuplicate(f)) continue;
                ItemCfg c = new ItemCfg();
                c.Path = f;
                c.Name = PrettyName(f);
                AddEntry(c, true);
            }
        }

        // ---------------------------------------------------------------- 配置

        void LoadConfig()
        {
            // 主配置损坏时回退 .bak；两份都读不出：归档损坏文件留证并提示，
            // 避免随后保存时被默认配置悄悄覆盖丢失现场
            Config c = TryReadConfig(ConfigFile);
            if (c == null) c = TryReadConfig(ConfigFile + ".bak");
            if (c == null)
            {
                bool hadFile = File.Exists(ConfigFile) || File.Exists(ConfigFile + ".bak");
                bool archived = hadFile && ArchiveCorruptConfig();
                if (hadFile)
                {
                    configLoadFailed = !archived; // 归档失败：暂停后续保存，避免覆盖损坏原文件
                    AppendErrorLog(archived
                        ? "配置损坏，已归档为 config.xml.corrupt-*，以默认设置启动。"
                        : "配置损坏且归档失败，已暂停配置保存以保护原文件。");
                    MessageBox.Show(archived
                        ? "Koi 配置文件损坏且无法恢复，原文件已归档为 config.xml.corrupt-*，本次以默认设置启动。"
                        : "Koi 配置文件损坏，且归档失败（文件可能被占用）。\n为保护原文件已暂停自动保存，请手动查看 %APPDATA%\\Koi\\ 下的 config.xml。",
                        "Koi", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }
            cfg.IconSize = c.IconSize >= 32 ? c.IconSize : 64;
            if (cfg.IconSize > 112) cfg.IconSize = 112;
            cfg.Opacity = c.Opacity >= 0.25 ? c.Opacity : 0.72; // 旧配置无此字段(=0)时用默认
            if (cfg.Opacity > 1.0) cfg.Opacity = 1.0;
            cfg.ShowNames = c.ShowNamesSpecified ? c.ShowNames : true; // 旧配置无此字段时默认显示
            cfg.Left = c.Left;
            cfg.Top = c.Top;
            cfg.Topmost = c.Topmost;
            cfg.AutoStart = c.AutoStart;
            if (c.Items != null) cfg.Items = c.Items;

            // 恢复动态内容：最近访问 / 工作组合 / 当前分组（缺了会被下次保存覆盖丢失）
            cfg.Recent = c.Recent != null ? c.Recent : new List<string>();
            cfg.Workflows = c.Workflows != null ? c.Workflows : new List<WorkflowCfg>();
            cfg.CurrentGroup = string.IsNullOrEmpty(c.CurrentGroup) ? "全部" : c.CurrentGroup;
            // 校验当前分组仍存在（有归属条目），失效则回落"全部"
            if (cfg.CurrentGroup != "全部")
            {
                bool groupExists = false;
                foreach (ItemCfg it in cfg.Items)
                {
                    if (it.Group == cfg.CurrentGroup) { groupExists = true; break; }
                }
                if (!groupExists) cfg.CurrentGroup = "全部";
            }
        }

        // 返回 false = 归档失败（文件占用等），调用方应暂停配置保存
        static bool ArchiveCorruptConfig()
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                if (File.Exists(ConfigFile)) File.Move(ConfigFile, ConfigFile + ".corrupt-" + stamp);
                if (File.Exists(ConfigFile + ".bak")) File.Move(ConfigFile + ".bak", ConfigFile + ".bak.corrupt-" + stamp);
                return true;
            }
            catch (Exception ex)
            {
                AppendErrorLog("归档损坏配置失败：" + ex.Message);
                return false;
            }
        }

        static Config TryReadConfig(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                using (FileStream fs = File.OpenRead(file))
                    return new XmlSerializer(typeof(Config)).Deserialize(fs) as Config;
            }
            catch { return null; }
        }

        void ScheduleSave()
        {
            if (saveTimer == null)
            {
                saveTimer = new DispatcherTimer();
                saveTimer.Interval = TimeSpan.FromMilliseconds(700);
                saveTimer.Tick += delegate { saveTimer.Stop(); DoSave(); };
            }
            saveTimer.Stop();
            saveTimer.Start();
        }

        void DoSave()
        {
            if (configLoadFailed) return; // 归档失败的保护：不覆盖损坏的原配置文件
            try
            {
                Directory.CreateDirectory(ConfigDir);
                cfg.Left = Left;
                cfg.Top = Top;
                cfg.ShowNamesSpecified = true; // 确保该字段总是写入
                cfg.Items.Clear();             // 顺序以当前 Dock 视觉顺序为准（拖动排序后）
                foreach (DockEntry en in entries) cfg.Items.Add(en.Cfg);
                // 先写临时文件再原子替换，写坏也只会损坏临时文件；.bak 保留上一份完整配置
                string tmp = ConfigFile + ".tmp";
                using (FileStream fs = File.Create(tmp))
                {
                    XmlSerializer x = new XmlSerializer(typeof(Config));
                    x.Serialize(fs, cfg);
                }
                if (File.Exists(ConfigFile))
                    File.Replace(tmp, ConfigFile, ConfigFile + ".bak");
                else
                    File.Move(tmp, ConfigFile);
            }
            catch (Exception ex)
            {
                AppendErrorLog("保存配置失败：" + ex.Message);
            }
        }

        static void AppendErrorLog(string msg)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.AppendAllText(Path.Combine(ConfigDir, "errors.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine);
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            StopCopyRetryTimer(); // 窗口退出：停止剪贴板重试，避免计时器残留
            copyPendingText = null;
            DoSave();
            base.OnClosed(e);
        }

        static readonly string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        bool IsAutoStartRegistered()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return k != null && k.GetValue("Koi") != null;
            }
            catch { return false; }
        }

        bool ApplyAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return false;
                    if (enable)
                        k.SetValue("Koi", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else if (k.GetValue("Koi") != null)
                        k.DeleteValue("Koi");
                    return true;
                }
            }
            catch { return false; }
        }

        // 不出现在 Alt+Tab 切换列表里（避免干扰正常窗口切换）
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x80;
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        void HideFromAltTab()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
            }
            catch { }
        }
    }

    // =====================================================================
    //  从 Windows Shell 图标缓存取高清图标（优先 256px jumbo，逐级回退）
    // =====================================================================
    static class ShellIcons
    {
        const uint SHGFI_SYSICONINDEX = 0x4000;
        const int ILD_TRANSPARENT = 1;
        const int SHIL_EXTRALARGE = 2;
        const int SHIL_JUMBO = 4;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", EntryPoint = "#727")]
        static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

        [ComImport]
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
            [PreserveSig] int Draw(IntPtr pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        static readonly Dictionary<string, ImageSource> cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase); // 会话级图标缓存：弹层反复打开零成本

        public static ImageSource ResolveIcon(ItemCfg c)
        {
            string key = (c.Path ?? "") + "|" + (c.Icon ?? "");
            ImageSource hit;
            if (cache.TryGetValue(key, out hit)) return hit;
            ImageSource s = ResolveIconCore(c);
            if (s == null) s = DefaultIcon();
            cache[key] = s;
            return s;
        }

        static ImageSource ResolveIconCore(ItemCfg c)
        {
            if (!string.IsNullOrEmpty(c.Icon))
            {
                ImageSource s = LoadImageFile(c.Icon);
                if (s != null) return s;
            }
            return GetIcon(c.Path);
        }

        public static ImageSource LoadImageFile(string file)
        {
            try
            {
                BitmapFrame f = BitmapFrame.Create(new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                f.Freeze();
                return f;
            }
            catch { return null; }
        }

        public static ImageSource GetIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                SHFILEINFO shfi = new SHFILEINFO();
                IntPtr ok = SHGetFileInfo(path, 0, ref shfi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_SYSICONINDEX);
                if (ok != IntPtr.Zero)
                {
                    ImageSource jumbo = FromShellList(SHIL_JUMBO, shfi.iIcon);
                    if (jumbo != null) return jumbo;
                    ImageSource exl = FromShellList(SHIL_EXTRALARGE, shfi.iIcon);
                    if (exl != null) return exl;
                }
            }
            catch { }
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    using (GdiIcon ic = GdiIcon.ExtractAssociatedIcon(path))
                    {
                        if (ic != null)
                        {
                            ImageSource s = FromGdiIcon(ic);
                            if (s != null) return s;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        static ImageSource FromShellList(int shil, int index)
        {
            try
            {
                Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                IImageList list;
                int hr = SHGetImageList(shil, ref iid, out list);
                if (hr != 0 || list == null) return null;
                IntPtr hIcon;
                hr = list.GetIcon(index, ILD_TRANSPARENT, out hIcon);
                if (hr != 0 || hIcon == IntPtr.Zero) return null;
                ImageSource s = FromHIcon(hIcon);
                DestroyIcon(hIcon);
                return s;
            }
            catch { return null; }
        }

        static ImageSource FromHIcon(IntPtr hIcon)
        {
            try
            {
                BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(192, 192)); // 256→192：鱼眼 1.9x 下无感知差异，省 44% 位图内存
                bs.Freeze();
                return bs;
            }
            catch { return null; }
        }

        static ImageSource FromGdiIcon(GdiIcon ic)
        {
            try
            {
                System.Drawing.Bitmap b = ic.ToBitmap();
                IntPtr hb = b.GetHbitmap();
                BitmapSource bs = Imaging.CreateBitmapSourceFromHBitmap(
                    hb, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                DeleteObject(hb);
                b.Dispose();
                bs.Freeze();
                return bs;
            }
            catch { return null; }
        }

        static ImageSource defIcon;
        public static ImageSource DefaultIcon()
        {
            if (defIcon == null)
            {
                using (GdiIcon ic = System.Drawing.SystemIcons.Application) defIcon = FromGdiIcon(ic);
            }
            return defIcon;
        }
    }

    public static class Program
    {
        static Mutex mutex;

        [STAThread]
        static void Main()
        {
            bool createdNew;
            mutex = new Mutex(true, "Koi_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Koi 已经在运行了（看看屏幕底部边缘）。", "Koi");
                return;
            }

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnLastWindowClose;
            DockWindow w = new DockWindow();
            w.Show();
            app.Run();
            GC.KeepAlive(mutex);
        }
    }
}
