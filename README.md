# ChatGPT OAuth · Nori Desktop 插件

独立的 `.noripack` 插件，需要 Nori Desktop 2.0.2 或更高版本、Plugin API 2.4 / .NET 10。插件使用 OpenAI 官方 Sign in with ChatGPT OAuth：系统浏览器授权、PKCE S256、state/nonce、127.0.0.1 临时回调和已签名 ID token 验证；推理及模型发现走 `https://api.openai.com/v1`。生产插件不引用宿主业务程序集，未修改 Plugin SDK。

## 构建和测试

在仓库根目录运行：

```bash
dotnet build src/Nori.Plugins.ChatGpt/Nori.Plugins.ChatGpt.csproj -c Release
dotnet test tests/Nori.Plugins.ChatGpt.Tests/Nori.Plugins.ChatGpt.Tests.csproj -c Release -m:1
# 可选：包含 Avalonia 登录页与插件生命周期测试（需要 Nori Desktop 源码）
dotnet test tests/Nori.Plugins.ChatGpt.Tests/Nori.Plugins.ChatGpt.Tests.csproj -c Release -m:1 -p:NoriDesktopRoot=C:/path/to/Nori-Desktop-Pet
dotnet msbuild src/Nori.Plugins.ChatGpt/Nori.Plugins.ChatGpt.csproj -t:PackPlugin -p:Configuration=Release
```

生成包位于 `src/Nori.Plugins.ChatGpt/bin/Release/net10.0/io.nori.chatgpt-1.1.0.noripack`。在 Nori 设置 → 插件 → 本地安装启用，再从 ChatGPT OAuth 设置页选择已保存账号或新增账号，并点击 **Continue with ChatGPT**。登录后模型服务选择 ChatGPT (OAuth)，API Key 留空；模型列表来自当前账号的 `/v1/models`，不会回退到硬编码目录。

测试使用模拟 OAuth 响应和签名密钥，覆盖 loopback 回调、state/nonce/JWKS 验证、账号隔离、过期恢复、refresh token 轮换、取消、退出、旧凭证恢复和模型目录。**尚未使用真实 ChatGPT 账号完成端到端登录或真实额度请求验证。**

## 官方协议来源

- [Sign in with ChatGPT：Registration and sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
- [Accounts and sessions](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions)
- [Models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
- [Token reference](https://developers.openai.com/siwc/token-sharing-open-source/token-reference)
- [Errors and recovery](https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery)
- [Preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations)
- [UI/UX guidelines](https://developers.openai.com/siwc/ui-ux-guidelines)

## 行为与边界

- 首次注册使用 `dynamic_agent_client` 和稳定的 `urn:uuid` 主机标识；回调签发的 client ID 才用于令牌交换与后续登录。已保存账号按 `(client_id, verified sub)` 分开保留。
- OAuth 凭证以现有 Windows DPAPI、macOS Keychain 或 Linux Secret Service 保护，并作为密文保存在插件 `oauth_credentials` KV 项中。主机 ID 单独保存在非秘密 `oauth_host_id` KV 项中，退出登录不删除。Linux 需要安装 `secret-tool` 并解锁密钥环；不提供明文回退。
- 令牌刷新尊重 `earliest_refresh_at`，并在轮换后持久化新 refresh token。退出登录尝试远端撤销，再清除当前账号的本地令牌；若撤销未确认，会明确提示。停用插件只清空内存、不删除存储。
- 旧版 Codex 设备码凭证不会用于新 API，也不会自动删除；设置页会提示用户重新授权，只有明确操作“清除无法恢复的凭证”才会清理损坏或不兼容的旧凭证。
- ChatGPT plan 授权必须包含 `chatgpt.tokens.use.direct` 与 `resource.invoke` 才允许推理。模型发现按官方规则保留 `visibility=list` 的 `slug` 和服务端排序；授权或请求失败时不会静默切换到 API key 或其他计费路径。
- 请求固定发往 `auth.openai.com` 与 `api.openai.com`，不把 OAuth 凭证发送到宿主配置的自定义地址。插件是受信任的进程内扩展，不是沙箱。
