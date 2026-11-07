using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Rdap.Diagnostics;

namespace Rdap.Cli;

/// <summary>Writes timestamped diagnostic lines (<c>-v</c>, <c>-vv</c>, <c>-vvv</c>) to stderr.</summary>
internal sealed class StderrLogger : IRdapLogger
{
    private readonly TextWriter _err;
    private readonly RdapLogLevel? _level;
    private readonly bool _color;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _lock = new();
    private readonly HashSet<Exception> _described = new(ReferenceEqualityComparer.Instance);

    /// <param name="verbosity">Number of <c>-v</c> flags: 0 logs nothing, 1 = info, 2 = debug, 3+ = trace.</param>
    public StderrLogger(TextWriter err, int verbosity, bool color)
    {
        _err = err;
        _level = verbosity <= 0 ? null : (RdapLogLevel)Math.Min(verbosity, (int)RdapLogLevel.Trace);
        _color = color;
    }

    public bool IsEnabled(RdapLogLevel level) => _level is { } l && level <= l;

    public void Log(RdapLogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var text = new StringBuilder();
        var prefix = $"[{_clock.Elapsed.TotalSeconds,7:0.000}] ";
        text.Append(_color ? Ansi.Dim + prefix + Ansi.Reset : prefix).Append(message);
        if (exception is not null)
        {
            text.AppendLine();
        }

        lock (_lock)
        {
            if (exception is not null)
            {
                // The same failure surfaces at several layers (HTTP, bootstrap, lookup); describe each exception once.
                text.Append(Describe(exception, IsEnabled(RdapLogLevel.Trace), "            ", _described));
            }

            _err.WriteLine(text.ToString().TrimEnd());
        }
    }

    /// <summary>
    /// The exception chain with types and the platform error codes that the messages leave out.
    /// Stack traces are only meaningful in builds with <c>StackTraceSupport</c> enabled.
    /// </summary>
    internal static string Describe(Exception exception, bool includeStackTrace, string indent = "", HashSet<Exception>? alreadyDescribed = null)
    {
        var sb = new StringBuilder();
        Append(exception, 0);
        return sb.ToString();

        void Append(Exception ex, int depth)
        {
            var pad = indent + new string(' ', depth * 2);
            sb.Append(pad).Append(depth == 0 ? "" : "---> ").Append(ex.GetType().FullName);
            if (alreadyDescribed is not null && !alreadyDescribed.Add(ex))
            {
                sb.AppendLine(" (details above)");
                return;
            }

            sb.Append(": ").Append(ex.Message);
            switch (ex)
            {
                case SocketException se:
                    sb.Append($" [SocketError={se.SocketErrorCode}, errno={se.NativeErrorCode}]");
                    break;
                case HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } he:
                    sb.Append($" [HttpRequestError={he.HttpRequestError}]");
                    break;
                case System.Security.Authentication.AuthenticationException:
                    sb.Append(" [TLS handshake failed]");
                    break;
            }

            sb.AppendLine();
            if (includeStackTrace && !string.IsNullOrWhiteSpace(ex.StackTrace))
            {
                foreach (var line in ex.StackTrace.Split('\n'))
                {
                    // Builds without StackTraceSupport only have raw addresses, which tell the reader nothing.
                    if (line.Contains("<BaseAddress>", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    sb.Append(pad).Append("    ").AppendLine(line.TrimEnd());
                }
            }

            if (ex is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    Append(inner, depth + 1);
                }
            }
            else if (ex.InnerException is { } innerException)
            {
                Append(innerException, depth + 1);
            }
        }
    }
}
