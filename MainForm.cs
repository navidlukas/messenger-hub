using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MessengerHub
{
    internal class MainForm : Form
    {
        private const string AppName = "Messenger Hub";
        private const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        private static readonly Color SidebarColor = Color.FromArgb(32, 44, 51);
        private static readonly Color ContentColor = Color.FromArgb(17, 27, 33);

        private readonly Settings settings;
        private readonly Security security;
        private readonly List<Account> accounts = new List<Account>();

        private readonly MenuStrip menu = new MenuStrip();
        private readonly Panel sidebar = new Panel();
        private readonly FlowLayoutPanel accountList = new FlowLayoutPanel();
        private readonly AccountButton addButton = new AccountButton();
        private readonly ContextMenuStrip addMenu = new ContextMenuStrip();
        private readonly Panel content = new Panel();
        private readonly Label emptyLabel = new Label();
        private readonly NotifyIcon tray = new NotifyIcon();
        // Four tray-icon variants: [unread dot?][silent moon?]. Rebuilt once, swapped as state changes.
        private readonly Icon[] trayIcons = new Icon[4];
        private int trayIconIndex = -1;
        private bool globalSilent; // mute every account's sound at once (overrides per-account Silent)
        private ToolStripMenuItem silentMenuItem, silentTrayItem;
        private readonly ToolTip tips = new ToolTip();
        private ToolStripMenuItem accountMenu;

        private CoreWebView2Environment env;
        private Account active;
        private bool reallyExit;
        private bool resetting;
        private bool wasMinimized;
        private static readonly TimeSpan AutoRefreshAfter = TimeSpan.FromHours(3);
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        private readonly float scale;

        // Site icons (favicons) per messenger, saved in data\icons\<service>.png.
        private readonly Dictionary<string, Image> serviceIcons = new Dictionary<string, Image>();
        private readonly HashSet<string> capturingIcons = new HashSet<string>();
        private static readonly HttpClient http = CreateHttpClient();

        // Sidebar size ("Compact"): icons are drawn at 2/3 of the button height (28 px).
        private const int SidebarWidth = 48;
        private const int ButtonHeight = 42;

        // Auto-naming: checks every 10 s for accounts still on a default name ("WhatsApp 1").
        private readonly System.Windows.Forms.Timer identityTimer = new System.Windows.Forms.Timer { Interval = 10 * 1000 };
        private readonly HashSet<Account> identityBusy = new HashSet<Account>();

        // Unread polling: for services whose tab title doesn't carry the count (e.g. Telegram),
        // read it from the page every few seconds instead.
        private readonly System.Windows.Forms.Timer unreadTimer = new System.Windows.Forms.Timer { Interval = 4000 };

        // Drag-to-reorder state.
        private readonly Panel dropIndicator = new Panel();
        private Account dragAcc;
        private Point dragStart;
        private bool dragging;
        private int dropIndex = -1;

        private RegisteredWaitHandle showSignalWait;

        private int S(float v) => (int)Math.Round(v * scale);

        public MainForm(EventWaitHandle showSignal, Security security)
        {
            this.security = security;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

            Text = AppName;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            MinimumSize = new Size(S(700), S(450));
            BackColor = ContentColor;

            settings = Settings.Load(AppPaths.SettingsFile);
            settings.ApplyTo(this);
            globalSilent = settings.Get("GlobalSilent") == "1";

            BuildMenu();
            BuildSidebar();
            BuildContent();
            BuildTray();

            // Order matters for docking: fill first, then left, then top.
            Controls.Add(content);
            Controls.Add(sidebar);
            Controls.Add(menu);
            MainMenuStrip = menu;

            Load += async (s, e) => await StartAsync();
            FormClosing += OnFormClosing;
            FormClosed += (s, e) =>
            {
                showSignalWait?.Unregister(null);
                refreshTimer.Dispose();
                identityTimer.Dispose();
                unreadTimer.Dispose();
                startupWatchdog?.Dispose();
                tray.Dispose();
                foreach (var ic in trayIcons) ic?.Dispose();
            };

            refreshTimer.Tick += (s, e) => AutoRefreshTick();
            refreshTimer.Start();

            identityTimer.Tick += async (s, e) => { await AutoNameTickAsync(); CalibrateCacheTick(); };
            identityTimer.Start();

            unreadTimer.Tick += async (s, e) => await UnreadPollAsync();
            unreadTimer.Start();

            // Hidden in tray or minimized -> all accounts go to background mode.
            VisibleChanged += (s, e) => ApplyBackgroundState();
            Resize += (s, e) =>
            {
                bool minimized = WindowState == FormWindowState.Minimized;
                if (minimized != wasMinimized) { wasMinimized = minimized; ApplyBackgroundState(); }
            };

            // Starting the .exe again while it's running shows this window.
            showSignalWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (st, timedOut) =>
            {
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)RestoreFromTray); } catch { }
            }, null, -1, false);
        }

        // ------------------------------------------------------------------ UI

        private void BuildMenu()
        {
            var app = new ToolStripMenuItem("&Menu");
            var add = new ToolStripMenuItem("&Add account");
            AddServiceItems(add.DropDownItems);
            app.DropDownItems.Add(add);
            app.DropDownItems.Add(new ToolStripSeparator());
            app.DropDownItems.Add("Check for &updates...", null, async (s, e) => await CheckForUpdatesAsync());
            app.DropDownItems.Add("&Clear cache...", null, async (s, e) => await ClearCacheAsync());
            app.DropDownItems.Add("&Reset Messenger Hub...", null, (s, e) => ResetApp());
            app.DropDownItems.Add(new ToolStripSeparator());
            silentMenuItem = new ToolStripMenuItem("&Silent") { Checked = globalSilent };
            silentMenuItem.Click += (s, e) => SetGlobalSilent(!globalSilent);
            app.DropDownItems.Add(silentMenuItem);
            app.DropDownItems.Add(new ToolStripSeparator());
            app.DropDownItems.Add("E&xit", null, (s, e) => ExitApp());

            // Same items as the right-click menu, for the selected account.
            accountMenu = new ToolStripMenuItem("A&ccount");
            accountMenu.DropDownOpening += (s, e) => ReplaceItems(accountMenu.DropDownItems,
                active != null
                    ? AccountMenuItems(active)
                    : new ToolStripItem[] { new ToolStripMenuItem("(no account selected)") { Enabled = false } });
            accountMenu.DropDownItems.Add("(none)"); // placeholder: a menu needs one item to open

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add("Open &data folder", null, (s, e) => OpenFolder(AppPaths.Data));
            help.DropDownItems.Add("&About", null, (s, e) => MessageBox.Show(this,
                AppName + " " + Application.ProductVersion + "\n\nPortable mode: all data is stored in\n" + AppPaths.Data,
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information));

            menu.Items.Add(app);
            menu.Items.Add(accountMenu);
            menu.Items.Add(help);
            menu.Dock = DockStyle.Top;
        }

        private void BuildSidebar()
        {
            sidebar.Dock = DockStyle.Left;
            sidebar.Width = S(SidebarWidth);
            sidebar.BackColor = SidebarColor;

            accountList.Dock = DockStyle.Fill;
            accountList.FlowDirection = FlowDirection.TopDown;
            accountList.WrapContents = false;
            accountList.AutoScroll = true;
            accountList.Padding = new Padding(0, S(6), 0, 0);
            accountList.BackColor = SidebarColor;

            // "+" opens a menu with every supported messenger.
            AddServiceItems(addMenu.Items);

            addButton.IsAddButton = true;
            addButton.Size = new Size(S(SidebarWidth), S(ButtonHeight));
            addButton.Margin = Padding.Empty;
            addButton.BackColor = SidebarColor;
            addButton.MouseClick += (s, e) => addMenu.Show(addButton, new Point(addButton.Width - S(8), S(8)));
            tips.SetToolTip(addButton, "Add account");

            accountList.Controls.Add(addButton);
            sidebar.Controls.Add(accountList);

            // Line that shows where a dragged icon will land.
            dropIndicator.BackColor = Color.White;
            dropIndicator.Height = Math.Max(2, S(2));
            dropIndicator.Width = S(SidebarWidth) - S(8);
            dropIndicator.Left = S(4);
            dropIndicator.Visible = false;
            sidebar.Controls.Add(dropIndicator);
            dropIndicator.BringToFront();
        }

        /// <summary>One menu item per supported messenger (used by "+" and App > Add account).</summary>
        private void AddServiceItems(ToolStripItemCollection items)
        {
            foreach (var service in ServiceType.All)
                items.Add(service.Name, service.MenuIcon(S(16)), async (s, e) => await AddAccountAsync(service));
        }

        /// <summary>Swaps a menu's items, disposing the old ones (menus are rebuilt each time they open).</summary>
        private static void ReplaceItems(ToolStripItemCollection items, IEnumerable<ToolStripItem> newItems)
        {
            var old = items.Cast<ToolStripItem>().ToList();
            items.Clear();
            foreach (var item in old) item.Dispose();
            items.AddRange(newItems.ToArray());
        }

        private void BuildContent()
        {
            content.Dock = DockStyle.Fill;
            content.BackColor = ContentColor;

            emptyLabel.Dock = DockStyle.Fill;
            emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            emptyLabel.ForeColor = Color.FromArgb(140, 160, 170);
            emptyLabel.Font = new Font("Segoe UI", 12f);
            emptyLabel.Text = "No accounts yet.\nClick + on the left to add WhatsApp, Telegram or Messenger.";
            emptyLabel.Visible = false;
            content.Controls.Add(emptyLabel);
        }

        private void BuildTray()
        {
            for (int i = 0; i < 4; i++) trayIcons[i] = MakeTrayIcon((i & 1) != 0, (i & 2) != 0);
            tray.Icon = trayIcons[0] ?? Icon;
            tray.Text = AppName;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };

            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open " + AppName, null, (s, e) => RestoreFromTray());
            trayMenu.Items.Add(new ToolStripSeparator());
            silentTrayItem = new ToolStripMenuItem("Silent") { Checked = globalSilent };
            silentTrayItem.Click += (s, e) => SetGlobalSilent(!globalSilent);
            trayMenu.Items.Add(silentTrayItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, (s, e) => ExitApp());
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        /// <summary>
        /// Builds the tray icon from the app icon, optionally with a red unread dot (top-right) and/or
        /// a crescent-moon "silent" badge (bottom-right).
        /// </summary>
        private Icon MakeTrayIcon(bool dot, bool silent)
        {
            if (Icon == null) return null;
            try
            {
                int size = 32;
                using (var bmp = new Bitmap(size, size))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        using (var src = Icon.ToBitmap())
                            g.DrawImage(src, new Rectangle(0, 0, size, size));

                        if (silent) DrawMoonBadge(g, size);

                        if (dot)
                        {
                            int r = (int)(size * 0.42f);
                            using (var brush = new SolidBrush(Color.FromArgb(234, 67, 53)))
                                g.FillEllipse(brush, size - r, 0, r - 1, r - 1);
                        }
                    }
                    IntPtr h = bmp.GetHicon();
                    try { return (Icon)Icon.FromHandle(h).Clone(); } // Clone owns its own copy
                    finally { DestroyIcon(h); }                      // so we can free the temporary handle
                }
            }
            catch { return null; }
        }

        // Crescent-moon badge (bottom-right): a blue disc with a crescent carved out of a white circle.
        private static void DrawMoonBadge(Graphics g, int size)
        {
            float r = size * 0.5f;
            float bx = size - r, by = size - r;
            using (var blue = new SolidBrush(Color.FromArgb(120, 120, 235)))
            using (var white = new SolidBrush(Color.White))
            {
                g.FillEllipse(blue, bx, by, r - 1, r - 1);
                float mr = r * 0.52f;
                float mx = bx + r * 0.26f, my = by + r * 0.22f;
                g.FillEllipse(white, mx, my, mr, mr);          // the moon
                g.FillEllipse(blue, mx + mr * 0.42f, my - mr * 0.12f, mr, mr); // carve the crescent
            }
        }

        // ------------------------------------------------------------------ Startup

        private async Task StartAsync()
        {
            try
            {
                // One shared browser engine; each account gets its own profile inside data\WebView2.
                env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                var answer = MessageBox.Show(this,
                    "Microsoft Edge WebView2 Runtime is not installed on this PC.\n" +
                    "It comes with Windows 11 and most Windows 10 PCs.\n\nOpen the download page now?",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes) OpenExternal(WebView2DownloadUrl);
                ExitApp();
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not start the browser engine:\n\n" + ex.Message,
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitApp();
                return;
            }

            // First run: one WhatsApp account. Its id "whatsapp" keeps the login from version 0.1.
            string saved = settings.Get("Accounts");
            var ids = saved == null
                ? new List<string> { "whatsapp" }
                : saved.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            foreach (var id in ids)
            {
                var service = ServiceType.ByKey(settings.Get("Type." + id, ServiceType.WhatsApp.Key));
                var acc = CreateAccount(id, service, settings.Get("Name." + id) ?? NextName(service));
                acc.Silent = settings.Get("Silent." + id) == "1";
                acc.Identity = settings.Get("Id." + id); // known identity, for duplicate detection
                // Apply a previously measured cache limit, or mark for calibration (keeps the service default meanwhile).
                if (int.TryParse(settings.Get("CacheLimit." + id), out int limMb))
                    security?.SetCacheLimit(id, limMb);
                else
                    acc.NeedsCalibration = true;
                RefreshButton(acc);
            }
            SaveSettings();

            string last = settings.Get("ActiveAccount");
            SelectAccount(accounts.FirstOrDefault(a => a.Id == last) ?? accounts.FirstOrDefault());

            await Task.WhenAll(accounts.ToList().Select(InitAccountAsync));

            // Some accounts occasionally hang on their own loading screen when many load at
            // once; reload any that are still stuck a short time after startup (once each).
            StartStartupWatchdog();

            // Check for a new version in the background (silent, at most once a week).
            Updater.StartBackgroundCheck();
        }

        // ------------------------------------------------------------------ Startup load recovery

        private const int MaxStartupReloads = 2;
        private System.Windows.Forms.Timer startupWatchdog;

        /// <summary>
        /// ~20s after startup, reload any account that never finished loading, and the account
        /// you're looking at if it still appears blank (e.g. stuck on a spinner). One-shot.
        /// </summary>
        private void StartStartupWatchdog()
        {
            startupWatchdog?.Dispose();
            startupWatchdog = new System.Windows.Forms.Timer { Interval = 20000 };
            startupWatchdog.Tick += async (s, e) =>
            {
                startupWatchdog.Stop();
                foreach (var acc in accounts.ToList()) await CheckStartupLoad(acc);
            };
            startupWatchdog.Start();
        }

        private async Task CheckStartupLoad(Account acc)
        {
            if (acc.View.IsDisposed) return;
            var core = acc.View.CoreWebView2;
            if (core == null || acc.StartupReloads >= MaxStartupReloads) return;

            bool stuck = !acc.FirstLoadOk;                 // a navigation never completed
            if (!stuck && acc == active)                   // loaded but maybe spinning: probe the one in view
                stuck = await LooksBlank(core);

            if (stuck)
            {
                acc.StartupReloads++;
                try { core.Reload(); } catch { }
            }
        }

        /// <summary>True if the page shows almost no text — a good sign it's stuck on a loading screen.</summary>
        private static async Task<bool> LooksBlank(CoreWebView2 core)
        {
            try
            {
                string r = await core.ExecuteScriptAsync(
                    "(function(){try{var b=document.body;return b?b.innerText.replace(/\\s+/g,'').length:0;}catch(e){return -1;}})()");
                return int.TryParse(r, out int n) && n >= 0 && n < 25;
            }
            catch { return false; }
        }

        /// <summary>Menu &gt; Check for updates: check now and, if one is ready, offer to restart and apply it.</summary>
        private async Task CheckForUpdatesAsync()
        {
            Cursor = Cursors.WaitCursor;
            Updater.CheckResult result;
            try { result = await Task.Run(() => Updater.CheckNow()); }
            finally { Cursor = Cursors.Default; }

            switch (result)
            {
                case Updater.CheckResult.Ready:
                    string v = Updater.PendingVersion();
                    var ans = MessageBox.Show(this,
                        "Update " + (v ?? "") + " is downloaded and ready.\n\n" +
                        "Restart Messenger Hub now to apply it?\n" +
                        "(If you choose No, it installs automatically next time you quit.)",
                        AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (ans == DialogResult.Yes)
                    {
                        Updater.RequestRelaunchOnExit = true; // install on exit, then reopen
                        ExitApp();
                    }
                    break;

                case Updater.CheckResult.UpToDate:
                    MessageBox.Show(this,
                        "You're on the latest version (" + Updater.CurrentVersion + ").",
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;

                default:
                    MessageBox.Show(this,
                        "Couldn't check for updates right now.\nCheck your internet connection and try again.",
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
            }
        }

        // ------------------------------------------------------------------ Accounts

        private string NextName(ServiceType service)
        {
            int n = 1;
            while (accounts.Any(a => a.Name == service.Name + " " + n)) n++;
            return service.Name + " " + n;
        }

        private Account CreateAccount(string id, ServiceType service, string name)
        {
            var acc = new Account { Id = id, Name = name, Service = service };

            acc.View = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = ContentColor,
                Visible = false,
            };
            content.Controls.Add(acc.View);

            acc.Button = new AccountButton
            {
                Size = new Size(S(SidebarWidth), S(ButtonHeight)),
                Margin = Padding.Empty,
                BackColor = SidebarColor,
                Accent = service.Color,
                Letter = service.Letter,
            };
            acc.Button.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left && !dragging) SelectAccount(acc); };

            // Drag up/down to reorder.
            acc.Button.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragAcc = acc; dragStart = Cursor.Position; } };
            acc.Button.MouseMove += (s, e) => OnDragMove(acc, e);
            acc.Button.MouseUp += (s, e) => OnDragEnd(acc);

            // Right-click menu (rebuilt each time so the check marks are current).
            var ctx = new ContextMenuStrip();
            ctx.Opening += (s, e) => ReplaceItems(ctx.Items, AccountMenuItems(acc));
            ctx.Items.Add("(loading)"); // placeholder: a menu needs one item to open
            acc.Button.ContextMenuStrip = ctx;

            // Keep the + button last.
            accountList.Controls.Add(acc.Button);
            accountList.Controls.SetChildIndex(addButton, accountList.Controls.Count - 1);

            security?.SetCacheLimit(acc.Id, acc.Service.CacheLimitMB); // so the exit cache-trim knows this account's limit
            accounts.Add(acc);
            RefreshButton(acc);
            return acc;
        }

        private ToolStripItem[] AccountMenuItems(Account acc)
        {
            var silent = new ToolStripMenuItem("&Silent (no sound)") { Checked = acc.Silent };
            silent.Click += (s, e) => SetSilent(acc, !acc.Silent);

            var rename = new ToolStripMenuItem("&Rename...");
            rename.Click += (s, e) => RenameAccount(acc);

            var reload = new ToolStripMenuItem("Re&load");
            reload.Click += (s, e) => acc.View.CoreWebView2?.Reload();

            var remove = new ToolStripMenuItem("Re&move...");
            remove.Click += async (s, e) => await RemoveAccountAsync(acc);

            return new ToolStripItem[]
            {
                silent, new ToolStripSeparator(),
                rename, reload, new ToolStripSeparator(),
                remove,
            };
        }

        private void RefreshButton(Account acc)
        {
            acc.Button.Label = Account.ShortLabel(acc.Name);
            acc.Button.Silent = acc.Silent;
            acc.Button.Unread = acc.Unread;
            acc.Button.SiteIcon = ServiceIcon(acc.Service);
            acc.Button.Invalidate();
            tips.SetToolTip(acc.Button, acc.TooltipText());
        }

        // ------------------------------------------------------------------ Drag to reorder

        private void OnDragMove(Account acc, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || dragAcc != acc) return;

            // Start dragging only after the mouse moved a few pixels (so a click stays a click).
            if (!dragging)
            {
                var drag = SystemInformation.DragSize;
                if (Math.Abs(Cursor.Position.Y - dragStart.Y) < drag.Height &&
                    Math.Abs(Cursor.Position.X - dragStart.X) < drag.Width) return;
                dragging = true;
                acc.Button.Cursor = Cursors.SizeNS;
            }

            // Where would it land? Before the first icon whose middle is below the mouse.
            var buttons = accounts.Select(a => a.Button).ToList();
            int y = accountList.PointToClient(Cursor.Position).Y;
            dropIndex = buttons.Count;
            for (int i = 0; i < buttons.Count; i++)
            {
                if (y < buttons[i].Top + buttons[i].Height / 2) { dropIndex = i; break; }
            }

            int lineY = dropIndex < buttons.Count ? buttons[dropIndex].Top : buttons[buttons.Count - 1].Bottom;
            var p = sidebar.PointToClient(accountList.PointToScreen(new Point(0, lineY)));
            dropIndicator.Top = p.Y - dropIndicator.Height / 2;
            dropIndicator.Visible = true;
            dropIndicator.BringToFront();
        }

        private void OnDragEnd(Account acc)
        {
            bool wasDragging = dragging && dragAcc == acc;
            dragAcc = null;
            dragging = false;
            dropIndicator.Visible = false;
            acc.Button.Cursor = Cursors.Hand;
            if (!wasDragging || dropIndex < 0) return;

            int from = accounts.IndexOf(acc);
            int to = dropIndex > from ? dropIndex - 1 : dropIndex;
            dropIndex = -1;
            if (from < 0 || to == from) return;

            accounts.RemoveAt(from);
            accounts.Insert(to, acc);
            accountList.Controls.SetChildIndex(acc.Button, to);
            accountList.Controls.SetChildIndex(addButton, accountList.Controls.Count - 1); // + stays last
            SaveSettings(); // the order is saved with the account list
        }

        // ------------------------------------------------------------------ Auto-naming

        /// <summary>True while the account still has its automatic name, e.g. "Telegram 2".</summary>
        private static bool HasDefaultName(Account acc) =>
            Regex.IsMatch(acc.Name ?? "", "^" + Regex.Escape(acc.Service.Name) + @" \d+$");

        /// <summary>
        /// After an account logs in, read who it is (phone number for WhatsApp/Telegram, profile name for
        /// Messenger). This is used to auto-name the account and to block adding the SAME account twice.
        /// Accounts you renamed keep their name; the detected identity is still recorded for the duplicate check.
        /// WeChat and Google Messages don't expose an identity, so duplicates can't be detected for them.
        /// </summary>
        private async Task AutoNameTickAsync()
        {
            foreach (var acc in accounts.ToList())
            {
                if (acc.View.IsDisposed) continue;                       // removed meanwhile
                if (!string.IsNullOrEmpty(acc.Identity)) continue;        // already identified
                if (string.IsNullOrEmpty(acc.Service.IdentityScript)) continue; // service has no identity
                var core = acc.View.CoreWebView2;
                if (core == null) continue;
                if (!identityBusy.Add(acc)) continue;

                try
                {
                    string found = DecodeJsonString(await core.ExecuteScriptAsync(acc.Service.IdentityScript));
                    found = (found ?? "").Replace(";", ",").Replace("\r", " ").Replace("\n", " ").Trim();
                    if (found.Length > 40) found = found.Substring(0, 40).Trim();
                    if (found.Length == 0 || !accounts.Contains(acc)) continue; // not logged in yet; try next tick

                    // Block a just-added account that is the same account as one already here.
                    if (acc.IsNew)
                    {
                        var existing = accounts.FirstOrDefault(a =>
                            a != acc && a.Service == acc.Service &&
                            string.Equals(a.Identity, found, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            await RemoveAccountCore(acc);
                            SelectAccount(existing);
                            MessageBox.Show(this,
                                acc.Service.Name + " account \"" + found + "\" is already added, so the duplicate was removed.",
                                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                            continue;
                        }
                    }

                    acc.Identity = found;
                    acc.IsNew = false;
                    if (HasDefaultName(acc)) acc.Name = found; // auto-name only if you haven't named it yourself
                    RefreshButton(acc);
                    SaveSettings();
                    UpdateTitle();
                }
                catch { /* page not ready or changed; try again next tick */ }
                finally
                {
                    identityBusy.Remove(acc);
                }
            }
        }

        /// <summary>ExecuteScriptAsync returns JSON: "null" or a quoted string. Returns the plain text or null.</summary>
        private static string DecodeJsonString(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length < 2 || json[0] != '"' || json[json.Length - 1] != '"') return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 1; i < json.Length - 1; i++)
            {
                char c = json[i];
                if (c != '\\' || i + 1 >= json.Length - 1) { sb.Append(c); continue; }
                char n = json[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < json.Length - 1 &&
                            int.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(n); break; // \" \\ \/
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ Site icons (favicons)

        private string IconFile(ServiceType svc) => Path.Combine(AppPaths.Icons, svc.Key + ".png");

        /// <summary>
        /// The saved site icon for a messenger, or null (then the colored letter circle is shown).
        /// The file is read once; "no icon yet" is remembered too, so the disk isn't checked on every title change.
        /// </summary>
        private Image ServiceIcon(ServiceType svc)
        {
            if (serviceIcons.TryGetValue(svc.Key, out var cached)) return cached;
            Image icon = null;
            try
            {
                string file = IconFile(svc);
                if (File.Exists(file)) icon = DecodeImage(File.ReadAllBytes(file));
            }
            catch { }
            serviceIcons[svc.Key] = icon;
            return icon;
        }

        /// <summary>
        /// Saves the site's favicon (the icon on its browser tab) the first time we see it.
        /// It's only saved while there are no unread messages, so an unread dot never gets stuck on it.
        /// </summary>
        private async Task CaptureIconAsync(Account acc)
        {
            var svc = acc.Service;
            var core = acc.View.CoreWebView2;
            if (core == null || acc.Unread > 0 || ServiceIcon(svc) != null) return;
            if (svc.IconPage != null && (core.Source ?? "").IndexOf(svc.IconPage, StringComparison.OrdinalIgnoreCase) < 0) return;
            if (!capturingIcons.Add(svc.Key)) return;

            try
            {
                // Best quality: download the favicon file itself. Fallback: the image WebView2 shows on the tab.
                Image best = await DownloadImageAsync(core.FaviconUri);
                Image shown = await GetFaviconImageAsync(core);
                if (shown != null && (best == null || shown.Width > best.Width)) { best?.Dispose(); best = shown; }
                else shown?.Dispose();

                if (best == null || acc.Unread > 0) { best?.Dispose(); return; }

                best.Save(IconFile(svc), ImageFormat.Png);
                serviceIcons[svc.Key] = best;
                foreach (var other in accounts.Where(x => x.Service == svc)) RefreshButton(other);
            }
            catch { }
            finally
            {
                capturingIcons.Remove(svc.Key);
            }
        }

        private static async Task<Image> GetFaviconImageAsync(CoreWebView2 core)
        {
            try
            {
                using (var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png))
                using (var ms = new MemoryStream())
                {
                    if (stream == null) return null;
                    await stream.CopyToAsync(ms);
                    return ms.Length == 0 ? null : DecodeImage(ms.ToArray());
                }
            }
            catch { return null; }
        }

        private static async Task<Image> DownloadImageAsync(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return null;
                const string pngData = "data:image/png;base64,";
                if (url.StartsWith(pngData, StringComparison.OrdinalIgnoreCase))
                    return DecodeImage(Convert.FromBase64String(url.Substring(pngData.Length)));
                if (!IsWebUrl(url)) return null;
                return DecodeImage(await http.GetByteArrayAsync(url));
            }
            catch { return null; }
        }

        /// <summary>PNG/JPG/GIF/BMP, or .ico (largest size). SVG isn't supported and returns null.</summary>
        private static Image DecodeImage(byte[] bytes)
        {
            try
            {
                using (var ms = new MemoryStream(bytes))
                {
                    bool isIco = bytes.Length > 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0;
                    if (isIco)
                        using (var ico = new Icon(ms, 256, 256)) return ico.ToBitmap();
                    using (var img = Image.FromStream(ms)) return new Bitmap(img);
                }
            }
            catch { return null; }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MessengerHub");
            return client;
        }

        private async Task InitAccountAsync(Account acc)
        {
            try
            {
                var options = env.CreateCoreWebView2ControllerOptions();
                options.ProfileName = acc.Id; // separate login/cookies per account
                await acc.View.EnsureCoreWebView2Async(env, options);
            }
            catch (Exception ex)
            {
                if (!acc.View.IsDisposed)
                    MessageBox.Show(this, "Could not open '" + acc.Name + "':\n\n" + ex.Message,
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            try
            {
                if (acc.View.IsDisposed) return; // removed while it was starting

                var core = acc.View.CoreWebView2;
                try { security?.AddBrowserPid((int)core.BrowserProcessId); } catch { } // so we can wait for it on exit
                ConfigureCore(core, acc);

                core.DocumentTitleChanged += (s, e) => UpdateUnread(acc, core.DocumentTitle);
                core.NewWindowRequested += (s, e) => OnNewWindowRequested(acc, e);
                core.NavigationStarting += (s, e) =>
                {
                    // Keep the account on its own site; anything else opens in the default browser,
                    // except sign-in provider pages, which must load in-app so SSO can finish.
                    if (IsWebUrl(e.Uri) && !acc.Service.Owns(e.Uri) && !IsAuthDomain(e.Uri))
                    {
                        e.Cancel = true;
                        OpenExternal(e.Uri);
                    }
                };
                core.NavigationCompleted += (s, e) =>
                {
                    if (e.IsSuccess) { acc.LoadedAt = DateTime.UtcNow; acc.FirstLoadOk = true; }
                };
                // If the page's process crashes, reload it once (rate-limited so it can't loop).
                core.ProcessFailed += (s, e) =>
                {
                    if (acc.View.IsDisposed) return;
                    if (DateTime.UtcNow - acc.LastRecoverUtc < TimeSpan.FromSeconds(20)) return;
                    acc.LastRecoverUtc = DateTime.UtcNow;
                    try { acc.View.CoreWebView2?.Reload(); } catch { }
                };
                core.FaviconChanged += async (s, e) => await CaptureIconAsync(acc);

                // Videos/GIFs only play when clicked (saves CPU).
                await core.AddScriptToExecuteOnDocumentCreatedAsync(Scripts.BlockVideoAutoplay);
                // Hide WhatsApp's "background sync" prompt and switch (it costs extra RAM/CPU).
                await core.AddScriptToExecuteOnDocumentCreatedAsync(Scripts.HideWhatsAppBackgroundSync);

                if (acc.View.IsDisposed) return;
                core.Navigate(acc.Service.Url);
                ApplyBackgroundState();
            }
            catch { /* the account was removed while starting */ }
        }

        /// <summary>Settings shared by account views and their pop-up windows.</summary>
        private void ConfigureCore(CoreWebView2 core, Account acc)
        {
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.PermissionRequested += (s, e) => OnPermissionRequested(acc, e);
            core.DownloadStarting += OnDownloadStarting;
        }

        private bool IsForeground(Account a) =>
            a == active && Visible && WindowState != FormWindowState.Minimized;

        /// <summary>
        /// Background accounts (and all accounts while hidden in the tray):
        ///  - low-memory mode (they keep running, so messages still arrive)
        ///  - muted if the account is set to Silent
        /// </summary>
        private void ApplyBackgroundState()
        {
            foreach (var a in accounts)
            {
                var core = a.View.CoreWebView2;
                if (core == null) continue;
                bool fg = IsForeground(a);
                try
                {
                    core.MemoryUsageTargetLevel = fg
                        ? CoreWebView2MemoryUsageTargetLevel.Normal
                        : CoreWebView2MemoryUsageTargetLevel.Low;
                }
                catch { }
                // Global Silent mutes everything; otherwise a per-account Silent mutes it while in the background.
                try { core.IsMuted = globalSilent || (a.Silent && !fg); } catch { }
            }
        }

        /// <summary>
        /// Auto-refresh: web messengers slowly grow in RAM over time. Every 5 minutes we check,
        /// and reload ONE background account that has been running 3+ hours. The account you
        /// are looking at is never reloaded.
        /// </summary>
        private void AutoRefreshTick()
        {
            // Long-running sessions: re-evaluate the weekly update check (cheap no-op until due).
            Updater.StartBackgroundCheck();

            var due = accounts
                .Where(a => a.View.CoreWebView2 != null && !IsForeground(a) &&
                            DateTime.UtcNow - a.LoadedAt >= AutoRefreshAfter)
                .OrderBy(a => a.LoadedAt)
                .FirstOrDefault();
            if (due == null) return;

            due.LoadedAt = DateTime.UtcNow;
            try { due.View.CoreWebView2.Reload(); } catch { }
        }

        private const int CacheFloorMB = 300; // a calibrated limit is never set below this

        /// <summary>
        /// Self-calibrating cache limit: once an account has been loaded ~2 minutes (warmed up, bundle cached),
        /// measure its cache and set its limit to that x2 (never below the floor). Runs once per account until
        /// a manual Clear cache resets it.
        /// </summary>
        private void CalibrateCacheTick()
        {
            if (security == null) return;
            foreach (var acc in accounts)
            {
                if (!acc.NeedsCalibration || acc.View.IsDisposed || acc.View.CoreWebView2 == null) continue;
                if ((DateTime.UtcNow - acc.LoadedAt).TotalSeconds < 120) continue; // let it warm up first

                long bytes = security.MeasureProfileCache(acc.Id);
                if (bytes <= 0) continue; // nothing cached yet; try again next tick

                int mb = (int)(bytes * 2 / (1024 * 1024));
                if (mb < CacheFloorMB) mb = CacheFloorMB;
                security.SetCacheLimit(acc.Id, mb);
                settings.Set("CacheLimit." + acc.Id, mb.ToString());
                settings.Save(AppPaths.SettingsFile);
                acc.NeedsCalibration = false;
            }
        }

        private void SelectAccount(Account acc)
        {
            active = acc;
            foreach (var a in accounts)
            {
                a.View.Visible = a == acc;
                a.Button.Active = a == acc;
                a.Button.Invalidate();
            }
            emptyLabel.Visible = acc == null;
            ApplyBackgroundState();

            if (acc != null)
            {
                acc.View.BringToFront();
                settings.Set("ActiveAccount", acc.Id);
                try { acc.View.Focus(); } catch { }
            }
            UpdateTitle();
        }

        private async Task AddAccountAsync(ServiceType service)
        {
            if (env == null) return;

            string id = service.Key.Substring(0, 2) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var acc = CreateAccount(id, service, NextName(service));
            acc.IsNew = true; // check it isn't a duplicate once it logs in
            acc.NeedsCalibration = true; // measure its cache limit after it warms up
            SaveSettings();
            SelectAccount(acc);
            await InitAccountAsync(acc);
        }

        /// <summary>Remove an account without asking (used when a just-added account turns out to be a duplicate).</summary>
        private async Task RemoveAccountCore(Account acc)
        {
            var core = acc.View.CoreWebView2;
            if (core != null)
            {
                try { await core.Profile.ClearBrowsingDataAsync(); } catch { }
                try { core.Profile.Delete(); } catch { } // removes the profile folder
            }

            accounts.Remove(acc);
            accountList.Controls.Remove(acc.Button);
            content.Controls.Remove(acc.View);
            tips.SetToolTip(acc.Button, null);
            acc.Button.ContextMenuStrip?.Dispose();
            acc.Button.Dispose();
            acc.View.Dispose();

            foreach (var key in new[] { "Name.", "Type.", "Silent.", "Id.", "CacheLimit." }) settings.Remove(key + acc.Id);
            SaveSettings();
            if (active == acc) SelectAccount(accounts.FirstOrDefault());
            UpdateTitle();
        }

        private void RenameAccount(Account acc)
        {
            string name = Dialogs.Prompt(this, "Rename account", "Name for this " + acc.Service.Name + " account:", acc.Name);
            if (string.IsNullOrWhiteSpace(name)) return;

            acc.Name = name.Replace(";", ",");
            RefreshButton(acc);
            SaveSettings();
            UpdateTitle();
        }

        /// <summary>Mute or unmute every account at once (from the Menu or the tray). Kept in sync in both menus.</summary>
        private void SetGlobalSilent(bool on)
        {
            globalSilent = on;
            if (silentMenuItem != null) silentMenuItem.Checked = on;
            if (silentTrayItem != null) silentTrayItem.Checked = on;
            ApplyBackgroundState();
            UpdateTitle(); // refresh the tray icon (moon badge) and tooltip
            settings.Set("GlobalSilent", on ? "1" : "0");
            settings.Save(AppPaths.SettingsFile);
        }

        private void SetSilent(Account acc, bool on)
        {
            acc.Silent = on;
            RefreshButton(acc);
            ApplyBackgroundState();
            SaveSettings();
        }

        private async Task RemoveAccountAsync(Account acc)
        {
            var answer = MessageBox.Show(this,
                "Remove '" + acc.Name + "'?\n\n" +
                "This logs it out of Messenger Hub and deletes its data.\n\n" +
                "Tip: also remove this device in the app on your phone\n" +
                "(WhatsApp: Linked devices, Telegram: Devices, Facebook: Where you're logged in).",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            await RemoveAccountCore(acc);
        }

        private void SaveSettings()
        {
            settings.Set("Accounts", string.Join(";", accounts.Select(a => a.Id)));
            foreach (var a in accounts)
            {
                settings.Set("Name." + a.Id, a.Name);
                settings.Set("Type." + a.Id, a.Service.Key);
                settings.Set("Silent." + a.Id, a.Silent ? "1" : "0");
                if (!string.IsNullOrEmpty(a.Identity)) settings.Set("Id." + a.Id, a.Identity);
            }
            settings.Save(AppPaths.SettingsFile);
        }

        // ------------------------------------------------------------------ Unread counts & notifications

        // Messengers put the unread count in the page title, e.g. "(3) WhatsApp".
        private static readonly Regex UnreadPattern = new Regex(@"\((\d+)\+?\)");

        /// <summary>
        /// For services whose tab title doesn't carry the unread count (e.g. Telegram), read it from
        /// the page. Runs for background accounts too, so their sidebar badge stays current.
        /// </summary>
        private async Task UnreadPollAsync()
        {
            foreach (var acc in accounts.ToList())
            {
                if (acc.View.IsDisposed) continue;
                var script = acc.Service.UnreadScript;
                if (script == null) continue;
                var core = acc.View.CoreWebView2;
                if (core == null) continue;
                try
                {
                    string r = await core.ExecuteScriptAsync(script);
                    if (int.TryParse((r ?? "").Trim('"'), out int n) && n >= 0 && n != acc.Unread)
                    {
                        acc.Unread = n;
                        RefreshButton(acc);
                        UpdateTitle();
                    }
                }
                catch { /* page not ready / account removed */ }
            }
        }

        private void UpdateUnread(Account acc, string title)
        {
            // Services with a DOM-based unread reader are driven by UnreadPollAsync, not the tab title.
            if (acc.Service.UnreadScript != null) return;

            var m = UnreadPattern.Match(title ?? "");
            acc.Unread = m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
            RefreshButton(acc);
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            int total = accounts.Sum(a => a.Unread);
            string count = total > 0 ? " (" + total + ")" : "";
            // Title mirrors the icon's hover tooltip: "Name (Service)"; app name only when no account.
            string who = active != null ? active.Name + " (" + active.Service.Name + ")" : AppName;
            Text = who + count;

            string silentSuffix = globalSilent ? " - silent" : "";
            string trayText = AppName + (total > 0 ? " - " + total + " unread" : "") + silentSuffix;
            tray.Text = trayText.Length > 63 ? trayText.Substring(0, 63) : trayText;

            UpdateTrayIcon(total > 0);
        }

        // Picks the tray icon for the current state: red dot if unread, crescent moon if globally silent.
        private void UpdateTrayIcon(bool unread)
        {
            int idx = (unread ? 1 : 0) | (globalSilent ? 2 : 0);
            if (idx == trayIconIndex || trayIcons[idx] == null) return;
            trayIconIndex = idx;
            tray.Icon = trayIcons[idx];
        }

        // Notifications are left to each messenger and the browser engine (native, with preview and
        // its own per-site on/off), so the app no longer intercepts or shows them itself.

        // ------------------------------------------------------------------ Tray / closing

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            // The X button hides to the tray instead of exiting.
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (!resetting)
            {
                settings.CaptureFrom(this);
                SaveSettings();
            }
            tray.Visible = false;
        }

        private void HideToTray()
        {
            settings.CaptureFrom(this);
            SaveSettings();
            Hide();

            if (settings.Get("TrayHintShown") != "1")
            {
                tray.ShowBalloonTip(4000, AppName,
                    "Still running here. Right-click this icon and choose Exit to close it.", ToolTipIcon.Info);
                settings.Set("TrayHintShown", "1");
                settings.Save(AppPaths.SettingsFile);
            }
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ExitApp()
        {
            reallyExit = true;
            Close();
        }

        // ------------------------------------------------------------------ Clear cache / reset

        private async Task ClearCacheAsync()
        {
            long before = FolderSize(AppPaths.Data);

            if (!Dialogs.ConfirmClearCache(this, FormatSize(before)))
                return;

            UseWaitCursor = true;
            try
            {
                // Cached media lives in the HTTP cache and Cache Storage.
                // Logins live in IndexedDB/cookies, which we don't touch.
                var kinds = CoreWebView2BrowsingDataKinds.DiskCache |
                            CoreWebView2BrowsingDataKinds.CacheStorage |
                            CoreWebView2BrowsingDataKinds.DownloadHistory;

                foreach (var acc in accounts.ToList())
                {
                    var core = acc.View.CoreWebView2;
                    if (core == null) continue;
                    try { await core.Profile.ClearBrowsingDataAsync(kinds); } catch { }
                }

                // Cache was wiped, so re-measure each account's limit after the next warm-up.
                foreach (var acc in accounts)
                {
                    acc.NeedsCalibration = true;
                    settings.Remove("CacheLimit." + acc.Id);
                    security?.SetCacheLimit(acc.Id, acc.Service.CacheLimitMB);
                }
                settings.Save(AppPaths.SettingsFile);

                foreach (var acc in accounts) { acc.LoadedAt = DateTime.UtcNow; acc.View.CoreWebView2?.Reload(); }

                await Task.Delay(2000); // give the browser a moment to remove files
            }
            finally
            {
                UseWaitCursor = false;
            }

            long after = FolderSize(AppPaths.Data);
            MessageBox.Show(this,
                "Cache cleared. Your logins were kept.\n\n" +
                "Data folder: " + FormatSize(before) + "  ->  " + FormatSize(after),
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ResetApp()
        {
            var answer = MessageBox.Show(this,
                "Reset Messenger Hub to defaults?\n\n" +
                "This will:\n" +
                "  - Log out and remove ALL accounts\n" +
                "  - Delete all settings and cache\n\n" +
                "The app will restart afterwards.",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            // The data folder can't be deleted while the browser engine is running,
            // so we flag the reset, restart, and the new copy deletes it on startup.
            try { File.WriteAllText(AppPaths.ResetFlag, "reset"); } catch { }
            resetting = true;

            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--reset") { UseShellExecute = false });
            }
            catch { /* the flag file finishes the reset on next manual start */ }

            ExitApp();
        }

        // ------------------------------------------------------------------ Browser events

        // Allow what a messenger needs (notifications, mic for voice notes, camera, clipboard)
        // but only for the account's own site.
        private void OnPermissionRequested(Account acc, CoreWebView2PermissionRequestedEventArgs e)
        {
            if (!acc.Service.Owns(e.Uri)) return;

            switch (e.PermissionKind)
            {
                case CoreWebView2PermissionKind.Notifications:
                case CoreWebView2PermissionKind.Microphone:
                case CoreWebView2PermissionKind.Camera:
                case CoreWebView2PermissionKind.ClipboardRead:
                    e.State = CoreWebView2PermissionState.Allow;
                    break;
            }
        }

        // New windows: same-site pop-ups (e.g. Messenger calls) open inside the app with the
        // same login; everything else (links in chats) goes to the default browser.
        private void OnNewWindowRequested(Account acc, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // A real third-party link (a concrete web URL that isn't this service or a sign-in provider)
            // opens in the default browser. Everything else from a pop-up-capable service — its own site,
            // sign-in providers, and the blank/handoff windows OAuth uses — stays in-app so SSO can finish.
            bool externalLink = IsWebUrl(e.Uri) && !acc.Service.Owns(e.Uri) && !IsAuthDomain(e.Uri);
            if (acc.Service.AllowPopups && !externalLink)
            {
                var deferral = e.GetDeferral();
                BeginInvoke((Action)(async () => await OpenPopupAsync(acc, e, deferral)));
                return;
            }

            e.Handled = true;
            OpenExternal(e.Uri);
        }

        private async Task OpenPopupAsync(Account acc, CoreWebView2NewWindowRequestedEventArgs e, CoreWebView2Deferral deferral)
        {
            var form = new Form
            {
                Text = acc.Name,
                Icon = Icon,
                Size = new Size(S(900), S(650)),
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = ContentColor,
            };
            try
            {
                var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = ContentColor };
                form.Controls.Add(view);
                form.Show();

                var options = env.CreateCoreWebView2ControllerOptions();
                options.ProfileName = acc.Id; // same login as the account
                await view.EnsureCoreWebView2Async(env, options);

                ConfigureCore(view.CoreWebView2, acc);
                // A window opened from within this popup (e.g. the OAuth consent step) follows the same
                // in-app rules, so multi-step SSO doesn't leak out to the external browser.
                view.CoreWebView2.NewWindowRequested += (s, a) => OnNewWindowRequested(acc, a);
                view.CoreWebView2.WindowCloseRequested += (s, a) => form.Close();
                view.CoreWebView2.DocumentTitleChanged += (s, a) => form.Text = view.CoreWebView2.DocumentTitle;

                e.NewWindow = view.CoreWebView2;
                e.Handled = true;
            }
            catch
            {
                e.Handled = true;
                form.Close(); // don't leave an empty window behind
            }
            finally
            {
                deferral.Complete();
            }
        }

        // Ask where to save every download (starts in the last folder you used).
        private void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            var deferral = e.GetDeferral();
            string suggested = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrEmpty(suggested)) suggested = "download";

            BeginInvoke((Action)(() =>
            {
                try
                {
                    using (var dialog = new SaveFileDialog())
                    {
                        string ext = Path.GetExtension(suggested);
                        dialog.Title = "Save file";
                        dialog.FileName = suggested;
                        dialog.Filter = string.IsNullOrEmpty(ext)
                            ? "All files (*.*)|*.*"
                            : ext.TrimStart('.').ToUpperInvariant() + " file (*" + ext + ")|*" + ext + "|All files (*.*)|*.*";
                        dialog.InitialDirectory = LastDownloadFolder();
                        dialog.OverwritePrompt = true;
                        dialog.RestoreDirectory = true;

                        if (dialog.ShowDialog(this) == DialogResult.OK)
                        {
                            e.ResultFilePath = dialog.FileName;
                            settings.Set("LastDownloadFolder", Path.GetDirectoryName(dialog.FileName));
                            settings.Save(AppPaths.SettingsFile);
                        }
                        else
                        {
                            e.Cancel = true;
                        }
                    }
                }
                catch
                {
                    e.Cancel = true;
                }
                finally
                {
                    deferral.Complete();
                }
            }));
        }

        private string LastDownloadFolder()
        {
            string last = settings.Get("LastDownloadFolder");
            if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;

            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        // ------------------------------------------------------------------ Helpers

        private static bool IsWebUrl(string uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

        // Sign-in provider domains. These are allowed to load inside any account so SSO
        // ("Sign in with Google/Apple/Microsoft/GitHub") can complete without leaving the app.
        private static readonly string[] AuthDomains =
        {
            "accounts.google.com", "accounts.youtube.com", "googleusercontent.com",
            "appleid.apple.com", "idmsa.apple.com",
            "login.microsoftonline.com", "login.microsoft.com", "login.live.com",
            "github.com",
        };

        private static bool IsAuthDomain(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
            foreach (var d in AuthDomains)
                if (u.Host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                    u.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static void OpenExternal(string uri)
        {
            if (!IsWebUrl(uri)) return; // only normal web links, never files or other schemes
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { }
        }

        private static long FolderSize(string path)
        {
            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
            return (bytes / 1024.0).ToString("0") + " KB";
        }
    }
}
