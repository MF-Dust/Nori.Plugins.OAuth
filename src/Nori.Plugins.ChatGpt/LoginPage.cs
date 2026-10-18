using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
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

/// <summary>沿用宿主 Fluent 控件与动态主题资源，使用系统浏览器完成官方 OAuth 授权。</summary>
internal sealed class LoginPage : IPluginPage
{
	private static readonly Uri UsageSettingsUri = new("https://chatgpt.com/#settings/Usage");
	private readonly ChatGptAuth _auth;
	private readonly CancellationToken _stopping;
	private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
	private readonly TextBlock _account = new() { TextWrapping = TextWrapping.Wrap };
	private readonly ComboBox _accounts = new() { MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly Button _login = new() { Content = "Continue with ChatGPT" };
	private readonly Button _open = new() { Content = "重新打开授权页面", IsEnabled = false, Margin = new Thickness(0, 0, 8, 8) };
	private readonly Button _cancel = new() { Content = "取消登录", IsEnabled = false, Margin = new Thickness(0, 0, 8, 8) };
	private readonly Button _signOut = new() { Content = "退出登录", Margin = new Thickness(0, 0, 8, 8) };
	private readonly Button _clearStored = new() { Content = "清除无法恢复的凭证", Margin = new Thickness(0, 0, 8, 8) };
	private readonly Button _manageUsage = new() { Content = "Manage usage", Margin = new Thickness(0, 0, 8, 8) };
	private readonly StackPanel _root;
	private CancellationTokenSource? _loginCancellation;
	private Uri? _authorizationUri;
	private bool _isSigningOut;
	private bool _isClearingStored;
	private bool _disposed;

	internal LoginPage(ChatGptAuth auth, CancellationToken stopping)
	{
		_auth = auth;
		_stopping = stopping;
		_status.Text = ReadyStatus();
		_account.Text = AccountStatus();
		_signOut.IsEnabled = auth.IsSignedIn || auth.CanRetryPersistence;
		_clearStored.IsEnabled = auth.CanClearUnusableStoredData;
		_manageUsage.IsEnabled = auth.CanUseChatGptPlan;
		_accounts.ItemsSource = auth.LoginOptions;
		_accounts.SelectedIndex = 0;
		if (auth.ActiveAccountOption is { } active)
		{
			_accounts.SelectedItem = auth.LoginOptions.FirstOrDefault(option => option.ClientId == active.ClientId && option.Subject == active.Subject);
		}

		_login.Click += OnLogin;
		_open.Click += OnOpen;
		_cancel.Click += OnCancel;
		_signOut.Click += OnSignOut;
		_manageUsage.Click += OnManageUsage;
		_clearStored.Click += OnClearStored;
		_login.Classes.Add("accent");
		_signOut.Classes.Add("danger");
		_clearStored.Classes.Add("danger");
		foreach (Button button in new[] { _login, _open, _cancel, _signOut, _clearStored, _manageUsage }) button.MinHeight = 32;
		_status.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SettingsSecondaryBrush"));
		_status.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("NoriFontBase"));
		_account.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SettingsPrimaryBrush"));
		_account.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("NoriFontBase"));

		_root = new StackPanel
		{
			// 设置窗口已提供标题和页面边距，不再叠加第二层外壳。
			Spacing = 28, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch,
			Children =
			{
				Section("账号连接",
					Description("使用 ChatGPT plan 完成本应用中符合条件的 AI 请求；这些请求会使用 ChatGPT 计划额度或 credits，而不是 OpenAI API key 计费。"),
					_accounts,
					new WrapPanel { Orientation = Orientation.Horizontal, Children = { _login, _signOut, _clearStored, _manageUsage } },
					_account,
					_status,
					Description("旧版 Codex 或损坏凭证不会自动删除。只有明确点击“清除无法恢复的凭证”才会删除本地凭证；主机标识会保留。")),
				Section("浏览器授权",
					Description("授权将在系统浏览器中进行。确认后请返回此页；可重新打开授权页面或取消登录。"),
					new WrapPanel { Orientation = Orientation.Horizontal, Children = { _open, _cancel } }),
				Section("使用与隐私",
					Description("登录后，符合条件的 AI 请求将使用 ChatGPT plan 或 credits。请在 Manage usage 中查看并管理用量。"),
					Description("登录凭证加密保存在插件数据中，由 Windows 当前用户 DPAPI、macOS 钥匙串或 Linux 密钥环保护。退出登录会尝试撤销远端刷新凭证，然后清除此账号的本地令牌；已保存的账号注册和主机标识会保留。停用插件只清空内存。")),
			},
		};
		_login.Margin = new Thickness(0, 0, 8, 8);
		Control = new ScrollViewer { Content = _root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
	}

	public Control Control { get; }

	private string ReadyStatus()
	{
		if (_auth.RestoreIssue is { } issue) return issue;
		if (!_auth.IsSignedIn) return "尚未登录。选择已保存账号继续授权，或新增 ChatGPT 账号。";
		return _auth.CanUseChatGptPlan
			? "正在使用 ChatGPT plan：本应用中符合条件的 AI 请求会使用你的 ChatGPT 计划额度或 credits。"
			: "账号身份已验证，但授权未包含 chatgpt.tokens.use.direct 与 resource.invoke；ChatGPT plan 使用已停用，请重新登录并授予相关权限。";
	}

	private string AccountStatus()
	{
		if (_auth.ActiveAccountLabel is { Length: > 0 } account)
			return _auth.IsSignedIn ? $"当前账号：{account}" : $"已保存账号：{account}（需要重新登录）";
		return "当前没有活动账号。";
	}

	private static TextBlock Description(string text)
	{
		TextBlock block = new() { Text = text, TextWrapping = TextWrapping.Wrap };
		block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SettingsSecondaryBrush"));
		block.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("NoriFontXs"));
		return block;
	}

	private static StackPanel Section(string title, params Control[] controls)
	{
		TextBlock heading = new() { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
		heading.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SettingsPrimaryBrush"));
		heading.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("NoriFontBase"));
		StackPanel body = new() { Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
		foreach (Control control in controls) body.Children.Add(control);
		Border divider = new() { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 10, 0, 0), Child = body };
		divider.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("SettingsBorderBrush"));
		return new StackPanel { Children = { heading, divider } };
	}

	private async void OnLogin(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
	{
		if (_loginCancellation is not null || _isSigningOut || _isClearingStored) return;
		using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
		_loginCancellation = cancellation;
		_authorizationUri = null;
		_login.IsEnabled = false;
		_accounts.IsEnabled = false;
		_signOut.IsEnabled = false;
		_clearStored.IsEnabled = false;
		_cancel.IsEnabled = true;
		_open.IsEnabled = false;
		_status.Text = "正在启动 ChatGPT 授权…";
		try
		{
			await _auth.SignInAsync(_accounts.SelectedItem as ChatGptAccountOption, OnAuthorizationStarted, cancellation.Token);
			if (!_disposed)
			{
				RefreshAccountOptions();
				_status.Text = ReadyStatus();
				_account.Text = AccountStatus();
				_signOut.IsEnabled = _auth.IsSignedIn || _auth.CanRetryPersistence;
				_clearStored.IsEnabled = _auth.CanClearUnusableStoredData;
				_manageUsage.IsEnabled = _auth.CanUseChatGptPlan;
			}
		}
		catch (OperationCanceledException)
		{
			if (!_disposed) _status.Text = "登录已取消或超时；当前账号未更改，可以重新尝试。";
		}
		catch (Exception exception)
		{
			if (!_disposed) _status.Text = exception.Message;
		}
		finally
		{
			_loginCancellation = null;
			if (!_disposed)
			{
				_login.IsEnabled = true;
				_accounts.IsEnabled = true;
				_signOut.IsEnabled = _auth.IsSignedIn || _auth.CanRetryPersistence;
				_clearStored.IsEnabled = _auth.CanClearUnusableStoredData;
				_cancel.IsEnabled = false;
				_open.IsEnabled = false;
				_manageUsage.IsEnabled = _auth.CanUseChatGptPlan;
				_authorizationUri = null;
			}
		}
	}

	private void OnAuthorizationStarted(Uri uri)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (_disposed || _loginCancellation is null) return;
			_authorizationUri = uri;
			_open.IsEnabled = true;
			_status.Text = "请在系统浏览器中完成 ChatGPT 授权。若浏览器没有打开，可重新打开授权页面；登录最多等待 15 分钟。";
			OpenAuthorizationPage();
		});
	}

	private void OnOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => OpenAuthorizationPage();

	private void OpenAuthorizationPage()
	{
		if (_authorizationUri is null) return;
		try { Process.Start(new ProcessStartInfo(_authorizationUri.AbsoluteUri) { UseShellExecute = true }); }
		catch { _status.Text = "无法打开系统浏览器，请检查默认浏览器设置后重试。"; }
	}

	private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => _loginCancellation?.Cancel();

	private async void OnSignOut(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
	{
		if (_loginCancellation is not null || _isSigningOut || _isClearingStored) return;
		_isSigningOut = true;
		_login.IsEnabled = false;
		_accounts.IsEnabled = false;
		_signOut.IsEnabled = false;
		_clearStored.IsEnabled = false;
		_manageUsage.IsEnabled = false;
		try
		{
			ChatGptSignOutResult result = await _auth.SignOutAsync(_stopping);
			if (!_disposed)
			{
				RefreshAccountOptions();
				_status.Text = result.RemoteRevocationConfirmed switch
				{
					true => "已退出登录，远端撤销已确认；账号注册信息已保留。",
					false => "已在本地退出登录，但未确认远端撤销。可在 ChatGPT 设置中断开此应用；账号注册信息已保留。",
					_ => "本地没有可撤销的令牌；账号注册信息已保留。",
				};
				_account.Text = AccountStatus();
				_manageUsage.IsEnabled = false;
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { if (!_disposed) _status.Text = exception.Message; }
		finally
		{
			_isSigningOut = false;
			if (!_disposed)
			{
				_login.IsEnabled = true;
				_accounts.IsEnabled = true;
				_signOut.IsEnabled = _auth.IsSignedIn || _auth.CanRetryPersistence;
				_clearStored.IsEnabled = _auth.CanClearUnusableStoredData;
				_manageUsage.IsEnabled = _auth.CanUseChatGptPlan;
			}
		}
	}

	private async void OnClearStored(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
	{
		if (_loginCancellation is not null || _isSigningOut || _isClearingStored) return;
		_isClearingStored = true;
		_login.IsEnabled = false;
		_accounts.IsEnabled = false;
		_signOut.IsEnabled = false;
		_clearStored.IsEnabled = false;
		_manageUsage.IsEnabled = false;
		try
		{
			await _auth.ClearUnusableStoredDataAsync(_stopping);
			if (!_disposed)
			{
				RefreshAccountOptions();
				_account.Text = AccountStatus();
				_status.Text = "已清除无法恢复的本地凭证。远端撤销无法确认；如需断开旧应用授权，请在 ChatGPT 设置中操作。主机标识已保留。";
				_signOut.IsEnabled = false;
				_manageUsage.IsEnabled = false;
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { if (!_disposed) _status.Text = exception.Message; }
		finally
		{
			_isClearingStored = false;
			if (!_disposed)
			{
				_login.IsEnabled = true;
				_accounts.IsEnabled = true;
				_signOut.IsEnabled = _auth.IsSignedIn || _auth.CanRetryPersistence;
				_clearStored.IsEnabled = _auth.CanClearUnusableStoredData;
				_manageUsage.IsEnabled = _auth.CanUseChatGptPlan;
			}
		}
	}

	private void OnManageUsage(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
	{
		try { Process.Start(new ProcessStartInfo(UsageSettingsUri.AbsoluteUri) { UseShellExecute = true }); }
		catch { _status.Text = "无法打开 ChatGPT Usage 设置，请手动访问 ChatGPT 设置中的 Usage 页面。"; }
	}

	private void RefreshAccountOptions()
	{
		ChatGptAccountOption? active = _auth.ActiveAccountOption;
		IReadOnlyList<ChatGptAccountOption> options = _auth.LoginOptions;
		_accounts.ItemsSource = options;
		_accounts.SelectedItem = active is null
			? options[0]
			: options.FirstOrDefault(option => option.ClientId == active.ClientId && option.Subject == active.Subject) ?? options[0];
	}

	public void Dispose()
	{
		_disposed = true;
		_loginCancellation?.Cancel();
		_login.Click -= OnLogin;
		_open.Click -= OnOpen;
		_cancel.Click -= OnCancel;
		_signOut.Click -= OnSignOut;
		_manageUsage.Click -= OnManageUsage;
		_clearStored.Click -= OnClearStored;
		_root.Children.Clear();
	}
}
