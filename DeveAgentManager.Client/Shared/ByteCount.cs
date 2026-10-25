namespace DeveAgentManager.Client.Shared;

/// <summary>File and disk sizes for people: 693 KB, 38 MB, 14 GB.</summary>
public static class ByteCount
{
    public static string Text(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} bytes",
    };
}
