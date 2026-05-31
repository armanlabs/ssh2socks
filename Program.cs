using Microsoft.Extensions.Logging;
using ssh2socks;

ProxyConfig config;
try
{
    config = ConfigLoader.Load(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    Console.Error.WriteLine("Create a .env file from .env.example, or pass: ssh2socks <host> <user> <password-or-key> [listen-port]");
    return 1;
}

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddSimpleConsole(opts =>
        {
            opts.SingleLine = true;
            opts.TimestampFormat = "HH:mm:ss ";
        })
        .SetMinimumLevel(config.Verbose ? LogLevel.Debug : LogLevel.Information);
});

var logger = loggerFactory.CreateLogger<SocksProxyServer>();

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.LogInformation("Shutting down...");
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Console.WriteLine("""
  ====================================
       ssh2socks  v1.0
  ====================================
""");

using var proxy = new SocksProxyServer(config, loggerFactory.CreateLogger<SocksProxyServer>());

try
{
    await proxy.StartAsync(cts.Token);
}
catch (OperationCanceledException)
{
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Fatal error");
    return 1;
}

logger.LogInformation("Proxy stopped.");
return 0;
