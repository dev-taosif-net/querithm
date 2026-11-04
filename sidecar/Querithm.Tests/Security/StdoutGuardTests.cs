using System.Diagnostics;

namespace Querithm.Tests.Security;

/// <summary>
/// Phase D3: native code writing to descriptor 1 (DuckDB's progress bar does) must not reach the JSON-RPC stream. Runs the
/// real sidecar binary: what it writes as native code lands on stderr, what it writes as RPC on stdout.
/// </summary>
public class StdoutGuardTests
{
    [Fact]
    public async Task Native_writes_to_stdout_go_to_stderr_and_rpc_keeps_stdout()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "querithm-sidecar.dll");
        var psi = new ProcessStartInfo(Environment.ProcessPath!.EndsWith("dotnet", StringComparison.Ordinal) || Environment.ProcessPath!.EndsWith("dotnet.exe", StringComparison.Ordinal)
            ? Environment.ProcessPath! : "dotnet", [dll, "--stdout-guard-test"])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TestTime.Of(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, p.ExitCode);
        Assert.Equal("rpc\n", await stdout);
        Assert.Contains("native", await stderr);
    }
}
