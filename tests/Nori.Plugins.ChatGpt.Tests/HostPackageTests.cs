using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Nori.PluginRuntime;

namespace Nori.Plugins.ChatGpt.Tests;

public sealed class HostPackageTests
{
	[Fact]
	public void PackageInstallsWithActualHostValidator()
	{
		string temporary = Path.Combine(Path.GetTempPath(), "nori-oauth-package-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(temporary);
		try
		{
			string? package = Environment.GetEnvironmentVariable("NORI_TEST_PLUGIN_PACKAGE");
			if (string.IsNullOrEmpty(package))
			{
				package = Path.Combine(temporary, "fixture.noripack");
				using ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create);
				using (StreamWriter writer = new(archive.CreateEntry("manifest.json").Open())) writer.Write(ManifestTests.ReadManifest());
				archive.CreateEntryFromFile(typeof(Plugin).Assembly.Location, "lib/Nori.Plugins.ChatGpt.dll");
				archive.CreateEntryFromFile(typeof(ProtectedData).Assembly.Location, "lib/System.Security.Cryptography.ProtectedData.dll");
				string output = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
				foreach (string dependency in new[] { "Microsoft.IdentityModel.JsonWebTokens", "Microsoft.IdentityModel.Tokens", "Microsoft.IdentityModel.Logging", "Microsoft.IdentityModel.Abstractions" })
					archive.CreateEntryFromFile(Path.Combine(output, dependency + ".dll"), "lib/" + dependency + ".dll");
			}

			using (ZipArchive archive = ZipFile.OpenRead(package))
			{
				string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();
				Assert.Contains("lib/Microsoft.IdentityModel.JsonWebTokens.dll", entries);
				Assert.Contains("lib/Microsoft.IdentityModel.Tokens.dll", entries);
				Assert.Contains("lib/Microsoft.IdentityModel.Logging.dll", entries);
				Assert.Contains("lib/Microsoft.IdentityModel.Abstractions.dll", entries);
				Assert.DoesNotContain(entries, entry => entry.EndsWith("/Nori.PluginRuntime.dll", StringComparison.OrdinalIgnoreCase));
				Assert.DoesNotContain(entries, entry => entry.EndsWith("/Nori.PluginSDK.dll", StringComparison.OrdinalIgnoreCase));
			}

			// 安装器是宿主内部类型；测试调用真实校验实现，不复制一套宽松的规则。
			Type installerType = typeof(INoriPlugin).Assembly.GetType("Nori.PluginRuntime.PluginPackageInstaller", throwOnError: true)!;
			object installer = Activator.CreateInstance(installerType, [Path.Combine(temporary, "installed"), null, null])!;
			try
			{
				object manifest = installerType.GetMethod("Install")!.Invoke(installer, [Path.GetFullPath(package), CancellationToken.None])!;
				string version = (string)manifest.GetType().GetProperty("Version")!.GetValue(manifest)!;
				Assert.True(File.Exists(Path.Combine(temporary, "installed", "io.nori.chatgpt", version, "lib", "Nori.Plugins.ChatGpt.dll")));
			}
			catch (TargetInvocationException exception) when (exception.InnerException is not null)
			{
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
				throw;
			}
		}
		finally { Directory.Delete(temporary, recursive: true); }
	}
}
