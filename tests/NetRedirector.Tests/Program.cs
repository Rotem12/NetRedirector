using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NetRedirector.Core;

namespace NetRedirector.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Test)[]
        {
            ("UDP datagram → UDP datagram", TestUdpToUdpAsync),
            ("TCP client → UDP datagram", TestTcpClientToUdpAsync),
            ("TCP server → UDP datagram", TestTcpServerToUdpAsync),
            ("UDP datagram → TCP server", TestUdpToTcpServerAsync),
            ("Multicast on selected interface → UDP", TestMulticastAsync),
            ("Read-only firewall assessment", TestFirewallAssessmentAsync),
            ("Saved settings preserve auto-run", TestSavedSettingsAsync)
        };

        var failures = 0;
        foreach (var (name, test) in tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine($"FAIL  {name}: {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "All network transfer tests passed."
            : $"{failures} network transfer test(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task TestUdpToUdpAsync()
    {
        var sourcePort = GetFreeUdpPort();
        var targetPort = GetFreeUdpPort();
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, targetPort));
        await using var redirector = CreateRedirector(
            new EndpointConfig { Protocol = EndpointProtocol.Udp, Host = IPAddress.Loopback.ToString(), Port = sourcePort },
            new EndpointConfig { Protocol = EndpointProtocol.Udp, Host = IPAddress.Loopback.ToString(), Port = targetPort });

        await redirector.StartAsync();
        var payload = Encoding.UTF8.GetBytes("udp-payload-0123456789");
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await sender.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, sourcePort));
        var received = await ReceiveAsync(receiver);
        AssertPayload(payload, received.Buffer);
        Assert.True(redirector.Metrics.BytesForwarded == payload.Length, "UDP byte metric did not match.");
        await redirector.StopAsync();
    }

    private static async Task TestTcpClientToUdpAsync()
    {
        var inputPort = GetFreeTcpPort();
        var targetPort = GetFreeUdpPort();
        using var inputListener = new TcpListener(IPAddress.Loopback, inputPort);
        inputListener.Start();
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, targetPort));
        await using var redirector = CreateRedirector(
            new EndpointConfig { Protocol = EndpointProtocol.TcpClient, Host = IPAddress.Loopback.ToString(), Port = inputPort },
            new EndpointConfig { Protocol = EndpointProtocol.Udp, Host = IPAddress.Loopback.ToString(), Port = targetPort });

        await redirector.StartAsync();
        using var inputClient = await inputListener.AcceptTcpClientAsync(TimeoutToken());
        inputClient.NoDelay = true;
        var payload = Encoding.UTF8.GetBytes("tcp-client-payload");
        await inputClient.GetStream().WriteAsync(payload, TimeoutToken());
        var received = await ReceiveAsync(receiver);
        AssertPayload(payload, received.Buffer);
        await redirector.StopAsync();
    }

    private static async Task TestTcpServerToUdpAsync()
    {
        var inputPort = GetFreeTcpPort();
        var targetPort = GetFreeUdpPort();
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, targetPort));
        await using var redirector = CreateRedirector(
            new EndpointConfig { Protocol = EndpointProtocol.TcpServer, Host = IPAddress.Loopback.ToString(), Port = inputPort },
            new EndpointConfig { Protocol = EndpointProtocol.Udp, Host = IPAddress.Loopback.ToString(), Port = targetPort });

        await redirector.StartAsync();
        using var inputClient = new TcpClient();
        await inputClient.ConnectAsync(IPAddress.Loopback, inputPort, TimeoutToken());
        var payload = Encoding.UTF8.GetBytes("tcp-server-payload");
        await inputClient.GetStream().WriteAsync(payload, TimeoutToken());
        var received = await ReceiveAsync(receiver);
        AssertPayload(payload, received.Buffer);
        await redirector.StopAsync();
    }

    private static async Task TestUdpToTcpServerAsync()
    {
        var sourcePort = GetFreeUdpPort();
        var targetPort = GetFreeTcpPort();
        await using var redirector = CreateRedirector(
            new EndpointConfig { Protocol = EndpointProtocol.Udp, Host = IPAddress.Loopback.ToString(), Port = sourcePort },
            new EndpointConfig { Protocol = EndpointProtocol.TcpServer, Host = IPAddress.Loopback.ToString(), Port = targetPort });

        await redirector.StartAsync();
        using var receiver = new TcpClient();
        await receiver.ConnectAsync(IPAddress.Loopback, targetPort, TimeoutToken());
        var payload = Encoding.UTF8.GetBytes("udp-to-tcp-payload");
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await sender.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, sourcePort));
        var received = await ReadExactAsync(receiver.GetStream(), payload.Length);
        AssertPayload(payload, received);
        await redirector.StopAsync();
    }

    private static async Task TestMulticastAsync()
    {
        var loopback = NetworkInterfaceCatalog.GetIpv4Interfaces()
            .FirstOrDefault(item => item.IsLoopback && item.Address == IPAddress.Loopback.ToString());
        if (loopback is null)
        {
            throw new InvalidOperationException("The active IPv4 loopback interface was not available.");
        }

        var sourcePort = GetFreeUdpPort();
        var targetPort = GetFreeUdpPort();
        var group = IPAddress.Parse("239.255.77.77");
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, targetPort));
        await using var redirector = CreateRedirector(
            new EndpointConfig { Protocol = EndpointProtocol.Udp, Host = group.ToString(), Port = sourcePort },
            new EndpointConfig { Protocol = EndpointProtocol.Udp, Host = IPAddress.Loopback.ToString(), Port = targetPort },
            [loopback.Address]);

        await redirector.StartAsync();
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
        sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        var payload = Encoding.UTF8.GetBytes("multicast-payload");
        await sender.SendAsync(payload, new IPEndPoint(group, sourcePort));
        var received = await ReceiveAsync(receiver, timeoutSeconds: 5);
        AssertPayload(payload, received.Buffer);
        await redirector.StopAsync();
    }

    private static Task TestFirewallAssessmentAsync()
    {
        var status = FirewallAssessment.Check(new RedirectConfig
        {
            Source = new EndpointConfig { Protocol = EndpointProtocol.Udp, Host = IPAddress.Loopback.ToString(), Port = 45001 },
            Target = new EndpointConfig { Protocol = EndpointProtocol.Udp, Host = IPAddress.Loopback.ToString(), Port = 45002 }
        }, Environment.ProcessPath);

        if (string.IsNullOrWhiteSpace(status.Summary) || string.IsNullOrWhiteSpace(status.Details))
        {
            throw new InvalidOperationException("The firewall assessment returned no user-facing status.");
        }

        Console.WriteLine($"      {status.Summary} — {status.Details}");
        return Task.CompletedTask;
    }

    private static Task TestSavedSettingsAsync()
    {
        if (!new RedirectConfig().AutoStart)
        {
            throw new InvalidOperationException("Auto-run is not enabled by default.");
        }

        var json = JsonSerializer.Serialize(new RedirectConfig { AutoStart = false });
        var loaded = JsonSerializer.Deserialize<RedirectConfig>(json);
        if (loaded is null || loaded.AutoStart)
        {
            throw new InvalidOperationException("The saved auto-run setting did not round-trip.");
        }

        return Task.CompletedTask;
    }

    private static RedirectorService CreateRedirector(
        EndpointConfig source,
        EndpointConfig target,
        IReadOnlyList<string>? interfaces = null)
    {
        return new RedirectorService(new RedirectConfig
        {
            Source = source,
            Target = target,
            MulticastInterfaces = interfaces?.ToList() ?? []
        });
    }

    private static async Task<UdpReceiveResult> ReceiveAsync(UdpClient receiver, int timeoutSeconds = 3)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        return await receiver.ReceiveAsync(timeout.Token);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length)
    {
        var result = new byte[length];
        var offset = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (offset < result.Length)
        {
            var read = await stream.ReadAsync(result.AsMemory(offset), timeout.Token);
            if (read == 0)
            {
                throw new InvalidOperationException("TCP target closed before the complete payload arrived.");
            }
            offset += read;
        }
        return result;
    }

    private static CancellationToken TimeoutToken() => new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token;

    private static int GetFreeUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void AssertPayload(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Payload mismatch: expected {expected.Length} bytes, received {actual.Length}.");
        }
    }

    private static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
