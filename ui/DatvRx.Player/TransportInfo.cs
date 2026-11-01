using System.Text;

namespace DatvRx.Player;

public sealed record StreamService(string Name = "", string Provider = "", string Video = "", string Audio = "");

// PAT/PMT/SDT extraction adapted from the Pi's ts_info.py. Tables must fit in a
// single TS payload. Carries partial input packets across arbitrary pipe reads.
public sealed class TransportInfo
{
    private readonly List<byte> _pending = [];
    private int _pmt = -1;
    public StreamService Current { get; private set; } = new();
    public event Action<StreamService> Changed;
    public void Feed(byte[] data)
    {
        _pending.AddRange(data);
        int offset = 0;
        while (offset + 188 <= _pending.Count)
        {
            if (_pending[offset] != 0x47) { offset++; continue; }
            Parse(_pending.GetRange(offset, 188).ToArray()); offset += 188;
        }
        if (offset > 0) _pending.RemoveRange(0, offset);
    }
    private void Parse(byte[] packet)
    {
        if ((packet[1] & 0xc0) != 0x40) return;
        int pid = (packet[1] & 31) * 256 + packet[2];
        if (pid != 0 && pid != 0x11 && pid != _pmt) return;
        int control = packet[3] >> 4 & 3;
        if ((control & 1) == 0) return;
        int start = 4 + ((control & 2) != 0 ? 1 + packet[4] : 0);
        if (start >= 188) return;
        start += 1 + packet[start];
        if (start + 3 >= 188) return;
        int size = 3 + ((packet[start + 1] & 15) * 256 + packet[start + 2]);
        if (size < 12 || start + size > 188) return;
        byte[] section = packet.AsSpan(start, size).ToArray();
        var previous = Current;
        if (pid == 0 && section[0] == 0)
        {
            for (int i = 8; i + 4 <= size - 4; i += 4)
                if (section[i] * 256 + section[i + 1] != 0) { _pmt = (section[i + 2] & 31) * 256 + section[i + 3]; break; }
        }
        else if (pid == 0x11 && section[0] == 0x42)
        {
            int i = 11;
            while (i + 5 <= size - 4)
            {
                int end = Math.Min(size - 4, i + 5 + (section[i + 3] & 15) * 256 + section[i + 4]);
                int j = i + 5;
                while (j + 2 <= end)
                {
                    int length = section[j + 1], body = j + 2;
                    if (body + length > end) break;
                    if (section[j] == 0x48 && length >= 3)
                    {
                        int providerSize = section[body + 1], namePos = body + 2 + providerSize;
                        if (namePos < body + length && namePos + 1 + section[namePos] <= body + length)
                            Current = Current with { Provider = Text(section.AsSpan(body + 2, providerSize)), Name = Text(section.AsSpan(namePos + 1, section[namePos])) };
                    }
                    j = body + length;
                }
                if (end <= i) break;
                i = end;
            }
        }
        else if (pid == _pmt && section[0] == 2)
        {
            int i = 12 + (section[10] & 15) * 256 + section[11];
            string video = "", audio = "";
            while (i + 5 <= size - 4)
            {
                byte kind = section[i];
                if (video.Length == 0) video = kind switch { 1 => "MPEG-1", 2 => "MPEG-2", 0x10 => "MPEG-4", 0x1b => "H.264", 0x24 => "H.265", 0x33 => "H.266", _ => "" };
                if (audio.Length == 0) audio = kind switch { 3 or 4 => "MPEG audio", 0x0f or 0x11 => "AAC", 0x81 => "AC-3", 0x87 => "E-AC-3", _ => "" };
                i += 5 + (section[i + 3] & 15) * 256 + section[i + 4];
            }
            Current = Current with { Video = video, Audio = audio };
        }
        if (Current != previous) Changed?.Invoke(Current);
    }
    private static string Text(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty && data[0] < 32) data = data[Math.Min(data.Length, data[0] == 0x10 ? 3 : 1)..];
        return new string(Encoding.Latin1.GetString(data).Where(c => !char.IsControl(c)).ToArray()).Trim();
    }
}
