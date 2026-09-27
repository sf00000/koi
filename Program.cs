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
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        DispatcherTimer saveTimer;

        public DockWindow()
        {
            LoadConfig();

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
            LocationChanged += delegate { ScheduleSave(); };
            Loaded += delegate
            {
                if (cfg.Left.HasValue && cfg.Top.HasValue)
                {
                    // 恢复上次位置，并夹在虚拟屏幕范围内（防止换显示器后跑到屏幕外）
                    double l = Math.Max(SystemParameters.VirtualScreenLeft - 40,
                               Math.Min(cfg.Left.Value,
                               SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80));
                    double t = Math.Max(SystemParameters.VirtualScreenTop,
                               Math.Min(cfg.Top.Value,
                               SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60));
                    Left = l;
                    Top = t;
                }
                else
                {
                    // 首次运行：主屏底部居中，落在任务栏正上方
                    Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - ActualWidth) / 2;
                    Top = SystemParameters.WorkArea.Bottom - ActualHeight - 10;
                }
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
            bg.Child = row;

            hint = new TextBlock();
            hint.Text = "把程序 / 快捷方式拖到这里，或右键 → 添加程序";
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
            img.MouseLeftButtonUp += delegate { Launch(c); };
            img.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            img.ContextMenu = BuildItemMenu(en);
            en.Img = img;

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
            if (save) ScheduleSave();
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
                cfg.AutoStart = !cfg.AutoStart;
                auto.IsChecked = cfg.AutoStart;
                ApplyAutoStart(cfg.AutoStart);
                ScheduleSave();
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
            if (e.OriginalSource is Image) return; // 点在图标上时不拖动
            DragMove();
        }

        void OnFisheye(object sender, MouseEventArgs e)
        {
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

        void SetIconSize(double v)
        {
            v = Math.Max(32, Math.Min(112, Math.Round(v)));
            if (v == IconSizePx()) return;
            cfg.IconSize = v;
            foreach (DockEntry en in entries)
            {
                en.Img.Width = v;
                en.Img.Height = v;
                if (en.Caption != null) en.Caption.MaxWidth = v + 10;
            }
            root.RowDefinitions[0].Height = new GridLength(v * 0.8, GridUnitType.Pixel);
            ScheduleSave();
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
        const int SW_RESTORE = 9;

        bool TryActivateRunning(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                if (string.Compare(Path.GetExtension(path), ".exe", true) != 0) return false;
                string name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(name)) return false;
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        ShowWindow(p.MainWindowHandle, SW_RESTORE); // 最小化则恢复
                        SetForegroundWindow(p.MainWindowHandle);     // 前置（点击后进程有前台权）
                        return true;
                    }
                }
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
            row.Children.Remove(en.Img);
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
            try
            {
                if (!File.Exists(ConfigFile)) return;
                using (FileStream fs = File.OpenRead(ConfigFile))
                {
                    XmlSerializer x = new XmlSerializer(typeof(Config));
                    Config c = x.Deserialize(fs) as Config;
                    if (c == null) return;
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
            }
            catch { }
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
                using (FileStream fs = File.Create(ConfigFile))
                {
                    XmlSerializer x = new XmlSerializer(typeof(Config));
                    x.Serialize(fs, cfg);
                }
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            DoSave();
            base.OnClosed(e);
        }

        void ApplyAutoStart(bool enable)
        {
            try
            {
                RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (k == null) return;
                if (enable)
                    k.SetValue("Koi", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                else if (k.GetValue("Koi") != null)
                    k.DeleteValue("Koi");
                k.Close();
            }
            catch { }
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
