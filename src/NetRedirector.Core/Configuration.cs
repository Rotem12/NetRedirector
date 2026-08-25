using System.Net;
using System.Net.Sockets;

namespace NetRedirector.Core;

public enum EndpointProtocol
{
    Udp,
    TcpClient,
    TcpServer,
    Serial
}

public sealed class EndpointConfig
{
    public EndpointProtocol Protocol { get; set; } = EndpointProtocol.Udp;

    // For UDP/TCP client this is the destination or multicast group. For a
    // server it is the local bind address. Empty means all local addresses.
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5000;

    public string SerialPort { get; set; } = "";

    public int BaudRate { get; set; } = 115200;

    public EndpointConfig Clone() => new()
    {
        Protocol = Protocol,
        Host = Host,
        Port = Port,
        SerialPort = SerialPort,
        BaudRate = BaudRate
    };
}

public sealed class RedirectConfig
{
    public EndpointConfig Source { get; set; } = new()
    {
        Protocol = EndpointProtocol.Udp,
        Host = "0.0.0.0",
        Port = 5000
    };

    public EndpointConfig Target { get; set; } = new()
    {
        Protocol = EndpointProtocol.Udp,
        Host = "127.0.0.1",
        Port = 5001
    };

    // IPv4 addresses of local interfaces. Empty means all active non-loopback
    // IPv4 interfaces for multicast. A selected list is also honored for UDP
    // unicast so a user can force egress through one or more adapters.
    public List<string> MulticastInterfaces { get; set; } = [];

    public int BufferSize { get; set; } = 65535;

    public RedirectConfig Clone() => new()
    {
        Source = Source.Clone(),
        Target = Target.Clone(),
        MulticastInterfaces = [.. MulticastInterfaces],
        BufferSize = BufferSize
    };
}

public sealed class RedirectStatusEventArgs : EventArgs
{
    public RedirectStatusEventArgs(string message, bool isError = false)
    {
        Message = message;
        IsError = isError;
    }

    public string Message { get; }
    public bool IsError { get; }
}

public readonly record struct RedirectMetrics(long BytesForwarded, long PacketsForwarded);

public static class ConfigurationValidator
{
    public static void Validate(RedirectConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidateEndpoint(config.Source, "Source");
        ValidateEndpoint(config.Target, "Target");

        if (config.BufferSize is < 1024 or > 65535)
        {
            throw new ArgumentException("Buffer size must be between 1024 and 65535 bytes.");
        }
    }

    private static void ValidateEndpoint(EndpointConfig endpoint, string name)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.Protocol == EndpointProtocol.Serial)
        {
            if (string.IsNullOrWhiteSpace(endpoint.SerialPort))
            {
                throw new ArgumentException($"{name}: choose a serial port.");
            }

            if (endpoint.BaudRate <= 0)
            {
                throw new ArgumentException($"{name}: baud rate must be positive.");
            }

            return;
        }

        if (endpoint.Port is < 1 or > 65535)
        {
            throw new ArgumentException($"{name}: port must be between 1 and 65535.");
        }

        if (endpoint.Protocol == EndpointProtocol.Udp && string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new ArgumentException($"{name}: enter an IPv4 address or multicast group.");
        }

        if (endpoint.Protocol == EndpointProtocol.TcpClient && string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new ArgumentException($"{name}: TCP client needs a remote host.");
        }

        if (!string.IsNullOrWhiteSpace(endpoint.Host) &&
            endpoint.Host != "0.0.0.0" &&
            endpoint.Host != "*" &&
            endpoint.Host != "localhost" &&
            !IPAddress.TryParse(endpoint.Host, out _))
        {
            // Host names are valid for active TCP/UDP destinations. Multicast
            // membership and local bind addresses are intentionally IPv4-only.
            if (endpoint.Protocol == EndpointProtocol.TcpServer ||
                (endpoint.Protocol == EndpointProtocol.Udp && IsMulticastName(endpoint.Host)))
            {
                throw new ArgumentException($"{name}: use an IPv4 address for a local bind or multicast group.");
            }
        }

        if (endpoint.Protocol == EndpointProtocol.Udp &&
            IPAddress.TryParse(endpoint.Host, out var address) &&
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"{name}: only IPv4 UDP addresses are supported.");
        }
    }

    public static bool IsMulticastAddress(string? host)
    {
        return IPAddress.TryParse(host, out var address) &&
               address.AddressFamily == AddressFamily.InterNetwork &&
               IsMulticastAddress(address);
    }

    public static bool IsMulticastAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork &&
               bytes.Length == 4 &&
               bytes[0] is >= 224 and <= 239;
    }

    private static bool IsMulticastName(string host) => host.Contains(':');
}
