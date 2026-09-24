using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

// Release signing for HamMeter updates (developer tool, not shipped).
//
//   create-key <public-key-file>   new ECDSA P-256 key pair; the private key is stored
//                                  DPAPI-protected (this Windows account only) under
//                                  %APPDATA%\HamMeter-ReleaseKey, the public key is
//                                  written for embedding into HamMeter.
//   sign <file>                    writes <file>.sig: ECDSA-SHA256 signature, base64.
//
// HamMeter only runs a downloaded update whose signature matches the embedded public
// key, so a compromised GitHub account alone cannot ship code to users.
internal static class Program
{
    private static readonly string KeyDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HamMeter-ReleaseKey");

    private static readonly string KeyFile = Path.Combine(KeyDir, "release-key.dpapi");

    private static readonly byte[] Entropy = "HamMeter release signing key v1"u8.ToArray();

    private static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["create-key", string pub] => CreateKey(pub),
                ["sign", string file] => Sign(file),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: ReleaseSigner create-key <public-key-file> | sign <file>");
        return 2;
    }

    private static int CreateKey(string publicKeyFile)
    {
        if (File.Exists(KeyFile))
        {
            // Replacing the key would lock every installed HamMeter out of updates.
            throw new InvalidOperationException($"A release key already exists: {KeyFile}");
        }

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            Directory.CreateDirectory(KeyDir);
            File.WriteAllBytes(KeyFile, Dpapi.Protect(pkcs8, Entropy));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        File.WriteAllText(publicKeyFile, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        Console.WriteLine($"Private key: {KeyFile} (DPAPI, this Windows account only)");
        Console.WriteLine($"Public key:  {publicKeyFile}");
        return 0;
    }

    private static int Sign(string file)
    {
        if (!File.Exists(KeyFile))
        {
            throw new FileNotFoundException("No release key. Run: ReleaseSigner create-key <public-key-file>");
        }

        byte[] pkcs8 = Dpapi.Unprotect(File.ReadAllBytes(KeyFile), Entropy);
        using ECDsa key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        byte[] signature;
        using (FileStream fs = File.OpenRead(file))
        {
            signature = key.SignData(fs, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        File.WriteAllText(file + ".sig", Convert.ToBase64String(signature));
        Console.WriteLine($"Signed: {file}.sig");
        return 0;
    }

    private static class Dpapi
    {
        public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, true);

        public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, false);

        private static byte[] Run(byte[] data, byte[] entropy, bool protect)
        {
            GCHandle hData = GCHandle.Alloc(data, GCHandleType.Pinned);
            GCHandle hEntropy = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            var output = default(Blob);
            try
            {
                var input = new Blob { Size = data.Length, Data = hData.AddrOfPinnedObject() };
                var extra = new Blob { Size = entropy.Length, Data = hEntropy.AddrOfPinnedObject() };
                bool ok = protect
                    ? CryptProtectData(ref input, null, ref extra, IntPtr.Zero, IntPtr.Zero, 1, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, 1, ref output);
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
                    Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                    LocalFree(output.Data);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);
    }
}
