# Claude Usage Monitor Windows

> 非官方项目，与 Anthropic 没有关联，也未获其认可或支持。“Claude”是 Anthropic 的商标，这里仅用于说明适用对象。

在 Windows 版 Claude 桌面应用 Code 页的输入框下方、模型按钮左侧显示一行用量：

~~~text
42.6 tok/s | 256.3万 | 93.5% | 5h 24% · 7d 38% | 上下文 26%
~~~

不注入 Claude、不开调试端口、不修改安装包，不读取账号凭据、不发送网络请求。

## 安装

需要 Windows 10/11（64 位）和已经打开过 Code 页的 Claude 桌面应用。安装时用 Windows 自带的 .NET Framework 编译器，不需额外 SDK。

把源码放到长期保留的目录（插件从这里读取），运行：

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
~~~

已打开的会话要重启后才加载新插件。额度和上下文要等会话收到下一次响应后才有读数，之前显示 `--`。

## 使用

- 点击状态条展开明细，可在“显示设置”里选择显示项、窄窗口保留顺序和配色；右键可暂时隐藏或退出。
- 空间不足时按优先级省略指标，放不下时隐藏。
- 拖动、缩放 Claude 窗口或展开菜单时隐藏，停下后恢复。
- 退出小窗后，下次启动加载了插件的会话会重新拉起它。

## 指标口径

| 指标 | 含义 |
| --- | --- |
| 今日 Token | 当天输入 + 写缓存 + 读缓存 + 输出，按 message.id 去重，含子代理。大部分是缓存读取，不代表计费用量。 |
| 缓存命中 | 读缓存 ÷（输入 + 写缓存 + 读缓存）。 |
| tok/s | 最近一次模型请求的输出 Token ÷ 请求耗时，含首 Token 等待，是估算的平均值。没有新请求时保留上一次的读数。 |
| 5h / 7d | 已用比例，不是剩余比例。过了重置时间、新读数未到时显示“已重置”。非订阅账号可能没有读数。 |
| 上下文 | 占用百分比；会话没加载插件时显示最新请求的 Token 数，如 `~12万`。 |

tok/s 和上下文取最近活跃的主会话，不一定是正在查看的那个。

## 隐私

- 读取 `%USERPROFILE%\.claude\projects` 下的对话记录（JSONL）。这些文件包含对话内容，小窗只提取用量字段和时间戳，不保存也不显示内容。
- 通过 UI Automation 读取按钮名称和位置来定位。
- 只在 `%LOCALAPPDATA%\ClaudeUsageMonitor` 保存读数、设置、状态和限长日志。
- 没有网络请求、遥测或凭据读取。

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
