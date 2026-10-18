using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class CredentialStoreTests
{
	[Fact]
	public async Task EncryptedStoreRoundTripsIndependentRegistrationsAndSignedOutMapping()
	{
		JsonNode? value = null;
		FakeProtection protection = new();
		CredentialStore store = new(_ => ValueTask.FromResult(value?.DeepClone()), (node, _) => { value = node.DeepClone(); return ValueTask.CompletedTask; }, _ => { value = null; return ValueTask.CompletedTask; }, protection);
		ChatGptCredential firstCredential = Credential("oaiapp-first", "subject-first");
		ChatGptRegistration first = new(firstCredential.ClientId, firstCredential.Subject, firstCredential.Email, firstCredential, firstCredential.Scopes);
		ChatGptRegistration signedOut = new("oaiapp-second", "subject-second", "same@example.com", null, "openid profile email");
		ChatGptAuthState state = new(first.ClientId, first.Subject, [first, signedOut]);

		await store.SaveAsync(state, default);
		Assert.DoesNotContain(firstCredential.AccessToken, value!.GetValue<string>(), StringComparison.Ordinal);
		CredentialStore anotherInstance = new(_ => ValueTask.FromResult(value?.DeepClone()), (node, _) => { value = node.DeepClone(); return ValueTask.CompletedTask; }, _ => { value = null; return ValueTask.CompletedTask; }, protection);
		ChatGptAuthState restored = Assert.IsType<ChatGptAuthState>(await anotherInstance.LoadAsync(default));
		Assert.Equal(state.ActiveClientId, restored.ActiveClientId);
		Assert.Equal(state.ActiveSubject, restored.ActiveSubject);
		Assert.Equal(JsonSerializer.Serialize(state), JsonSerializer.Serialize(restored));
		await anotherInstance.DeleteAsync(default);
		Assert.Null(value);
	}

	[Fact]
	public async Task LegacyCodexCredentialIsPreservedUntilExplicitClear()
	{
		JsonNode? value = null;
		FakeProtection protection = new();
		byte[] oldPlain = JsonSerializer.SerializeToUtf8Bytes(new
		{
			AccessToken = "opaque-old-token",
			RefreshToken = "old-refresh",
			ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
			AccountId = "legacy-account",
		});
		byte[] envelope = await protection.ProtectAsync(oldPlain, default);
		value = JsonValue.Create(Convert.ToBase64String(envelope));
		JsonNode? original = value.DeepClone();
		CredentialStore store = new(_ => ValueTask.FromResult(value?.DeepClone()), (node, _) => { value = node.DeepClone(); return ValueTask.CompletedTask; }, _ => { value = null; return ValueTask.CompletedTask; }, protection);
		ChatGptAuth auth = new(new HttpClient(), store, new MemoryHostIdStore());

		await auth.RestoreAsync(default);
		Assert.Contains("旧版 Codex", auth.RestoreIssue, StringComparison.Ordinal);
		Assert.True(auth.CanClearUnusableStoredData);
		Assert.Equal(original!.ToJsonString(), value!.ToJsonString());

		await auth.ClearUnusableStoredDataAsync(default);
		Assert.Null(value);
		Assert.False(auth.CanClearUnusableStoredData);
		Assert.Null(auth.RestoreIssue);
	}

	[Fact]
	public async Task StableHostIdIsCreatedOnceAndStoredSeparately()
	{
		string? saved = null;
		int writes = 0;
		HostIdStore store = new(
			_ => Task.FromResult(saved),
			(value, _) => { saved = value; writes++; return Task.CompletedTask; });
		string first = await store.GetOrCreateAsync(default);
		string second = await store.GetOrCreateAsync(default);
		Assert.Equal(first, second);
		Assert.StartsWith("urn:uuid:", first, StringComparison.Ordinal);
		Assert.True(Guid.TryParseExact(first[9..], "D", out _));
		Assert.Equal(1, writes);
	}

	[Fact]
	public async Task WindowsDpapiProtectsAndRejectsCorruptEnvelope()
	{
		if (!OperatingSystem.IsWindows()) return;
		PlatformCredentialProtection protection = new();
		byte[] plain = Encoding.UTF8.GetBytes("credential-test");
		byte[] encrypted = await protection.ProtectAsync(plain, default);
		Assert.Equal(plain, await protection.UnprotectAsync(encrypted, default));
		encrypted[encrypted.Length / 2] ^= 0x01;
		await Assert.ThrowsAnyAsync<Exception>(() => protection.UnprotectAsync(encrypted, default));
	}

	private static ChatGptCredential Credential(string clientId, string subject)
	{
		return new ChatGptCredential("opaque-access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), subject,
			clientId, subject, "verified-id-token", "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", "same@example.com", DateTimeOffset.UtcNow.AddMinutes(-1));
	}

	private sealed class FakeProtection : ICredentialProtection
	{
		public Task<byte[]> ProtectAsync(byte[] plain, CancellationToken cancellationToken) => Task.FromResult(Convert.ToBase64String(plain).Select(character => (byte)character).ToArray());
		public Task<byte[]> UnprotectAsync(byte[] envelope, CancellationToken cancellationToken) => Task.FromResult(Convert.FromBase64String(Encoding.UTF8.GetString(envelope)));
		public Task DeleteKeyAsync(byte[] envelope, CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
