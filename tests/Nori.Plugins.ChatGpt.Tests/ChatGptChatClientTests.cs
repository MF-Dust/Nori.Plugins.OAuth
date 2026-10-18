using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class ChatGptChatClientTests
{
	private static readonly ChatMessage[] Hello = [new(ChatRole.User, "你好")];
	private static readonly ChatGptCredential Credential = new("access-secret", "refresh-secret", DateTimeOffset.UtcNow.AddHours(1), "account-secret");
	private const string Finished = """{"type":"response.completed","response":{"id":"resp_1","model":"gpt-5","status":"completed","output":[]}}""";

	private static ChatGptChatClient Client(HttpClient http) => new(http, _ => Task.FromResult(Credential), "gpt-5");

	private static HttpResponseMessage Sse(params string[] events) => RawSse(string.Concat(events.Select(data => $"data: {data}\n\n")));

	private static HttpResponseMessage RawSse(string text) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(text, Encoding.UTF8, "text/event-stream"),
	};

	private static HttpResponseMessage Streaming(Stream stream) => new(HttpStatusCode.OK)
	{
		Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } },
	};

	private static string TextFinished(string text) => JsonSerializer.Serialize(new
	{
		type = "response.completed",
		response = new
		{
			id = "resp_1", model = "gpt-5", status = "completed",
			output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } } },
		},
	});

	private static string CallFinished(string arguments) => JsonSerializer.Serialize(new
	{
		type = "response.completed",
		response = new
		{
			id = "resp_tool", status = "completed",
			output = new[] { new { type = "function_call", id = "fc_1", call_id = "call_1", name = "get_weather", arguments } },
		},
	});

	private static async Task<List<ChatResponseUpdate>> CollectAsync(IChatClient client, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		List<ChatResponseUpdate> updates = [];
		await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Hello, options, cancellationToken)) updates.Add(update);
		return updates;
	}

	[Fact]
	public async Task 请求固定端点且不允许扩展选项覆盖凭证或协议字段()
	{
		ScriptedHandler handler = new(Sse(TextFinished("好")));
		using HttpClient http = new(handler) { BaseAddress = new Uri("https://attacker.test/") };
		using ChatGptChatClient client = Client(http);
		bool rawFactoryCalled = false;
		ChatOptions options = new()
		{
			ModelId = "gpt-5-codex", Instructions = "选项指令",
			AdditionalProperties = new()
			{
				["BaseUrl"] = "https://attacker.test/steal", ["base_url"] = "https://attacker.test/steal",
				["store"] = true, ["stream"] = false, ["model"] = "evil", ["instructions"] = "恶意覆盖",
				["headers"] = new Dictionary<string, string> { ["Authorization"] = "evil" },
			},
			RawRepresentationFactory = _ => { rawFactoryCalled = true; return new object(); },
		};

		ChatResponse response = await client.GetResponseAsync(
			[new(ChatRole.System, "系统指令"), new(new ChatRole("developer"), "开发者指令"), .. Hello], options);

		Assert.Equal("好", response.Text);
		CapturedRequest request = Assert.Single(handler.Requests);
		Assert.Equal("https://chatgpt.com/backend-api/codex/responses", request.Uri.AbsoluteUri);
		Assert.Equal(HttpMethod.Post, request.Method);
		Assert.Equal("Bearer access-secret", request.Headers["Authorization"]);
		Assert.Equal("account-secret", request.Headers["chatgpt-account-id"]);
		Assert.Equal("responses=experimental", request.Headers["OpenAI-Beta"]);
		Assert.Equal("text/event-stream", request.Headers["Accept"]);
		Assert.Equal("nori", request.Headers["originator"]);
		Assert.Contains("Nori", request.Headers["User-Agent"], StringComparison.Ordinal);
		Assert.False(rawFactoryCalled);
		JsonNode json = JsonNode.Parse(request.Body)!;
		Assert.Equal("gpt-5-codex", json["model"]!.GetValue<string>());
		Assert.False(json["store"]!.GetValue<bool>());
		Assert.True(json["stream"]!.GetValue<bool>());
		Assert.Equal("选项指令\n\n系统指令\n\n开发者指令", json["instructions"]!.GetValue<string>());
		Assert.Equal("user", Assert.Single(json["input"]!.AsArray())!["role"]!.GetValue<string>());
		Assert.DoesNotContain("attacker", request.Body, StringComparison.Ordinal);
		Assert.DoesNotContain("refresh-secret", request.Body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 无系统提示时仍发送指令字符串并在每次请求读取新凭证()
	{
		ScriptedHandler handler = new(Sse(Finished), Sse(Finished));
		using HttpClient http = new(handler);
		int count = 0;
		using ChatGptChatClient client = new(http, _ => Task.FromResult(Credential with { AccessToken = $"token-{++count}" }), "gpt-5");

		await client.GetResponseAsync(Hello);
		await client.GetResponseAsync(Hello);

		Assert.Equal(2, count);
		Assert.Equal("Bearer token-1", handler.Requests[0].Headers["Authorization"]);
		Assert.Equal("Bearer token-2", handler.Requests[1].Headers["Authorization"]);
		Assert.False(string.IsNullOrWhiteSpace(JsonNode.Parse(handler.Requests[0].Body)!["instructions"]!.GetValue<string>()));
	}

	[Fact]
	public async Task 请求保留文本图片和助手历史顺序()
	{
		ScriptedHandler handler = new(Sse(Finished));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);
		ChatMessage image = new(ChatRole.User,
		[
			new TextContent("看图"), new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
			new UriContent("https://images.test/picture.jpg", "image/jpeg"), new TextContent("末尾"),
		]);

		await client.GetResponseAsync([new(ChatRole.Assistant, "先前答复"), image]);

		JsonNode input = JsonNode.Parse(Assert.Single(handler.Requests).Body)!["input"]!;
		Assert.Equal("assistant", input[0]!["role"]!.GetValue<string>());
		Assert.Equal("output_text", input[0]!["content"]![0]!["type"]!.GetValue<string>());
		Assert.Equal("先前答复", input[0]!["content"]![0]!["text"]!.GetValue<string>());
		JsonArray parts = input[1]!["content"]!.AsArray();
		Assert.Equal("input_text", parts[0]!["type"]!.GetValue<string>());
		Assert.Equal("看图", parts[0]!["text"]!.GetValue<string>());
		Assert.Equal("data:image/png;base64,AQID", parts[1]!["image_url"]!.GetValue<string>());
		Assert.Equal("input_image", parts[1]!["type"]!.GetValue<string>());
		Assert.Equal("https://images.test/picture.jpg", parts[2]!["image_url"]!.GetValue<string>());
		Assert.Equal("末尾", parts[3]!["text"]!.GetValue<string>());
	}

	[Fact]
	public async Task 函数定义调用和不同类型工具结果使用Responses结构()
	{
		ScriptedHandler handler = new(Sse(Finished));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);
		AIFunction function = AIFunctionFactory.Create((string city) => city, "get_weather", "查询天气");
		ChatOptions options = new() { Tools = [function], ToolMode = ChatToolMode.RequireSpecific("get_weather"), AllowMultipleToolCalls = false };

		await client.GetResponseAsync(
		[
			new(ChatRole.Assistant, [new TextContent("先查"), new FunctionCallContent("call_1", "get_weather", new Dictionary<string, object?> { ["city"] = "上海" })]),
			new(ChatRole.Tool, [new FunctionResultContent("call_1", new { weather = "晴" }), new FunctionResultContent("call_2", "纯文本"), new FunctionResultContent("call_3", null)]),
		], options);

		JsonNode json = JsonNode.Parse(Assert.Single(handler.Requests).Body)!;
		JsonNode tool = Assert.Single(json["tools"]!.AsArray())!;
		Assert.Equal("function", tool["type"]!.GetValue<string>());
		Assert.Equal("get_weather", tool["name"]!.GetValue<string>());
		Assert.Equal("查询天气", tool["description"]!.GetValue<string>());
		Assert.Equal("object", tool["parameters"]!["type"]!.GetValue<string>());
		Assert.Equal("string", tool["parameters"]!["properties"]!["city"]!["type"]!.GetValue<string>());
		Assert.False(tool["strict"]!.GetValue<bool>());
		Assert.False(json["parallel_tool_calls"]!.GetValue<bool>());
		Assert.Equal("function", json["tool_choice"]!["type"]!.GetValue<string>());
		Assert.Equal("get_weather", json["tool_choice"]!["name"]!.GetValue<string>());
		JsonArray input = json["input"]!.AsArray();
		Assert.Equal("先查", input[0]!["content"]![0]!["text"]!.GetValue<string>());
		Assert.Equal("function_call", input[1]!["type"]!.GetValue<string>());
		Assert.Equal("call_1", input[1]!["call_id"]!.GetValue<string>());
		Assert.Equal("上海", JsonNode.Parse(input[1]!["arguments"]!.GetValue<string>())!["city"]!.GetValue<string>());
		Assert.Equal("function_call_output", input[2]!["type"]!.GetValue<string>());
		Assert.Equal("晴", JsonNode.Parse(input[2]!["output"]!.GetValue<string>())!["weather"]!.GetValue<string>());
		Assert.Equal("纯文本", input[3]!["output"]!.GetValue<string>());
		Assert.Equal("null", input[4]!["output"]!.GetValue<string>());
	}

	[Theory]
	[InlineData("auto", "auto")]
	[InlineData("none", "none")]
	[InlineData("required", "required")]
	public async Task 工具选择模式转换(string mode, string expected)
	{
		ScriptedHandler handler = new(Sse(Finished));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);
		ChatOptions options = new() { ToolMode = mode == "none" ? ChatToolMode.None : mode == "required" ? ChatToolMode.RequireAny : ChatToolMode.Auto };

		await client.GetResponseAsync(Hello, options);

		Assert.Equal(expected, JsonNode.Parse(Assert.Single(handler.Requests).Body)!["tool_choice"]!.GetValue<string>());
	}

	[Fact]
	public async Task 流式文本去重并报告完整用量和元数据()
	{
		ScriptedHandler handler = new(Sse(
			"""{"type":"response.created","response":{"id":"resp_1","model":"gpt-5-server","created_at":1000}}""",
			"""{"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"你"}""",
			"""{"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"好"}""",
			"""{"type":"response.output_text.done","output_index":0,"content_index":0,"text":"你好"}""",
			"""{"type":"response.output_item.done","output_index":0,"item":{"type":"message","content":[{"type":"output_text","text":"你好"}]}}""",
			"""{"type":"response.completed","response":{"id":"resp_1","model":"gpt-5-server","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"你好"}]}],"usage":{"input_tokens":11,"output_tokens":7,"total_tokens":18,"input_tokens_details":{"cached_tokens":4},"output_tokens_details":{"reasoning_tokens":5}}}}""",
			"[DONE]"));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		List<ChatResponseUpdate> updates = await CollectAsync(client);

		Assert.Equal(["你", "好"], updates.Where(update => update.Text.Length > 0).Select(update => update.Text));
		Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);
		Assert.All(updates, update => Assert.Equal("resp_1", update.ResponseId));
		Assert.All(updates, update => Assert.Equal("gpt-5-server", update.ModelId));
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000), updates[^1].CreatedAt);
		UsageDetails usage = Assert.Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>()).Details;
		Assert.Equal(11, usage.InputTokenCount);
		Assert.Equal(7, usage.OutputTokenCount);
		Assert.Equal(18, usage.TotalTokenCount);
		Assert.Equal(4, usage.CachedInputTokenCount);
		Assert.Equal(5, usage.ReasoningTokenCount);
	}

	[Fact]
	public async Task 非流式仍使用SSE且从完成快照补足文本和用量()
	{
		ScriptedHandler handler = new(Sse(
			"""{"type":"response.output_text.delta","output_index":0,"delta":"早"}""",
			"""{"type":"response.completed","response":{"id":"resp_2","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"早安"},{"type":"refusal","refusal":"不能执行"}]}],"usage":{"input_tokens":3,"output_tokens":2}}}"""));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		ChatResponse response = await client.GetResponseAsync(Hello);

		Assert.Equal("早安不能执行", response.Text);
		Assert.Equal("resp_2", response.ResponseId);
		Assert.Equal("gpt-5", response.ModelId);
		Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
		Assert.Equal(5, response.Usage?.TotalTokenCount);
		Assert.True(JsonNode.Parse(Assert.Single(handler.Requests).Body)!["stream"]!.GetValue<bool>());
	}

	[Fact]
	public async Task SSE支持分片UTF8多行数据注释事件名CRLF和EOF尾帧()
	{
		string sse = "\uFEFF: 心跳\r\nid: 1\r\nretry: 2\r\nevent: response.output_text.delta\r\ndata: {\"output_index\":0,\r\ndata: \"delta\":\"你😀\"}\r\n\r\n"
			+ "event: response.completed\rdata: {\"response\":{\"status\":\"completed\"}}";
		FragmentedStream stream = new(Encoding.UTF8.GetBytes(sse), 1);
		ScriptedHandler handler = new(Streaming(stream));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		ChatResponse response = await client.GetResponseAsync(Hello);

		Assert.Equal("你😀", response.Text);
		Assert.True(stream.Disposed);
	}

	[Fact]
	public async Task 成功完成后无需等待连接关闭()
	{
		BlockingTailStream stream = new(Encoding.UTF8.GetBytes($"data: {TextFinished("完成")}\n\n"));
		ScriptedHandler handler = new(Streaming(stream));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		ChatResponse response = await client.GetResponseAsync(Hello).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal("完成", response.Text);
		Assert.False(stream.Blocked.Task.IsCompleted);
		Assert.True(stream.Disposed);
	}

	[Fact]
	public async Task 流式函数只输出一次并回传加密思考和服务端项标识()
	{
		ScriptedHandler handler = new(Sse(
			"""{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":""}}""",
			"""{"type":"response.function_call_arguments.delta","output_index":1,"delta":"{\"city\":"}""",
			"""{"type":"response.function_call_arguments.delta","output_index":1,"delta":"\"上海\"}"}""",
			"""{"type":"response.function_call_arguments.done","output_index":1,"arguments":"{\"city\":\"上海\"}"}""",
			"""{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[]}}""",
			"""{"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":"{\"city\":\"上海\"}"}}""",
			"""{"type":"response.completed","response":{"status":"completed","output":[{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"opaque-signed-data"},{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":"{\"city\":\"上海\"}"}]}}"""), Sse(Finished));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		ChatResponse response = await client.GetResponseAsync(Hello);
		FunctionCallContent call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
		Assert.Equal("call_1", call.CallId);
		Assert.Equal("get_weather", call.Name);
		Assert.Equal("上海", Assert.IsType<JsonElement>(call.Arguments!["city"]).GetString());
		Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
		TextReasoningContent reasoning = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>());
		Assert.Contains("opaque-signed-data", reasoning.ProtectedData, StringComparison.Ordinal);

		await client.GetResponseAsync([.. Hello, .. response.Messages, new(ChatRole.Tool, [new FunctionResultContent(call.CallId, "晴")])]);

		JsonArray input = JsonNode.Parse(handler.Requests[1].Body)!["input"]!.AsArray();
		Assert.Equal("reasoning", input[1]!["type"]!.GetValue<string>());
		Assert.Equal("opaque-signed-data", input[1]!["encrypted_content"]!.GetValue<string>());
		Assert.Equal("fc_1", input[2]!["id"]!.GetValue<string>());
		Assert.Equal("call_1", input[2]!["call_id"]!.GetValue<string>());
		Assert.Equal("call_1", input[3]!["call_id"]!.GetValue<string>());
	}

	[Fact]
	public async Task 宿主函数调用中间件能完成流式工具往返()
	{
		ScriptedHandler handler = new(Sse(CallFinished("{\"city\":\"上海\"}")), Sse(TextFinished("上海晴")));
		using HttpClient http = new(handler);
		using IChatClient client = new ChatClientBuilder(Client(http)).UseFunctionInvocation().Build();
		List<string> cities = [];
		AIFunction function = AIFunctionFactory.Create((string city) => { cities.Add(city); return "晴"; }, "get_weather", "查询天气");

		ChatResponse response = await client.GetStreamingResponseAsync(Hello, new ChatOptions { Tools = [function], AllowMultipleToolCalls = true }).ToChatResponseAsync();

		Assert.Equal("上海晴", response.Text);
		Assert.Equal(["上海"], cities);
		Assert.Equal(2, handler.Requests.Count);
		JsonArray input = JsonNode.Parse(handler.Requests[1].Body)!["input"]!.AsArray();
		Assert.Contains(input, item => item?["type"]?.GetValue<string>() == "function_call" && item["call_id"]?.GetValue<string>() == "call_1");
		JsonNode output = Assert.Single(input, item => item?["type"]?.GetValue<string>() == "function_call_output")!;
		// 中间件返回 JsonElement，工具结果按 JSON 字符串编码后交给 Responses。
		Assert.Equal("晴", JsonSerializer.Deserialize<string>(output["output"]!.GetValue<string>()));
	}

	[Theory]
	[InlineData("response.incomplete", "incomplete")]
	[InlineData("response.failed", "failed")]
	[InlineData("error", "failed")]
	[InlineData("response.error", "failed")]
	[InlineData("response.completed", "incomplete")]
	[InlineData("response.completed", "failed")]
	[InlineData("response.completed", "cancelled")]
	[InlineData("response.completed", "in_progress")]
	public async Task 不完整和失败事件必须报错且不泄露服务端正文(string type, string status)
	{
		string failure = JsonSerializer.Serialize(new { type, message = "access-secret 私密提示词", response = new { status, error = new { message = "refresh-secret account-secret" } } });
		ScriptedHandler handler = new(Sse(
			"""{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","call_id":"call_1","name":"get_weather","arguments":"{}"}}""", failure));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);
		List<ChatResponseUpdate> updates = [];

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Hello)) updates.Add(update);
		});

		Assert.Empty(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>());
		Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("私密提示词", error.ToString(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("")]
	[InlineData("data: [DONE]\n\n")]
	[InlineData("data: {\"type\":\"response.created\",\"response\":{}}\n\n")]
	[InlineData("data: {\"type\":\"response.output_text.delta\",\"delta\":\"半句\"}\n\n")]
	public async Task 未收到完成事件的EOF不能伪装成功(string stream)
	{
		ScriptedHandler handler = new(RawSse(stream));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Hello));

		Assert.Contains("完成事件之前中断", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("data: {\"type\":")]
	[InlineData("data: []\n\n")]
	[InlineData("data: {\"type\":\"response.completed\"}\n\n")]
	public async Task 无效JSON或缺失完成响应必须报错(string stream)
	{
		ScriptedHandler handler = new(RawSse(stream));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Hello));
	}

	[Theory]
	[InlineData("{\"city\":")]
	[InlineData("[]")]
	[InlineData("null")]
	[InlineData("\"access-secret\"")]
	public async Task 工具参数必须完整且为JSON对象(string arguments)
	{
		ScriptedHandler handler = new(Sse(CallFinished(arguments)));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Hello));

		Assert.DoesNotContain("access-secret", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task 未结束的函数参数不能因完成事件而被执行()
	{
		ScriptedHandler handler = new(Sse(
			"""{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call_1","name":"get_weather","arguments":""}}""",
			"""{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{}"}""", Finished));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Hello));
	}

	[Fact]
	public async Task 支持Codex的done完成事件别名()
	{
		ScriptedHandler handler = new(Sse(TextFinished("完成").Replace("response.completed", "response.done", StringComparison.Ordinal)));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		Assert.Equal("完成", (await client.GetResponseAsync(Hello)).Text);
	}

	[Theory]
	[InlineData(401)]
	[InlineData(403)]
	[InlineData(429)]
	[InlineData(500)]
	public async Task HTTP失败只暴露状态码且不读取敏感正文(int status)
	{
		NeverReadContent content = new();
		ScriptedHandler handler = new(new HttpResponseMessage((HttpStatusCode)status)
		{
			ReasonPhrase = "access-secret 私密提示词", Content = content,
		});
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(Hello));

		Assert.Equal((HttpStatusCode)status, error.StatusCode);
		Assert.Contains(status.ToString(), error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("私密提示词", error.ToString(), StringComparison.Ordinal);
		Assert.False(content.Read);
		Assert.True(content.Disposed);
	}

	[Fact]
	public async Task 预取消不读取凭证或发送请求()
	{
		ScriptedHandler handler = new(Sse(Finished));
		using HttpClient http = new(handler);
		bool credentialCalled = false;
		using ChatGptChatClient client = new(http, _ => { credentialCalled = true; return Task.FromResult(Credential); }, "gpt-5");
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync(Hello, cancellationToken: cancellation.Token));

		Assert.False(credentialCalled);
		Assert.Empty(handler.Requests);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task 凭证获取和HTTP发送都接收取消信号(bool blockCredential)
	{
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		ScriptedHandler handler = new(async token =>
		{
			started.TrySetResult();
			await Task.Delay(Timeout.Infinite, token);
			return Sse(Finished);
		});
		using HttpClient http = new(handler);
		using ChatGptChatClient client = new(http, async token =>
		{
			if (blockCredential)
			{
				started.TrySetResult();
				await Task.Delay(Timeout.Infinite, token);
			}
			return Credential;
		}, "gpt-5");
		using CancellationTokenSource cancellation = new();
		Task response = client.GetResponseAsync(Hello, cancellationToken: cancellation.Token);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public async Task 枚举器取消可中断等待中的SSE读取并释放响应()
	{
		BlockingTailStream stream = new(Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"半句\"}\n\n"));
		ScriptedHandler handler = new(Streaming(stream));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);
		using CancellationTokenSource cancellation = new();
		await using IAsyncEnumerator<ChatResponseUpdate> enumerator = client.GetStreamingResponseAsync(Hello).GetAsyncEnumerator(cancellation.Token);
		Assert.True(await enumerator.MoveNextAsync());
		Assert.Equal("半句", enumerator.Current.Text);
		Task<bool> move = enumerator.MoveNextAsync().AsTask();
		await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.True(stream.Disposed);
	}

	[Fact]
	public async Task 提前停止枚举释放响应但不关闭共享HTTP()
	{
		BlockingTailStream stream = new(Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"开头\"}\n\n"));
		ScriptedHandler handler = new(Streaming(stream), Sse(TextFinished("仍可使用")));
		using HttpClient http = new(handler);
		ChatGptChatClient client = Client(http);
		await using (IAsyncEnumerator<ChatResponseUpdate> enumerator = client.GetStreamingResponseAsync(Hello).GetAsyncEnumerator())
		{
			Assert.True(await enumerator.MoveNextAsync());
		}
		client.Dispose();

		Assert.True(stream.Disposed);
		Assert.False(handler.Disposed);
		using ChatGptChatClient next = Client(http);
		Assert.Equal("仍可使用", (await next.GetResponseAsync(Hello)).Text);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task 单帧大小限制同时覆盖超长单行和多行帧(bool multipleLines)
	{
		string frame = multipleLines ? string.Concat(Enumerable.Repeat(":" + new string('x', 8190) + "\n", 129)) : "data: " + new string('x', 1024 * 1024);
		ScriptedHandler handler = new(RawSse(frame));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(client));

		Assert.Contains("大小限制", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 原始流总字节上限不能被心跳帧绕过()
	{
		RepeatingStream stream = new(Encoding.UTF8.GetBytes(":" + new string('x', 8190) + "\n\n"), 32L * 1024 * 1024 + 1024);
		ScriptedHandler handler = new(Streaming(stream));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(client));

		Assert.Contains("大小限制", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 多帧累计文本仍受输出上限约束()
	{
		string data = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = new string('x', 512 * 1024) });
		byte[] frame = Encoding.UTF8.GetBytes($"data: {data}\n\n");
		ScriptedHandler handler = new(Streaming(new RepeatingStream(frame, frame.LongLength * 17)));
		using HttpClient http = new(handler);
		using ChatGptChatClient client = Client(http);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(Hello)) { }
		});

		Assert.Contains("大小限制", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void 服务查询报告固定提供商且不暴露共享HTTP()
	{
		using HttpClient http = new(new ScriptedHandler(Sse(Finished)));
		using ChatGptChatClient client = Client(http);

		Assert.Same(client, client.GetService(typeof(IChatClient)));
		ChatClientMetadata metadata = Assert.IsType<ChatClientMetadata>(client.GetService(typeof(ChatClientMetadata)));
		Assert.Equal("ChatGPT", metadata.ProviderName);
		Assert.Equal("gpt-5", metadata.DefaultModelId);
		Assert.Equal("https://chatgpt.com/backend-api/codex/responses", metadata.ProviderUri?.AbsoluteUri);
		Assert.Null(client.GetService(typeof(HttpClient)));
		Assert.Null(client.GetService(typeof(IChatClient), "other"));
	}

	private sealed record CapturedRequest(Uri Uri, HttpMethod Method, string Body, Dictionary<string, string> Headers);

	private sealed class ScriptedHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] responses) : HttpMessageHandler
	{
		private int _index;
		internal List<CapturedRequest> Requests { get; } = [];
		internal bool Disposed { get; private set; }

		internal ScriptedHandler(params HttpResponseMessage[] responses)
			: this(responses.Select(response => new Func<CancellationToken, Task<HttpResponseMessage>>(_ => Task.FromResult(response))).ToArray()) { }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(new CapturedRequest(request.RequestUri!, request.Method,
				request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
				request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase)));
			return await responses[Math.Min(_index++, responses.Length - 1)](cancellationToken);
		}

		protected override void Dispose(bool disposing)
		{
			Disposed = true;
			base.Dispose(disposing);
		}
	}

	private sealed class NeverReadContent : HttpContent
	{
		internal bool Read { get; private set; }
		internal bool Disposed { get; private set; }
		protected override bool TryComputeLength(out long length) { length = 0; return false; }
		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
		{
			Read = true;
			throw new InvalidOperationException("access-secret 私密提示词");
		}
		protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
	}

	private sealed class FragmentedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
	{
		internal bool Disposed { get; private set; }
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
			base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
		protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
	}

	private sealed class BlockingTailStream(byte[] prefix) : Stream
	{
		private int _position;
		internal bool Disposed { get; private set; }
		internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => throw new NotSupportedException();
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_position < prefix.Length)
			{
				int length = Math.Min(buffer.Length, prefix.Length - _position);
				prefix.AsMemory(_position, length).CopyTo(buffer);
				_position += length;
				return length;
			}
			Blocked.TrySetResult();
			await Task.Delay(Timeout.Infinite, cancellationToken);
			return 0;
		}

		protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
	}

	private sealed class RepeatingStream(byte[] pattern, long length) : Stream
	{
		private long _position;
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => length;
		public override long Position { get => _position; set => throw new NotSupportedException(); }
		public override void Flush() => throw new NotSupportedException();
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int count = (int)Math.Min(buffer.Length, length - _position);
			int copied = 0;
			while (copied < count)
			{
				int offset = (int)(_position % pattern.Length);
				int part = Math.Min(count - copied, pattern.Length - offset);
				pattern.AsMemory(offset, part).CopyTo(buffer[copied..]);
				copied += part;
				_position += part;
			}
			return ValueTask.FromResult(count);
		}
	}
}
