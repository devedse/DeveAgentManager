using System.Diagnostics;
using DeveAgentManager.Core.Agents;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Finding a project's icon: the ranking (on the image lists of real repositories) and the scan on a Runner.</summary>
public sealed class RepoIconTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dam-icons-").FullName;

    private static RepoIconFile F(string path, double? w, double? h, long bytes = 1000) => new(path, bytes, w, h);

    [Fact]
    public void Picks_TheFavicon_OfAWebApp() =>
        Assert.Equal("DeveAgentManager/wwwroot/favicon.svg", RepoIcons.Pick(new([F("DeveAgentManager/wwwroot/favicon.svg", 64, 64)], []), "DeveAgentManager")?.Path);

    [Fact]
    public void Picks_TheProjectsIcon_NotItsScreenshotsOrSplashScreens()
    {
        // From DeveMazeGeneratorCore: icons referenced by the projects, store art and screenshots.
        var scan = new RepoIconScan(
        [
            F("DeveMazeGeneratorCore/IconBig.png", 120, 120, 456),
            F("DeveMazeGeneratorCore/Icon.ico", 120, 120, 17406),
            F("Images/Icon400_400_With_512_512border.png", 512, 512),
            F("Images/AppIcon_FireTV_1280x720.png", 1280, 720),
            F("Images/ScreenShotDeveMazeGeneratorMonoGame_1920x1080_1.png", 1920, 1080),
            F("Images/FunctieAfbeelding.png", 1024, 500),
            F("DeveMazeGeneratorCore.MonoGame.WindowsUWP/Assets/SplashScreen.scale-200.png", 1240, 600),
            F("DeveMazeGeneratorCore.MonoGame.WindowsUWP/Assets/WideTile.scale-100.png", 310, 150),
        ],
        [
            new("DeveMazeGeneratorCore/DeveMazeGeneratorCore.csproj", "IconBig.png"),
            new("DeveMazeGeneratorCore/DeveMazeGeneratorCore.csproj", "Icon.ico"),
        ]);

        var pick = RepoIcons.Pick(scan, "DeveMazeGeneratorCore")?.Path;
        Assert.Equal("DeveMazeGeneratorCore/IconBig.png", pick);
        var ranked = RepoIcons.Rank(scan, "DeveMazeGeneratorCore");
        Assert.All(ranked.Where(r => r.File.Path.Contains("Screen", StringComparison.Ordinal) || r.File.Path.Contains("Splash", StringComparison.Ordinal)), r => Assert.False(r.Eligible));
    }

    [Fact]
    public void Picks_TheIconTheProjectNames_OverABigPngOfTheSameName()
    {
        // From QueueCache: the csproj's ApplicationIcon (256 px) and a 1254 px PNG next to it.
        var scan = new RepoIconScan(
            [F("assets/branding/queuecache.ico", 256, 256, 134845), F("assets/branding/queuecache.png", 1254, 1254, 1054107)],
            [new("src/QueueCache.Cli/QueueCache.Cli.csproj", "../../assets/branding/queuecache.ico")]);
        Assert.Equal("assets/branding/queuecache.ico", RepoIcons.Pick(scan, "https://github.com/devedse/QueueCache.git".Split('/').Last())?.Path);
    }

    [Fact]
    public void Prefers_ThePng_OfTheIconAProjectNames_WhenBothExist()
    {
        var scan = new RepoIconScan([F("assets/app.ico", 256, 256), F("assets/app.png", 256, 256)], [new("src/App/App.csproj", "../../assets/app.ico")]);
        Assert.Equal("assets/app.png", RepoIcons.Pick(scan, "something")?.Path);
    }

    [Fact]
    public void Picks_Nothing_WhenNoImageLooksLikeAnIcon()
    {
        var scan = new RepoIconScan([F("docs/architecture.png", 1600, 900), F("tests/fixtures/sample.jpg", 64, 64), F("src/photo.jpg", 400, 300)], []);
        Assert.Null(RepoIcons.Pick(scan, "myproject"));
    }

    [Fact]
    public void Picks_AnImageNamedLikeTheProject()
    {
        var scan = new RepoIconScan([F("assets/myproject.png", 128, 128), F("assets/diagram.png", 128, 128)], []);
        Assert.Equal("assets/myproject.png", RepoIcons.Pick(scan, "my-project")?.Path);
    }

    [Theory]
    [InlineData("assets/icon.png", true)]
    [InlineData("icon.svg", true)]
    [InlineData("../icon.png", false)]
    [InlineData("a/../../icon.png", false)]
    [InlineData("/etc/icon.png", false)]
    [InlineData("~/icon.png", false)]
    [InlineData("a\\icon.png", false)]
    [InlineData("assets/icon.txt", false)]
    [InlineData("assets//icon.png", false)]
    [InlineData("", false)]
    public void OnlyImagesInsideTheFolder_CanBeRead(string path, bool safe) => Assert.Equal(safe, RepoIcons.IsSafePath(path));

    [Fact]
    public void Scan_ReadsSizesAndReferences_AndSkipsDependencies()
    {
        if (OperatingSystem.IsWindows() || !HasNode())
        {
            return;
        }

        Write("branding/app.png", Png(48, 48));
        Write("src/App/App.ico", Ico((0, 0), (32, 32)));
        Write("wwwroot/favicon.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 32"><rect/></svg>"""u8.ToArray());
        Write("node_modules/pkg/logo.png", Png(10, 10));
        Write(".git/logo.png", Png(10, 10));
        Write("src/App/App.csproj", "<Project><PropertyGroup><ApplicationIcon>App.ico</ApplicationIcon></PropertyGroup></Project>"u8.ToArray());
        Write("package.json", """{ "name": "x", "icon": "branding/app.png" }"""u8.ToArray());

        var start = new ProcessStartInfo("bash", ["-c", RepoIcons.ScanScript(_folder)]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);

        var scan = RepoIcons.ParseScan(output);
        Assert.Equal(["branding/app.png", "src/App/App.ico", "wwwroot/favicon.svg"], scan.Files.Select(f => f.Path).Order().ToArray());
        Assert.Equal((48d, 48d), (scan.Files.Single(f => f.Path.EndsWith(".png")).Width!.Value, scan.Files.Single(f => f.Path.EndsWith(".png")).Height!.Value));
        Assert.Equal(256, scan.Files.Single(f => f.Path.EndsWith(".ico")).Width);
        Assert.Equal((64d, 32d), (scan.Files.Single(f => f.Path.EndsWith(".svg")).Width!.Value, scan.Files.Single(f => f.Path.EndsWith(".svg")).Height!.Value));
        Assert.Contains(scan.References, r => r.From == "src/App/App.csproj" && r.Target == "App.ico");
        Assert.Contains(scan.References, r => r.From == "package.json" && r.Target == "branding/app.png");
    }

    private void Write(string path, byte[] content)
    {
        var full = Path.Combine(_folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    /// <summary>Enough of a PNG for its size: the signature and the IHDR chunk.</summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    /// <summary>An ICO directory with these sizes (0 means 256).</summary>
    private static byte[] Ico(params (byte W, byte H)[] sizes)
    {
        var bytes = new byte[6 + 16 * sizes.Length];
        bytes[2] = 1;
        bytes[4] = (byte)sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            bytes[6 + 16 * i] = sizes[i].W;
            bytes[7 + 16 * i] = sizes[i].H;
        }

        return bytes;
    }

    private static bool HasNode()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("node", ["--version"]) { RedirectStandardOutput = true })!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
