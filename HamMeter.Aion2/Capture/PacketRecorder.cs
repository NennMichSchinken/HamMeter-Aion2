using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AionDpsMeter.Services.PacketCapture;
using Microsoft.Extensions.Logging;

namespace HamMeter.Capture;

// Optional recording of the game packets, so a fight can be replayed and checked.
// Raw game traffic can contain names and chat, so recordings are protected:
//   - every record is encrypted with AES-256-GCM (confidentiality + tamper detection),
//   - the per-file key is wrapped with Windows DPAPI (CurrentUser): only the same
//     Windows account on the same PC can decrypt; no password or key file exists,
//   - recording is off on every start (never persisted) and files older than
//     RetentionDays are deleted automatically.
//
// File layout: "HMREC1\0\0" | u32 keyBlobLength | keyBlob |
//              records: u32 cipherLength | 12-byte nonce | 16-byte tag | cipher
// Plaintext of a record: i64 unix-ms | payload. The record index is the AAD, so
// records cannot be reordered or dropped from the middle unnoticed.
public sealed class PacketRecorder : IDisposable
{
    public const int RetentionDays = 7;
    public const string Extension = ".hmrec";

    private static readonly byte[] Magic = "HMREC1\0\0"u8.ToArray();
    private static readonly byte[] Entropy = "HamMeter-Aion2 packet recording v1"u8.ToArray();

    private readonly TcpStreamBuffer m_streamBuffer;
    private readonly ILogger<PacketRecorder> m_log;
    private readonly Lock m_sync = new();
    private readonly string m_directory;

    private FileStream? m_file;
    private AesGcm? m_aes;
    private byte[] m_noncePrefix = new byte[4];
    private ulong m_counter;

    public PacketRecorder(TcpStreamBuffer streamBuffer, ILogger<PacketRecorder> log)
        : this(streamBuffer, log, Path.Combine(Config.DataDirectory, "PacketLogs"))
    {
    }

    internal PacketRecorder(TcpStreamBuffer streamBuffer, ILogger<PacketRecorder> log, string directory)
    {
        m_streamBuffer = streamBuffer;
        m_log = log;
        m_directory = directory;
        m_streamBuffer.PacketExtracted += this.OnPacket;
    }

    internal string? CurrentFile => m_file?.Name;

    public bool Recording
    {
        get
        {
            lock (m_sync)
            {
                return m_file is not null;
            }
        }
    }

    public void SetRecording(bool on)
    {
        lock (m_sync)
        {
            if (on == (m_file is not null))
            {
                return;
            }

            if (on)
            {
                this.Open();
            }
            else
            {
                this.Close();
            }
        }
    }

    // Deletes recordings past the retention period (also old plaintext logs).
    public void DeleteExpired()
    {
        if (!Directory.Exists(m_directory))
        {
            return;
        }

        DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
        foreach (string file in Directory.EnumerateFiles(m_directory))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // In use or locked: try again next start.
            }
        }
    }

    private void Open()
    {
        Directory.CreateDirectory(m_directory);
        string path = Path.Combine(m_directory, $"rec_{DateTime.Now:yyyyMMdd_HHmmss_fff}{Extension}");

        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            byte[] blob = Dpapi.Protect(key, Entropy);
            m_file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
            m_file.Write(Magic);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)blob.Length);
            m_file.Write(len);
            m_file.Write(blob);

            m_aes = new AesGcm(key, 16);
            m_noncePrefix = RandomNumberGenerator.GetBytes(4);
            m_counter = 0;
            m_log.LogInformation("Packet recording started");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private void Close()
    {
        m_file?.Flush();
        m_file?.Dispose();
        m_file = null;
        m_aes?.Dispose();
        m_aes = null;
        m_log.LogInformation("Packet recording stopped");
    }

    private void OnPacket(object? sender, TcpPacketEventArgs e) => this.Record(e.ReceivedAt, e.Payload);

    internal void Record(long unixMs, byte[] payload)
    {
        lock (m_sync)
        {
            if (m_file is null || m_aes is null)
            {
                return;
            }

            try
            {
                this.WriteRecord(unixMs, payload);
            }
            catch (Exception ex)
            {
                m_log.LogWarning("Packet recording failed, stopping: {Message}", ex.Message);
                this.Close();
            }
        }
    }

    private void WriteRecord(long unixMs, byte[] payload)
    {
        int plainLength = 8 + payload.Length;
        byte[] plain = new byte[plainLength];
        BinaryPrimitives.WriteInt64LittleEndian(plain, unixMs);
        payload.CopyTo(plain, 8);

        Span<byte> nonce = stackalloc byte[12];
        m_noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], m_counter);

        Span<byte> aad = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(aad, m_counter);

        byte[] cipher = new byte[plainLength];
        Span<byte> tag = stackalloc byte[16];
        m_aes!.Encrypt(nonce, plain, cipher, tag, aad);
        CryptographicOperations.ZeroMemory(plain);

        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)cipher.Length);
        m_file!.Write(len);
        m_file.Write(nonce);
        m_file.Write(tag);
        m_file.Write(cipher);
        m_counter++;
    }

    // Reads a recording back (same Windows account only). Used for replaying a fight.
    public static IEnumerable<(DateTimeOffset Time, byte[] Payload)> Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(file);

        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a HamMeter recording.");
        }

        byte[] blob = reader.ReadBytes((int)Math.Min(reader.ReadUInt32(), 4096));
        byte[] key = Dpapi.Unprotect(blob, Entropy);
        using var aes = new AesGcm(key, 16);
        CryptographicOperations.ZeroMemory(key);

        ulong counter = 0;
        byte[] aad = new byte[8];
        while (file.Position < file.Length)
        {
            uint length = reader.ReadUInt32();
            if (length < 8 || length > 1024 * 1024)
            {
                throw new InvalidDataException("Corrupt recording.");
            }

            byte[] nonce = reader.ReadBytes(12);
            byte[] tag = reader.ReadBytes(16);
            byte[] cipher = reader.ReadBytes((int)length);
            byte[] plain = new byte[length];
            BinaryPrimitives.WriteUInt64LittleEndian(aad, counter++);
            aes.Decrypt(nonce, cipher, tag, plain, aad); // throws if tampered

            long unixMs = BinaryPrimitives.ReadInt64LittleEndian(plain);
            yield return (DateTimeOffset.FromUnixTimeMilliseconds(unixMs), plain[8..]);
        }
    }

    public void Dispose()
    {
        m_streamBuffer.PacketExtracted -= this.OnPacket;
        lock (m_sync)
        {
            this.Close();
        }
    }

    // Windows DPAPI (crypt32) via P/Invoke, so no extra package is needed.
    private static class Dpapi
    {
        private const int UiForbidden = 0x1;

        public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, protect: true);

        public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, protect: false);

        private static byte[] Run(byte[] data, byte[] entropy, bool protect)
        {
            GCHandle hData = GCHandle.Alloc(data, GCHandleType.Pinned);
            GCHandle hEntropy = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            var output = default(DataBlob);
            try
            {
                var input = new DataBlob { Size = data.Length, Data = hData.AddrOfPinnedObject() };
                var extra = new DataBlob { Size = entropy.Length, Data = hEntropy.AddrOfPinnedObject() };
                bool ok = protect
                    ? CryptProtectData(ref input, null, ref extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
                if (!ok)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                byte[] result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                hData.Free();
                hEntropy.Free();
                if (output.Data != IntPtr.Zero)
                {
                    // Wipe before freeing: for Unprotect this buffer holds the key.
                    Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                    LocalFree(output.Data);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);
    }
}
