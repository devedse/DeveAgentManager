using DeveAgentManager.Logging;
using Microsoft.Extensions.Logging;
using Xunit;
namespace DeveAgentManager.Tests;
public sealed class ManagerLogStoreTests
{
    [Fact] public void LogsAreFilteredRedactedAndSurviveRestart()
    {
        var directory = Directory.CreateTempSubdirectory("dam-logs-");
        try
        {
            var store = new ManagerLogStore(directory.FullName);
            store.Append(LogLevel.Information, "ordinary", "startup");
            store.Append(LogLevel.Error, "jobs", "password=sample-secret Authorization: Bearer sample-token\nexception details");
            var entry = Assert.Single(store.Read(LogLevel.Warning, "jobs", 100));
            Assert.DoesNotContain("sample-secret", entry.Message);
            Assert.DoesNotContain("sample-token", entry.Message);
            Assert.Contains("exception details", entry.Message);
            var reloaded = new ManagerLogStore(directory.FullName);
            Assert.Equal(entry, Assert.Single(reloaded.Read(LogLevel.Warning, "jobs", 100)));
            Assert.Equal(2, reloaded.Read(LogLevel.Trace, null, 100).Count);
        }
        finally { directory.Delete(true); }
    }
    [Fact] public void LogBufferAndMessagesAreBounded()
    {
        var directory = Directory.CreateTempSubdirectory("dam-logs-");
        try
        {
            var store = new ManagerLogStore(directory.FullName);
            store.Append(LogLevel.Error, "test", new string('x', 9000));
            Assert.True(Assert.Single(store.Read(LogLevel.Trace, null, 1)).Message.Length < 9000);
            for (var i = 0; i < 5001; i++) store.Append(LogLevel.Information, "test", i.ToString());
            var entries = store.Read(LogLevel.Trace, null, 1000);
            Assert.Equal(1000, entries.Count);
            Assert.Equal("5000", entries[0].Message);
        }
        finally { directory.Delete(true); }
    }
}
