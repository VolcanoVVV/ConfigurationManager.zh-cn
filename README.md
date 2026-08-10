# ConfigurationManager.zh-cn 汉化拓展附属插件

这是一个独立的 BepInEx 插件，为 ConfigurationManager 提供中文本地化扩展，无需修改原项目源码即可生效。

## 功能简介

- 读取 `BepInEx/plugins/zh-cn/{GUID}.jsonc`，为插件名称、配置项名称、分类与描述做本地化
- 支持用 `[分类]设置名` 解决同名设置项的重复覆盖
- 枚举/Flags 显示本地化（支持枚举类型全名，避免同名冲突）
- UI 文本本地化（通过 `BepInEx/plugins/zh-cn/ui.json`）
- JSONC/JSON 热更新（修改文件后短时间内自动生效）

## 文件结构

- `BepInEx/plugins/ConfigurationManager.zh-cn.dll`
- `BepInEx/plugins/zh-cn/ui.json`
- `BepInEx/plugins/zh-cn/{PluginGUID}.jsonc`

## JSONC 本地化示例

以下示例用于 `guid = com.example.myplugin` 的插件，文件路径为：

`BepInEx/plugins/zh-cn/com.example.myplugin.jsonc`

```jsonc
{
  // 插件信息
  "PluginInfo": {
    "PluginName": "示例插件（中文）"
  },

  // 配置项本地化
  "ConfigEntries": {
    // 如果存在同名设置项，用 [分类]设置名 来精准匹配
    "[通用]Enabled": {
      "DispName": "启用",
      "Description": "是否启用此功能",
      "Category": "通用"
    },
    "Speed": {
      "DispName": "速度",
      "Description": "移动速度倍率",
      "Category": "高级"
    }
  },

  // 枚举本地化
  "Enum": {
    // 推荐使用枚举全名，避免同名冲突
    "MyPlugin.Config.EffectMode": {
      "Off": "关闭",
      "Low": "低",
      "High": "高"
    }
  }
}
```

注意：
- `ConfigEntries` 的 key 默认用设置项显示名（DispName）匹配
- 若出现同名设置项，使用 `[分类]设置名` 可以避免冲突
- 枚举优先匹配全名（含命名空间），找不到时再匹配短名


## 使用步骤

1) 编译 `ConfigurationManager.zh-cn` 项目，得到 `ConfigurationManager.zh-cn.dll`  
2) 放入 `BepInEx/plugins/`  
3) 在 `BepInEx/plugins/zh-cn/` 目录中创建 `ui.json` 与各插件的 `{GUID}.jsonc`

## 常见问题

- 修改 JSONC/JSON 后不生效：等待 1 秒左右（支持热更新），或重新打开配置窗口。
- 枚举同名冲突：在 `Enum` 中使用枚举类型全名。
