namespace Querithm.Tests;

/// <summary>
/// The one place a test's time limit is stretched for slow hardware. <c>QUERITHM_TEST_TIME_SCALE</c> (default 1, clamped
/// to 1–10) multiplies upper bounds only: how long a test waits for something before failing, and the slack of a
/// "finished within" check. It never scales a production constant a test compares against, a lower bound ("waited at
/// least the busy budget") or a settle delay that proves something did not happen. The release workflow sets it on the
/// macOS Intel runner (several times slower than the others); every wait stays bounded, a 10x slowdown still fails at 4,
/// and the real targets are measured by <c>pnpm bench</c> (ARCHITECTURE §14).
/// </summary>
public static class TestTime
{
    public static readonly double Factor = Read();

    /// <summary>An upper-bound wait or timeout, scaled.</summary>
    public static TimeSpan Of(TimeSpan limit) => limit * Factor;

    /// <summary>An upper bound in milliseconds, scaled.</summary>
    public static int Ms(int limitMs) => (int)Math.Min(int.MaxValue, limitMs * Factor);

    /// <summary>A "finished within" budget in milliseconds, scaled.</summary>
    public static double Budget(double limitMs) => limitMs * Factor;

    static double Read()
    {
        var raw = Environment.GetEnvironmentVariable("QUERITHM_TEST_TIME_SCALE");
        if (string.IsNullOrWhiteSpace(raw) || !double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
            return 1;
        f = Math.Clamp(f, 1, 10);
        if (f != 1) Console.WriteLine($"[TestTime] upper-bound time limits scaled ×{f} (QUERITHM_TEST_TIME_SCALE)");
        return f;
    }
}
