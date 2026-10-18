using System.Text.Json;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class OpenAiOAuthProtocolTests
{
	[Fact]
	public void NewAuthorizationUsesRequiredScopeResourceAndPkceOnly()
	{
		Uri redirect = new("http://127.0.0.1:54321/auth/callback");
		Uri uri = OpenAiOAuthProtocol.CreateAuthorizationUri(
			OpenAiOAuthProtocol.DynamicClientId, "urn:uuid:00000000-0000-4000-8000-000000000001", redirect,
			"state-random", "nonce-random", "challenge-random", null);
		Dictionary<string, string> query = OAuthTestHelpers.ParseQuery(uri.Query);
		Assert.Equal(OpenAiOAuthProtocol.DynamicClientId, query["client_id"]);
		Assert.Equal(OpenAiOAuthProtocol.AgentName, query["agent_name_hint"]);
		Assert.Equal("urn:uuid:00000000-0000-4000-8000-000000000001", query["ext_agent_host_id"]);
		Assert.Equal("code", query["response_type"]);
		Assert.Equal(redirect.AbsoluteUri, query["redirect_uri"]);
		Assert.Equal(OpenAiOAuthProtocol.RequiredScopes, query["scope"]);
		Assert.Equal(OpenAiOAuthProtocol.Resource, query["resource"]);
		Assert.Equal("S256", query["code_challenge_method"]);
		Assert.Equal("state-random", query["state"]);
		Assert.Equal("nonce-random", query["nonce"]);
		Assert.DoesNotContain("prompt", query.Keys);
		Assert.DoesNotContain("force_reconsent", query.Keys);
	}

	[Fact]
	public void ReauthorizationUsesIdAndLoginHintsWithoutRegistrationName()
	{
		ChatGptCredential credential = new("opaque", "refresh", DateTimeOffset.UtcNow.AddHours(1), "subject",
			"oaiapp-issued", "subject", "retained-id-token", OpenAiOAuthProtocol.RequiredScopes, "user@example.com", DateTimeOffset.UtcNow.AddMinutes(-1));
		ChatGptRegistration registration = new("oaiapp-issued", "subject", "user@example.com", credential, credential.Scopes);
		Uri uri = OpenAiOAuthProtocol.CreateAuthorizationUri("oaiapp-issued", "urn:uuid:host", new Uri("http://127.0.0.1:1234/auth/callback"), "s", "n", "c", registration);
		Dictionary<string, string> query = OAuthTestHelpers.ParseQuery(uri.Query);
		Assert.Equal("retained-id-token", query["id_token_hint"]);
		Assert.Equal("user@example.com", query["login_hint"]);
		Assert.DoesNotContain("agent_name_hint", query.Keys);
		Assert.DoesNotContain("prompt", query.Keys);
	}

	[Fact]
	public void CallbackRejectsDuplicatedParametersAndMalformedEncoding()
	{
		Assert.Throws<InvalidOperationException>(() => OpenAiOAuthProtocol.ParseCallback("/auth/callback?code=a&state=b&state=c"));
		Assert.Throws<InvalidOperationException>(() => OpenAiOAuthProtocol.ParseCallback("/auth/callback?code=%Q0&state=b"));
		Assert.Throws<InvalidOperationException>(() => OpenAiOAuthProtocol.ParseCallback("/callback?code=a&state=b"));
	}

	[Fact]
	public void UsagePermissionRequiresBothDirectAndResourceScopes()
	{
		Assert.True(OpenAiOAuthProtocol.HasUsageScopes("openid resource.invoke chatgpt.tokens.use.direct email"));
		Assert.False(OpenAiOAuthProtocol.HasUsageScopes("openid resource.invoke"));
		Assert.False(OpenAiOAuthProtocol.HasUsageScopes("openid chatgpt.tokens.use.direct"));
	}

	[Theory]
	[InlineData("unix")]
	[InlineData("iso")]
	public void EarliestRefreshAtAcceptsUnixSecondsAndUtcIsoTimestamp(string format)
	{
		DateTimeOffset expected = DateTimeOffset.UtcNow.AddMinutes(5);
		string value = format == "unix" ? expected.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) : $"\"{expected:O}\"";
		using JsonDocument document = JsonDocument.Parse($"{{\"earliest_refresh_at\":{value}}}");
		DateTimeOffset parsed = OpenAiOAuthProtocol.RequiredEarliestRefreshAt(document.RootElement);
		Assert.InRange(Math.Abs((parsed - expected).TotalSeconds), 0, 1);
	}
}
