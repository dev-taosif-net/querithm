using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Core.Logging;

namespace Querithm.Host;

/// <summary>
/// M21: how Querithm's log files are named, capped and kept, for the sidecar's logs and — the same rule, in Rust
/// (src-tauri/src/shell_log.rs) — the shell's. A file stops at <see cref="MaxFileBytes"/> and the next part starts; of one
/// kind, files last written within <see cref="KeepDays"/> days are kept, at most <see cref="MaxFiles"/> of them.
/// The sidecar writes one file per process (<c>sidecar-yyyyMMdd-&lt;pid&gt;.log</c>, parts <c>-2</c>, <c>-3</c>, …):
/// .NET writes at the offset it tracks itself, so two app instances appending to one file would overwrite each other's
/// lines. The shell appends with O_APPEND / FILE_APPEND_DATA, which keeps whole lines, to <c>shell-yyyyMMdd.log</c>.
/// </summary>
public static class LogFiles
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int KeepDays = 7;
    public const int MaxFiles = 14;
    /// <summary>The kinds of file in logs/ that are Querithm's own; nothing else there is ever read.</summary>
    public static readonly string[] Kinds = ["sidecar-", "shell-"];

    /// <summary>Deletes the files of one kind past the retention rule; <paramref name="keep"/> (the file being written) always stays.</summary>
    public static void Prune(string dir, string kind, DateTime utcNow, string? keep = null)
    {
        try
        {
            var files = Directory.GetFiles(dir, kind + "*.log")
                .Select(f => (Path: f, At: File.GetLastWriteTimeUtc(f)))
                .OrderByDescending(f => f.At)
                .ToList();
            for (var i = 0; i < files.Count; i++)
            {
                var (path, at) = files[i];
                if (path == keep || i < MaxFiles && utcNow - at <= TimeSpan.FromDays(KeepDays)) continue;
                try { File.Delete(path); } catch (IOException) { /* another instance still writes it (Windows) */ } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { /* the folder went away: nothing to keep tidy */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Part <paramref name="part"/> of this process's file for the day.</summary>
    public static string SidecarPath(string dir, DateTime utc, int pid, int part) =>
        Path.Combine(dir, part <= 1 ? $"sidecar-{utc:yyyyMMdd}-{pid}.log" : $"sidecar-{utc:yyyyMMdd}-{pid}-{part}.log");

    /// <summary>The longest line <see cref="Tail"/> returns; and at most this much of each file is read from its end.</summary>
    const int MaxLine = 2000;
    const int MaxReadPerFile = 512 * 1024;

    /// <summary>
    /// The newest <paramref name="lines"/> lines of Querithm's own logs in <paramref name="dir"/> (sidecar and shell, newest
    /// files first, each file's lines in order under a header naming it), every line scrubbed again: lines an older build
    /// wrote went through an older scrubber. A link in the folder is never followed.
    /// </summary>
    public static string[] Tail(string dir, int lines)
    {
        if (!Directory.Exists(dir)) return [];
        var files = Kinds.SelectMany(k => Directory.GetFiles(dir, k + "*.log"))
            .Where(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        var blocks = new List<string[]>();
        var left = lines;
        foreach (var file in files)
        {
            if (left <= 0) break;
            string text;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > MaxReadPerFile) fs.Seek(-MaxReadPerFile, SeekOrigin.End);
                text = new StreamReader(fs).ReadToEnd();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var take = all.TakeLast(left).Select(l => LogScrubber.Scrub(l.TrimEnd('\r'))).Select(l => l.Length > MaxLine ? l[..MaxLine] + "…" : l).ToArray();
            left -= take.Length;
            blocks.Add([$"== {Path.GetFileName(file)} ==", .. take]);
        }
        blocks.Reverse();
        return [.. blocks.SelectMany(b => b)];
    }
}

/// <summary>
/// M21: an exception nothing caught — on any thread, or in a task nobody awaited — is written through the scrubbing
/// logger (whose file flushes every line) before the runtime ends the process, so a crash on a user's machine leaves its
/// reason in the log instead of only on a stderr nobody reads.
/// </summary>
public static class CrashLog
{
    public static void Install(ILogger log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnUnhandled(log, e.ExceptionObject, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => OnUnobserved(log, e.Exception);
    }

    public static void OnUnhandled(ILogger log, object error, bool terminating)
    {
        try
        {
            if (error is Exception ex) log.LogCritical(ex, "Unhandled exception{Ending}", terminating ? "; the sidecar stops" : "");
            else log.LogCritical("Unhandled exception of a non-exception type {Type}{Ending}", error?.GetType().Name ?? "null", terminating ? "; the sidecar stops" : "");
        }
        catch { /* the process is ending: nothing more to do */ }
    }

    public static void OnUnobserved(ILogger log, AggregateException error)
    {
        try { log.LogWarning(error.Flatten(), "A background task failed and nothing waited for it"); }
        catch { /* logging must never end the process */ }
    }
}

/// <summary>
/// The sidecar's diagnostics surface (M17, M21, M22): one-time startup notices, webview error reports into the log, and
/// the scrubbed tail of the log files for "Copy diagnostics".
/// </summary>
public sealed class SidecarDiagnostics(string logDir, ILogger webview)
{
    readonly List<SystemNotice> _notices = [];
    int _reports;

    /// <summary>How many webview reports one process logs; a render loop must not fill the disk.</summary>
    public const int MaxReports = 50;

    public string LogDir => logDir;

    public void AddNotice(SystemNotice notice)
    {
        lock (_notices) _notices.Add(notice);
    }

    /// <summary>The notices not answered yet; each is answered once.</summary>
    public SystemNotice[] TakeNotices()
    {
        lock (_notices)
        {
            var all = _notices.ToArray();
            _notices.Clear();
            return all;
        }
    }

    static string Cap(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    /// <summary>Untrusted input (§13): each field capped; the logger scrubs the line like any other.</summary>
    public void Report(ErrorReport p)
    {
        var n = Interlocked.Increment(ref _reports);
        if (n > MaxReports)
        {
            if (n == MaxReports + 1) webview.LogWarning("Further webview error reports are not logged by this process");
            return;
        }
        webview.LogError("Render error in {Where}: {Name}: {Message}\n{Stack}\nComponent stack:{ComponentStack}",
            Cap(p.Where, 64), Cap(p.Name, 100), Cap(p.Message, 500), Cap(p.Stack, 4000), Cap(p.ComponentStack, 4000));
    }

    public LogTailResult Read(LogTailParams? p)
    {
        var lines = Math.Clamp(p?.Lines ?? 200, 1, 500);
        return new LogTailResult { LogDir = logDir, Lines = LogFiles.Tail(logDir, lines) };
    }
}
