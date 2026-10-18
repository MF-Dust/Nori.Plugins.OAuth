using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class CredentialStoreTests
{
	[Fact]
	public async Task EncryptedStoreRoundTripsAndDeletes()
	{
		JsonNode? value = null; FakeProtection protection = new();
		CredentialStore store = new(_ => ValueTask.FromResult(value?.DeepClone()), (node, _) => { value = node.DeepClone(); return ValueTask.CompletedTask; }, _ => { value = null; return ValueTask.CompletedTask; }, protection);
		ChatGptCredential credential = Credential(); await store.SaveAsync(credential, default);
		Assert.NotEqual(credential.AccessToken, value!.GetValue<string>());
		CredentialStore anotherInstance = new(_ => ValueTask.FromResult(value?.DeepClone()), (node, _) => { value = node.DeepClone(); return ValueTask.CompletedTask; }, _ => { value = null; return ValueTask.CompletedTask; }, protection);
		Assert.Equal(credential, await anotherInstance.LoadAsync(default)); await anotherInstance.DeleteAsync(default); Assert.Null(value);
	}

	[Fact]
	public async Task WindowsDpapiProtectsAndRejectsCorruptEnvelope()
	{
		if (!OperatingSystem.IsWindows()) return;
		PlatformCredentialProtection protection = new(); byte[] plain = Encoding.UTF8.GetBytes("credential-test"); byte[] encrypted = await protection.ProtectAsync(plain, default);
		Assert.Equal(plain, await protection.UnprotectAsync(encrypted, default));
		encrypted[encrypted.Length / 2] ^= 0x01;
		await Assert.ThrowsAnyAsync<Exception>(() => protection.UnprotectAsync(encrypted, default));
	}

	private static ChatGptCredential Credential()
	{
		string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"account\"}}" )).TrimEnd('=').Replace('+','-').Replace('/','_');
		return new ChatGptCredential($"header.{payload}.sig", "refresh", DateTimeOffset.UtcNow.AddHours(1), "account");
	}
	private sealed class FakeProtection : ICredentialProtection
	{
		public Task<byte[]> ProtectAsync(byte[] plain, CancellationToken cancellationToken) => Task.FromResult(Convert.ToBase64String(plain).Select(c => (byte)c).ToArray());
		public Task<byte[]> UnprotectAsync(byte[] envelope, CancellationToken cancellationToken) => Task.FromResult(Convert.FromBase64String(Encoding.UTF8.GetString(envelope)));
		public Task DeleteKeyAsync(byte[] envelope, CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
