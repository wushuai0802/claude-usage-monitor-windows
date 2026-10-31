# Claude Usage Monitor Windows

> **非官方项目**，与 Anthropic 没有关联，也未获其认可或支持。“Claude”是 Anthropic 的商标，这里仅用于说明适用对象。当前是**预览版**。

在 Windows 版 Claude 桌面应用 Code 页的输入框下方（模型按钮左侧）显示：

```text
117.4 tok/s | 4535万 | 95.8% | 5h 24% · 7d 22% | 上下文 21%
```

依次是 `tok/s`、今日 Token、缓存命中、5 小时和 7 天窗口已用比例、上下文占用。

它不注入 Claude、不开调试端口、不修改安装包。

## 环境

- Windows 10/11（64 位）和 Claude 桌面应用，已打开过一次 Code 页。
- 不需要额外的 SDK：安装时用 Windows 自带的 .NET Framework 编译器从源码构建。

## 安装

下载或克隆仓库，放到一个长期不动的目录（插件从这里读取），然后运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

重启已打开的 Claude 会话。5h、7d 和上下文要等会话收到下一次响应后才有数，在此之前显示 `--`。

## 使用

- 鼠标悬停看明细，右键退出。
- 空间不足时依次省略缓存、Token、`tok/s` 和上下文；连 5h 和 7d 都放不下时隐藏。
- 小窗退出后，下次开始新会话时由插件重新拉起。

## 卸载

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\uninstall.ps1
```

加 `-RemoveData` 会同时删除读数和日志。不会动 Claude 自己的数据。

## 组成

| 部分 | 位置 | 作用 |
| --- | --- | --- |
| 小窗 | `overlay\ClaudeUsageOverlay.cs`，安装到 `%LOCALAPPDATA%\ClaudeUsageMonitor\bin` | 属于 Claude 窗口的小窗，通过 UI Automation 跟随模型按钮 |
| 插件 | `plugin\usage-monitor` | 拉起小窗，写出额度和上下文读数 |

## 指标口径

- **今日 Token**：当天响应的 输入 + 写缓存 + 读缓存 + 输出，按 `message.id` 去重，含子代理。大部分是缓存读取，**不代表计费用量**。
- **缓存命中**：今日 读缓存 ÷（输入 + 写缓存 + 读缓存）。
- **tok/s**：最近活跃会话的 输出 Token ÷ 该次响应耗时（含首 Token 等待），是**估算值**，不是逐 Token 测速；回合结束 5 秒后归 0。
- **5h / 7d**：已用比例，不是剩余比例。要有会话收到 API 响应后才更新；窗口已过重置时间显示 `--`。非订阅账号可能一直没有读数。
- **上下文**：最近活跃会话的上下文占用。

## 隐私

- **读取：** 小窗会逐行读取 `%USERPROFILE%\.claude\projects` 下当天的对话记录（JSONL）。这些文件**包含对话内容**；小窗只提取用量字段和时间戳，用于计算后不保存也不显示内容。它还会通过 UI Automation 读取 Claude 窗口里按钮的名称和位置来定位。
- **保存：** `%LOCALAPPDATA%\ClaudeUsageMonitor` 下只有额度读数（`limits.json`）、按会话 ID 命名的上下文读数（`context\`，超过 7 天清理）和日志（`overlay.log`，含异常信息和显示状态变化）。
- **网络：** 没有任何网络请求，也没有遥测。不读取账号凭据。

## 开发

```powershell
.\tests\run-tests.ps1
```

## 已知限制

- 依赖模型按钮的名称以 `模型：` 或 `Model:` 开头，**其他界面语言目前找不到按钮**；Claude 改版后也可能失效，不承诺长期兼容。找不到时小窗隐藏，不影响输入。
- 小窗是 Claude 窗口之上的独立窗口，弹出菜单展开到同一位置时可能被遮住。
- UI Automation 可能让 Chromium 保持无障碍树开启，有少量性能开销。
- 多会话时 `tok/s` 和上下文取最近有活动的会话，不一定是当前看的那个。
- 目前只在一台机器的中文界面上验证过，其他缩放比例、多显示器和分屏布局未测试。

## 许可证

[MIT](LICENSE)
