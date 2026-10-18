using System.Net.Http.Headers;
using System.Text.Json;

namespace Nori.Plugins.ChatGpt;

internal static class ChatGptModelCatalog
{
	internal const string DefaultBaseUrl = "https://api.openai.com/v1";
	private static readonly Uri ModelsEndpoint = new(DefaultBaseUrl + "/models");

	internal static async ValueTask<IReadOnlyList<string>> GetModelsAsync(HttpClient http, ChatGptAuth auth, CancellationToken cancellationToken)
	{
		ChatGptCredential credential = await auth.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));
		using HttpRequestMessage request = new(HttpMethod.Get, ModelsEndpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
		using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
			throw await ChatGptApiException.FromResponseAsync(response, timeout.Token).ConfigureAwait(false);

		string body = await OAuthHttpResponse.ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
		try
		{
			using JsonDocument document = JsonDocument.Parse(body);
			if (!document.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
				throw new InvalidOperationException("ChatGPT 模型目录响应缺少 models 列表。");
			List<string> result = [];
			foreach (JsonElement model in models.EnumerateArray())
			{
				if (model.ValueKind == JsonValueKind.Object &&
					model.TryGetProperty("visibility", out JsonElement visibility) && visibility.ValueKind == JsonValueKind.String && visibility.GetString() == "list" &&
					model.TryGetProperty("slug", out JsonElement slug) && slug.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(slug.GetString()))
					result.Add(slug.GetString()!);
			}
			return result;
		}
		catch (JsonException) { throw new InvalidOperationException("ChatGPT 模型目录响应不是有效 JSON。"); }
	}
}
