using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Rdap.Net;

namespace Rdap.Tests;

public sealed class HappyEyeballsTests
{
    private static readonly IPAddress V6a = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress V6b = IPAddress.Parse("2001:db8::2");
    private static readonly IPAddress V4a = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress V4b = IPAddress.Parse("192.0.2.2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Interleave_alternates_families_starting_with_the_first_answer()
    {
        Assert.Equal([V6a, V4a, V6b, V4b], HappyEyeballsConnector.Interleave([V6a, V6b, V4a, V4b]));
        Assert.Equal([V4a, V6a, V4b], HappyEyeballsConnector.Interleave([V4a, V4b, V6a]));
    }

    [Fact]
    public async Task Unresponsive_ipv6_falls_back_to_ipv4_after_the_attempt_delay()
    {
        // The reported bug: an IPv6 route that silently drops packets used to eat the whole request timeout.
        var abandoned = new TaskCompletionSource();
        using var v4Socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var connector = Connector(TimeSpan.FromMilliseconds(50), async (ep, ct) =>
        {
            if (ep.AddressFamily == AddressFamily.InterNetworkV6)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                finally
                {
                    abandoned.TrySetResult();
                }
            }

            return v4Socket;
        });

        var sw = Stopwatch.StartNew();
        var socket = await connector.ConnectAsync("rdap.example.test", 443, Ct);

        Assert.Same(v4Socket, socket);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // the losing attempt is cancelled
    }

    [Fact]
    public async Task Failed_attempt_starts_the_next_one_without_waiting()
    {
        using var v4Socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var connector = Connector(TimeSpan.FromMinutes(5), (ep, _) => ep.AddressFamily == AddressFamily.InterNetworkV6
            ? Task.FromException<Socket>(new SocketException((int)SocketError.NetworkUnreachable))
            : Task.FromResult(v4Socket));

        var socket = await connector.ConnectAsync("rdap.example.test", 443, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Same(v4Socket, socket);
    }

    [Fact]
    public async Task All_attempts_failing_throws_the_last_socket_error()
    {
        var connector = Connector(TimeSpan.FromMilliseconds(10), (ep, _) => Task.FromException<Socket>(
            new SocketException((int)(ep.AddressFamily == AddressFamily.InterNetworkV6 ? SocketError.NetworkUnreachable : SocketError.ConnectionRefused))));

        var ex = await Assert.ThrowsAsync<SocketException>(() => connector.ConnectAsync("rdap.example.test", 443, Ct));

        Assert.Contains(ex.SocketErrorCode, new[] { SocketError.NetworkUnreachable, SocketError.ConnectionRefused });
    }

    [Fact]
    public async Task Ip_literal_skips_dns()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var connector = new HappyEyeballsConnector(
            null, null,
            (_, _) => throw new InvalidOperationException("should not resolve"),
            (ep, _) => Task.FromResult(ep.Address.Equals(V4a) ? socket : throw new InvalidOperationException()));

        Assert.Same(socket, await connector.ConnectAsync("192.0.2.1", 443, Ct));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var connector = Connector(TimeSpan.FromMilliseconds(10), async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.ConnectAsync("rdap.example.test", 443, cts.Token));
    }

    [Fact]
    public async Task Connects_to_a_real_listener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptSocketAsync(Ct);

        using var socket = await new HappyEyeballsConnector().ConnectAsync("127.0.0.1", port, Ct);
        using var accepted = await accept;

        Assert.True(socket.Connected);
    }

    private static HappyEyeballsConnector Connector(TimeSpan delay, Func<IPEndPoint, CancellationToken, Task<Socket>> connect) =>
        new(null, delay, (_, _) => Task.FromResult(new[] { V6a, V4a }), connect);
}
