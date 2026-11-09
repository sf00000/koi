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

        // 会话级图标缓存（LRU，上限 64 个）：弹层反复打开零成本；超容量按最近使用淘汰
        const int IconCacheCap = 64;
        static readonly Dictionary<string, ImageSource> cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> cacheOrder = new List<string>(); // 访问序：尾=最新

        static ImageSource CacheGet(string key)
        {
            ImageSource hit;
            if (!cache.TryGetValue(key, out hit)) return null;
            cacheOrder.Remove(key);
            cacheOrder.Add(key); // 刷新最近使用
            return hit;
        }

        static void CacheStore(string key, ImageSource s)
        {
            if (cache.ContainsKey(key)) cacheOrder.Remove(key);
            cache[key] = s;
            cacheOrder.Add(key);
            while (cacheOrder.Count > IconCacheCap)
            {
                string oldest = cacheOrder[0];
                cacheOrder.RemoveAt(0);
                cache.Remove(oldest);
            }
        }

        public static ImageSource ResolveIcon(ItemCfg c)
        {
            string key = (c.Path ?? "") + "|" + (c.Icon ?? "");
            ImageSource hit = CacheGet(key);
            if (hit != null) return hit;
            ImageSource s = ResolveIconCore(c);
            if (s == null) s = DefaultIcon();
            CacheStore(key, s);
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
                BitmapImage f = new BitmapImage();
                f.BeginInit();
                f.UriSource = new Uri(file);
                f.CacheOption = BitmapCacheOption.OnLoad;
                f.DecodePixelWidth = 192;
                f.EndInit();
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

}
