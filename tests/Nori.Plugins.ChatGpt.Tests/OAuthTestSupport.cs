using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nori.Plugins.ChatGpt.Tests;

internal sealed class TestIdentityKey : IDisposable
{
	private readonly RSA _rsa = RSA.Create(2048);
	internal const string KeyId = "test-signing-key";

	internal string Jwks
	{
		get
		{
			RSAParameters parameters = _rsa.ExportParameters(false);
			return JsonSerializer.Serialize(new
			{
				keys = new[]
				{
					new { kty = "RSA", kid = KeyId, use = "sig", alg = "RS256", n = Base64Url(parameters.Modulus!), e = Base64Url(parameters.Exponent!) },
				},
			});
		}
	}

	internal string CreateIdToken(
		string clientId,
		string nonce,
		string subject = "subject-1",
		string issuer = OpenAiOAuthProtocol.Issuer,
		string? audience = null,
		DateTimeOffset? expiresAt = null,
		string? email = "user@example.com")
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		Dictionary<string, object?> claims = new()
		{
			["iss"] = issuer,
			["aud"] = audience ?? clientId,
			["sub"] = subject,
			["iat"] = now.ToUnixTimeSeconds(),
			["exp"] = (expiresAt ?? now.AddMinutes(10)).ToUnixTimeSeconds(),
			["nonce"] = nonce,
			["email_verified"] = true,
		};
		if (email is not null) claims["email"] = email;
		string header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid = KeyId }));
		string payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
		byte[] input = Encoding.ASCII.GetBytes($"{header}.{payload}");
		string signature = Base64Url(_rsa.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
		return $"{header}.{payload}.{signature}";
	}

	internal string CreateInvalidSignature(string clientId, string nonce)
	{
		string token = CreateIdToken(clientId, nonce);
		string[] parts = token.Split('.');
		string replacement = parts[2][0] == 'A' ? "B" : "A";
		parts[2] = replacement + parts[2][1..];
		return string.Join('.', parts);
	}

	internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	public void Dispose() => _rsa.Dispose();
}

internal sealed class MemoryCredentialStore(ChatGptAuthState? value = null) : ICredentialStore
{
	internal ChatGptAuthState? Value = value;
	internal ChatGptAuthState? PendingValue;
	internal int FailNextSaveCount;
	internal int DeleteCalls;
	public Task<ChatGptAuthState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);

	public Task SaveAsync(ChatGptAuthState state, CancellationToken cancellationToken)
	{
		PendingValue = state;
		if (FailNextSaveCount-- > 0) return Task.FromException(new InvalidOperationException("save failed"));
		Value = state;
		return Task.CompletedTask;
	}

	public Task DeleteAsync(CancellationToken cancellationToken)
	{
		DeleteCalls++;
		Value = null;
		return Task.CompletedTask;
	}
}

internal sealed class MemoryHostIdStore(string value = "urn:uuid:00000000-0000-4000-8000-000000000001") : IHostIdStore
{
	internal string Value = value;
	public Task<string> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
}

internal sealed class TestHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}

internal static class OAuthTestHelpers
{
	internal static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
		new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

	internal static HttpResponseMessage TokenResponse(
		string idToken,
		string accessToken = "opaque-access-token",
		string refreshToken = "refresh-token-1",
		string scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
		int expiresIn = 3600,
		DateTimeOffset? earliestRefreshAt = null) =>
		Json(JsonSerializer.Serialize(new
		{
			access_token = accessToken,
			refresh_token = refreshToken,
			id_token = idToken,
			token_type = "Bearer",
			expires_in = expiresIn,
			scope = scopes,
			earliest_refresh_at = (earliestRefreshAt ?? DateTimeOffset.UtcNow.AddMinutes(5)).ToUnixTimeSeconds(),
		}));

	internal static Dictionary<string, string> ParseForm(string value) => ParseQuery(value);

	internal static Dictionary<string, string> ParseQuery(string value)
	{
		Dictionary<string, string> result = new(StringComparer.Ordinal);
		foreach (string pair in value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			int separator = pair.IndexOf('=');
			string key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator].Replace('+', ' '));
			string data = Uri.UnescapeDataString(separator < 0 ? "" : pair[(separator + 1)..].Replace('+', ' '));
			result.Add(key, data);
		}
		return result;
	}

	internal static async Task<int> SendCallbackAsync(Uri redirectUri, string rawQuery)
	{
		using TcpClient client = new();
		await client.ConnectAsync(IPAddress.Loopback, redirectUri.Port);
		NetworkStream stream = client.GetStream();
		string request = $"GET {redirectUri.AbsolutePath}?{rawQuery} HTTP/1.1\r\nHost: 127.0.0.1:{redirectUri.Port}\r\nConnection: close\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
		using MemoryStream response = new();
		byte[] buffer = new byte[1024];
		while (response.Length <= 8192)
		{
			int read = await stream.ReadAsync(buffer);
			if (read == 0) break;
			await response.WriteAsync(buffer.AsMemory(0, read));
			if (Encoding.ASCII.GetString(response.GetBuffer(), 0, checked((int)response.Length)).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
		}
		string status = Encoding.ASCII.GetString(response.GetBuffer(), 0, checked((int)response.Length)).Split("\r\n", StringSplitOptions.None)[0];
		return int.Parse(status.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
	}

	internal static string CallbackQuery(Uri authorizationUri, string? clientId = "oaiapp_test", string code = "authorization-code")
	{
		Dictionary<string, string> authorization = ParseQuery(authorizationUri.Query);
		List<KeyValuePair<string, string>> fields = [new("code", code), new("state", authorization["state"])];
		if (clientId is not null) fields.Add(new("client_id", clientId));
		return string.Join("&", fields.Select(field => $"{Uri.EscapeDataString(field.Key)}={Uri.EscapeDataString(field.Value)}"));
	}
}
