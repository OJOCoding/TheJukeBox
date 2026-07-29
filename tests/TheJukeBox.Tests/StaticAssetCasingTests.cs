using System.Text.RegularExpressions;

namespace TheJukeBox.Tests;

/// <summary>
/// Windows and macOS resolve static-file requests case-insensitively, so markup
/// referencing "/CSS/index.css" serves a 200 locally and a 404 in the Linux
/// container. Nothing in a local run surfaces that, which makes these tests the
/// only guard before deployment.
/// </summary>
public class StaticAssetCasingTests
{
    private static readonly Regex AssetReference = new(
        @"(?:href|src)\s*=\s*""(?<path>/[^""]+\.(?:css|js|mjs|png|jpg|svg|ico|webmanifest|woff2?))""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string WebRoot = FindWebRoot();

    public static TheoryData<string> HtmlFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(WebRoot, "*.html", SearchOption.AllDirectories))
        {
            data.Add(Path.GetRelativePath(WebRoot, file).Replace('\\', '/'));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HtmlFiles))]
    public void Asset_references_are_lowercase(string htmlFile)
    {
        var referenced = ReferencedAssets(htmlFile);

        var mixedCase = referenced
            .Where(path => path != path.ToLowerInvariant())
            .ToArray();

        Assert.Empty(mixedCase);
    }

    [Theory]
    [MemberData(nameof(HtmlFiles))]
    public void Referenced_assets_exist_with_exactly_that_casing(string htmlFile)
    {
        // File.Exists is case-insensitive on Windows, so compare against the real
        // directory entries with an ordinal set instead of asking the filesystem.
        var onDisk = Directory
            .EnumerateFiles(WebRoot, "*", SearchOption.AllDirectories)
            .Select(f => "/" + Path.GetRelativePath(WebRoot, f).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        var missing = ReferencedAssets(htmlFile)
            .Where(path => !onDisk.Contains(path))
            .ToArray();

        Assert.Empty(missing);
    }

    private static string[] ReferencedAssets(string htmlFile)
    {
        var html = File.ReadAllText(Path.Combine(WebRoot, htmlFile));

        var matches = AssetReference.Matches(html)
            .Select(m => m.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Both assertions above are Assert.Empty, so a regex that silently stopped
        // matching would make them pass vacuously. Every page in this app links at
        // least a stylesheet, so zero matches means the guard itself is broken.
        Assert.NotEmpty(matches);

        return matches;
    }

    private static string FindWebRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TheJukeBox", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate src/TheJukeBox/wwwroot walking up from {AppContext.BaseDirectory}.");
    }
}
