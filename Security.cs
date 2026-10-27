using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MessengerHub
{
    /// <summary>
    /// Password lock + at-rest encryption of the login data, all keyed to a password set on first run.
    ///
    /// Design:
    ///  - A random 64-byte MASTER key encrypts everything. The master key is itself stored encrypted
    ///    with a key derived from the password (PBKDF2), so changing the password never re-encrypts data.
    ///  - On exit, each account's login data (cookies, Local Storage, IndexedDB) is encrypted into a
    ///    .mhf / .mhd file beside it, and the plain copy is deleted. On launch (after the password), it
    ///    is restored. Crash-safe rule: if the plain copy is present it always wins, and the lock file
    ///    is treated as stale, so a crash never loses data.
    ///  - The browser engine locks cookies to the Windows account. We keep a portable copy of that key
    ///    (encrypted with the master key) so logins survive moving the folder to another PC.
    /// </summary>
    internal class Security
    {
        private const int KdfIterations = 200000;

        // Per-account login data to protect. Folders are zipped first; the single file is encrypted directly.
        private static readonly string[] DirTargets = { "Local Storage", "IndexedDB" };
        private static readonly string[] FileTargets = { Path.Combine("Network", "Cookies") };

        private byte[] master; // 64 bytes; held only in memory while the app is unlocked
        private readonly HashSet<int> browserPids = new HashSet<int>();

        public bool IsSet => File.Exists(AppPaths.SecurityFile);

        // ------------------------------------------------------------------ password / vault

        public void CreatePassword(string password)
        {
            byte[] salt = Crypto.Random(16);
            byte[] pwKey = Crypto.DeriveKey(password, salt, KdfIterations);
            master = Crypto.Random(64);
            byte[] wrapped = Crypto.Encrypt(master, pwKey);

            var sb = new StringBuilder();
            sb.Append("version=1\n");
            sb.Append("iter=" + KdfIterations + "\n");
            sb.Append("salt=" + Convert.ToBase64String(salt) + "\n");
            sb.Append("master=" + Convert.ToBase64String(wrapped) + "\n");
            SafeWrite(AppPaths.SecurityFile, Encoding.UTF8.GetBytes(sb.ToString()));
        }

        /// <summary>Returns true and unlocks (keeps the master key in memory) when the password is correct.</summary>
        public bool TryUnlock(string password)
        {
            try
            {
                var map = File.ReadAllLines(AppPaths.SecurityFile)
                    .Select(l => l.Split(new[] { '=' }, 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

                int iter = int.Parse(map["iter"]);
                byte[] salt = Convert.FromBase64String(map["salt"]);
                byte[] wrapped = Convert.FromBase64String(map["master"]);
                byte[] pwKey = Crypto.DeriveKey(password, salt, iter);
                byte[] m = Crypto.Decrypt(wrapped, pwKey);
                if (m == null) return false; // wrong password: MAC check failed
                master = m;
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ profile lock / unlock

        private static IEnumerable<string> ProfileDirs()
        {
            if (!Directory.Exists(AppPaths.EbWebView)) yield break;
            foreach (var dir in Directory.GetDirectories(AppPaths.EbWebView))
                if (Path.GetFileName(dir).StartsWith("WV2Profile_", StringComparison.OrdinalIgnoreCase))
                    yield return dir;
        }

        /// <summary>
        /// Restore all login data to plain form. Called at launch, before the browser starts.
        /// Accounts are processed in parallel to use all CPU cores.
        /// </summary>
        public void UnlockProfiles()
        {
            Parallel.ForEach(ProfileDirs().ToList(), profile =>
            {
                foreach (var rel in DirTargets) UnlockDir(profile, rel);
                foreach (var rel in FileTargets) UnlockFile(profile, rel);
            });
        }

        /// <summary>
        /// Encrypt all login data. Called at exit, after the browser has closed.
        /// Accounts are processed in parallel; an account whose data hasn't changed since it was
        /// unlocked is skipped (its existing encrypted copy is reused).
        /// </summary>
        public void LockProfiles()
        {
            Parallel.ForEach(ProfileDirs().ToList(), profile =>
            {
                foreach (var rel in DirTargets) LockDir(profile, rel);
                foreach (var rel in FileTargets) LockFile(profile, rel);
            });
        }

        // Safety net: if an account's media cache grows past its limit, it's cleared on exit so the folder
        // can't balloon over time. The app's offline shell and the login data are left alone, so the
        // only effect is that account re-downloads its media next time it's opened.
        private const long DefaultCacheLimitBytes = 300L * 1024 * 1024;

        // Per-account cache limit in bytes (set from each account's service). Keyed by profile id.
        private readonly Dictionary<string, long> cacheLimits = new Dictionary<string, long>();

        public void SetCacheLimit(string profileId, int megabytes)
        {
            lock (cacheLimits) cacheLimits[profileId] = megabytes * 1024L * 1024L;
        }

        /// <summary>Current media-cache size (bytes) for one account's profile — used to calibrate its limit.</summary>
        public long MeasureProfileCache(string profileId)
        {
            string profile = Path.Combine(AppPaths.EbWebView, "WV2Profile_" + profileId);
            long total = 0;
            foreach (var c in CacheFolders) total += DirSize(Path.Combine(profile, c));
            return total;
        }

        private long CacheLimitFor(string profileFolderName)
        {
            const string prefix = "WV2Profile_";
            string id = profileFolderName.StartsWith(prefix) ? profileFolderName.Substring(prefix.Length) : profileFolderName;
            lock (cacheLimits)
                return cacheLimits.TryGetValue(id, out long v) ? v : DefaultCacheLimitBytes;
        }

        // The media / throwaway caches. "Service Worker" (the offline app shell) is deliberately NOT here,
        // so the app still starts fast; logins live elsewhere and are never touched.
        private static readonly string[] CacheFolders =
            { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "GrShaderCache" };

        /// <summary>At exit (browser already closed): clear the media cache of any account over the size limit.</summary>
        public void TrimCaches()
        {
            Parallel.ForEach(ProfileDirs().ToList(), profile =>
            {
                long limit = CacheLimitFor(Path.GetFileName(profile));
                long total = 0;
                foreach (var c in CacheFolders) total += DirSize(Path.Combine(profile, c));
                if (total <= limit) return;
                foreach (var c in CacheFolders) TryDeleteDir(Path.Combine(profile, c));
            });
        }

        private static long DirSize(string path)
        {
            long total = 0;
            try
            {
                if (!Directory.Exists(path)) return 0;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        // A cheap fingerprint of a target (file count + total bytes + newest modified time).
        // It is stored beside the lock file when we encrypt/restore, so next time we can tell
        // whether anything changed without re-reading the whole thing.
        private static string Signature(string path, bool isDir)
        {
            try
            {
                if (isDir)
                {
                    if (!Directory.Exists(path)) return "";
                    long size = 0, newest = 0; int count = 0;
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        var fi = new FileInfo(f);
                        size += fi.Length;
                        long t = fi.LastWriteTimeUtc.Ticks;
                        if (t > newest) newest = t;
                        count++;
                    }
                    return count + "|" + size + "|" + newest;
                }
                if (!File.Exists(path)) return "";
                var f2 = new FileInfo(path);
                return "1|" + f2.Length + "|" + f2.LastWriteTimeUtc.Ticks;
            }
            catch { return ""; }
        }

        private static bool Unchanged(string sigPath, string lockPath, string currentSig) =>
            currentSig.Length > 0 && File.Exists(lockPath) && File.Exists(sigPath) &&
            SafeRead(sigPath) == currentSig;

        private static string SafeRead(string p) { try { return File.ReadAllText(p); } catch { return null; } }

        private void LockDir(string profile, string rel)
        {
            string src = Path.Combine(profile, rel);
            if (!Directory.Exists(src)) return; // nothing there (or already locked)
            string lockPath = src + ".mhd";
            string sigPath = lockPath + ".sig";
            string tmpZip = src + ".mhtmp";
            try
            {
                string sig = Signature(src, true);
                if (Unchanged(sigPath, lockPath, sig))
                {
                    Directory.Delete(src, true); // nothing changed: reuse the existing encrypted copy
                    return;
                }

                if (File.Exists(tmpZip)) File.Delete(tmpZip);
                ZipFile.CreateFromDirectory(src, tmpZip, CompressionLevel.Fastest, false);
                Crypto.EncryptFile(tmpZip, lockPath + ".tmp", master);
                File.Delete(tmpZip);

                if (!Crypto.VerifyFile(lockPath + ".tmp", master)) // verify it reads back
                    throw new Exception("verify failed");

                if (File.Exists(lockPath)) File.Delete(lockPath);
                File.Move(lockPath + ".tmp", lockPath);
                WriteText(sigPath, sig);
                Directory.Delete(src, true); // plain copy gone only after the lock file is verified and in place
            }
            catch
            {
                Clean(tmpZip, lockPath + ".tmp"); // leave the plain copy untouched on any failure
            }
        }

        private void UnlockDir(string profile, string rel)
        {
            string src = Path.Combine(profile, rel);
            string lockPath = src + ".mhd";
            string sigPath = lockPath + ".sig";
            if (!File.Exists(lockPath)) return;

            // Plain copy wins: a crash left both, so keep the plain one and drop the stale lock + signature.
            if (Directory.Exists(src) && Directory.EnumerateFileSystemEntries(src).Any())
            {
                TryDelete(lockPath);
                TryDelete(sigPath);
                return;
            }

            string tmpZip = src + ".mhtmp";
            try
            {
                if (!Crypto.DecryptFile(lockPath, tmpZip, master)) return; // keep the lock file; don't lose data
                if (Directory.Exists(src)) Directory.Delete(src, true);
                ZipFile.ExtractToDirectory(tmpZip, src);
                File.Delete(tmpZip);
                // Keep the lock file: if nothing changes this session, LockDir reuses it. Record the restored state.
                WriteText(sigPath, Signature(src, true));
            }
            catch
            {
                Clean(tmpZip);
            }
        }

        private void LockFile(string profile, string rel)
        {
            string src = Path.Combine(profile, rel);
            if (!File.Exists(src)) return;
            string lockPath = src + ".mhf";
            string sigPath = lockPath + ".sig";
            try
            {
                string sig = Signature(src, false);
                if (Unchanged(sigPath, lockPath, sig)) { File.Delete(src); return; } // unchanged: reuse

                Crypto.EncryptFile(src, lockPath + ".tmp", master);
                if (!Crypto.VerifyFile(lockPath + ".tmp", master)) throw new Exception("verify failed");
                if (File.Exists(lockPath)) File.Delete(lockPath);
                File.Move(lockPath + ".tmp", lockPath);
                WriteText(sigPath, sig);
                File.Delete(src);
            }
            catch
            {
                Clean(lockPath + ".tmp");
            }
        }

        private void UnlockFile(string profile, string rel)
        {
            string src = Path.Combine(profile, rel);
            string lockPath = src + ".mhf";
            string sigPath = lockPath + ".sig";
            if (!File.Exists(lockPath)) return;

            if (File.Exists(src)) { TryDelete(lockPath); TryDelete(sigPath); return; } // plain copy wins

            try
            {
                if (!Crypto.DecryptFile(lockPath, src + ".tmp", master)) return;
                if (File.Exists(src)) File.Delete(src);
                File.Move(src + ".tmp", src);
                WriteText(sigPath, Signature(src, false)); // keep the lock for possible reuse
            }
            catch
            {
                Clean(src + ".tmp");
            }
        }

        // ------------------------------------------------------------------ portable cookie key

        // The browser keeps its cookie key in "Local State" as base64 of ("DPAPI" + a Windows-locked blob).
        private static readonly byte[] DpapiPrefix = Encoding.ASCII.GetBytes("DPAPI");
        private static readonly Regex KeyField = new Regex("\"encrypted_key\":\"(.*?)\"", RegexOptions.Compiled);

        /// <summary>At exit: save a portable copy of the cookie key (encrypted with the master key).</summary>
        public void CaptureCookieKey()
        {
            try
            {
                if (File.Exists(AppPaths.CookieKeyFile)) return; // already have it
                byte[] raw = ReadCookieKey();
                if (raw == null) return;
                SafeWrite(AppPaths.CookieKeyFile, Crypto.Encrypt(raw, master));
            }
            catch { }
        }

        /// <summary>At launch (before the browser): re-lock the portable cookie key to THIS Windows account.</summary>
        public void RestoreCookieKey()
        {
            try
            {
                if (!File.Exists(AppPaths.CookieKeyFile) || !File.Exists(AppPaths.LocalStateFile)) return;
                byte[] raw = Crypto.Decrypt(File.ReadAllBytes(AppPaths.CookieKeyFile), master);
                if (raw == null) return;
                WriteCookieKey(raw);
            }
            catch { }
        }

        private static byte[] ReadCookieKey()
        {
            string text = File.ReadAllText(AppPaths.LocalStateFile);
            var m = KeyField.Match(text);
            if (!m.Success) return null;
            byte[] withPrefix = Convert.FromBase64String(m.Groups[1].Value);
            if (withPrefix.Length <= DpapiPrefix.Length) return null;
            var blob = new byte[withPrefix.Length - DpapiPrefix.Length];
            Buffer.BlockCopy(withPrefix, DpapiPrefix.Length, blob, 0, blob.Length);
            return ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
        }

        private static void WriteCookieKey(byte[] raw)
        {
            byte[] blob = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
            var withPrefix = new byte[DpapiPrefix.Length + blob.Length];
            Buffer.BlockCopy(DpapiPrefix, 0, withPrefix, 0, DpapiPrefix.Length);
            Buffer.BlockCopy(blob, 0, withPrefix, DpapiPrefix.Length, blob.Length);
            string b64 = Convert.ToBase64String(withPrefix);

            string text = File.ReadAllText(AppPaths.LocalStateFile);
            string updated = KeyField.Replace(text, "\"encrypted_key\":\"" + b64 + "\"", 1);
            if (updated != text) SafeWrite(AppPaths.LocalStateFile, Encoding.UTF8.GetBytes(updated));
        }

        // ------------------------------------------------------------------ browser process tracking

        public void AddBrowserPid(int pid) { if (pid > 0) lock (browserPids) browserPids.Add(pid); }

        /// <summary>At exit: wait until the browser processes have really closed, so their files are free to encrypt.</summary>
        public void WaitForBrowsers(int timeoutMs = 15000)
        {
            int[] pids;
            lock (browserPids) pids = browserPids.ToArray();
            var sw = Stopwatch.StartNew();
            foreach (int pid in pids)
            {
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    try { using (var p = Process.GetProcessById(pid)) { if (p.HasExited) break; } }
                    catch { break; } // no such process: it has exited
                    System.Threading.Thread.Sleep(200);
                }
            }
            System.Threading.Thread.Sleep(300); // small grace for file handles to release
        }

        // ------------------------------------------------------------------ helpers

        private static void SafeWrite(string path, byte[] bytes)
        {
            string tmp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static void WriteText(string path, string text)
        {
            try { File.WriteAllText(path, text); } catch { }
        }

        private static void Clean(params string[] paths)
        {
            foreach (var p in paths) TryDelete(p);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
