# ChatGPT OAuth · Nori Desktop 插件

独立的 `.noripack` 插件，适用于 Nori Plugin API 2.4 / .NET 10。凭证由 Windows 当前用户 DPAPI、macOS Keychain 或 Linux Secret Service 保护，并以密文存储在插件 `oauth_credentials` KV 项中。Linux 需要安装 `secret-tool` 并解锁密钥环；不提供明文回退。插件数据由宿主存放于 `<PackageRoot>/data/plugins`。

## 构建和测试

在仓库根目录运行：

```bash
dotnet build src/Nori.Plugins.ChatGpt/Nori.Plugins.ChatGpt.csproj -c Release
dotnet test tests/Nori.Plugins.ChatGpt.Tests/Nori.Plugins.ChatGpt.Tests.csproj -c Release -m:1
# 可选：包含 Avalonia 登录页与插件生命周期测试（需要 Nori Desktop 源码）
dotnet test tests/Nori.Plugins.ChatGpt.Tests/Nori.Plugins.ChatGpt.Tests.csproj -c Release -m:1 -p:NoriDesktopRoot=C:/path/to/Nori-Desktop-Pet
dotnet msbuild src/Nori.Plugins.ChatGpt/Nori.Plugins.ChatGpt.csproj -t:PackPlugin -p:Configuration=Release
```

生成包位于 `src/Nori.Plugins.ChatGpt/bin/Release/net10.0/io.nori.chatgpt-1.0.2.noripack`。在 Nori 设置 → 插件 → 本地安装启用，再从 ChatGPT OAuth 设置页登录；模型服务选择 ChatGPT (OAuth / Codex)，API Key 留空。

设备码登录需要用户在系统浏览器中授权。真实设备授权与账号配额需安装后手动验证；测试使用模拟响应，并覆盖刷新令牌轮换与保存重试、退出删除重试、停用后凭证保留、恢复失败及敏感信息不泄漏。

登录页复用宿主 Nori 主题的字体、青色主按钮、危险操作样式、输入框与细线分组；生产插件不引用宿主程序集。可选 UI 测试直接加载主程序的实际主题，并检查 720×480 与 1080p 布局。

## 行为与边界

- 使用 ChatGPT Codex OAuth 通道，不是 OpenAI Platform API 或浏览器会话；模型推荐列表不保证账号均可用。
- 凭证只保存在插件隔离 KV 的加密字段；重启及插件重新加载后恢复，刷新令牌轮换后持久化。退出登录删除该插件凭证及其对应密钥；停用插件仅清空内存。
- 请求固定发往 `auth.openai.com` 与 `chatgpt.com`，不把 OAuth 凭证发送到宿主配置的自定义地址。插件是受信任的进程内扩展，不是沙箱。
- 不读取其他应用的凭证、不使用浏览器自动化/Cookie/API Key回退。远端协议变化可能导致授权失效。
