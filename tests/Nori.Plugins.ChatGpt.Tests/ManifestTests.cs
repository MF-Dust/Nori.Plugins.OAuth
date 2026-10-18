using System.Text.Json;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class ManifestTests
{
	internal static string ReadManifest()
	{
		using Stream stream = typeof(ManifestTests).Assembly.GetManifestResourceStream("PluginManifest.json")!;
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}

	[Fact]
	public void ManifestIncludesRequiredHostVersionAndMatchesPluginAssembly()
	{
		using JsonDocument document = JsonDocument.Parse(ReadManifest());
		JsonElement root = document.RootElement;
		Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal("io.nori.chatgpt", root.GetProperty("id").GetString());
		Assert.Equal("2.4", root.GetProperty("apiVersion").GetString());
		Assert.Equal("2.0.2", root.GetProperty("minHostVersion").GetString());
		Assert.Equal(typeof(ChatGptAuth).Assembly.GetName().Version!.ToString(3), root.GetProperty("version").GetString());
		Assert.Equal("lib/Nori.Plugins.ChatGpt.dll", root.GetProperty("runtime").GetProperty("assembly").GetString());
		Assert.Equal("Nori.Plugins.ChatGpt.Plugin", root.GetProperty("runtime").GetProperty("entryType").GetString());
	}
}
