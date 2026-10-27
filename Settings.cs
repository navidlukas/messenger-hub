using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace MessengerHub
{
    /// <summary>Tiny key=value settings file stored in data\settings.ini.</summary>
    internal class Settings
    {
        private readonly Dictionary<string, string> values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Loads settings.ini; if it's missing, empty or damaged, falls back to settings.ini.bak.</summary>
        public static Settings Load(string path)
        {
            var s = new Settings();
            if (!s.ReadFrom(path)) s.ReadFrom(path + ".bak");
            return s;
        }

        private bool ReadFrom(string path)
        {
            values.Clear();
            try
            {
                if (!File.Exists(path)) return false;
                foreach (var line in File.ReadAllLines(path))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { values.Clear(); }
            return values.Count > 0;
        }

        /// <summary>
        /// Crash-safe save: write a temporary file, force it onto the disk, then swap it in and keep the
        /// previous version as settings.ini.bak. A crash or power cut at any moment leaves either the old
        /// or the new file complete, never a half-written one.
        /// </summary>
        public void Save(string path)
        {
            string tmp = path + ".tmp", bak = path + ".bak";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs))
                {
                    foreach (var kv in values) w.WriteLine(kv.Key + "=" + kv.Value);
                    w.Flush();
                    fs.Flush(true); // really on the disk, not just in Windows' write cache
                }

                if (!File.Exists(path)) { File.Move(tmp, path); return; }
                try
                {
                    File.Replace(tmp, path, bak); // one step: tmp becomes settings.ini, old one becomes .bak
                }
                catch
                {
                    // Some USB-stick file systems don't support Replace: do the same in three steps.
                    File.Copy(path, bak, true);
                    File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            catch { /* not critical: the previous settings stay in place */ }
        }

        private int GetInt(string key, int fallback) =>
            values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        private void SetInt(string key, int value) =>
            values[key] = value.ToString(CultureInfo.InvariantCulture);

        public string Get(string key, string fallback = null) =>
            values.TryGetValue(key, out var v) ? v : fallback;

        public void Set(string key, string value) =>
            values[key] = (value ?? "").Replace("\r", " ").Replace("\n", " ");

        public void Remove(string key) => values.Remove(key);

        /// <summary>Restore the last window size/position (only if it is still visible on a screen).</summary>
        public void ApplyTo(Form form)
        {
            var bounds = new Rectangle(GetInt("WindowX", -1), GetInt("WindowY", -1),
                                       GetInt("WindowWidth", 1200), GetInt("WindowHeight", 800));

            bool onScreen = false;
            foreach (var screen in Screen.AllScreens)
                if (screen.WorkingArea.IntersectsWith(bounds)) { onScreen = true; break; }

            if (values.ContainsKey("WindowX") && onScreen)
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Bounds = bounds;
            }
            else
            {
                form.StartPosition = FormStartPosition.CenterScreen;
                form.Size = new Size(bounds.Width, bounds.Height);
            }

            if (GetInt("Maximized", 0) == 1) form.WindowState = FormWindowState.Maximized;
        }

        public void CaptureFrom(Form form)
        {
            var b = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            SetInt("WindowX", b.X);
            SetInt("WindowY", b.Y);
            SetInt("WindowWidth", b.Width);
            SetInt("WindowHeight", b.Height);
            SetInt("Maximized", form.WindowState == FormWindowState.Maximized ? 1 : 0);
        }
    }
}
