using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using AionDpsMeter.Services.PacketCapture;
using HamMeter.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HamMeter.Tests;

public class RawSocketFilterTests
{
    private static readonly uint Local = Addr("192.168.178.20");
    private static readonly uint Server = Addr("203.0.113.10");
    private static readonly uint Stranger = Addr("198.51.100.7");
    private const ushort LocalPort = 50123;
    private const ushort ServerPort = 7777;

    private readonly RawSocketCaptureDevice m_device = new(
        new TcpStreamBuffer(NullLogger<TcpStreamBuffer>.Instance),
        NullLogger<RawSocketCaptureDevice>.Instance);

    public RawSocketFilterTests()
    {
        m_device.SetAllowed([new TcpConnection(Local, LocalPort, Server, ServerPort)]);
    }

    [Fact]
    public void PacketFromAionServer_IsAccepted()
    {
        m_device.HandleDatagram(Ip(Server, ServerPort, Local, LocalPort, [1, 2, 3]));
        Assert.Equal(1, m_device.MatchedPackets);
    }

    [Fact]
    public void PacketFromAnyOtherHostOrPort_IsDropped()
    {
        m_device.HandleDatagram(Ip(Stranger, ServerPort, Local, LocalPort, [1]));
        m_device.HandleDatagram(Ip(Server, 443, Local, LocalPort, [1]));
        m_device.HandleDatagram(Ip(Server, ServerPort, Local, 9999, [1]));

        // Client -> server direction is not needed and never read.
        m_device.HandleDatagram(Ip(Local, LocalPort, Server, ServerPort, [1]));

        Assert.Equal(0, m_device.MatchedPackets);
    }

    [Fact]
    public void MalformedHeaders_AreDroppedWithoutThrowing()
    {
        byte[] good = Ip(Server, ServerPort, Local, LocalPort, [1, 2, 3]);

        byte[] tooShort = good[..19];
        byte[] badIhl = (byte[])good.Clone();
        badIhl[0] = 0x44; // header length 16 < 20
        byte[] lyingTotal = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(lyingTotal.AsSpan(2), 60_000);
        byte[] badTcpOffset = (byte[])good.Clone();
        badTcpOffset[20 + 12] = 0xF0; // data offset 60 > segment length
        byte[] ipv6 = (byte[])good.Clone();
        ipv6[0] = 0x65;
        byte[] udp = (byte[])good.Clone();
        udp[9] = 17;

        foreach (byte[] p in new[] { tooShort, badIhl, lyingTotal, badTcpOffset, ipv6, udp, Array.Empty<byte>() })
        {
            m_device.HandleDatagram(p);
        }

        Assert.Equal(0, m_device.MatchedPackets);
    }

    [Fact]
    public void Fragments_AreDropped()
    {
        byte[] p = Ip(Server, ServerPort, Local, LocalPort, [1]);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), 0x2000); // more-fragments
        m_device.HandleDatagram(p);

        Assert.Equal(0, m_device.MatchedPackets);
    }

    [Fact]
    public void Stream_IsOnlyUsedAfterAionHeartbeats()
    {
        Assert.Null(m_device.DeviceName);

        for (int i = 0; i < 5; i++)
        {
            m_device.HandleDatagram(Ip(Server, ServerPort, Local, LocalPort, [0x0B, 0x0E, 0x00, 0x36, 0, 0], seq: (uint)(i * 6)));
        }

        Assert.NotNull(m_device.DeviceName);
    }

    private static uint Addr(string ip) => BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes());

    private static byte[] Ip(uint src, ushort srcPort, uint dst, ushort dstPort, byte[] payload, uint seq = 1)
    {
        byte[] p = new byte[20 + 20 + payload.Length];
        p[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
        p[8] = 64;
        p[9] = 6;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), src);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16), dst);

        Span<byte> tcp = p.AsSpan(20);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], seq);
        tcp[12] = 0x50; // data offset 20
        tcp[13] = 0x18; // PSH + ACK
        payload.CopyTo(tcp[20..]);
        return p;
    }
}

public sealed class PacketRecorderTests : IDisposable
{
    private readonly string m_dir = Path.Combine(Path.GetTempPath(), "HamMeterTests_" + Guid.NewGuid().ToString("N"));

    private PacketRecorder NewRecorder() => new(
        new TcpStreamBuffer(NullLogger<TcpStreamBuffer>.Instance),
        NullLogger<PacketRecorder>.Instance,
        m_dir);

    [Fact]
    public void Recording_RoundTrips_AndIsNotPlaintext()
    {
        byte[] secret = "PlayerNameSecret"u8.ToArray();
        string file = this.Record(secret, [9, 8, 7]);

        List<(DateTimeOffset Time, byte[] Payload)> records = PacketRecorder.Read(file).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal(secret, records[0].Payload);
        Assert.Equal(new byte[] { 9, 8, 7 }, records[1].Payload);
        Assert.Equal(1_700_000_000_000, records[0].Time.ToUnixTimeMilliseconds());

        // The file on disk must not contain the payload in the clear.
        byte[] raw = File.ReadAllBytes(file);
        Assert.Equal(-1, raw.AsSpan().IndexOf(secret));
    }

    [Fact]
    public void TamperedRecording_IsRejected()
    {
        string file = this.Record([1, 2, 3, 4, 5, 6, 7, 8]);
        byte[] raw = File.ReadAllBytes(file);
        raw[^1] ^= 0xFF; // flip a ciphertext bit
        File.WriteAllBytes(file, raw);

        Assert.ThrowsAny<CryptographicException>(() => PacketRecorder.Read(file).ToList());
    }

    [Fact]
    public void ExpiredRecordings_AreDeleted()
    {
        Directory.CreateDirectory(m_dir);
        string old = Path.Combine(m_dir, "old" + PacketRecorder.Extension);
        string fresh = Path.Combine(m_dir, "fresh" + PacketRecorder.Extension);
        File.WriteAllText(old, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-(PacketRecorder.RetentionDays + 1)));

        using PacketRecorder recorder = this.NewRecorder();
        recorder.DeleteExpired();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    private string Record(params byte[][] payloads)
    {
        using PacketRecorder recorder = this.NewRecorder();
        recorder.SetRecording(true);
        string file = recorder.CurrentFile!;
        long t = 1_700_000_000_000;
        foreach (byte[] p in payloads)
        {
            recorder.Record(t++, p);
        }

        recorder.SetRecording(false);
        return file;
    }

    public void Dispose()
    {
        if (Directory.Exists(m_dir))
        {
            Directory.Delete(m_dir, true);
        }
    }
}
