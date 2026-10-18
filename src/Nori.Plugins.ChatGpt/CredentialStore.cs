using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nori.Plugins.ChatGpt;

internal interface ICredentialStore
{
	Task<ChatGptAuthState?> LoadAsync(CancellationToken cancellationToken);
	Task SaveAsync(ChatGptAuthState state, CancellationToken cancellationToken);
	Task DeleteAsync(CancellationToken cancellationToken);
}

internal interface IHostIdStore
{
	Task<string> GetOrCreateAsync(CancellationToken cancellationToken);
}

internal interface ICredentialProtection
{
	Task<byte[]> ProtectAsync(byte[] plain, CancellationToken cancellationToken);
	Task<byte[]> UnprotectAsync(byte[] envelope, CancellationToken cancellationToken);
	Task DeleteKeyAsync(byte[] envelope, CancellationToken cancellationToken);
}

/// <summary>宿主 KV 只接收密文；不直接定位目录，不读取其他应用或插件的凭证。</summary>
internal sealed class CredentialStore(
	Func<CancellationToken, ValueTask<JsonNode?>> read,
	Func<JsonNode, CancellationToken, ValueTask> write,
	Func<CancellationToken, ValueTask> delete,
	ICredentialProtection protection) : ICredentialStore
{
	private byte[]? _envelope;

	public async Task<ChatGptAuthState?> LoadAsync(CancellationToken cancellationToken)
	{
		try
		{
			JsonNode? value = await read(cancellationToken).ConfigureAwait(false);
			if (value is null) return null;
			string encoded = value.GetValue<string>();
			if (encoded.Length > 3 * 1024 * 1024) throw new FormatException();
			_envelope = Convert.FromBase64String(encoded);
			if (_envelope.Length > 2 * 1024 * 1024) throw new FormatException();
			byte[] plain = await protection.UnprotectAsync(_envelope, cancellationToken).ConfigureAwait(false);
			try
			{
				using JsonDocument document = JsonDocument.Parse(plain);
				if (IsLegacyCredential(document.RootElement)) throw new LegacyCodexCredentialException();
				ChatGptAuthState state = JsonSerializer.Deserialize<ChatGptAuthState>(plain) ?? throw new JsonException();
				Validate(state);
				return state;
			}
			finally { CryptographicOperations.ZeroMemory(plain); }
		}
		catch (OperationCanceledException) { throw; }
		catch (LegacyCodexCredentialException) { throw; }
		catch { throw new InvalidOperationException("无法恢复 ChatGPT 登录数据：密文损坏或系统密钥库不可用。原数据已保留。请重新登录，或检查系统密钥库。"); }
	}

	public async Task SaveAsync(ChatGptAuthState state, CancellationToken cancellationToken)
	{
		Validate(state);
		byte[] plain = JsonSerializer.SerializeToUtf8Bytes(state);
		try
		{
			byte[] envelope = await protection.ProtectAsync(plain, cancellationToken).ConfigureAwait(false);
			await write(JsonValue.Create(Convert.ToBase64String(envelope))!, cancellationToken).ConfigureAwait(false);
			_envelope = envelope;
		}
		catch (OperationCanceledException) { throw; }
		catch { throw new InvalidOperationException("无法安全保存 ChatGPT 登录数据，请检查插件数据目录权限及系统密钥库是否可用。Linux 需要安装 secret-tool 并解锁密钥环。"); }
		finally { CryptographicOperations.ZeroMemory(plain); }
	}

	public async Task DeleteAsync(CancellationToken cancellationToken)
	{
		try { await delete(cancellationToken).ConfigureAwait(false); }
		catch (OperationCanceledException) { throw; }
		catch { throw new InvalidOperationException("无法删除 ChatGPT 登录数据，请检查插件数据目录权限后重试。"); }
		byte[]? envelope = _envelope;
		_envelope = null;
		if (envelope is not null)
		{
			try { await protection.DeleteKeyAsync(envelope, CancellationToken.None).ConfigureAwait(false); }
			catch (Exception) { }
		}
	}

	private static bool IsLegacyCredential(JsonElement root) =>
		root.ValueKind == JsonValueKind.Object &&
		(root.TryGetProperty("AccessToken", out _) || root.TryGetProperty("access_token", out _)) &&
		!root.TryGetProperty("Registrations", out _) && !root.TryGetProperty("registrations", out _);

	private static void Validate(ChatGptAuthState state)
	{
		if (state.Registrations is null) throw new JsonException();
		HashSet<string> registrations = new(StringComparer.Ordinal);
		foreach (ChatGptRegistration registration in state.Registrations)
		{
			if (string.IsNullOrWhiteSpace(registration.ClientId) || string.IsNullOrWhiteSpace(registration.Subject) ||
				!registrations.Add(registration.ClientId + "\0" + registration.Subject)) throw new JsonException();
			if (registration.Credential is not { } credential) continue;
			if (string.IsNullOrWhiteSpace(credential.AccessToken) || string.IsNullOrWhiteSpace(credential.RefreshToken) ||
				string.IsNullOrWhiteSpace(credential.IdToken) || string.IsNullOrWhiteSpace(credential.ClientId) ||
				string.IsNullOrWhiteSpace(credential.Subject) || credential.ExpiresAt == default || credential.EarliestRefreshAt == default ||
				credential.ClientId != registration.ClientId || credential.Subject != registration.Subject || credential.AccountId != credential.Subject || credential.Scopes != registration.Scopes)
				throw new JsonException();
		}
		if ((state.ActiveClientId is null) != (state.ActiveSubject is null) ||
			state.ActiveClientId is not null && !state.Registrations.Any(registration => registration.ClientId == state.ActiveClientId && registration.Subject == state.ActiveSubject))
			throw new JsonException();
	}
}

/// <summary>稳定主机标识单独保存在非秘密 KV 中，退出登录不删除。</summary>
internal sealed class HostIdStore(
	Func<CancellationToken, Task<string?>> read,
	Func<string, CancellationToken, Task> write) : IHostIdStore
{
	private readonly SemaphoreSlim _gate = new(1, 1);

	public async Task<string> GetOrCreateAsync(CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			string? saved;
			try { saved = await read(cancellationToken).ConfigureAwait(false); }
			catch (OperationCanceledException) { throw; }
			catch { throw new InvalidOperationException("无法读取已保存的 ChatGPT 主机标识，请检查插件存储。"); }

			if (saved is not null)
			{
				if (IsHostId(saved)) return saved;
				throw new InvalidOperationException("已保存的 ChatGPT 主机标识无效；为避免创建新的注册，请检查插件存储。");
			}

			string created = $"urn:uuid:{Guid.NewGuid():D}";
			try { await write(created, cancellationToken).ConfigureAwait(false); }
			catch (OperationCanceledException) { throw; }
			catch { throw new InvalidOperationException("无法保存稳定的 ChatGPT 主机标识，请检查插件存储。"); }
			return created;
		}
		finally { _gate.Release(); }
	}

	private static bool IsHostId(string value) =>
		value.StartsWith("urn:uuid:", StringComparison.Ordinal) && Guid.TryParseExact(value[9..], "D", out Guid id) &&
		string.Equals(value, $"urn:uuid:{id:D}", StringComparison.Ordinal);
}
