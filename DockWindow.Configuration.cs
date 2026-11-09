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
        // ---------------------------------------------------------------- 配置

        readonly ConfigStore configStore = new ConfigStore(ConfigFile, AppendErrorLog);

        void LoadConfig()
        {
            ConfigLoadResult result = configStore.Load();
            cfg = result.Value;
            if (result.RecoveredFromBackup) AppendErrorLog("主配置无法读取，已恢复备份。");
            if (result.Warning != null)
                MessageBox.Show(result.Warning, "Koi", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (!configStore.CanSave) return;
            try
            {
                cfg.Left = Left;
                cfg.Top = Top;
                cfg.Items = new List<ItemCfg>();
                foreach (DockEntry en in entries) cfg.Items.Add(en.Cfg);
                configStore.Save(cfg);
            }
            catch (Exception ex) { AppendErrorLog("保存配置失败：" + ex.Message); }
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
            CancelStackPull();
            CloseFolderFlyout();
            if (saveTimer != null) saveTimer.Stop();
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

    }
}
