using System.Net;
using System.Text.Json;

namespace Nori.Plugins.ChatGpt;

internal sealed record ChatGptCredential(
	string AccessToken,
	string RefreshToken,
	DateTimeOffset ExpiresAt,
	string AccountId,
	string ClientId = "",
	string Subject = "",
	string IdToken = "",
	string Scopes = "",
	string? Email = null,
	DateTimeOffset EarliestRefreshAt = default);

internal sealed record ChatGptRegistration(string ClientId, string Subject, string? Email, ChatGptCredential? Credential, string Scopes = "");
internal sealed record ChatGptAuthState(string? ActiveClientId, string? ActiveSubject, List<ChatGptRegistration> Registrations);
internal sealed record ChatGptAccountOption(string? ClientId, string? Subject, string Label)
{
	public override string ToString() => Label;
}
internal sealed record ChatGptSignOutResult(bool? RemoteRevocationConfirmed);

internal sealed class LegacyCodexCredentialException() : InvalidOperationException("检测到旧版 Codex 设备码凭证。为避免将旧凭证用于新版 API，原数据已保留；请使用 Continue with ChatGPT 重新授权。");

/// <summary>串行刷新凭证；每次登录独立验证，只有安全保存后才切换活动账号。</summary>
internal sealed class ChatGptAuth(HttpClient http, ICredentialStore? store = null, IHostIdStore? hostIdStore = null)
{
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(15);
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly SemaphoreSlim _loginGate = new(1, 1);
	private ChatGptAuthState _state = new(null, null, []);
	private bool _pendingSave;
	private bool? _pendingSignOutRevocationConfirmed;
	private bool _storedCredentialUnreadable;
	private string? _transientHostId;

	internal bool IsSignedIn => ActiveCredential is not null;
	internal bool CanUseChatGptPlan => ActiveCredential is { } credential && OpenAiOAuthProtocol.HasUsageScopes(credential.Scopes);
	internal string? RestoreIssue { get; private set; }
	internal bool CanRetryPersistence => _pendingSave;
	internal bool CanClearUnusableStoredData => _storedCredentialUnreadable;
	internal string? ActiveAccountLabel => ActiveRegistration is { } registration ? RegistrationLabel(registration) : null;
	internal ChatGptAccountOption? ActiveAccountOption => ActiveRegistration is { } registration ? ToAccountOption(registration) : null;
	internal IReadOnlyList<ChatGptAccountOption> LoginOptions =>
		[new(null, null, "新增 ChatGPT 账号"), .. Volatile.Read(ref _state).Registrations.Select(ToAccountOption)];

	private ChatGptRegistration? ActiveRegistration
	{
		get
		{
			ChatGptAuthState state = Volatile.Read(ref _state);
			return FindRegistration(state, state.ActiveClientId, state.ActiveSubject);
		}
	}

	private ChatGptCredential? ActiveCredential => ActiveRegistration?.Credential;

	internal async Task RestoreAsync(CancellationToken cancellationToken)
	{
		if (store is null) return;
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			try
			{
				ChatGptAuthState? restored = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
				Volatile.Write(ref _state, restored ?? new ChatGptAuthState(null, null, []));
				RestoreIssue = null;
				_storedCredentialUnreadable = false;
			}
			catch (OperationCanceledException) { throw; }
			catch (LegacyCodexCredentialException exception)
			{
				RestoreIssue = exception.Message;
				_storedCredentialUnreadable = true;
			}
			catch (Exception)
			{
				RestoreIssue = "无法恢复已保存的登录数据；原数据已保留。请重新登录，或检查系统密钥库和插件存储。";
				_storedCredentialUnreadable = true;
			}
		}
		finally { _gate.Release(); }
	}

	internal async Task SignInAsync(ChatGptAccountOption? target, Action<Uri> openBrowser, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(openBrowser);
		if (!await _loginGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
			throw new InvalidOperationException("已有 ChatGPT 登录正在进行，请先取消当前登录。");

		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(LoginTimeout);
			ChatGptRegistration? selected = null;
			if (target?.ClientId is { } targetClientId && target.Subject is { } targetSubject)
			{
				selected = FindRegistration(Volatile.Read(ref _state), targetClientId, targetSubject)
					?? throw new InvalidOperationException("所选 ChatGPT 账号已不存在，请选择新增账号。");
			}
			else if (target is not null && (target.ClientId is not null || target.Subject is not null))
			{
				throw new InvalidOperationException("所选 ChatGPT 账号信息无效，请重新选择。");
			}

			string hostId = await GetHostIdAsync(timeout.Token).ConfigureAwait(false);
			string clientId = selected?.ClientId ?? OpenAiOAuthProtocol.DynamicClientId;
			for (int attempt = 0; attempt < 2; attempt++)
			{
				await using LoopbackCallbackListener listener = LoopbackCallbackListener.Start();
				string state = OpenAiOAuthProtocol.RandomBase64Url(32);
				string nonce = OpenAiOAuthProtocol.RandomBase64Url(32);
				string verifier = OpenAiOAuthProtocol.RandomBase64Url(32);
				Uri authorizationUri = OpenAiOAuthProtocol.CreateAuthorizationUri(clientId, hostId, listener.RedirectUri, state, nonce, OpenAiOAuthProtocol.CreateChallenge(verifier), selected);
				openBrowser(authorizationUri);

				(string code, string exchangeClientId) = await WaitForAuthorizationCodeAsync(listener, clientId, state, timeout.Token).ConfigureAwait(false);
				JsonDocument tokenDocument;
				try
				{
					tokenDocument = await RequestTokenAsync(new Dictionary<string, string>
					{
						["grant_type"] = "authorization_code",
						["client_id"] = exchangeClientId,
						["code"] = code,
						["code_verifier"] = verifier,
						["redirect_uri"] = listener.RedirectUri.AbsoluteUri,
						["resource"] = OpenAiOAuthProtocol.Resource,
					}, timeout.Token).ConfigureAwait(false);
				}
				catch (OAuthTokenException exception) when (exception.Code == "invalid_grant" && attempt == 0)
				{
					// 授权码只能使用一次；保留刚签发的 client_id 并重新走完整授权。
					clientId = exchangeClientId;
					continue;
				}

				using (tokenDocument)
				{
					JsonElement root = tokenDocument.RootElement;
					string accessToken = RequiredString(root, "access_token");
					string refreshToken = RequiredString(root, "refresh_token");
					string idToken = RequiredString(root, "id_token");
					int expiresIn = RequiredInt32(root, "expires_in");
					DateTimeOffset earliestRefreshAt = OpenAiOAuthProtocol.RequiredEarliestRefreshAt(root);
					string scopes = ReadOptionalString(root, "scope") ?? "";
					VerifiedIdToken identity = await OpenAiIdTokenValidator.ValidateAsync(http, idToken, exchangeClientId, nonce, timeout.Token).ConfigureAwait(false);
					if (selected is not null && !string.Equals(selected.Subject, identity.Subject, StringComparison.Ordinal))
						throw new InvalidOperationException("重新授权返回了不同的 ChatGPT 账号；原账号凭证未更改。");

					ChatGptCredential credential = new(
						accessToken,
						refreshToken,
						DateTimeOffset.UtcNow.AddSeconds(expiresIn),
						identity.Subject,
						exchangeClientId,
						identity.Subject,
						idToken,
						scopes,
						identity.Email,
						earliestRefreshAt);
				await SaveAndActivateAsync(credential, timeout.Token).ConfigureAwait(false);
				return;
				}
			}

			throw new InvalidOperationException("ChatGPT 授权码已失效，请再次点击 Continue with ChatGPT 重试。");
		}
		finally { _loginGate.Release(); }
	}

	internal async Task<ChatGptCredential> GetCredentialAsync(CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_pendingSave) await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);
			ChatGptRegistration registration = ActiveRegistration ?? throw new InvalidOperationException("请先在设置 → ChatGPT OAuth 中使用 ChatGPT 账号登录。");
			ChatGptCredential current = registration.Credential ?? throw new InvalidOperationException("此 ChatGPT 账号需要重新登录；原注册信息已保留。");
			EnsureUsageScopes(current);

			DateTimeOffset now = DateTimeOffset.UtcNow;
			if (current.ExpiresAt <= now && current.EarliestRefreshAt > now)
				throw new InvalidOperationException("ChatGPT 令牌暂不可刷新，请稍后重试。");
			if (current.ExpiresAt > now.AddMinutes(3) || current.EarliestRefreshAt > now) return current;

			ChatGptCredential refreshed;
			try { refreshed = await RefreshAsync(current, cancellationToken).ConfigureAwait(false); }
			catch (OAuthTokenException exception) when (IsTerminalRefreshError(exception.Code))
			{
				await MarkReauthorizationRequiredAsync(registration, cancellationToken).ConfigureAwait(false);
				throw new InvalidOperationException("ChatGPT 刷新凭证已失效，请使用 Continue with ChatGPT 重新登录；原注册信息已保留。");
			}

			ReplaceActiveCredential(refreshed);
			_pendingSave = store is not null;
			if (store is not null) await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);
			EnsureUsageScopes(refreshed);
			return refreshed;
		}
		finally { _gate.Release(); }
	}

	internal async Task<ChatGptSignOutResult> SignOutAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_pendingSave)
			{
				bool? pendingSignOut = _pendingSignOutRevocationConfirmed;
				try { await PersistStateAsync(CancellationToken.None).ConfigureAwait(false); }
				catch (Exception) { throw new InvalidOperationException("本地退出状态仍未写回；请检查插件存储后重试。远端撤销结果未改变。"); }
				if (pendingSignOut.HasValue)
				{
					_pendingSignOutRevocationConfirmed = null;
					return new ChatGptSignOutResult(pendingSignOut.Value);
				}
			}

			ChatGptRegistration? registration = ActiveRegistration;
			if (registration?.Credential is not { } credential) return new ChatGptSignOutResult(null);

			bool revoked = await TryRevokeAsync(credential, cancellationToken).ConfigureAwait(false);
			ChatGptAuthState current = Volatile.Read(ref _state);
			List<ChatGptRegistration> registrations = current.Registrations
				.Select(item => SameRegistration(item, registration) ? item with { Credential = null } : item)
				.ToList();
			Volatile.Write(ref _state, current with { Registrations = registrations });
			_pendingSave = store is not null;
			_pendingSignOutRevocationConfirmed = store is not null ? revoked : null;
			if (store is not null)
			{
				try { await PersistStateAsync(CancellationToken.None).ConfigureAwait(false); }
				catch (Exception) { throw new InvalidOperationException("已停止使用本机 ChatGPT 令牌，但无法确认本地凭证状态已写回；请检查插件存储后重试。"); }
				_pendingSignOutRevocationConfirmed = null;
			}
			RestoreIssue = null;
			return new ChatGptSignOutResult(revoked);
		}
		finally { _gate.Release(); }
	}

	internal async Task ClearUnusableStoredDataAsync(CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!_storedCredentialUnreadable || store is null)
				throw new InvalidOperationException("当前没有无法恢复的本地凭证可清除。");
			await store.DeleteAsync(cancellationToken).ConfigureAwait(false);
			Volatile.Write(ref _state, new ChatGptAuthState(null, null, []));
			_pendingSave = false;
			_pendingSignOutRevocationConfirmed = null;
			_storedCredentialUnreadable = false;
			RestoreIssue = null;
		}
		finally { _gate.Release(); }
	}

	internal async Task ClearSessionAsync()
	{
		await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
		try
		{
			Volatile.Write(ref _state, new ChatGptAuthState(null, null, []));
			_pendingSave = false;
			_pendingSignOutRevocationConfirmed = null;
			RestoreIssue = null;
		}
		finally { _gate.Release(); }
	}

	private async Task<string> GetHostIdAsync(CancellationToken cancellationToken)
	{
		if (hostIdStore is not null) return await hostIdStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
		return _transientHostId ??= $"urn:uuid:{Guid.NewGuid():D}";
	}

	private async Task<(string Code, string ClientId)> WaitForAuthorizationCodeAsync(
		LoopbackCallbackListener listener,
		string requestedClientId,
		string expectedState,
		CancellationToken cancellationToken)
	{
		while (true)
		{
			await using LoopbackCallbackRequest request = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
			OAuthCallback callback;
			try { callback = OpenAiOAuthProtocol.ParseCallback(request.RequestTarget); }
			catch (InvalidOperationException)
			{
				await request.RespondAsync(false, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (!string.Equals(callback.State, expectedState, StringComparison.Ordinal))
			{
				await request.RespondAsync(false, cancellationToken).ConfigureAwait(false);
				continue;
			}
			if (!string.IsNullOrEmpty(callback.Error))
			{
				await request.RespondAsync(true, cancellationToken).ConfigureAwait(false);
				if (callback.Error == "access_denied") throw new InvalidOperationException("你已取消 ChatGPT 授权；当前账号未更改。");
				throw new InvalidOperationException("ChatGPT 授权未完成，请返回设置重新登录。");
			}
			if (string.IsNullOrWhiteSpace(callback.Code))
			{
				await request.RespondAsync(false, cancellationToken).ConfigureAwait(false);
				throw new InvalidOperationException("ChatGPT 授权回调缺少授权码，请重新登录。");
			}

			string exchangeClientId;
			if (requestedClientId == OpenAiOAuthProtocol.DynamicClientId)
			{
				if (string.IsNullOrWhiteSpace(callback.ClientId) || callback.ClientId == OpenAiOAuthProtocol.DynamicClientId)
				{
					await request.RespondAsync(false, cancellationToken).ConfigureAwait(false);
					throw new InvalidOperationException("首次 ChatGPT 注册未返回已签发的 client_id；没有交换授权码或保存凭证。");
				}
				exchangeClientId = callback.ClientId;
			}
			else
			{
				if (callback.ClientId is { Length: > 0 } returnedClientId && returnedClientId != requestedClientId)
				{
					await request.RespondAsync(false, cancellationToken).ConfigureAwait(false);
					throw new InvalidOperationException("ChatGPT 授权返回的 client_id 与所选注册不一致；原账号凭证未更改。");
				}
				exchangeClientId = requestedClientId;
			}

			await request.RespondAsync(true, cancellationToken).ConfigureAwait(false);
			return (callback.Code, exchangeClientId);
		}
	}

	private async Task<JsonDocument> RequestTokenAsync(Dictionary<string, string> fields, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(RequestTimeout);
		using HttpRequestMessage request = new(HttpMethod.Post, OpenAiOAuthProtocol.TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
		using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
		string body = await OAuthHttpResponse.ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
			throw new OAuthTokenException(OpenAiOAuthProtocol.SafeOAuthError(body), response.StatusCode);
		try
		{
			JsonDocument document = JsonDocument.Parse(body);
			if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new JsonException(); }
			return document;
		}
		catch (JsonException) { throw new InvalidOperationException("ChatGPT 令牌服务返回了无效响应。"); }
	}

	private async Task<ChatGptCredential> RefreshAsync(ChatGptCredential current, CancellationToken cancellationToken)
	{
		using JsonDocument document = await RequestTokenAsync(new Dictionary<string, string>
		{
			["grant_type"] = "refresh_token",
			["client_id"] = current.ClientId,
			["refresh_token"] = current.RefreshToken,
			["resource"] = OpenAiOAuthProtocol.Resource,
		}, cancellationToken).ConfigureAwait(false);
		JsonElement root = document.RootElement;
		string access = RequiredString(root, "access_token");
		string refresh = RequiredString(root, "refresh_token");
		string scopes = ReadOptionalString(root, "scope") ?? current.Scopes;
		int expiresIn = RequiredInt32(root, "expires_in");
		DateTimeOffset earliestRefreshAt = OpenAiOAuthProtocol.RequiredEarliestRefreshAt(root);
		return current with
		{
			AccessToken = access,
			RefreshToken = refresh,
			ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
			Scopes = scopes,
			EarliestRefreshAt = earliestRefreshAt,
		};
	}

	private async Task<bool> TryRevokeAsync(ChatGptCredential credential, CancellationToken cancellationToken)
	{
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(RequestTimeout);
			using HttpRequestMessage request = new(HttpMethod.Post, OpenAiOAuthProtocol.RevocationEndpoint)
			{
				Content = new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["token"] = credential.RefreshToken,
					["token_type_hint"] = "refresh_token",
					["client_id"] = credential.ClientId,
				}),
			};
			using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
			_ = await OAuthHttpResponse.ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
			return response.StatusCode == HttpStatusCode.OK;
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
		{
			return false;
		}
	}

	private async Task SaveAndActivateAsync(ChatGptCredential credential, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ChatGptAuthState current = Volatile.Read(ref _state);
			List<ChatGptRegistration> registrations = current.Registrations.ToList();
			int index = registrations.FindIndex(item => item.ClientId == credential.ClientId && item.Subject == credential.Subject);
			ChatGptRegistration registration = new(credential.ClientId, credential.Subject, credential.Email, credential, credential.Scopes);
			if (index < 0) registrations.Add(registration);
			else registrations[index] = registration;
			ChatGptAuthState updated = new(credential.ClientId, credential.Subject, registrations);
			if (store is not null) await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
			Volatile.Write(ref _state, updated);
			_pendingSave = false;
			_pendingSignOutRevocationConfirmed = null;
			_storedCredentialUnreadable = false;
			RestoreIssue = null;
		}
		finally { _gate.Release(); }
	}

	private async Task MarkReauthorizationRequiredAsync(ChatGptRegistration registration, CancellationToken cancellationToken)
	{
		ChatGptAuthState current = Volatile.Read(ref _state);
		List<ChatGptRegistration> registrations = current.Registrations
			.Select(item => SameRegistration(item, registration) ? item with { Credential = null } : item)
			.ToList();
		Volatile.Write(ref _state, current with { Registrations = registrations });
		_pendingSave = store is not null;
		RestoreIssue = "ChatGPT 刷新凭证已失效，请使用 Continue with ChatGPT 重新登录；原注册信息已保留。";
		if (store is not null) await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);
	}

	private void ReplaceActiveCredential(ChatGptCredential credential)
	{
		ChatGptAuthState current = Volatile.Read(ref _state);
		List<ChatGptRegistration> registrations = current.Registrations
			.Select(item => item.ClientId == credential.ClientId && item.Subject == credential.Subject
				? item with { Email = credential.Email ?? item.Email, Credential = credential, Scopes = credential.Scopes }
				: item)
			.ToList();
		Volatile.Write(ref _state, current with { Registrations = registrations });
	}

	private async Task PersistStateAsync(CancellationToken cancellationToken)
	{
		if (store is not null) await store.SaveAsync(Volatile.Read(ref _state), cancellationToken).ConfigureAwait(false);
		_pendingSave = false;
	}

	private static void EnsureUsageScopes(ChatGptCredential credential)
	{
		if (!OpenAiOAuthProtocol.HasUsageScopes(credential.Scopes))
			throw new InvalidOperationException("此账号的授权未包含 chatgpt.tokens.use.direct 与 resource.invoke，不能使用 ChatGPT plan。请重新登录并授予相关权限。");
	}

	private static bool IsTerminalRefreshError(string? code) => code is
		"invalid_grant" or "invalid_refresh_token" or "token_expired" or "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused";

	private static ChatGptAccountOption ToAccountOption(ChatGptRegistration registration) =>
		new(registration.ClientId, registration.Subject, RegistrationLabel(registration));

	private static string RegistrationLabel(ChatGptRegistration registration)
	{
		string suffix = registration.ClientId.Length <= 6 ? registration.ClientId : registration.ClientId[^6..];
		return $"{registration.Email ?? $"已保存账号 {registration.Subject}"} · {suffix}";
	}

	private static ChatGptRegistration? FindRegistration(ChatGptAuthState state, string? clientId, string? subject) =>
		clientId is null || subject is null ? null : state.Registrations.FirstOrDefault(item => item.ClientId == clientId && item.Subject == subject);

	private static bool SameRegistration(ChatGptRegistration left, ChatGptRegistration right) => left.ClientId == right.ClientId && left.Subject == right.Subject;

	private static string RequiredString(JsonElement root, string name)
	{
		if (root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!;
		throw new InvalidOperationException("ChatGPT 令牌响应缺少必要字段。");
	}

	private static string? ReadOptionalString(JsonElement root, string name) =>
		root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static int RequiredInt32(JsonElement root, string name)
	{
		if (root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) && result > 0) return result;
		throw new InvalidOperationException("ChatGPT 令牌响应缺少有效的有效期。");
	}
}

internal sealed class OAuthTokenException(string? code, HttpStatusCode statusCode) : InvalidOperationException(CreateMessage(code, statusCode))
{
	internal string? Code { get; } = code;

	private static string CreateMessage(string? code, HttpStatusCode statusCode) => code switch
	{
		"invalid_grant" => "ChatGPT 授权码或刷新凭证已失效，请重新授权。",
		"invalid_client" => "ChatGPT OAuth 客户端注册无效，请稍后重试或重新登录。",
		_ => $"ChatGPT 令牌请求失败（HTTP {(int)statusCode}），请检查网络或稍后重试。",
	};
}
