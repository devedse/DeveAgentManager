using System.Text.Json;

namespace DeveAgentManager.Core.Agents;

/// <summary>Reads only the next archive entry's metadata; file contents and the full index never enter memory.</summary>
internal sealed class MoveTransferIndex(string path) : IDisposable
{
    private readonly StreamReader _reader = File.OpenText(path);
    private long _end;
    private string _file = "bundle.tar";
    private bool _finished;

    internal string FileAt(long completed)
    {
        while (!_finished && (_end == 0 || completed > _end))
        {
            var line = _reader.ReadLine();
            if (line is null) { _finished = true; break; }
            using var entry = JsonDocument.Parse(line);
            _end = entry.RootElement[0].GetInt64();
            _file = entry.RootElement[1].GetString() ?? "bundle.tar";
        }
        return _file;
    }

    public void Dispose() => _reader.Dispose();
}
