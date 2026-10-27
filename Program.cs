using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace MessengerHub
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool resetRequested = args.Any(a => a.Equals("--reset", StringComparison.OrdinalIgnoreCase));

            // One running copy per app folder (copies in different folders can run side by side).
            string id = AppPaths.Root.ToLowerInvariant().GetHashCode().ToString("X8");
            using (var mutex = new Mutex(false, "MessengerHub_" + id))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "MessengerHub_Show_" + id))
            {
                bool owned;
                try
                {
                    // After a reset, the old copy is still closing, so wait for it.
                    owned = mutex.WaitOne(resetRequested ? 20000 : 0);
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }

                if (!owned)
                {
                    // Already running (maybe hidden in the tray): ask it to show its window.
                    showSignal.Set();
                    return;
                }

                try
                {
                    if (resetRequested || File.Exists(AppPaths.ResetFlag))
                        CompleteReset();

                    try
                    {
                        AppPaths.EnsureFolders();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            "Messenger Hub stores everything in a 'data' folder next to the app, " +
                            "but it can't write to this location:\n\n" + AppPaths.Root +
                            "\n\nMove the app folder somewhere writable (e.g. Desktop, Documents or a USB drive).\n\n" +
                            ex.Message,
                            "Messenger Hub", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    // A download staged on a previous run but not yet installed: install it now
                    // and relaunch. This instance exits so its files can be replaced.
                    if (Updater.HasPendingUpdate() && Updater.LaunchRunner(true))
                        return;

                    // Password gate. Everything the app does is behind this.
                    var security = new Security();
                    if (!security.IsSet)
                    {
                        string pw = PasswordDialog.CreatePassword();
                        if (pw == null) return; // user closed the window: don't start
                        security.CreatePassword(pw);
                    }
                    else
                    {
                        var result = PasswordDialog.EnterPassword(security.TryUnlock);
                        if (result == UnlockResult.Cancel) return;
                        if (result == UnlockResult.Reset)
                        {
                            // Hand off to a fresh "--reset" instance, which wipes the data folder and starts clean.
                            try { File.WriteAllText(AppPaths.ResetFlag, "reset"); } catch { }
                            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--reset") { UseShellExecute = false }); } catch { }
                            return;
                        }
                    }

                    // Unlocked: restore the login data to plain form before the browser touches it.
                    security.RestoreCookieKey();
                    security.UnlockProfiles();

                    Application.Run(new MainForm(showSignal, security));

                    // Closed for good: wait for the browser to exit, trim oversized caches, then re-lock.
                    security.WaitForBrowsers();
                    security.CaptureCookieKey();
                    security.TrimCaches();
                    security.LockProfiles();

                    // If a newer build is staged, install it quietly now (no relaunch: the user quit).
                    Updater.LaunchRunner(false);
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        /// <summary>Delete the whole data folder. Retries while the old browser processes finish closing.</summary>
        private static void CompleteReset()
        {
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    if (Directory.Exists(AppPaths.Data)) Directory.Delete(AppPaths.Data, true);
                    File.Delete(AppPaths.ResetFlag);
                    return;
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }

            MessageBox.Show(
                "The reset could not finish because some files are still in use.\n\n" +
                "It will finish automatically the next time you start Messenger Hub.",
                "Messenger Hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>All paths live inside the app folder, so the app is fully portable.</summary>
    internal static class AppPaths
    {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string Data = Path.Combine(Root, "data");
        public static readonly string WebView = Path.Combine(Data, "WebView2");   // logins, cookies, cache
        public static readonly string EbWebView = Path.Combine(WebView, "EBWebView");       // browser profiles live here
        public static readonly string LocalStateFile = Path.Combine(EbWebView, "Local State");
        public static readonly string Icons = Path.Combine(Data, "icons");        // saved site icons (sidebar)
        public static readonly string SettingsFile = Path.Combine(Data, "settings.ini");
        public static readonly string SecurityFile = Path.Combine(Data, "security.dat"); // salt + wrapped master key
        public static readonly string CookieKeyFile = Path.Combine(Data, "webview.key"); // portable cookie key
        public static readonly string ResetFlag = Path.Combine(Root, "reset.pending");

        public static void EnsureFolders()
        {
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(WebView);
            Directory.CreateDirectory(Icons);

            // Test that we can really write here.
            string probe = Path.Combine(Data, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
    }
}
