using Nori.PluginRuntime;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class PluginTests
{
	[Fact]
	public async Task PluginRegistersProviderAndSettingsPageAndPreservesStoredCredentialOnDeactivate()
	{
		Context context = new();
		Plugin plugin = new();
		await plugin.ActivateAsync(context, default);
		IPluginChatProviderContribution provider = Assert.Single(context.Registry.Items.OfType<IPluginChatProviderContribution>());
		Assert.False(provider.RequiresApiKey);
		Assert.Equal("chatgpt", provider.Id);
		Assert.Equal(PluginPagePlacement.Settings, Assert.Single(context.Registry.Items.OfType<IPluginPageContribution>()).Placement);
		Assert.NotEmpty(await provider.GetModelsAsync(new("https://untrusted.invalid", "ignored", ""), default));
		using (var client = provider.CreateClient(new("https://untrusted.invalid", "ignored", "gpt-5.4")))
		{
			InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new(Microsoft.Extensions.AI.ChatRole.User, "hello")]));
			Assert.Contains("登录", error.Message);
		}
		await plugin.DeactivateAsync(default);
		Assert.Empty(context.Registry.Items);
		Assert.NotNull(context.StorageFake.Value);
		Assert.DoesNotContain("access_token", context.StorageFake.Value!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
		await plugin.DeactivateAsync(default);
	}

	private sealed class Context : IPluginContext
	{
		internal Registry Registry { get; } = new();
		internal Storage StorageFake { get; } = new() { Value = System.Text.Json.Nodes.JsonValue.Create(Convert.ToBase64String("encrypted-existing-credential"u8.ToArray())) };
		public IContributionRegistry Contributions => Registry;
		public CancellationToken StoppingToken => default;
		public PluginDescriptor Plugin => throw new NotSupportedException();
		public IPluginLogger Logger => throw new NotSupportedException();
		public IPluginStorage Storage => StorageFake;
		public IPluginAssets Assets => throw new NotSupportedException();
		public IPluginCapabilities Capabilities => throw new NotSupportedException();
	}

	private sealed class Storage : IPluginStorage
	{
		internal System.Text.Json.Nodes.JsonNode? Value;
		public ValueTask<System.Text.Json.Nodes.JsonNode?> GetAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult(Value?.DeepClone());
		public ValueTask SetAsync(string key, System.Text.Json.Nodes.JsonNode? value, CancellationToken cancellationToken = default) { Value = value?.DeepClone(); return ValueTask.CompletedTask; }
		public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { Value = null; return ValueTask.CompletedTask; }
	}

	private sealed class Registry : IContributionRegistry
	{
		internal List<IPluginContribution> Items { get; } = [];
		public IPluginRegistration Register<T>(T contribution) where T : class, IPluginContribution
		{
			Items.Add(contribution);
			return new Registration(() => Items.Remove(contribution));
		}
	}

	private sealed class Registration(Action remove) : IPluginRegistration
	{
		public void Dispose() => remove();
	}
}
