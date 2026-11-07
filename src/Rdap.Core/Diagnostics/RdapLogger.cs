namespace Rdap.Diagnostics;

/// <summary>How much diagnostic detail to emit. Higher levels include everything below them.</summary>
public enum RdapLogLevel
{
    /// <summary>Requests, timings, and which bootstrap source was used.</summary>
    Info = 1,

    /// <summary>DNS answers, connection attempts, proxy and TLS details.</summary>
    Debug = 2,

    /// <summary>HTTP headers and full exception details.</summary>
    Trace = 3,
}

/// <summary>A minimal, dependency-free diagnostic sink (keeps the library trimming/AOT-friendly).</summary>
public interface IRdapLogger
{
    bool IsEnabled(RdapLogLevel level);

    void Log(RdapLogLevel level, string message, Exception? exception = null);
}

/// <summary>Discards everything.</summary>
public sealed class NullRdapLogger : IRdapLogger
{
    public static readonly NullRdapLogger Instance = new();

    private NullRdapLogger()
    {
    }

    public bool IsEnabled(RdapLogLevel level) => false;

    public void Log(RdapLogLevel level, string message, Exception? exception = null)
    {
    }
}
