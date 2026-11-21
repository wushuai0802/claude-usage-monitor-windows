# Claude Usage Monitor Windows

> 非官方项目，与 Anthropic 没有关联，也未获其认可或支持。“Claude”是 Anthropic 的商标，这里仅用于说明适用对象。

在 Windows 版 Claude 桌面应用 Code 页的输入框下方、模型按钮左侧显示一行用量：

~~~text
42.6 tok/s | 256.3万 | 93.5% | 5h 24% · 7d 38% | 上下文 26%
~~~

不注入 Claude、不开调试端口、不修改安装包。默认只读本地数据。vm2api 额度需要单独启用。

## 安装

需要 Windows 10/11（64 位）和已经打开过 Code 页的 Claude 桌面应用。安装时用 Windows 自带的 .NET Framework 编译器，不需额外 SDK。

把源码放到长期保留的目录（插件从这里读取），运行：

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
~~~

已打开的会话要重启后才加载新插件。额度和上下文要等会话收到下一次响应后才有读数，之前显示 `--`。

Claude 桌面的第三方模式（`Claude-3p`）有独立的插件目录。在 PowerShell 进程中设置 `CLAUDE_CONFIG_DIR` 为该实例实际使用的配置目录，安装脚本就会在这个目录登记插件。可通过 `-ClaudeCli` 指定桌面实例对应的 `claude.exe`。已安装的第三方会话启动时会拉起小窗。即使当前会话还没加载插件，小窗也能从第三方会话记录计算 Token、缓存命中、上下文 Token 数和估算速率。

### 可选：通过 CC Switch 显示 vm2api 额度

Claude Desktop 已通过 CC Switch 转发到 vm2api 时，在 `%LOCALAPPDATA%\ClaudeUsageMonitor\vm2api.json` 填入网关基址并重启小窗：

~~~json
{"baseUrl":"https://vm2api.example.com"}
~~~

基址须与 CC Switch 选中路由的基址一致。HTTPS、回环与私网/Tailnet HTTP 均可。小窗只在最近的 Claude 会话有匹配的成功代理记录时，复用该路由的 vm2api 托管 Key 请求 `GET /v1/usage?platform=claude`，每分钟至多一次。明细里标注 vm2api 和重置时间。该接口返回网关当前选中的 Claude 账户额度；同一个 Key 可以调度多个账户时，匹配的是网关所选账户，接口没有返回上次推理的账户 ID。未取得额度时状态条省略该项。移走这个配置文件并重启小窗即可关闭。

## 使用

- 点击状态条展开明细，点击明细以外的地方收起；可在“显示设置”里选择显示项、窄窗口保留顺序和配色；右键可暂时隐藏或退出。
- 空间不足时按优先级省略指标，放不下时隐藏。
- 拖动、缩放 Claude 窗口或展开菜单时隐藏，停下后恢复。
- 退出小窗后，下次启动加载了插件的会话会重新拉起它。

## 指标口径

| 指标 | 含义 |
| --- | --- |
| 今日 Token | 当天输入 + 写缓存 + 读缓存 + 输出，按 message.id 去重，含子代理。大部分是缓存读取，不代表计费用量。 |
| 缓存命中 | 读缓存 ÷（输入 + 写缓存 + 读缓存）。 |
| tok/s | 最近一次模型请求的输出 Token ÷ 请求耗时，含首 Token 等待，是估算的平均值。回合进行中保留上一次的读数，空闲时显示 `--`。 |
| 5h / 7d | 已用比例，不是剩余比例。过了重置时间、新读数未到时显示“已重置”。非订阅账号可能没有读数。 |
| 上下文 | 占用百分比；会话没加载插件时显示最新请求的 Token 数，如 `~12万`。 |

tok/s 和上下文取最近活跃的主会话，不一定是正在查看的那个。

## 隐私

- 读取 `%USERPROFILE%\.claude\projects` 和 `%LOCALAPPDATA%\Claude-3p\local-agent-mode-sessions` 下隔离配置的 `.claude\projects` 对话记录（JSONL）。这些文件包含对话内容，小窗只提取用量字段和时间戳，不保存也不显示内容。
- 通过 UI Automation 读取按钮名称和位置来定位。
- 只在 `%LOCALAPPDATA%\ClaudeUsageMonitor` 保存读数、设置、状态和限长日志。
- 默认没有网络请求。单独开启 vm2api 后，仅向配置中匹配的网关查询额度，托管 Key 只从 CC Switch 数据库只读取得并用于当次请求，不写入小窗设置、日志或快照。没有遥测。

## 开发

~~~powershell
.\tests\run-tests.ps1
claude plugin validate .\plugin\usage-monitor
~~~

测试需要 Node.js，正常安装和运行不需要。

## 已知限制

- 定位依赖“模型”／“Model”按钮名称和 Opus／Sonnet／Haiku 名称，其他界面语言和 Claude 改版后可能失效；定位不可靠时隐藏。
- 没有暴露状态的自定义弹层可能被小窗遮住。
- UI Automation 可能让 Chromium 保持无障碍树，有少量开销。
- 混合缩放的多显示器、分屏布局尚未实机验证。

## 卸载

~~~powershell
.\uninstall.ps1
~~~

加 `-RemoveData` 会删除读数、设置和日志，不删除 Claude 自身的数据。

## 许可证

[MIT](LICENSE)
