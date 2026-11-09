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
        // 文件夹悬停就地展开：子目录 + 最近修改的文件 + 终端/编辑器直达
        DispatcherTimer folderFlyoutCloseTimer;
        readonly FolderService folderService = new FolderService();
        CancellationTokenSource folderRequest;
        CancellationTokenSource folderProbe;
        DockEntry folderProbeEntry;

        async void ProbeFolder(DockEntry en)
        {
            CancelFolderProbe();
            if (dragStarted || pullFromEntry != null) return;
            CancellationTokenSource request = new CancellationTokenSource();
            folderProbe = request;
            folderProbeEntry = en;
            try
            {
                bool exists = await folderService.IsDirectoryAsync(en.Cfg.Path, request.Token);
                if (request.IsCancellationRequested || !entries.Contains(en)) return;
                en.IsFolder = exists;
                if (exists && en.Img.IsMouseOver)
                {
                    hoveredEntry = null;
                    nameLabel.Visibility = Visibility.Collapsed;
                    ShowFolderFlyout(en);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppendErrorLog("检查文件夹失败：" + ex.Message); }
            finally
            {
                if (folderProbe == request) { folderProbe = null; folderProbeEntry = null; }
                request.Dispose();
            }
        }

        void CancelFolderProbe()
        {
            CancellationTokenSource previous = folderProbe;
            folderProbe = null;
            folderProbeEntry = null;
            if (previous != null) previous.Cancel();
        }

        async void ShowFolderFlyout(DockEntry en)
        {
            if (dragStarted || pullFromEntry != null) return;
            CloseFolderFlyout();
            string dir = en.Cfg.Path;

            StackPanel listPanel = new StackPanel { MinWidth = 260 };

            // 头部动作：打开文件夹 / 在此打开终端 / 用 Cursor 打开 / 复制文件夹路径
            listPanel.Children.Add(FlyoutRow("📂 打开文件夹", delegate { RunPath(dir); }));
            listPanel.Children.Add(FlyoutRow("⌨ 在此打开终端", delegate { OpenTerminalAt(dir); }));
            listPanel.Children.Add(FlyoutRow("📋 复制文件夹路径", delegate
            {
                CloseFolderFlyout();
                CopyLocation(new ItemCfg { Path = dir, Name = en.Cfg.Name });
            }));
            if (CursorAvailable())
                listPanel.Children.Add(FlyoutRow("✎ 用 Cursor 打开", delegate { OpenCursorAt(dir); }));
            listPanel.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(4, 4, 4, 4)
            });

            StackPanel contents = new StackPanel();
            contents.Children.Add(FolderStatus("正在读取目录…"));
            listPanel.Children.Add(contents);

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
                StaysOpen = true, // 不吞点击：左键点图标照常打开文件夹；收起交给鼠标离开计时器
                AllowsTransparency = true,
                Child = bd
            };
            popup.MouseEnter += delegate { if (folderFlyoutCloseTimer != null) folderFlyoutCloseTimer.Stop(); };
            popup.MouseLeave += delegate { if (stackPopup == popup) CloseFolderFlyout(); };
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
            CancellationTokenSource request = new CancellationTokenSource();
            folderRequest = request;
            try
            {
                FolderSnapshot snapshot = await folderService.ReadAsync(dir, request.Token);
                if (request.IsCancellationRequested || stackPopup != popup || !popup.IsOpen) return;
                contents.Children.Clear();
                foreach (string directory in snapshot.Directories)
                {
                    string captured = directory;
                    contents.Children.Add(FlyoutRow("📁 " + FolderName(directory), delegate
                    {
                        CloseFolderFlyout();
                        Launch(new ItemCfg { Path = captured, Name = FolderName(captured) });
                    }));
                }
                foreach (FolderFile file in snapshot.Files)
                {
                    string captured = file.Path;
                    contents.Children.Add(FlyoutRow("📄 " + Path.GetFileName(captured), delegate
                    {
                        CloseFolderFlyout();
                        Launch(new ItemCfg { Path = captured, Name = Path.GetFileName(captured) });
                    }));
                }
                if (snapshot.MoreDirectories)
                    contents.Children.Add(FolderStatus("仅显示前 " + FolderService.DirectoryLimit + " 个子目录；打开文件夹查看全部。"));
                if (snapshot.FileScanLimited)
                    contents.Children.Add(FolderStatus("仅扫描前 " + FolderService.FileScanLimit + " 个文件；最近修改排序限于已扫描文件。"));
                if (snapshot.Error != null) contents.Children.Add(FolderStatus(snapshot.Error));
                else if (snapshot.Directories.Count == 0 && snapshot.Files.Count == 0)
                    contents.Children.Add(FolderStatus("文件夹为空。"));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppendErrorLog("读取文件夹失败：" + ex.Message);
                if (stackPopup == popup && popup.IsOpen)
                {
                    contents.Children.Clear();
                    contents.Children.Add(FolderStatus("读取失败，请打开文件夹查看。"));
                }
            }
            finally
            {
                if (folderRequest == request) folderRequest = null;
                request.Dispose();
            }
        }

        static string FolderName(string path)
        {
            string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        static TextBlock FolderStatus(string message)
        {
            return new TextBlock { Text = message, Foreground = Brushes.LightGray,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Margin = new Thickness(8, 6, 8, 6) };
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
            CancelFolderProbe();
            if (folderRequest != null) { folderRequest.Cancel(); folderRequest = null; }
            if (folderFlyoutCloseTimer != null) folderFlyoutCloseTimer.Stop();
            System.Windows.Controls.Primitives.Popup previous = stackPopup;
            stackPopup = null;
            if (previous != null) previous.IsOpen = false;
        }

    }
}
