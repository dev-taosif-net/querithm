using System.Reflection;
using Oracle.ManagedDataAccess.Client;

namespace Querithm.Tests;

/// <summary>An OracleException with a given ORA- number for unit tests (the driver's constructors are internal).</summary>
internal static class OracleErrorFactory
{
    public static OracleException Create(int number, string message) =>
        (OracleException)typeof(OracleException).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(int), typeof(string), typeof(string), typeof(string), typeof(int)])!
            .Invoke([number, "test", "", message, 0]);
}
