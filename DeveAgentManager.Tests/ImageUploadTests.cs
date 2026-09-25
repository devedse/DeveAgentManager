using DeveAgentManager.Core.Runners;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Images pasted into a terminal are checked before anything is sent to a Runner.</summary>
public class ImageUploadTests
{
    // The checks run before the database or a Runner is used.
    private static TerminalService Service() => new(null!, null!, null!);

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/bmp")]
    [InlineData("application/pdf")]
    [InlineData("")]
    public async Task RefusesTypesModelsCantRead(string contentType)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service().UploadImageAsync(null, Guid.NewGuid(), contentType, [1, 2, 3], CancellationToken.None));
        Assert.Contains("PNG, JPEG, WebP and GIF", error.Message);
    }

    [Fact]
    public async Task RefusesEmptyAndTooBigImages()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().UploadImageAsync(null, Guid.NewGuid(), "image/png", [], CancellationToken.None));
        var tooBig = new byte[TerminalService.MaxImageBytes + 1];
        await Assert.ThrowsAsync<ArgumentException>(() => Service().UploadImageAsync(null, Guid.NewGuid(), "image/png", tooBig, CancellationToken.None));
    }
}
