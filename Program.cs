// Application entry point and assembly version. Build all root *.cs files.
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

[assembly: System.Reflection.AssemblyVersion("1.8.6.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.8.6.0")]

namespace Koi
{
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
