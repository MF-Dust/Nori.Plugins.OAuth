using System.Net;
using System.Text.Json;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class ChatGptAuthTests
{
	private const string IssuedClientId = "oaiapp-issued-1";

	[Fact]
	public async Task NewRegistrationUsesLoopbackPkceAndOnlySavesAfterSignedIdentityValidation()
	{
		using TestIdentityKey signingKey = new();
		MemoryCredentialStore store = new();
		MemoryHostIdStore hostIdStore = new();
		Uri? authorizationUri = null;
		using HttpClient http = new(new TestHttpHandler(async request =>
		{
			if (request.RequestUri!.AbsoluteUri == OpenAiOAuthProtocol.JwksEndpoint) return OAuthTestHelpers.Json(signingKey.Jwks);
			Assert.Equal(OpenAiOAuthProtocol.TokenEndpoint, request.RequestUri.AbsoluteUri);
			Dictionary<string, string> form = OAuthTestHelpers.ParseForm(await request.Content!.ReadAsStringAsync());
			Dictionary<string, string> query = OAuthTestHelpers.ParseQuery(authorizationUri!.Query);
			Assert.Equal("authorization_code", form["grant_type"]);
			Assert.Equal(IssuedClientId, form["client_id"]);
			Assert.NotEqual(OpenAiOAuthProtocol.DynamicClientId, form["client_id"]);
			Assert.Equal(query["redirect_uri"], form["redirect_uri"]);
			Assert.StartsWith("http://127.0.0.1:", form["redirect_uri"], StringComparison.Ordinal);
			Assert.DoesNotContain("localhost", form["redirect_uri"], StringComparison.OrdinalIgnoreCase);
			Assert.Equal(query["code_challenge"], OpenAiOAuthProtocol.CreateChallenge(form["code_verifier"]));
			Assert.Equal(OpenAiOAuthProtocol.Resource, form["resource"]);
			return OAuthTestHelpers.TokenResponse(signingKey.CreateIdToken(form["client_id"], query["nonce"]));
		}));
		ChatGptAuth auth = new(http, store, hostIdStore);
		(Task signIn, Uri uri) = await StartLoginAsync(auth, value => authorizationUri = value);

		Dictionary<string, string> parameters = OAuthTestHelpers.ParseQuery(uri.Query);
		Assert.Equal(OpenAiOAuthProtocol.DynamicClientId, parameters["client_id"]);
		Assert.Equal(OpenAiOAuthProtocol.AgentName, parameters["agent_name_hint"]);
		Assert.Equal(hostIdStore.Value, parameters["ext_agent_host_id"]);
		Assert.Equal(OpenAiOAuthProtocol.RequiredScopes, parameters["scope"]);
		Assert.Equal(OpenAiOAuthProtocol.Resource, parameters["resource"]);
		Assert.Equal("S256", parameters["code_challenge_method"]);
		Assert.StartsWith("http://127.0.0.1:", parameters["redirect_uri"], StringComparison.Ordinal);
		Assert.EndsWith(OpenAiOAuthProtocol.CallbackPath, new Uri(parameters["redirect_uri"]).AbsolutePath, StringComparison.Ordinal);
		Assert.DoesNotContain("localhost", parameters["redirect_uri"], StringComparison.OrdinalIgnoreCase);

		Assert.Equal(200, await OAuthTestHelpers.SendCallbackAsync(new Uri(parameters["redirect_uri"]), OAuthTestHelpers.CallbackQuery(uri, IssuedClientId)));
		await signIn.WaitAsync(TimeSpan.FromSeconds(5));

		ChatGptCredential credential = await auth.GetCredentialAsync(default);
		Assert.True(auth.IsSignedIn);
		Assert.True(auth.CanUseChatGptPlan);
		Assert.Equal("subject-1", credential.AccountId);
		Assert.Equal("subject-1", credential.Subject);
		Assert.Equal(IssuedClientId, credential.ClientId);
		Assert.Equal("opaque-access-token", credential.AccessToken);
		Assert.Equal("refresh-token-1", credential.RefreshToken);
		Assert.Equal("user@example.com", credential.Email);
		Assert.Contains(OpenAiOAuthProtocol.DirectScope, credential.Scopes, StringComparison.Ordinal);
		Assert.NotEqual(default, credential.EarliestRefreshAt);
		Assert.Equal(IssuedClientId, store.Value!.ActiveClientId);
		Assert.Equal("subject-1", Assert.Single(store.Value.Registrations).Subject);
		Assert.Equal(hostIdStore.Value, await hostIdStore.GetOrCreateAsync(default));
	}

	[Fact]
	public async Task WrongStateAndDuplicateCallbackParametersAreRejectedWithoutEndingAttempt()
	{
		using TestIdentityKey signingKey = new();
		Uri? authorizationUri = null;
		int exchanges = 0;
		using HttpClient http = new(new TestHttpHandler(async request =>
		{
			if (request.RequestUri!.AbsoluteUri == OpenAiOAuthProtocol.JwksEndpoint) return OAuthTestHelpers.Json(signingKey.Jwks);
			exchanges++;
			Dictionary<string, string> form = OAuthTestHelpers.ParseForm(await request.Content!.ReadAsStringAsync());
			string nonce = OAuthTestHelpers.ParseQuery(authorizationUri!.Query)["nonce"];
			return OAuthTestHelpers.TokenResponse(signingKey.CreateIdToken(form["client_id"], nonce));
		}));
		ChatGptAuth auth = new(http);
		(Task signIn, Uri uri) = await StartLoginAsync(auth, value => authorizationUri = value);
		string valid = OAuthTestHelpers.CallbackQuery(uri);
		string wrongState = valid.Replace(OAuthTestHelpers.ParseQuery(uri.Query)["state"], "wrong-state", StringComparison.Ordinal);
		Assert.Equal(400, await OAuthTestHelpers.SendCallbackAsync(new Uri(OAuthTestHelpers.ParseQuery(uri.Query)["redirect_uri"]), wrongState));
		Assert.False(signIn.IsCompleted);
		Assert.Equal(400, await OAuthTestHelpers.SendCallbackAsync(new Uri(OAuthTestHelpers.ParseQuery(uri.Query)["redirect_uri"]), valid + "&state=duplicate"));
		Assert.False(signIn.IsCompleted);
		Assert.Equal(200, await OAuthTestHelpers.SendCallbackAsync(new Uri(OAuthTestHelpers.ParseQuery(uri.Query)["redirect_uri"]), valid));
		await signIn.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(1, exchanges);
	}

	[Theory]
	[InlineData("nonce")]
	[InlineData("signature")]
	[InlineData("expired")]
	[InlineData("issuer")]
	[InlineData("audience")]
	public async Task InvalidIdTokenIsRejectedAndNeverPersisted(string invalidClaim)
	{
		using TestIdentityKey signingKey = new();
		MemoryCredentialStore store = new();
		Uri? authorizationUri = null;
		using HttpClient http = new(new TestHttpHandler(async request =>
		{
			if (request.RequestUri!.AbsoluteUri == OpenAiOAuthProtocol.JwksEndpoint) return OAuthTestHelpers.Json(signingKey.Jwks);
			Dictionary<string, string> form = OAuthTestHelpers.ParseForm(await request.Content!.ReadAsStringAsync());
			string nonce = OAuthTestHelpers.ParseQuery(authorizationUri!.Query)["nonce"];
			string token = invalidClaim switch
			{
				"nonce" => signingKey.CreateIdToken(form["client_id"], "different-nonce"),
				"signature" => signingKey.CreateInvalidSignature(form["client_id"], nonce),
				"expired" => signingKey.CreateIdToken(form["client_id"], nonce, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5)),
				"issuer" => signingKey.CreateIdToken(form["client_id"], nonce, issuer: "https://attacker.invalid"),
				"audience" => signingKey.CreateIdToken(form["client_id"], nonce, audience: "another-client"),
				_ => throw new InvalidOperationException(),
			};
			return OAuthTestHelpers.TokenResponse(token);
		}));
		ChatGptAuth auth = new(http, store);
		(Task signIn, Uri uri) = await StartLoginAsync(auth, value => authorizationUri = value);
		Assert.Equal(200, await OAuthTestHelpers.SendCallbackAsync(new Uri(OAuthTestHelpers.ParseQuery(uri.Query)["redirect_uri"]), OAuthTestHelpers.CallbackQuery(uri)));
		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => signIn);
		Assert.Contains("验证失败", error.Message, StringComparison.Ordinal);
		Assert.False(auth.IsSignedIn);
		Assert.Null(store.Value);
	}

	[Fact]
	public async Task ReauthorizationUsesSavedIssuedClientAndRejectsDifferentCallbackClient()
	{
		ChatGptAuthState original = StateWith(StoredCredential(IssuedClientId, "subject-1"));
		MemoryCredentialStore store = new(original);
		using HttpClient http = new(new TestHttpHandler(_ => throw new Xunit.Sdk.XunitException("Rejected callback must not exchange a code")));
		ChatGptAuth auth = new(http, store, new MemoryHostIdStore());
		await auth.RestoreAsync(default);
		(Task signIn, Uri uri) = await StartLoginAsync(auth, target: auth.ActiveAccountOption);
		Dictionary<string, string> query = OAuthTestHelpers.ParseQuery(uri.Query);
		Assert.Equal(IssuedClientId, query["client_id"]);
		Assert.DoesNotContain("agent_name_hint", query.Keys);
		Assert.Equal("verified-id-token", query["id_token_hint"]);
		string callback = OAuthTestHelpers.CallbackQuery(uri, "oaiapp-other");
		Assert.Equal(400, await OAuthTestHelpers.SendCallbackAsync(new Uri(query["redirect_uri"]), callback));
		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => signIn);
		Assert.Contains("client_id", error.Message, StringComparison.Ordinal);
		Assert.Equal(original, store.Value);
		Assert.Equal("subject-1", (await auth.GetCredentialAsync(default)).AccountId);
	}

	[Fact]
	public async Task NewRegistrationWithoutIssuedClientIdDoesNotExchangeCode()
	{
		using HttpClient http = new(new TestHttpHandler(_ => throw new Xunit.Sdk.XunitException("Missing issued client ID must not exchange a code")));
		ChatGptAuth auth = new(http);
		(Task signIn, Uri uri) = await StartLoginAsync(auth);
		Dictionary<string, string> query = OAuthTestHelpers.ParseQuery(uri.Query);
		string callback = OAuthTestHelpers.CallbackQuery(uri, clientId: null);
		Assert.Equal(400, await OAuthTestHelpers.SendCallbackAsync(new Uri(query["redirect_uri"]), callback));
		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => signIn);
		Assert.Contains("client_id", error.Message, StringComparison.Ordinal);
		Assert.False(auth.IsSignedIn);
	}

	[Fact]
	public async Task InvalidGrantStartsFreshAuthorizationUsingIssuedClientId()
	{
		using TestIdentityKey signingKey = new();
		Uri? currentAuthorizationUri = null;
		TaskCompletionSource<Uri> firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<Uri> secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int authorizations = 0;
		List<string> exchangedClients = [];
		using HttpClient http = new(new TestHttpHandler(async request =>
		{
			if (request.RequestUri!.AbsoluteUri == OpenAiOAuthProtocol.JwksEndpoint) return OAuthTestHelpers.Json(signingKey.Jwks);
			Dictionary<string, string> form = OAuthTestHelpers.ParseForm(await request.Content!.ReadAsStringAsync());
			exchangedClients.Add(form["client_id"]);
			if (exchangedClients.Count == 1) return OAuthTestHelpers.Json("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest);
			string nonce = OAuthTestHelpers.ParseQuery(currentAuthorizationUri!.Query)["nonce"];
			return OAuthTestHelpers.TokenResponse(signingKey.CreateIdToken(form["client_id"], nonce));
		}));
		ChatGptAuth auth = new(http);
		Task signIn = auth.SignInAsync(null, uri =>
		{
			currentAuthorizationUri = uri;
			if (Interlocked.Increment(ref authorizations) == 1) firstStarted.TrySetResult(uri);
			else secondStarted.TrySetResult(uri);
		}, default);

		Uri first = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(200, await OAuthTestHelpers.SendCallbackAsync(new Uri(OAuthTestHelpers.ParseQuery(first.Query)["redirect_uri"]), OAuthTestHelpers.CallbackQuery(first, IssuedClientId)));
		Uri second = await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Dictionary<string, string> secondQuery = OAuthTestHelpers.ParseQuery(second.Query);
		Assert.Equal(IssuedClientId, secondQuery["client_id"]);
		Assert.DoesNotContain("agent_name_hint", secondQuery.Keys);
		Assert.Equal(200, await OAuthTestHelpers.SendCallbackAsync(new Uri(secondQuery["redirect_uri"]), OAuthTestHelpers.CallbackQuery(second, clientId: null)));
		await signIn.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(new[] { IssuedClientId, IssuedClientId }, exchangedClients);
	}

	[Fact]
	public async Task MissingUsageScopeBlocksCredentialAndConsentIsRequestedForSavedRegistration()
	{
		ChatGptCredential credential = StoredCredential(IssuedClientId, "subject-1", scopes: "openid profile email offline_access");
		using HttpClient noNetwork = new(new TestHttpHandler(_ => throw new Xunit.Sdk.XunitException("Missing scopes must block before network use")));
		ChatGptAuth auth = new(noNetwork, new MemoryCredentialStore(StateWith(credential)));
		await auth.RestoreAsync(default);
		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.GetCredentialAsync(default));
		Assert.Contains(OpenAiOAuthProtocol.DirectScope, error.Message, StringComparison.Ordinal);

		ChatGptRegistration denied = new(IssuedClientId, "subject-1", "user@example.com", credential, credential.Scopes);
		Uri retry = OpenAiOAuthProtocol.CreateAuthorizationUri(IssuedClientId, "urn:uuid:00000000-0000-4000-8000-000000000001", new Uri("http://127.0.0.1:12345/auth/callback"), "state", "nonce", "challenge", denied);
		Dictionary<string, string> fields = OAuthTestHelpers.ParseQuery(retry.Query);
		Assert.Equal("consent", fields["prompt"]);
		Assert.Equal(OpenAiOAuthProtocol.RequiredScopes, fields["scope"]);
		Assert.DoesNotContain("force_reconsent", fields.Keys);
	}

	[Fact]
	public async Task RefreshRotationSaveFailureKeepsNewRefreshTokenAndRetriesPersistenceFirst()
	{
		ChatGptCredential saved = StoredCredential(IssuedClientId, "subject-1", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), earliestRefreshAt: DateTimeOffset.UtcNow.AddMinutes(-2));
		MemoryCredentialStore store = new(StateWith(saved)) { FailNextSaveCount = 1 };
		int refreshes = 0;
		using HttpClient http = new(new TestHttpHandler(async request =>
		{
			Assert.Equal(OpenAiOAuthProtocol.TokenEndpoint, request.RequestUri!.AbsoluteUri);
			Dictionary<string, string> form = OAuthTestHelpers.ParseForm(await request.Content!.ReadAsStringAsync());
			Assert.Equal("refresh_token", form["grant_type"]);
			Assert.Equal(IssuedClientId, form["client_id"]);
			Assert.Equal(saved.RefreshToken, form["refresh_token"]);
			Assert.Equal(OpenAiOAuthProtocol.Resource, form["resource"]);
			refreshes++;
			return OAuthTestHelpers.TokenResponse("refreshed-id-token", accessToken: "opaque-access-token-2", refreshToken: "refresh-token-2", earliestRefreshAt: DateTimeOffset.UtcNow.AddMinutes(5));
		}));
		ChatGptAuth auth = new(http, store);
		await auth.RestoreAsync(default);
		await Assert.ThrowsAsync<InvalidOperationException>(() => auth.GetCredentialAsync(default));
		Assert.Equal("refresh-token-2", store.PendingValue!.Registrations.Single().Credential!.RefreshToken);
		Assert.True(auth.IsSignedIn);

		ChatGptCredential retried = await auth.GetCredentialAsync(default);
		Assert.Equal("opaque-access-token-2", retried.AccessToken);
		Assert.Equal("refresh-token-2", retried.RefreshToken);
		Assert.Equal(1, refreshes);
		Assert.Equal(retried, store.Value!.Registrations.Single().Credential);
	}

	[Fact]
	public async Task EarliestRefreshAtPreventsPrematureRefreshAndExpiredRefreshInvalidatesOnlyTerminalGrant()
	{
		ChatGptCredential nearExpiry = StoredCredential(IssuedClientId, "subject-1", expiresAt: DateTimeOffset.UtcNow.AddMinutes(2), earliestRefreshAt: DateTimeOffset.UtcNow.AddMinutes(5));
		MemoryCredentialStore futureStore = new(StateWith(nearExpiry));
		using HttpClient noRefresh = new(new TestHttpHandler(_ => throw new Xunit.Sdk.XunitException("Refresh must wait for earliest_refresh_at")));
		ChatGptAuth futureAuth = new(noRefresh, futureStore);
		await futureAuth.RestoreAsync(default);
		Assert.Equal(nearExpiry, await futureAuth.GetCredentialAsync(default));

		ChatGptCredential expired = StoredCredential(IssuedClientId, "subject-1", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), earliestRefreshAt: DateTimeOffset.UtcNow.AddMinutes(-2));
		MemoryCredentialStore expiredStore = new(StateWith(expired));
		using HttpClient invalidGrant = new(new TestHttpHandler(_ => Task.FromResult(OAuthTestHelpers.Json("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest))));
		ChatGptAuth invalidAuth = new(invalidGrant, expiredStore);
		await invalidAuth.RestoreAsync(default);
		await Assert.ThrowsAsync<InvalidOperationException>(() => invalidAuth.GetCredentialAsync(default));
		ChatGptRegistration registration = Assert.Single(expiredStore.Value!.Registrations);
		Assert.Null(registration.Credential);
		Assert.Equal(IssuedClientId, registration.ClientId);
		Assert.False(invalidAuth.IsSignedIn);

		MemoryCredentialStore temporaryStore = new(StateWith(expired));
		using HttpClient unavailable = new(new TestHttpHandler(_ => Task.FromResult(OAuthTestHelpers.Json("{\"error\":\"temporarily_unavailable\"}", HttpStatusCode.ServiceUnavailable))));
		ChatGptAuth temporaryAuth = new(unavailable, temporaryStore);
		await temporaryAuth.RestoreAsync(default);
		await Assert.ThrowsAsync<OAuthTokenException>(() => temporaryAuth.GetCredentialAsync(default));
		Assert.Equal(expired.RefreshToken, temporaryStore.Value!.Registrations.Single().Credential!.RefreshToken);
	}

	[Fact]
	public async Task SignOutSaveRetryPreservesUnconfirmedRevocationAndDoesNotRepeatRevoke()
	{
		ChatGptCredential saved = StoredCredential(IssuedClientId, "subject-1");
		MemoryCredentialStore store = new(StateWith(saved)) { FailNextSaveCount = 1 };
		MemoryHostIdStore hostIdStore = new();
		int revocations = 0;
		using HttpClient http = new(new TestHttpHandler(async request =>
		{
			Assert.Equal(OpenAiOAuthProtocol.RevocationEndpoint, request.RequestUri!.AbsoluteUri);
			Dictionary<string, string> form = OAuthTestHelpers.ParseForm(await request.Content!.ReadAsStringAsync());
			Assert.Equal(saved.RefreshToken, form["token"]);
			Assert.Equal("refresh_token", form["token_type_hint"]);
			Assert.Equal(IssuedClientId, form["client_id"]);
			revocations++;
			return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
		}));
		ChatGptAuth auth = new(http, store, hostIdStore);
		await auth.RestoreAsync(default);
		await Assert.ThrowsAsync<InvalidOperationException>(() => auth.SignOutAsync());
		Assert.False(auth.IsSignedIn);
		Assert.True(auth.CanRetryPersistence);
		ChatGptSignOutResult retried = await auth.SignOutAsync();
		Assert.False(retried.RemoteRevocationConfirmed);
		Assert.Equal(1, revocations);
		Assert.Null(store.Value!.Registrations.Single().Credential);
		Assert.Equal(IssuedClientId, store.Value.Registrations.Single().ClientId);
		Assert.Equal(hostIdStore.Value, await hostIdStore.GetOrCreateAsync(default));
		Assert.Equal(0, store.DeleteCalls);
	}

	[Fact]
	public async Task SignInSaveFailureDoesNotReplaceActiveAccount()
	{
		ChatGptCredential existing = StoredCredential("oaiapp-old", "old-subject");
		MemoryCredentialStore store = new(StateWith(existing)) { FailNextSaveCount = 1 };
		using TestIdentityKey signingKey = new();
		Uri? authorizationUri = null;
		using HttpClient http = new(new TestHttpHandler(async request =>
		{
			if (request.RequestUri!.AbsoluteUri == OpenAiOAuthProtocol.JwksEndpoint) return OAuthTestHelpers.Json(signingKey.Jwks);
			Dictionary<string, string> form = OAuthTestHelpers.ParseForm(await request.Content!.ReadAsStringAsync());
			string nonce = OAuthTestHelpers.ParseQuery(authorizationUri!.Query)["nonce"];
			return OAuthTestHelpers.TokenResponse(signingKey.CreateIdToken(form["client_id"], nonce, subject: "new-subject"));
		}));
		ChatGptAuth auth = new(http, store);
		await auth.RestoreAsync(default);
		(Task signIn, Uri uri) = await StartLoginAsync(auth, value => authorizationUri = value);
		Assert.Equal(200, await OAuthTestHelpers.SendCallbackAsync(new Uri(OAuthTestHelpers.ParseQuery(uri.Query)["redirect_uri"]), OAuthTestHelpers.CallbackQuery(uri, "oaiapp-new")));
		await Assert.ThrowsAsync<InvalidOperationException>(() => signIn);
		Assert.Equal("old-subject", (await auth.GetCredentialAsync(default)).Subject);
		Assert.Equal("old-subject", store.Value!.ActiveSubject);
		Assert.Single(store.Value.Registrations);
	}

	[Fact]
	public async Task CancellingLoginClosesListenerAndAllowsAnotherAttempt()
	{
		using HttpClient http = new(new TestHttpHandler(_ => throw new Xunit.Sdk.XunitException("Cancelled login must not call token endpoints")));
		ChatGptAuth auth = new(http);
		using CancellationTokenSource firstCancellation = new();
		TaskCompletionSource<Uri> firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task first = auth.SignInAsync(null, uri => firstStarted.TrySetResult(uri), firstCancellation.Token);
		await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		firstCancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

		using CancellationTokenSource secondCancellation = new();
		TaskCompletionSource<Uri> secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task second = auth.SignInAsync(null, uri => secondStarted.TrySetResult(uri), secondCancellation.Token);
		await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		secondCancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
		Assert.False(auth.IsSignedIn);
	}

	[Fact]
	public async Task DuplicateEmailRegistrationsRemainDistinctByClientId()
	{
		ChatGptRegistration first = new("oaiapp-first-123456", "subject-1", "same@example.com", null);
		ChatGptRegistration second = new("oaiapp-second-987654", "subject-2", "same@example.com", null);
		ChatGptAuth auth = new(new HttpClient(), new MemoryCredentialStore(new ChatGptAuthState(first.ClientId, first.Subject, [first, second])));
		await auth.RestoreAsync(default);
		ChatGptAccountOption[] options = auth.LoginOptions.Skip(1).ToArray();
		Assert.Equal(2, options.Length);
		Assert.Contains("123456", options[0].Label, StringComparison.Ordinal);
		Assert.Contains("987654", options[1].Label, StringComparison.Ordinal);
		Assert.NotEqual(options[0].Label, options[1].Label);
	}

	private static async Task<(Task Login, Uri AuthorizationUri)> StartLoginAsync(
		ChatGptAuth auth,
		Action<Uri>? onStarted = null,
		ChatGptAccountOption? target = null)
	{
		TaskCompletionSource<Uri> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task login = auth.SignInAsync(target, uri =>
		{
			onStarted?.Invoke(uri);
			started.TrySetResult(uri);
		}, default);
		Uri authorizationUri = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		return (login, authorizationUri);
	}

	private static ChatGptCredential StoredCredential(
		string clientId,
		string subject,
		DateTimeOffset? expiresAt = null,
		DateTimeOffset? earliestRefreshAt = null,
		string scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct") =>
		new("opaque-stored-access", "stored-refresh-token", expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), subject,
			clientId, subject, "verified-id-token", scopes, "user@example.com", earliestRefreshAt ?? DateTimeOffset.UtcNow.AddMinutes(-1));

	private static ChatGptAuthState StateWith(ChatGptCredential credential)
	{
		ChatGptRegistration registration = new(credential.ClientId, credential.Subject, credential.Email, credential, credential.Scopes);
		return new ChatGptAuthState(credential.ClientId, credential.Subject, [registration]);
	}
}
