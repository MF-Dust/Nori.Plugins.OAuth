using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class LoginPageTests
{
	private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(LoginPageTests), AvaloniaTestIsolationLevel.PerTest));

	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();

	public sealed class TestApp : Application
	{
		public override void Initialize()
		{
			RequestedThemeVariant = ThemeVariant.Dark;
			Styles.Add(new FluentTheme());
		}
	}

	[Theory]
	[InlineData(720, 480)]
	[InlineData(1920, 1080)]
	public Task LoginPageFitsAndCanScrollWithoutNetwork(int width, int height) => Session.Value.Dispatch(() =>
	{
		using HttpClient http = new(new NoNetwork());
		using LoginPage page = new(new ChatGptAuth(http), default);
		Window window = new() { Width = width, Height = height, Content = page.Control };
		try
		{
			window.Show();
			window.UpdateLayout();
			ScrollViewer scroll = Assert.IsType<ScrollViewer>(page.Control);
			Assert.True(scroll.Viewport.Height > 0);
			Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
			Assert.Contains(page.Control.GetLogicalDescendants().OfType<Button>(), item => Equals(item.Content, "登录 ChatGPT") && item.IsEnabled);
			TextBox code = Assert.Single(page.Control.GetLogicalDescendants().OfType<TextBox>());
			Assert.True(code.IsReadOnly);
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
