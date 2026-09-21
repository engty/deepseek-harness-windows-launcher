# Runtime Bundle（发布构建输入）

完整 Windows 发布包由受控构建流程生成，运行时目录结构如下：

```text
Resources/runtime/
  node/bin/node.exe
  node_modules/.bin/pnpm.cmd
  node_modules/@deepseek-ai/dsh/
```

Runtime 至少包含固定版本的 Node.js、pnpm、`@deepseek-ai/dsh` 及其完整生产依赖。旧 Windows 项目已经验证的 `@deepseek-ai/dsh@0.1.0-rc.6` Runtime 不包含 `default-profile`，官方 Harness 会从自身包和用户 profile 初始化；如果构建输入提供 `default-profile/profiles/web`，启动器会在首次启动时复制它，已有 profile 永不被模板覆盖。

Runtime 只写入发布包和 `%LOCALAPPDATA%\DeepSeekHarness` 的用户目录，不修改全局 Node.js、npm、pnpm、PATH、注册表、服务或计划任务。

Windows 外壳从官方 npm Registry 查询 `@deepseek-ai/dsh` 版本，使用 Runtime 自带 Node/pnpm 在 App-owned staging 目录重建候选包，完成启动预检后再切换。用户插件、lockfile 和会话数据不随 Runtime 更新被静默替换。

如果构建输入提供 `Resources/webview2/msedgewebview2.exe`，full 包会同时携带 x64 Fixed Version WebView2，从而完全隔离 WebView2；没有该目录时保持旧项目的系统 Evergreen Runtime 兼容行为。
