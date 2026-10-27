using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MessengerHub
{
    /// <summary>
    /// Silent self-updater. About once a week the running app asks GitHub for the
    /// latest release, downloads it in the background and stages it under
    /// data\update\staged. The file swap itself is done by a separate updater.exe
    /// after the app exits (or before it opens next time), so no files are locked.
    /// Everything in data\ (logins, settings) is left untouched.
    /// </summary>
    internal static class Updater
    {
        // GitHub repository that publishes releases.
        private const string Owner = "navidlukas";
        private const string Repo = "messenger-hub";
        private const string LatestApi =
            "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

        private static readonly TimeSpan CheckEvery = TimeSpan.FromDays(7);

        // data\update layout
        private static string UpdateDir => Path.Combine(AppPaths.Data, "update");
        private static string StateFile => Path.Combine(UpdateDir, "state.txt");
        private static string ApplyFlag => Path.Combine(UpdateDir, "apply.flag");
        private static string StagedRoot => Path.Combine(UpdateDir, "staged");
        private static string DownloadDir => Path.Combine(UpdateDir, "download");
        private static string BackupDir => Path.Combine(UpdateDir, "backup");
        private static string RunnerDir => Path.Combine(UpdateDir, "runner");
        private static string LogFile => Path.Combine(UpdateDir, "update.log");

        private static int running; // 0/1 guard against overlapping checks

        private static readonly HttpClient http = CreateClient();

        private static HttpClient CreateClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MessengerHub-Updater");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        /// <summary>This build's version (first three parts).</summary>
        public static Version CurrentVersion
        {
            get
            {
                try { return Normalize(Assembly.GetExecutingAssembly().GetName().Version); }
                catch { return new Version(0, 0, 0); }
            }
        }

        // ---------------------------------------------------------------- checking

        /// <summary>Fire-and-forget weekly check. Returns at once; cheap no-op when not due.</summary>
        public static void StartBackgroundCheck()
        {
            Task.Run(() => { try { CheckDue(); } catch (Exception ex) { Log("check error: " + ex.Message); } });
        }

        private static void CheckDue()
        {
            if (Interlocked.Exchange(ref running, 1) == 1) return;
            try
            {
                Directory.CreateDirectory(UpdateDir);
                if (HasPendingUpdate()) return;                     // already staged, waiting to apply
                if (DateTime.UtcNow - ReadLastCheck() < CheckEvery) return;
                WriteLastCheck(DateTime.UtcNow);                    // record now so a failure won't retry each tick
                RunCheck();
            }
            finally { Interlocked.Exchange(ref running, 0); }
        }

        private static void RunCheck()
        {
            string json = HttpGet(LatestApi);
            if (json == null) return;

            Version latest = ParseTag(Extract(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\""));
            if (latest == null || latest <= CurrentVersion) return;

            string url = FindZipAsset(json);
            if (url == null) { Log("no .zip asset in latest release"); return; }

            SafeDelete(DownloadDir);
            Directory.CreateDirectory(DownloadDir);
            string zipPath = Path.Combine(DownloadDir, "update.zip");
            if (!Download(url, zipPath)) { SafeDelete(DownloadDir); return; }

            string staged = Path.Combine(StagedRoot, latest.ToString());
            SafeDelete(staged);
            Directory.CreateDirectory(staged);
            try { ZipFile.ExtractToDirectory(zipPath, staged); }
            catch (Exception ex) { Log("extract failed: " + ex.Message); SafeDelete(staged); SafeDelete(DownloadDir); return; }
            SafeDelete(DownloadDir);

            string appRoot = LocateApp(staged);
            if (appRoot == null) { Log("MessengerHub.exe not found in download"); SafeDelete(staged); return; }

            WriteFlag(latest, appRoot);
            Log("staged update " + latest);
        }

        // ---------------------------------------------------------------- applying

        /// <summary>True when a newer, complete build is staged and ready to install.</summary>
        public static bool HasPendingUpdate()
        {
            try
            {
                if (!File.Exists(ApplyFlag)) return false;
                var map = ReadFlag();
                Version v = ParseTag(map.TryGet("version"));
                string staged = map.TryGet("staged");
                if (v == null || v <= CurrentVersion || string.IsNullOrEmpty(staged) ||
                    !File.Exists(Path.Combine(staged, "MessengerHub.exe")))
                {
                    CleanupStale();
                    return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Starts updater.exe to swap in the staged build after this process exits.
        /// relaunch=true restarts the app afterwards (used at startup); false just
        /// installs quietly (used when the user is quitting). Returns true if launched.
        /// </summary>
        public static bool LaunchRunner(bool relaunch)
        {
            try
            {
                if (!HasPendingUpdate()) return false;
                var map = ReadFlag();
                string staged = map.TryGet("staged").TrimEnd('\\');
                string appDir = AppPaths.Root.TrimEnd('\\');

                // Run updater.exe from a neutral folder so it can overwrite appDir\updater.exe.
                SafeDelete(RunnerDir);
                Directory.CreateDirectory(RunnerDir);
                string src = Path.Combine(staged, "updater.exe");
                if (!File.Exists(src)) src = Path.Combine(appDir, "updater.exe");
                if (!File.Exists(src)) { Log("updater.exe missing; cannot apply"); return false; }
                string runner = Path.Combine(RunnerDir, "updater.exe");
                File.Copy(src, runner, true);

                int pid = Process.GetCurrentProcess().Id;
                string args = string.Format(
                    "--apply --pid {0} --app \"{1}\" --staged \"{2}\" --backup \"{3}\" --update \"{4}\" --relaunch {5}",
                    pid, appDir, staged, BackupDir.TrimEnd('\\'), UpdateDir.TrimEnd('\\'), relaunch ? 1 : 0);

                Process.Start(new ProcessStartInfo(runner, args)
                {
                    UseShellExecute = false,
                    WorkingDirectory = RunnerDir,
                    CreateNoWindow = true
                });
                Log("launched updater (relaunch=" + relaunch + ")");
                return true;
            }
            catch (Exception ex) { Log("LaunchRunner failed: " + ex.Message); return false; }
        }

        // ---------------------------------------------------------------- helpers

        private static string LocateApp(string dir)
        {
            if (File.Exists(Path.Combine(dir, "MessengerHub.exe"))) return dir;
            foreach (var sub in Directory.GetDirectories(dir))
                if (File.Exists(Path.Combine(sub, "MessengerHub.exe"))) return sub;
            return null;
        }

        private static string HttpGet(string url)
        {
            try { return http.GetStringAsync(url).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log("GET failed: " + ex.Message); return null; }
        }

        private static bool Download(string url, string dest)
        {
            try
            {
                using (var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    using (var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                        s.CopyTo(fs);
                }
                return new FileInfo(dest).Length > 0;
            }
            catch (Exception ex) { Log("download failed: " + ex.Message); return false; }
        }

        private static string Extract(string s, string pattern)
        {
            var m = Regex.Match(s, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string FindZipAsset(string json)
        {
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string u = m.Groups[1].Value;
                if (u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return u;
            }
            return null;
        }

        private static Version Normalize(Version v)
        {
            if (v == null) return new Version(0, 0, 0);
            return new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
        }

        private static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string s = tag.TrimStart('v', 'V').Trim();
            var m = Regex.Match(s, @"^[0-9]+(\.[0-9]+){0,3}");
            if (!m.Success) return null;
            return Version.TryParse(m.Value, out var v) ? Normalize(v) : null;
        }

        private static DateTime ReadLastCheck()
        {
            try
            {
                string s = File.ReadAllText(StateFile).Trim();
                if (DateTime.TryParse(s, null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal |
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                    return dt;
            }
            catch { }
            return DateTime.MinValue;
        }

        private static void WriteLastCheck(DateTime t)
        {
            try { Directory.CreateDirectory(UpdateDir); File.WriteAllText(StateFile, t.ToUniversalTime().ToString("o")); }
            catch { }
        }

        private static void WriteFlag(Version v, string stagedAppDir)
        {
            try
            {
                Directory.CreateDirectory(UpdateDir);
                File.WriteAllText(ApplyFlag, "version=" + v + "\nstaged=" + stagedAppDir + "\n");
            }
            catch { }
        }

        private sealed class FlagMap
        {
            private readonly Dictionary<string, string> d =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public void Set(string k, string v) { d[k] = v; }
            public string TryGet(string k) { return d.TryGetValue(k, out var v) ? v : null; }
        }

        private static FlagMap ReadFlag()
        {
            var map = new FlagMap();
            try
            {
                foreach (var line in File.ReadAllLines(ApplyFlag))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) map.Set(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
            }
            catch { }
            return map;
        }

        private static void CleanupStale()
        {
            SafeDelete(ApplyFlag);
            SafeDelete(StagedRoot);
            SafeDelete(BackupDir);
            SafeDelete(RunnerDir);
            SafeDelete(DownloadDir);
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(UpdateDir);
                File.AppendAllText(LogFile, DateTime.Now.ToString("s") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
