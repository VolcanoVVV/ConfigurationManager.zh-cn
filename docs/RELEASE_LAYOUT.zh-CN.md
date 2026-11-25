# 中文版客户端发布包约定（2026-11-25）

客户端 2.14.0 生成 Mono 和 IL2CPP 两个中文版 ZIP。

## 用户目录

ZIP 首页包含 `安装说明.txt`、`BepInEx/`、`网页汉化工作台/` 和 `说明与工具/`。安装说明涵盖日常操作。说明目录包含 AI 与 Agent 汉化指南、依赖检查脚本、构建清单、备用依赖与许可证。

两个运行时共用同一次构建的中文网页工作台。离线术语表使用随包提供的 TSV 范例，通过文本编辑器修改后，导入 F12 或网页工作台。安装路径如下。

- `BepInEx/plugins/ConfigurationManager.zh-cn.dll`。
- IL2CPP 专用的 `BepInEx/plugins/Newtonsoft.Json.dll`。
- `BepInEx/plugins/zh-cn/UI.json`。
- `BepInEx/plugins/zh-cn/glossary/eft.zh-CN.example.tsv`。
- `BepInEx/plugins/zh-cn/translations/`。译文文件通过 `_Manual.jsonc`、`_Manual.overrides.jsonc`、`_API.jsonc` 和 `_MT.jsonc` 后缀归类。
- `网页汉化工作台/网页汉化工作台.exe` 及同目录配套文件。将整个目录解压到游戏根目录，以便游戏内一键启动。需要 .NET 8 ASP.NET Core Runtime。

## 打包入口

`tools/package_client_release.py` 编译两个客户端版本并发布网页工作台。指定 `--no-build` 可复用已验证的客户端 DLL 和 `obj/网页汉化工作台/` 内的程序。默认输出目录为 `releases/2.14.0`。

新版依赖清单使用 `packageLayoutVersion=7`，记录网页工作台文件的 SHA256。校验器也可读取历史布局 2–6，包括旧国际版。依赖脚本默认定位上一级游戏根目录，也可传入 `-GameRoot`。

Windows 文件权限继承发布目录，保证桌面账户可以打开生成的 ZIP。配套服务端项目的联合打包入口也使用相同的客户端打包函数。

## 验证命令

```powershell
python tools/package_client_release.py
python -m unittest discover -s tests -p test_release_dependencies.py -v
```

发布测试覆盖两个运行时、随包文档、两包共享的中文网页工作台、依赖安装和 ZIP 权限。
