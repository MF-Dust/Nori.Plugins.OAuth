using Microsoft.Extensions.AI;
using Nori.PluginRuntime;

namespace Nori.Plugins.ChatGpt;

/// <summary>独立的 Nori Desktop ChatGPT OAuth 提供商，不引用宿主业务程序集。</summary>
public sealed class Plugin : INoriPlugin
{
	private HttpClient? _http;
	private ChatGptAuth? _auth;
	private CancellationTokenSource? _lifetime;
	private readonly List<IPluginRegistration> _registrations = [];

	public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.StoppingToken);
		_http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
		string key = "oauth_credentials";
		CredentialStore store = new(
			ct => new(Task.Run(async () => await context.Storage.GetAsync(key, ct), ct)),
			(value, ct) => new(Task.Run(async () => await context.Storage.SetAsync(key, value, ct), ct)),
			ct => new(Task.Run(async () => await context.Storage.DeleteAsync(key, ct), ct)),
			new PlatformCredentialProtection());
		_auth = new ChatGptAuth(_http, store);
		await _auth.RestoreAsync(cancellationToken);
		_registrations.Add(context.Contributions.Register(new Provider(_http, _auth)));
		_registrations.Add(context.Contributions.Register(new LoginPageContribution(_auth, _lifetime.Token)));
	}

	public async ValueTask DeactivateAsync(CancellationToken cancellationToken)
	{
		_lifetime?.Cancel();
		foreach (IPluginRegistration registration in _registrations) registration.Dispose();
		_registrations.Clear();
		if (_auth is not null) await _auth.ClearSessionAsync();
		_auth = null;
		_http?.Dispose();
		_http = null;
		_lifetime?.Dispose();
		_lifetime = null;
	}

	private sealed class Provider(HttpClient http, ChatGptAuth auth) : IPluginChatProviderContribution
	{
		public string Id => "chatgpt";
		public string Name => "ChatGPT (OAuth / Codex)";
		public string DefaultBaseUrl => "https://chatgpt.com/backend-api/codex";
		public bool RequiresApiKey => false;

		public IChatClient CreateClient(PluginChatClientOptions options) => new ChatGptChatClient(http, auth.GetCredentialAsync, options.Model);

		public ValueTask<IReadOnlyList<string>> GetModelsAsync(PluginChatClientOptions options, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			// Codex 没有稳定的公开模型发现 API；用户也可在宿主输入其账号支持的模型 ID。
			return ValueTask.FromResult<IReadOnlyList<string>>(["gpt-5.4", "gpt-5.3-codex", "gpt-5.2-codex", "gpt-5.2"]);
		}
	}
}
