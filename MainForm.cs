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
        private const string AppName = Program.AppName;
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
        private ITaskbarList3 taskbarList;
        private string lastTaskbarBadge = "";
        private IntPtr taskbarBadgeHIcon = IntPtr.Zero;
        private bool globalSilent; // mute every account's sound at once (overrides per-account Silent)
        private ToolStripMenuItem silentMenuItem, silentTrayItem;
        private readonly ToolTip tips = new ToolTip();
        private ToolStripMenuItem accountMenu;

        private CoreWebView2Environment env;
        private Account active;
        private bool reallyExit;
        private bool resetting;
        private bool wasMinimized;
        private readonly System.Windows.Forms.Timer updateCheckTimer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
        private const uint IdleCacheCheckMs = 10u * 60u * 1000u;
        private const int IdlePollActiveMs = 30 * 1000;
        private const int IdlePollWakeMs = 1000;
        private readonly System.Windows.Forms.Timer idleMonitorTimer = new System.Windows.Forms.Timer { Interval = IdlePollActiveMs };
        private bool isSystemIdle;
        private DateTime? minimizedSinceUtc;
        private DateTime lastIdleCacheCheckUtc = DateTime.MinValue;
        private float scale; // updated when the window moves to a monitor with different scaling

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

        // Unread fallback polling: real-time unread changes are pushed immediately via MutationObserver
        // (WebMessageReceived); this slow fallback timer only reconciles state every 30-60s.
        private const int UnreadForegroundInterval = 30000;
        private const int UnreadBackgroundInterval = 60000;
        private readonly System.Windows.Forms.Timer unreadTimer = new System.Windows.Forms.Timer { Interval = UnreadForegroundInterval };

        // Drag-to-reorder state.
        private readonly Panel dropIndicator = new Panel();
        private Account dragAcc;
        private Point dragStart;
        private bool dragging;
        private int dropIndex = -1;

        private readonly System.Windows.Forms.Timer saveDebounceTimer = new System.Windows.Forms.Timer { Interval = 500 };
        private System.Windows.Forms.Timer startupPriorityTimer;

        private RegisteredWaitHandle showSignalWait;

        // ------------------------------------------------------------------ Mouse hook to close menus on click outside
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCRBUTTONDOWN = 0x00A4;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

        private const uint MSGFLT_ADD = 1;
        private static readonly uint WM_TASKBARBUTTONCREATED = RegisterWindowMessage("TaskbarButtonCreated");

        [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            [PreserveSig] int HrInit();
            [PreserveSig] int AddTab(IntPtr hwnd);
            [PreserveSig] int DeleteTab(IntPtr hwnd);
            [PreserveSig] int ActivateTab(IntPtr hwnd);
            [PreserveSig] int SetActiveAlt(IntPtr hwnd);
            [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
            [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
            [PreserveSig] int SetProgressState(IntPtr hwnd, int tbpFlags);
            [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
            [PreserveSig] int UnregisterTab(IntPtr hwndTab);
            [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
            [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
            [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
            [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
            [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        }

        [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        private class TaskbarListInstance { }

        private static uint GetSystemIdleMilliseconds()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            return GetLastInputInfo(ref info) ? unchecked(GetTickCount() - info.dwTime) : 0u;
        }

        private const int ProcessPowerThrottling = 4;
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, int ProcessInformationSize);

        private bool isEcoMode;

        private void SetEfficiencyMode(bool enable)
        {
            if (isEcoMode == enable) return;
            isEcoMode = enable;
            try
            {
                using (var p = Process.GetCurrentProcess())
                {
                    var state = new PROCESS_POWER_THROTTLING_STATE
                    {
                        Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                        ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                        StateMask = enable ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0u
                    };
                    SetProcessInformation(p.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
                }
            }
            catch { }
        }

        private static readonly string[] BlockedResourcePatterns =
        {
            "*://*.doubleclick.net/*",
            "*://*.googlesyndication.com/*",
            "*://*.google-analytics.com/*",
            "*://*.sentry.io/*",
            "*://*.bugsnag.com/*",
            "*://crashlogs.whatsapp.net/*"
        };

        private void TrimProcessMemory()
        {
            Task.Run(() =>
            {
                try
                {
                    security.TrimBrowserWorkingSets();
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                }
                catch { }
            });
        }

        private readonly LowLevelMouseProc mouseProc;
        private IntPtr mouseHook = IntPtr.Zero;
        private readonly HashSet<ToolStripDropDown> activeDropDowns = new HashSet<ToolStripDropDown>();

        private int S(float v) => (int)Math.Round(v * scale);

        public MainForm(EventWaitHandle showSignal, Security security)
        {
            mouseProc = MouseHookCallback;
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

            if (WM_TASKBARBUTTONCREATED != 0)
            {
                try { ChangeWindowMessageFilter(WM_TASKBARBUTTONCREATED, MSGFLT_ADD); } catch { }
            }

            StartStartupPriorityBoost();

            Load += async (s, e) => await StartAsync();
            Shown += (s, e) => UpdateTaskbarOverlay(accounts.Sum(a => a.Unread), true);
            FormClosing += OnFormClosing;
            FormClosed += (s, e) =>
            {
                UnhookMouse();
                showSignalWait?.Unregister(null);
                startupPriorityTimer?.Dispose();
                updateCheckTimer.Dispose();
                idleMonitorTimer.Dispose();
                identityTimer.Dispose();
                unreadTimer.Dispose();
                startupWatchdog?.Dispose();
                saveDebounceTimer.Dispose();
                tray.Dispose();
                foreach (var ic in trayIcons) ic?.Dispose();
                if (taskbarBadgeHIcon != IntPtr.Zero)
                {
                    DestroyIcon(taskbarBadgeHIcon);
                    taskbarBadgeHIcon = IntPtr.Zero;
                }
            };

            saveDebounceTimer.Tick += (s, e) =>
            {
                saveDebounceTimer.Stop();
                settings.Save(AppPaths.SettingsFile);
            };

            updateCheckTimer.Tick += (s, e) => UpdateCheckTick();
            updateCheckTimer.Start();

            idleMonitorTimer.Tick += async (s, e) => await IdleMonitorTickAsync();
            idleMonitorTimer.Start();

            identityTimer.Tick += async (s, e) =>
            {
                await AutoNameTickAsync();
                UpdateIdentityTimerState();
            };
            identityTimer.Start();

            unreadTimer.Tick += async (s, e) => await UnreadPollAsync();
            unreadTimer.Start();

            // Hidden in tray or minimized -> all accounts go to background mode.
            VisibleChanged += async (s, e) =>
            {
                ApplyBackgroundState();
                if (Visible)
                {
                    UpdateTaskbarOverlay(accounts.Sum(a => a.Unread), true);
                    await Task.Delay(250);
                    if (!IsDisposed && Visible) UpdateTaskbarOverlay(accounts.Sum(a => a.Unread), true);
                }
            };
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

        private void RegisterDropDown(ToolStripDropDown dropDown)
        {
            if (dropDown == null) return;
            dropDown.Opened += (s, e) =>
            {
                activeDropDowns.Add(dropDown);
                EnsureMouseHook();
            };
            dropDown.Closed += (s, e) =>
            {
                activeDropDowns.Remove(dropDown);
                if (activeDropDowns.Count == 0) UnhookMouse();
            };
        }

        private void EnsureMouseHook()
        {
            if (mouseHook != IntPtr.Zero) return;
            try
            {
                using (var curProcess = Process.GetCurrentProcess())
                using (var curModule = curProcess.MainModule)
                {
                    mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
            catch { }
        }

        private void UnhookMouse()
        {
            if (mouseHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(mouseHook); } catch { }
                mouseHook = IntPtr.Zero;
            }
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_NCLBUTTONDOWN || msg == WM_NCRBUTTONDOWN)
                {
                    var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    var pt = new Point(info.pt.x, info.pt.y);

                    bool inside = false;
                    foreach (var d in activeDropDowns)
                    {
                        if (d != null && d.Visible && d.Bounds.Contains(pt))
                        {
                            inside = true;
                            break;
                        }
                    }

                    if (!inside && menu.Visible && menu.RectangleToScreen(menu.ClientRectangle).Contains(pt))
                    {
                        inside = true;
                    }

                    if (!inside)
                    {
                        try { BeginInvoke((Action)CloseAllMenus); } catch { }
                    }
                }
            }
            return CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        private void CloseAllMenus()
        {
            foreach (ToolStripItem item in menu.Items)
            {
                if (item is ToolStripDropDownItem dropDownItem && dropDownItem.DropDown.Visible)
                {
                    dropDownItem.DropDown.Close();
                }
            }

            if (addMenu.Visible) addMenu.Close();

            foreach (var a in accounts)
            {
                if (a.Button?.ContextMenuStrip != null && a.Button.ContextMenuStrip.Visible)
                {
                    a.Button.ContextMenuStrip.Close();
                }
            }
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
                AppName + " " + Application.ProductVersion + "\n\nPortable mode: all data is stored in\n" + AppPaths.Data +
                "\n\nSecure DNS (strict): " + (secureDnsInUse ?? "starting...") + "\n(fastest first; re-checked daily, applied at next start)",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information));

            // Shows each account's current cache size, measured fresh each time it's opened.
            var size = new ToolStripMenuItem("&Size");
            size.DropDownOpening += (s, e) => ReplaceItems(size.DropDownItems, CacheSizeItems());
            size.DropDownItems.Add("(size)"); // placeholder: a menu needs one item to open

            menu.Items.Add(app);
            menu.Items.Add(accountMenu);
            menu.Items.Add(help);
            menu.Items.Add(size);
            menu.Dock = DockStyle.Top;

            foreach (ToolStripItem item in menu.Items)
            {
                if (item is ToolStripDropDownItem ddi) RegisterDropDown(ddi.DropDown);
            }
            RegisterDropDown(add.DropDown);
        }

        /// <summary>
        /// One line per account (in sidebar order): "Name    1,234 MB". Cache size is measured on a
        /// background thread each time the Size menu opens so the UI never freezes.
        /// </summary>
        private ToolStripItem[] CacheSizeItems()
        {
            if (accounts.Count == 0)
                return new ToolStripItem[] { new ToolStripMenuItem("(no accounts)") { Enabled = false } };

            var snapshot = accounts.Select(a => new
            {
                Id = a.Id,
                Name = a.Name,
                Item = new ToolStripMenuItem(a.Name + "    ...")
            }).ToList();

            Task.Run(() =>
            {
                foreach (var entry in snapshot)
                {
                    long mb = security.MeasureProfileCache(entry.Id) / (1024L * 1024L);
                    string text = entry.Name + "    " +
                        mb.ToString("#,##0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
                    try
                    {
                        if (IsHandleCreated && !IsDisposed)
                        {
                            BeginInvoke((Action)(() =>
                            {
                                if (!entry.Item.IsDisposed) entry.Item.Text = text;
                            }));
                        }
                    }
                    catch { }
                }
            });

            return snapshot.Select(x => (ToolStripItem)x.Item).ToArray();
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
            RegisterDropDown(addMenu);

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

        /// <summary>One menu item per supported service (used by "+" and Menu > Add account).</summary>
        private void AddServiceItems(ToolStripItemCollection items)
        {
            foreach (var service in ServiceType.All)
            {
                string label = service == ServiceType.Custom ? "Custom..." : service.Name;
                items.Add(label, service.MenuIcon(S(16)), async (s, e) => await AddAccountAsync(service));
            }
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
            emptyLabel.Text = "No accounts yet.\nClick + on the left to add an account.";
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
            // First run: one WhatsApp account. Its id "whatsapp" keeps the login from version 0.1.
            string saved = settings.Get("Accounts");
            var ids = saved == null
                ? new List<string> { "whatsapp" }
                : saved.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            foreach (var id in ids)
            {
                string rawType = settings.Get("Type." + id, ServiceType.WhatsApp.Key);
                var service = ServiceType.ByKey(rawType);
                string customUrl = null;
                bool showBar = false;
                if (service == ServiceType.Custom)
                {
                    customUrl = Account.NormalizeUrl(settings.Get("Url." + id))
                        ?? (string.Equals(rawType, "wechat", StringComparison.OrdinalIgnoreCase)
                            ? "https://web.wechat.com/"
                            : ServiceType.Custom.Url);
                    showBar = settings.Get("Bar." + id) == "1";
                }
                string defaultName = service == ServiceType.Custom ? NextCustomName(customUrl) : NextName(service);
                var acc = CreateAccount(id, service, settings.Get("Name." + id) ?? defaultName, customUrl, showBar);
                acc.Silent = settings.Get("Silent." + id) == "1";
                acc.Identity = settings.Get("Id." + id); // known identity, for duplicate detection
                RefreshButton(acc);
            }
            SaveSettings();

            string last = settings.Get("ActiveAccount");
            var initial = accounts.FirstOrDefault(a => a.Id == last) ?? accounts.FirstOrDefault();
            SelectAccount(initial);

            // Unlock the active account's profile on a background thread in parallel with WebView2 engine startup.
            var initialUnlockTask = initial != null
                ? security.UnlockProfileAsync(initial.Id)
                : Task.CompletedTask;

            try
            {
                env = await CreateEnvironmentAsync();
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

            // Initialize the active account first, then automatically initialize ALL remaining accounts
            // with a 500ms stagger so every profile connects cleanly without CPU or network starvation at startup.
            var remaining = accounts.Where(a => a != initial).ToList();
            if (initial != null)
            {
                await initialUnlockTask;
                // Begin unlocking all remaining profiles in parallel in the background.
                foreach (var bg in remaining) _ = security.UnlockProfileAsync(bg.Id);
                await InitAccountAsync(initial);
                await Task.Delay(500);
            }

            foreach (var acc in remaining)
            {
                await Task.Delay(500);
                if (IsDisposed) return;
                await InitAccountAsync(acc);
            }
            UpdateIdentityTimerState();
            UpdateUnreadTimerState();

            // Some accounts occasionally hang on their own loading screen when many load at
            // once; reload any that are still stuck a short time after startup (once each).
            StartStartupWatchdog();

            // Check for a new version (weekly) and time the secure-DNS servers (daily), in the background.
            Updater.StartBackgroundCheck();
            CheckSecureDns();

            // Compact managed heap and release startup working-set pages back to the OS.
            TrimProcessMemory();
        }

        /// <summary>One shared browser engine with background optimization flags; each account gets its own profile.</summary>
        private string secureDnsInUse; // shown in Help > About

        private Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            secureDnsInUse = string.Join(", ", SecureDns.CurrentOrder(settings));
            SecureDns.ApplyToLocalState(settings); // the browser's own secure-DNS setting

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = SecureDns.BrowserArgument(settings) + " " +
                                             "--disable-features=Translate,OptimizationHints,MediaRouter,SpareRendererForSitePerProcess,BackForwardCache,InterestFeedContentSuggestions,AutofillServerCommunication,CalculateNativeWinOcclusion,MemorySaverModeRenderTuning " +
                                             "--disable-background-timer-throttling " +
                                             "--disable-sync " +
                                             "--disable-component-update " +
                                             "--disable-domain-reliability " +
                                             "--disable-gpu-shader-disk-cache " +
                                             "--js-flags=\"--optimize-for-size\""
            };
            return CoreWebView2Environment.CreateAsync(null, AppPaths.WebView, options);
        }

        // ------------------------------------------------------------------ Browser engine recovery

        private bool recoveringBrowser;
        private DateTime lastBrowserRecoveryUtc = DateTime.MinValue;

        /// <summary>
        /// The whole browser engine stopped (every account goes blank). Start a new one and reload every
        /// account in place, without asking for the password again. Only once per 5 minutes, so it can't loop.
        /// </summary>
        private void OnBrowserProcessExited()
        {
            if (recoveringBrowser || reallyExit || resetting || IsDisposed) return;
            recoveringBrowser = true;

            if (DateTime.UtcNow - lastBrowserRecoveryUtc < TimeSpan.FromMinutes(5))
            {
                Diag.Write("browser engine stopped again within 5 minutes; not restarting it");
                MessageBox.Show(this,
                    "The browser engine inside Messenger Hub stopped twice in a few minutes.\n\n" +
                    "Please close Messenger Hub (Menu > Exit) and open it again.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return; // stays "recovering" so the message isn't repeated
            }

            lastBrowserRecoveryUtc = DateTime.UtcNow;
            BeginInvoke((Action)(async () => await RecoverBrowserAsync()));
        }

        private async Task RecoverBrowserAsync()
        {
            Diag.Write("browser engine stopped; starting a new one");
            try
            {
                env = null;
                foreach (var acc in accounts) ReplaceView(acc);
                await Task.Delay(1500); // let the old engine finish exiting
                if (IsDisposed || reallyExit) return;

                env = await CreateEnvironmentAsync();
                var first = active;
                if (first != null) await InitAccountAsync(first);
                foreach (var acc in accounts.ToList())
                {
                    if (acc == first) continue;
                    await Task.Delay(500);
                    if (IsDisposed || reallyExit) return;
                    await InitAccountAsync(acc);
                }
                StartStartupWatchdog();
                UpdateIdentityTimerState();
                UpdateUnreadTimerState();
            }
            catch (Exception ex)
            {
                Diag.Write("browser engine restart failed: " + ex.Message);
            }
            finally
            {
                recoveringBrowser = false;
            }
        }

        /// <summary>Swaps an account's dead browser view for a fresh one in the same place.</summary>
        private void ReplaceView(Account acc)
        {
            var old = acc.View;
            var view = NewWebView(old.Visible);
            var parent = old.Parent;
            if (parent != null)
            {
                int index = parent.Controls.GetChildIndex(old);
                parent.Controls.Remove(old);
                parent.Controls.Add(view);
                parent.Controls.SetChildIndex(view, index);
            }
            acc.View = view;
            acc.ResetForNewBrowser();
            try { old.Dispose(); } catch { }
        }

        private static WebView2 NewWebView(bool visible) => new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = ContentColor,
            Visible = visible,
        };

        // ------------------------------------------------------------------ Startup load recovery

        private const int MaxStartupReloads = 3;
        private System.Windows.Forms.Timer startupWatchdog;
        private int startupPasses;

        /// <summary>
        /// After startup, reload any account that never finished loading, and any account whose
        /// page didn't come up usable (e.g. blank dark screen or broken client initialization).
        /// Runs 4 passes (~15s, ~30s, ~45s, ~60s) covering all active and background accounts.
        /// </summary>
        private void StartStartupWatchdog()
        {
            startupWatchdog?.Dispose();
            startupPasses = 0;
            startupWatchdog = new System.Windows.Forms.Timer { Interval = 15000 };
            startupWatchdog.Tick += async (s, e) =>
            {
                if (++startupPasses >= 4) startupWatchdog.Stop();
                foreach (var acc in accounts.ToList()) await CheckStartupLoad(acc);
            };
            startupWatchdog.Start();
        }

        private async Task CheckStartupLoad(Account acc)
        {
            if (acc.View.IsDisposed || acc.StartupReloads >= MaxStartupReloads) return;
            if (!acc.InitStarted)
            {
                // Its browser failed to start earlier: try again.
                acc.StartupReloads++;
                Diag.Write("startup check: " + acc.Name + " never started, retrying (" + acc.StartupReloads + ")");
                ReloadOrOpen(acc);
                return;
            }
            var core = acc.View.CoreWebView2;
            if (core == null) return;

            // If initial navigation is still running in background, give it time to finish.
            if (!acc.FirstNavDone.Task.IsCompleted) return;
            if (DateTime.UtcNow - acc.LoadedAt < TimeSpan.FromSeconds(10)) return;

            bool stuck = !acc.FirstLoadOk || !await IsReady(acc);

            if (stuck)
            {
                acc.StartupReloads++;
                acc.LastRecoverUtc = DateTime.UtcNow;
                Diag.Write("startup check: " + acc.Name + " not usable, reloading (" + acc.StartupReloads + ")");
                ReloadOrOpen(acc);
            }
        }

        /// <summary>
        /// Is the account's page actually usable (not a half-loaded/stuck screen)? Uses the service's
        /// ReadyScript when it has one (e.g. WhatsApp: chat list present), else falls back to "has text".
        /// </summary>
        private static async Task<bool> IsReady(Account acc)
        {
            var core = acc.View.CoreWebView2;
            if (core == null) return false;

            string ready = acc.Service.ReadyScript;
            if (ready != null)
            {
                try { string r = await core.ExecuteScriptAsync(ready); return (r ?? "").Trim() == "true"; }
                catch { return false; }
            }

            // Fallback: a page with almost no text is probably stuck on a loading screen.
            try
            {
                string r = await core.ExecuteScriptAsync(
                    "(function(){try{var b=document.body;return b?b.innerText.replace(/\\s+/g,'').length:0;}catch(e){return -1;}})()");
                return !(int.TryParse(r, out int n) && n >= 0 && n < 25);
            }
            catch { return true; } // unknown: assume ok, don't reload
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

                case Updater.CheckResult.Busy:
                    MessageBox.Show(this, "An update check is already running. Try again in a moment.",
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

        private string NextCustomName(string url)
        {
            string baseName = Account.DefaultNameFromUrl(url);
            if (!accounts.Any(a => string.Equals(a.Name, baseName, StringComparison.OrdinalIgnoreCase)))
                return baseName;
            int n = 2;
            while (accounts.Any(a => string.Equals(a.Name, baseName + " " + n, StringComparison.OrdinalIgnoreCase))) n++;
            return baseName + " " + n;
        }

        private Account CreateAccount(string id, ServiceType service, string name, string customUrl = null, bool showAddressBar = false)
        {
            var acc = new Account
            {
                Id = id,
                Name = name,
                Service = service,
                CustomUrl = customUrl,
                ShowAddressBar = showAddressBar,
            };

            acc.View = NewWebView(acc.IsCustom);

            if (acc.IsCustom)
            {
                acc.Container = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = ContentColor,
                    Visible = false,
                };
                acc.BrowserBar = BuildBrowserBar(acc);
                acc.BrowserBar.Visible = acc.ShowAddressBar;
                // Docking order: Fill first, Top second so WebView2 fills the area below the bar.
                acc.Container.Controls.Add(acc.View);
                acc.Container.Controls.Add(acc.BrowserBar);
            }
            content.Controls.Add(acc.HostControl);

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
            RegisterDropDown(ctx);
            acc.Button.ContextMenuStrip = ctx;

            // Keep the + button last.
            accountList.Controls.Add(acc.Button);
            accountList.Controls.SetChildIndex(addButton, accountList.Controls.Count - 1);

            accounts.Add(acc);
            RefreshButton(acc);
            return acc;
        }

        private static readonly Font NavButtonFont = new Font("Segoe UI Symbol", 9.5f);
        private static readonly Font AddressFont = new Font("Segoe UI", 9.5f);

        private Panel BuildBrowserBar(Account acc)
        {
            var bar = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(36),
                BackColor = SidebarColor,
                Padding = new Padding(S(6), S(4), S(6), S(5)),
            };
            bar.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(46, 60, 69)))
                    e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
            };

            Button MakeNavButton(string glyph, string tooltip, EventHandler onClick)
            {
                var btn = new Button
                {
                    Text = glyph,
                    FlatStyle = FlatStyle.Flat,
                    Size = new Size(S(26), S(26)),
                    Margin = new Padding(0, 0, S(2), 0),
                    BackColor = SidebarColor,
                    ForeColor = Color.FromArgb(215, 225, 230),
                    Font = NavButtonFont,
                    TabStop = false,
                    Cursor = Cursors.Hand,
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 64, 74);
                btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(58, 76, 88);
                btn.Click += onClick;
                tips.SetToolTip(btn, tooltip);
                return btn;
            }

            acc.BackButton = MakeNavButton("\u2190", "Back", (s, e) =>
            {
                try { if (acc.View.CoreWebView2?.CanGoBack == true) acc.View.CoreWebView2.GoBack(); } catch { }
            });
            acc.BackButton.Enabled = false;

            acc.ForwardButton = MakeNavButton("\u2192", "Forward", (s, e) =>
            {
                try { if (acc.View.CoreWebView2?.CanGoForward == true) acc.View.CoreWebView2.GoForward(); } catch { }
            });
            acc.ForwardButton.Enabled = false;

            var reloadBtn = MakeNavButton("\u21BB", "Reload", (s, e) => ReloadOrOpen(acc));

            var homeBtn = MakeNavButton("\u2302", "Go to starting URL", (s, e) =>
            {
                try { acc.View.CoreWebView2?.Navigate(acc.StartUrl); } catch { }
            });

            var urlBorder = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ContentColor,
                Margin = new Padding(S(4), S(1), S(4), S(1)),
                Padding = new Padding(S(7), S(3), S(7), S(2)),
            };
            bool urlFocused = false;
            urlBorder.Paint += (s, e) =>
            {
                var c = urlFocused ? Color.FromArgb(85, 140, 180) : Color.FromArgb(55, 72, 82);
                using (var pen = new Pen(c))
                    e.Graphics.DrawRectangle(pen, 0, 0, urlBorder.Width - 1, urlBorder.Height - 1);
            };

            acc.AddressBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                BackColor = ContentColor,
                ForeColor = Color.FromArgb(225, 235, 240),
                Font = AddressFont,
                Text = acc.StartUrl,
            };
            acc.AddressBox.GotFocus += (s, e) => { urlFocused = true; urlBorder.Invalidate(); };
            acc.AddressBox.LostFocus += (s, e) => { urlFocused = false; urlBorder.Invalidate(); SyncBrowserBar(acc); };
            acc.AddressBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    NavigateCustomAddress(acc, acc.AddressBox.Text);
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    SyncBrowserBar(acc);
                    try { acc.View.Focus(); } catch { }
                }
            };
            urlBorder.Controls.Add(acc.AddressBox);

            var hideBtn = MakeNavButton("\u2715", "Hide address bar (toggle in right-click menu)", (s, e) =>
                SetAddressBarVisible(acc, false));
            hideBtn.Margin = Padding.Empty;

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 6,
                RowCount = 1,
                BackColor = SidebarColor,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            grid.Controls.Add(acc.BackButton, 0, 0);
            grid.Controls.Add(acc.ForwardButton, 1, 0);
            grid.Controls.Add(reloadBtn, 2, 0);
            grid.Controls.Add(homeBtn, 3, 0);
            grid.Controls.Add(urlBorder, 4, 0);
            grid.Controls.Add(hideBtn, 5, 0);

            bar.Controls.Add(grid);
            return bar;
        }

        private static void SyncBrowserBar(Account acc)
        {
            if (acc == null || !acc.IsCustom || acc.BrowserBar == null) return;
            var core = acc.View?.CoreWebView2;
            if (acc.BackButton != null) acc.BackButton.Enabled = core != null && core.CanGoBack;
            if (acc.ForwardButton != null) acc.ForwardButton.Enabled = core != null && core.CanGoForward;
            if (acc.AddressBox != null && !acc.AddressBox.Focused)
            {
                string src = core?.Source;
                acc.AddressBox.Text = !string.IsNullOrEmpty(src) && IsWebUrl(src) ? src : acc.StartUrl;
            }
        }

        private void SetAddressBarVisible(Account acc, bool visible)
        {
            if (acc == null || !acc.IsCustom) return;
            acc.ShowAddressBar = visible;
            if (acc.BrowserBar != null) acc.BrowserBar.Visible = visible;
            SyncBrowserBar(acc);
            SaveSettings();
        }

        private void NavigateCustomAddress(Account acc, string rawInput)
        {
            string url = Account.NormalizeUrl(rawInput);
            if (url == null)
            {
                MessageBox.Show(this, Dialogs.InvalidUrlMessage,
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                acc.AddressBox?.Focus();
                return;
            }

            if (!acc.OwnsUrl(url))
            {
                acc.CustomUrl = url;
                ResetCustomIcon(acc);
                RefreshButton(acc);
                SaveSettings();
            }

            if (acc.AddressBox != null) acc.AddressBox.Text = url;
            try { acc.View.CoreWebView2?.Navigate(url); } catch { }
            try { acc.View.Focus(); } catch { }
        }

        private void ChangeCustomUrl(Account acc)
        {
            if (acc == null || !acc.IsCustom) return;
            string url = Dialogs.PromptCustomUrl(this, acc.StartUrl);
            if (url == null) return;

            bool domainChanged = !acc.OwnsUrl(url);
            acc.CustomUrl = url;
            if (domainChanged) ResetCustomIcon(acc);
            RefreshButton(acc);
            SaveSettings();

            if (acc.AddressBox != null) acc.AddressBox.Text = url;
            try { acc.View.CoreWebView2?.Navigate(url); } catch { }
        }

        private ToolStripItem[] AccountMenuItems(Account acc)
        {
            var silent = new ToolStripMenuItem("&Silent (no sound)") { Checked = acc.Silent };
            silent.Click += (s, e) => SetSilent(acc, !acc.Silent);

            var rename = new ToolStripMenuItem("&Rename...");
            rename.Click += (s, e) => RenameAccount(acc);

            var reload = new ToolStripMenuItem("Re&load");
            reload.Click += (s, e) => ReloadOrOpen(acc);

            var remove = new ToolStripMenuItem("Re&move...");
            remove.Click += async (s, e) => await RemoveAccountAsync(acc);

            if (acc.IsCustom)
            {
                var showBar = new ToolStripMenuItem("Show &address bar") { Checked = acc.ShowAddressBar };
                showBar.Click += (s, e) => SetAddressBarVisible(acc, !acc.ShowAddressBar);

                var changeUrl = new ToolStripMenuItem("Change &URL...");
                changeUrl.Click += (s, e) => ChangeCustomUrl(acc);

                return new ToolStripItem[]
                {
                    silent, showBar, new ToolStripSeparator(),
                    changeUrl, rename, reload, new ToolStripSeparator(),
                    remove,
                };
            }

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
            acc.Button.SiteIcon = AccountIcon(acc);
            acc.Button.Invalidate();
            string tip = acc.TooltipText();
            if (acc.LastTooltip != tip)
            {
                acc.LastTooltip = tip;
                tips.SetToolTip(acc.Button, tip);
            }
        }

        // ------------------------------------------------------------------ Drag to reorder

        private void OnDragMove(Account acc, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || dragAcc != acc || accounts.Count == 0) return;

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
            int count = accounts.Count;
            int y = accountList.PointToClient(Cursor.Position).Y;
            dropIndex = count;
            for (int i = 0; i < count; i++)
            {
                var btn = accounts[i].Button;
                if (y < btn.Top + btn.Height / 2) { dropIndex = i; break; }
            }

            int lineY = dropIndex < count ? accounts[dropIndex].Button.Top : accounts[count - 1].Button.Bottom;
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
        private static bool HasDefaultName(Account acc)
        {
            if (string.IsNullOrEmpty(acc.Name) || acc.Service == null) return false;
            string prefix = acc.Service.Name + " ";
            if (!acc.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string numPart = acc.Name.Substring(prefix.Length);
            return numPart.Length > 0 && numPart.All(char.IsDigit);
        }

        private void UpdateIdentityTimerState()
        {
            bool anyNeedsWork = accounts.Any(a =>
                !a.View.IsDisposed && a.InitStarted &&
                string.IsNullOrEmpty(a.Identity) && !string.IsNullOrEmpty(a.Service.IdentityScript));

            if (anyNeedsWork)
            {
                if (!identityTimer.Enabled) identityTimer.Start();
            }
            else
            {
                if (identityTimer.Enabled) identityTimer.Stop();
            }
        }

        /// <summary>
        /// After an account logs in, read who it is (phone number for WhatsApp/Telegram, profile name for
        /// Messenger). This is used to auto-name the account and to block adding the SAME account twice.
        /// Accounts you renamed keep their name; the detected identity is still recorded for the duplicate check.
        /// Google Messages, Slack, LinkedIn and custom sites don't expose one, so duplicates can't be detected for them.
        /// </summary>
        private async Task AutoNameTickAsync()
        {
            foreach (var acc in accounts.ToList())
            {
                if (!acc.InitStarted || acc.View.IsDisposed) continue;   // not started or removed meanwhile
                if (!string.IsNullOrEmpty(acc.Identity)) continue;        // already identified
                if (DateTime.UtcNow < acc.IdentityNextUtc) continue;      // backing off while logged out
                if (string.IsNullOrEmpty(acc.Service.IdentityScript)) continue; // service has no identity
                var core = acc.View.CoreWebView2;
                if (core == null) continue;
                if (!identityBusy.Add(acc)) continue;

                try
                {
                    string found = DecodeJsonString(await core.ExecuteScriptAsync(acc.Service.IdentityScript));
                    found = (found ?? "").Replace(";", ",").Replace("\r", " ").Replace("\n", " ").Trim();
                    if (found.Length > 40) found = found.Substring(0, 40).Trim();
                    if (!accounts.Contains(acc)) continue;
                    if (found.Length == 0)
                    {
                        // Not logged in yet: check again later, backing off up to one minute.
                        acc.IdentityMisses++;
                        acc.IdentityNextUtc = DateTime.UtcNow.AddSeconds(Math.Min(60, 10 * Math.Pow(2, Math.Min(acc.IdentityMisses, 3))));
                        continue;
                    }

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

        private static string IconKey(Account acc) => acc.IsCustom ? "custom_" + acc.Id : acc.Service.Key;
        private static string IconFile(Account acc) => Path.Combine(AppPaths.Icons, IconKey(acc) + ".png");

        /// <summary>
        /// The saved site icon for an account (shared per service for standard messengers, or per-tab for Custom),
        /// or null (then the colored letter circle is shown).
        /// The file is read once; "no icon yet" is remembered too, so the disk isn't checked on every title change.
        /// </summary>
        private Image AccountIcon(Account acc)
        {
            string key = IconKey(acc);
            if (serviceIcons.TryGetValue(key, out var cached)) return cached;
            Image icon = null;
            try
            {
                string file = IconFile(acc);
                if (File.Exists(file)) icon = DecodeImage(File.ReadAllBytes(file));
            }
            catch { }
            serviceIcons[key] = icon;
            return icon;
        }

        private void ResetCustomIcon(Account acc)
        {
            if (acc == null || !acc.IsCustom) return;
            string key = IconKey(acc);
            if (acc.Button != null) acc.Button.SiteIcon = null;
            if (serviceIcons.TryGetValue(key, out var old))
            {
                serviceIcons.Remove(key);
                old?.Dispose();
            }
            try
            {
                string file = IconFile(acc);
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }
        }

        /// <summary>
        /// Saves the site's favicon (the icon on its browser tab) the first time we see it.
        /// It's only saved while there are no unread messages, so an unread dot never gets stuck on it.
        /// For Custom tabs, also probes DOM <link rel="icon"> tags (including SVG/apple-touch-icon) and
        /// the host / base-domain favicon endpoints, never saving Chromium's default empty-URI globe icon.
        /// </summary>
        private async Task CaptureIconAsync(Account acc)
        {
            var svc = acc.Service;
            var core = acc.View.CoreWebView2;
            if (core == null || acc.Unread > 0 || AccountIcon(acc) != null) return;
            if (!acc.IsCustom && svc.IconPage != null && (core.Source ?? "").IndexOf(svc.IconPage, StringComparison.OrdinalIgnoreCase) < 0) return;
            string key = IconKey(acc);
            if (!capturingIcons.Add(key)) return;

            try
            {
                Image best = null;
                bool onOwnSite = !acc.IsCustom || acc.OwnsUrl(core.Source);

                if (onOwnSite && !string.IsNullOrEmpty(core.FaviconUri))
                {
                    // Best quality: download the favicon file itself. Fallback: the image WebView2 shows on the tab
                    // (only when FaviconUri is non-empty, otherwise WebView2 returns its default grey globe).
                    best = await DownloadImageAsync(core.FaviconUri);
                    Image shown = await GetFaviconImageAsync(core);
                    best = PickLargerImage(best, shown);
                }

                if (acc.IsCustom && (best == null || best.Width < 32))
                {
                    if (onOwnSite)
                    {
                        Image domIcon = await TryExtractDomFaviconAsync(core);
                        best = PickLargerImage(best, domIcon);
                    }
                    if (best == null || best.Width < 32)
                    {
                        Image domainIcon = await TryFetchDomainFaviconAsync(acc, onOwnSite ? core.Source : null);
                        best = PickLargerImage(best, domainIcon);
                    }
                }

                if (best == null || acc.Unread > 0) { best?.Dispose(); return; }

                best.Save(IconFile(acc), ImageFormat.Png);
                serviceIcons[key] = best;
                if (acc.IsCustom)
                {
                    RefreshButton(acc);
                }
                else
                {
                    foreach (var other in accounts.Where(x => !x.IsCustom && x.Service == svc)) RefreshButton(other);
                }
            }
            catch { }
            finally
            {
                capturingIcons.Remove(key);
            }
        }

        private static Image PickLargerImage(Image a, Image b)
        {
            if (b == null) return a;
            if (a == null) return b;
            if (b.Width > a.Width) { a.Dispose(); return b; }
            b.Dispose();
            return a;
        }

        private const string DomFaviconEvalJson =
            "{\"expression\":\"(async()=>{try{const links=Array.from(document.querySelectorAll('link[rel]'))" +
            ".filter(l=>/(^|[\\\\s-])icon($|[\\\\s-])/i.test(l.rel)&&l.href);" +
            "links.sort((a,b)=>{const sc=e=>{let v=/apple-touch-icon/i.test(e.rel)?500:0;" +
            "const m=/(\\\\d+)x\\\\d+/i.exec(e.getAttribute('sizes')||'');if(m)v+=parseInt(m[1],10);" +
            "if(/\\\\.svg(\\\\?|$)/i.test(e.href))v+=300;return v;};return sc(b)-sc(a);});" +
            "const urls=links.map(l=>l.href);if(!urls.length)urls.push(new URL('/favicon.ico',location.origin).href);" +
            "for(const u of urls){try{const d=await new Promise((res,rej)=>{const i=new Image();i.crossOrigin='anonymous';" +
            "const t=setTimeout(()=>rej(),2500);i.onload=()=>{clearTimeout(t);if(i.naturalWidth<8||i.naturalHeight<8){rej();return;}" +
            "const s=Math.min(128,Math.max(64,i.naturalWidth)),c=document.createElement('canvas');c.width=s;c.height=s;" +
            "c.getContext('2d').drawImage(i,0,0,s,s);res(c.toDataURL('image/png'));};" +
            "i.onerror=()=>{clearTimeout(t);rej();};i.src=u;});if(d)return d;}catch(e){}}" +
            "return 'URLS:'+urls.slice(0,4).join('|');}catch(e){return '';}})()\"," +
            "\"awaitPromise\":true,\"returnByValue\":true}";

        private static async Task<Image> TryExtractDomFaviconAsync(CoreWebView2 core)
        {
            try
            {
                string cdpJson = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", DomFaviconEvalJson);
                var m = Regex.Match(cdpJson ?? "", "\"value\"\\s*:\\s*(\"[^\"]*\")");
                if (!m.Success) return null;
                string val = DecodeJsonString(m.Groups[1].Value);
                if (string.IsNullOrEmpty(val)) return null;
                if (val.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase))
                    return await DownloadImageAsync(val);
                if (val.StartsWith("URLS:", StringComparison.Ordinal))
                {
                    Image best = null;
                    foreach (var u in val.Substring(5).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        best = PickLargerImage(best, await DownloadImageAsync(u));
                        if (best != null && best.Width >= 64) break;
                    }
                    return best;
                }
            }
            catch { }
            return null;
        }

        private static async Task<Image> TryFetchDomainFaviconAsync(Account acc, string activeSourceUrl)
        {
            var hosts = new List<string>();
            void AddHost(string url)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return;
                string h = u.Host.ToLowerInvariant();
                if (!hosts.Contains(h)) hosts.Add(h);
                string baseDom = Account.GetBaseDomain(h);
                if (!string.IsNullOrEmpty(baseDom) && baseDom.Contains("."))
                {
                    if (!hosts.Contains("www." + baseDom)) hosts.Add("www." + baseDom);
                    if (!hosts.Contains(baseDom)) hosts.Add(baseDom);
                }
            }

            AddHost(activeSourceUrl);
            AddHost(acc.StartUrl);

            var candidates = new List<string>();
            foreach (var h in hosts)
            {
                candidates.Add("https://" + h + "/apple-touch-icon.png");
                candidates.Add("https://" + h + "/favicon.ico");
            }

            Image best = null;
            foreach (var url in candidates)
            {
                best = PickLargerImage(best, await DownloadImageAsync(url));
                if (best != null && best.Width >= 48) break;
            }
            return best;
        }

        private static async Task<Image> GetFaviconImageAsync(CoreWebView2 core)
        {
            try
            {
                if (string.IsNullOrEmpty(core.FaviconUri)) return null;
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
                using (var resp = await http.GetAsync(url))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                    return bytes != null && bytes.Length > 0 ? DecodeImage(bytes) : null;
                }
            }
            catch { return null; }
        }

        /// <summary>PNG/JPG/GIF/BMP, or .ico (extracts largest embedded PNG/BMP frame). Rejects 1x1 pixels.</summary>
        private static Image DecodeImage(byte[] bytes)
        {
            try
            {
                if (bytes == null || bytes.Length < 8) return null;
                bool isIco = bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0;
                if (isIco)
                {
                    int count = bytes[4] | (bytes[5] << 8);
                    int bestSize = -1, bestOffset = -1, bestLen = 0;
                    for (int i = 0; i < count && 6 + (i + 1) * 16 <= bytes.Length; i++)
                    {
                        int entry = 6 + i * 16;
                        int w = bytes[entry] == 0 ? 256 : bytes[entry];
                        int h = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
                        int len = BitConverter.ToInt32(bytes, entry + 8);
                        int off = BitConverter.ToInt32(bytes, entry + 12);
                        if (len > 8 && off >= 0 && off + len <= bytes.Length && w * h > bestSize)
                        {
                            bestSize = w * h;
                            bestOffset = off;
                            bestLen = len;
                        }
                    }
                    // Modern .ico files embed high-res PNG frames directly inside the ICO container.
                    if (bestOffset >= 0 && bytes[bestOffset] == 0x89 && bytes[bestOffset + 1] == 0x50 &&
                        bytes[bestOffset + 2] == 0x4E && bytes[bestOffset + 3] == 0x47)
                    {
                        using (var pms = new MemoryStream(bytes, bestOffset, bestLen))
                        using (var pimg = Image.FromStream(pms))
                        {
                            if (pimg.Width >= 8 && pimg.Height >= 8) return new Bitmap(pimg);
                        }
                    }
                    using (var ms = new MemoryStream(bytes))
                    using (var ico = new Icon(ms, 256, 256))
                    {
                        var bmp = ico.ToBitmap();
                        if (bmp.Width >= 8 && bmp.Height >= 8) return bmp;
                        bmp.Dispose();
                        return null;
                    }
                }
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                {
                    if (img.Width < 8 || img.Height < 8) return null;
                    return new Bitmap(img);
                }
            }
            catch { return null; }
        }

        private static HttpClient CreateHttpClient()
        {
            // Only used for site icons: short timeout, and nothing larger than 2 MB is accepted.
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            return client;
        }

        private async Task InitAccountAsync(Account acc)
        {
            if (acc == null || acc.InitStarted || acc.View.IsDisposed) return;
            acc.InitStarted = true;

            try
            {
                await security.UnlockProfileAsync(acc.Id);
                if (acc.View.IsDisposed) { acc.FirstNavDone.TrySetResult(false); return; }
                var options = env.CreateCoreWebView2ControllerOptions();
                options.ProfileName = acc.Id; // separate login/cookies per account
                await acc.View.EnsureCoreWebView2Async(env, options);
            }
            catch (Exception ex)
            {
                Diag.Write("browser start failed for " + acc.Name + ": " + ex.Message);
                acc.FirstNavDone.TrySetResult(false);
                acc.InitStarted = false; // the startup check, Reload or selecting the tile will try again
                if (!acc.View.IsDisposed && acc.StartupReloads == 0)
                    MessageBox.Show(this, "Could not open '" + acc.Name + "':\n\n" + ex.Message,
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            try
            {
                if (acc.View.IsDisposed) { acc.FirstNavDone.TrySetResult(false); return; } // removed while it was starting

                var core = acc.View.CoreWebView2;
                try { security.AddBrowserPid((int)core.BrowserProcessId); } catch { } // so we can wait for it on exit
                ConfigureCore(core, acc);
                EnableTrackingPrevention(acc); // ad/tracker blocking on by default, before the first navigation

                // Persistent storage (so logins stay) and, unless you already chose, notifications.
                if (Uri.TryCreate(acc.StartUrl, UriKind.Absolute, out var startUri))
                    _ = GrantDefaultPermissionsAsync(core, startUri.GetLeftPart(UriPartial.Authority));

                // Block high-volume telemetry, analytics, and ad-beacon requests at the native browser layer.
                foreach (var pattern in BlockedResourcePatterns)
                {
                    try { core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All); } catch { }
                }
                core.WebResourceRequested += (s, e) =>
                {
                    try { e.Response = env.CreateWebResourceResponse(Stream.Null, 204, "No Content", ""); } catch { }
                };

                core.DocumentTitleChanged += (s, e) => UpdateUnread(acc, core.DocumentTitle);
                core.NotificationReceived += (s, e) =>
                {
                    if (acc.Service.UnreadScript != null) _ = PollAccountUnreadAsync(acc);
                };
                core.SourceChanged += (s, e) => SyncBrowserBar(acc);
                core.HistoryChanged += (s, e) => SyncBrowserBar(acc);
                core.NewWindowRequested += (s, e) => OnNewWindowRequested(acc, e);
                core.NavigationStarting += (s, e) =>
                {
                    // Keep the account on its own site; anything else opens in the default browser,
                    // except sign-in provider pages, which must load in-app so SSO can finish.
                    if (IsWebUrl(e.Uri) && !acc.OwnsUrl(e.Uri) && !IsAuthDomain(e.Uri))
                    {
                        e.Cancel = true;
                        OpenExternal(e.Uri);
                    }
                };
                core.NavigationCompleted += (s, e) =>
                {
                    acc.IdleBgApplied = false;
                    acc.SentActive = null; // a new page has no state yet: send the flag again
                    SyncBrowserBar(acc);
                    if (e.IsSuccess)
                    {
                        acc.LoadedAt = DateTime.UtcNow;
                        if (!acc.FirstLoadOk && !IsForeground(acc))
                        {
                            // Low-memory mode only once the first page has finished loading.
                            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
                        }
                        acc.FirstLoadOk = true;
                        UpdateUnread(acc, core.DocumentTitle);
                    }
                    if (AccountIcon(acc) == null && (e.IsSuccess || acc.IsCustom))
                        _ = CaptureIconAsync(acc);
                    if (!e.IsSuccess) Diag.Write("page did not load for " + acc.Name + ": " + e.WebErrorStatus);
                    acc.FirstNavDone.TrySetResult(e.IsSuccess);
                    if (isSystemIdle && IsVisibleActive(acc)) ApplyIdlePageFreeze(acc, true);
                };
                // If the page's process crashes, reload it once (rate-limited so it can't loop).
                core.ProcessFailed += (s, e) =>
                {
                    if (acc.View.IsDisposed) return;
                    Diag.Write("browser process failed for " + acc.Name + ": " + e.ProcessFailedKind);
                    if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    {
                        OnBrowserProcessExited(); // the whole engine is gone: a reload can't help
                        return;
                    }
                    if (DateTime.UtcNow - acc.LastRecoverUtc < TimeSpan.FromSeconds(20)) return;
                    acc.LastRecoverUtc = DateTime.UtcNow;
                    try { acc.View.CoreWebView2?.Reload(); } catch { }
                };
                core.FaviconChanged += async (s, e) => await CaptureIconAsync(acc);

                // Unread counts pushed by the page (registered before the scripts that send them).
                core.WebMessageReceived += (s, e) =>
                {
                    try
                    {
                        string msg = e.TryGetWebMessageAsString();
                        if (msg != null && msg.StartsWith("__mh_unread:", StringComparison.Ordinal))
                        {
                            if (int.TryParse(msg.Substring(12), out int n) && n >= 0 && n != acc.Unread)
                            {
                                acc.Unread = n;
                                RefreshButton(acc);
                                UpdateTitle();
                            }
                        }
                    }
                    catch { }
                };

                // Page scripts. Retried once, and the page is opened even if they can't be added.
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try { await AddPageScriptsAsync(core, acc); break; }
                    catch (Exception ex)
                    {
                        Diag.Write("page scripts failed (try " + attempt + ") for " + acc.Name + ": " + ex.Message);
                        await Task.Delay(500);
                    }
                }

                if (acc.View.IsDisposed) { acc.FirstNavDone.TrySetResult(false); return; }
                core.Navigate(acc.StartUrl);
                SyncBrowserBar(acc);
                ApplyBackgroundState();
                UpdateIdentityTimerState();
                UpdateUnreadTimerState();
            }
            catch (Exception ex)
            {
                Diag.Write("setup failed for " + acc.Name + ": " + ex.Message);
                acc.FirstNavDone.TrySetResult(false); // usually the account was removed while starting
                // Still open the page, so the tile is never left blank.
                try { if (!acc.View.IsDisposed) acc.View.CoreWebView2?.Navigate(acc.StartUrl); } catch { }
            }
        }

        /// <summary>
        /// Always allows persistent storage. Allows notifications only when the account has no saved choice yet,
        /// so a "Block" chosen on a notification pop-up is kept across restarts.
        /// </summary>
        private static async Task GrantDefaultPermissionsAsync(CoreWebView2 core, string origin)
        {
            try
            {
                var profile = core.Profile;
                await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.PersistentStorage, origin, CoreWebView2PermissionState.Allow);

                var saved = await profile.GetNonDefaultPermissionSettingsAsync();
                bool chosen = saved.Any(p => p.PermissionKind == CoreWebView2PermissionKind.Notifications &&
                    string.Equals((p.PermissionOrigin ?? "").TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                if (!chosen)
                    await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Notifications, origin, CoreWebView2PermissionState.Allow);
            }
            catch { }
        }

        /// <summary>Scripts that run on every page of an account. Safe to add twice (each guards itself).</summary>
        private static async Task AddPageScriptsAsync(CoreWebView2 core, Account acc)
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(Scripts.EnsurePersistentStorage); // keep logins from being evicted
            await core.AddScriptToExecuteOnDocumentCreatedAsync(Scripts.BlockVideoAutoplay);      // videos/GIFs only play when clicked
            if (acc.Service == ServiceType.WhatsApp)
                await core.AddScriptToExecuteOnDocumentCreatedAsync(Scripts.EnableWhatsAppBackgroundSync);
            if (acc.Service == ServiceType.Telegram) // its pop-ups go through a service worker, which WebView2 can't show
                await core.AddScriptToExecuteOnDocumentCreatedAsync(Scripts.ServiceWorkerNotificationsToPage);
            if (acc.Service.UnreadScript != null) // push unread changes immediately instead of waiting for the poll
                await core.AddScriptToExecuteOnDocumentCreatedAsync(Scripts.BuildUnreadPushScript(acc.Service.UnreadScript));
        }

        /// <summary>Reloads the page; if the tile has no page yet (blank), opens its start page; if its browser never started, starts it.</summary>
        private void ReloadOrOpen(Account acc)
        {
            if (acc == null || acc.View.IsDisposed) return;
            var core = acc.View.CoreWebView2;
            if (core == null) { if (env != null && !acc.InitStarted) _ = InitAccountAsync(acc); return; }
            try
            {
                string src = core.Source;
                if (string.IsNullOrEmpty(src) || src.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) core.Navigate(acc.StartUrl);
                else core.Reload();
            }
            catch { }
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

        private bool IsVisibleActive(Account a) =>
            a == active && Visible && WindowState != FormWindowState.Minimized;

        private bool IsForeground(Account a) =>
            !isSystemIdle && IsVisibleActive(a);

        private static void ApplyIdlePageFreeze(Account a, bool freeze)
        {
            if (a == null || a.View.IsDisposed || a.IdleBgApplied == freeze) return;
            var core = a.View.CoreWebView2;
            if (core == null) return;
            a.IdleBgApplied = freeze;
            try
            {
                _ = core.ExecuteScriptAsync(freeze
                    ? "if(window.__mhSetIdleBg)window.__mhSetIdleBg(true);"
                    : "if(window.__mhSetIdleBg)window.__mhSetIdleBg(false);");
            }
            catch { }
        }

        /// <summary>Background account housekeeping: compact its JS heap (rate limited per account).</summary>
        private async Task PurgeIdleAccountAsync(Account a)
        {
            try
            {
                if (a == null || a.View.IsDisposed || IsForeground(a)) return;
                var core = a.View.CoreWebView2;
                if (core == null) return;

                // Compact only this tab's V8 JS heap, at most every 10 minutes per account.
                if ((DateTime.UtcNow - a.LastMemoryPurgeUtc).TotalMinutes >= 10)
                {
                    a.LastMemoryPurgeUtc = DateTime.UtcNow;
                    try { await core.CallDevToolsProtocolMethodAsync("HeapProfiler.collectGarbage", "{}"); } catch { }
                }
            }
            catch { }
        }

        private void UpdateMinimizedTimestamp()
        {
            if (!IsHandleCreated) return;
            bool isMinimizedOrHidden = !Visible || WindowState == FormWindowState.Minimized;
            if (isMinimizedOrHidden)
            {
                if (!minimizedSinceUtc.HasValue) minimizedSinceUtc = DateTime.UtcNow;
            }
            else
            {
                minimizedSinceUtc = null;
            }
        }

        private bool IsAppMinimizedAtLeast10Min()
        {
            UpdateMinimizedTimestamp();
            return minimizedSinceUtc.HasValue &&
                   (DateTime.UtcNow - minimizedSinceUtc.Value).TotalMilliseconds >= IdleCacheCheckMs;
        }

        private bool IsEligibleForSilentCacheClean() =>
            GetSystemIdleMilliseconds() >= IdleCacheCheckMs || IsAppMinimizedAtLeast10Min();

        /// <summary>
        /// Background accounts (and all accounts while hidden in the tray or after 10 minutes of PC idle):
        ///  - low-memory mode (they keep running, so messages and notifications still arrive)
        ///  - V8 JS heap GC compaction
        ///  - CSS animation / non-call video pause on idle visible tab
        ///  - muted if the account is set to Silent and in the background
        /// </summary>
        private void ApplyBackgroundState()
        {
            UpdateMinimizedTimestamp();
            if (isSystemIdle && GetSystemIdleMilliseconds() < IdleCacheCheckMs)
            {
                isSystemIdle = false;
                if (idleMonitorTimer.Interval != IdlePollActiveMs) idleMonitorTimer.Interval = IdlePollActiveMs;
            }
            if (!IsEligibleForSilentCacheClean())
            {
                lastIdleCacheCheckUtc = DateTime.MinValue;
            }

            // Reduce polling frequency and enable Windows EcoQoS Efficiency Mode when hidden, minimized, or PC idle >= 10m.
            bool isVisibleOnScreen = Visible && WindowState != FormWindowState.Minimized;
            bool isBackground = !isVisibleOnScreen || isSystemIdle;
            SetEfficiencyMode(isBackground);
            int targetInterval = isBackground ? UnreadBackgroundInterval : UnreadForegroundInterval;
            if (unreadTimer.Interval != targetInterval) unreadTimer.Interval = targetInterval;

            foreach (var a in accounts)
            {
                var core = a.View.CoreWebView2;
                if (core == null) continue;
                bool visibleActive = a == active && isVisibleOnScreen;
                bool fg = !isSystemIdle && visibleActive;
                try
                {
                    // A page that is still loading for the first time stays in normal mode.
                    core.MemoryUsageTargetLevel = fg || !a.FirstLoadOk
                        ? CoreWebView2MemoryUsageTargetLevel.Normal
                        : CoreWebView2MemoryUsageTargetLevel.Low;
                }
                catch { }

                if (a.SentActive != fg)
                {
                    a.SentActive = fg;
                    try { _ = core.ExecuteScriptAsync("window.__mhActive = " + (fg ? "true" : "false") + ";"); } catch { }
                }

                ApplyIdlePageFreeze(a, isSystemIdle && visibleActive);

                if (!fg) _ = PurgeIdleAccountAsync(a);

                // Global Silent mutes everything; otherwise a per-account Silent mutes it while in the background.
                try { core.IsMuted = globalSilent || (a.Silent && !visibleActive); } catch { }
            }
            UpdateUnreadTimerState();
        }

        /// <summary>Hourly nudge for the update check, which itself only runs once a week.</summary>
        private void UpdateCheckTick()
        {
            // Cheap no-ops until the weekly update check / daily secure-DNS timing is due.
            Updater.StartBackgroundCheck();
            CheckSecureDns();
        }

        /// <summary>Times the secure-DNS servers (daily, in the background) and saves the fastest for the next start.</summary>
        private void CheckSecureDns() => SecureDns.CheckIfDue(settings, order =>
        {
            try
            {
                BeginInvoke((Action)(() =>
                {
                    SecureDns.Save(settings, order);
                    settings.Save(AppPaths.SettingsFile);
                }));
            }
            catch { }
        });

        private async Task IdleMonitorTickAsync()
        {
            bool nowIdle = GetSystemIdleMilliseconds() >= IdleCacheCheckMs;
            if (nowIdle != isSystemIdle)
            {
                isSystemIdle = nowIdle;
                idleMonitorTimer.Interval = nowIdle ? IdlePollWakeMs : IdlePollActiveMs;
                ApplyBackgroundState();
                if (nowIdle) TrimProcessMemory();
            }

            bool eligible = IsEligibleForSilentCacheClean();
            if (!eligible)
            {
                lastIdleCacheCheckUtc = DateTime.MinValue;
                return;
            }

            if ((DateTime.UtcNow - lastIdleCacheCheckUtc).TotalMilliseconds >= IdleCacheCheckMs)
            {
                lastIdleCacheCheckUtc = DateTime.UtcNow;
                await IdleCacheCheckTickAsync();
            }
        }

        private bool checkingIdleCache;

        /// <summary>
        /// Periodic 10-minute idle / minimized cache monitor: when the PC has been idle for >= 10 minutes
        /// OR Messenger Hub has been minimized / hidden in the system tray for >= 10 minutes (checked every
        /// 10 minutes while either condition holds), measure each profile individually and silently clear +
        /// refresh only the profile(s) exceeding 200 MB.
        /// </summary>
        private async Task IdleCacheCheckTickAsync()
        {
            if (checkingIdleCache || !IsEligibleForSilentCacheClean()) return;
            checkingIdleCache = true;
            try
            {
                const CoreWebView2BrowsingDataKinds cacheKinds = CoreWebView2BrowsingDataKinds.DiskCache;

                foreach (var acc in accounts.ToList())
                {
                    if (IsDisposed || !IsEligibleForSilentCacheClean()) break;
                    if (!acc.InitStarted || acc.View.IsDisposed || acc.View.CoreWebView2 == null) continue;

                    string accId = acc.Id;
                    long bytes = await Task.Run(() => security.MeasureProfileCache(accId));
                    if (bytes <= Security.ProfileCacheLimitBytes) continue;
                    if (IsDisposed || acc.View.IsDisposed || !IsEligibleForSilentCacheClean()) continue;

                    var core = acc.View.CoreWebView2;
                    if (core == null) continue;

                    try { await core.Profile.ClearBrowsingDataAsync(cacheKinds); } catch { }
                    await Task.Delay(1000); // let the browser finish removing files
                    if (IsDisposed || acc.View.IsDisposed) continue;

                    // Reload only if clearing freed space; otherwise the rest is removed on exit,
                    // so the page isn't reloaded again every 10 minutes for nothing.
                    long after = await Task.Run(() => security.MeasureProfileCache(accId));
                    if (after >= bytes) continue;

                    acc.LoadedAt = DateTime.UtcNow;
                    try { acc.View.CoreWebView2?.Reload(); } catch { }
                }
            }
            finally
            {
                checkingIdleCache = false;
            }
        }

        private void SelectAccount(Account acc)
        {
            active = acc;
            foreach (var a in accounts)
            {
                a.HostControl.Visible = a == acc;
                a.Button.Active = a == acc;
                a.Button.Invalidate();
            }
            emptyLabel.Visible = acc == null;
            ApplyBackgroundState();

            if (acc != null)
            {
                acc.HostControl.BringToFront();
                SyncBrowserBar(acc);
                settings.Set("ActiveAccount", acc.Id);
                if (env != null && !acc.InitStarted)
                {
                    _ = InitAccountAsync(acc);
                }
                else if (acc.InitStarted && acc.FirstNavDone.Task.IsCompleted)
                {
                    _ = RecoverIfBlankAsync(acc);
                }
                try { acc.View.Focus(); } catch { }
                _ = PollAfterSelectAsync(acc);
            }
            UpdateTitle();
        }

        /// <summary>Reads the unread count now and again shortly after, once the page knows it is the active tab.</summary>
        private async Task PollAfterSelectAsync(Account acc)
        {
            await PollAccountUnreadAsync(acc);
            await Task.Delay(250);
            if (!IsDisposed && acc == active) await PollAccountUnreadAsync(acc);
        }

        private async Task RecoverIfBlankAsync(Account acc)
        {
            if (acc == null || acc.View.IsDisposed) return;
            var core = acc.View.CoreWebView2;
            if (core == null) return;
            if (!acc.FirstNavDone.Task.IsCompleted) return;
            if (DateTime.UtcNow - acc.LoadedAt < TimeSpan.FromSeconds(8)) return;
            if (DateTime.UtcNow - acc.LastRecoverUtc < TimeSpan.FromSeconds(15)) return;

            if (!await IsReady(acc))
            {
                acc.LastRecoverUtc = DateTime.UtcNow;
                ReloadOrOpen(acc);
            }
        }

        private async Task AddAccountAsync(ServiceType service)
        {
            if (env == null) return;

            string customUrl = null;
            string accountName;
            if (service == ServiceType.Custom)
            {
                if (!Dialogs.PromptCustomSite(this, out customUrl, out string customName)) return;
                accountName = string.IsNullOrWhiteSpace(customName)
                    ? NextCustomName(customUrl)
                    : customName.Replace(";", ",");
            }
            else
            {
                accountName = NextName(service);
            }

            string id = service.Key.Substring(0, 2) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var acc = CreateAccount(id, service, accountName, customUrl, false);
            acc.IsNew = true; // check it isn't a duplicate once it logs in
            SaveSettings();
            SelectAccount(acc);
            await InitAccountAsync(acc);
            UpdateIdentityTimerState();
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
            else
            {
                // The browser never started for this account, so delete its (possibly restored) profile folder directly.
                await Task.Run(() => security.DeleteProfile(acc.Id));
            }

            ResetCustomIcon(acc);
            accounts.Remove(acc);
            accountList.Controls.Remove(acc.Button);
            content.Controls.Remove(acc.HostControl);
            tips.SetToolTip(acc.Button, null);
            if (acc.Button?.ContextMenuStrip != null)
            {
                activeDropDowns.Remove(acc.Button.ContextMenuStrip);
                if (activeDropDowns.Count == 0) UnhookMouse();
                acc.Button.ContextMenuStrip.Dispose();
            }
            acc.Button.Dispose();
            acc.View.Dispose();
            acc.Container?.Dispose();

            foreach (var key in new[] { "Name.", "Type.", "Silent.", "Id.", "Url.", "Bar." }) settings.Remove(key + acc.Id);
            SaveSettings();
            if (active == acc) SelectAccount(accounts.FirstOrDefault());
            UpdateTitle();
            UpdateIdentityTimerState();
            UpdateUnreadTimerState();
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

        /// <summary>
        /// On app launch, the app and its whole WebView2 process tree runs at AboveNormal priority
        /// for the first minute (re-applied every 3s so processes that spawn mid-startup are covered too),
        /// then everything drops back to Normal. No admin needed.
        /// </summary>
        private void StartStartupPriorityBoost()
        {
            ApplyPriorityTree(ProcessPriorityClass.AboveNormal);

            int ticks = 0;
            startupPriorityTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            startupPriorityTimer.Tick += (s, e) =>
            {
                ticks++;
                if (ticks >= 20) // 20 * 3s = 60 seconds
                {
                    startupPriorityTimer.Stop();
                    startupPriorityTimer.Dispose();
                    startupPriorityTimer = null;
                    ApplyPriorityTree(ProcessPriorityClass.Normal);
                    return;
                }

                ApplyPriorityTree(ProcessPriorityClass.AboveNormal);
            };
            startupPriorityTimer.Start();
        }

        private void ApplyPriorityTree(ProcessPriorityClass priority)
        {
            try
            {
                var cur = Process.GetCurrentProcess();
                if (cur.PriorityClass != priority) cur.PriorityClass = priority;
            }
            catch { }

            if (env != null)
            {
                try
                {
                    var infos = env.GetProcessInfos();
                    if (infos != null)
                    {
                        foreach (var info in infos)
                        {
                            TrySetProcessPriority(info.ProcessId, priority);
                        }
                    }
                }
                catch { }
            }
        }

        private static void TrySetProcessPriority(int pid, ProcessPriorityClass priority)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    if (!p.HasExited && p.PriorityClass != priority)
                    {
                        p.PriorityClass = priority;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Ad/tracker blocking is always on: set every profile's WebView2 tracking prevention to Strict.
        /// Blocks third-party trackers and ad-network domains (not first-party in-feed sponsored posts).
        /// </summary>
        private static void EnableTrackingPrevention(Account acc)
        {
            var profile = acc.View.CoreWebView2?.Profile;
            if (profile == null) return;
            try { profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict; }
            catch { /* older WebView2 runtime without this API */ }
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

        private void SaveSettings(bool immediate = false)
        {
            settings.Set("Accounts", string.Join(";", accounts.Select(a => a.Id)));
            foreach (var a in accounts)
            {
                settings.Set("Name." + a.Id, a.Name);
                settings.Set("Type." + a.Id, a.Service.Key);
                settings.Set("Silent." + a.Id, a.Silent ? "1" : "0");
                if (!string.IsNullOrEmpty(a.Identity)) settings.Set("Id." + a.Id, a.Identity);
                if (a.IsCustom)
                {
                    settings.Set("Url." + a.Id, a.StartUrl);
                    settings.Set("Bar." + a.Id, a.ShowAddressBar ? "1" : "0");
                }
            }
            if (immediate)
            {
                saveDebounceTimer.Stop();
                settings.Save(AppPaths.SettingsFile);
            }
            else
            {
                saveDebounceTimer.Stop();
                saveDebounceTimer.Start();
            }
        }

        // ------------------------------------------------------------------ Unread counts & notifications

        // Messengers put the unread count in the page title, e.g. "(3) WhatsApp".
        private static readonly Regex UnreadPattern = new Regex(@"\(\*?(\d+)\+?\)", RegexOptions.Compiled);

        private void UpdateUnreadTimerState()
        {
            bool anyNeedsPoll = accounts.Any(a =>
                !a.View.IsDisposed && a.InitStarted && a.Service.UnreadScript != null);

            if (anyNeedsPoll)
            {
                if (!unreadTimer.Enabled) unreadTimer.Start();
            }
            else
            {
                if (unreadTimer.Enabled) unreadTimer.Stop();
            }
        }

        private async Task PollAccountUnreadAsync(Account acc)
        {
            if (acc == null || !acc.InitStarted || acc.View.IsDisposed) return;
            var script = acc.Service.UnreadScript;
            if (script == null) return;
            var core = acc.View.CoreWebView2;
            if (core == null) return;
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

        /// <summary>
        /// For services whose tab title doesn't carry the unread count (e.g. Telegram), read it from
        /// the page. Runs for background accounts too, so their sidebar badge stays current.
        /// </summary>
        private async Task UnreadPollAsync()
        {
            for (int i = 0; i < accounts.Count; i++)
            {
                await PollAccountUnreadAsync(accounts[i]);
            }
        }

        private void UpdateUnread(Account acc, string title)
        {
            if (acc.Service.NoUnreadBadge) return; // e.g. Slack: no badge at all
            var m = UnreadPattern.Match(title ?? "");
            int titleUnread = m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;

            if (acc.Service.UnreadScript != null)
            {
                // When document.title shows unread activity while backgrounded, update immediately so badges respond without latency.
                if (!IsForeground(acc) && titleUnread > 0 && titleUnread != acc.Unread)
                {
                    acc.Unread = titleUnread;
                    RefreshButton(acc);
                    UpdateTitle();
                }
                _ = PollAccountUnreadAsync(acc);
                return;
            }

            if (titleUnread == acc.Unread) return;
            acc.Unread = titleUnread;
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
            UpdateTaskbarOverlay(total);
        }

        // Picks the tray icon for the current state: red dot if unread, crescent moon if globally silent.
        private void UpdateTrayIcon(bool unread)
        {
            int idx = (unread ? 1 : 0) | (globalSilent ? 2 : 0);
            if (idx == trayIconIndex || trayIcons[idx] == null) return;
            trayIconIndex = idx;
            tray.Icon = trayIcons[idx];
        }

        private const int WM_DPICHANGED = 0x02E0;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = (int)((long)m.WParam & 0xFFFF);
                if (dpi > 0 && Math.Abs(dpi / 96f - scale) > 0.001f) { scale = dpi / 96f; ApplyScale(); }
            }
            if (WM_TASKBARBUTTONCREATED != 0 && (uint)m.Msg == WM_TASKBARBUTTONCREATED)
            {
                UpdateTaskbarOverlay(accounts.Sum(a => a.Unread), true);
            }
        }

        /// <summary>Re-applies every DPI-dependent size (after a move to a monitor with different scaling).</summary>
        private void ApplyScale()
        {
            MinimumSize = new Size(S(700), S(450));
            sidebar.Width = S(SidebarWidth);
            accountList.Padding = new Padding(0, S(6), 0, 0);
            addButton.Size = new Size(S(SidebarWidth), S(ButtonHeight));
            foreach (var a in accounts)
            {
                a.Button.Size = new Size(S(SidebarWidth), S(ButtonHeight));
                if (a.BrowserBar != null) a.BrowserBar.Height = S(36);
            }
            dropIndicator.Height = Math.Max(2, S(2));
            dropIndicator.Width = S(SidebarWidth) - S(8);
            dropIndicator.Left = S(4);
        }

        private static IntPtr MakeTaskbarOverlayHIcon(string text)
        {
            try
            {
                int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
                using (var bmp = new Bitmap(size, size))
                {
                    using (var g = Graphics.FromImage(bmp))
                    using (var bg = new SolidBrush(Color.FromArgb(234, 67, 53)))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        g.FillEllipse(bg, 0, 0, size - 1, size - 1);

                        float fontPx = text.Length == 1 ? size * 0.68f : (text.Length == 2 ? size * 0.54f : size * 0.44f);
                        using (var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        {
                            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size, size), sf);
                        }
                    }
                    return bmp.GetHicon();
                }
            }
            catch { return IntPtr.Zero; }
        }

        private void UpdateTaskbarOverlay(int total, bool force = false)
        {
            if (!Visible || !IsHandleCreated || IsDisposed) return;
            string badge = total <= 0 ? "" : (total > 99 ? "99+" : total.ToString());
            if (!force && badge == lastTaskbarBadge) return;

            try
            {
                if (taskbarList == null)
                {
                    var inst = (ITaskbarList3)new TaskbarListInstance();
                    if (inst.HrInit() < 0) return;
                    taskbarList = inst;
                }

                if (badge != lastTaskbarBadge)
                {
                    if (taskbarBadgeHIcon != IntPtr.Zero)
                    {
                        DestroyIcon(taskbarBadgeHIcon);
                        taskbarBadgeHIcon = IntPtr.Zero;
                    }
                    if (badge.Length > 0)
                    {
                        taskbarBadgeHIcon = MakeTaskbarOverlayHIcon(badge);
                    }
                    lastTaskbarBadge = badge;
                }

                taskbarList.SetOverlayIcon(Handle, taskbarBadgeHIcon,
                    badge.Length > 0 ? total + " unread" : null);
            }
            catch { }
        }

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

            reallyExit = true; // closing for good (also on Windows shutdown): don't treat the engine stopping as a crash
            SetEfficiencyMode(false);
            if (!resetting)
            {
                settings.CaptureFrom(this);
                SaveSettings(true);
            }
            tray.Visible = false;
        }

        private void HideToTray()
        {
            settings.CaptureFrom(this);
            SaveSettings(true);
            Hide();
            TrimProcessMemory();

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
            long before = await Task.Run(() => Security.DirSize(AppPaths.Data));

            if (!Dialogs.ConfirmClearCache(this, FormatSize(before)))
                return;

            UseWaitCursor = true;
            try
            {
                // Cached media lives in the HTTP disk cache; logins/Service Workers are never touched.
                var kinds = CoreWebView2BrowsingDataKinds.DiskCache |
                            CoreWebView2BrowsingDataKinds.DownloadHistory;

                foreach (var acc in accounts.ToList())
                {
                    var core = acc.View.CoreWebView2;
                    if (core == null) continue;
                    try { await core.Profile.ClearBrowsingDataAsync(kinds); } catch { }
                }

                foreach (var acc in accounts) { acc.LoadedAt = DateTime.UtcNow; acc.View.CoreWebView2?.Reload(); }

                await Task.Delay(2000); // give the browser a moment to remove files
            }
            finally
            {
                UseWaitCursor = false;
            }

            long after = await Task.Run(() => Security.DirSize(AppPaths.Data));
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
            Program.ResetInProgress = true;

            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--reset") { UseShellExecute = false });
            }
            catch { /* the flag file finishes the reset on next manual start */ }

            ExitApp();
        }

        // ------------------------------------------------------------------ Browser events

        // Allow what a messenger needs (notifications, mic for voice notes, camera, clipboard)
        // but only for the account's own site. Custom websites are asked before mic, camera or clipboard.
        private void OnPermissionRequested(Account acc, CoreWebView2PermissionRequestedEventArgs e)
        {
            if (!acc.OwnsUrl(e.Uri)) return;

            switch (e.PermissionKind)
            {
                case CoreWebView2PermissionKind.Notifications:
                case CoreWebView2PermissionKind.PersistentStorage:
                    e.State = CoreWebView2PermissionState.Allow;
                    break;
                case CoreWebView2PermissionKind.Microphone:
                case CoreWebView2PermissionKind.Camera:
                case CoreWebView2PermissionKind.ClipboardRead:
                    // Messengers get these automatically; a custom website has to ask (the browser's own prompt).
                    if (!acc.IsCustom) e.State = CoreWebView2PermissionState.Allow;
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
            bool externalLink = IsWebUrl(e.Uri) && !acc.OwnsUrl(e.Uri) && !IsAuthDomain(e.Uri);
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
                form.Controls.Add(view); // closing the window disposes it and the view
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
        // ("Sign in with Google/Apple/Microsoft") can complete without leaving the app.
        private static readonly string[] AuthDomains =
        {
            "accounts.google.com", "accounts.youtube.com",
            "appleid.apple.com", "idmsa.apple.com",
            "login.microsoftonline.com", "login.microsoft.com", "login.live.com",
            "auth0.com", "okta.com", "oktapreview.com", "onelogin.com",
            "duosecurity.com", "cloudflareaccess.com", "challenges.cloudflare.com",
            "arkoselabs.com", "withpersona.com", "recaptcha.net", "hcaptcha.com"
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

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
            return (bytes / 1024.0).ToString("0") + " KB";
        }
    }
}
