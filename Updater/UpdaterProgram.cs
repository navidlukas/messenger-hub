using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace MessengerHubUpdater
{
    /// <summary>
    /// Tiny background helper that installs a staged Messenger Hub update.
    /// It runs from data\update\runner (outside the app folder) so it can overwrite
    /// every app file, including the previous updater.exe. It never touches data\.
    ///
    ///   updater.exe --apply --pid N --app "DIR" --staged "DIR" --backup "DIR" --update "DIR" --relaunch 0|1
    ///
    /// Steps: wait for the app (pid) to exit, back up the files it is about to replace,
    /// copy the staged build over the app folder, verify, and roll back on any failure.
    /// </summary>
    internal static class Program
    {
        private static string logFile;

        private static void Main(string[] args)
        {
            try { Run(args); }
            catch (Exception ex) { Log("fatal: " + ex.Message); }
        }

        private static void Run(string[] args)
        {
            var a = Parse(args);
            if (!a.ContainsKey("apply")) return;

            int pid = GetInt(a, "pid", 0);
            string appDir = Get(a, "app");
            string staged = Get(a, "staged");
            string backup = Get(a, "backup");
            string updateDir = Get(a, "update");
            bool relaunch = GetInt(a, "relaunch", 0) == 1;

            if (string.IsNullOrEmpty(appDir) || string.IsNullOrEmpty(staged) ||
                string.IsNullOrEmpty(backup) || string.IsNullOrEmpty(updateDir))
                return;

            logFile = Path.Combine(updateDir, "update.log");
            Log("updater start pid=" + pid + " relaunch=" + relaunch);

            WaitForExit(pid);
            Thread.Sleep(1000); // let the OS release file handles

            string appExe = Path.Combine(appDir, "MessengerHub.exe");
            string stagedExe = Path.Combine(staged, "MessengerHub.exe");

            bool ok;
            try
            {
                SafeDeleteDir(backup);
                Directory.CreateDirectory(backup);
                CopyTree(staged, appDir, backup);
                ok = File.Exists(appExe) && new FileInfo(appExe).Length > 0 && SameVersion(appExe, stagedExe);
            }
            catch (Exception ex)
            {
                Log("apply error: " + ex.Message);
                ok = false;
            }

            if (!ok)
            {
                Log("apply failed; rolling back");
                try { RollBack(backup, appDir); } catch (Exception ex) { Log("rollback error: " + ex.Message); }
            }
            else
            {
                Log("apply ok -> " + Version(appExe));
            }

            // Clear the flag either way so the install is not attempted again.
            try { File.Delete(Path.Combine(updateDir, "apply.flag")); } catch { }

            if (relaunch && File.Exists(appExe))
            {
                try { Process.Start(new ProcessStartInfo(appExe) { UseShellExecute = false, WorkingDirectory = appDir }); }
                catch (Exception ex) { Log("relaunch error: " + ex.Message); }
            }

            // Best-effort cleanup of the temporary install folders (log + state are kept).
            SafeDeleteDir(staged);
            SafeDeleteDir(backup);
            SafeDeleteDir(Path.Combine(updateDir, "staged"));
            SafeDeleteDir(Path.Combine(updateDir, "download"));
            Log("updater done");
        }

        // ---------------------------------------------------------------- file ops

        /// <summary>Copy every file under src into dst; back up each file we overwrite first.</summary>
        private static void CopyTree(string src, string dst, string backupRoot)
        {
            foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = Relative(src, file);
                string target = Path.Combine(dst, rel);

                // Back up the original before replacing it, so we can roll back.
                if (File.Exists(target))
                {
                    string bak = Path.Combine(backupRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(bak));
                    CopyWithRetry(target, bak);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                CopyWithRetry(file, target);
            }
        }

        /// <summary>Restore every backed-up file over the app folder.</summary>
        private static void RollBack(string backupRoot, string dst)
        {
            if (!Directory.Exists(backupRoot)) return;
            foreach (string file in Directory.GetFiles(backupRoot, "*", SearchOption.AllDirectories))
            {
                string rel = Relative(backupRoot, file);
                string target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                CopyWithRetry(file, target);
            }
        }

        private static void CopyWithRetry(string from, string to)
        {
            Exception last = null;
            for (int i = 0; i < 20; i++)
            {
                try { File.Copy(from, to, true); return; }
                catch (Exception ex) { last = ex; Thread.Sleep(500); }
            }
            throw new IOException("copy failed: " + from + " -> " + to, last);
        }

        private static string Relative(string root, string full)
        {
            root = root.TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length)
                : Path.GetFileName(full);
        }

        private static bool SameVersion(string a, string b)
        {
            try { return Version(a) == Version(b) && Version(a) != null; }
            catch { return false; }
        }

        private static string Version(string exe)
        {
            try { return FileVersionInfo.GetVersionInfo(exe).FileVersion; }
            catch { return null; }
        }

        private static void WaitForExit(int pid)
        {
            if (pid <= 0) return;
            try
            {
                var p = Process.GetProcessById(pid);
                if (!p.WaitForExit(60000)) Log("parent still running after 60s");
            }
            catch { /* already gone */ }
        }

        private static void SafeDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        // ---------------------------------------------------------------- args + log

        private static Dictionary<string, string> Parse(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                string key = args[i].Substring(2);
                string val = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "";
                d[key] = val;
            }
            return d;
        }

        private static string Get(Dictionary<string, string> d, string k) { return d.TryGetValue(k, out var v) ? v : null; }

        private static int GetInt(Dictionary<string, string> d, string k, int fallback)
        {
            return d.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : fallback;
        }

        private static void Log(string msg)
        {
            try
            {
                if (logFile != null)
                    File.AppendAllText(logFile, DateTime.Now.ToString("s") + "  [updater] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
