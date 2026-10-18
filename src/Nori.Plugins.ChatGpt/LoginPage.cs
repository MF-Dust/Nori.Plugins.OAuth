using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.PluginRuntime;
using System.Diagnostics;

namespace Nori.Plugins.ChatGpt;

internal sealed class LoginPageContribution(ChatGptAuth auth, CancellationToken stopping) : IPluginPageContribution
{
	public string Id => "oauth";
	public string Title => "ChatGPT OAuth";
	public PluginPagePlacement Placement => PluginPagePlacement.Settings;
	public IPluginPage CreatePage() => new LoginPage(auth, stopping);
}

/// <summary>沿用宿主 Fluent 控件与主题；设备码由用户在系统浏览器中授权。</summary>
internal sealed class LoginPage : IPluginPage
{
	private readonly ChatGptAuth _auth;
	private readonly CancellationToken _stopping;
	private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
	private readonly TextBox _code = new() { IsReadOnly = true, PlaceholderText = "登录后显示设备码", HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly Button _login = new() { Content = "登录 ChatGPT" };
	private readonly Button _open = new() { Content = "打开授权网页", IsEnabled = false, Margin = new Thickness(0, 0, 8, 8) };
	private readonly Button _cancel = new() { Content = "取消登录", IsEnabled = false, Margin = new Thickness(0, 0, 8, 8) };
	private readonly Button _signOut = new() { Content = "退出登录", Margin = new Thickness(0, 0, 8, 8) };
	private readonly StackPanel _root;
	private CancellationTokenSource? _loginCancellation;
	private bool _disposed;

	internal LoginPage(ChatGptAuth auth, CancellationToken stopping)
	{
		_auth = auth;
		_stopping = stopping;
		Avalonia.Automation.AutomationProperties.SetName(_code, "ChatGPT 设备授权码");
		_status.Text = auth.RestoreIssue ?? (auth.IsSignedIn ? "已登录，可在模型服务中选择 ChatGPT (OAuth / Codex)。" : "尚未登录");
		_signOut.IsEnabled = auth.IsSignedIn || auth.RestoreIssue is not null;
		_login.Click += OnLogin;
		_open.Click += OnOpen;
		_cancel.Click += OnCancel;
		_signOut.Click += OnSignOut;
		_root = new StackPanel
		{
			Spacing = 16, Margin = new Thickness(24), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left,
			Children =
			{
				new TextBlock { Text = "连接 ChatGPT", FontSize = 24, FontWeight = FontWeight.SemiBold },
				new TextBlock { Text = "使用 ChatGPT 账号授权 Codex 模型，无需填写 OpenAI API 密钥。可用模型与额度取决于账号。", TextWrapping = TextWrapping.Wrap },
				_status,
				_login,
				new TextBlock { Text = "设备授权码（可选中复制）" },
				_code,
				new WrapPanel { Orientation = Orientation.Horizontal, Children = { _open, _cancel, _signOut } },
				new TextBlock { Text = "若授权网页拒绝设备码，请先在 ChatGPT → 设置 → 安全中启用设备代码授权。登录成功后，在 Nori 的模型服务中选择此提供商和模型。", TextWrapping = TextWrapping.Wrap },
				new TextBlock { Text = "登录凭证以加密形式保存在插件数据中，密钥由 Windows 当前用户 DPAPI、macOS 钥匙串或 Linux 密钥环保护。退出会清除此插件保存的登录状态，不会退出浏览器账号或撤销远端授权。", TextWrapping = TextWrapping.Wrap },
			},
		};
		Control = new ScrollViewer { Content = _root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
	}

	public Control Control { get; }

	private async void OnLogin(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
	{
		if (_loginCancellation is not null) return;
		using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
		_loginCancellation = cancellation;
		_login.IsEnabled = false;
		_signOut.IsEnabled = false;
		_cancel.IsEnabled = true;
		_status.Text = "正在申请设备授权码…";
		try
		{
			DeviceAuthorization device = await _auth.BeginLoginAsync(cancellation.Token);
			if (_disposed) return;
			_code.Text = device.UserCode;
			_open.IsEnabled = true;
			_status.Text = "请打开授权网页并输入设备码；此页将等待授权完成（最多 15 分钟）。";
			await _auth.CompleteLoginAsync(device, cancellation.Token);
			if (!_disposed) _status.Text = "已登录。请在模型服务中选择 ChatGPT (OAuth / Codex)。";
		}
		catch (OperationCanceledException)
		{
			if (!_disposed) _status.Text = "登录已取消或设备码已过期，可以重新登录。";
		}
		catch (Exception exception)
		{
			if (!_disposed) _status.Text = exception is HttpRequestException ? "无法连接 ChatGPT，请检查网络后重试。" : exception.Message;
		}
		finally
		{
			_loginCancellation = null;
			if (!_disposed)
			{
				_login.IsEnabled = true;
				_signOut.IsEnabled = _auth.IsSignedIn || _auth.RestoreIssue is not null;
				_cancel.IsEnabled = false;
				_open.IsEnabled = false;
				_code.Text = "";
			}
		}
	}

	private void OnOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
	{
		try { Process.Start(new ProcessStartInfo(ChatGptAuth.VerificationUrl) { UseShellExecute = true }); }
		catch { _status.Text = "无法打开系统浏览器，请手动访问 " + ChatGptAuth.VerificationUrl; }
	}

	private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => _loginCancellation?.Cancel();

	private async void OnSignOut(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
	{
		_login.IsEnabled = false;
		_signOut.IsEnabled = false;
		try
		{
			await _auth.SignOutAsync(_stopping);
			if (!_disposed) _status.Text = "已清除此插件的登录状态。";
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { if (!_disposed) _status.Text = exception.Message; }
		finally { if (!_disposed) { _login.IsEnabled = true; _signOut.IsEnabled = _auth.IsSignedIn || _auth.RestoreIssue is not null; } }
	}

	public void Dispose()
	{
		_disposed = true;
		_loginCancellation?.Cancel();
		_login.Click -= OnLogin;
		_open.Click -= OnOpen;
		_cancel.Click -= OnCancel;
		_signOut.Click -= OnSignOut;
		_root.Children.Clear();
	}
}
