# DeepSeek Harness for Windows

> 把 DeepSeek Harness 变成 Windows 上双击就能用的桌面 App。**非官方项目**，与 DeepSeek 官方无关。
>
> 本项目是 [deepseek-harness-macos-launcher](https://github.com/engty/deepseek-harness-macos-launcher) 的 Windows 移植版。

![DeepSeek Harness 界面](Resources/DeepSeekHarness-screenshot.png)

## 项目初衷

[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 本身是一个通过命令行或浏览器使用的工具：要自己装 Node.js、装依赖、敲命令、管版本。对普通用户来说门槛太高了。

这个项目做的事情很简单：**把官方 Harness 原封不动地装进一个 Windows App 里**。双击图标就能打开，不用碰终端，不用装任何东西，聊天、模型、插件全是官方的原样功能。

## 怎么用

**要求**：Windows 10 (1809+) 或 Windows 11，x64。无需管理员权限。

1. **下载**：到本仓库的 Releases 页面，下载 `DeepSeek-Harness-v<版本号>-windows-x64.zip`（或单独的 `DeepSeekHarness.exe`）。
2. **解压即用**：这是一个**便携软件（portable）**——没有安装程序、不写注册表、不需要管理员权限。解压到任意你有写权限的目录（比如 `D:\Tools\` 或桌面），双击 `DeepSeekHarness.exe` 即可。AD 域内网受限账号也能正常用。
3. **首次打开**：App 未做代码签名，SmartScreen 可能提示「Windows 已保护你的电脑」——点「更多信息 → 仍要运行」即可。
4. **配置 API Key**：打开 App 后，点击顶栏的「余额」按钮，粘贴你的 DeepSeek API Key。配好后顶栏会显示余额，聊天和余额共用同一个 Key。
5. **装插件**：菜单栏 `插件 → 安装插件…`，粘贴官方安装命令即可，例如：
   ```
   dsh plugin --profile web add dsh-llm-codex
   ```
   装好的插件可以在同一菜单里启动、停用或卸载。
6. **更新**：App 会静默检查 Harness 更新，有新版时顶栏出现下载按钮，一键升级、失败自动回退；`DeepSeek → 检查 DeepSeek Harness App 更新…` 检查外壳自身更新。**整个更新过程发生在用户目录内，同样不需要管理员权限。**

## 界面随系统主题

App 外壳（窗口、菜单、对话框）会**自动跟随 Windows 的亮色/暗色模式**，在「设置 → 个性化 → 颜色」里切换系统主题时即时生效；窗口内的 Harness 网页界面也会同步收到对应的 `prefers-color-scheme`。

## 安全性

- **API Key 只留在你的电脑上**：一份用 Windows DPAPI 加密保存（只有当前 Windows 用户能解密，不需要管理员、不弹授权框），另一份存 Harness 的私有凭据文件（所在目录已 ACL 限制为仅当前用户可读）。不上传到任何服务器，没有遥测、没有广告、没有账号系统。
- **界面只连本机**：窗口里加载的是运行在你电脑上的 Harness 界面（127.0.0.1），只有你主动点击的外部链接才会交给系统浏览器打开。
- **不碰你的系统环境**：App 在私有目录（`%LOCALAPPDATA%\DeepSeekHarness`）里运行 Harness 和插件，不会改你的 Node.js、npm、pnpm、PATH 或系统配置。
- **更新经过校验**：Harness 升级包走 HTTPS 下载、SHA-256 校验、启动预检，失败自动回退到旧版本。
- **插件是第三方代码**：安装插件前请确认来源可信，插件行为由插件作者负责。

## 实现方式

一句话：这是一个「薄外壳」，与 macOS 版一一对应。

| macOS 版 | Windows 版 |
| --- | --- |
| SwiftUI + WKWebView | WPF (.NET 8) + WebView2 |
| macOS Keychain | DPAPI `ProtectedData`（当前用户作用域） |
| `~/Library/Application Support` | `%LOCALAPPDATA%\DeepSeekHarness` |
| `/usr/bin/tar` | `C:\Windows\System32\tar.exe`（Win10+ 内置 bsdtar） |
| `.bin/dsh` shell 脚本 | `.bin/dsh.cmd`（经 cmd.exe）/ 直接 node + JS |
| POSIX 0700 权限 | 目录 ACL（仅当前用户 + SYSTEM） |

App 内置了一份固定版本的 Node.js 和官方 DeepSeek Harness，在你电脑本地启动它，然后用 WebView2 把官方 Web 界面显示在 App 窗口里。外壳只负责四件事：

- 启动、停止、崩溃后自动拉起 Harness；
- 提供 Windows 菜单来配置 Key、管理插件；
- 安全保存凭据；
- 检查并安装 Harness 更新。

聊天、会话、模型选择、插件功能全部由官方 Harness 原样提供，本项目不重写、不阉割、也不夹带自己的逻辑。

## 从源码构建

```powershell
# 需要 .NET 8 SDK（构建用；运行不需要）
git clone <本仓库地址>
cd deepseek-harness-windows-launcher

# 单元测试
dotnet test tests/HarnessLauncher.Tests/HarnessLauncher.Tests.csproj

# 打包便携版（单文件 exe + zip，产物在 artifacts\）
.\script\package_portable.ps1 -Version 0.1.0

# （可选）先把官方 Runtime 打进 Resources\runtime，
# package_portable.ps1 会额外生成含 Runtime 的 -full.zip
$env:HARNESS_RUNTIME_SOURCE = "C:\path\to\runtime-source"
.\script\package_runtime.ps1
```

WebView2 运行时：Windows 10/11 绝大多数系统已内置；个别精简系统如果没有，App 会提示安装，WebView2 支持**当前用户免管理员**安装。

## 许可证

MIT，与 macOS 版一致。DeepSeek Harness 本身遵循其上游许可证。
