using System.Text;
using DeveAgentManager.Client.Shared;
using DeveAgentManager.Core.Sessions;
using Xunit;

namespace DeveAgentManager.Tests;

public sealed class SessionFilePreviewTests
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    public async Task PreservesUnicodeWhitespaceAndOriginalMarkdown(string kind)
    {
        var encoding = kind switch { "utf16-le" => Encoding.Unicode, "utf16-be" => Encoding.BigEndianUnicode,
            "utf8-bom" => new UTF8Encoding(true), _ => new UTF8Encoding(false) };
        const string text = "# Read me\r\n\tHello ✓ 🌍\n";
        var result = await PreviewAsync([.. encoding.GetPreamble(), .. encoding.GetBytes(text)], "report.MD");
        Assert.Equal(text, result.Text);
        Assert.True(result.Markdown);
        Assert.Null(result.UnavailableReason);
    }

    [Fact]
    public async Task BinaryInvalidEncodingAndLargeFilesKeepDownloadInsteadOfRendering()
    {
        foreach (var bytes in new byte[][] { [0x50, 0x4b, 0, 3], [0xff, 0xfe, 0], [0xff, 0xff], new byte[SessionFilePreviewReader.MaxBytes + 1] })
        {
            var result = await PreviewAsync(bytes, "claimed-text.md");
            Assert.Null(result.Text);
            Assert.Contains("Download", result.UnavailableReason!);
        }
        var boundary = await PreviewAsync(Encoding.UTF8.GetBytes(new string('x', SessionFilePreviewReader.MaxBytes)), "log.txt");
        Assert.Equal(SessionFilePreviewReader.MaxBytes, boundary.Text!.Length);
        Assert.False(boundary.Markdown);
    }

    [Fact]
    public void MarkdigRendersTablesListsAndCodeWithoutExecutingFileContent()
    {
        var html = MarkdownRenderer.FileToHtml("""
            # Results

            | Name | Result |
            | --- | --- |
            | Test | Pass |

            - [x] Checked

            ```html
            <script>untrusted()</script>
            ```

            <img src=x onerror=untrusted()>
            [Unsafe](javascript:untrusted())
            [Relative](../../../account/logout)
            [Good](https://example.com/report#results)
            """).Value;
        Assert.Contains("<table>", html);
        Assert.Contains("<h1", html);
        Assert.Contains("<pre><code", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.DoesNotContain("href=\"../../../", html);
        Assert.Contains("href=\"https://example.com/report#results\"", html);
        Assert.Contains("noopener noreferrer", html);
    }

    private static async Task<DeveAgentManager.Contracts.SessionFilePreview> PreviewAsync(byte[] bytes, string name)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, bytes, TestContext.Current.CancellationToken);
            return await SessionFilePreviewReader.ReadAsync(file, name, TestContext.Current.CancellationToken);
        }
        finally { File.Delete(file); }
    }
}
