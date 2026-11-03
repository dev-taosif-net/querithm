namespace Querithm.Tests;

/// <summary>
/// M16: a test that needs the Docker fixture is reported as Skipped — never as Passed without running — unless
/// QUERITHM_IT=1. <c>alsoNeeds</c> names further opt-in variables that must be set too (QUERITHM_IT_DISRUPTIVE=1,
/// QUERITHM_IT_LONG=1, QUERITHM_PERF=1, QUERITHM_SOAK_MINUTES=&lt;n&gt;; unset or "0" counts as not set);
/// <see cref="LinuxOnly"/> skips on other systems (Docker Desktop does not route the containers' own addresses). File
/// engines and unit tests never use these: they run in plain <c>dotnet test</c>.
/// </summary>
public static class IntegrationGate
{
    public const string Reason = "needs QUERITHM_IT=1 and the Docker fixture";

    /// <summary>Null when the test may run, else the skip reason.</summary>
    public static string? SkipReason(bool needsIt, string[] alsoNeeds, bool linuxOnly)
    {
        var missing = alsoNeeds.Where(n => Environment.GetEnvironmentVariable(n) is null or "" or "0").ToArray();
        if (needsIt && !TestHost.IntegrationEnabled)
            return missing.Length == 0 ? Reason : $"{Reason}, and {string.Join(", ", missing)}";
        if (missing.Length > 0) return $"needs {string.Join(", ", missing)}" + (needsIt ? "" : " and the Docker fixture");
        if (linuxOnly && !OperatingSystem.IsLinux()) return "needs Linux (Docker Desktop does not route the containers' addresses)";
        return null;
    }
}

/// <summary>A <see cref="FactAttribute"/> skipped unless QUERITHM_IT=1 (see <see cref="IntegrationGate"/>).</summary>
public sealed class IntegrationFactAttribute(params string[] alsoNeeds) : FactAttribute
{
    public bool LinuxOnly { get; set; }

    public override string? Skip
    {
        get => base.Skip ?? IntegrationGate.SkipReason(true, alsoNeeds, LinuxOnly);
        set => base.Skip = value;
    }
}

/// <summary>A <see cref="TheoryAttribute"/> skipped unless QUERITHM_IT=1 (one skipped entry per theory, not per row).</summary>
public sealed class IntegrationTheoryAttribute(params string[] alsoNeeds) : TheoryAttribute
{
    public bool LinuxOnly { get; set; }

    public override string? Skip
    {
        get => base.Skip ?? IntegrationGate.SkipReason(true, alsoNeeds, LinuxOnly);
        set => base.Skip = value;
    }
}

/// <summary>
/// A fixture benchmark with an opt-in of its own instead of QUERITHM_IT (MonitorBench: QUERITHM_BENCH_MONITOR=1), skipped
/// until it is set.
/// </summary>
public sealed class OptInFactAttribute(params string[] needs) : FactAttribute
{
    public override string? Skip
    {
        get => base.Skip ?? IntegrationGate.SkipReason(false, needs, false);
        set => base.Skip = value;
    }
}

/// <summary>The theory form of <see cref="OptInFactAttribute"/>.</summary>
public sealed class OptInTheoryAttribute(params string[] needs) : TheoryAttribute
{
    public override string? Skip
    {
        get => base.Skip ?? IntegrationGate.SkipReason(false, needs, false);
        set => base.Skip = value;
    }
}
