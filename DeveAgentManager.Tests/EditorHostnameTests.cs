using DeveAgentManager.Api;
using DeveAgentManager.Core.Configuration;
using Xunit;

namespace DeveAgentManager.Tests;

/// <summary>Editor hostnames: {runner} anywhere in the first label, so one wildcard (DNS, certificate, proxy) covers all Runners.</summary>
public sealed class EditorHostnameTests
{
    private static readonly Uri Click = new("https://dam.devedse.click");

    private static DeploymentBoundary Boundary(string editorUrl) => new(new ManagerOptions
    {
        PublicOrigins = ["https://dam.devedse.click", "https://dam.devedse.duckdns.org"],
        EditorUrl = editorUrl,
        DataDirectory = Path.GetTempPath(),
    });

    [Theory]
    // Flat, one level under the domain: covered by Cloudflare's free *.devedse.click certificate.
    [InlineData("https://dam-vscode-{runner}.devedse.click", "dam-vscode-runner-01.devedse.click", "runner-01")]
    [InlineData("https://dam-vscode-{runner}.devedse.click", "DAM-VSCODE-Runner-01.devedse.click", "runner-01")]
    [InlineData("https://dam-vscode-{runner}.devedse.click", "dam-vscode-a.b.devedse.click", null)]
    [InlineData("https://dam-vscode-{runner}.devedse.click", "dam-vscode-.devedse.click", null)]
    [InlineData("https://dam-vscode-{runner}.devedse.click", "dam.devedse.click", null)]
    [InlineData("https://dam-vscode-{runner}.devedse.click", "other.devedse.click", null)]
    [InlineData("https://dam-vscode-{runner}.devedse.click", "dam-vscode-runner-01.evil.click", null)]
    // Nested, the original form.
    [InlineData("https://{runner}.code.{host}", "runner-01.code.dam.devedse.click", "runner-01")]
    [InlineData("https://{runner}.code.{host}", "a.runner-01.code.dam.devedse.click", null)]
    [InlineData("https://{runner}.code.{host}", "dam.devedse.click", null)]
    public void FindsTheRunnerOnlyInItsOwnLabel(string editorUrl, string host, string? runner) =>
        Assert.Equal(runner, Boundary(editorUrl).EditorRunner(host, Click));

    [Theory]
    [InlineData("https://{runner}.devedse.click/x")] // a path
    [InlineData("https://code.{runner}.devedse.click")] // not the first label
    [InlineData("https://{runner}")] // no domain after it
    [InlineData("https://{runner}.{runner}.devedse.click")] // twice
    [InlineData("https://{runner}.devedse.click")] // the Portal dam.devedse.click would match
    [InlineData("https://code.devedse.click")] // one host for every Runner
    public void RejectsTemplatesThatAreNotOneHostPerRunner(string editorUrl) =>
        Assert.Throws<ArgumentException>(() => Boundary(editorUrl));

    [Theory]
    [InlineData("https://dam-vscode-{runner}.devedse.click", "https://*.devedse.click")]
    [InlineData("https://{runner}.code.dam.devedse.click", "https://*.code.dam.devedse.click")]
    [InlineData("http://{runner}.code.test:18082", "http://*.code.test:18082")]
    public void FrameSourceIsAValidCspWildcard(string editorUrl, string source) =>
        Assert.Equal(source, DeploymentBoundary.EditorFrameSource(editorUrl));
}
