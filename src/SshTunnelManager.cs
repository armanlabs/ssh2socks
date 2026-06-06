using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace ssh2socks;


public class SshTunnelManager : IDisposable
{
    private readonly ProxyConfig _config;
    private readonly ILogger<SshTunnelManager> _logger;
    private SshClient? _sshClient;
    private bool _disposed;

    public SshTunnelManager(ProxyConfig config, ILogger<SshTunnelManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _sshClient = BuildSshClient();
        await Task.Run(() => _sshClient.Connect(), ct);
        _sshClient.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _logger.LogInformation("SSH connected to {Host}:{Port}", _config.SshHost, _config.SshPort);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sshClient?.Dispose();
    }
}
