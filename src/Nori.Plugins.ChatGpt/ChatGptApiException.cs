using System.Net;
using System.Text.Json;

namespace Nori.Plugins.ChatGpt;

/// <summary>保留可诊断的协议元数据，不把响应正文、账号或令牌带入错误。</summary>
internal static class ChatGptApiException
{
	internal static async Task<HttpRequestException> FromResponseAsync(HttpResponseMessage response, CancellationToken ct)
	{
		string? code = null;
		try
		{
			await response.Content.LoadIntoBufferAsync(64 * 1024, ct).ConfigureAwait(false);
			using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
			code = ErrorCode(document.RootElement);
		}
		catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException or InvalidOperationException) { }
		HttpRequestException error = new(Message(code, response.StatusCode), null, response.StatusCode);
		AttachMetadata(error, code, response);
		return error;
	}

	internal static InvalidOperationException FromStream(JsonElement item, HttpResponseMessage response)
	{
		JsonElement source = item.TryGetProperty("response", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object ? nested : item;
		string? code = ErrorCode(source);
		InvalidOperationException error = new(Message(code, null));
		AttachMetadata(error, code, response);
		return error;
	}

	private static string? ErrorCode(JsonElement source)
	{
		if (source.ValueKind != JsonValueKind.Object) return null;
		if (source.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object) source = error;
		return source.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.String ? SafeIdentifier(code.GetString()) : null;
	}

	private static string? SafeIdentifier(string? value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? value : null;

	private static void AttachMetadata(Exception error, string? code, HttpResponseMessage response)
	{
		if (code is not null) error.Data["openai.error_code"] = code;
		if (response.Headers.TryGetValues("x-request-id", out IEnumerable<string>? values) && SafeIdentifier(values.FirstOrDefault()) is { } id)
			error.Data["openai.request_id"] = id;
	}

	private static string Message(string? code, HttpStatusCode? status)
	{
		string advice = code switch
		{
			"subscription_sharing_user_not_eligible" => "当前账号或工作区不允许共享 ChatGPT 订阅额度。",
			"subscription_sharing_usage_limit_exceeded" => "此应用的 ChatGPT 额度已达上限，请在 ChatGPT 设置 → Usage 中检查用量和应用限额。",
			"subscription_sharing_unsupported_capability" => "此 ChatGPT 订阅通道不支持请求中的模型、参数或工具。",
			"subscription_sharing_route_not_supported" => "ChatGPT 订阅授权不支持此请求路径。",
			"chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context" => "此授权没有允许使用 ChatGPT 订阅额度，请检查授权范围。",
			"subscription_sharing_invalid_user" => "ChatGPT 授权身份无效，请重新登录。",
			"subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" => "ChatGPT 额度服务暂时不可用，请稍后重试。",
			_ => "请检查 ChatGPT 登录状态、授权范围或稍后重试。",
		};
		return status is { } httpStatus ? $"ChatGPT 请求失败（HTTP {(int)httpStatus}）。{advice}" : $"ChatGPT 生成回复失败。{advice}";
	}
}
