using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Core.Logging;
using Querithm.Host;

namespace Querithm.Tests;

/// <summary>
/// Crash diagnostics (audit M17, M21): unhandled exceptions reach the scrubbed log, webview error reports are capped, the
/// log tail for "Copy diagnostics" reads only Querithm's own logs and scrubs them again, and trace adds no third-party
/// logging.
/// </summary>
public sealed class DiagnosticsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qdiag-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    static string Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(fs).ReadToEnd();
    }

    [Fact]
    public void An_unhandled_exception_is_written_scrubbed_with_its_stack_before_the_process_ends()
    {
        string text;
        using (var provider = new ScrubbingLoggerProvider(_dir, LogLevel.Information, echoToStderr: false))
        {
            var log = provider.CreateLogger("host");
            Exception thrown;
            try { throw new InvalidOperationException("open failed: Server=db;Password=hunter2"); }
            catch (Exception ex) { thrown = ex; }
            CrashLog.OnUnhandled(log, thrown, terminating: true);
            CrashLog.OnUnhandled(log, "a string was thrown", terminating: false);
            CrashLog.OnUnobserved(log, new AggregateException(new TimeoutException("background pwd=s3cret")));
            text = Read(provider.FilePath!);
        }
        Assert.Contains("[crit] host: Unhandled exception; the sidecar stops", text);
        Assert.Contains("InvalidOperationException: open failed: Server=db;Password=***", text);
        Assert.Contains(nameof(An_unhandled_exception_is_written_scrubbed_with_its_stack_before_the_process_ends), text);
        Assert.Contains("non-exception type String", text);
        Assert.Contains("[warn] host: A background task failed and nothing waited for it", text);
        Assert.Contains("TimeoutException: background pwd=***", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("s3cret", text);
    }

    [Fact]
    public void The_handlers_are_installed_on_the_app_domain_and_the_task_scheduler()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "sidecar", "Querithm.Host", "Program.cs"));
        var install = program.IndexOf("CrashLog.Install(log);", StringComparison.Ordinal);
        Assert.True(install > 0, "Program.cs installs the crash handlers");
        // Right after the logger exists: before the data plane, the store or any service can throw.
        Assert.True(install < program.IndexOf("new DataPlaneServer(", StringComparison.Ordinal));
        Assert.True(install < program.IndexOf("LocalStore.OpenOrRecover(", StringComparison.Ordinal));
    }

    [Fact]
    public void A_webview_error_report_is_logged_capped_and_scrubbed_and_a_flood_stops_at_the_limit()
    {
        string text;
        using (var provider = new ScrubbingLoggerProvider(_dir, LogLevel.Information, echoToStderr: false))
        {
            var diag = new SidecarDiagnostics(_dir, provider.CreateLogger("webview"));
            diag.Report(new ErrorReport
            {
                Where = "Results", Name = "TypeError", Message = "x is undefined; Password=hunter2 " + new string('m', 2000),
                Stack = "at Grid (grid.tsx:1)", ComponentStack = "\n    at ResultsPanel\n    at BottomPanel",
            });
            for (var i = 0; i < SidecarDiagnostics.MaxReports + 10; i++) diag.Report(new ErrorReport { Where = "loop", Message = $"again {i}" });
            text = Read(provider.FilePath!);
        }
        Assert.Contains("[erro] webview: Render error in Results: TypeError: x is undefined; Password=***", text);
        Assert.Contains("at ResultsPanel", text);
        Assert.DoesNotContain(new string('m', 501), text);
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains($"again {SidecarDiagnostics.MaxReports - 2}", text);
        Assert.DoesNotContain($"again {SidecarDiagnostics.MaxReports}", text);
        Assert.Single(text.Split('\n'), l => l.Contains("Further webview error reports are not logged", StringComparison.Ordinal));
    }

    [Fact]
    public void The_log_tail_reads_only_querithms_own_logs_newest_last_and_scrubs_them_again()
    {
        var older = Path.Combine(_dir, "sidecar-20261101-10.log");
        File.WriteAllText(older, "old 1\nold 2 connect Password=fromAnOlderBuild\n");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        var shell = Path.Combine(_dir, "shell-20261103.log");
        File.WriteAllText(shell, "shell 1\nshell 2\n");
        File.SetLastWriteTimeUtc(shell, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(_dir, "notes.log"), "user notes");
        File.WriteAllText(Path.Combine(_dir, "sidecar-x.txt"), "not a log");
        var outside = Path.Combine(Path.GetTempPath(), $"qdiag-outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "a user's own file");
        try
        {
            if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(_dir, "sidecar-link.log"), outside);
            var diag = new SidecarDiagnostics(_dir, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            var all = diag.Read(new LogTailParams());
            Assert.Equal(_dir, all.LogDir);
            Assert.Equal(["== sidecar-20261101-10.log ==", "old 1", "old 2 connect Password=***", "== shell-20261103.log ==", "shell 1", "shell 2"], all.Lines);
            // The newest lines win; the count is clamped to 1..500.
            Assert.Equal(["== shell-20261103.log ==", "shell 2"], diag.Read(new LogTailParams { Lines = 1 }).Lines);
            Assert.Equal(["== shell-20261103.log ==", "shell 2"], diag.Read(new LogTailParams { Lines = -5 }).Lines);
            Assert.Empty(new SidecarDiagnostics(Path.Combine(_dir, "missing"), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).Read(null).Lines);
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void Startup_notices_are_answered_once()
    {
        var diag = new SidecarDiagnostics(_dir, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        diag.AddNotice(new SystemNotice { Code = "storeReset", Message = "m", File = "store.db.corrupt-x" });
        Assert.Equal("store.db.corrupt-x", Assert.Single(diag.TakeNotices()).File);
        Assert.Empty(diag.TakeNotices());
    }

    /// <summary>
    /// M21 (d): QUERITHM_LOG=trace stays honoured in release. It adds nothing beyond Querithm's own debug lines: no
    /// Querithm code logs at Trace, and no driver or library (Npgsql's command text, SSH.NET) is handed the logger factory.
    /// The debug lines log exception messages of Querithm's own catalog reads, never SQL text or row values; a debug
    /// line that read a row logs the exception type only (DataEditorService).
    /// </summary>
    [Fact]
    public void Trace_adds_no_third_party_logging_and_no_querithm_trace_lines()
    {
        var sources = Directory.GetFiles(Path.Combine(RepoRoot(), "sidecar"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains("Querithm.Tests") && !f.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(sources);
        foreach (var f in sources)
        {
            var text = File.ReadAllText(f);
            foreach (var banned in new[] { "LogTrace(", "Log(LogLevel.Trace", "UseLoggerFactory", "InitializeLogging", "EnableParameterLogging", "AddLogging(" })
                Assert.False(text.Contains(banned, StringComparison.Ordinal), $"{Path.GetFileName(f)} uses {banned}");
        }
        var editor = File.ReadAllText(Path.Combine(RepoRoot(), "sidecar", "Querithm.Core", "Execution", "DataEditorService.cs"));
        Assert.Contains("log.LogDebug(\"Could not read the current row: {Type}\", ex.GetType().Name);", editor);
    }

    sealed class DuplicateKey(string message) : System.Data.Common.DbException(message)
    {
        public override string? SqlState => "23000";
    }

    [Fact]
    public void A_server_error_is_logged_by_type_and_code_never_by_a_message_that_quotes_a_row()
    {
        var e = new DuplicateKey("Violation of PRIMARY KEY constraint 'PK_t'. The duplicate key value is (alice@example.com).");
        Assert.Equal("DuplicateKey 23000", LogScrubber.Describe(e));
        Assert.Equal("The import was cancelled.", LogScrubber.Describe(new OperationCanceledException("The import was cancelled.")));
        var import = File.ReadAllText(Path.Combine(RepoRoot(), "sidecar", "Querithm.Core", "Import", "ImportService.cs"));
        Assert.Contains("log.LogWarning(\"Import failed: {Error}\", Logging.LogScrubber.Describe(ex));", import);
        var editor = File.ReadAllText(Path.Combine(RepoRoot(), "sidecar", "Querithm.Core", "Execution", "DataEditorService.cs"));
        Assert.Contains("log.LogWarning(\"Finishing the grid changes failed: {Error}\", Logging.LogScrubber.Describe(ex));", editor);
    }

    [Fact]
    public void A_trace_level_logger_still_scrubs_secrets()
    {
        LogScrubber.RegisterSecret("zq-trace-secret-9");
        string text;
        using (var provider = new ScrubbingLoggerProvider(_dir, LogLevel.Trace, echoToStderr: false))
        {
            var log = provider.CreateLogger("t");
            Assert.True(log.IsEnabled(LogLevel.Trace));
            log.LogTrace("token zq-trace-secret-9 and Password=abc;");
            log.LogDebug("body {Body}", "{\"secret\":\"p\"}");
            text = Read(provider.FilePath!);
        }
        Assert.DoesNotContain("zq-trace-secret-9", text);
        Assert.DoesNotContain("Password=abc", text);
        Assert.Contains("\"secret\":\"***\"", text);
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src-tauri", "tauri.conf.json"))) return d.FullName;
        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}
