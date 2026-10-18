using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Nori.Plugins.ChatGpt;

internal static class OpenAiOAuthProtocol
{
	internal const string Issuer = "https://auth.openai.com";
	internal const string AuthorizationEndpoint = Issuer + "/api/accounts/authorize";
	internal const string TokenEndpoint = Issuer + "/api/accounts/oauth/token";
	internal const string RevocationEndpoint = Issuer + "/api/accounts/oauth/revoke";
	internal const string JwksEndpoint = Issuer + "/.well-known/jwks.json";
	internal const string Resource = "https://api.openai.com/v1";
	internal const string DynamicClientId = "dynamic_agent_client";
	internal const string AgentName = "Nori Desktop";
	internal const string CallbackPath = "/auth/callback";
	internal const string RequiredScopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
	internal const string DirectScope = "chatgpt.tokens.use.direct";
	internal const string InvokeScope = "resource.invoke";

	internal static string RandomBase64Url(int byteCount) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(byteCount));

	internal static string CreateChallenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

	internal static Uri CreateAuthorizationUri(
		string clientId,
		string hostId,
		Uri redirectUri,
		string state,
		string nonce,
		string challenge,
		ChatGptRegistration? registration)
	{
		List<KeyValuePair<string, string>> fields =
		[
			new("client_id", clientId),
			new("ext_agent_host_id", hostId),
			new("response_type", "code"),
			new("redirect_uri", redirectUri.AbsoluteUri),
			new("scope", RequiredScopes),
			new("resource", Resource),
			new("state", state),
			new("nonce", nonce),
			new("code_challenge_method", "S256"),
			new("code_challenge", challenge),
		];
		if (clientId == DynamicClientId)
			fields.Add(new("agent_name_hint", AgentName));
		if (registration is not null && !HasUsageScopes(registration.Scopes))
			fields.Add(new("prompt", "consent"));
		if (registration?.Credential?.IdToken is { Length: > 0 } idToken)
			fields.Add(new("id_token_hint", idToken));
		if (registration?.Email is { Length: > 0 } email)
			fields.Add(new("login_hint", email));

		string query = string.Join("&", fields.Select(field => $"{Uri.EscapeDataString(field.Key)}={Uri.EscapeDataString(field.Value)}"));
		return new Uri($"{AuthorizationEndpoint}?{query}", UriKind.Absolute);
	}

	internal static OAuthCallback ParseCallback(string requestTarget)
	{
		if (requestTarget.Length > 16 * 1024 || !requestTarget.StartsWith(CallbackPath + "?", StringComparison.Ordinal))
			throw new InvalidOperationException("授权回调地址无效，请返回设置重新登录。");

		string query = requestTarget[(CallbackPath.Length + 1)..];
		Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
		foreach (string pair in query.Split('&'))
		{
			int equals = pair.IndexOf('=');
			string key = DecodeQueryValue(equals < 0 ? pair : pair[..equals]);
			string value = DecodeQueryValue(equals < 0 ? "" : pair[(equals + 1)..]);
			if (key.Length == 0 || key.Any(char.IsControl) || value.Any(char.IsControl) || !fields.TryAdd(key, value))
				throw new InvalidOperationException("授权回调包含重复或无效的参数，请返回设置重新登录。");
		}

		return new OAuthCallback(
			fields.GetValueOrDefault("code"),
			fields.GetValueOrDefault("state"),
			fields.GetValueOrDefault("client_id"),
			fields.GetValueOrDefault("error"));
	}

	internal static HashSet<string> ParseScopes(string? value) =>
		(value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

	internal static bool HasUsageScopes(string? value)
	{
		HashSet<string> scopes = ParseScopes(value);
		return scopes.Contains(DirectScope) && scopes.Contains(InvokeScope);
	}

	internal static DateTimeOffset RequiredEarliestRefreshAt(JsonElement root)
	{
		if (!root.TryGetProperty("earliest_refresh_at", out JsonElement value))
			throw new InvalidOperationException("ChatGPT 登录响应缺少最早刷新时间，请重新登录。");

		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long unixSeconds))
		{
			try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
			catch (ArgumentOutOfRangeException) { }
		}
		else if (value.ValueKind == JsonValueKind.String)
		{
			string? text = value.GetString();
			if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset timestamp)) return timestamp;
			if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out unixSeconds))
			{
				try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
				catch (ArgumentOutOfRangeException) { }
			}
		}

		throw new InvalidOperationException("ChatGPT 登录响应的最早刷新时间无效，请重新登录。");
	}

	internal static string? SafeOAuthError(string body)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(body);
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out JsonElement error)) return null;
			string? code = error.ValueKind == JsonValueKind.String
				? error.GetString()
				: error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out JsonElement nested) && nested.ValueKind == JsonValueKind.String
					? nested.GetString()
					: null;
			return code is { Length: > 0 and <= 128 } && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ? code : null;
		}
		catch (JsonException) { return null; }
	}

	private static string DecodeQueryValue(string value)
	{
		for (int i = 0; i < value.Length; i++)
		{
			if (value[i] != '%') continue;
			if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
				throw new InvalidOperationException("授权回调包含无效的编码参数。");
			i += 2;
		}
		return Uri.UnescapeDataString(value.Replace('+', ' '));
	}
}

internal sealed record OAuthCallback(string? Code, string? State, string? ClientId, string? Error);
internal sealed record VerifiedIdToken(string Subject, string? Email);

internal static class OpenAiIdTokenValidator
{
	internal static async Task<VerifiedIdToken> ValidateAsync(HttpClient http, string idToken, string clientId, string nonce, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));
		using HttpRequestMessage request = new(HttpMethod.Get, OpenAiOAuthProtocol.JwksEndpoint);
		using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
		string body = await OAuthHttpResponse.ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode) throw new InvalidOperationException("无法获取 ChatGPT 身份验证密钥，请检查网络后重试。");

		try
		{
			JsonWebKeySet keySet = new(body);
			IList<SecurityKey> signingKeys = keySet.GetSigningKeys();
			if (signingKeys.Count == 0) throw new JsonException();
			JsonWebTokenHandler handler = new() { MapInboundClaims = false };
			TokenValidationResult result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
			{
				ValidIssuer = OpenAiOAuthProtocol.Issuer,
				ValidateIssuer = true,
				ValidAudience = clientId,
				ValidateAudience = true,
				ValidateLifetime = true,
				RequireExpirationTime = true,
				RequireSignedTokens = true,
				ValidateIssuerSigningKey = true,
				IssuerSigningKeys = signingKeys,
				ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
				ClockSkew = TimeSpan.FromMinutes(1),
			}).ConfigureAwait(false);
			if (!result.IsValid || result.SecurityToken is not JsonWebToken token || result.ClaimsIdentity is null)
				throw new JsonException();

			string[] audiences = token.Audiences.ToArray();
			string? subject = result.ClaimsIdentity.FindFirst("sub")?.Value;
			string? tokenNonce = result.ClaimsIdentity.FindFirst("nonce")?.Value;
			if (audiences.Length != 1 || audiences[0] != clientId || string.IsNullOrWhiteSpace(subject) || !string.Equals(tokenNonce, nonce, StringComparison.Ordinal))
				throw new JsonException();
			return new VerifiedIdToken(subject, result.ClaimsIdentity.FindFirst("email")?.Value);
		}
		catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or SecurityTokenException)
		{
			throw new InvalidOperationException("ChatGPT 身份令牌签名或身份信息验证失败；没有保存此次登录，请重试。");
		}
	}
}

internal static class OAuthHttpResponse
{
	internal const int MaximumResponseBytes = 1024 * 1024;

	internal static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
		return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
	}
}
