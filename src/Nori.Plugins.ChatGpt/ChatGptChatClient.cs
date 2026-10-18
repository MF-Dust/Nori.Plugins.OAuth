using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Nori.Plugins.ChatGpt;

/// <summary>将 ChatGPT 订阅的 Codex Responses 流转换为宿主的聊天与工具契约。</summary>
internal sealed class ChatGptChatClient(
	HttpClient http,
	Func<CancellationToken, Task<ChatGptCredential>> getCredential,
	string model) : IChatClient
{
	private static readonly Uri Endpoint = new("https://chatgpt.com/backend-api/codex/responses");
	private const string ItemIdKey = "chatgpt.item_id";
	private const int MaximumFrameBytes = 1024 * 1024;
	private const int MaximumResponseBytes = 32 * 1024 * 1024;
	private const int MaximumOutputCharacters = 8 * 1024 * 1024;

	public Task<ChatResponse> GetResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		CancellationToken cancellationToken = default) =>
		GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(messages);
		cancellationToken.ThrowIfCancellationRequested();
		string requestedModel = string.IsNullOrWhiteSpace(options?.ModelId) ? model : options.ModelId;
		JsonObject body = BuildRequest(messages, options, requestedModel);
		ChatGptCredential credential = await getCredential(cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();

		// 只允许固定端点与明确支持的字段，不能让调用方的 BaseUrl 或原始请求覆盖凭证的去向。
		using HttpRequestMessage request = new(HttpMethod.Post, Endpoint)
		{
			Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
		request.Headers.Add("chatgpt-account-id", credential.AccountId);
		request.Headers.Add("OpenAI-Beta", "responses=experimental");
		request.Headers.Add("originator", "nori");
		request.Headers.UserAgent.ParseAdd("Nori.Desktop");
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

		using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			// 错误正文和 ReasonPhrase 可能回显令牌、提示词或账户信息，均不进入异常。
			throw new HttpRequestException($"ChatGPT 请求失败（HTTP {(int)response.StatusCode}），请检查登录状态或订阅额度。", null, response.StatusCode);
		}

		using StreamReader reader = new(new BoundedSseStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false)), Encoding.UTF8);
		ResponseState state = new(requestedModel);
		await foreach ((string? eventName, string data) in ReadEventsAsync(reader, cancellationToken).ConfigureAwait(false))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (data.Trim() == "[DONE]") break;
			JsonElement item = ParseObject(data);
			string? type = String(item, "type") ?? eventName;
			switch (type)
			{
				case "response.created":
				case "response.in_progress":
					state.SetMetadata(Response(item));
					break;
				case "response.output_text.delta":
				case "response.refusal.delta":
					string delta = String(item, "delta") ?? throw ProtocolError();
					state.AppendText(Index(item), ContentIndex(item), delta);
					if (delta.Length > 0) yield return state.Update([new TextContent(delta)]);
					break;
				case "response.output_text.done":
				case "response.refusal.done":
					string text = String(item, type == "response.refusal.done" ? "refusal" : "text") ?? throw ProtocolError();
					string suffix = state.CompleteText(Index(item), ContentIndex(item), text);
					if (suffix.Length > 0) yield return state.Update([new TextContent(suffix)]);
					break;
				case "response.output_item.added":
					if (item.TryGetProperty("item", out JsonElement added) && String(added, "type") == "function_call")
						state.SetCall(Index(item), added, false);
					break;
				case "response.function_call_arguments.delta":
					state.AppendArguments(Index(item), String(item, "delta") ?? throw ProtocolError());
					break;
				case "response.function_call_arguments.done":
					state.CompleteArguments(Index(item), String(item, "arguments") ?? throw ProtocolError());
					break;
				case "response.output_item.done":
					if (!item.TryGetProperty("item", out JsonElement completed)) throw ProtocolError();
					List<AIContent> contents = state.AcceptItem(Index(item), completed);
					if (contents.Count > 0) yield return state.Update(contents);
					break;
				case "response.incomplete":
					throw new InvalidOperationException("ChatGPT 回复未完成，请重试或减少输入内容。");
				case "response.failed":
				case "error":
				case "response.error":
					throw new InvalidOperationException("ChatGPT 生成回复失败，请检查登录状态或稍后重试。");
				case "response.completed":
				case "response.done":
					JsonElement final = Response(item);
					string? status = String(final, "status");
					if (status == "incomplete") throw new InvalidOperationException("ChatGPT 回复未完成，请重试或减少输入内容。");
					if (status is "failed" or "cancelled") throw new InvalidOperationException("ChatGPT 生成回复失败，请检查登录状态或稍后重试。");
					if (status is not null and not "completed") throw ProtocolError();
					state.SetMetadata(final);
					List<AIContent> finalContents = state.Finish(final);
					cancellationToken.ThrowIfCancellationRequested();
					ChatResponseUpdate update = state.Update(finalContents);
					update.FinishReason = state.HasCalls ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop;
					yield return update;
					yield break;
			}
		}
		cancellationToken.ThrowIfCancellationRequested();
		throw new InvalidOperationException("ChatGPT 响应流在完成事件之前中断。");
	}

	public object? GetService(Type serviceType, object? serviceKey = null)
	{
		ArgumentNullException.ThrowIfNull(serviceType);
		if (serviceKey is not null) return null;
		if (serviceType == typeof(ChatClientMetadata)) return new ChatClientMetadata("ChatGPT", Endpoint, model);
		return serviceType.IsInstanceOfType(this) ? this : null;
	}

	public void Dispose()
	{
		// HTTP 连接池属于插件，宿主每次聊天释放适配器时不能关闭它。
	}

	private static JsonObject BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, string requestedModel)
	{
		List<string> instructions = [];
		if (!string.IsNullOrWhiteSpace(options?.Instructions)) instructions.Add(options.Instructions);
		JsonArray input = [];
		foreach (ChatMessage message in messages)
		{
			if (message.Role == ChatRole.System || message.Role.Value == "developer")
			{
				foreach (AIContent content in message.Contents)
				{
					if (content is not TextContent text) throw new NotSupportedException("ChatGPT 的系统指令只支持文本。");
					if (text.Text.Length > 0) instructions.Add(text.Text);
				}
				continue;
			}

			JsonArray parts = [];
			void FlushText()
			{
				if (parts.Count == 0) return;
				input.Add(new JsonObject { ["role"] = message.Role.Value, ["content"] = parts });
				parts = [];
			}
			foreach (AIContent content in message.Contents)
			{
				switch (content)
				{
					case TextContent text when message.Role == ChatRole.User || message.Role == ChatRole.Assistant:
						parts.Add(new JsonObject { ["type"] = message.Role == ChatRole.Assistant ? "output_text" : "input_text", ["text"] = text.Text });
						break;
					case DataContent data when message.Role == ChatRole.User && data.HasTopLevelMediaType("image"):
						parts.Add(Image(data.Uri));
						break;
					case UriContent uri when message.Role == ChatRole.User && uri.HasTopLevelMediaType("image"):
						if (uri.Uri.Scheme is not "https" and not "http") throw new NotSupportedException("ChatGPT 图片地址必须使用 HTTP 或 HTTPS。");
						parts.Add(Image(uri.Uri.AbsoluteUri));
						break;
					case FunctionCallContent call when message.Role == ChatRole.Assistant:
						FlushText();
						JsonObject function = new()
						{
							["type"] = "function_call", ["call_id"] = call.CallId, ["name"] = call.Name,
							["arguments"] = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions),
						};
						if (call.AdditionalProperties?.TryGetValue(ItemIdKey, out object? id) == true && id is string itemId)
							function["id"] = itemId;
						input.Add(function);
						break;
					case FunctionResultContent result when message.Role == ChatRole.Tool:
						FlushText();
						input.Add(new JsonObject
						{
							["type"] = "function_call_output", ["call_id"] = result.CallId,
							["output"] = result.Result as string ?? JsonSerializer.Serialize(result.Result, AIJsonUtilities.DefaultOptions),
						});
						break;
					case TextReasoningContent reasoning when message.Role == ChatRole.Assistant:
						FlushText();
						// store=false 的工具往返需要回传服务端签名的思考项，而非仅回传可读摘要。
						if (!string.IsNullOrEmpty(reasoning.ProtectedData))
						{
							JsonElement item = ParseObject(reasoning.ProtectedData);
							if (String(item, "type") != "reasoning") throw ProtocolError();
							input.Add(JsonNode.Parse(item.GetRawText()));
						}
						break;
					default:
						throw new NotSupportedException("ChatGPT 暂不支持此消息角色或内容类型。");
				}
			}
			FlushText();
		}

		JsonObject body = new()
		{
			["model"] = requestedModel, ["store"] = false, ["stream"] = true,
			["instructions"] = instructions.Count == 0 ? "你是一个乐于助人的助手。" : string.Join("\n\n", instructions),
			["input"] = input, ["include"] = new JsonArray("reasoning.encrypted_content"),
			["parallel_tool_calls"] = options?.AllowMultipleToolCalls ?? true,
		};
		body["tool_choice"] = options?.ToolMode switch
		{
			RequiredChatToolMode { RequiredFunctionName: { } name } => new JsonObject { ["type"] = "function", ["name"] = name },
			RequiredChatToolMode => JsonValue.Create("required"),
			{ } mode when mode.Equals(ChatToolMode.None) => JsonValue.Create("none"),
			_ => JsonValue.Create("auto"),
		};
		if (options?.Tools is { Count: > 0 } tools)
		{
			JsonArray converted = [];
			foreach (AITool tool in tools)
			{
				if (tool is not AIFunctionDeclaration function) throw new NotSupportedException("ChatGPT 暂不支持非函数工具。");
				converted.Add(new JsonObject
				{
					["type"] = "function", ["name"] = function.Name, ["description"] = function.Description,
					["parameters"] = JsonNode.Parse(function.JsonSchema.GetRawText()), ["strict"] = false,
				});
			}
			body["tools"] = converted;
		}
		return body;
	}

	private static JsonObject Image(string uri) => new() { ["type"] = "input_image", ["image_url"] = uri, ["detail"] = "auto" };

	private static async IAsyncEnumerable<(string? EventName, string Data)> ReadEventsAsync(
		StreamReader reader,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		StringBuilder data = new();
		string? eventName = null;
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			if (line is null || line.Length == 0)
			{
				if (data.Length > 0) yield return (eventName, data.ToString(0, data.Length - 1));
				data.Clear();
				eventName = null;
				if (line is null) yield break;
				continue;
			}
			int colon = line.IndexOf(':');
			if (colon == 0) continue;
			string field = colon < 0 ? line : line[..colon];
			string value = colon < 0 ? "" : line[(colon + 1)..];
			if (value.StartsWith(' ')) value = value[1..];
			if (field == "data") data.Append(value).Append('\n');
			else if (field == "event") eventName = value;
		}
	}

	private static JsonElement ParseObject(string json)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			if (document.RootElement.ValueKind != JsonValueKind.Object) throw ProtocolError();
			return document.RootElement.Clone();
		}
		catch (JsonException)
		{
			throw new InvalidOperationException("ChatGPT 响应包含无效的 JSON。");
		}
	}

	private static string? String(JsonElement item, string name) =>
		item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static long? Number(JsonElement item, string name) =>
		item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;

	private static int Index(JsonElement item) => checked((int)(Number(item, "output_index") ?? 0));
	private static int ContentIndex(JsonElement item) => checked((int)(Number(item, "content_index") ?? 0));
	private static InvalidOperationException ProtocolError() => new("ChatGPT 响应格式不完整或不符合 Responses 协议。");

	private static JsonElement Response(JsonElement item) =>
		item.TryGetProperty("response", out JsonElement response) && response.ValueKind == JsonValueKind.Object ? response : throw ProtocolError();

	private static InvalidOperationException SizeError() => new("ChatGPT 回复超过大小限制。");

	/// <summary>在 StreamReader 扩展行缓冲区之前限制原始字节和帧，避免超长单行绕过限制。</summary>
	private sealed class BoundedSseStream(Stream inner) : Stream
	{
		private long _bytes;
		private int _frameBytes;
		private bool _lineHasData;
		private bool _carriageReturn;

		public override bool CanRead => inner.CanRead;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override int Read(byte[] buffer, int offset, int count)
		{
			int read = inner.Read(buffer, offset, count);
			Check(buffer.AsSpan(offset, read));
			return read;
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			Check(buffer.Span[..read]);
			return read;
		}

		private void Check(ReadOnlySpan<byte> bytes)
		{
			_bytes += bytes.Length;
			if (_bytes > MaximumResponseBytes) throw SizeError();
			foreach (byte value in bytes)
			{
				if (_carriageReturn && value != '\n') EndLine();
				if (++_frameBytes > MaximumFrameBytes) throw SizeError();
				if (value == '\r') _carriageReturn = true;
				else if (value == '\n') EndLine();
				else _lineHasData = true;
			}
		}

		private void EndLine()
		{
			if (!_lineHasData) _frameBytes = 0;
			_lineHasData = false;
			_carriageReturn = false;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing) inner.Dispose();
			base.Dispose(disposing);
		}
	}

	private sealed class ResponseState(string requestedModel)
	{
		private readonly Dictionary<(int Output, int Content), StringBuilder> _text = [];
		private readonly SortedDictionary<int, PendingCall> _calls = [];
		private readonly SortedDictionary<int, JsonElement> _reasoning = [];
		private string? _responseId;
		private string _modelId = requestedModel;
		private DateTimeOffset? _createdAt;
		private int _outputCharacters;

		internal bool HasCalls => _calls.Count > 0;

		internal void SetMetadata(JsonElement response)
		{
			_responseId = String(response, "id") ?? _responseId;
			_modelId = String(response, "model") ?? _modelId;
			if (Number(response, "created_at") is { } timestamp) _createdAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
		}

		internal ChatResponseUpdate Update(List<AIContent> contents) => new(ChatRole.Assistant, contents)
		{
			ResponseId = _responseId, ModelId = _modelId, CreatedAt = _createdAt,
		};

		private void ReserveCharacters(int count)
		{
			if (count > MaximumOutputCharacters - _outputCharacters) throw SizeError();
			_outputCharacters += count;
		}

		internal void AppendText(int index, int contentIndex, string text)
		{
			ReserveCharacters(text.Length);
			if (!_text.TryGetValue((index, contentIndex), out StringBuilder? builder)) _text[(index, contentIndex)] = builder = new StringBuilder();
			builder.Append(text);
		}

		internal string CompleteText(int index, int contentIndex, string text)
		{
			string previous = _text.GetValueOrDefault((index, contentIndex))?.ToString() ?? "";
			if (!text.StartsWith(previous, StringComparison.Ordinal)) throw ProtocolError();
			string suffix = text[previous.Length..];
			AppendText(index, contentIndex, suffix);
			return suffix;
		}

		private PendingCall Call(int index)
		{
			if (!_calls.TryGetValue(index, out PendingCall? call)) _calls[index] = call = new PendingCall();
			return call;
		}

		internal void SetCall(int index, JsonElement item, bool completed)
		{
			PendingCall call = Call(index);
			call.Id = String(item, "call_id") ?? call.Id;
			call.Name = String(item, "name") ?? call.Name;
			call.ItemId = String(item, "id") ?? call.ItemId;
			if (String(item, "arguments") is { } arguments) SetArguments(call, arguments);
			call.Completed |= completed;
		}

		private void SetArguments(PendingCall call, string arguments)
		{
			ReserveCharacters(Math.Max(0, arguments.Length - call.Arguments.Length));
			call.Arguments.Clear().Append(arguments);
		}

		internal void AppendArguments(int index, string delta)
		{
			ReserveCharacters(delta.Length);
			Call(index).Arguments.Append(delta);
		}

		internal void CompleteArguments(int index, string arguments)
		{
			PendingCall call = Call(index);
			SetArguments(call, arguments);
			call.Completed = true;
		}

		internal List<AIContent> AcceptItem(int index, JsonElement item)
		{
			List<AIContent> contents = [];
			switch (String(item, "type"))
			{
				case "function_call":
					if (String(item, "status") is { } status && status != "completed") throw ProtocolError();
					SetCall(index, item, true);
					break;
				case "reasoning":
					_reasoning[index] = item.Clone();
					break;
				case "message":
					if (!item.TryGetProperty("content", out JsonElement parts) || parts.ValueKind != JsonValueKind.Array) throw ProtocolError();
					int contentIndex = 0;
					foreach (JsonElement part in parts.EnumerateArray())
					{
						string? text = String(part, "type") == "refusal" ? String(part, "refusal") : String(part, "text");
						if (text is not null)
						{
							string suffix = CompleteText(index, contentIndex, text);
							if (suffix.Length > 0) contents.Add(new TextContent(suffix));
						}
						contentIndex++;
					}
					break;
			}
			return contents;
		}

		internal List<AIContent> Finish(JsonElement response)
		{
			List<AIContent> contents = [];
			if (response.TryGetProperty("output", out JsonElement output))
			{
				if (output.ValueKind != JsonValueKind.Array) throw ProtocolError();
				int index = 0;
				foreach (JsonElement item in output.EnumerateArray()) contents.AddRange(AcceptItem(index++, item));
			}
			// 工具只在成功完成后交给宿主，避免失败或截断的流触发半成品调用。
			foreach (int index in _calls.Keys.Union(_reasoning.Keys).Order())
			{
				if (_reasoning.TryGetValue(index, out JsonElement reasoning))
				{
					string protectedData = reasoning.GetRawText();
					ReserveCharacters(protectedData.Length);
					contents.Add(new TextReasoningContent("") { ProtectedData = protectedData });
				}
				if (_calls.TryGetValue(index, out PendingCall? call)) contents.Add(call.ToContent());
			}
			if (response.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
			{
				UsageDetails details = new()
				{
					InputTokenCount = Number(usage, "input_tokens"), OutputTokenCount = Number(usage, "output_tokens"),
					TotalTokenCount = Number(usage, "total_tokens"),
				};
				if (usage.TryGetProperty("input_tokens_details", out JsonElement inputDetails)) details.CachedInputTokenCount = Number(inputDetails, "cached_tokens");
				if (usage.TryGetProperty("output_tokens_details", out JsonElement outputDetails)) details.ReasoningTokenCount = Number(outputDetails, "reasoning_tokens");
				details.TotalTokenCount ??= details.InputTokenCount + details.OutputTokenCount;
				contents.Add(new UsageContent(details));
			}
			return contents;
		}
	}

	private sealed class PendingCall
	{
		internal string? Id;
		internal string? Name;
		internal string? ItemId;
		internal readonly StringBuilder Arguments = new();
		internal bool Completed;

		internal FunctionCallContent ToContent()
		{
			if (!Completed || string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name)) throw ProtocolError();
			IDictionary<string, object?> arguments;
			try
			{
				arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(Arguments.Length == 0 ? "{}" : Arguments.ToString(), AIJsonUtilities.DefaultOptions) ?? throw ProtocolError();
			}
			catch (JsonException)
			{
				throw new InvalidOperationException("ChatGPT 返回的工具参数不是有效的 JSON 对象。");
			}
			FunctionCallContent content = new(Id, Name, arguments);
			if (ItemId is not null) content.AdditionalProperties = new() { [ItemIdKey] = ItemId };
			return content;
		}
	}
}
