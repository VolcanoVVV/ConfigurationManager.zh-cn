# ConfigurationManager.zh-cn 2.14.1

本次提供 F12 Mono、F12 IL2CPP 和独立网页汉化工作台三个安装包。只使用 F12 时选择对应的客户端包。需要编辑物品、任务等服务端文本时，再安装匹配 SPT 版本的 [SimpleTranslatorCS 1.5.0](https://github.com/VolcanoVVV/SimpleTranslatorCS/releases/tag/1.5.0)。

## 相比 2.14.0

- 网页端改为独立安装，使用根目录的 `网页汉化工作台.exe`。完整解压到游戏根目录后可从 F12 打开。单独管理服务端时可直接启动网页工作台，再连接 SPT。
- 网页支持精翻和 API 批量模式、来源与字段多选、跨页全选和排除个别条目。模型可以刷新列表或手动输入，紧急停止和取消等待放在 API 操作区。
- 网页可拉取并发布 SimpleTranslatorCS 文本。连接时先检测服务端能力。Fika 和远程管理需要管理员密钥，远程地址还需要受信任的 HTTPS。
- 同一工作区的新版服务共用一个本地进程，最后一个网页标签关闭后会退出。升级时先从旧页面停止旧服务。译文、术语表和草稿保留在原有数据目录。
- 新版 F12 支持多插件草稿。仍在使用 2.14.0 的游戏可继续单插件编辑。升级 F12 后，在游戏里刷新快照即可开始跨插件编辑或翻译。

## 选择安装包

| 安装包 | 适用环境 |
| --- | --- |
| F12 Mono | SPT 3.11.x、4.0.x、4.1.x / BepInEx 5 |
| F12 IL2CPP | SPT 5.0 BE / BepInEx 6。其他 BE 构建可能需要匹配的依赖。 |
| 独立网页工作台 | Windows / .NET 8 ASP.NET Core Runtime |

升级前备份 `BepInEx/plugins/zh-cn/`。使用 API 前按供应商的实际计费设置消费上限。网页预算按批次计算，已发送的请求在停止、超时或响应无效时仍可能计费。

详见 [玩家与开发者手册](../README.zh-CN.md) 和 [网页工作台说明](WEB_WORKBENCH.zh-CN.md)。
