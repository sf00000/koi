// =====================================================================
//  Koi（锦鲤坞）- Mac 风格的 Windows 悬浮启动坞
//  名字取自锦鲤：鼠标划过时图标如锦鲤聚拢——Dock 放大效果的学名正是「鱼眼 fisheye」
//  - 悬浮在桌面边缘的半透明圆角 Dock 栏，总在最前
//  - 图标悬停时像 Mac 一样放大（鱼眼效果）
//  - 滚轮直接调节图标大小（32~112px），Ctrl+滚轮调节背景透明度（25%~100%）
//  - 拖拽 .exe / 快捷方式 / 文件夹到 Dock 上即可添加
//  - 右键图标：打开 / 打开文件位置 / 重命名 / 移除
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

namespace Koi
{
    public class ItemCfg
    {
        public string Name;
        public string Path;
        public string Icon; // 可选：自定义图标文件（png/jpg/ico），如 Store 应用无法自动取图标时使用
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
        public List<ItemCfg> Items = new List<ItemCfg>();
    }

    public class DockEntry
    {
        public ItemCfg Cfg;
        public Image Img;
        public TextBlock Caption;  // 图标下方常显名称
        public StackPanel Host;    // 图标+名称 的纵向组合，鱼眼 ZIndex 作用在它上面
        public ScaleTransform Scale;
    }

    public class DockWindow : Window
    {
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
            LocationChanged += delegate { ScheduleSave(); };
            SizeChanged += delegate { OnWindowSizeChanged(); }; // 内容变化后统一做屏幕约束
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
                ClampToScreen();
            };
        }

        double IconSizePx() { return cfg.IconSize; }

        // ---------------------------------------------------------------- UI

        void BuildUi()
        {
            root = new Grid();
            root.Margin = new Thickness(18, 6, 18, 12);

            RowDefinition overflow = new RowDefinition();
            overflow.Height = new GridLength(IconSizePx() * 0.8, GridUnitType.Pixel); // 图标放大时向上凸出的空间
            RowDefinition body = new RowDefinition();
            body.Height = GridLength.Auto;

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
                m.Items.Add(Mi("添加程序…", delegate { BrowseAdd(); }));
                m.Items.Add(Mi("添加文件夹…", delegate { BrowseAddFolder(); }));
                m.PlacementTarget = plus;
                m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                m.IsOpen = true;
            };

            StackPanel outer = new StackPanel();
            outer.Orientation = Orientation.Horizontal;
            outer.Children.Add(plus);
            outer.Children.Add(row);
            bg.Child = outer;

            hint = new TextBlock();
            hint.Text = "把程序 / 快捷方式拖到这里，或点左侧 ＋ 添加";
            hint.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xE8, 0xEA, 0xED));
            hint.FontSize = 13;
            hint.Margin = new Thickness(12, 4, 12, 0);
            row.Children.Add(hint);

            Grid.SetRow(bg, 1);
            root.Children.Add(bg);

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

            bg.PreviewMouseLeftButtonDown += OnBgLeftDown;
            bg.ContextMenu = BuildBgMenu();

            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragOver += OnDragOver;
            DragLeave += OnDragLeave;
            Drop += OnDrop;
            PreviewMouseWheel += OnWheel;

            row.MouseMove += OnFisheye;
            row.MouseLeave += OnFisheyeLeave;
        }

        void AddEntry(ItemCfg c, bool save)
        {
            DockEntry en = new DockEntry();
            en.Cfg = c;

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
            img.MouseEnter += delegate { hoveredEntry = en; ShowNameLabel(en); };
            img.MouseLeave += delegate { if (hoveredEntry == en) { hoveredEntry = null; nameLabel.Visibility = Visibility.Collapsed; } };
            img.MouseLeftButtonUp += delegate
            {
                bool wasDrag = dragStarted;
                EndReorder(en);
                if (!wasDrag) Launch(c);
            };
            img.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            img.ContextMenu = BuildItemMenu(en);
            en.Img = img;

            // Mac 式拖动排序：按住移动即抬起跟随光标，实时换位，松手回弹落位
            img.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e2)
            {
                reorderSource = en;
                reorderStart = e2.GetPosition(row);
            };
            img.MouseMove += OnItemMouseMove;
            img.LostMouseCapture += delegate { if (dragEntry == en) EndReorder(en); }; // 系统抢占捕获等中断场景

            // 名称常显在图标下方，超长省略号
            TextBlock cap = new TextBlock();
            cap.Text = c.Name;
            cap.FontSize = 11;
            cap.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xD3, 0xD7, 0xDC));
            cap.TextAlignment = TextAlignment.Center;
            cap.TextTrimming = TextTrimming.CharacterEllipsis;
            cap.MaxWidth = IconSizePx() + 10;
            cap.Margin = new Thickness(0, 2, 0, 0);
            cap.Visibility = cfg.ShowNames ? Visibility.Visible : Visibility.Collapsed;
            cap.MouseLeftButtonUp += delegate { Launch(c); };
            cap.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            cap.ContextMenu = BuildItemMenu(en); // 名称右键 = 图标右键，统一操作
            en.Caption = cap;

            StackPanel host = new StackPanel();
            host.Orientation = Orientation.Vertical;
            host.Margin = new Thickness(5, 0, 5, 0);
            host.Children.Add(img);
            host.Children.Add(cap);
            en.Host = host;

            row.Children.Add(host);
            entries.Add(en);
            UpdateHint();
            if (save) ScheduleSave(); // 屏幕约束统一由 SizeChanged 处理（此时布局尚未刷新）
        }

        void UpdateHint()
        {
            hint.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        ContextMenu BuildItemMenu(DockEntry en)
        {
            ContextMenu m = new ContextMenu();
            m.Items.Add(Mi("打开", delegate { Launch(en.Cfg); }));
            m.Items.Add(Mi("打开文件位置", delegate { OpenLocation(en.Cfg); }));
            m.Items.Add(new Separator());
            m.Items.Add(Mi("重命名…", delegate { Rename(en); }));
            m.Items.Add(Mi("从 Dock 移除", delegate { Remove(en); }));
            return m;
        }

        ContextMenu BuildBgMenu()
        {
            ContextMenu m = new ContextMenu();
            m.Items.Add(Mi("添加程序…", delegate { BrowseAdd(); }));
            m.Items.Add(Mi("添加文件夹…", delegate { BrowseAddFolder(); }));
            m.Items.Add(new Separator());
            m.Items.Add(Mi("图标增大（滚轮 ↑）", delegate { SetIconSize(IconSizePx() + 6); }));
            m.Items.Add(Mi("图标减小（滚轮 ↓）", delegate { SetIconSize(IconSizePx() - 6); }));
            m.Items.Add(Mi("背景更透明（Ctrl+滚轮 ↓）", delegate { SetOpacity(cfg.Opacity - 0.08); }));
            m.Items.Add(Mi("背景更不透明（Ctrl+滚轮 ↑）", delegate { SetOpacity(cfg.Opacity + 0.08); }));
            m.Items.Add(Mi("恢复默认透明度", delegate { SetOpacity(0.72); }));

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

        void OnBgLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource != bg && e.OriginalSource != hint) return; // 点在图标/名称上时不拖动窗口
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
            // 光标越过一个槽位宽度即换位（槽距含左右 Margin）
            int cur = entries.IndexOf(src);
            double pitch = Math.Max(1, src.Host.ActualWidth + src.Host.Margin.Left + src.Host.Margin.Right);
            int target = cur + (int)Math.Round((p.X - cx) / pitch);
            if (target < 0) target = 0;
            if (target > entries.Count - 1) target = entries.Count - 1;
            if (target != cur)
            {
                entries.RemoveAt(cur);
                entries.Insert(target, src);
                row.Children.Remove(src.Host);
                row.Children.Insert(target, src.Host);
                // 换位后槽位基准变了：重新落基准并贴回光标，消除"新槽位＋旧位移"的跳位
                dragTranslate.X = 0;
                double ncx = src.Host.TranslatePoint(new Point(src.Host.ActualWidth / 2.0, 0), row).X;
                dragTranslate.X = p.X - ncx;
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
            dragEntry = null;
            reorderSource = null;
        }

        void OnFisheye(object sender, MouseEventArgs e)
        {
            if (dragStarted) return; // 拖动中不叠加鱼眼效果
            double mx = e.GetPosition(row).X;
            double sigma = IconSizePx() * 1.35;
            const double amp = 0.55;
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
            // 滚轮：图标大小；Ctrl+滚轮：Dock 背景透明度
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

        // 窗口尺寸随内容变化后统一处理：宽过屏幕则自动缩图标，再约束位置
        void OnWindowSizeChanged()
        {
            if (fitting) return;
            fitting = true;
            try
            {
                double bottom = Top + ActualHeight;
                int guard = 0;
                double maxW = MaxDockWidth();
                while (ActualWidth > maxW && cfg.IconSize > 32 && guard++ < 40)
                    ApplyIconSize(Math.Max(32, cfg.IconSize - 4));
                Top = bottom - ActualHeight;
                ClampToScreen();
            }
            finally { fitting = false; }
        }

        void SetIconSize(double requested)
        {
            double v = Math.Max(32, Math.Min(112, Math.Round(requested)));
            // 图标过多时按屏幕宽度封顶，避免窗口比屏幕还宽
            try
            {
                int n = Math.Max(1, entries.Count);
                double maxW = MaxDockWidth();
                while (v > 32 && 56 + n * (v + 10) > maxW) v -= 4;
            }
            catch { }
            v = Math.Round(v);
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
                if (en.Caption != null) en.Caption.MaxWidth = v + 10;
            }
            root.RowDefinitions[0].Height = new GridLength(v * 0.8, GridUnitType.Pixel);
            UpdateLayout();
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

        bool TryActivateRunning(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                if (string.Compare(Path.GetExtension(path), ".exe", true) != 0) return false;
                string name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(name)) return false;

                // 精确匹配：进程 exe 与配置路径一致，或位于配置 exe 所在目录之内
                //（覆盖豆包这类"Application\Doubao.exe 启动器 + app\Doubao.exe 真身"布局）。
                // 不做同名兜底：不同目录的同名程序互不干扰，点击 B 目录的就启动 B 的。
                string baseDir = null;
                try { baseDir = Path.GetDirectoryName(path.Trim()); } catch { }
                HashSet<int> pids = new HashSet<int>();
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        try
                        {
                            string exe = p.MainModule != null ? p.MainModule.FileName : null;
                            if (exe == null) continue;
                            exe = exe.Trim();
                            if (string.Compare(exe, path.Trim(), true) == 0) { pids.Add(p.Id); continue; }
                            if (baseDir == null) continue;
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
            ScheduleSave();
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
                    ItemCfg c = new ItemCfg();
                    c.Path = f;
                    c.Name = PrettyName(f);
                    AddEntry(c, true);
                }
            }
        }

        void BrowseAddFolder()
        {
            WinForms.FolderBrowserDialog dlg = new WinForms.FolderBrowserDialog();
            dlg.Description = "选择要加入 Dock 的文件夹";
            dlg.ShowNewFolderButton = false;
            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            {
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
                ArchiveCorruptConfig();
                if (hadFile)
                    MessageBox.Show("Koi 配置文件损坏且无法恢复，原文件已归档为 config.xml.corrupt-*，本次以默认设置启动。", "Koi",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
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
        }

        static void ArchiveCorruptConfig()
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                if (File.Exists(ConfigFile)) File.Move(ConfigFile, ConfigFile + ".corrupt-" + stamp);
                if (File.Exists(ConfigFile + ".bak")) File.Move(ConfigFile + ".bak", ConfigFile + ".bak.corrupt-" + stamp);
            }
            catch { }
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

        public static ImageSource ResolveIcon(ItemCfg c)
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
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
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
