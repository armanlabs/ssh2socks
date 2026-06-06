# ssh2socks

A lightweight SOCKS5 proxy that sends TCP traffic through an SSH tunnel.

The app listens locally, accepts SOCKS5 connections from browsers or tools, opens an SSH-backed tunnel to the requested target, and relays traffic in both directions.

Maintained by [armanlabs](https://github.com/armanlabs).

## Features

- SOCKS5 `CONNECT` support for TCP traffic.
- SSH password or private key authentication.
- Optional `.env` configuration.
- Command-line overrides for quick one-off runs.
- Automatic SSH reconnect.
- Connection limit control.
- Tunable relay and socket buffers for better throughput.
- Basic UDP ASSOCIATE support for DNS/53 by relaying DNS over TCP through the SSH tunnel.

## Feature Comparison: ssh2socks vs. Raw SSH Tunneling

| Feature | Raw `ssh -D` | ssh2socks |
| :--- | :---: | :---: |
| **DNS Leak Protection (UDP Relay)** | ❌ No (Leaks DNS easily) | ⚠️ Partial DNS relay over TCP (experimental) |
| **Auto-Reconnect** | ❌ No (Requires manual restart) | ✅ Yes (Background auto-recovery) |
| **Windows Ease-of-Use** | ⚠️ Hard (Needs CMD/OpenSSH setup) | ✅ Easy (Standalone portable `.exe`) |
| **Configuration Ease** | ⚠️ Hard (Long CLI arguments) | ✅ Easy (Structured `.env` file) |
| **Performance Tuning** | ❌ None (Fixed system TCP buffers) | ✅ Yes (Customizable relay & socket buffers) |
| **Resource Protection** | ❌ None | ✅ Yes (`MAX_CONNECTIONS` limit control) |
## Requirements

- .NET 8 SDK or runtime.
- Access to an SSH server that can reach the target websites/services.

## Build

```bash
dotnet restore
dotnet build
```

## Publish Single-File Release Builds

You can publish self-contained single-file executables for GitHub Releases.

Users who download these release files do not need the .NET SDK, and usually do not need the .NET runtime either. They only need the executable and a `.env` file next to it.

Windows x64:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish/win-x64
```

Linux x64:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish/linux-x64
```

Linux ARM64:

```bash
dotnet publish -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish/linux-arm64
```

macOS Apple Silicon:

```bash
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish/osx-arm64
```

macOS Intel:

```bash
dotnet publish -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish/osx-x64
```

For each release package, put `.env.example` next to the executable so users can copy it to `.env`.

Example release folder:

```text
ssh2socks-win-x64/
+-- ssh2socks.exe
+-- .env.example
+-- README.md
```

On Linux and macOS, users may need to make the file executable:

```bash
chmod +x ./ssh2socks
```

Then they can run it directly:

```bash
./ssh2socks
```

Or on Windows:

```powershell
.\ssh2socks.exe
```

## Automated GitHub Releases

This repository includes a GitHub Actions workflow at `.github/workflows/release.yml`.

When you push a version tag such as `v1.0.0`, GitHub automatically:

1. Restores dependencies.
2. Publishes self-contained single-file builds.
3. Packages release archives.
4. Creates a GitHub Release.
5. Uploads the release assets.

Release targets:

- `win-x64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Each archive contains:

- The executable.
- `.env.example`.
- `README.md`.
- `LICENSE`.

Create and push a release tag:

```bash
git tag v1.0.0
git push origin v1.0.0
```

The workflow can also be started manually from the GitHub Actions tab, but manual runs only build artifacts. A GitHub Release is created only for tag runs.

## Run With `.env`

Create a `.env` file next to `Program.cs`, or next to the published executable.

You can start from the example file:

```powershell
Copy-Item .env.example .env
```

Then edit `.env`:

```env
SSH_HOST=your-server.example.com
SSH_PORT=22
SSH_USERNAME=root

# Use either SSH_PASSWORD or SSH_PRIVATE_KEY_PATH.
SSH_PASSWORD=
SSH_PRIVATE_KEY_PATH=
SSH_PRIVATE_KEY_PASSPHRASE=

LISTEN_ADDRESS=127.0.0.1
LISTEN_PORT=1090
MAX_CONNECTIONS=100
CONNECTION_TIMEOUT_SECONDS=30
RELAY_BUFFER_SIZE=65536
SOCKET_BUFFER_SIZE=262144
VERBOSE=true
```

Run:

```bash
dotnet run
```

## Run With Command-Line Arguments

You can still run the app without putting the main SSH values in `.env`.

With a private key:

```bash
dotnet run -- your-server.com ubuntu ~/.ssh/id_rsa 1080
```

With a password:

```bash
dotnet run -- your-server.com ubuntu mypassword 1080
```

For a published Windows executable:

```powershell
.\ssh2socks.exe your-server.com ubuntu C:\Users\you\.ssh\id_rsa 1080
```

Command-line arguments override these values:

```text
args[0] -> SSH_HOST
args[1] -> SSH_USERNAME
args[2] -> SSH_PASSWORD or SSH_PRIVATE_KEY_PATH
args[3] -> LISTEN_PORT
```

Other settings, such as `SSH_PORT`, `MAX_CONNECTIONS`, `RELAY_BUFFER_SIZE`, and `SOCKET_BUFFER_SIZE`, still come from environment variables, `.env`, or built-in defaults.

## Configuration Priority

Configuration is loaded in this order:

1. Command-line arguments for host, username, password/key, and listen port.
2. Real environment variables.
3. `.env` file.
4. Built-in defaults.

The app looks for `.env` next to the executable first, then in the current working directory, then in parent directories.

## Supported Settings

| Key | Default | Description |
| --- | ---: | --- |
| `SSH_HOST` | required | SSH server hostname or IP. |
| `SSH_PORT` | `22` | SSH server port. |
| `SSH_USERNAME` | required | SSH username. |
| `SSH_PASSWORD` | empty | SSH password. Use this or `SSH_PRIVATE_KEY_PATH`. |
| `SSH_PRIVATE_KEY_PATH` | empty | Path to private key file. Use this or `SSH_PASSWORD`. |
| `SSH_PRIVATE_KEY_PASSPHRASE` | empty | Private key passphrase, if needed. |
| `LISTEN_ADDRESS` | `127.0.0.1` | Local address for the SOCKS5 listener. Keep this local unless you intentionally want LAN access. |
| `LISTEN_PORT` | `1080` | Local SOCKS5 port. |
| `MAX_CONNECTIONS` | `100` | Maximum concurrent client connections. |
| `CONNECTION_TIMEOUT_SECONDS` | `30` | SSH connection timeout. |
| `RELAY_BUFFER_SIZE` | `65536` | Buffer size used while relaying traffic. Clamped between 8 KB and 1 MB. |
| `SOCKET_BUFFER_SIZE` | `262144` | TCP send/receive buffer size. Clamped between 8 KB and 4 MB. |
| `VERBOSE` | `true` | Set to `false` to disable. |

Aliases are accepted for a few common names:

- `SSH_SERVER` or `SSH_SERVER_HOST` for `SSH_HOST`.
- `SSH_USER` or `SSH_LOGIN` for `SSH_USERNAME`.
- `SSH_PASS` for `SSH_PASSWORD`.
- `SSH_KEY_PATH` or `SSH_KEY` for `SSH_PRIVATE_KEY_PATH`.
- `SSH_KEY_PASSPHRASE` for `SSH_PRIVATE_KEY_PASSPHRASE`.

## Browser Setup

Firefox:

1. Open Settings.
2. Search for Network Settings.
3. Select Manual proxy configuration.
4. Set SOCKS Host to `127.0.0.1`.
5. Set Port to your `LISTEN_PORT`, for example `1090`.
6. Select SOCKS v5.
7. Enable remote DNS over SOCKS if available.

Chrome or Chromium:

```bash
google-chrome --proxy-server="socks5://127.0.0.1:1090"
```

curl:

```bash
curl --socks5-hostname 127.0.0.1:1090 https://api.ipify.org
```

Use `--socks5-hostname` when possible so DNS resolution goes through the SOCKS proxy instead of leaking locally.

## Security Notes

Traffic between your machine and the SSH server is encrypted by SSH.

Traffic after it leaves the SSH server depends on the destination protocol:

- HTTPS remains encrypted end to end.
- Plain HTTP is visible between the SSH server and the target website.

Keep `LISTEN_ADDRESS=127.0.0.1` unless you intentionally want other devices to use your proxy.

Treat `.env` files as sensitive if they contain SSH passwords, private key paths, or passphrases. Do not upload them to public repositories or share published folders that include secrets.

## Responsible Use and Disclaimer

This project is provided for legitimate privacy, development, administration, and troubleshooting use cases.

You are responsible for how you use this software and for complying with all applicable laws, service terms, workplace policies, and network rules.

The maintainers do not endorse using this project to bypass access controls, hide unlawful activity, attack systems, violate third-party rights, or abuse networks and services.

This software is provided as-is, without warranties. The maintainers are not responsible for damages, data loss, service disruption, account suspension, legal consequences, or other issues caused by using or modifying this software.

## Performance Notes

The relay loop uses larger buffers and does not flush after every chunk, which improves throughput for long-lived or high-volume connections.

Recommended defaults:

```env
RELAY_BUFFER_SIZE=65536
SOCKET_BUFFER_SIZE=262144
```

If throughput is still low, the bottleneck is usually one of these:

- SSH server CPU.
- SSH server network bandwidth.
- Long network latency.
- Remote destination speed.
- SSH encryption overhead.

## Project Structure

```text
ssh2socks/
+-- Program.cs
+-- ssh2socks.csproj
+-- .github/
+   `-- workflows/
+       `-- release.yml
+-- LICENSE
+-- README.md
+-- .env.example
`-- src/
    +-- ConfigLoader.cs
    +-- ForwardedTcpStream.cs
    +-- ProxyConfig.cs
    +-- Socks5Handler.cs
    +-- SocksProxyServer.cs
    `-- SshTunnelManager.cs
```

## Connection Flow

```text
Client app
  -> SOCKS5 proxy on 127.0.0.1:LISTEN_PORT
  -> SOCKS5 handshake
  -> CONNECT target-host:target-port
  -> SSH tunnel
  -> SSH server
  -> target-host:target-port
```

Once the tunnel is open, the app relays bytes in both directions until either side closes the connection.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
