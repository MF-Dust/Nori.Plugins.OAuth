using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nori.Plugins.ChatGpt;

internal interface ICredentialStore
{
	Task<ChatGptCredential?> LoadAsync(CancellationToken cancellationToken);
	Task SaveAsync(ChatGptCredential credential, CancellationToken cancellationToken);
	Task DeleteAsync(CancellationToken cancellationToken);
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

	public async Task<ChatGptCredential?> LoadAsync(CancellationToken cancellationToken)
	{
		try
		{
			JsonNode? value = await read(cancellationToken);
			if (value is null) return null;
			string encoded = value.GetValue<string>();
			if (encoded.Length > 3 * 1024 * 1024) throw new FormatException();
			_envelope = Convert.FromBase64String(encoded);
			if (_envelope.Length > 2 * 1024 * 1024) throw new FormatException();
			byte[] plain = await protection.UnprotectAsync(_envelope, cancellationToken);
			try
			{
				ChatGptCredential credential = JsonSerializer.Deserialize<ChatGptCredential>(plain) ?? throw new JsonException();
				if (string.IsNullOrWhiteSpace(credential.AccessToken) || string.IsNullOrWhiteSpace(credential.RefreshToken) ||
					string.IsNullOrWhiteSpace(credential.AccountId) || credential.ExpiresAt == default ||
					ChatGptAuth.AccountIdFromToken(credential.AccessToken) != credential.AccountId) throw new JsonException();
				return credential;
			}
			finally { CryptographicOperations.ZeroMemory(plain); }
		}
		catch (OperationCanceledException) { throw; }
		catch { throw new InvalidOperationException("无法恢复 ChatGPT 登录数据：密文损坏或系统密钥库不可用。请重新登录，或退出登录以清除保存的数据。"); }
	}

	public async Task SaveAsync(ChatGptCredential credential, CancellationToken cancellationToken)
	{
		byte[] plain = JsonSerializer.SerializeToUtf8Bytes(credential);
		try
		{
			byte[] envelope = await protection.ProtectAsync(plain, cancellationToken);
			await write(JsonValue.Create(Convert.ToBase64String(envelope))!, cancellationToken);
			_envelope = envelope;
		}
		catch (OperationCanceledException) { throw; }
		catch { throw new InvalidOperationException("无法安全保存 ChatGPT 登录数据，请检查插件数据目录权限及系统密钥库是否可用。Linux 需要安装 secret-tool 并解锁密钥环。"); }
		finally { CryptographicOperations.ZeroMemory(plain); }
	}

	public async Task DeleteAsync(CancellationToken cancellationToken)
	{
		try { await delete(cancellationToken); }
		catch (OperationCanceledException) { throw; }
		catch { throw new InvalidOperationException("无法删除 ChatGPT 登录数据，请检查插件数据目录权限后重试。"); }
		byte[]? envelope = _envelope;
		_envelope = null;
		if (envelope is not null)
		{
			// 密文已删除；清理不可用的密钥环条目失败，不应让已完成的退出变成失败。
			try { await protection.DeleteKeyAsync(envelope, CancellationToken.None); }
			catch (Exception) { }
		}
	}
}
