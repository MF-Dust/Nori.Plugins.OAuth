using System.Net;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class ModelCatalogTests
{
	[Fact]
	public async Task CatalogUsesBearerAndReturnsOnlyListVisibleSlugsInServerOrder()
	{
		ChatGptCredential credential = Credential();
		int requests = 0;
		using HttpClient http = new(new TestHttpHandler(request =>
		{
			requests++;
			Assert.Equal(HttpMethod.Get, request.Method);
			Assert.Equal("https://api.openai.com/v1/models", request.RequestUri!.AbsoluteUri);
			Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
			Assert.Equal(credential.AccessToken, request.Headers.Authorization.Parameter);
			return Task.FromResult(OAuthTestHelpers.Json("""
				{"models":[
					{"slug":"model-z","visibility":"list","display_name":"Z"},
					{"slug":"model-hidden","visibility":"hidden","display_name":"Hidden"},
					{"slug":"model-a","visibility":"list","display_name":"A"},
					{"slug":"","visibility":"list"}
				]}
				"""));
		}));
		ChatGptAuth auth = await AuthWith(credential, http);

		IReadOnlyList<string> models = await ChatGptModelCatalog.GetModelsAsync(http, auth, default);
		Assert.Equal("https://api.openai.com/v1", ChatGptModelCatalog.DefaultBaseUrl);
		Assert.Equal(new[] { "model-z", "model-a" }, models);
		Assert.Equal(1, requests);
	}

	[Fact]
	public async Task UnauthenticatedCatalogFailsInsteadOfReturningFallbackModels()
	{
		int requests = 0;
		using HttpClient http = new(new TestHttpHandler(_ => { requests++; return Task.FromResult(OAuthTestHelpers.Json("{}")); }));
		ChatGptAuth auth = new(http);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ChatGptModelCatalog.GetModelsAsync(http, auth, default).AsTask());
		Assert.Contains("登录", error.Message, StringComparison.Ordinal);
		Assert.Equal(0, requests);
	}

	[Fact]
	public async Task CatalogPreservesApiFailureStatusAndRequestId()
	{
		using HttpClient http = new(new TestHttpHandler(_ =>
		{
			HttpResponseMessage response = OAuthTestHelpers.Json("{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"private response\"}}", HttpStatusCode.TooManyRequests);
			response.Headers.Add("x-request-id", "req_123");
			return Task.FromResult(response);
		}));
		ChatGptAuth auth = await AuthWith(Credential(), http);

		HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => ChatGptModelCatalog.GetModelsAsync(http, auth, default).AsTask());
		Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
		Assert.Equal("req_123", error.Data["openai.request_id"]);
		Assert.Equal("subscription_sharing_usage_limit_exceeded", error.Data["openai.error_code"]);
		Assert.DoesNotContain("private response", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task MissingModelsArrayIsNotTreatedAsAnEmptySuccessfulCatalog()
	{
		using HttpClient http = new(new TestHttpHandler(_ => Task.FromResult(OAuthTestHelpers.Json("{}"))));
		ChatGptAuth auth = await AuthWith(Credential(), http);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ChatGptModelCatalog.GetModelsAsync(http, auth, default).AsTask());
		Assert.Contains("models", error.Message, StringComparison.Ordinal);
	}

	private static async Task<ChatGptAuth> AuthWith(ChatGptCredential credential, HttpClient http)
	{
		ChatGptRegistration registration = new(credential.ClientId, credential.Subject, credential.Email, credential, credential.Scopes);
		ChatGptAuth auth = new(http, new MemoryCredentialStore(new ChatGptAuthState(credential.ClientId, credential.Subject, [registration])));
		await auth.RestoreAsync(default);
		return auth;
	}

	private static ChatGptCredential Credential() => new(
		"opaque-access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), "subject",
		"oaiapp-models", "subject", "verified-id-token", "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", "user@example.com", DateTimeOffset.UtcNow.AddMinutes(-1));
}
