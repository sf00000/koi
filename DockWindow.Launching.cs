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
        LaunchService launchService;

        LaunchService Launcher
        {
            get
            {
                if (launchService == null) launchService = new LaunchService(new DockLaunchPlatform(this), AddRecent);
                return launchService;
            }
        }

        sealed class DockLaunchPlatform : ILaunchPlatform
        {
            readonly DockWindow owner;
            public DockLaunchPlatform(DockWindow owner) { this.owner = owner; }
            public ShortcutInfo ResolveShortcut(string path) { return ResolveLnk(path); }
            public ActivationResult Activate(string path) { return owner.TryActivateRunning(path); }
            public void Start(ProcessStartInfo startInfo)
            {
                Process process = Process.Start(startInfo);
                if (process != null) process.Dispose();
            }
        }

        void Launch(ItemCfg c)
        {
            if (c == null) return;
            LaunchResult result = Launcher.Launch(c.Path);
            if (!result.Succeeded)
                MessageBox.Show(this, "无法打开「" + (string.IsNullOrEmpty(c.Name) ? PrettyName(c.Path) : c.Name)
                    + "」：" + result.Error, "Koi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---------------- 最近访问 / 分组 / 工作组合 ----------------

        void AddRecent(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
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
            CancelStackPull();
            CloseFolderFlyout();
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
        void OpenTerminalAt(string dir)
        {
            try
            {
                string wt = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft\\WindowsApps\\wt.exe");
                if (File.Exists(wt))
                {
                    LaunchResult terminal = Launcher.LaunchCommand(new ProcessStartInfo(wt, "-d " + LaunchPolicy.QuoteArgument(dir))
                        { UseShellExecute = true }, dir);
                    if (terminal.Succeeded) return;
                }
            }
            catch { }
            LaunchResult result = Launcher.LaunchCommand(LaunchPolicy.PowerShellAt(dir), dir);
            if (!result.Succeeded) MessageBox.Show(this, "无法打开终端：" + result.Error, "Koi");
        }

        void OpenCursorAt(string dir)
        {
            if (!CursorAvailable()) return;
            LaunchResult result = Launcher.LaunchCommand(new ProcessStartInfo(CursorExePath, LaunchPolicy.QuoteArgument(dir))
                { UseShellExecute = true }, dir);
            if (!result.Succeeded) MessageBox.Show(this, "无法打开 Cursor：" + result.Error, "Koi");
        }

        // 通用打开：网址走默认浏览器、目录走资源管理器、其余走系统关联
        void RunPath(string p)
        {
            Launch(new ItemCfg { Path = p, Name = PrettyName(p) });
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

        // 只复用已核实目标的窗口；链接是否适合复用由 LaunchService 决定。
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
        [DllImport("user32.dll")]
        static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT
        {
            public int Length, Flags, ShowCmd;
            public NativePoint MinPosition, MaxPosition;
            public RECT NormalPosition;
        }

        class WinCand { public IntPtr H; public bool Visible; public long Area; }

        // "启动器外壳 + 子目录真身"布局的应用名单：点击这些应用允许目录级进程匹配
        static readonly HashSet<string> LauncherApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "doubao",
        };

        // 读取复用决策所需的链接元数据；真正启动时仍将原链接交给 Shell。
        static ShortcutInfo ResolveLnk(string lnkPath)
        {
            object shell = null, lnk = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                shell = Activator.CreateInstance(t);
                lnk = t.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (lnk == null) return null;
                Type linkType = lnk.GetType();
                return new ShortcutInfo
                {
                    TargetPath = linkType.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, lnk, null) as string,
                    Arguments = linkType.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, lnk, null) as string,
                    WorkingDirectory = linkType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.GetProperty, null, lnk, null) as string
                };
            }
            catch { return null; }
            finally
            {
                if (lnk != null && Marshal.IsComObject(lnk)) Marshal.FinalReleaseComObject(lnk);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        sealed class ProcessMatches
        {
            public readonly HashSet<int> Ids = new HashSet<int>();
            public int Unverified;
            public string Error;
        }

        // 收集可核实路径的匹配进程；不宣称包含不同名称的辅助进程或完整进程树。
        // .exe 精确匹配进程 exe 与配置路径；.lnk 先解析目标再按上述规则匹配；
        // 已知"启动器外壳 + 子目录真身"布局的应用
        // （见 LauncherApps，如豆包）额外匹配配置 exe 所在目录内的进程。
        ProcessMatches FindAppProcesses(string path)
        {
            ProcessMatches matches = new ProcessMatches();
            try
            {
                string target = LaunchPolicy.CleanPath(path);
                bool isExe = LaunchPolicy.IsExecutable(target);
                bool isLnk = LaunchPolicy.IsShortcut(target);
                if (!isExe && !isLnk) return matches; // Store 应用/文件夹等不支持进程级匹配
                if (isLnk)
                {
                    ShortcutInfo shortcut = ResolveLnk(target);
                    if (shortcut == null || !LaunchPolicy.IsExecutable(shortcut.TargetPath))
                    {
                        matches.Error = "无法解析快捷方式的可执行文件目标。";
                        return matches;
                    }
                    target = LaunchPolicy.CleanPath(shortcut.TargetPath);
                }
                target = Path.GetFullPath(target);
                string name = Path.GetFileNameWithoutExtension(target);
                if (string.IsNullOrEmpty(name)) return matches;
                string targetKey = PathIdentity.Normalize(target);
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
                            if (exe == null) { matches.Unverified++; continue; }
                            exe = exe.Trim();
                            if (PathIdentity.Normalize(exe) == targetKey) { matches.Ids.Add(p.Id); continue; }
                            if (!allowDirTree || baseDir == null) continue;
                            string dir = Path.GetDirectoryName(exe);
                            if (dir == null) continue;
                            if (dir.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
                                && (dir.Length == baseDir.Length
                                    || dir[baseDir.Length] == Path.DirectorySeparatorChar
                                    || dir[baseDir.Length] == Path.AltDirectorySeparatorChar))
                                matches.Ids.Add(p.Id);
                        }
                        catch
                        {
                            // Exited processes are not ambiguous; unreadable live processes are.
                            try { if (!p.HasExited) matches.Unverified++; }
                            catch { matches.Unverified++; }
                        }
                    }
                }
            }
            catch (Exception ex) { matches.Error = ex.Message; }
            return matches;
        }

        ActivationResult TryActivateRunning(string path)
        {
            try
            {
                ProcessMatches matches = FindAppProcesses(path);
                if (matches.Error != null) return new ActivationResult(ActivationStatus.Unavailable, "无法检查已有程序：" + matches.Error);
                HashSet<int> pids = matches.Ids;
                if (pids.Count == 0)
                {
                    if (matches.Unverified > 0)
                        return new ActivationResult(ActivationStatus.Unavailable, "存在无法核实路径的同名进程，已停止再次启动；请检查任务栏或进程权限。");
                    return new ActivationResult(ActivationStatus.NotRunning);
                }

                // 枚举该进程组的顶层窗口，挑"有标题且足够大"的真界面。
                // 不能用 Process.MainWindowHandle：Chromium 系应用常指向 52x52 的
                // 无标题辅助窗（如豆包），聚焦它毫无效果。
                List<WinCand> cands = new List<WinCand>();
                bool enumerated = EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    int pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (!pids.Contains(pid)) return true;
                    StringBuilder sb = new StringBuilder(256);
                    GetWindowText(h, sb, 256);
                    if (sb.Length == 0) return true;
                    RECT r;
                    if (!GetWindowRect(h, out r)) return true;
                    if (IsIconic(h))
                    {
                        WINDOWPLACEMENT placement = new WINDOWPLACEMENT();
                        placement.Length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                        if (GetWindowPlacement(h, ref placement)) r = placement.NormalPosition;
                    }
                    long area = (long)Math.Abs(r.Right - r.Left) * Math.Abs(r.Bottom - r.Top);
                    if (area < 200L * 120) return true; // 过滤托盘图标、提示气泡等小窗
                    WinCand c = new WinCand();
                    c.H = h;
                    c.Visible = IsWindowVisible(h);
                    c.Area = area;
                    cands.Add(c);
                    return true;
                }, IntPtr.Zero);
                if (!enumerated || cands.Count == 0)
                    return new ActivationResult(ActivationStatus.Unavailable, "程序已在运行，但没有找到可切换的窗口；请检查任务栏或托盘，未重复启动。");

                WinCand best = null;
                foreach (WinCand c in cands)
                    if (c.Visible && (best == null || c.Area > best.Area)) best = c;
                if (best == null)
                    foreach (WinCand c in cands)
                        if (best == null || c.Area > best.Area) best = c;

                // 隐藏/最小化则恢复显示，再前置
                if (!IsWindowVisible(best.H) || IsIconic(best.H)) ShowWindow(best.H, SW_RESTORE);
                if (!SetForegroundWindow(best.H))
                    return new ActivationResult(ActivationStatus.Unavailable, "已找到运行窗口，但 Windows 未允许切到前台；请从任务栏切换，未重复启动。");
                return new ActivationResult(ActivationStatus.Activated);
            }
            catch (Exception ex) { return new ActivationResult(ActivationStatus.Unavailable, "切换已有窗口失败：" + ex.Message); }
        }

        // 强制结束已核实路径的匹配进程（程序卡死时用）：
        // 先 CloseMainWindow 优雅关闭给 2 秒保存机会，仍未退出的再 Kill。
        // 等待/强杀在后台线程执行，Dock 界面全程不卡；结果回 UI 线程提示。
        void KillApp(ItemCfg c)
        {
            MessageBoxResult r = MessageBox.Show(this,
                "强制结束「" + c.Name + "」中匹配目标路径的进程？\n"
                    + "可能包括该程序的其他窗口；不同名称的辅助进程不在此范围内。\n未保存的数据将会丢失。",
                "Koi", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;

            ThreadPool.QueueUserWorkItem(delegate { DoKillApp(c); });
        }

        void DoKillApp(ItemCfg c)
        {
            string name = c.Name;
            List<Process> created = new List<Process>(); // 所有打开的进程对象，finally 统一释放
            string failReason = null;
            int total = 0, uncertain = 0, unverified = 0;
            try
            {
                ProcessMatches matches = FindAppProcesses(c.Path);
                if (matches.Error != null) throw new InvalidOperationException(matches.Error);
                HashSet<int> pids = matches.Ids;
                unverified = matches.Unverified;
                if (pids.Count == 0)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        MessageBox.Show(this, "没有找到「" + name + "」可核实路径的匹配进程。"
                            + (unverified > 0 ? "\n另有 " + unverified + " 个同名进程无法读取路径，未进行处理。" : ""), "Koi");
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
                msg = "「" + name + "」匹配到的进程中仍有 " + uncertain + " / " + total + " 个未能确认退出（可能需要管理员权限）。";
                icon = System.Windows.MessageBoxImage.Warning;
            }
            else
            {
                msg = "「" + name + "」匹配到的 " + total + " 个进程均已退出。";
            }
            if (unverified > 0) msg += "\n另有 " + unverified + " 个同名进程无法核实路径，未纳入处理。";
            Dispatcher.BeginInvoke(new Action(delegate
            {
                MessageBox.Show(this, msg, "Koi", MessageBoxButton.OK, icon);
            }));
        }

    }
}
