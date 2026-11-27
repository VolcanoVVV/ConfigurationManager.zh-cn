# 中文版客户端与网页工作台发布包约定

同一仓库维护 F12 交接代码、共享翻译规则与唯一的网页工作台源码 `tools/web-workbench/`。F12 生成 Mono 和 IL2CPP 两个中文版 ZIP，网页工作台单独提供 Windows ZIP。

## 用户目录

客户端 ZIP 首页包含 `安装说明.txt`、`BepInEx/` 和 `说明与工具/`。说明目录包含 AI 与 Agent 汉化指南、依赖检查脚本、构建清单、备用依赖与许可证。

两个运行时共用同一次构建的中文网页工作台。离线术语表使用随包提供的 TSV 范例，通过文本编辑器修改后，导入 F12 或网页工作台。安装路径如下。

- `BepInEx/plugins/ConfigurationManager.zh-cn.dll`。
- IL2CPP 专用的 `BepInEx/plugins/Newtonsoft.Json.dll`。
- `BepInEx/plugins/zh-cn/UI.json`。
- `BepInEx/plugins/zh-cn/glossary/eft.zh-CN.example.tsv`。
- `BepInEx/plugins/zh-cn/translations/`。译文文件通过 `_Manual.jsonc`、`_Manual.overrides.jsonc`、`_API.jsonc` 和 `_MT.jsonc` 后缀归类。
- 独立网页 ZIP 根目录只有一个启动程序 `网页汉化工作台.exe`，运行组件在同名子目录。完整解压到游戏根目录后可以从 F12 启动。网页需要 .NET 8 ASP.NET Core Runtime。

## 打包入口

`tools/package_client_release.py` 默认构建两个客户端 ZIP，输出到 `releases/separate/`。指定 `--no-build` 可复用已经验证的构建产物。正式发版应从对应的源码提交重新构建。

分开发布时可执行：

```powershell
python -X utf8 tools/package_client_release.py --target all --output releases/standalone
```

此命令输出 Mono、IL2CPP 和 Windows 网页工作台三个 ZIP。只打网页用 `--target web`，只打客户端用 `--target client`。GitHub 2.14.0 内置网页属于历史版本。升级时先退出旧服务，再安装独立网页包。

SimpleTranslatorCS 在其仓库构建目标服务端后执行 `python tools/package_release.py --server-only`，生成 SPT 4.0、4.1、5.0 BE 服务端包。网页端的连接方式见 [网页工作台说明](WEB_WORKBENCH.zh-CN.md)。

新版客户端依赖清单使用 `packageLayoutVersion=8` 和 `webWorkbenchSeparate=true`。ZIP 的 SHA-256 由 `SHA256SUMS.txt` 提供。校验器可读取历史布局 2–7，包括旧国际版。依赖脚本默认定位上一级游戏根目录，也可传入 `-GameRoot`。

Windows 文件权限继承发布目录，保证桌面账户可以打开生成的 ZIP。配套服务端项目的联合打包入口也使用相同的客户端打包函数。

## 验证命令

```powershell
python -X utf8 tools/workbench_selftest.py --package-dir releases/standalone
```

发布测试覆盖两个运行时、独立网页包、旧服务退出提示、旧 F12 快照限制、依赖安装和 ZIP 权限。正式发布前还需确认真实 F12 界面与 SPT/Fika 联动。
