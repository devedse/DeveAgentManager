using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeveAgentManager.Core.Agents;

/// <summary>An image in an agent's folder that could be its icon. <see cref="Path"/> is relative to the folder.</summary>
public sealed record RepoIconFile(string Path, long Bytes, double? Width, double? Height);

/// <summary>A project file that names its icon: csproj ApplicationIcon/PackageIcon, package.json, an HTML page, a web manifest.</summary>
public sealed record RepoIconReference(string From, string Target);

public sealed record RepoIconScan(IReadOnlyList<RepoIconFile> Files, IReadOnlyList<RepoIconReference> References);

/// <param name="Eligible">Good enough to be picked automatically (the picker lists the others too).</param>
public sealed record ScoredRepoIcon(RepoIconFile File, int Score, bool Eligible);

/// <summary>
/// Finds the icon of the project an agent works on. The Runner lists the images in the folder with their size (read
/// from the files' headers) and the icons project files point at (<see cref="ScanScript"/>); <see cref="Rank"/> scores
/// them. What counts: being named by a project file, a name like the repository's or like icon/logo/favicon, a place like
/// the root or an assets/branding folder, a square shape, and a small but not tiny size (the app icon rather than a
/// store banner). Screenshots, splash screens and banners count against.
/// </summary>
public static class RepoIcons
{
    public const long MaxBytes = 2 * 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
    };

    /// <summary>
    /// Bash that prints the scan of <paramref name="folder"/> as JSON (node is on every Runner). Skips dependency and build
    /// folders and hidden ones (but .github), goes 5 levels deep, and reads at most the first 64 KB of an image.
    /// </summary>
    public static string ScanScript(string folder) => $$"""
        node - {{Convert.ToBase64String(Encoding.UTF8.GetBytes(folder))}} <<'DAM_ICONS'
        const fs = require("fs");
        const path = require("path");
        const root = Buffer.from(process.argv[2], "base64").toString("utf8");
        const skip = new Set(["node_modules", ".git", "bin", "obj", "dist", "build", "out", "target", "vendor", "packages", ".venv", "venv",
          "__pycache__", ".next", ".nuxt", "coverage", "TestResults", "artifacts", ".idea", ".vs", "Pods", "DerivedData", ".gradle", ".dart_tool"]);
        const images = new Set([".png", ".ico", ".svg", ".jpg", ".jpeg", ".webp", ".gif"]);
        const files = [], references = [];
        let visited = 0;

        function size(file, ext, bytes) {
          const fd = fs.openSync(file, "r");
          try {
            const buf = Buffer.alloc(Math.min(bytes, 65536));
            fs.readSync(fd, buf, 0, buf.length, 0);
            switch (ext) {
              case ".png":
                return buf.length >= 24 && buf.toString("ascii", 12, 16) === "IHDR" ? [buf.readUInt32BE(16), buf.readUInt32BE(20)] : null;
              case ".gif":
                return buf.length >= 10 ? [buf.readUInt16LE(6), buf.readUInt16LE(8)] : null;
              case ".ico": {
                // The largest of the sizes inside (0 means 256).
                if (buf.length < 6 || buf.readUInt16LE(2) !== 1) return null;
                let best = null;
                for (let i = 0, n = buf.readUInt16LE(4); i < n && 8 + 16 * i <= buf.length; i++) {
                  const w = buf[6 + 16 * i] || 256, h = buf[7 + 16 * i] || 256;
                  if (!best || w * h > best[0] * best[1]) best = [w, h];
                }
                return best;
              }
              case ".jpg":
              case ".jpeg": {
                for (let i = 2; i + 9 < buf.length;) {
                  if (buf[i] !== 0xFF) { i++; continue; }
                  const marker = buf[i + 1];
                  if (marker >= 0xC0 && marker <= 0xCF && marker !== 0xC4 && marker !== 0xC8 && marker !== 0xCC) return [buf.readUInt16BE(i + 7), buf.readUInt16BE(i + 5)];
                  i += 2 + buf.readUInt16BE(i + 2);
                }
                return null;
              }
              case ".webp": {
                if (buf.length < 30 || buf.toString("ascii", 0, 4) !== "RIFF" || buf.toString("ascii", 8, 12) !== "WEBP") return null;
                const kind = buf.toString("ascii", 12, 16);
                if (kind === "VP8X") return [1 + buf.readUIntLE(24, 3), 1 + buf.readUIntLE(27, 3)];
                if (kind === "VP8L") { const b = buf.readUInt32LE(21); return [1 + (b & 0x3FFF), 1 + ((b >> 14) & 0x3FFF)]; }
                if (kind === "VP8 ") return [buf.readUInt16LE(26) & 0x3FFF, buf.readUInt16LE(28) & 0x3FFF];
                return null;
              }
              case ".svg": {
                const tag = (buf.toString("utf8").match(/<svg\b[^>]*>/i) || [])[0];
                if (!tag) return null;
                const attribute = name => { const m = tag.match(new RegExp("\\s" + name + "\\s*=\\s*[\"']\\s*([\\d.]+)(px)?\\s*[\"']", "i")); return m ? parseFloat(m[1]) : null; };
                const w = attribute("width"), h = attribute("height");
                if (w && h) return [w, h];
                const box = tag.match(/viewBox\s*=\s*["']\s*[-\d.]+[\s,]+[-\d.]+[\s,]+([\d.]+)[\s,]+([\d.]+)/i);
                return box ? [parseFloat(box[1]), parseFloat(box[2])] : null;
              }
            }
            return null;
          } catch {
            return null;
          } finally {
            fs.closeSync(fd);
          }
        }

        function read(file) {
          try { return fs.readFileSync(file, "utf8").slice(0, 200000); } catch { return ""; }
        }

        function project(file, rel) {
          for (const m of read(file).matchAll(/<(ApplicationIcon|PackageIcon)>\s*([^<]+?)\s*<\//g)) references.push({ from: rel, target: m[2] });
        }

        function page(file, rel) {
          const text = read(file);
          for (const m of text.matchAll(/<link\b[^>]*>/gi)) {
            const rel_ = (m[0].match(/\brel\s*=\s*["']([^"']+)["']/i) || [])[1] || "";
            const href = (m[0].match(/\bhref\s*=\s*["']([^"']+)["']/i) || [])[1];
            if (href && /(^|\s)(icon|apple-touch-icon)(\s|$)/i.test(rel_)) references.push({ from: rel, target: href });
          }
          if (file.endsWith("manifest.json") || file.endsWith(".webmanifest")) {
            for (const m of text.matchAll(/"src"\s*:\s*"([^"]+)"/g)) references.push({ from: rel, target: m[1] });
          }
        }

        function walk(dir, rel, depth) {
          if (depth > 5 || visited > 20000 || files.length >= 400) return;
          let entries;
          try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
          for (const entry of entries) {
            visited++;
            const relPath = rel ? rel + "/" + entry.name : entry.name, full = path.join(dir, entry.name);
            if (entry.isDirectory()) {
              if (!skip.has(entry.name) && (!entry.name.startsWith(".") || entry.name === ".github")) walk(full, relPath, depth + 1);
              continue;
            }
            if (!entry.isFile()) continue;
            const ext = path.extname(entry.name).toLowerCase();
            if (images.has(ext)) {
              let bytes;
              try { bytes = fs.statSync(full).size; } catch { continue; }
              if (bytes > 0 && bytes <= {{MaxBytes}}) {
                const dims = size(full, ext, bytes);
                files.push({ path: relPath, bytes, width: dims ? dims[0] : null, height: dims ? dims[1] : null });
              }
            } else if (depth <= 4 && /\.(cs|fs|vb)proj$/.test(entry.name)) {
              project(full, relPath);
            } else if (depth === 0 && entry.name === "package.json") {
              try { const icon = JSON.parse(read(full)).icon; if (typeof icon === "string") references.push({ from: relPath, target: icon }); } catch {}
            } else if (depth <= 3 && (entry.name === "index.html" || entry.name === "_Layout.cshtml" || entry.name === "App.razor" || entry.name === "manifest.json" || entry.name.endsWith(".webmanifest"))) {
              page(full, relPath);
            }
          }
        }

        walk(root, "", 0);
        process.stdout.write(JSON.stringify({ files, references }));
        DAM_ICONS
        """;

    public static RepoIconScan ParseScan(string json)
    {
        var scan = JsonSerializer.Deserialize<ScanJson>(json, JsonOptions) ?? new ScanJson();
        return new RepoIconScan(
            scan.Files.Where(f => IsSafePath(f.Path)).Select(f => new RepoIconFile(f.Path, f.Bytes, f.Width, f.Height)).ToList(),
            scan.References.Select(r => new RepoIconReference(r.From, r.Target)).ToList());
    }

    /// <summary>
    /// A path inside the folder: relative, no "..", with an image extension. Paths from the Portal (a picked icon) are
    /// checked with this before the Manager reads them.
    /// </summary>
    public static bool IsSafePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') && !path.StartsWith('~') && !path.Contains('\\') && !path.Contains('\0')
        && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..")
        && ContentTypes.ContainsKey(System.IO.Path.GetExtension(path));

    /// <summary>All images, best first. <paramref name="projectName"/> is the repository's or folder's name.</summary>
    public static IReadOnlyList<ScoredRepoIcon> Rank(RepoIconScan scan, string projectName)
    {
        var project = Key(projectName.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? projectName[..^4] : projectName);
        var referenced = scan.Files.Where(f => IsReferenced(f, scan.References)).Select(f => SameIcon(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return scan.Files
            .Select(f => Score(f, project, referenced.Contains(SameIcon(f.Path))))
            .OrderByDescending(s => s.Eligible)
            .ThenByDescending(s => s.Score)
            .ThenBy(s => PixelSize(s.File) is { } px && px >= 32 ? px : double.MaxValue)
            .ThenBy(s => s.File.Path.Count(c => c == '/'))
            .ThenBy(s => s.File.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The icon to use, or null when nothing looks like one.</summary>
    public static RepoIconFile? Pick(RepoIconScan scan, string projectName) =>
        Rank(scan, projectName).FirstOrDefault(s => s.Eligible)?.File;

    private static readonly string[] IconWords = ["icon", "logo", "launcher", "brand", "emblem", "mark"];

    private static readonly string[] NotIconWords = ["screenshot", "screen", "splash", "banner", "wide", "background", "backdrop", "social", "preview", "cover",
        "header", "hero", "feature", "promo", "poster", "wallpaper", "demo", "example", "sample", "thumbnail"];

    private static readonly string[] IconFolders = ["branding", "brand", "logo", "logos", "icon", "icons", "appicon"];

    private static readonly string[] AssetFolders = ["assets", "asset", "images", "image", "img", "media", "resources", "res", "static", "public", "wwwroot", ".github", "art", "graphics"];

    private static readonly string[] TestFolders = ["test", "tests", "spec", "specs", "fixtures", "testdata", "samples", "examples", "e2e", "screenshots"];

    private static ScoredRepoIcon Score(RepoIconFile file, string project, bool referenced)
    {
        var folders = file.Path.Split('/')[..^1].Select(f => f.ToLowerInvariant()).ToList();
        var fileName = System.IO.Path.GetFileNameWithoutExtension(file.Path);
        var name = Key(fileName);
        var ext = System.IO.Path.GetExtension(file.Path).ToLowerInvariant();
        var score = 0;
        var signal = referenced;

        if (referenced)
        {
            score += 12;
        }

        if (project.Length > 0 && name == project)
        {
            score += 10;
            signal = true;
        }
        else if (project.Length >= 4 && name.Contains(project, StringComparison.Ordinal))
        {
            score += 8;
            signal = true;
        }

        if (name.Contains("favicon", StringComparison.Ordinal))
        {
            score += 8;
            signal = true;
        }
        else if (IconWords.Any(w => name.Contains(w, StringComparison.Ordinal)))
        {
            score += 6;
            signal = true;
        }

        if (NotIconWords.Any(w => name.Contains(w, StringComparison.Ordinal)))
        {
            score -= 10;
        }

        if (folders.Any(f => IconFolders.Contains(f) || f.Contains("appicon", StringComparison.Ordinal)))
        {
            score += 4;
            signal = true;
        }
        else if (folders.Any(AssetFolders.Contains))
        {
            score += 2;
        }

        if (folders.Any(TestFolders.Contains))
        {
            score -= 6;
        }

        score += Math.Max(0, 3 - folders.Count);

        if (file.Width is { } width && file.Height is { } height && width > 0 && height > 0)
        {
            var ratio = Math.Min(width, height) / Math.Max(width, height);
            score += ratio >= 0.95 ? 6 : ratio >= 0.8 ? 1 : -8;
            var px = Math.Max(width, height);
            score += px < 24 ? -4 : px < 64 ? 2 : px <= 256 ? 4 : px <= 512 ? 3 : px <= 1024 ? 1 : -2;
        }
        else if (ext == ".svg")
        {
            score += 2;
        }

        score += ext switch
        {
            ".svg" or ".png" => 2,
            ".ico" => 1,
            ".gif" => -1,
            _ => 0,
        };

        return new ScoredRepoIcon(file, score, signal && score >= 12);
    }

    private static double? PixelSize(RepoIconFile file) => file.Width is { } w && file.Height is { } h ? Math.Max(w, h) : null;

    /// <summary>A project file names this image, or the same image in another format (icon.ico next to icon.png).</summary>
    private static bool IsReferenced(RepoIconFile file, IReadOnlyList<RepoIconReference> references) =>
        references.Any(r =>
        {
            var target = r.Target.Replace('\\', '/').Split('?', '#')[0].Trim();
            if (target.Length == 0 || target.Contains("://", StringComparison.Ordinal))
            {
                return false;
            }

            var fromFolder = r.From.Contains('/') ? r.From[..r.From.LastIndexOf('/')] : "";
            var resolved = Normalize(target.StartsWith('/') ? target.TrimStart('/') : $"{fromFolder}/{target}");
            var targetName = target[(target.LastIndexOf('/') + 1)..];
            // Web pages link from their web root (/favicon.png is in wwwroot or public); NuGet's PackageIcon is a path
            // in the package. Both are matched by the end of the path.
            return string.Equals(file.Path, resolved, StringComparison.OrdinalIgnoreCase)
                   || file.Path.EndsWith("/" + target.TrimStart('/', '.'), StringComparison.OrdinalIgnoreCase)
                   || (targetName.Length > 0 && string.Equals(file.Path[(file.Path.LastIndexOf('/') + 1)..], targetName, StringComparison.OrdinalIgnoreCase));
        });

    private static string SameIcon(string path) => path.Contains('.') ? path[..path.LastIndexOf('.')] : path;

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    /// <summary>Lower case letters and digits only: "Deve-Maze_Generator" → "devemazegenerator".</summary>
    private static string Key(string name) => new(name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private sealed class ScanJson
    {
        public List<FileJson> Files { get; set; } = [];

        public List<ReferenceJson> References { get; set; } = [];
    }

    private sealed class FileJson
    {
        public string Path { get; set; } = "";

        public long Bytes { get; set; }

        public double? Width { get; set; }

        public double? Height { get; set; }
    }

    private sealed class ReferenceJson
    {
        public string From { get; set; } = "";

        public string Target { get; set; } = "";
    }
}
