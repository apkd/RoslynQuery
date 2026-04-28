using System.Text.Json;
using static System.StringComparison;

namespace RoslynQuery;

public sealed class SelfUpdaterTests
{
    [Test]
    public async Task FindAssetReturnsMatchingBrowserDownloadUrl()
    {
        using var release = JsonDocument.Parse(
            """
            {
              "tag_name": "release",
              "assets": [
                {
                  "name": "roslynquery-linux-x64",
                  "browser_download_url": "https://github.com/apkd/RoslynQuery/releases/download/release/roslynquery-linux-x64"
                },
                {
                  "name": "roslynquery-win-x64.exe",
                  "browser_download_url": "https://github.com/apkd/RoslynQuery/releases/download/release/roslynquery-win-x64.exe"
                }
              ]
            }
            """
        );

        var asset = SelfUpdater.FindAsset(release.RootElement, "roslynquery-win-x64.exe");

        await Assert.That(asset?.Name).IsEqualTo("roslynquery-win-x64.exe");
        await Assert.That(asset?.DownloadUrl.ToString()).EndsWith("roslynquery-win-x64.exe", Ordinal);
    }

    [Test]
    public async Task FindAssetIgnoresAssetNameCase()
    {
        using var release = JsonDocument.Parse(
            """
            {
              "assets": [
                {
                  "name": "roslynquery-linux-x64",
                  "browser_download_url": "https://github.com/apkd/RoslynQuery/releases/download/release/roslynquery-linux-x64"
                }
              ]
            }
            """
        );

        var asset = SelfUpdater.FindAsset(release.RootElement, "ROSLYNQUERY-LINUX-X64");

        await Assert.That(asset?.Name).IsEqualTo("roslynquery-linux-x64");
    }

    [Test]
    public async Task FindAssetReturnsNullWhenAssetsAreMissing()
    {
        using var release = JsonDocument.Parse("""{ "tag_name": "release" }""");

        var asset = SelfUpdater.FindAsset(release.RootElement, "roslynquery-linux-x64");

        await Assert.That(asset).IsNull();
    }
}
