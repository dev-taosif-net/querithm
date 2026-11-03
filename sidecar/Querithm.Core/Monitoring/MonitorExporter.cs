using System.Text;
using System.Text.Json;
using Querithm.Contract;

namespace Querithm.Core.Monitoring;

/// <summary>
/// Files the monitor hands to the user (history JSON, deadlock .xdl): written only under &lt;data dir&gt;/exports (the
/// sidecar never writes into user folders, §13), to a temporary file first and moved into place, so a failed write
/// (disk full) leaves nothing behind (H7). The webview reads the file and saves it where the user picks.
/// </summary>
public sealed class MonitorExporter(string exportDir, Func<string, Stream>? openWrite = null)
{
    public string Directory { get; } = Path.GetFullPath(exportDir);

    /// <summary>A file name from data the sidecar controls: letters, digits, '-', '_' only (no separators, no "..": H6).</summary>
    public static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (sb.Length >= 60) break;
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }
        var s = sb.ToString().Trim('_');
        return s.Length == 0 ? "export" : s;
    }

    /// <summary>An exports folder that is a link could point anywhere: nothing is written to or deleted through it.</summary>
    bool IsLinked => new DirectoryInfo(Directory) is { Exists: true } d && (d.LinkTarget != null || d.Attributes.HasFlag(FileAttributes.ReparsePoint));

    string Target(string baseName, string extension)
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (IsLinked) throw QuerithmException.Invalid("The app's exports folder is a link; exports are written only to the app's own folder.");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        // Two exports in the same second get distinct names (-2, -3, …).
        for (var n = 1; ; n++)
        {
            var path = Path.GetFullPath(Path.Combine(Directory, $"{SafeName(baseName)}-{stamp}{(n > 1 ? $"-{n}" : "")}{extension}"));
            if (!path.StartsWith(Directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw QuerithmException.Invalid("Exports are written only to the app's exports folder.");
            if (!File.Exists(path) && !File.Exists(path + ".partial")) return path;
        }
    }

    public MonitorFileResult WriteJson(string baseName, Action<Utf8JsonWriter> write) =>
        Write(baseName, ".json", s =>
        {
            using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = false });
            write(w);
            w.Flush();
        });

    public MonitorFileResult WriteText(string baseName, string extension, string content) =>
        Write(baseName, extension, s =>
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes);
        });

    /// <summary>A staged file older than this was saved (or the save was cancelled) long ago.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Deletes staged exports left behind: the webview copies a file to where the user picks and cannot delete it itself,
    /// and a cancelled save dialog leaves it too. They can hold query text, logins and hosts (user data, §13), so they do
    /// not stay. Only regular top-level files with the exporter's own extensions, never through a link. Run at start and
    /// before each export.
    /// </summary>
    public int Sweep(DateTime? nowUtc = null)
    {
        if (!System.IO.Directory.Exists(Directory) || IsLinked) return 0;
        var cutoff = (nowUtc ?? DateTime.UtcNow) - StaleAfter;
        var deleted = 0;
        foreach (var f in new DirectoryInfo(Directory).EnumerateFiles())
        {
            if (f.LinkTarget != null || f.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (f.Extension is not (".json" or ".xdl" or ".partial") || f.LastWriteTimeUtc > cutoff) continue;
            try { f.Delete(); deleted++; } catch (IOException) { /* in use: next sweep */ } catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    MonitorFileResult Write(string baseName, string extension, Action<Stream> body)
    {
        try { Sweep(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* never fails an export */ }
        var path = Target(baseName, extension);
        var tmp = path + ".partial";
        try
        {
            using (var s = openWrite?.Invoke(tmp) ?? new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                body(s);
            File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw new QuerithmException("exportFailed", $"The export could not be written: {ex.Message}");
        }
        return new MonitorFileResult { Path = path, FileName = Path.GetFileName(path), Bytes = new FileInfo(path).Length };
    }
}
