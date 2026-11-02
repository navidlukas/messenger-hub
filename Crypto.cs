using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MessengerHub
{
    /// <summary>
    /// AES-256-CBC with HMAC-SHA256 (encrypt-then-MAC), on .NET Framework 4.8.
    /// A 64-byte key is split into a 32-byte AES key and a 32-byte MAC key.
    /// Blob / file layout: [16-byte IV][ciphertext][32-byte HMAC tag].
    /// </summary>
    internal static class Crypto
    {
        private const int AesKey = 32, MacKey = 32, IvLen = 16, TagLen = 32;
        private const int FileBufferSize = 131072; // 128 KB buffer for optimal SSD throughput

        public static byte[] Random(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        /// <summary>PBKDF2-SHA256 → 64 bytes (AES key + MAC key).</summary>
        public static byte[] DeriveKey(string password, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(password ?? "", salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(AesKey + MacKey);
        }

        // ---------------------------------------------------------------- in-memory (small data)

        public static byte[] Encrypt(byte[] plain, byte[] key64)
        {
            byte[] enc = Sub(key64, 0, AesKey), mk = Sub(key64, AesKey, MacKey);
            byte[] iv = Random(IvLen);
            byte[] cipher;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Key = enc; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var e = aes.CreateEncryptor()) cipher = e.TransformFinalBlock(plain, 0, plain.Length);
            }
            byte[] ivCipher = Concat(iv, cipher);
            byte[] tag;
            using (var h = new HMACSHA256(mk)) tag = h.ComputeHash(ivCipher);
            return Concat(ivCipher, tag);
        }

        /// <summary>Returns null if the MAC check fails (wrong key or damaged data).</summary>
        public static byte[] Decrypt(byte[] blob, byte[] key64)
        {
            if (blob == null || blob.Length < IvLen + TagLen) return null;
            byte[] enc = Sub(key64, 0, AesKey), mk = Sub(key64, AesKey, MacKey);
            int ivCipherLen = blob.Length - TagLen;
            byte[] ivCipher = Sub(blob, 0, ivCipherLen);
            byte[] tag = Sub(blob, ivCipherLen, TagLen);
            byte[] expect;
            using (var h = new HMACSHA256(mk)) expect = h.ComputeHash(ivCipher);
            if (!FixedEquals(tag, expect)) return null;
            try
            {
                byte[] iv = Sub(ivCipher, 0, IvLen);
                byte[] cipher = Sub(ivCipher, IvLen, ivCipherLen - IvLen);
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256; aes.Key = enc; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    using (var d = aes.CreateDecryptor()) return d.TransformFinalBlock(cipher, 0, cipher.Length);
                }
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- streaming (large files)

        /// <summary>Encrypts src to dst, streaming (handles files of any size without loading them into memory).</summary>
        public static void EncryptFile(string src, string dst, byte[] key64)
        {
            EncryptTo(dst, key64, cs =>
            {
                using (var inFs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize))
                    inFs.CopyTo(cs, FileBufferSize);
            });
        }

        /// <summary>Zips and encrypts srcDir directly to dst in a single streaming pass (no temporary zip file on disk).</summary>
        public static void EncryptDirectory(string srcDir, string dst, byte[] key64)
        {
            EncryptTo(dst, key64, cs =>
            {
                using (var zip = new ZipArchive(cs, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var rootDi = new DirectoryInfo(srcDir);
                    string prefix = rootDi.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var buf = GetBuffer();
                    foreach (var fi in rootDi.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        string relPath = fi.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            ? fi.FullName.Substring(prefix.Length).Replace('\\', '/')
                            : fi.Name;
                        string ext = Path.GetExtension(fi.Name);
                        bool rawStore = ext.Equals(".ldb", StringComparison.OrdinalIgnoreCase) ||
                                        ext.Equals(".pma", StringComparison.OrdinalIgnoreCase) ||
                                        ext.Equals(".pak", StringComparison.OrdinalIgnoreCase);
                        var entry = zip.CreateEntry(relPath, rawStore ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                        try { entry.LastWriteTime = fi.LastWriteTimeUtc; } catch { }
                        using (var inFs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileBufferSize))
                        using (var es = entry.Open())
                        {
                            int r;
                            while ((r = inFs.Read(buf, 0, buf.Length)) > 0)
                                es.Write(buf, 0, r);
                        }
                    }
                }
            });
        }

        /// <summary>Writes [IV][ciphertext][HMAC tag] to dst; writePlain receives the stream that takes the plain data.</summary>
        private static void EncryptTo(string dst, byte[] key64, Action<Stream> writePlain)
        {
            byte[] enc = Sub(key64, 0, AesKey), mk = Sub(key64, AesKey, MacKey);
            byte[] iv = Random(IvLen);
            using (var outFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferSize))
            using (var mac = new HMACSHA256(mk))
            {
                outFs.Write(iv, 0, IvLen);
                mac.TransformBlock(iv, 0, IvLen, null, 0);
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256; aes.Key = enc; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    var tee = new HmacTeeStream(outFs, mac);
                    using (var cs = new CryptoStream(tee, aes.CreateEncryptor(), CryptoStreamMode.Write))
                        writePlain(cs); // CryptoStream.Dispose flushes the final block into the tee
                }
                mac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                outFs.Write(mac.Hash, 0, TagLen);
                outFs.Flush(true); // really on disk before we delete the original
            }
        }

        [ThreadStatic]
        private static byte[] perThreadBuffer;
        private static byte[] GetBuffer() => perThreadBuffer ?? (perThreadBuffer = new byte[65536]);
        public static void ReleaseBuffers() => perThreadBuffer = null;

        /// <summary>
        /// Checks the MAC of an encrypted file without decrypting or writing anything (read-only,
        /// safe to run on many files at once). A pass means the key is right and the data is intact.
        /// </summary>
        public static bool VerifyFile(string src, byte[] key64)
        {
            try
            {
                byte[] mk = Sub(key64, AesKey, MacKey);
                long len = new FileInfo(src).Length;
                if (len < IvLen + TagLen) return false;
                using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize))
                using (var mac = new HMACSHA256(mk))
                {
                    long toMac = len - TagLen;
                    var buf = GetBuffer();
                    long done = 0;
                    while (done < toMac)
                    {
                        int want = (int)Math.Min(buf.Length, toMac - done);
                        int r = fs.Read(buf, 0, want);
                        if (r <= 0) return false;
                        mac.TransformBlock(buf, 0, r, null, 0);
                        done += r;
                    }
                    mac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    var tag = new byte[TagLen];
                    return fs.Read(tag, 0, TagLen) == TagLen && FixedEquals(tag, mac.Hash);
                }
            }
            catch { return false; }
        }

        /// <summary>Verifies the MAC and decrypts src to dst. Returns false (leaving no file) on any failure.</summary>
        public static bool DecryptFile(string src, string dst, byte[] key64)
        {
            if (!VerifyFile(src, key64)) return false; // no plaintext is written unless the MAC is correct
            try
            {
                using (var outFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferSize))
                    DecryptTo(src, key64, outFs);
                return true;
            }
            catch
            {
                try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Verifies the MAC, decrypts src (in memory up to 128 MB, else via a temporary file) and
        /// extracts the archive into dstDir.
        /// </summary>
        public static bool DecryptDirectory(string src, string dstDir, byte[] key64)
        {
            string tmpZip = null;
            try
            {
                long cipherLen = new FileInfo(src).Length - IvLen - TagLen;
                if (cipherLen < 0) return false;

                Stream zipStream;
                if (cipherLen > 128L * 1024L * 1024L)
                {
                    tmpZip = dstDir + ".mhtmp";
                    if (!DecryptFile(src, tmpZip, key64)) return false;
                    zipStream = new FileStream(tmpZip, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize);
                }
                else
                {
                    var ms = new MemoryStream((int)cipherLen);
                    DecryptTo(src, key64, ms);
                    ms.Position = 0;
                    zipStream = ms;
                }

                if (Directory.Exists(dstDir)) { try { Directory.Delete(dstDir, true); } catch { } }
                string root = Path.GetFullPath(dstDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Directory.CreateDirectory(root);

                using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false))
                {
                    var buf = GetBuffer();
                    foreach (var entry in zip.Entries)
                    {
                        string dest = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never write outside dstDir
                        if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; } // a folder entry
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        using (var inEs = entry.Open())
                        using (var outFile = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferSize))
                        {
                            int r;
                            while ((r = inEs.Read(buf, 0, buf.Length)) > 0)
                                outFile.Write(buf, 0, r);
                        }
                        try { File.SetLastWriteTimeUtc(dest, entry.LastWriteTime.UtcDateTime); } catch { }
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (tmpZip != null && File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            }
        }

        /// <summary>Streams the decrypted content of src into dest and checks the MAC at the end (throws if it fails).</summary>
        private static void DecryptTo(string src, byte[] key64, Stream dest)
        {
            byte[] enc = Sub(key64, 0, AesKey), mk = Sub(key64, AesKey, MacKey);
            long cipherLen = new FileInfo(src).Length - IvLen - TagLen;
            if (cipherLen < 0) throw new CryptographicException("File too short");

            using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize))
            using (var mac = new HMACSHA256(mk))
            {
                var iv = new byte[IvLen];
                if (fs.Read(iv, 0, IvLen) != IvLen) throw new CryptographicException("Unexpected end of stream");
                mac.TransformBlock(iv, 0, IvLen, null, 0);

                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256; aes.Key = enc; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    using (var cs = new CryptoStream(dest, aes.CreateDecryptor(), CryptoStreamMode.Write, leaveOpen: true))
                    {
                        var buf = GetBuffer();
                        long done = 0;
                        while (done < cipherLen)
                        {
                            int want = (int)Math.Min(buf.Length, cipherLen - done);
                            int r = fs.Read(buf, 0, want);
                            if (r <= 0) throw new CryptographicException("Unexpected end of stream");
                            mac.TransformBlock(buf, 0, r, null, 0);
                            cs.Write(buf, 0, r);
                            done += r;
                        }
                        mac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        var tag = new byte[TagLen];
                        if (fs.Read(tag, 0, TagLen) != TagLen || !FixedEquals(tag, mac.Hash))
                            throw new CryptographicException("MAC verification failed");
                    }
                }
            }
        }

        // ---------------------------------------------------------------- helpers

        private static byte[] Sub(byte[] a, int off, int len) { var r = new byte[len]; Buffer.BlockCopy(a, off, r, 0, len); return r; }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        private static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        /// <summary>Write-only stream that feeds every byte to an HMAC and passes it on to the real stream.</summary>
        private sealed class HmacTeeStream : Stream
        {
            private readonly Stream inner;
            private readonly HMACSHA256 mac;
            public HmacTeeStream(Stream inner, HMACSHA256 mac) { this.inner = inner; this.mac = mac; }
            public override void Write(byte[] b, int o, int c) { mac.TransformBlock(b, o, c, null, 0); inner.Write(b, o, c); }
            public override void Flush() => inner.Flush();
            public override bool CanWrite => true;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override long Seek(long o, SeekOrigin or) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
        }
    }
}
