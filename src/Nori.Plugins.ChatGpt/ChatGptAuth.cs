using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Nori.Plugins.ChatGpt;

internal sealed record ChatGptCredential(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string AccountId);
internal sealed record DeviceAuthorization(string Id, string UserCode, TimeSpan Interval);

/// <summary>串行恢复、登录、刷新和退出；轮换后的令牌先保留在内存，再安全持久化。</summary>
internal sealed class ChatGptAuth(HttpClient http, ICredentialStore? store = null)
{
	internal const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
	internal const string VerificationUrl = "https://auth.openai.com/codex/device";
	private readonly SemaphoreSlim _gate = new(1, 1);
	private ChatGptCredential? _credential;
	private bool _pendingSave;

	internal bool IsSignedIn => Volatile.Read(ref _credential) is not null;
	internal string? RestoreIssue { get; private set; }

	internal async Task RestoreAsync(CancellationToken cancellationToken)
	{
		if (store is null) return;
		await _gate.WaitAsync(cancellationToken);
		try
		{
			try { Volatile.Write(ref _credential, await store.LoadAsync(cancellationToken)); RestoreIssue = null; }
			catch (OperationCanceledException) { throw; }
			catch (Exception) { RestoreIssue = "无法恢复已保存的登录数据。请重新登录，或退出登录清除损坏的数据。"; }
		}
		finally { _gate.Release(); }
	}

	internal async Task<DeviceAuthorization> BeginLoginAsync(CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new(HttpMethod.Post, "https://auth.openai.com/api/accounts/deviceauth/usercode") { Content = JsonContent.Create(new { client_id = ClientId }) };
		using HttpResponseMessage response = await SendAsync(request, cancellationToken);
		using JsonDocument document = await ReadResponseAsync(response, "申请设备码", cancellationToken);
		JsonElement root = document.RootElement;
		string intervalText = root.GetProperty("interval").ToString();
		if (!double.TryParse(intervalText, NumberStyles.Float, CultureInfo.InvariantCulture, out double interval) || !double.IsFinite(interval) || interval < 0 || interval > 900)
			throw new InvalidOperationException("ChatGPT 返回的设备码轮询间隔无效");
		return new DeviceAuthorization(RequiredString(root, "device_auth_id"), RequiredString(root, "user_code"), TimeSpan.FromSeconds(Math.Max(1, interval)));
	}

	internal async Task CompleteLoginAsync(DeviceAuthorization device, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromMinutes(15));
			TimeSpan interval = device.Interval;
			while (true)
			{
				await Task.Delay(interval, timeout.Token);
				using HttpRequestMessage request = new(HttpMethod.Post, "https://auth.openai.com/api/accounts/deviceauth/token") { Content = JsonContent.Create(new { device_auth_id = device.Id, user_code = device.UserCode }) };
				using HttpResponseMessage response = await SendAsync(request, timeout.Token);
				if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) continue;
				if (!response.IsSuccessStatusCode)
				{
					// 仅解析协议错误码，不把响应正文或凭证带入错误消息。
					string body = await response.Content.ReadAsStringAsync(timeout.Token);
					string? error = null;
					try
					{
						using JsonDocument failure = JsonDocument.Parse(body);
						if (failure.RootElement.TryGetProperty("error", out JsonElement value))
							error = value.ValueKind == JsonValueKind.String ? value.GetString() : value.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
					}
					catch (JsonException) { }
					if (error == "deviceauth_authorization_pending") continue;
					if (error == "slow_down") { interval += TimeSpan.FromSeconds(5); continue; }
					throw new InvalidOperationException($"ChatGPT 设备授权失败（HTTP {(int)response.StatusCode}），请重新登录");
				}
				using JsonDocument document = await ReadResponseAsync(response, "读取设备授权", timeout.Token);
				ChatGptCredential credential = await RequestTokenAsync(new()
				{
					["grant_type"] = "authorization_code",
					["client_id"] = ClientId,
					["code"] = RequiredString(document.RootElement, "authorization_code"),
					["code_verifier"] = RequiredString(document.RootElement, "code_verifier"),
					["redirect_uri"] = "https://auth.openai.com/deviceauth/callback",
				}, null, timeout.Token);
				Volatile.Write(ref _credential, credential);
				_pendingSave = store is not null;
				if (store is not null) await store.SaveAsync(credential, CancellationToken.None);
				_pendingSave = false;
				RestoreIssue = null;
				return;
			}
		}
		finally { _gate.Release(); }
	}

	internal async Task<ChatGptCredential> GetCredentialAsync(CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			ChatGptCredential current = _credential ?? throw new InvalidOperationException("请先在设置 → ChatGPT OAuth 中登录 ChatGPT 账号");
			if (_pendingSave && store is not null) { await store.SaveAsync(current, CancellationToken.None); _pendingSave = false; }
			if (current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(3)) return current;
			ChatGptCredential refreshed = await RequestTokenAsync(new()
			{
				["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = current.RefreshToken,
			}, current.RefreshToken, cancellationToken);
			Volatile.Write(ref _credential, refreshed);
			if (store is not null)
			{
				_pendingSave = true;
				await store.SaveAsync(refreshed, CancellationToken.None);
				_pendingSave = false;
			}
			return refreshed;
		}
		finally { _gate.Release(); }
	}

	internal async Task SignOutAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (store is not null) await store.DeleteAsync(cancellationToken);
			Volatile.Write(ref _credential, null);
			_pendingSave = false;
			RestoreIssue = null;
		}
		finally { _gate.Release(); }
	}

	internal async Task ClearSessionAsync()
	{
		await _gate.WaitAsync(CancellationToken.None);
		try { Volatile.Write(ref _credential, null); }
		finally { _gate.Release(); }
	}

	private async Task<ChatGptCredential> RequestTokenAsync(Dictionary<string, string> fields, string? previousRefresh, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new(HttpMethod.Post, "https://auth.openai.com/oauth/token") { Content = new FormUrlEncodedContent(fields) };
		using HttpResponseMessage response = await SendAsync(request, cancellationToken);
		using JsonDocument document = await ReadResponseAsync(response, "换取或刷新登录令牌，请重新登录", cancellationToken);
		JsonElement root = document.RootElement;
		string access = RequiredString(root, "access_token");
		string refresh = root.TryGetProperty("refresh_token", out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : previousRefresh ?? throw new InvalidOperationException("ChatGPT 登录响应缺少刷新令牌");
		if (!root.TryGetProperty("expires_in", out JsonElement expires) || !expires.TryGetInt32(out int seconds) || seconds <= 0)
			throw new InvalidOperationException("ChatGPT 登录响应缺少有效期");
		return new ChatGptCredential(access, refresh, DateTimeOffset.UtcNow.AddSeconds(seconds), AccountIdFromToken(access));
	}

	internal static string AccountIdFromToken(string token)
	{
		// 这里只读取 HTTPS 令牌响应中的路由元数据，不将未验证的 JWT 当作身份认证依据。
		try
		{
			string[] parts = token.Split('.');
			if (parts.Length != 3) throw new FormatException();
			string payload = parts[1].Replace('-', '+').Replace('_', '/');
			payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
			using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
			return RequiredString(document.RootElement.GetProperty("https://api.openai.com/auth"), "chatgpt_account_id");
		}
		catch (Exception exception) when (exception is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
		{
			throw new InvalidOperationException("ChatGPT 登录令牌缺少账号信息，请重新登录");
		}
	}

	private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		try
		{
			await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken);
			return response;
		}
		catch { response.Dispose(); throw; }
	}

	private static string RequiredString(JsonElement root, string name)
	{
		if (root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!;
		throw new InvalidOperationException("ChatGPT 授权响应缺少必要字段");
	}

	private static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
	{
		if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ChatGPT {operation}失败（HTTP {(int)response.StatusCode}）");
		try { return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); }
		catch (JsonException) { throw new InvalidOperationException("ChatGPT 授权响应不是有效 JSON"); }
	}
}
