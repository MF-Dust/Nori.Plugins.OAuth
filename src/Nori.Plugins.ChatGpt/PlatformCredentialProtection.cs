using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nori.Plugins.ChatGpt;

internal sealed class PlatformCredentialProtection : ICredentialProtection
{
	private sealed record Envelope(int Version, string Scheme, string Payload, string? KeyId = null);
	private string? _keyId;

	// DPAPI 和钥匙串调用可能阻塞，不占用 Avalonia UI 线程。
	public Task<byte[]> ProtectAsync(byte[] plain, CancellationToken ct) => Task.Run(() => ProtectCoreAsync(plain, ct), ct);
	public Task<byte[]> UnprotectAsync(byte[] bytes, CancellationToken ct) => Task.Run(() => UnprotectCoreAsync(bytes, ct), ct);
	public Task DeleteKeyAsync(byte[] bytes, CancellationToken ct) => Task.Run(() => DeleteKeyCoreAsync(bytes, ct), ct);

	private async Task<byte[]> ProtectCoreAsync(byte[] plain, CancellationToken ct)
	{
		if (plain.Length > 1024 * 1024) throw new InvalidOperationException("登录数据过大。");
		if (OperatingSystem.IsWindows()) return JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, "dpapi-current-user", Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser))));
		string id = _keyId ?? Guid.NewGuid().ToString("N");
		byte[] key = await GetKeyAsync(id, _keyId is null, ct);
		_keyId = id;
		try { byte[] nonce = RandomNumberGenerator.GetBytes(12), cipher = new byte[plain.Length], tag = new byte[16]; using AesGcm aes = new(key, 16); aes.Encrypt(nonce, plain, cipher, tag); return JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, OperatingSystem.IsMacOS() ? "mac-aes256gcm" : "linux-aes256gcm", Convert.ToBase64String([.. nonce, .. cipher, .. tag]), id)); }
		finally { CryptographicOperations.ZeroMemory(key); }
	}
	private async Task<byte[]> UnprotectCoreAsync(byte[] bytes, CancellationToken ct)
	{
		if (bytes.Length > 2 * 1024 * 1024) throw new InvalidOperationException(); Envelope e = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new InvalidOperationException(); if (e.Version != 1) throw new InvalidOperationException(); byte[] p = Convert.FromBase64String(e.Payload);
		if (e.Scheme == "dpapi-current-user" && OperatingSystem.IsWindows()) return ProtectedData.Unprotect(p, null, DataProtectionScope.CurrentUser);
		if (e.Scheme != (OperatingSystem.IsMacOS() ? "mac-aes256gcm" : "linux-aes256gcm") || p.Length < 28 || e.KeyId is null) throw new InvalidOperationException();
		if (!Guid.TryParseExact(e.KeyId, "N", out _)) throw new InvalidOperationException();
		byte[] key = await GetKeyAsync(e.KeyId, false, ct); try { byte[] plain = new byte[p.Length - 28]; using AesGcm aes = new(key, 16); aes.Decrypt(p.AsSpan(0, 12), p.AsSpan(12, plain.Length), p.AsSpan(12 + plain.Length, 16), plain); _keyId = e.KeyId; return plain; } finally { CryptographicOperations.ZeroMemory(key); }
	}
	private async Task DeleteKeyCoreAsync(byte[] bytes, CancellationToken ct)
	{
		if (OperatingSystem.IsWindows()) return; Envelope e = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new InvalidOperationException(); if (e.KeyId is null) return;
		if (!Guid.TryParseExact(e.KeyId, "N", out _)) throw new InvalidOperationException();
		_keyId = null;
		if (OperatingSystem.IsMacOS()) { Mac.Delete(e.KeyId); return; }
		await RunSecretToolAsync(["clear", "application", "nori-chatgpt-oauth", "key", e.KeyId], null, ct, lookup: false);
	}
	private static async Task<byte[]> GetKeyAsync(string id, bool create, CancellationToken ct)
	{
		if (OperatingSystem.IsMacOS()) return Mac.Get(id, create);
		byte[] found = await RunSecretToolAsync(["lookup", "application", "nori-chatgpt-oauth", "key", id], null, ct, lookup: true);
		if (found.Length > 0) { try { byte[] decoded = Convert.FromBase64String(Encoding.UTF8.GetString(found).Trim()); if (decoded.Length == 32) return decoded; } catch (FormatException) { } throw new InvalidOperationException("系统密钥库中的密钥不可用。"); }
		if (!create) throw new InvalidOperationException("系统密钥库中的密钥不存在。"); byte[] key = RandomNumberGenerator.GetBytes(32);
		await RunSecretToolAsync(["store", "--label=Nori ChatGPT OAuth", "application", "nori-chatgpt-oauth", "key", id], Encoding.UTF8.GetBytes(Convert.ToBase64String(key)), ct, lookup: false); return key;
	}
	private static async Task<byte[]> RunSecretToolAsync(string[] args, byte[]? input, CancellationToken ct, bool lookup)
	{
		using Process p = new() { StartInfo = new ProcessStartInfo("/usr/bin/secret-tool") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } }; foreach (string arg in args) p.StartInfo.ArgumentList.Add(arg);
		try
		{
			p.Start(); using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
			Task<byte[]> stdout = ReadBoundedAsync(p.StandardOutput.BaseStream, 4096, timeout.Token); Task<byte[]> stderr = ReadBoundedAsync(p.StandardError.BaseStream, 4096, timeout.Token);
			if (input is not null) { await p.StandardInput.BaseStream.WriteAsync(input, timeout.Token); await p.StandardInput.WriteAsync("\n".AsMemory(), timeout.Token); } p.StandardInput.Close(); await p.WaitForExitAsync(timeout.Token); _ = await stderr;
			byte[] output = await stdout; if (p.ExitCode != 0) { if (lookup && p.ExitCode == 1 && (await stderr).Length == 0) return []; throw new InvalidOperationException(); } return output;
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested) { try { if (!p.HasExited) p.Kill(true); } catch { } throw; }
		catch { try { if (!p.HasExited) p.Kill(true); } catch { } throw new InvalidOperationException("Linux 系统密钥环不可用；请安装 secret-tool 并解锁密钥环。"); }
	}
	private static async Task<byte[]> ReadBoundedAsync(Stream stream, int max, CancellationToken ct) { using MemoryStream m = new(); byte[] buffer = new byte[512]; int n; while ((n = await stream.ReadAsync(buffer, ct)) > 0) { if (m.Length + n > max) throw new InvalidOperationException(); await m.WriteAsync(buffer.AsMemory(0, n), ct); } return m.ToArray(); }
	private static class Mac
	{
		private const int NotFound = -25300; private static readonly byte[] Service = Encoding.UTF8.GetBytes("Nori ChatGPT OAuth");
		[DllImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint="SecKeychainFindGenericPassword")] private static extern int Find(nint k, uint sl, byte[] s, uint al, byte[] a, out uint dl, out nint data, out nint item);
		[DllImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint="SecKeychainAddGenericPassword")] private static extern int Add(nint k, uint sl, byte[] s, uint al, byte[] a, uint pl, byte[] pass, out nint item);
		[DllImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint="SecKeychainItemFreeContent")] private static extern int Free(nint attr, nint data);
		[DllImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint="SecKeychainItemDelete")] private static extern int DeleteItem(nint item);
		[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint="CFRelease")] private static extern void Release(nint value);
		internal static byte[] Get(string id, bool create) { byte[] account = Encoding.UTF8.GetBytes(id); int status = Find(0, (uint)Service.Length, Service, (uint)account.Length, account, out uint len, out nint data, out nint item); if (status == 0) { try { byte[] b = new byte[len]; Marshal.Copy(data,b,0,(int)len); return b; } finally { Free(0,data); if (item != 0) Release(item); } } if (status != NotFound || !create) throw new InvalidOperationException("macOS 钥匙串中的密钥不可用。"); byte[] key = RandomNumberGenerator.GetBytes(32); int added = Add(0,(uint)Service.Length,Service,(uint)account.Length,account,32,key,out nint addedItem); if (addedItem != 0) Release(addedItem); if (added != 0) throw new InvalidOperationException("macOS 钥匙串不可用。"); return key; }
		internal static void Delete(string id) { byte[] account = Encoding.UTF8.GetBytes(id); int status = Find(0,(uint)Service.Length,Service,(uint)account.Length,account,out uint len,out nint data,out nint item); if (status == NotFound) return; if (status != 0) throw new InvalidOperationException("macOS 钥匙串不可用。"); Free(0,data); try { if (DeleteItem(item) != 0) throw new InvalidOperationException("macOS 钥匙串不可用。"); } finally { if(item != 0) Release(item); } }
	}
}
