# 版本号与数字迭代约定

当前版本由项目维护者指定。

| 项目 | 当前版本 |
| --- | --- |
| ConfigurationManager.zh-cn（Mono / IL2CPP / 独立网页） | 2.14.2 |
| SimpleTranslatorCS 服务端（SPT 4.0 / 4.1 / 5.0 BE） | 1.5.0 |

## 修改规则

版本号由项目维护者明确决定。在维护者指定下一个版本号之前，修复、增加功能、重构、测试、重新构建及打包都沿用上述版本，不自动递增补丁号、次版本号或主版本号。不得根据工作量、功能数量、日期或构建次数推断应当升级版本。

需要记录多轮开发进度时，在带日期的文档中记录修改内容及验证结果，不通过频繁跳号区分开发批次。客户端和服务端分别管理版本号，无需同步增加。Mono / IL2CPP 共用客户端版本，三个 SPT 构建目标共用服务端版本。

维护者明确指定新版本后，再统一修改版本元数据、当前文档、安装说明、安装包文件名及 SHA256 校验清单。既往文档记录的已用版本不批量改写。

## 当前版本与维护位置

2026-11-27，维护者将 F12 与独立网页更新至 2.14.2，配套服务端版本为 1.5.0。正式安装包及其校验清单由两个仓库的 GitHub Releases 提供。

- 服务端元数据：`SimpleTranslatorCS.csproj` 的 `<Version>`，`ModMetadata` 从程序集读取。
- 客户端插件版本：`ConfigurationManagerZhCnPlugin.cs` 的 `PluginVersion`。
- 客户端程序集版本取自 `src/Properties/AssemblyInfo.cs` 的 `AssemblyVersion`、`AssemblyFileVersion`，当前为 `2.14.2.0`。
- 网页程序集版本取自 `tools/web-workbench/WebWorkbench.csproj` 的 `<Version>`。
- 客户端使用 `tools/package_client_release.py` 打包。服务端使用服务端项目的 `tools/package_release.py`。脚本读取现有源码版本并生成文件名。

后续接手本项目的维护者或 AI 助手应先遵循本约定。只有用户明确指定或授权更改版本号时，才修改上述数字。
