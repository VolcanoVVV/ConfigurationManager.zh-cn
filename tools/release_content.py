"""Localized installation instructions and supported client archive paths."""

LANGUAGES = ('zh-CN',)
ARCHIVE_LANGUAGES = ('zh-CN', 'en')
LAYOUT_VERSION = 7
COMPACT_LAYOUT_VERSION = 5
CHINESE_WEB_LAYOUT_VERSION = 6
WEB_NAME = '网页汉化工作台'
WEB_INSTALL_DIR = WEB_NAME + '/'
WEB_GUIDE_PATH = {'zh-CN': '说明与工具/网页工作台使用说明.md', 'en': 'Extras/Web-Workbench-Guide.md'}

def web_payload(version=LAYOUT_VERSION):
    name = WEB_NAME if version >= CHINESE_WEB_LAYOUT_VERSION else 'WebWorkbench'
    return name + '/', (
        name + '.exe', name + '.dll', name + '.deps.json', name + '.runtimeconfig.json',
        'Newtonsoft.Json.dll', 'wwwroot/index.html', 'wwwroot/workbench.css', 'wwwroot/workbench.js',
    )


def layout(language, version=LAYOUT_VERSION):
    if language == 'zh-CN':
        paths = dict(readme='安装说明.txt', script='说明与工具/检查并补齐依赖.ps1',
            manifest='说明与工具/依赖与构建信息.txt', payload='说明与工具/依赖备用/Newtonsoft.Json.dll',
            license='说明与工具/依赖备用/许可证.md')
        historical = dict(guide='说明与工具/使用说明.md', glossary='说明与工具/术语表使用说明.md',
            selection='说明与工具/插件选择说明.md', editor='说明与工具/离线术语表编辑器/打开编辑器.html',
            editor_js='说明与工具/离线术语表编辑器/编辑器脚本.js')
        agent = '说明与工具/AI与Agent汉化指南.md'
    elif language == 'en':
        paths = dict(readme='README.txt', script='Extras/Check-Dependencies.ps1', manifest='Extras/DEPENDENCIES.txt',
            payload='Extras/Dependencies/Newtonsoft.Json/Newtonsoft.Json.dll', license='Extras/Dependencies/Newtonsoft.Json/LICENSE.md')
        historical = dict(guide='Extras/User-Guide.md', glossary='Extras/Glossary-Guide.md',
            selection='Extras/Plugin-Selection.md', editor='Extras/Glossary-Editor/index.html',
            editor_js='Extras/Glossary-Editor/glossary-editor.js')
        agent = 'Extras/AI-Agent-Guide.md'
    else:
        raise ValueError('Unknown package language: ' + language)
    paths.update(historical if version < COMPACT_LAYOUT_VERSION else {'agent': agent})
    return paths


def installation(language, runtime, version, web_included):
    if language not in LANGUAGES:
        raise ValueError('Only the Chinese client edition is published')
    paths = layout(language)
    target = ('SPT 3.11 / 4.0 / 4.1' if runtime == 'Mono-SPT4x' else 'SPT 5.0 BE')
    web = f'''4. 在 F12 点「打开网页」，也可双击「{WEB_INSTALL_DIR}{WEB_NAME}.exe」。
   在「快照与草稿」刷新网页数据或确认应用网页草稿。网页需要 .NET 8 ASP.NET Core Runtime，关闭最后一个标签后服务会自动退出。
   从旧版升级时先停止旧网页服务，再使用新的「{WEB_NAME}」目录。
''' if web_included else ''
    return f'''ConfigurationManager.zh-cn {version} · 中文版 · {runtime}（{target}）

1. 关闭游戏，确认已安装对应运行时的 ConfigurationManager。备份个人译文、术语表和 UI.json。
2. 将本包解压到游戏根目录，与 BepInEx 合并。更新 DLL 后完整重启。
3. 按 F12 打开「汉化工作台」。原版入口在顶部，Mono 下兼容的 BetterBepInEx 菜单在左侧显示入口。
   「精翻」可逐条校对，「API」和「机翻」可按插件批量运行。人工译文优先。
{web}
本机术语表可在 F12 编辑，也可离线修改 UTF-8 TSV 后载入。路径和格式见 AI 指南。
旧译文会在首次启动时归类；同层若已有新文件，以新路径为准，旧文件保留。
API 翻译由使用者自费。已发出的请求即使超时、取消或未得到可用译文，也可能计费。
先小批试译并设置供应商消费上限。F12 和网页均可紧急停止后续请求。
排查 API 翻译时，在游戏目录「API翻译日志」中先打开「翻译总览」TXT。
依赖报错时，关闭游戏并运行「{paths['script']}」检查。F12 本机汉化无需服务端组件。
制作汉化、API 配置和离线术语表操作见「{paths['agent']}」。
完整玩家与开发者手册 https://github.com/VolcanoVVV/ConfigurationManager.zh-cn/blob/{version}/README.zh-CN.md
'''
