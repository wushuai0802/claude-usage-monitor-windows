# Claude Usage Monitor Windows

> 非官方项目，与 Anthropic 没有关联，也未获其认可或支持。“Claude”是 Anthropic 的商标，这里仅用于说明适用对象。

在 Windows 版 Claude 桌面应用 Code 页的输入框下方、模型按钮左侧显示一行用量：

~~~text
42.6 tok/s | 256.3万 | 93.5% | 5h 24% · 7d 38% | 上下文 26%
~~~

不注入 Claude、不开调试端口、不修改安装包，不读取账号凭据、不发送网络请求。

## 安装

需要 Windows 10/11（64 位）和已经打开过 Code 页的 Claude 桌面应用。安装时用 Windows 自带的 .NET Framework 编译器，不需额外 SDK。

把源码放到长期保留的目录，运行：

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
~~~

插件从该目录读取。现有会话可能继续使用已加载的旧 hook，直到正常重新加载；新版浮层兼容旧读数，不强制重启 Claude。新版额度和上下文读数要等加载了插件的会话收到响应后产生，之前显示已有读数及时间，或 --。

## 使用

- 点击透明状态条展开明细，再点一次或点击“关闭”收起；右键可暂时隐藏或退出。
- “显示设置”中勾选显示项、调整窄窗口保留优先级、选择自动／浅色／深色配色。
- 默认显示全部指标；空间不足时按优先级省略，无法容纳时隐藏，不覆盖原生按钮。
- 拖动、缩放和可识别的菜单展开时隐藏，停稳或菜单关闭后恢复。点击浮层不会夺走输入焦点。
- Claude 桌面完全退出后停止扫描并释放文件监视器，再打开后恢复。只保留低频检查，不新增开机启动项。
- 彻底退出小窗后，下次启动加载了插件的会话会重新拉起它。

## 指标口径

| 指标 | 含义 |
| --- | --- |
| 今日 Token | 当天输入 + 写缓存 + 读缓存 + 输出，按 message.id 去重，含子代理。大量缓存读取不代表计费用量。 |
| 缓存命中 | 读缓存 ÷（输入 + 写缓存 + 读缓存）。 |
| tok/s | 最近活跃主会话的输出 Token ÷ 该次响应耗时，包含首 Token 等待，是估算值。新请求、工具执行或样本超过 5 秒时归 0。 |
| 5h / 7d | 已用比例，不是剩余比例。过了重置时间而新读数未到时显示“已重置”，不假设恢复为 0%。非订阅账号可能无读数。 |
| 上下文 | 最近活跃主会话的读数；没有窗口百分比时以 ~Token 数表示最新请求的输入量，未知时显示 --。 |

详情中明确说明“最近活跃会话”并显示读数时间，不把后台日志当作当前页面的绑定。子代理计入今日统计，但不抢占主会话的速率。

## 隐私与运行数据

- 逐行读取用户 .claude/projects 下相关 JSONL。这些文件包含对话内容；采集器只提取用量字段、消息标识和时间戳，不保存或显示内容。
- UI Automation 读取按钮名称、角色和坐标，用于定位，不导出对话文字。
- 自有运行目录为 %LOCALAPPDATA%\ClaudeUsageMonitor。包含 bin、用量快照、ui-settings.json、status.json 和限长 overlay.log。升级保留现有设置和读数。
- 新插件在 measurements 下每个会话保留两个交替写入的快照文件；中断写入时保留上一份完整读数。仍兼容旧 limits.json 与 context 读数。
- 自动配色只采样未被其他窗口遮挡的 Claude 像素；不保存屏幕截图，不再定期抓取整个窗口。
- 没有网络请求、遥测或凭据读取。预览图仅使用合成数据。

## 开发与验证

~~~powershell
.\tests\run-tests.ps1
claude plugin validate .\plugin\usage-monitor
.\install.ps1 -InstallRoot .\work\install-fixture -SkipPlugin -SkipLaunch
~~~

测试需要 Node.js，正常安装和运行不需要。隔离安装不会注册插件或启动小窗。Windows CI 执行离线测试与编译；它不能代替真实桌面的视觉和手感验收。

| 文件 | 职责 |
| --- | --- |
| overlay/Core.cs | 增量采集、去重、双份快照恢复和显示设置 |
| overlay/NativeHost.cs | 原生窗口识别、UI Automation 定位和 DPI 几何 |
| overlay/OverlayUI.cs | 透明 WPF 状态条与详情面板 |
| overlay/Program.cs | 独立采集线程、定位线程及生命周期 |
| plugin/usage-monitor/hooks/register.js | 会话启动与测量钩子 |

## 已知限制

- 当前页面 ID 不等于日志会话 ID，尚无经过验证的当前页面绑定；不按标题猜测关联。
- 定位优先识别“模型”／“Model”，并用 Opus／Sonnet／Haiku 名称与输入框位置回退。其他语言和未来 Claude 改版仍需实测，定位不可靠时隐藏。
- 菜单避让依赖可访问性状态，未暴露状态的自定义弹层仍可能需要适配。
- UI Automation 可能使 Chromium 保持无障碍树，有额外开销；后台待命也不是零开销。
- 100%／125%／150%／200% 几何有回归覆盖，物理混合缩放双屏、分屏及长时间交互仍需实机验证。
- 大于 16 MiB 的单条日志会被跳过并标为统计不完整，以限制内存占用。初次补读完成前也会显示统计仍在补读。

## 卸载与回滚

~~~powershell
.\uninstall.ps1
~~~

加 -RemoveData 会删除本插件的读数、设置和日志，不删除 Claude 自身的数据。安装器替换前保留 bin/ClaudeUsageOverlay.exe.previous；部署时还应保存原插件源码与版本，用于配套回滚。

## 许可证

[MIT](LICENSE)
