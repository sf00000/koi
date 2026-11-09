# 测试、验收与发布恢复

## 自动化回归

在 Windows 仓库目录执行：

```powershell
powershell -NoProfile -File .\test.ps1 -NonInteractive
powershell -NoProfile -File .\build.ps1 -NonInteractive
powershell -NoProfile -File .\tests\ReleaseTests.ps1 -NonInteractive
```

`test.ps1` 使用系统 .NET Framework C# 5 编译器生成并运行 `artifacts/Koi.Tests.exe`。测试入口逐个运行测试组；任何断言失败返回非零退出码。测试不会启动真实目标应用、关闭用户进程或修改用户的 Koi 配置。

| 测试组 | 主要回归场景 |
| --- | --- |
| `StackTests` | 旧 XML 迁移、成员名称/图标/分组保留、拆分、首成员拖出、单项退化、去重和无嵌套快照 |
| `LaunchTests` | 已有窗口激活失败不重复启动、成功才记最近、快捷方式参数与工作目录保留、URL 分类、终端目录和引号处理 |
| `ConfigTests` | 保存往返、有效备份恢复后不被损坏主配置覆盖、双损坏归档、写入失败保护、旧字段与异常数值归一化 |
| `FolderTests` | 读取数量上限、最新项排序、缓存过期、取消结果不入缓存、错误后可重试、并发上限、离线目录恢复探测及真实临时目录读取 |

GitHub Actions 使用 `windows-latest` 跑上述测试，解析 PowerShell 脚本，编译全部根目录 C# 文件并上传 `Koi.exe` / `Koi.exe.sha256`。`ReleaseTests.ps1` 在临时 fixture 中模拟 git/gh 和进程接口，覆盖脏工作区拒绝、测试/构建/推送/上传失败、成功发布后再安装、安装失败后恢复运行、未提交的部分文件写入恢复以及其他路径实例保护；不调用真实 GitHub、git 或用户进程。GUI 行为不由这些单元测试代替。

## Windows 桌面验收

验证时记录 Windows 版本、运行权限、显示器分辨率/缩放、构建 commit 和 exe SHA-256。先备份 `%APPDATA%\Koi\config.xml`，关闭其他 Koi 实例，再启动 `artifacts/Koi.exe`。

| 场景 | 操作与预期 |
| --- | --- |
| 首次使用与退出恢复 | 添加 exe、文件夹、文档与快捷方式；改变名称、大小、透明度、位置；退出重启后配置完整 |
| 文件堆 | 给两个文件分别设置不同名称/图标/分组，合并后拖出首项与末项、拆开、移动分组；文件不丢失，名称/图标/分组符合操作语义；重启再检查 |
| 程序复用 | 测试普通 exe 的未运行、运行中、最小化和多窗口状态；切换受系统拒绝时有提示且不再次启动 |
| 快捷方式 | 分别测试带参数、不同工作目录、无参数的 `.lnk`；行为与双击原快捷方式一致；解析失败不能丢弃原链接 |
| 最近打开与工作组合 | 成功打开的路径进入最近记录，失败不进入；组合内单项失败不应静默记为成功 |
| 特殊文件夹 | 测试含空格、中文、单引号、美元符号、反引号的目录；终端应在该目录打开，不执行目录名中的字符 |
| 目录弹层 | 本地空目录、无权限目录、离线 UNC、超过 40 子目录或 5,000 文件目录；主窗仍可操作，截断/失败有提示，切换或关闭后旧结果不覆盖新弹层 |
| 配置损坏 | 在测试账户或临时配置环境中分别破坏主文件/两份文件；恢复后有效备份不被坏数据覆盖；原损坏文件按规则保留 |
| 显示器 | 100%/150%/200% 混合 DPI、负坐标副屏、跨屏拖动、拔插与改变分辨率；窗口可达、鱼眼不被裁切、弹层不丢失 |
| 系统交互 | 全屏应用、提权应用、剪贴板被占用、屏幕锁定/恢复、远程桌面与高对比度；记录当前不支持或受系统限制的情形 |
| 运行成本 | 比较空 Dock、20/100 条目、较大自定义图标及大目录；记录空闲/悬停/拖拽时 CPU、工作集、专用字节和可感知卡顿 |

当前没有固定硬件的基准报告。不要用“立即释放内存”后的工作集数字代表稳定运行成本；网络文件系统调用也无法保证在取消时立即返回。

## 发布失败恢复

`release.ps1` 必须从干净的、已提交且跟踪 `origin` 分支的 checkout 执行。测试和版本修改发生在临时 clone 中；只提交 `VERSION`、`Program.cs`、`DockWindow.cs`、`Koi.exe`、`Koi.exe.sha256`。脚本不使用 `git add -A`，不自动 reset、强推或删除远端 ref。

成功流程为：依赖/权限预检 → 临时 checkout 改版本 → 测试 → 构建和 checksum → 提交与 tag → 原子推送分支/tag → 草稿 Release → 上传两个附件 → 发布 → 当前 checkout 快进 → 恢复原先运行的目标实例。

失败记录在 `artifacts/release-recovery.txt`，包含阶段、临时 checkout、tag 与发布 commit。先查看两处的 `git status`、远端 tag 和 Release 状态；**不要直接重新运行版本递增脚本**。临时目录内的 commit/tag 和产物会保留，方便继续同一个版本。

| 失败阶段 | 当前 checkout / 已运行程序 | 恢复方式 |
| --- | --- | --- |
| 预检、测试、构建 | 保持原版本及原运行状态 | 修复原因并提交；未产生发布 commit/tag 时可重新发版 |
| 本地提交/tag、推送 | 原 checkout 与程序不变；临时 checkout 保留已完成步骤 | 在临时 checkout 检查 commit/tag；只重试缺少的步骤，推送分支/tag 必须保持原子性 |
| 创建草稿/上传/公开 Release | 远端可能已有新 commit/tag 或草稿，原 checkout 与程序仍不变 | 用现有 tag 完成草稿与附件上传，再公开；不要删远端 tag 来掩盖失败 |
| 本地快进或重启 | 远端 Release 已发布；本地可能仍旧版或已经快进 | 关闭当前路径实例，确认工作区干净后快进到既有发布 commit；需要时手动重启。HEAD 未推进时仅恢复内容仍等于本次候选的部分写入，不覆盖并发编辑；脚本 finally 会尝试恢复原本在运行的实例 |

以下为恢复示例，`<...>` 必须替换为恢复记录和当前 Git 配置中的实际值，先核实远端状态再执行：

```powershell
# 在保留的临时 checkout 中，补推同一 commit 与 tag；禁止 --force。
git push --atomic origin HEAD:refs/heads/<remote-branch> refs/tags/<tag>

# 仅当该 tag 还没有 Release 时，先建草稿。
gh release create <tag> --repo <owner/repository> --verify-tag --draft --title <tag> --generate-notes
# 检查已存在附件后补传；需要覆盖同版本不完整附件时再明确加 --clobber。
gh release upload <tag> .\Koi.exe .\Koi.exe.sha256 --repo <owner/repository>
gh release edit <tag> --repo <owner/repository> --draft=false

# 回到原 checkout，确认未改动，并关闭该路径 Koi 后同步已发布提交。
git fetch origin
git merge --ff-only <release-commit>
.\Koi.exe
```

只在发布验证和本地恢复完成后删除保留的临时 checkout。若原 checkout 在发版期间被其他操作修改，脚本会拒绝覆盖它；先处理这些改动，再手动快进。

## 校验发布文件

```powershell
Get-FileHash .\Koi.exe -Algorithm SHA256
Get-Content .\Koi.exe.sha256
```

比较两个 SHA-256 字符串。当前流程没有 Authenticode 签名和固定构建镜像，因此校验文件用于确认下载内容一致，不构成发布者身份认证或字节级可复现构建保证。
