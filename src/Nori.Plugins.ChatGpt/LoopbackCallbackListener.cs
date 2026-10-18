using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nori.Plugins.ChatGpt;

internal sealed class LoopbackCallbackListener : IAsyncDisposable
{
	private const int MaximumHeaderBytes = 16 * 1024;
	private readonly TcpListener _listener;
	private readonly string _expectedHost;
	private bool _disposed;

	private LoopbackCallbackListener(TcpListener listener, int port)
	{
		_listener = listener;
		_expectedHost = $"127.0.0.1:{port}";
		RedirectUri = new Uri($"http://127.0.0.1:{port}{OpenAiOAuthProtocol.CallbackPath}", UriKind.Absolute);
	}

	internal Uri RedirectUri { get; }

	internal static LoopbackCallbackListener Start()
	{
		TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start(1);
		int port = ((IPEndPoint)listener.LocalEndpoint).Port;
		return new LoopbackCallbackListener(listener, port);
	}

	internal async Task<LoopbackCallbackRequest> AcceptAsync(CancellationToken cancellationToken)
	{
		while (true)
		{
			TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
			NetworkStream? stream = null;
			using CancellationTokenSource headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			headerTimeout.CancelAfter(TimeSpan.FromSeconds(5));
			try
			{
				stream = client.GetStream();
				string target = await ReadRequestAsync(stream, headerTimeout.Token).ConfigureAwait(false);
				return new LoopbackCallbackRequest(client, stream, target);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				client.Dispose();
				throw;
			}
			catch (Exception exception) when (exception is InvalidDataException or IOException or SocketException or OperationCanceledException)
			{
				try
				{
					if (stream is not null) await TryRespondAsync(stream, false, cancellationToken).ConfigureAwait(false);
				}
				finally { client.Dispose(); }
			}
			catch
			{
				client.Dispose();
				throw;
			}
		}
	}

	private async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		int totalBytes = 0;
		string requestLine;
		(requestLine, totalBytes) = await ReadLineAsync(stream, totalBytes, cancellationToken).ConfigureAwait(false);
		string[] requestParts = requestLine.Split(' ');
		if (requestParts.Length != 3 || requestParts[0] != "GET" || requestParts[2] != "HTTP/1.1" ||
			requestParts[1].Length > 16 * 1024 || !requestParts[1].StartsWith(OpenAiOAuthProtocol.CallbackPath, StringComparison.Ordinal))
			throw new InvalidDataException();

		bool hasHost = false;
		while (true)
		{
			string line;
			(line, totalBytes) = await ReadLineAsync(stream, totalBytes, cancellationToken).ConfigureAwait(false);
			if (line.Length == 0) break;
			int separator = line.IndexOf(':');
			if (separator <= 0) throw new InvalidDataException();
			string name = line[..separator].Trim();
			string value = line[(separator + 1)..].Trim();
			if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
			{
				if (hasHost || !string.Equals(value, _expectedHost, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
				hasHost = true;
			}
			else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && value != "0" ||
				name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException();
			}
		}

		if (!hasHost) throw new InvalidDataException();
		return requestParts[1];
	}

	private static async Task<(string Line, int TotalBytes)> ReadLineAsync(NetworkStream stream, int totalBytes, CancellationToken cancellationToken)
	{
		using MemoryStream line = new();
		byte[] one = new byte[1];
		while (true)
		{
			int read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
			if (read == 0) throw new IOException();
			if (++totalBytes > MaximumHeaderBytes) throw new InvalidDataException();
			if (one[0] == '\n')
			{
				if (line.Length == 0 || line.GetBuffer()[line.Length - 1] != '\r') throw new InvalidDataException();
				line.SetLength(line.Length - 1);
				return (Encoding.ASCII.GetString(line.GetBuffer(), 0, checked((int)line.Length)), totalBytes);
			}
			if (one[0] > 0x7f || one[0] == 0) throw new InvalidDataException();
			line.WriteByte(one[0]);
			if (line.Length > MaximumHeaderBytes) throw new InvalidDataException();
		}
	}

	private static async Task TryRespondAsync(NetworkStream stream, bool accepted, CancellationToken cancellationToken)
	{
		try { await LoopbackCallbackRequest.RespondAsync(stream, accepted, cancellationToken).ConfigureAwait(false); }
		catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
	}

	public ValueTask DisposeAsync()
	{
		if (!_disposed)
		{
			_disposed = true;
			_listener.Stop();
		}
		return ValueTask.CompletedTask;
	}
}

internal sealed class LoopbackCallbackRequest(TcpClient client, NetworkStream stream, string requestTarget) : IAsyncDisposable
{
	private int _responded;
	internal string RequestTarget { get; } = requestTarget;

	internal async Task RespondAsync(bool accepted, CancellationToken cancellationToken)
	{
		if (Interlocked.Exchange(ref _responded, 1) != 0) return;
		try { await RespondAsync(stream, accepted, cancellationToken).ConfigureAwait(false); }
		finally { client.Dispose(); }
	}

	internal static async Task RespondAsync(NetworkStream stream, bool accepted, CancellationToken cancellationToken)
	{
		string bodyText = accepted
			? "ChatGPT 授权已收到。您可以关闭此页面并返回 Nori Desktop。"
			: "授权回调无效或已过期。请返回 Nori Desktop 重新登录。";
		byte[] body = Encoding.UTF8.GetBytes(bodyText);
		string header = $"HTTP/1.1 {(accepted ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
		await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	public ValueTask DisposeAsync()
	{
		client.Dispose();
		return ValueTask.CompletedTask;
	}
}
