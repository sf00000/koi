# Koi 锦鲤坞 — Mac 风格的 Windows 悬浮启动坞

把常用软件做成桌面悬浮图标栏：鼠标划过时图标像 Mac Dock 一样**鱼眼放大**，
**滚轮**直接调图标大小。单文件、零依赖，Windows 10/11 开箱即用。

> **为什么叫 Koi（锦鲤）？**
> 鼠标划过时图标聚拢放大的效果，学名叫「鱼眼（fisheye）」——
> 锦鲤浮于水面，指尖所至，鱼群聚拢。好记，也点题。

![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![dependencies](https://img.shields.io/badge/dependencies-none-success)
![license](https://img.shields.io/badge/license-MIT-green)
![language](https://img.shields.io/badge/C%23-WPF-orange)

![Koi 运行效果](docs/dock.png)

## ✨ 功能一览

- **悬浮 Dock 栏**：深色半透明圆角背景 + 投影，默认停在屏幕底部（任务栏正上方），可整体拖到任意位置
- **Mac 式鱼眼放大**：鼠标划过时图标放大并凸出到 Dock 上方，相邻图标按距离联动
- **悬停名称标签**：图标上方浮出深色名称胶囊，跟随鱼眼位置（文件夹、程序都显示）
- **图标名称常显**：每个图标下方显示名称（超长省略号），一眼区分；右键「显示图标名称」可开关
- **文件夹快捷入口**：常用文件夹拖进 Dock 或右键「添加文件夹…」，点击直达，省去层层开窗口
- **滚轮调图标大小**：32–112px 无级缩放
- **Ctrl+滚轮调背景透明度**：25%–100%，让 Dock 融进壁纸（右键菜单也有「背景透明度」入口）
- **拖拽即添加**：`.exe` / 快捷方式 / 文件夹 / 文档，拖上去松手就完成
- **右键全管理**：打开文件位置、重命名、移除；开机自启也在右键菜单里
- **记住一切**：位置、大小、项目列表自动保存，重启后原样恢复
- 干净：不占任务栏、不出现在 Alt-Tab、单进程 ~20MB 内存
- 支持 **Microsoft Store（UWP）应用** 与**自定义 png 图标**
- 高清图标：优先取 Shell 图标缓存的 256px 版本

## 🚀 如何使用

### 第一步：启动

双击 `Koi.exe`（本仓库若未附带 exe，按下方[自己编译](#%EF%B8%8F-自己编译)一节生成）。

首次启动：Dock 出现在**屏幕底部居中、任务栏正上方**，里面有一条提示文字。
之后每次启动都会恢复上次的图标、大小和位置。

### 第二步：添加常用软件（三种方式任选）

| 方式 | 做法 |
| --- | --- |
| **拖拽**（推荐） | 从桌面 / 开始菜单 / 文件夹里把程序图标、快捷方式或文件夹**直接拖到 Dock 上**，松手即添加 |
| 右键菜单 | 右键 Dock 空白处 → **「添加程序…」** → 在弹窗中选择（可多选） |
| 改配置文件 | 编辑 `%APPDATA%\Koi\config.xml`，在 `<Items>` 里加一条 `<ItemCfg>`，保存后重启 Koi |

### 第三步：日常操作

| 操作 | 效果 |
| --- | --- |
| **点击图标** | 程序没在运行 → 启动；**已在运行 → 直接切到它的窗口**（不重复开新实例） |
| 鼠标悬停 | 图标放大（鱼眼效果）+ 上方显示名称标签 |
| **滚轮**（Dock 上滚动） | 放大 / 缩小所有图标（32–112px） |
| **Ctrl + 滚轮** | 调节 Dock 背景透明度（25%–100%，调到最低即近乎隐形的玻璃感） |
| **按住 Dock 空白处拖动** | 把 Dock 移到屏幕任意位置 |
| 右键某个图标 | 打开 / 打开文件位置 / 重命名 / **从 Dock 移除** |
| 右键 Dock 空白处 | 添加程序 / **添加文件夹** / 图标增大减小 / 背景透明度 / **显示图标名称** / 总在最前 / **开机自启动** / 退出 |

### 推荐的初始化顺序

1. 把 5–8 个最常用的软件拖进 Dock
2. 滚轮把图标调到顺眼的大小
3. 拖到喜欢的位置（比如屏幕左侧竖放区、底部居中、或副屏）
4. 右键勾选 **「开机自启动」**——它会自动注册当前 exe 路径，
   所以上一步之前请先把 exe 放到一个不会移动的固定目录

## 📦 Microsoft Store（UWP）应用与自定义图标

Store 应用（如 Codex、终端等）装在带版本号、无权限的系统目录里，
**拖拽和「添加程序」对它们无效**，需要改配置文件：

1. 查应用 AUMID：PowerShell 运行 `Get-StartApps`，找到目标应用的 `AppID`
   （形如 `OpenAI.Codex_2p2nqsd0c76g0!App`）
2. 在 `%APPDATA%\Koi\config.xml` 的 `<Items>` 里加：

   ```xml
   <ItemCfg>
     <Name>应用名</Name>
     <Path>shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App</Path>
     <Icon>C:\path\to\logo.png</Icon>
   </ItemCfg>
   ```

3. `Icon` 指向一张 png（Store 应用取不到自动图标）。
   高清 logo 的获取方式：`Get-AppxPackage -Name <包名>` 拿到 `InstallLocation`，
   从里面的 `assets\` 目录挑一张大尺寸的拷出来
4. 保存并重启 Koi

`Icon` 字段对**任何**条目都有效——普通程序也能换成自定义 png 图标。

## 🔄 开机自启动

三种方式任选其一：

**方式一：程序内一键开启（推荐）**

右键 Dock 空白处 → 勾选 **「开机自启动」**。再点一次取消勾选即关闭。
它内部做的事就是在下面的注册表位置写入一条启动项。

**方式二：手动写注册表**

```bat
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Koi /t REG_SZ /d "\"C:\Koi\Koi.exe\"" /f
```

移除自启：

```bat
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Koi /f
```

**方式三：任务管理器管理**

开启过自启后，「任务管理器 → 启动应用」（或「设置 → 应用 → 启动」）
里会出现 Koi，可随时启用 / 禁用。

> ⚠️ **注意**：自启记录的是开启那一刻 exe 的完整路径。
> 先把 `Koi.exe` 放到固定目录再开启自启；之后挪动文件夹需重新勾选一次。

## 🔧 如何把 Program.cs 编译成 exe

仓库里的 `Koi.exe` 是已经编译好的成品，**不想编译直接用它即可**。
想自己改代码（换背景色、圆角、放大幅度、间距……）或从源码构建时，看这里。

**前提**：Windows 10 / 11 自带 .NET Framework 4.x 编译器——
**不需要安装 Visual Studio、SDK 或任何开发工具**，源码 `Program.cs` 是单文件 C# 5 语法，
约 700 行、注释齐全。

### 方式一：一键编译（推荐）

双击仓库根目录的 **`build.bat`**，它自动调用系统自带的 `csc.exe` 编译器，
在当前目录生成最新的 `Koi.exe`，完成。

> 重新编译前请先退出正在运行的 Koi（右键 Dock → 退出），否则 exe 文件被占用会写入失败。

### 方式二：手动命令行

打开 `cmd`，执行（路径按你的仓库位置调整）：

```bat
cd /d C:\Windows\Microsoft.NET\Framework64\v4.0.30319

csc -nologo -target:winexe -out:G:\workbuddy\Koi\Koi.exe ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:Microsoft.VisualBasic.dll ^
  -r:System.Windows.Forms.dll ^
  -r:WPF\PresentationFramework.dll -r:WPF\PresentationCore.dll -r:WPF\WindowsBase.dll ^
  -r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll ^
  G:\workbuddy\Koi\Program.cs
```

命令说明：

| 部分 | 作用 |
| --- | --- |
| `-target:winexe` | 生成 Windows 窗体程序（双击运行不弹黑控制台） |
| `-out:...` | 输出的 exe 路径 |
| `-r:...` | 引用的系统程序集（WPF 三件套 + 绘图 + 注册表访问） |
| `Program.cs` | 源码，单文件即全部工程 |

### 常见编译问题

- **提示 csc 不存在**：个别精简版系统缺少 .NET Framework 4.x，去微软官网装上即可
- **写不进 Koi.exe**：程序还在运行，先右键 Dock → 退出
- **提示找不到元数据文件**：必须在 `Framework64\v4.0.30319` 目录下执行（WPF 程序集的相对路径才有效），或直接用 `build.bat`

## ❓ 常见问题

**Q：图标放大后有的一侧发虚？**
老程序只带 32px 图标，放大后略糊，属正常。可用 `Icon` 字段换高清 png。

**Q：全屏游戏 / 看视频时 Dock 还浮在上面？**
右键 Dock → 取消「总在最前」，需要时再勾回。

**Q：想临时藏起来？**
目前没有隐藏热键。可以把它拖到屏幕边缘只留一条，或右键退出（配置会保留）。

**Q：开机自启注册的是哪个路径？**
见上方[「开机自启动」](#-开机自启动)章节——记录的是开启那一刻 exe 的完整路径，
挪动文件夹后需重新勾选。

**Q：配置存在哪？**
`%APPDATA%\Koi\config.xml`（XmlSerializer 格式，可直接手工编辑）。
图标文件缓存目录：`%APPDATA%\Koi\icons\`。

## 🗑 卸载

1. 右键 Dock → 取消勾选「开机自启动」→ 退出
2. 删除 `Koi.exe`
3. 删除 `%APPDATA%\Koi\` 文件夹

## License

MIT
