using DeveAgentManager.Core.Runners;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Files pasted into a terminal (images, logs, …) are checked and named before anything is sent to a Runner.</summary>
public class ImageUploadTests
{
    // The checks run before the database or a Runner is used.
    private static TerminalService Service() => new(null!, null!, null!, null!);
    private static readonly DateTime Now = new(2026, 10, 11, 1, 2, 3);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0];

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0 }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "image/gif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0 }, null)]
    public void DetectsTheTypeFromTheContent(byte[] bytes, string? type) => Assert.Equal(type, TerminalService.DetectImageType(bytes));

    [Fact]
    public void Images_GetAFreshNameWithTheirRealExtension_WhateverTheyWereCalled()
    {
        Assert.Matches("^20261011-010203-[0-9a-f]{8}\\.png$", TerminalService.UploadName("Screenshot 2026.jpg", Png, Now));
        Assert.Matches("^20261011-010203-[0-9a-f]{8}\\.png$", TerminalService.UploadName(null, Png, Now));
    }

    [Fact]
    public void OtherFiles_KeepTheirNameInAFolderOfTheirOwn()
    {
        Assert.Matches("^20261011-010203-[0-9a-f]{8}/manager\\.log$", TerminalService.UploadName("manager.log", "started"u8, Now));
        // An SVG or BMP isn't sent to the model as an image; the agent can still open the file.
        Assert.Matches("/drawing\\.svg$", TerminalService.UploadName("drawing.svg", "<svg></svg>"u8, Now));
        Assert.NotEqual(TerminalService.UploadName("a.log", "x"u8, Now), TerminalService.UploadName("a.log", "x"u8, Now));
    }

    [Theory]
    [InlineData("manager.log", "manager.log")]
    [InlineData("my log (1).txt", "my-log-1-.txt")]
    [InlineData("C:\\Users\\me\\Downloads\\app.log", "app.log")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("rm -rf $(whoami); `x`.sh", "rm--rf-whoami-x-.sh")]
    [InlineData("..", "file")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    public void FileNames_BecomeOnePathWithoutFoldersOrShellCharacters(string? name, string expected) => Assert.Equal(expected, TerminalService.SafeFileName(name));

    [Fact]
    public void LongFileNames_AreCutKeepingTheirExtension()
    {
        var name = TerminalService.SafeFileName(new string('a', 300) + ".log");
        Assert.Equal(100, name.Length);
        Assert.EndsWith(".log", name);
    }

    [Fact]
    public async Task RefusesEmptyAndTooBigFiles()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().UploadAsync(null, Guid.NewGuid(), "a.log", new MemoryStream(), CancellationToken.None));
        // Refused on its length, before anything is read.
        await Assert.ThrowsAsync<ArgumentException>(() => Service().UploadAsync(null, Guid.NewGuid(), "a.log", new LengthOnly(TerminalService.MaxUploadBytes + 1),
            CancellationToken.None));
        Assert.Equal(500L * 1024 * 1024, TerminalService.MaxUploadBytes);
    }

    [Fact]
    public async Task AFileWaitsOnDiskWhileItArrives_AndIsGoneOnceClosed()
    {
        var data = Directory.CreateTempSubdirectory("dam-uploads-").FullName;
        try
        {
            var options = new DeveAgentManager.Core.Configuration.ManagerOptions { DataDirectory = data };
            var service = new TerminalService(null!, null!, null!, null!, options);
            string path;
            await using (var buffer = service.NewUploadBuffer())
            {
                path = buffer.Name;
                await buffer.WriteAsync(Png);
                Assert.StartsWith(options.TerminalUploadsDirectory, path);
                Assert.True(File.Exists(path));
            }

            Assert.False(File.Exists(path));
            // One left behind by a crash is removed at the next start.
            File.WriteAllText(Path.Combine(options.TerminalUploadsDirectory, "left-over"), "x");
            TerminalService.ClearUploadBuffers(options);
            Assert.False(Directory.Exists(options.TerminalUploadsDirectory));
        }
        finally
        {
            Directory.Delete(data, true);
        }
    }

    private sealed class LengthOnly(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Read before the size check.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
