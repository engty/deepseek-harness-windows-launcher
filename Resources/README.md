# Runtime Bundle（发布构建输入）

完整 Windows 发布包由受控构建流程生成，运行时目录结构如下：

```text
Resources/runtime/
  node/bin/node.exe
  node_modules/.bin/pnpm.cmd
  node_modules/@deepseek-ai/dsh/
  default-profile/profiles/web/
Resources/webview2/msedgewebview2.exe
```

`default-profile` 应包含与 macOS 当前版本一致的 1024 Store、better-dsh-pet、dsh-mnemon、GenUI、隐私路由和技能包。启动器首次启动时只在没有用户 profile 的情况下复制它；已有 profile 永不被模板覆盖。

Runtime 只写入发布包和 `%LOCALAPPDATA%\DeepSeekHarness` 的用户目录，不修改全局 Node.js、npm、pnpm、PATH、注册表、服务或计划任务。

Windows 外壳从官方 npm Registry 查询 `@deepseek-ai/dsh` 版本，使用 Runtime 自带 Node/pnpm 在 App-owned staging 目录重建候选包，完成启动预检后再切换。用户插件、lockfile 和会话数据不随 Runtime 更新被静默替换。

完整发布还会把 x64 Fixed Version WebView2 一起放入 `Resources/webview2`；开发包缺少该目录时才回退到系统 Evergreen Runtime。
