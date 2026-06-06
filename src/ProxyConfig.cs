namespace ssh2socks;

public class ProxyConfig
{
    // SSH Server
    public string SshHost { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshUsername { get; set; } = "";

    public string? SshPassword { get; set; }
    public string? SshPrivateKeyPath { get; set; }
    public string? SshPrivateKeyPassphrase { get; set; }

    // SOCKS5 Listener
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 1080;
    public int MaxConnections { get; set; } = 100;
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    public int RelayBufferSize { get; set; } = 65536;
    public int SocketBufferSize { get; set; } = 262144;
    public bool Verbose { get; set; } = false;
}
