using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.Styling;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class LoginPageTests
{
	private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(LoginPageTests), AvaloniaTestIsolationLevel.PerTest));

	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Nori.Desktop.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();

	[Theory]
	[InlineData(720, 480)]
	[InlineData(1920, 1080)]
	public Task LoginPageFitsAndCanScrollWithoutNetwork(int width, int height) => Session.Value.Dispatch(() =>
	{
		using HttpClient http = new(new NoNetwork());
		using LoginPage page = new(new ChatGptAuth(http), default);
		Window window = new() { Width = width, Height = height, Content = new Border { Padding = new Thickness(28, 20, 28, 28), Child = page.Control }, RequestedThemeVariant = ThemeVariant.Dark };
		window.Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml"),
		});
		try
		{
			window.Show();
			window.UpdateLayout();
			ScrollViewer scroll = Assert.IsType<ScrollViewer>(page.Control);
			Assert.True(scroll.Viewport.Height > 0);
			Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
			Assert.Contains(page.Control.GetLogicalDescendants().OfType<Button>(), item => Equals(item.Content, "Continue with ChatGPT") && item.IsEnabled);
			Assert.Empty(page.Control.GetLogicalDescendants().OfType<TextBox>());
			Button login = Assert.Single(page.Control.GetLogicalDescendants().OfType<Button>(), item => Equals(item.Content, "Continue with ChatGPT"));
			Assert.Contains("accent", login.Classes);
			Assert.Equal(32, login.MinHeight);
			Assert.Equal(6, login.CornerRadius.TopLeft);
			Assert.Equal(13, login.FontSize);
			TextBlock heading = Assert.Single(page.Control.GetLogicalDescendants().OfType<TextBlock>(), item => item.Text == "账号连接");
			Assert.Equal(14, heading.FontSize);
			Assert.Equal(window.Foreground, heading.Foreground);
			Assert.Contains("danger", Assert.Single(page.Control.GetLogicalDescendants().OfType<Button>(), item => Equals(item.Content, "退出登录")).Classes);
			Assert.Contains(page.Control.GetLogicalDescendants().OfType<Button>(), item => Equals(item.Content, "Manage usage"));
			Assert.Contains(page.Control.GetLogicalDescendants().OfType<Button>(), item => Equals(item.Content, "重新打开授权页面"));
			Assert.Contains(page.Control.GetLogicalDescendants().OfType<Button>(), item => Equals(item.Content, "取消登录"));
			Assert.DoesNotContain(page.Control.GetLogicalDescendants().OfType<TextBlock>(), item => item.Text?.Contains("设备码", StringComparison.Ordinal) == true);
			string? directory = Environment.GetEnvironmentVariable("NORI_UI_CAPTURE_DIR");
			if (!string.IsNullOrEmpty(directory))
			{
				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				using var frame = window.CaptureRenderedFrame();
				Assert.NotNull(frame);
				Directory.CreateDirectory(directory);
				frame.Save(Path.Combine(directory, $"chatgpt-{width}x{height}.png"), PngBitmapEncoderOptions.Default);
			}
		}
		finally { window.Close(); }
	}, CancellationToken.None);

	private sealed class NoNetwork : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("创建登录页不得联网");
	}
}
