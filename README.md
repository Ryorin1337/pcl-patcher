# pcl-patcher

解除 Plain Craft Launcher 2（PCL2）中 `ModSecret.RestrictMcDownload` 限制的补丁工具。

PCL2 在 2.99 等版本中以 `Public Const RestrictMcDownload As Boolean = True` 的形式在**编译期**禁用了 MC 下载与整合包安装功能。由于 VB 编译器会把 `Const` 内联并折叠，受影响的功能代码在官方 exe 中根本不存在，因此本工具除了把常量改为 `False`，还会用 IL 重建被删除的逻辑。

## 原理

补丁共 5 处（基于 [dnlib](https://github.com/0xd4d/dnlib) 实现）：

| # | 位置 | 修改内容 |
|---|------|----------|
| 1 | `PCL.ModSecret.RestrictMcDownload` | 常量 `True` → `False`（元数据 Constant blob `01` → `00`） |
| 2 | `PCL.PageDownloadInstall.LoaderInit` | 把编译期折叠出来的"关闭下载"假 Loader 替换回真正的 `PCL.ModDownload::DlClientListLoader`，恢复 MC 版本列表下载 |
| 3 | `PCL.PageDownloadModify.LoadInstanceFinished` | `CardMinecraft.Visibility` 由硬编码的 `Collapsed` 改回 `Visible` |
| 4 | `PCL.ModModpack.ModpackInstall(String, String, String)` | 重建被编译器删除的整合包类型分发逻辑（CurseForge / HMCL / MMC / MCBBS / Modrinth / 带启动器压缩包 / 普通压缩包），新增 `ModpackInstallDispatch` 方法 |
| 5 | `PCL.ModModpack.ModpackInstall()` | 重建文件选择对话框入口，选中整合包后调用 3 参数重载进行安装 |

> 注：源码中 `PageDownloadCompDetail` 的相关检查在 2.99 的 exe 中不存在（该类未编译进去），无需处理；`FormMain` 拖放处理中的限制分支被折叠为 `False`，本身无害。

## 使用

```cmd
patch.exe "Plain Craft Launcher 2.exe" patched.exe
```

- 第一个参数：官方下载的原版 `Plain Craft Launcher 2.exe`
- 第二个参数：输出的补丁版 exe
- 生成后可直接替换原文件运行

**本仓库不提供、也不分发任何 PCL2 的二进制文件（无论原版还是补丁版）。** 请自行从官方渠道获取 PCL2 后用本工具打补丁。

## 构建

需要 Windows + .NET Framework 4.x 自带的 `csc.exe`（系统自带，无需安装 SDK），以及 [dnlib](https://www.nuget.org/packages/dnlib) 的 `net45` 版 `dnlib.dll` 放在仓库根目录。

```cmd
build.cmd
```

`build.cmd` 会编译 `src\patch.cs` 并输出 `patch.exe`。

## 免责声明

- 本工具仅供学习与研究，请支持原作者。
- 对使用补丁版本产生的一切后果由使用者自行承担。
- PCL2 版本更新后偏移和方法可能变化，本工具基于 **2.99.0.0** 编写并验证，其他版本不保证可用。

## License

MIT
