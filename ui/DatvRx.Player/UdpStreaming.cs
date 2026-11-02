using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace DatvRx.Player;

public static class UdpStreamAddress
{
    public const string Default = "udp://239.255.42.42:1234";
    public static IPEndPoint Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string value = text.Trim().Replace("udp://@", "udp://", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "udp"
            || !IPAddress.TryParse(uri.Host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.UserInfo.Length != 0 || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast))
            throw new ArgumentException("Enter udp:// followed by an IPv4 destination and port, for example udp://239.255.42.42:1234.");
        return new IPEndPoint(ip, uri.Port);
    }
    public static bool Multicast(IPEndPoint endpoint) => endpoint.Address.GetAddressBytes()[0] is >= 224 and <= 239;
    public static string Vlc(string destination)
    {
        var endpoint = Parse(destination);
        return Multicast(endpoint) ? $"udp://@{endpoint}" : $"udp://@:{endpoint.Port}";
    }
}

// Read boundaries in a pipe are arbitrary. UDP datagrams contain seven complete
// 188-byte TS packets (1316 bytes), small enough for a normal Ethernet MTU.
public sealed class TsUdpPacketizer
{
    private readonly List<byte> _pending = [];
    public IEnumerable<byte[]> Feed(byte[] block)
    {
        _pending.AddRange(block);
        int offset = 0;
        while (_pending.Count - offset >= 1316)
        {
            bool aligned = true;
            for (int packet = 0; packet < 7; packet++)
                if (_pending[offset + packet * 188] != 0x47) { aligned = false; break; }
            if (!aligned) { offset++; continue; }
            byte[] datagram = _pending.GetRange(offset, 1316).ToArray();
            offset += 1316;
            yield return datagram;
        }
        if (offset > 0) _pending.RemoveRange(0, offset);
    }
}

public sealed class UdpTsSender : IAsyncDisposable
{
    private readonly UdpClient _client = new(AddressFamily.InterNetwork);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _worker;
    public UdpTsSender(string destination, Action<string> error)
    {
        try
        {
            var endpoint = UdpStreamAddress.Parse(destination);
            _client.Ttl = 1;
            if (UdpStreamAddress.Multicast(endpoint))
                _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            _client.Connect(endpoint);
            _worker = Task.Run(async () =>
            {
                var packets = new TsUdpPacketizer();
                try
                {
                    await foreach (var block in _queue.Reader.ReadAllAsync(_stop.Token))
                        foreach (var datagram in packets.Feed(block))
                            await _client.SendAsync(datagram, _stop.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                { error("UDP streaming: " + ex.Message); }
            });
        }
        catch { _client.Dispose(); _stop.Dispose(); throw; }
    }
    public void Feed(byte[] block) => _queue.Writer.TryWrite(block);
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete(); _stop.Cancel(); _client.Dispose();
        try { await _worker; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
