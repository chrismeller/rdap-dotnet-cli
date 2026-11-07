using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Rdap.Diagnostics;

namespace Rdap.Net;

/// <summary>
/// A <see cref="SocketsHttpHandler.ConnectCallback"/> that races connection attempts across the resolved
/// addresses, alternating IPv6 and IPv4 and starting the next attempt after a short delay (RFC 8305,
/// "Happy Eyeballs"). <see cref="SocketsHttpHandler"/> on its own tries addresses one at a time with no
/// per-attempt timeout, so a host whose IPv6 route silently drops packets spends the whole request timeout
/// on the first (IPv6) address and never tries IPv4.
/// </summary>
public sealed class HappyEyeballsConnector
{
    /// <summary>RFC 8305 recommends 250 ms between attempts.</summary>
    public static readonly TimeSpan DefaultAttemptDelay = TimeSpan.FromMilliseconds(250);

    private readonly IRdapLogger _log;
    private readonly TimeSpan _attemptDelay;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPEndPoint, CancellationToken, Task<Socket>> _connect;

    public HappyEyeballsConnector(IRdapLogger? logger = null, TimeSpan? attemptDelay = null)
        : this(logger, attemptDelay, Dns.GetHostAddressesAsync, ConnectSocketAsync)
    {
    }

    internal HappyEyeballsConnector(
        IRdapLogger? logger,
        TimeSpan? attemptDelay,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, Task<Socket>> connect)
    {
        _log = logger ?? NullRdapLogger.Instance;
        _attemptDelay = attemptDelay ?? DefaultAttemptDelay;
        _resolve = resolve;
        _connect = connect;
    }

    /// <summary>Plug into <see cref="SocketsHttpHandler.ConnectCallback"/>.</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var socket = await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Resolves <paramref name="host"/> and returns the first socket to connect.</summary>
    public async Task<Socket> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            _log.Log(RdapLogLevel.Debug, $"dns: resolving {host}");
            var sw = Stopwatch.StartNew();
            try
            {
                addresses = await _resolve(host, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Log(RdapLogLevel.Info, $"dns: resolving {host} failed after {sw.ElapsedMilliseconds} ms", ex);
                throw;
            }

            if (_log.IsEnabled(RdapLogLevel.Debug))
            {
                _log.Log(RdapLogLevel.Debug, addresses.Length == 0
                    ? $"dns: {host} has no addresses ({sw.ElapsedMilliseconds} ms)"
                    : $"dns: {host} -> {string.Join(", ", addresses.Select(a => a.ToString()))} ({sw.ElapsedMilliseconds} ms)");
            }
        }

        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        return await ConnectFirstAsync(Interleave(addresses), port, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Orders addresses by alternating families, starting with the family of the first answer.</summary>
    internal static List<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        var first = addresses[0].AddressFamily;
        var preferred = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily == first));
        var other = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily != first));
        var result = new List<IPAddress>(addresses.Count);
        while (preferred.Count > 0 || other.Count > 0)
        {
            if (preferred.TryDequeue(out var p))
            {
                result.Add(p);
            }

            if (other.TryDequeue(out var o))
            {
                result.Add(o);
            }
        }

        return result;
    }

    internal async Task<Socket> ConnectFirstAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<Socket>>();
        Exception? lastError = null;
        var next = 0;

        Task<Socket> Start()
        {
            var endpoint = new IPEndPoint(addresses[next++], port);
            return AttemptAsync(endpoint, cts.Token);
        }

        pending.Add(Start());
        try
        {
            while (pending.Count > 0)
            {
                // Wait for an attempt to finish, or for the delay before starting the next one.
                var delay = next < addresses.Count ? Task.Delay(_attemptDelay, cts.Token) : Task.Delay(Timeout.Infinite, cts.Token);
                var completed = await Task.WhenAny(pending.Append<Task>(delay)).ConfigureAwait(false);

                if (completed == delay)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var started = pending.Count;
                    pending.Add(Start());
                    _log.Log(RdapLogLevel.Debug, $"connect: no answer yet from {started} address(es) after {_attemptDelay.TotalMilliseconds:0} ms; also trying {addresses[next - 1]}");
                    continue;
                }

                var attempt = (Task<Socket>)completed;
                pending.Remove(attempt);
                if (attempt.IsCompletedSuccessfully)
                {
                    return attempt.Result;
                }

                lastError = attempt.Exception?.InnerException ?? new OperationCanceledException(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // A failed attempt starts the next one straight away rather than waiting for the delay.
                if (pending.Count == 0 && next < addresses.Count)
                {
                    pending.Add(Start());
                }
            }
        }
        finally
        {
            // Cancel and clean up the attempts that lost the race.
            await cts.CancelAsync().ConfigureAwait(false);
            foreach (var loser in pending)
            {
                _ = loser.ContinueWith(
                    static t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            t.Result.Dispose();
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        ExceptionDispatchInfo.Throw(lastError!);
        return null!; // unreachable
    }

    private async Task<Socket> AttemptAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        _log.Log(RdapLogLevel.Debug, $"connect: trying {endpoint}");
        var sw = Stopwatch.StartNew();
        try
        {
            var socket = await _connect(endpoint, cancellationToken).ConfigureAwait(false);
            _log.Log(RdapLogLevel.Debug, $"connect: connected to {endpoint} in {sw.ElapsedMilliseconds} ms");
            return socket;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log.Log(RdapLogLevel.Debug, $"connect: abandoned {endpoint} after {sw.ElapsedMilliseconds} ms");
            throw;
        }
        catch (Exception ex)
        {
            _log.Log(RdapLogLevel.Debug, $"connect: {endpoint} failed after {sw.ElapsedMilliseconds} ms", ex);
            throw;
        }
    }

    private static async Task<Socket> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
