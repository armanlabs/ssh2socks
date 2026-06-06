using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ssh2socks;

public class Socks5Handler
{
    // SOCKS5 Constants
    public const byte SOCKS_VERSION = 0x05;
    private const byte AUTH_NO_AUTH = 0x00;
    private const byte AUTH_NO_ACCEPTABLE = 0xFF;
    public const byte CMD_CONNECT = 0x01;
    public const byte CMD_UDP_ASSOCIATE = 0x03;
    public const byte ADDR_IPV4 = 0x01;
    public const byte ADDR_DOMAIN = 0x03;
    public const byte ADDR_IPV6 = 0x04;
    private const byte REPLY_SUCCESS = 0x00;
    private const byte REPLY_FAILURE = 0x01;
    private const byte REPLY_CMD_NOT_SUPPORTED = 0x07;
    private const byte REPLY_ADDR_NOT_SUPPORTED = 0x08;


    public record ConnectionRequest(byte Command, string TargetHost, int TargetPort);

  
    public static async Task NegotiateAuthAsync(NetworkStream stream, CancellationToken ct)
    {
  
        var header = await ReadExactAsync(stream, 2, ct);

        if (header[0] != SOCKS_VERSION)
            throw new InvalidOperationException($"SOCKS version {header[0]} not supported");

        var numMethods = header[1];
        var methods = await ReadExactAsync(stream, numMethods, ct);

        if (!methods.Contains(AUTH_NO_AUTH))
        {
            await stream.WriteAsync(new byte[] { SOCKS_VERSION, AUTH_NO_ACCEPTABLE }, ct);
            throw new InvalidOperationException("Client does not support no-auth method");
        }

        await stream.WriteAsync(new byte[] { SOCKS_VERSION, AUTH_NO_AUTH }, ct);
    }

 
    public static async Task<ConnectionRequest> ReadConnectionRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        // VER | CMD | RSV | ATYP
        var header = await ReadExactAsync(stream, 4, ct);

        if (header[0] != SOCKS_VERSION)
            throw new InvalidOperationException("Invalid SOCKS version in request");

        if (header[1] != CMD_CONNECT && header[1] != CMD_UDP_ASSOCIATE)
        {
            await SendReplyAsync(stream, REPLY_CMD_NOT_SUPPORTED, ct);
            throw new InvalidOperationException($"Command {header[1]} not supported");
        }

        var command = header[1];
        var addressType = header[3];
        string targetHost;

        switch (addressType)
        {
            case ADDR_IPV4:
            {
                var ipBytes = await ReadExactAsync(stream, 4, ct);
                targetHost = new IPAddress(ipBytes).ToString();
                break;
            }
            case ADDR_DOMAIN:
            {
                var lenBuf = await ReadExactAsync(stream, 1, ct);
                var domainBytes = await ReadExactAsync(stream, lenBuf[0], ct);
                targetHost = Encoding.ASCII.GetString(domainBytes);
                break;
            }
            case ADDR_IPV6:
            {
                var ipBytes = await ReadExactAsync(stream, 16, ct);
                targetHost = new IPAddress(ipBytes).ToString();
                break;
            }
            default:
                await SendReplyAsync(stream, REPLY_ADDR_NOT_SUPPORTED, ct);
                throw new InvalidOperationException($"Address type {addressType} not supported");
        }

        var portBytes = await ReadExactAsync(stream, 2, ct);
        var targetPort = (portBytes[0] << 8) | portBytes[1];

        return new ConnectionRequest(command, targetHost, targetPort);
    }

    public static async Task SendSuccessReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        await SendReplyAsync(stream, REPLY_SUCCESS, ct);
    }

    public static async Task SendSuccessReplyAsync(NetworkStream stream, IPEndPoint bindEndPoint, CancellationToken ct)
    {
        var addressBytes = bindEndPoint.Address.MapToIPv4().GetAddressBytes();
        var port = bindEndPoint.Port;

        var reply = new byte[]
        {
            SOCKS_VERSION,
            REPLY_SUCCESS,
            0x00,
            ADDR_IPV4,
            addressBytes[0],
            addressBytes[1],
            addressBytes[2],
            addressBytes[3],
            (byte)(port >> 8),
            (byte)(port & 0xFF)
        };

        await stream.WriteAsync(reply, ct);
    }

    public static async Task SendFailureReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        await SendReplyAsync(stream, REPLY_FAILURE, ct);
    }

    private static async Task SendReplyAsync(NetworkStream stream, byte replyCode, CancellationToken ct)
    {
        // VER | REP | RSV | ATYP(IPv4) | BIND.ADDR (4 bytes 0) | BIND.PORT (2 bytes 0)
        var reply = new byte[]
        {
            SOCKS_VERSION,
            replyCode,
            0x00,         // RSV
            ADDR_IPV4,    // ATYP
            0x00, 0x00, 0x00, 0x00,  // BND.ADDR
            0x00, 0x00    // BND.PORT
        };
        await stream.WriteAsync(reply, ct);
    }

    public static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var totalRead = 0;

        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
            if (read == 0)
                throw new EndOfStreamException("Connection closed by client");
            totalRead += read;
        }

        return buffer;
    }
}
