# ConfigurationManager.zh-cn 汉化工作台

> [!WARNING]
> **API 翻译由使用者自费。** 工作台使用你提供的供应商账户和密钥。请求一旦到达供应商，即使超时、取消、响应无效或译文未被采用，也可能计费。项目无法保证每次请求都有可用译文，也无法保证实际 Token 用量符合估算。费用以供应商账单为准。
>
> 首次正式启动 API 翻译前，请先在供应商后台检查计费与限额，用少量条目试译。F12 和网页都有费用确认与 **紧急停止 API**。停止可取消本地等待并阻止后续请求，已发送的请求仍可能计费。仅在可信服务器上使用服务端 API 翻译，提交任务时密钥会交给当前服务器。

为 BepInEx ConfigurationManager 的 **F12 模组设置**增加简体中文、精翻、API 与机翻。兼容的 Mono 环境还可接入 BetterBepInEx。独立分发的网页汉化工作台可编辑草稿、连接 SimpleTranslatorCS 管理服务端文本、管理 AI 术语表，以及从静态 CFG 制作词典。

## 下载与快速上手

| 客户端 | 选择的 ZIP |
| --- | --- |
| SPT 3.11.x、4.0.x、4.1.x · BepInEx 5 | 中文版 **Mono** |
| SPT 5.0 BE · BepInEx 6 | 中文版 **IL2CPP** |

1. 先安装对应运行时的 ConfigurationManager。关闭游戏，备份已有译文、术语表和配置。
2. 从 [GitHub Releases](https://github.com/VolcanoVVV/ConfigurationManager.zh-cn/releases) 下载对应 ZIP，完整解压到游戏根目录。
3. 进游戏按 F12，打开「汉化工作台」。原版入口在顶部，兼容的 BetterBepInEx 入口在左侧。可从「精翻」开始校对。
4. 使用网页时另下载「网页汉化工作台」，安装 .NET 8 ASP.NET Core Runtime。将整个网页包解压到游戏根目录后可从 F12 打开。仅管理服务端时，解压到任意目录，双击根目录的「网页汉化工作台.exe」即可连接 SimpleTranslatorCS。

只有 F12 插件设置需要汉化时，安装客户端即可。物品、任务等游戏文本由与服务器版本匹配的 SimpleTranslatorCS 提供。检测到 Fika 时，服务端工作台按服务器管理员密钥授权。

## 文档

- 安装升级、翻译与校对、术语、网页、服务器联动、排错与二次开发。[玩家与开发者手册](README.zh-CN.md)
- 批量汉化、词典格式、本机接口、移植和升级验证。[AI 与 Agent 汉化指南](docs/AI_AGENT_GUIDE.zh-CN.md)
- API 模块的自费确认、5 秒倒计时和安装包选择。[2.14.2 更新说明](docs/RELEASE_NOTES_2.14.2.zh-CN.md)
- 本地构建、依赖验证与打包。[发布包约定](docs/RELEASE_LAYOUT.zh-CN.md)

## 项目结构

| 目录 | 内容 |
| --- | --- |
| [src/](src/) | 客户端源码，按本地化、翻译任务、工作台和游戏集成划分 |
| [Shared/](Shared/) | 客户端、网页和服务端共用的术语、请求与预算规则 |
| [Dependencies/](Dependencies/) | 构建引用和随包依赖 |
| [tools/](tools/) | 网页工作台、打包脚本和一键自测入口 |
| [tests/](tests/) | 客户端、网页和安装包回归测试 |
| [zh-cn/](zh-cn/) | 界面译文与术语表范例 |

根目录中的 `ConfigurationManager.zh-cn.sln` 可打开两个客户端工程。联合打包脚本从根目录的 `ConfigurationManagerZhCnPlugin.cs` 读取客户端版本号。

[项目许可证](LICENSE.txt)
