# Newtonsoft.Json 依赖与打包约定（2026-11-02）

Newtonsoft.Json（Json.NET）负责 JSON 序列化/反序列化，不是翻译模型或网络服务。本插件通过它读取 UI.json、JSONC 译文和原文跟踪记录，处理 AI/机翻及服务端工作台请求/响应，保存日志和凭据的 JSON 结构。人工汉化也会使用它，并非仅在开启 AI 后才需要。

## 客户端版本核对

| 客户端 | 程序所需 CLR Newtonsoft.Json | 正常安装位置 | 发布包策略 |
| --- | --- | --- | --- |
| SPT 4.0 / Mono | 游戏自带 13.0.0.0，netstandard2.0，公钥标记 30ad4fe6b2a6aeed | EscapeFromTarkov_Data/Managed/Newtonsoft.Json.dll | 使用游戏内置；ZIP 另附离线备用，不自动放第二份进插件目录 |
| SPT 4.1 / Mono | 同上；本机文件哈希与 4.0 不同，但程序集身份和需要的 API 相同 | 同上 | 同上，不用备用版覆盖游戏文件 |
| SPT 5.0 BE / IL2CPP | 另需 CLR 版 13.0.0.0，netstandard2.0，公钥标记 30ad4fe6b2a6aeed | BepInEx/plugins/Newtonsoft.Json.dll | ZIP 直接附带，同时附离线备用与检测脚本 |

5.0 BE 的 BepInEx/interop/Newtonsoft.Json.dll 是生成的 IL2CPP 代理，引用 Il2CppInterop.Runtime / Il2Cppmscorlib、公钥标记为 null。它虽然同名且版本号也为 13.0.0.0，不能替代插件需要的 CLR 实现，不能拿它来编译或打包。

本次检查的本地 2.13.1 根目录和 2026-10-10 安装包：IL2CPP ZIP 已包含 CLR 依赖，Mono ZIP 未另附依赖。不能据此证明用户手中的每个历史下载包都相同；诊断旧包仍应核对实际 ZIP 和报错日志。

上述核对针对客户端；SimpleTranslatorCS 服务端是另一进程，不能用服务器中的同名 DLL 来补客户端依赖。

本机三个 SimpleTranslatorCS 服务端构建的程序集引用中均没有 Newtonsoft.Json，分别以 net9.0 / net10.0 / net10.0 为目标；本次不把客户端 JSON DLL 塞入服务端 Mod。

## 缺失时如何处理

完整解压后，关闭游戏，在游戏根目录运行：

```powershell
powershell -File .\Check-Dependencies.ps1 -CheckOnly
powershell -File .\Check-Dependencies.ps1
```

第一条只检查；第二条仅在 CLR 实现缺失时，把 `Dependencies/Newtonsoft.Json/Newtonsoft.Json.dll` 安装至 BepInEx/plugins。已有兼容的游戏/插件副本保持原样；发现不兼容副本则报出位置，不盲目覆盖。此脚本不需要联网，不执行被检查 DLL。完成后重启游戏。

## 维护者打包入口

客户端：

```powershell
python tools/package_client_release.py
```

默认重新构建两种运行时，发布到本项目 releases/2026-11-02-dependencies。每包先在临时目录生成并检查，通过后才替换目标 ZIP；失败不发布半成品。正式打包必须使用此入口或已委托此入口的服务端组合脚本，不能手动压缩 bin 文件夹。

每个 ZIP 强制包含备用 DLL、MIT 许可证、DEPENDENCIES.txt 和检测脚本；IL2CPP 强制额外包含直接加载的 CLR DLL；Mono 禁止在 BepInEx 内另放副本。校验会读取 PE 元数据，核对运行时、引用身份、目标框架、公钥标记、JSON API、固定依赖哈希，并检查 4.0/4.1 的内置程序集。interop 代理、错误运行时插件、缺件或校验失败将阻止发布。

源码中的 Dependencies/Newtonsoft.Json 保存离线依赖、来源和固定校验值；IL2CPP 构建直接引用该位置并将 DLL 复制到输出目录。更新依赖时必须同步检查 manifest、兼容性测试和发布包，不能只替换游戏里的某份 DLL。

包内默认不含个人译文、活动术语表、缓存、密钥和调试符号。版本号仍遵循维护者约定，不因重新打包自动增加。

## 本轮验证

- SPT 4.0 与 4.1 的实际 Mono 引用分别构建并运行回归测试，各通过 382 项断言。
- IL2CPP 使用源码附带的 CLR DLL 构建成功；AI / 机翻 / 模型查询 / 术语 API 冒烟检查通过。
- 14 项发布依赖测试通过，覆盖依赖遗漏、错误运行时、interop 代理、篡改、缺失宿主，以及检测脚本补齐/保留/拒绝覆盖的行为。
- 原组合打包入口在 SimpleTranslatorCS/releases/2026-11-02-dependencies 成功生成并校验五个 ZIP；本项目 releases/2026-11-02-dependencies 提供两个客户端 ZIP 与 SHA256SUMS.txt。
- 保留历史发布目录；尚未向公开下载站上传或替换游戏安装中的 DLL。程序集元数据与本地夹具验证不能替代完整游戏内复测。
