using System.Net;
using System.Text;
using System.Text.Json;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class ChatGptAuthTests
{
	[Theory]
	[InlineData("5", 5)]
	[InlineData("\"2\"", 2)]
	[InlineData("0", 1)]
	public async Task DeviceCodeAcceptsNumericOrStringInterval(string interval, int expected)
	{
		using HttpClient http = new(new Handler(async request =>
		{
			Assert.Equal("https://auth.openai.com/api/accounts/deviceauth/usercode", request.RequestUri!.AbsoluteUri);
			Assert.Contains(ChatGptAuth.ClientId, await request.Content!.ReadAsStringAsync());
			return Json($$"""{"device_auth_id":"device","user_code":"CODE","interval":{{interval}}}""");
		}));
		DeviceAuthorization device = await new ChatGptAuth(http).BeginLoginAsync(default);
		Assert.Equal("CODE", device.UserCode);
		Assert.Equal(TimeSpan.FromSeconds(expected), device.Interval);
	}

	[Theory]
	[InlineData("-1")]
	[InlineData("\"NaN\"")]
	[InlineData("901")]
	public async Task InvalidPollingIntervalIsRejected(string interval)
	{
		using HttpClient http = new(new Handler(_ => Task.FromResult(Json($$"""{"device_auth_id":"device","user_code":"CODE","interval":{{interval}}}"""))));
		await Assert.ThrowsAsync<InvalidOperationException>(() => new ChatGptAuth(http).BeginLoginAsync(default));
	}

	[Fact]
	public async Task LoginPollsThenExchangesPkceCodeAndSignOutClearsCredentials()
	{
		int polls = 0;
		using HttpClient http = new(new Handler(async request =>
		{
			if (request.RequestUri!.AbsolutePath.EndsWith("deviceauth/token"))
			{
				if (++polls == 1) return new HttpResponseMessage(HttpStatusCode.Forbidden);
				return Json("""{"authorization_code":"code","code_verifier":"verifier"}""");
			}
			string body = await request.Content!.ReadAsStringAsync();
			Assert.Contains("code_verifier=verifier", body);
			Assert.Contains("grant_type=authorization_code", body);
			Assert.Contains(Uri.EscapeDataString("https://auth.openai.com/deviceauth/callback"), body);
			return Token(3600);
		}));
		ChatGptAuth auth = new(http);
		await auth.CompleteLoginAsync(new DeviceAuthorization("device", "CODE", TimeSpan.Zero), default);
		Assert.True(auth.IsSignedIn);
		Assert.Equal("account", (await auth.GetCredentialAsync(default)).AccountId);
		Assert.Equal(2, polls);
		await auth.SignOutAsync();
		Assert.False(auth.IsSignedIn);
		await Assert.ThrowsAsync<InvalidOperationException>(() => auth.GetCredentialAsync(default));
	}

	[Fact]
	public async Task ConcurrentRequestsRefreshOnlyOnceAndKeepExistingRefreshTokenIfOmitted()
	{
		int refreshes = 0;
		using HttpClient http = new(new Handler(async request =>
		{
			if (request.RequestUri!.AbsolutePath.EndsWith("deviceauth/token")) return Json("""{"authorization_code":"code","code_verifier":"verifier"}""");
			string body = await request.Content!.ReadAsStringAsync();
			if (!body.Contains("grant_type=refresh_token")) return Token(1);
			Interlocked.Increment(ref refreshes);
			Assert.Contains("refresh_token=refresh-secret", body);
			return Token(3600, false);
		}));
		ChatGptAuth auth = new(http);
		await auth.CompleteLoginAsync(new DeviceAuthorization("device", "CODE", TimeSpan.Zero), default);
		ChatGptCredential[] credentials = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => auth.GetCredentialAsync(default)));
		Assert.Equal(1, refreshes);
		Assert.All(credentials, value => Assert.Equal("refresh-secret", value.RefreshToken));
	}

	[Fact]
	public async Task ExpiredStoredCredentialRestoresAndPersistsRotatedRefresh()
	{
		string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"account\"}}" )).TrimEnd('=').Replace('+','-').Replace('/','_');
		ChatGptCredential saved = new($"header.{payload}.sig", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-1), "account");
		MemoryStore store = new(saved);
		using HttpClient http = new(new Handler(async request => { Assert.Contains("refresh_token=old-refresh", await request.Content!.ReadAsStringAsync()); return Token(3600); }));
		ChatGptAuth first = new(http, store); await first.RestoreAsync(default);
		ChatGptCredential refreshed = await first.GetCredentialAsync(default);
		Assert.Equal("refresh-secret", refreshed.RefreshToken);
		Assert.Equal(refreshed, store.Value);
		ChatGptAuth next = new(http, store); await next.RestoreAsync(default); Assert.Equal(refreshed, await next.GetCredentialAsync(default));
	}

	[Fact]
	public async Task RotationSaveFailureKeepsNewTokenAndRetriesPersistenceBeforeAnyRefresh()
	{
		MemoryStore store = new(new ChatGptCredential(TokenValue(), "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-1), "account")) { FailNextSave = true };
		int refreshRequests = 0;
		using HttpClient http = new(new Handler(async request =>
		{
			refreshRequests++;
			Assert.Contains("refresh_token=old-refresh", await request.Content!.ReadAsStringAsync());
			return Token(3600, refresh: "rotated-refresh");
		}));
		ChatGptAuth auth = new(http, store); await auth.RestoreAsync(default);
		await Assert.ThrowsAsync<InvalidOperationException>(() => auth.GetCredentialAsync(default));
		Assert.Equal("rotated-refresh", Assert.IsType<ChatGptCredential>(store.PendingCredential).RefreshToken);
		ChatGptCredential current = await auth.GetCredentialAsync(default);
		Assert.Equal("rotated-refresh", current.RefreshToken);
		Assert.Equal(1, refreshRequests);
		Assert.Equal(current, store.Value);
	}

	[Fact]
	public async Task FailedSignOutDeleteRetainsSessionAndCanRetry()
	{
		MemoryStore store = new(new ChatGptCredential(TokenValue(), "refresh", DateTimeOffset.UtcNow.AddHours(1), "account")) { FailDeleteCount = 1 };
		using HttpClient http = new(new Handler(_ => throw new Xunit.Sdk.XunitException("Unexpected network request")));
		ChatGptAuth auth = new(http, store); await auth.RestoreAsync(default);
		await Assert.ThrowsAsync<InvalidOperationException>(() => auth.SignOutAsync());
		Assert.True(auth.IsSignedIn);
		await auth.SignOutAsync();
		Assert.False(auth.IsSignedIn);
		Assert.Null(store.Value);
	}

	[Fact]
	public async Task ClearSessionLeavesPersistedCredentialForNextInstance()
	{
		MemoryStore store = new(new ChatGptCredential(TokenValue(), "refresh", DateTimeOffset.UtcNow.AddHours(1), "account"));
		using HttpClient http = new(new Handler(_ => throw new Xunit.Sdk.XunitException("Unexpected network request")));
		ChatGptAuth active = new(http, store); await active.RestoreAsync(default); await active.ClearSessionAsync();
		Assert.False(active.IsSignedIn); Assert.NotNull(store.Value);
		ChatGptAuth reloaded = new(http, store); await reloaded.RestoreAsync(default); Assert.True(reloaded.IsSignedIn);
	}

	[Fact]
	public async Task RestoreFailureDoesNotLeakSecretsAndSuccessfulLoginClearsIssue()
	{
		const string secret = "access-secret-refresh-secret";
		MemoryStore store = new(null) { LoadException = new InvalidOperationException("damaged data " + secret) };
		int tokenPolls = 0;
		using HttpClient http = new(new Handler(request =>
		{
			if (request.RequestUri!.AbsolutePath.EndsWith("deviceauth/usercode")) return Task.FromResult(Json("{\"device_auth_id\":\"device\",\"user_code\":\"CODE\",\"interval\":0}"));
			if (request.RequestUri.AbsolutePath.EndsWith("deviceauth/token")) return Task.FromResult(Json("{\"authorization_code\":\"code\",\"code_verifier\":\"verifier\"}"));
			if (request.RequestUri.AbsolutePath == "/oauth/token") { tokenPolls++; return Task.FromResult(Token(3600)); }
			throw new Xunit.Sdk.XunitException("Unexpected request");
		}));
		ChatGptAuth auth = new(http, store); await auth.RestoreAsync(default);
		Assert.NotNull(auth.RestoreIssue); Assert.DoesNotContain(secret, auth.RestoreIssue, StringComparison.Ordinal);
		store.LoadException = null;
		DeviceAuthorization device = await auth.BeginLoginAsync(default);
		await auth.CompleteLoginAsync(device, default);
		Assert.Null(auth.RestoreIssue); Assert.True(auth.IsSignedIn); Assert.Equal(1, tokenPolls);
	}

	[Fact]
	public async Task CancelledPollingDoesNotStoreCredentials()
	{
		using HttpClient http = new(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
		ChatGptAuth auth = new(http);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.CompleteLoginAsync(new DeviceAuthorization("device", "CODE", TimeSpan.Zero), cancellation.Token));
		Assert.False(auth.IsSignedIn);
	}

	[Fact]
	public async Task CancellingActiveLoginReleasesCredentialGate()
	{
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using HttpClient http = new(new Handler(async _ =>
		{
			await Task.Yield();
			started.TrySetResult();
			return new HttpResponseMessage(HttpStatusCode.Forbidden);
		}));
		ChatGptAuth auth = new(http);
		using CancellationTokenSource cancellation = new();
		Task login = auth.CompleteLoginAsync(new DeviceAuthorization("device", "CODE", TimeSpan.Zero), cancellation.Token);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
		await auth.SignOutAsync().WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(auth.IsSignedIn);
	}

	[Fact]
	public async Task ErrorsNeverIncludeTokenResponseBody()
	{
		using HttpClient http = new(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("secret-token") })));
		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ChatGptAuth(http).BeginLoginAsync(default));
		Assert.DoesNotContain("secret-token", error.ToString());
		Assert.Contains("400", error.Message);
	}

	[Fact]
	public async Task OversizedAuthResponseIsRejected()
	{
		using HttpClient http = new(new Handler(_ => Task.FromResult(Json(new string('x', 1024 * 1024 + 1)))));
		await Assert.ThrowsAsync<HttpRequestException>(() => new ChatGptAuth(http).BeginLoginAsync(default));
	}

	[Theory]
	[InlineData("invalid")]
	[InlineData("a.@@.b")]
	[InlineData("a.e30.b")]
	public void MissingAccountMetadataIsRejectedWithoutEchoingToken(string token)
	{
		InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ChatGptAuth.AccountIdFromToken(token));
		Assert.DoesNotContain(token, error.Message);
	}

	private static string TokenValue()
	{
		string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"account\"}}" )).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		return $"header.{payload}.signature";
	}

	private static HttpResponseMessage Token(int seconds, bool includeRefresh = true) => Token(seconds, includeRefresh ? "refresh-secret" : null);

	private static HttpResponseMessage Token(int seconds, string? refresh)
	{
		string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"https://api.openai.com/auth":{"chatgpt_account_id":"account"}}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		return Json(JsonSerializer.Serialize(new { access_token = $"header.{payload}.signature", refresh_token = refresh, expires_in = seconds }));
	}

	private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

	private sealed class MemoryStore(ChatGptCredential? value) : ICredentialStore
	{
		internal ChatGptCredential? Value = value;
		internal ChatGptCredential? PendingCredential;
		internal bool FailNextSave;
		internal int FailDeleteCount;
		internal Exception? LoadException;
		public Task<ChatGptCredential?> LoadAsync(CancellationToken cancellationToken) => LoadException is null ? Task.FromResult(Value) : Task.FromException<ChatGptCredential?>(LoadException);
		public Task SaveAsync(ChatGptCredential credential, CancellationToken cancellationToken) { PendingCredential = credential; if (FailNextSave) { FailNextSave = false; return Task.FromException(new InvalidOperationException("save failed")); } Value = credential; return Task.CompletedTask; }
		public Task DeleteAsync(CancellationToken cancellationToken) { if (FailDeleteCount-- > 0) return Task.FromException(new InvalidOperationException("delete failed")); Value = null; return Task.CompletedTask; }
	}

	private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
	}
}
