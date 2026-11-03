using System.Text.RegularExpressions;

namespace Querithm.Tests;

/// <summary>
/// Rules a reviewer cannot be trusted to remember, checked over the sidecar's source (C# code, never SQL).
///
/// No unbounded waits (CLAUDE.md §4): every await on something outside the process — a database, the data plane, the
/// webview — carries the caller's token or a time limit, so Cancel and shutdown always end it. A run whose end-of-run
/// frame waited for a reader with <c>CancellationToken.None</c> kept its tab busy forever and Cancel did nothing.
/// Each use of <c>CancellationToken.None</c> / <c>default(CancellationToken)</c> is listed here with what bounds it.
/// </summary>
public class ArchitectureRulesTests
{
    /// <summary>
    /// Repository-relative path → (uses, what bounds them). Update the count and the reason together. Cleanup after the
    /// caller's token fired (rollbacks, restoring session options, the state reads after a run) is not listed: it runs
    /// under <c>Cleanup.Start()</c> (60 s), because the drivers' command timeout defaults to none (§13 rule 8).
    /// </summary>
    static readonly Dictionary<string, (int Count, string Bound)> Allowed = new()
    {
        ["sidecar/Querithm.Core/Monitoring/BlockingBannerService.cs"] = (1, "the banner's timer cycle: its check query has a 5 s command timeout and its pool wait a 1 s bound (§16)"),
        ["sidecar/Querithm.Core/Monitoring/MonitorScheduler.cs"] = (1, "a ContinueWith scheduling token, not a wait"),
        ["sidecar/Querithm.Host/Program.cs"] = (2, "startup self-check of the bundled in-process engines on a local temp file: no network, no user input"),
        ["sidecar/Querithm.Host/RpcServer.cs"] = (1, "RpcServer.OwnBound: the handlers that use it are listed with their own bound in RpcOwnBounds; every other handler that waits on a server has a deadline (InteractiveDeadline / LongDeadline)"),
    };

    /// <summary>RPC handlers that pass <c>OwnBound</c> instead of a deadline token, and what ends their wait.</summary>
    static readonly Dictionary<string, string> RpcOwnBounds = new()
    {
        ["ImportInspect"] = "reads a picked local file (no server); import.cancel with the file token cancels it, and a large file legitimately takes minutes",
        ["DebugStep"] = "waits for the target's next breakpoint, which may legitimately take long; debug.detach (Stop) cancels the target's call, which ends the wait",
        ["TxCommit"] = "a COMMIT may legitimately take minutes (a synchronous standby, a large transaction) and one cut short by a deadline has an unknown outcome; SessionManager.TxEndLimit (30 min) bounds it, and a forced tab close, disconnect, engine restart or quit (Session.Closing) ends it sooner",
        ["TxRollback"] = "a large ROLLBACK may legitimately take minutes while it holds the session; SessionManager.TxEndLimit (30 min) bounds it, and a forced tab close, disconnect, engine restart or quit (Session.Closing) ends it sooner",
    };

    static readonly Regex Unbounded = new(@"CancellationToken\.None|default\(CancellationToken\)", RegexOptions.Compiled);

    /// <summary>Found from this source file, so it works whatever the build output directory is.</summary>
    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(here)!); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src-tauri", "tauri.conf.json"))) return d.FullName;
        throw new InvalidOperationException("repository root not found above " + here);
    }

    static Dictionary<string, int> Uses()
    {
        var root = RepoRoot();
        var sidecar = Path.Combine(root, "sidecar");
        var uses = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(sidecar, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("sidecar/Querithm.Tests/", StringComparison.Ordinal) || rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            var n = Unbounded.Matches(File.ReadAllText(file)).Count;
            if (n > 0) uses[rel] = n;
        }
        return uses;
    }

    [Fact]
    public void Every_unbounded_cancellation_token_in_the_sidecar_is_listed_with_what_bounds_it()
    {
        var uses = Uses();
        var problems = new List<string>();
        foreach (var (file, n) in uses)
        {
            if (!Allowed.TryGetValue(file, out var allowed))
                problems.Add($"{file}: {n} new use(s). Pass the caller's token or a time limit; if it is truly bounded, add it to {nameof(Allowed)} with what bounds it.");
            else if (n > allowed.Count)
                problems.Add($"{file}: {n} uses, {allowed.Count} listed. Pass the caller's token or a time limit, or raise the count with the reason.");
            else if (n < allowed.Count)
                problems.Add($"{file}: {n} uses, {allowed.Count} listed. Lower the count so the list stays exact.");
        }
        foreach (var file in Allowed.Keys.Where(f => !uses.ContainsKey(f)))
            problems.Add($"{file}: listed but has no use any more. Remove it from {nameof(Allowed)}.");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_listed_use_says_what_bounds_it()
    {
        Assert.All(Allowed, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value.Bound), kv.Key));
        Assert.All(RpcOwnBounds, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
    }

    /// <summary>
    /// M5: an RPC handler that waits on a server either has a deadline or is listed with its own bound. A new handler
    /// passing <c>OwnBound</c> (an unbounded token) fails here until it is listed with the reason.
    /// </summary>
    [Fact]
    public void Rpc_handlers_without_a_deadline_are_listed_with_their_bound()
    {
        var source = File.ReadAllLines(Path.Combine(RepoRoot(), "sidecar", "Querithm.Host", "RpcServer.cs"));
        var handler = new Regex(@"public\s+Task(?:<[^(]*>)?\s+(\w+)\(", RegexOptions.Compiled);
        var using_ = source.Where(l => l.Contains("OwnBound") && !l.Contains("static CancellationToken OwnBound"))
            .Select(l => handler.Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(RpcOwnBounds.Keys.OrderBy(k => k), using_.OrderBy(k => k));
    }
}
