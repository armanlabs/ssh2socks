namespace ssh2socks;

public static class ConfigLoader
{
    public static ProxyConfig Load(string[] args)
    {
        var values = LoadDotEnv();

        var config = new ProxyConfig
        {
            SshHost = GetAny(values, "SSH_HOST", "SSH_SERVER", "SSH_SERVER_HOST") ?? "",
            SshPort = GetInt(values, "SSH_PORT", 22),
            SshUsername = GetAny(values, "SSH_USERNAME", "SSH_USER", "SSH_LOGIN") ?? "",
            SshPassword = GetAny(values, "SSH_PASSWORD", "SSH_PASS"),
            SshPrivateKeyPath = GetAny(values, "SSH_PRIVATE_KEY_PATH", "SSH_KEY_PATH", "SSH_KEY"),
            SshPrivateKeyPassphrase = GetAny(values, "SSH_PRIVATE_KEY_PASSPHRASE", "SSH_KEY_PASSPHRASE"),
            ListenAddress = Get(values, "LISTEN_ADDRESS") ?? "127.0.0.1",
            ListenPort = GetInt(values, "LISTEN_PORT", 1080),
            MaxConnections = GetInt(values, "MAX_CONNECTIONS", 100),
            ConnectionTimeoutSeconds = GetInt(values, "CONNECTION_TIMEOUT_SECONDS", 30),
            RelayBufferSize = GetInt(values, "RELAY_BUFFER_SIZE", 65536),
            SocketBufferSize = GetInt(values, "SOCKET_BUFFER_SIZE", 262144),
            Verbose = GetBool(values, "VERBOSE", false)
        };

        ApplyCommandLineOverrides(config, args);
        Validate(config);

        return config;
    }

    private static Dictionary<string, string> LoadDotEnv()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = FindDotEnvPath();

        if (path is null)
        {
            return values;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            var value = line[(equalsIndex + 1)..].Trim();

            values[key] = Unquote(value);
        }

        return values;
    }

    private static string? FindDotEnvPath()
    {
        var exactCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env")
        };

        foreach (var path in exactCandidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        var parentSearchRoots = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var root in parentSearchRoots)
        {
            var directory = new DirectoryInfo(root).Parent;

            while (directory is not null)
            {
                var path = Path.Combine(directory.FullName, ".env");
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static void ApplyCommandLineOverrides(ProxyConfig config, string[] args)
    {
        if (args.Length < 3)
        {
            return;
        }

        config.SshHost = args[0];
        config.SshUsername = args[1];

        if (LooksLikePrivateKeyPath(args[2]))
        {
            config.SshPrivateKeyPath = args[2];
            config.SshPassword = null;
        }
        else
        {
            config.SshPassword = args[2];
            config.SshPrivateKeyPath = null;
        }

        if (args.Length >= 4 && int.TryParse(args[3], out var listenPort))
        {
            config.ListenPort = listenPort;
        }
    }

    private static void Validate(ProxyConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SshHost))
        {
            throw new InvalidOperationException("SSH_HOST is required. Set it in .env or pass command-line args.");
        }

        if (config.SshHost.StartsWith("your-", StringComparison.OrdinalIgnoreCase) ||
            config.SshHost.Contains("example.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SSH_HOST looks like a placeholder: {config.SshHost}");
        }

        if (string.IsNullOrWhiteSpace(config.SshUsername))
        {
            throw new InvalidOperationException("SSH_USERNAME is required. Set it in .env or pass command-line args.");
        }

        if (string.IsNullOrWhiteSpace(config.SshPassword) && string.IsNullOrWhiteSpace(config.SshPrivateKeyPath))
        {
            throw new InvalidOperationException("Set SSH_PASSWORD or SSH_PRIVATE_KEY_PATH in .env.");
        }

        config.RelayBufferSize = Math.Clamp(config.RelayBufferSize, 8192, 1048576);
        config.SocketBufferSize = Math.Clamp(config.SocketBufferSize, 8192, 4194304);
    }

    private static string? Get(Dictionary<string, string> values, string key)
    {
        var environmentValue = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string? GetAny(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Get(values, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int GetInt(Dictionary<string, string> values, string key, int defaultValue)
        => int.TryParse(Get(values, key), out var value) ? value : defaultValue;

    private static bool GetBool(Dictionary<string, string> values, string key, bool defaultValue)
        => bool.TryParse(Get(values, key), out var value) ? value : defaultValue;

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static bool LooksLikePrivateKeyPath(string value)
        => value.StartsWith('/') ||
           value.StartsWith('~') ||
           value.Contains(".pem", StringComparison.OrdinalIgnoreCase) ||
           value.Contains(".rsa", StringComparison.OrdinalIgnoreCase);
}
