# FloatDock — Mac 风格的 Windows 悬浮启动坞

一个**零依赖**的单文件小工具：把常用软件做成桌面悬浮图标栏，
鼠标划过时图标像 Mac Dock 一样鱼眼放大，滚轮直接调图标大小。

无需安装任何运行库或框架——纯 C# WPF 单文件源码，用 Windows 自带的
.NET Framework 编译器构建。

![FloatDock](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![FloatDock](https://img.shields.io/badge/dependencies-none-success)
![FloatDock](https://img.shields.io/badge/license-MIT-green)

## 功能

- **悬浮 Dock 栏**：深色半透明圆角背景 + 阴影，默认停在屏幕底部（任务栏上方），可整体拖到任意位置
- **Mac 式鱼眼放大**：鼠标划过时图标放大并凸出到 Dock 上方，相邻图标按距离衰减联动
- **滚轮调图标大小**：32–112px 随意缩放
- **拖拽添加**：把 `.exe` / 快捷方式 / 文件夹 / 文档直接拖到 Dock 上即添加
- **一键启动**：点击图标启动程序（走 Shell，支持 exe / lnk / 文件夹 / Store 应用）
- **右键管理**：打开文件位置、重命名、从 Dock 移除；背景右键有添加程序、大小调整、总在最前、开机自启、退出
- **记住一切**：位置、图标大小、项目列表保存在 `%APPDATA%\FloatDock\config.xml`
- 不占任务栏、不出现在 Alt-Tab
- **支持 Microsoft Store（UWP）应用**和自定义 png 图标（见下文）
- 高清图标：优先从 Shell 图标缓存取 256px 版本

## 使用

双击 `FloatDock.exe` 即可运行。自己编译见下文。

| 操作 | 效果 |
| --- | --- |
| 把程序 / 快捷方式 / 文件夹**拖到 Dock 上** | 添加图标 |
| **点击图标** | 启动 |
| 鼠标悬停 | 放大 + 显示名称 |
| **滚轮** | 调整图标大小 |
| **按住 Dock 空白处拖动** | 移动位置（会记住） |
| 右键图标 | 打开 / 打开文件位置 / 重命名 / 移除 |
| 右键空白处 | 添加程序 / 图标大小 / 总在最前 / 开机自启动 / 退出 |

## 编译（无需安装任何开发工具）

使用 Windows 自带的 .NET Framework 4.x 编译器（Win10/11 均内置）：

```bat
cd /d C:\Windows\Microsoft.NET\Framework64\v4.0.30319
csc -nologo -target:winexe -out:G:\workbuddy\FloatDock\FloatDock.exe ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:Microsoft.VisualBasic.dll ^
  -r:WPF\PresentationFramework.dll -r:WPF\PresentationCore.dll -r:WPF\WindowsBase.dll ^
  -r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll ^
  G:\workbuddy\FloatDock\Program.cs
```

源码是 C# 5 兼容语法（适配内置编译器），单文件 `Program.cs` 约 700 行，注释齐全，方便自行修改样式和行为。

## Microsoft Store（UWP）应用与自定义图标

Store 应用装在带版本号、无权限的系统目录里，不能直接写 exe 路径：

1. 查应用 AUMID：PowerShell 运行 `Get-StartApps`
2. 在 `%APPDATA%\FloatDock\config.xml` 里加条目，`Path` 填 `shell:AppsFolder\<AUMID>`
3. Store 应用取不到自动图标，把它的 logo（`Get-AppxPackage` 拿 InstallLocation 后从 `assets\` 拷一个高清 png）放到任意位置，用 `Icon` 字段指向它

`Icon` 字段对任何条目都有效——想给某个程序换自定义图片，填 png 路径即可。改完配置重启 FloatDock 生效。

## 卸载

1. 右键 Dock → 取消勾选「开机自启动」→ 退出
2. 删除 `FloatDock.exe`
3. 删除 `%APPDATA%\FloatDock\` 文件夹

## 已知边界

- 极少数老程序只有 32px 图标，放大后会略糊
- 「总在最前」开启时全屏应用下 Dock 也会置顶，需要时可在右键菜单关闭

## License

MIT
