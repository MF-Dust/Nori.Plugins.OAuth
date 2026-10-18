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
		const string credentialKey = "oauth_credentials";
		const string hostIdKey = "oauth_host_id";
		CredentialStore store = new(
			ct => new(Task.Run(async () => await context.Storage.GetAsync(credentialKey, ct), ct)),
			(value, ct) => new(Task.Run(async () => await context.Storage.SetAsync(credentialKey, value, ct), ct)),
			ct => new(Task.Run(async () => await context.Storage.DeleteAsync(credentialKey, ct), ct)),
			new PlatformCredentialProtection());
		HostIdStore hostIdStore = new(
			ct => Task.Run(async () => (await context.Storage.GetAsync(hostIdKey, ct))?.GetValue<string>(), ct),
			(value, ct) => Task.Run(async () => await context.Storage.SetAsync(hostIdKey, System.Text.Json.Nodes.JsonValue.Create(value), ct), ct));
		_auth = new ChatGptAuth(_http, store, hostIdStore);
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

	internal sealed class Provider(HttpClient http, ChatGptAuth auth) : IPluginChatProviderContribution
	{
		public string Id => "chatgpt";
		public string Name => "ChatGPT (OAuth)";
		public string DefaultBaseUrl => ChatGptModelCatalog.DefaultBaseUrl;
		public bool RequiresApiKey => false;

		public IChatClient CreateClient(PluginChatClientOptions options) => new ChatGptChatClient(http, auth.GetCredentialAsync, options.Model);

		public ValueTask<IReadOnlyList<string>> GetModelsAsync(PluginChatClientOptions options, CancellationToken cancellationToken) =>
			ChatGptModelCatalog.GetModelsAsync(http, auth, cancellationToken);
	}
}
