using System.Net;
using System.Net.Sockets;
using System.Buffers;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace ssh2socks;

public class SocksProxyServer : IDisposable
{
    private static readonly TimeSpan ForwardIdleTimeout = TimeSpan.FromSeconds(10);
    private const int DnsPort = 53;

    private readonly ProxyConfig _config;
    private readonly ILogger<SocksProxyServer> _logger;
    private readonly SemaphoreSlim _connectionSemaphore;
    private readonly SemaphoreSlim _sshLock = new(1, 1);
    private readonly object _forwardLock = new();
    private readonly Dictionary<string, TargetForward> _forwards = new(StringComparer.OrdinalIgnoreCase);

    private TcpListener? _listener;
    private SshClient? _sshClient;
    private int _activeConnections;
    private bool _disposed;

    public SocksProxyServer(ProxyConfig config, ILogger<SocksProxyServer> logger)
    {
        _config = config;
        _logger = logger;
        _connectionSemaphore = new SemaphoreSlim(config.MaxConnections, config.MaxConnections);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _sshClient = BuildSshClient();
        _logger.LogInformation("Connecting to SSH {User}@{Host}:{Port}...",
            _config.SshUsername, _config.SshHost, _config.SshPort);

        await Task.Run(() => _sshClient.Connect(), ct);
        _sshClient.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _logger.LogInformation("SSH connected");

        var endpoint = new IPEndPoint(IPAddress.Parse(_config.ListenAddress), _config.ListenPort);
        _listener = new TcpListener(endpoint);
        _listener.Start();

        _logger.LogInformation("SOCKS5 proxy listening on {Address}:{Port}",
            _config.ListenAddress, _config.ListenPort);

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

        if (!await _connectionSemaphore.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            _logger.LogWarning("Max connections reached, rejecting {Client}", clientEndpoint);
            client.Dispose();
            return;
        }

        Interlocked.Increment(ref _activeConnections);
        _logger.LogDebug("New client {Client} (active: {Count})", clientEndpoint, _activeConnections);

        try
        {
            ConfigureTcpClient(client);

            using var networkStream = client.GetStream();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.ConnectionTimeoutSeconds * 10));

            await Socks5Handler.NegotiateAuthAsync(networkStream, cts.Token);
            var request = await Socks5Handler.ReadConnectionRequestAsync(networkStream, cts.Token);

            if (request.Command == Socks5Handler.CMD_UDP_ASSOCIATE)
            {
                _logger.LogInformation("UDP ASSOCIATE from {Client}", clientEndpoint);
                await EnsureSshConnectedAsync(ct);
                await HandleUdpAssociateAsync(networkStream, cts.Token);
                return;
            }

            _logger.LogInformation("CONNECT {Host}:{Port}", request.TargetHost, request.TargetPort);

            await EnsureSshConnectedAsync(ct);

            Stream tunnelStream;
            try
            {
                tunnelStream = await OpenViaCachedForwardingAsync(
                    request.TargetHost,
                    request.TargetPort,
                    cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open SSH tunnel to {Host}:{Port}",
                    request.TargetHost, request.TargetPort);
                await Socks5Handler.SendFailureReplyAsync(networkStream, cts.Token);
                return;
            }

            await Socks5Handler.SendSuccessReplyAsync(networkStream, cts.Token);
            await RelayDataAsync(networkStream, tunnelStream, _config.RelayBufferSize, cts.Token);

            _logger.LogDebug("Connection {Host}:{Port} closed", request.TargetHost, request.TargetPort);
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client {Client}", clientEndpoint);
        }
        finally
        {
            client.Dispose();
            Interlocked.Decrement(ref _activeConnections);
            _connectionSemaphore.Release();
        }
    }

    private async Task<Stream> OpenViaCachedForwardingAsync(string targetHost, int targetPort, CancellationToken ct)
    {
        var forward = await RentForwardAsync(targetHost, targetPort, ct);
        var tcpClient = new TcpClient
        {
            NoDelay = true
        };
        ConfigureTcpClient(tcpClient);

        try
        {
            await tcpClient.ConnectAsync(IPAddress.Loopback, forward.LocalPort, ct);
            return new ForwardedTcpStream(tcpClient, forward.ForwardedPort, () => ReleaseForward(forward.Key));
        }
        catch
        {
            tcpClient.Dispose();
            ReleaseForward(forward.Key);
            throw;
        }
    }

    private async Task HandleUdpAssociateAsync(NetworkStream controlStream, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var bindEndPoint = (IPEndPoint)udp.Client.LocalEndPoint!;

        await Socks5Handler.SendSuccessReplyAsync(controlStream, bindEndPoint, ct);
        _logger.LogDebug("UDP associate bound on {Address}:{Port}", bindEndPoint.Address, bindEndPoint.Port);

        var controlClosed = WaitForControlConnectionCloseAsync(controlStream, ct);

        while (!ct.IsCancellationRequested)
        {
            var receiveTask = udp.ReceiveAsync(ct).AsTask();
            var completed = await Task.WhenAny(receiveTask, controlClosed);

            if (completed == controlClosed)
            {
                break;
            }

            UdpReceiveResult received;
            try
            {
                received = await receiveTask;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!TryParseSocksUdpPacket(received.Buffer, out var packet))
            {
                continue;
            }

            _ = ProcessDnsUdpPacketAsync(udp, received.RemoteEndPoint, packet, ct);
        }
    }

    private static async Task WaitForControlConnectionCloseAsync(NetworkStream controlStream, CancellationToken ct)
    {
        var buffer = new byte[1];

        while (!ct.IsCancellationRequested)
        {
            var read = await controlStream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return;
            }
        }
    }

    private async Task ProcessDnsUdpPacketAsync(
        UdpClient udp,
        IPEndPoint clientEndPoint,
        SocksUdpPacket packet,
        CancellationToken ct)
    {
        if (packet.TargetPort != DnsPort)
        {
            _logger.LogDebug("Dropping UDP packet for {Host}:{Port}; only DNS/53 is supported",
                packet.TargetHost, packet.TargetPort);
            return;
        }

        try
        {
            using var dnsTcpStream = await OpenViaCachedForwardingAsync(packet.TargetHost, DnsPort, ct);
            var dnsResponse = await QueryDnsOverTcpAsync(dnsTcpStream, packet.Payload, ct);
            var socksResponse = BuildSocksUdpPacket(packet, dnsResponse);

            await udp.SendAsync(socksResponse, clientEndPoint, ct);
            _logger.LogDebug("Relayed DNS UDP over TCP via {Host}:53", packet.TargetHost);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DNS UDP-over-TCP relay failed for {Host}:53", packet.TargetHost);
        }
    }

    private static async Task<byte[]> QueryDnsOverTcpAsync(Stream stream, byte[] dnsQuery, CancellationToken ct)
    {
        if (dnsQuery.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("DNS query is too large");
        }

        var lengthPrefix = new[]
        {
            (byte)(dnsQuery.Length >> 8),
            (byte)(dnsQuery.Length & 0xFF)
        };

        await stream.WriteAsync(lengthPrefix, ct);
        await stream.WriteAsync(dnsQuery, ct);
        await stream.FlushAsync(ct);

        var responseLengthPrefix = await ReadExactAsync(stream, 2, ct);
        var responseLength = (responseLengthPrefix[0] << 8) | responseLengthPrefix[1];

        return await ReadExactAsync(stream, responseLength, ct);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var totalRead = 0;

        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
            if (read == 0)
            {
                throw new EndOfStreamException("Stream closed before enough bytes were read");
            }

            totalRead += read;
        }

        return buffer;
    }

    private static bool TryParseSocksUdpPacket(byte[] buffer, out SocksUdpPacket packet)
    {
        packet = default!;

        if (buffer.Length < 7 || buffer[0] != 0x00 || buffer[1] != 0x00 || buffer[2] != 0x00)
        {
            return false;
        }

        var addressType = buffer[3];
        var offset = 4;
        string targetHost;
        byte[] addressBytes;

        switch (addressType)
        {
            case Socks5Handler.ADDR_IPV4:
                if (buffer.Length < offset + 4 + 2) return false;
                addressBytes = buffer[offset..(offset + 4)];
                targetHost = new IPAddress(addressBytes).ToString();
                offset += 4;
                break;

            case Socks5Handler.ADDR_DOMAIN:
                if (buffer.Length < offset + 1) return false;
                var domainLength = buffer[offset++];
                if (buffer.Length < offset + domainLength + 2) return false;
                addressBytes = buffer[(offset - 1)..(offset + domainLength)];
                targetHost = System.Text.Encoding.ASCII.GetString(buffer, offset, domainLength);
                offset += domainLength;
                break;

            case Socks5Handler.ADDR_IPV6:
                if (buffer.Length < offset + 16 + 2) return false;
                addressBytes = buffer[offset..(offset + 16)];
                targetHost = new IPAddress(addressBytes).ToString();
                offset += 16;
                break;

            default:
                return false;
        }

        var targetPort = (buffer[offset] << 8) | buffer[offset + 1];
        offset += 2;

        if (buffer.Length <= offset)
        {
            return false;
        }

        packet = new SocksUdpPacket(
            addressType,
            addressBytes,
            targetHost,
            targetPort,
            buffer[offset..]);

        return true;
    }

    private static byte[] BuildSocksUdpPacket(SocksUdpPacket request, byte[] payload)
    {
        var response = new byte[4 + request.AddressBytes.Length + 2 + payload.Length];

        response[0] = 0x00;
        response[1] = 0x00;
        response[2] = 0x00;
        response[3] = request.AddressType;

        var offset = 4;
        request.AddressBytes.CopyTo(response, offset);
        offset += request.AddressBytes.Length;

        response[offset++] = (byte)(request.TargetPort >> 8);
        response[offset++] = (byte)(request.TargetPort & 0xFF);
        payload.CopyTo(response, offset);

        return response;
    }

    private async Task<TargetForward> RentForwardAsync(string targetHost, int targetPort, CancellationToken ct)
    {
        var key = MakeForwardKey(targetHost, targetPort);

        lock (_forwardLock)
        {
            if (_forwards.TryGetValue(key, out var existing) && existing.ForwardedPort.IsStarted)
            {
                existing.ActiveStreams++;
                return existing;
            }
        }

        await _sshLock.WaitAsync(ct);
        try
        {
            if (_sshClient is null || !_sshClient.IsConnected)
            {
                throw new InvalidOperationException("SSH client is not connected");
            }

            lock (_forwardLock)
            {
                if (_forwards.TryGetValue(key, out var existing) && existing.ForwardedPort.IsStarted)
                {
                    existing.ActiveStreams++;
                    return existing;
                }
            }

            var localPort = ReserveLocalPort();
            var forwardedPort = new ForwardedPortLocal("127.0.0.1", (uint)localPort, targetHost, (uint)targetPort);
            var forward = new TargetForward(key, forwardedPort, localPort);

            forwardedPort.Exception += (_, e) =>
                _logger.LogWarning(e.Exception, "ForwardedPort error for {Host}:{Port}", targetHost, targetPort);

            _sshClient.AddForwardedPort(forwardedPort);
            forwardedPort.Start();

            if (!forwardedPort.IsStarted)
            {
                forwardedPort.Dispose();
                throw new InvalidOperationException($"ForwardedPort failed to start for {targetHost}:{targetPort}");
            }

            lock (_forwardLock)
            {
                forward.ActiveStreams = 1;
                _forwards[key] = forward;
            }

            _logger.LogDebug("Forwarded {Host}:{Port} through 127.0.0.1:{LocalPort}",
                targetHost, targetPort, localPort);

            return forward;
        }
        finally
        {
            _sshLock.Release();
        }
    }

    private void ReleaseForward(string key)
    {
        TargetForward? forward;
        DateTime releasedAt;

        lock (_forwardLock)
        {
            if (!_forwards.TryGetValue(key, out forward))
            {
                return;
            }

            forward.ActiveStreams = Math.Max(0, forward.ActiveStreams - 1);
            forward.LastReleasedUtc = DateTime.UtcNow;
            releasedAt = forward.LastReleasedUtc;

            if (forward.ActiveStreams > 0)
            {
                return;
            }
        }

        _ = CloseIdleForwardLaterAsync(key, releasedAt);
    }

    private async Task CloseIdleForwardLaterAsync(string key, DateTime releasedAt)
    {
        try
        {
            await Task.Delay(ForwardIdleTimeout);

            TargetForward? forward;
            lock (_forwardLock)
            {
                if (!_forwards.TryGetValue(key, out forward))
                {
                    return;
                }

                if (forward.ActiveStreams > 0 || forward.LastReleasedUtc != releasedAt)
                {
                    return;
                }

                _forwards.Remove(key);
            }

            StopForward(forward);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring error while closing idle forward {Key}", key);
        }
    }

    private void StopForward(TargetForward forward)
    {
        try
        {
            if (forward.ForwardedPort.IsStarted)
            {
                forward.ForwardedPort.Stop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring error while stopping forward {Key}", forward.Key);
        }
        finally
        {
            forward.ForwardedPort.Dispose();
        }
    }

    private static int ReserveLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string MakeForwardKey(string targetHost, int targetPort)
        => $"{targetHost}:{targetPort}";

    private void ConfigureTcpClient(TcpClient client)
    {
        client.NoDelay = true;
        client.ReceiveBufferSize = _config.SocketBufferSize;
        client.SendBufferSize = _config.SocketBufferSize;
    }

    private static async Task RelayDataAsync(Stream clientStream, Stream tunnelStream, int bufferSize, CancellationToken ct)
    {
        using (tunnelStream)
        {
            var t1 = CopyStreamAsync(clientStream, tunnelStream, bufferSize, ct);
            var t2 = CopyStreamAsync(tunnelStream, clientStream, bufferSize, ct);
            await Task.WhenAny(t1, t2);
        }
    }

    private static async Task CopyStreamAsync(Stream source, Stream destination, int bufferSize, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }
        }
        catch
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task EnsureSshConnectedAsync(CancellationToken ct)
    {
        if (_sshClient?.IsConnected == true) return;

        await _sshLock.WaitAsync(ct);
        try
        {
            if (_sshClient?.IsConnected == true) return;

            _logger.LogWarning("SSH disconnected, reconnecting...");
            StopAllForwards();
            _sshClient?.Dispose();

            _sshClient = BuildSshClient();
            await Task.Run(() => _sshClient.Connect(), ct);
            _sshClient.KeepAliveInterval = TimeSpan.FromSeconds(30);
            _logger.LogInformation("SSH reconnected");
        }
        finally
        {
            _sshLock.Release();
        }
    }

    private SshClient BuildSshClient()
    {
        ConnectionInfo connInfo;

        if (!string.IsNullOrEmpty(_config.SshPrivateKeyPath))
        {
            var expandedPath = _config.SshPrivateKeyPath
                .Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            IPrivateKeySource key = string.IsNullOrEmpty(_config.SshPrivateKeyPassphrase)
                ? new PrivateKeyFile(expandedPath)
                : new PrivateKeyFile(expandedPath, _config.SshPrivateKeyPassphrase);

            connInfo = new ConnectionInfo(_config.SshHost, _config.SshPort, _config.SshUsername,
                new PrivateKeyAuthenticationMethod(_config.SshUsername, key));
        }
        else
        {
            connInfo = new ConnectionInfo(_config.SshHost, _config.SshPort, _config.SshUsername,
                new PasswordAuthenticationMethod(_config.SshUsername, _config.SshPassword!));
        }

        connInfo.Timeout = TimeSpan.FromSeconds(_config.ConnectionTimeoutSeconds);
        return new SshClient(connInfo);
    }

    private void StopAllForwards()
    {
        List<TargetForward> forwards;

        lock (_forwardLock)
        {
            forwards = _forwards.Values.ToList();
            _forwards.Clear();
        }

        foreach (var forward in forwards)
        {
            StopForward(forward);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _listener?.Stop();
        StopAllForwards();
        _sshClient?.Dispose();
        _sshLock.Dispose();
        _connectionSemaphore.Dispose();
    }

    private sealed class TargetForward
    {
        public TargetForward(string key, ForwardedPortLocal forwardedPort, int localPort)
        {
            Key = key;
            ForwardedPort = forwardedPort;
            LocalPort = localPort;
        }

        public string Key { get; }
        public ForwardedPortLocal ForwardedPort { get; }
        public int LocalPort { get; }
        public int ActiveStreams { get; set; }
        public DateTime LastReleasedUtc { get; set; }
    }

    private sealed record SocksUdpPacket(
        byte AddressType,
        byte[] AddressBytes,
        string TargetHost,
        int TargetPort,
        byte[] Payload);
}
