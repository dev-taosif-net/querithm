using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Querithm.Host;

/// <summary>Orphan prevention (§4 step 4). Windows is covered by the Job Object Rust creates; this is the backup.</summary>
static partial class ParentWatch
{
    const int PR_SET_PDEATHSIG = 1;
    const int SIGKILL = 9;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, nint arg2, nint arg3, nint arg4, nint arg5);

    [LibraryImport("libc")]
    private static partial int getppid();

    public static void Start(Action onParentGone)
    {
        var expected = int.TryParse(Environment.GetEnvironmentVariable("QUERITHM_PARENT_PID"), out var p) ? p : (int?)null;
        if (OperatingSystem.IsLinux())
        {
            // Must run before anything else: if the parent already died, getppid() no longer matches.
            _ = prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0);
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var initial = getppid();
            if (expected is { } e && initial != e) { onParentGone(); return; }
            var t = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(2000);
                    var now = getppid();
                    if (now != initial || now == 1) { onParentGone(); return; }
                }
            }) { IsBackground = true, Name = "parent-watch" };
            t.Start();
        }
        else if (OperatingSystem.IsWindows() && expected is { } pid)
        {
            var t = new Thread(() =>
            {
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    parent.WaitForExit();
                }
                catch (ArgumentException) { /* already gone */ }
                onParentGone();
            }) { IsBackground = true, Name = "parent-watch" };
            t.Start();
        }
    }
}
