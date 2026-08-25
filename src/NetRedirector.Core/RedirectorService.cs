using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace NetRedirector.Core;

public sealed class RedirectorService : IAsyncDisposable
{
    private readonly RedirectConfig _configuration;
    private readonly object _lifecycleLock = new();
    private readonly Action<string, bool> _log;
    private IByteSource? _source;
    private IByteSink? _sink;
    private CancellationTokenSource? _cancellation;
    private Task? _pipelineTask;
    private long _bytesForwarded;
    private long _packetsForwarded;

    public RedirectorService(RedirectConfig configuration, Action<string, bool>? log = null)
    {
        _configuration = configuration.Clone();
        _log = log ?? ((_, _) => { });
    }

    public event EventHandler<RedirectStatusEventArgs>? StatusChanged;

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _cancellation is not null;
            }
        }
    }

    public RedirectMetrics Metrics => new(
        Interlocked.Read(ref _bytesForwarded),
        Interlocked.Read(ref _packetsForwarded));

    public async Task StartAsync()
    {
        ConfigurationValidator.Validate(_configuration);

        lock (_lifecycleLock)
        {
            if (_cancellation is not null)
            {
                throw new InvalidOperationException("The redirect is already running.");
            }

            _cancellation = new CancellationTokenSource();
            _bytesForwarded = 0;
            _packetsForwarded = 0;
        }

        try
        {
            var cancellationToken = _cancellation.Token;
            _sink = EndpointFactory.CreateSink(_configuration.Target, _configuration, Log);
            await _sink.StartAsync(cancellationToken).ConfigureAwait(false);

            _source = EndpointFactory.CreateSource(_configuration.Source, _configuration, Log);
            _pipelineTask = RunPipelineAsync(_source, _sink, cancellationToken);

            Log($"Started {_configuration.Source.Protocol} → {_configuration.Target.Protocol}", false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        Task? pipeline;
        CancellationTokenSource? cancellation;

        lock (_lifecycleLock)
        {
            cancellation = _cancellation;
            pipeline = _pipelineTask;
            if (cancellation is null)
            {
                return;
            }

            _cancellation = null;
        }

        cancellation.Cancel();

        if (pipeline is not null)
        {
            try
            {
                await pipeline.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_source is not null)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
            _source = null;
        }

        if (_sink is not null)
        {
            await _sink.DisposeAsync().ConfigureAwait(false);
            _sink = null;
        }

        cancellation.Dispose();
        _pipelineTask = null;
        Log("Stopped", false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunPipelineAsync(IByteSource source, IByteSink sink, CancellationToken cancellationToken)
    {
        try
        {
            await source.RunAsync(async (data, isDatagram) =>
            {
                await sink.WriteAsync(data, isDatagram, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _bytesForwarded, data.Length);
                Interlocked.Increment(ref _packetsForwarded);
            }, cancellationToken).ConfigureAwait(false);

            if (!cancellationToken.IsCancellationRequested)
            {
                Log("Source ended", false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log(exception.Message, true);
        }
    }

    private void Log(string message, bool isError)
    {
        _log(message, isError);
        StatusChanged?.Invoke(this, new RedirectStatusEventArgs(message, isError));
    }
}

internal delegate ValueTask DataHandler(ReadOnlyMemory<byte> data, bool isDatagram);

internal interface IByteSource : IAsyncDisposable
{
    Task RunAsync(DataHandler publish, CancellationToken cancellationToken);
}

internal interface IByteSink : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool isDatagram, CancellationToken cancellationToken);
}

internal static class EndpointFactory
{
    public static IByteSource CreateSource(EndpointConfig config, RedirectConfig redirectConfig, Action<string, bool> log)
    {
        return config.Protocol switch
        {
            EndpointProtocol.Udp => new UdpSource(config, redirectConfig.MulticastInterfaces, redirectConfig.BufferSize, log),
            EndpointProtocol.TcpClient => new TcpClientSource(config, redirectConfig.BufferSize, log),
            EndpointProtocol.TcpServer => new TcpServerSource(config, redirectConfig.BufferSize, log),
            EndpointProtocol.Serial => new SerialSource(config, redirectConfig.BufferSize, log),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public static IByteSink CreateSink(EndpointConfig config, RedirectConfig redirectConfig, Action<string, bool> log)
    {
        return config.Protocol switch
        {
            EndpointProtocol.Udp => new UdpSink(config, redirectConfig, log),
            EndpointProtocol.TcpClient => new TcpClientSink(config, log),
            EndpointProtocol.TcpServer => new TcpServerSink(config, log),
            EndpointProtocol.Serial => new SerialSink(config, log),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}

internal static class SocketHelpers
{
    public static Socket CreateUdpSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = false
        };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        return socket;
    }

    public static IPAddress ParseBindAddress(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host is "0.0.0.0" or "*")
        {
            return IPAddress.Any;
        }

        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"Invalid IPv4 bind address: {host}");
        }

        return address;
    }

    public static int NormalizeBufferSize(int requested) => Math.Clamp(requested, 4096, 65535);
}

internal sealed class UdpSource : IByteSource
{
    private readonly EndpointConfig _config;
    private readonly IReadOnlyList<string> _interfaceAddresses;
    private readonly int _bufferSize;
    private readonly Action<string, bool> _log;
    private readonly List<Socket> _sockets = [];

    public UdpSource(EndpointConfig config, IReadOnlyList<string> interfaceAddresses, int bufferSize, Action<string, bool> log)
    {
        _config = config;
        _interfaceAddresses = interfaceAddresses;
        _bufferSize = SocketHelpers.NormalizeBufferSize(bufferSize);
        _log = log;
    }

    public async Task RunAsync(DataHandler publish, CancellationToken cancellationToken)
    {
        if (ConfigurationValidator.IsMulticastAddress(_config.Host))
        {
            await RunMulticastAsync(IPAddress.Parse(_config.Host), publish, cancellationToken).ConfigureAwait(false);
            return;
        }

        var socket = SocketHelpers.CreateUdpSocket();
        _sockets.Add(socket);
        socket.Bind(new IPEndPoint(SocketHelpers.ParseBindAddress(_config.Host), _config.Port));
        _log($"UDP listening on {socket.LocalEndPoint}", false);
        await ReceiveLoopAsync(socket, publish, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var socket in _sockets)
        {
            socket.Dispose();
        }

        _sockets.Clear();
        return ValueTask.CompletedTask;
    }

    private async Task RunMulticastAsync(IPAddress group, DataHandler publish, CancellationToken cancellationToken)
    {
        var interfaces = NetworkInterfaceCatalog.ResolveSelected(_interfaceAddresses);
        if (interfaces.Count == 0)
        {
            throw new InvalidOperationException("No active IPv4 interface is available for multicast.");
        }

        _log($"Joining {group}:{_config.Port} on {interfaces.Count} IPv4 interface(s)", false);
        var loops = new List<Task>(interfaces.Count);

        foreach (var interfaceAddress in interfaces)
        {
            var socket = SocketHelpers.CreateUdpSocket();
            _sockets.Add(socket);
            socket.Bind(new IPEndPoint(IPAddress.Any, _config.Port));
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(group, interfaceAddress));
            loops.Add(ReceiveLoopAsync(socket, publish, cancellationToken));
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(Socket socket, DataHandler publish, CancellationToken cancellationToken)
    {
        var buffer = new byte[_bufferSize];
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveFromAsync(
                buffer.AsMemory(),
                SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0),
                cancellationToken).ConfigureAwait(false);

            if (result.ReceivedBytes > 0)
            {
                await publish(buffer.AsMemory(0, result.ReceivedBytes), true).ConfigureAwait(false);
            }
        }
    }
}

internal sealed class UdpSink : IByteSink
{
    private readonly EndpointConfig _config;
    private readonly RedirectConfig _redirectConfig;
    private readonly Action<string, bool> _log;
    private readonly List<(Socket Socket, IPEndPoint Endpoint)> _routes = [];

    public UdpSink(EndpointConfig config, RedirectConfig redirectConfig, Action<string, bool> log)
    {
        _config = config;
        _redirectConfig = redirectConfig;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var destination = await ResolveDestinationAsync(_config.Host, _config.Port).ConfigureAwait(false);
        var selectedInterfaces = _redirectConfig.MulticastInterfaces;
        var isMulticast = ConfigurationValidator.IsMulticastAddress(destination.Address);
        var selected = selectedInterfaces.Count == 0
            ? (isMulticast ? NetworkInterfaceCatalog.ResolveSelected([]) : Array.Empty<IPAddress>())
            : NetworkInterfaceCatalog.ResolveSelected(selectedInterfaces);

        if (isMulticast && selected.Count == 0)
        {
            throw new InvalidOperationException("No active IPv4 interface is available for multicast egress.");
        }

        if (selected.Count == 0)
        {
            var socket = SocketHelpers.CreateUdpSocket();
            _routes.Add((socket, destination));
        }
        else
        {
            foreach (var interfaceAddress in selected)
            {
                var socket = SocketHelpers.CreateUdpSocket();
                socket.Bind(new IPEndPoint(interfaceAddress, 0));
                if (isMulticast)
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface,
                        interfaceAddress.GetAddressBytes());
                }

                _routes.Add((socket, destination));
            }
        }

        _log($"UDP sending to {destination} via {_routes.Count} route(s)", false);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool isDatagram, CancellationToken cancellationToken)
    {
        if (_routes.Count == 0)
        {
            throw new InvalidOperationException("UDP sink is not started.");
        }

        var sends = _routes.Select(route => route.Socket.SendToAsync(
            data,
            SocketFlags.None,
            route.Endpoint,
            cancellationToken).AsTask());
        var results = await Task.WhenAll(sends).ConfigureAwait(false);
        if (results.Any(result => result != data.Length))
        {
            throw new IOException("The UDP socket did not send the complete datagram.");
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var route in _routes)
        {
            route.Socket.Dispose();
        }

        _routes.Clear();
        return ValueTask.CompletedTask;
    }

    private static async Task<IPEndPoint> ResolveDestinationAsync(string host, int port)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("Only IPv4 UDP destinations are supported.");
            }

            return new IPEndPoint(address, port);
        }

        var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        var ipv4 = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);
        return ipv4 is null
            ? throw new ArgumentException($"Could not resolve an IPv4 address for {host}.")
            : new IPEndPoint(ipv4, port);
    }
}

internal sealed class TcpClientSource : IByteSource
{
    private readonly EndpointConfig _config;
    private readonly int _bufferSize;
    private readonly Action<string, bool> _log;

    public TcpClientSource(EndpointConfig config, int bufferSize, Action<string, bool> log)
    {
        _config = config;
        _bufferSize = SocketHelpers.NormalizeBufferSize(bufferSize);
        _log = log;
    }

    public async Task RunAsync(DataHandler publish, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient(AddressFamily.InterNetwork)
                {
                    NoDelay = true
                };
                await client.ConnectAsync(_config.Host, _config.Port, cancellationToken).ConfigureAwait(false);
                _log($"TCP connected to {_config.Host}:{_config.Port}", false);

                using var stream = client.GetStream();
                var buffer = new byte[_bufferSize];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await publish(buffer.AsMemory(0, read), false).ConfigureAwait(false);
                }

                _log("TCP source disconnected; reconnecting", false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log($"TCP source: {exception.Message}; retrying", true);
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TcpServerSource : IByteSource
{
    private readonly EndpointConfig _config;
    private readonly int _bufferSize;
    private readonly Action<string, bool> _log;
    private TcpListener? _listener;

    public TcpServerSource(EndpointConfig config, int bufferSize, Action<string, bool> log)
    {
        _config = config;
        _bufferSize = SocketHelpers.NormalizeBufferSize(bufferSize);
        _log = log;
    }

    public async Task RunAsync(DataHandler publish, CancellationToken cancellationToken)
    {
        _listener = new TcpListener(SocketHelpers.ParseBindAddress(_config.Host), _config.Port);
        _listener.Start();
        _log($"TCP listening on {_listener.LocalEndpoint}", false);

        var clients = new ConcurrentBag<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                _log($"TCP client connected: {client.Client.RemoteEndPoint}", false);
                clients.Add(ReadClientAsync(client, publish, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
            _listener = null;
            await Task.WhenAll(clients.ToArray()).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener?.Stop();
        _listener = null;
        return ValueTask.CompletedTask;
    }

    private async Task ReadClientAsync(TcpClient client, DataHandler publish, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var buffer = new byte[_bufferSize];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await publish(buffer.AsMemory(0, read), false).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _log($"TCP client: {exception.Message}", true);
            }
            finally
            {
                _log("TCP client disconnected", false);
            }
        }
    }
}

internal sealed class TcpClientSink : IByteSink
{
    private readonly EndpointConfig _config;
    private readonly Action<string, bool> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpClientSink(EndpointConfig config, Action<string, bool> log)
    {
        _config = config;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool isDatagram, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream!.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Disconnect();
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await _stream!.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = new TcpClient(AddressFamily.InterNetwork)
                {
                    NoDelay = true
                };
                await client.ConnectAsync(_config.Host, _config.Port, cancellationToken).ConfigureAwait(false);
                _client = client;
                _stream = client.GetStream();
                _log($"TCP connected to {_config.Host}:{_config.Port}", false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log($"TCP target: {exception.Message}; retrying", true);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Disconnect()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }
}

internal sealed class TcpServerSink : IByteSink
{
    private readonly EndpointConfig _config;
    private readonly Action<string, bool> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _clientLock = new();
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private TaskCompletionSource<NetworkStream> _clientReady = NewClientSource();
    private CancellationTokenSource? _acceptCancellation;
    private Task? _acceptTask;

    public TcpServerSink(EndpointConfig config, Action<string, bool> log)
    {
        _config = config;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(SocketHelpers.ParseBindAddress(_config.Host), _config.Port);
        _listener.Start();
        _acceptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptTask = AcceptLoopAsync(_acceptCancellation.Token);
        _log($"TCP target listening on {_listener.LocalEndpoint}", false);
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool isDatagram, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await WaitForClientAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                ClearClient(stream);
                var replacement = await WaitForClientAsync(cancellationToken).ConfigureAwait(false);
                await replacement.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _acceptCancellation?.Cancel();
        _listener?.Stop();
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        ClearClient(null);
        _acceptCancellation?.Dispose();
        _acceptCancellation = null;
        _listener = null;
        _writeLock.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                NetworkStream? oldStream;
                lock (_clientLock)
                {
                    oldStream = _stream;
                    _client = client;
                    _stream = client.GetStream();
                    _clientReady.TrySetResult(_stream);
                }

                oldStream?.Dispose();
                _log($"TCP target client connected: {client.Client.RemoteEndPoint}", false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task<NetworkStream> WaitForClientAsync(CancellationToken cancellationToken)
    {
        lock (_clientLock)
        {
            if (_stream is not null)
            {
                return Task.FromResult(_stream);
            }

            return _clientReady.Task.WaitAsync(cancellationToken);
        }
    }

    private void ClearClient(NetworkStream? expected)
    {
        lock (_clientLock)
        {
            if (expected is not null && !ReferenceEquals(expected, _stream))
            {
                return;
            }

            _stream?.Dispose();
            _client?.Dispose();
            _stream = null;
            _client = null;
            _clientReady = NewClientSource();
        }
    }

    private static TaskCompletionSource<NetworkStream> NewClientSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class SerialSource : IByteSource
{
    private readonly EndpointConfig _config;
    private readonly int _bufferSize;
    private readonly Action<string, bool> _log;
    private SerialPort? _serialPort;

    public SerialSource(EndpointConfig config, int bufferSize, Action<string, bool> log)
    {
        _config = config;
        _bufferSize = SocketHelpers.NormalizeBufferSize(bufferSize);
        _log = log;
    }

    public async Task RunAsync(DataHandler publish, CancellationToken cancellationToken)
    {
        _serialPort = SerialPortFactory.Open(_config);
        using var cancelRegistration = cancellationToken.Register(() =>
        {
            try
            {
                _serialPort?.Close();
            }
            catch
            {
                // Best effort: closing a serial port is only used to unblock ReadAsync.
            }
        });

        try
        {
            _log($"Serial listening on {_config.SerialPort} at {_config.BaudRate} baud", false);
            var buffer = new byte[_bufferSize];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _serialPort.BaseStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read > 0)
                {
                    await publish(buffer.AsMemory(0, read), false).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _serialPort.Dispose();
            _serialPort = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _serialPort?.Dispose();
        _serialPort = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class SerialSink : IByteSink
{
    private readonly EndpointConfig _config;
    private readonly Action<string, bool> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SerialPort? _serialPort;

    public SerialSink(EndpointConfig config, Action<string, bool> log)
    {
        _config = config;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serialPort = SerialPortFactory.Open(_config);
        _log($"Serial target opened on {_config.SerialPort} at {_config.BaudRate} baud", false);
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool isDatagram, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _serialPort!.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _serialPort?.Dispose();
        _serialPort = null;
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class SerialPortFactory
{
    public static SerialPort Open(EndpointConfig config)
    {
        var serialPort = new SerialPort(config.SerialPort, config.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadBufferSize = 65536,
            WriteBufferSize = 65536,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = SerialPort.InfiniteTimeout
        };
        serialPort.Open();
        return serialPort;
    }
}
