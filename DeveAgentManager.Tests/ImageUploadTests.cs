using DeveAgentManager.Core.Runners;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Images pasted into a terminal are checked before anything is sent to a Runner.</summary>
public class ImageUploadTests
{
    // The checks run before the database or a Runner is used.
    private static TerminalService Service() => new(null!, null!, null!);

    [Fact]
    public async Task RefusesContentThatIsNoImage_WhateverTheBrowserSays()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service().UploadImageAsync(null, Guid.NewGuid(), "image/png", "<svg></svg>"u8.ToArray(), CancellationToken.None));
        Assert.Contains("PNG, JPEG, WebP and GIF", error.Message);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0 }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "image/gif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0 }, null)]
    public void DetectsTheTypeFromTheContent(byte[] bytes, string? type) => Assert.Equal(type, TerminalService.DetectImageType(bytes));

    [Fact]
    public async Task RefusesEmptyAndTooBigImages()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().UploadImageAsync(null, Guid.NewGuid(), "image/png", [], CancellationToken.None));
        var tooBig = new byte[TerminalService.MaxImageBytes + 1];
        await Assert.ThrowsAsync<ArgumentException>(() => Service().UploadImageAsync(null, Guid.NewGuid(), "image/png", tooBig, CancellationToken.None));
    }
}
