using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace MessengerHub
{
    /// <summary>One messenger connection. Each has its own WebView2 profile (separate login).</summary>
    internal class Account
    {
        public string Id;           // also the WebView2 profile name
        public string Name;
        public ServiceType Service;
        public bool Silent;             // no sound while in the background
        public string Identity;         // the logged-in number/name (for duplicate detection); set once after login
        public bool IsNew;              // added this session and not yet checked for being a duplicate
        public WebView2 View;
        public AccountButton Button;
        public int Unread;
        public DateTime LoadedAt = DateTime.UtcNow; // last full page load (stuck-page checks wait a moment after it)
        public bool InitStarted;                     // true once WebView2 initialization has been started
        public TaskCompletionSource<bool> FirstNavDone = NewNavSignal();
        public bool? SentActive;                     // last "is this tab active" value sent to the page (null = not sent since load)
        public int IdentityMisses;                   // identity checks that found no logged-in name (for back-off)
        public DateTime IdentityNextUtc = DateTime.MinValue; // next identity check allowed
        public bool FirstLoadOk;                     // a top-level navigation has succeeded at least once
        public int StartupReloads;                   // auto-reloads used by the startup stuck-load watchdog
        public DateTime LastRecoverUtc = DateTime.MinValue; // last crash-recovery reload (rate limit)
        public DateTime LastMemoryPurgeUtc = DateTime.MinValue; // last JS heap compaction (rate limit)
        public bool IdleBgApplied;                   // true while PC-idle CSS/video pause is active on this tab
        public string LastTooltip;                   // cached tooltip string to avoid redundant Win32 TTM_UPDATETIPTEXT calls
        public string CustomUrl;                     // pinned starting URL for Custom website tabs
        public bool ShowAddressBar;                  // whether the slim browser navigation bar is visible (Custom tabs)
        public Panel Container;                      // wraps BrowserBar + View for Custom tabs (null for standard services)
        public Panel BrowserBar;
        public TextBox AddressBox;
        public Button BackButton;
        public Button ForwardButton;

        public bool IsCustom => Service == ServiceType.Custom;

        private static TaskCompletionSource<bool> NewNavSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Clears the per-browser state after the browser engine was restarted (a new view is about to start).</summary>
        public void ResetForNewBrowser()
        {
            InitStarted = false;
            FirstLoadOk = false;
            StartupReloads = 0;
            SentActive = null;
            IdleBgApplied = false;
            FirstNavDone = NewNavSignal();
        }
        public Control HostControl => (Control)Container ?? View;
        public string StartUrl => IsCustom && !string.IsNullOrEmpty(CustomUrl) ? CustomUrl : Service.Url;

        // Hosting services where every customer gets a subdomain. Each customer is its own "site",
        // so a custom tile never treats another customer's pages as its own.
        private static readonly HashSet<string> PrivateSuffixes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "github.io", "gitlab.io", "vercel.app", "netlify.app", "pages.dev", "workers.dev",
                "herokuapp.com", "azurewebsites.net", "web.app", "firebaseapp.com", "appspot.com",
                "onrender.com", "fly.dev", "glitch.me", "replit.dev", "surge.sh", "ngrok.io",
                "blogspot.com", "wixsite.com", "squarespace.com", "myshopify.com"
            };

        private static readonly HashSet<string> SecondLevelTlds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "co", "com", "net", "org", "gov", "edu", "ac", "mil", "ne", "or", "go", "gob"
            };

        public static string NormalizeUrl(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return null;
            if (!s.Contains("://")) s = "https://" + s;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            if (string.IsNullOrWhiteSpace(u.Host)) return null;
            if (!u.Host.Contains(".") &&
                !u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                u.HostNameType != UriHostNameType.IPv4 &&
                u.HostNameType != UriHostNameType.IPv6)
                return null;
            return u.AbsoluteUri;
        }

        public static string GetBaseDomain(string host)
        {
            string h = (host ?? "").Trim().ToLowerInvariant();
            if (h.StartsWith("www.")) h = h.Substring(4);
            if (Uri.CheckHostName(h) != UriHostNameType.Dns) return h;
            string[] parts = h.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 2) return h;
            int n = parts.Length;
            if (PrivateSuffixes.Contains(parts[n - 2] + "." + parts[n - 1])) return h;
            if (parts[n - 1].Length == 2 && SecondLevelTlds.Contains(parts[n - 2]))
                return parts[n - 3] + "." + parts[n - 2] + "." + parts[n - 1];
            return parts[n - 2] + "." + parts[n - 1];
        }

        public static string DefaultNameFromUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "Custom";
            string baseDomain = GetBaseDomain(u.Host);
            string label = baseDomain.Split('.')[0];
            if (string.IsNullOrEmpty(label)) return "Custom";
            return char.ToUpperInvariant(label[0]) + (label.Length > 1 ? label.Substring(1) : "");
        }

        public bool OwnsUrl(string uri)
        {
            if (!IsCustom) return Service.Owns(uri);
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var target) ||
                !Uri.TryCreate(StartUrl, UriKind.Absolute, out var pinned))
                return false;
            string baseDomain = GetBaseDomain(pinned.Host);
            return target.Host.Equals(baseDomain, StringComparison.OrdinalIgnoreCase) ||
                   target.Host.EndsWith("." + baseDomain, StringComparison.OrdinalIgnoreCase);
        }

        public string TooltipText()
        {
            string sub = Service.Name;
            if (IsCustom && Uri.TryCreate(StartUrl, UriKind.Absolute, out var u))
            {
                sub = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host.Substring(4) : u.Host;
            }
            string t = Name + "  (" + sub + ")";
            if (Silent) t += "\nSilent";
            return t;
        }

        /// <summary>"WhatsApp 2" -> "2", "Work Phone" -> "WP", "Home" -> "Ho".</summary>
        public static string ShortLabel(string name)
        {
            var parts = (name ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";

            string last = parts[parts.Length - 1];
            if (parts.Length > 1 && last.Length <= 3 && last.All(char.IsDigit)) return last;
            if (parts.Length >= 2) return char.ToUpper(parts[0][0]).ToString() + char.ToUpper(parts[1][0]);
            if (parts[0].Length >= 2) return char.ToUpper(parts[0][0]).ToString() + char.ToLower(parts[0][1]);
            return parts[0].ToUpper();
        }
    }

    /// <summary>
    /// Round sidebar icon: service color, account label, service letter badge (bottom-right),
    /// unread badge (top-right), silent mark (bottom-left).
    /// </summary>
    internal class AccountButton : Control
    {
        public string Label = "";
        public string Letter = "";
        public bool Active;
        public bool IsAddButton;
        public bool Silent;
        public int Unread;
        public Color Accent = Color.FromArgb(37, 211, 102);
        public Image SiteIcon;   // the messenger's favicon; null = colored letter circle

        private static readonly StringFormat CenterFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        private static readonly SolidBrush BadgeRedBrush = new SolidBrush(Color.FromArgb(234, 67, 53));
        private static readonly SolidBrush MarkBgBrush = new SolidBrush(Color.FromArgb(70, 85, 95));

        private bool hover;

        // The site icon scaled to the circle size and opacity; rebuilt only when one of those changes.
        private Bitmap scaledIcon;
        private Image scaledFrom;
        private int scaledSize;
        private float scaledOpacity;
        private TextureBrush scaledBrush;

        private float cachedScale = -1f;
        private Font fontAdd, fontLabel, fontLetter, fontBadge;

        private void EnsureFonts(float s)
        {
            if (Math.Abs(cachedScale - s) < 0.001f) return;
            fontAdd?.Dispose();
            fontLabel?.Dispose();
            fontLetter?.Dispose();
            fontBadge?.Dispose();
            fontAdd = new Font("Segoe UI", 20 * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fontLabel = new Font("Segoe UI", 15 * s, FontStyle.Bold, GraphicsUnit.Pixel);
            fontLetter = new Font("Segoe UI", 10 * s, FontStyle.Bold, GraphicsUnit.Pixel);
            fontBadge = new Font("Segoe UI", 11 * s, FontStyle.Bold, GraphicsUnit.Pixel);
            cachedScale = s;
        }

        /// <summary>Draws the icon inside the circle with smooth (anti-aliased) round edges.</summary>
        private void DrawSiteIcon(Graphics g, RectangleF circle, float opacity)
        {
            int size = Math.Max(1, (int)Math.Ceiling(circle.Width));
            if (scaledIcon == null || scaledFrom != SiteIcon || scaledSize != size || Math.Abs(scaledOpacity - opacity) > 0.01f)
            {
                // Each button keeps only its own scaled copy, rebuilt when the icon, size or opacity changes.
                scaledBrush?.Dispose();
                scaledIcon?.Dispose();
                scaledIcon = new Bitmap(size, size);
                using (var sg = Graphics.FromImage(scaledIcon))
                using (var attrs = new ImageAttributes())
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    attrs.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
                    sg.DrawImage(SiteIcon, new Rectangle(0, 0, size, size),
                                 0, 0, SiteIcon.Width, SiteIcon.Height, GraphicsUnit.Pixel, attrs);
                }
                scaledBrush = new TextureBrush(scaledIcon);
                scaledFrom = SiteIcon;
                scaledSize = size;
                scaledOpacity = opacity;
            }

            if (scaledBrush != null)
            {
                scaledBrush.ResetTransform();
                scaledBrush.TranslateTransform(circle.X, circle.Y);
                g.FillEllipse(scaledBrush, circle.X, circle.Y, size, size);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                scaledBrush?.Dispose();
                scaledIcon?.Dispose();
                fontAdd?.Dispose();
                fontLabel?.Dispose();
                fontLetter?.Dispose();
                fontBadge?.Dispose();
            }
            base.Dispose(disposing);
        }

        public AccountButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(BackColor);

            float s = Height / 60f;
            EnsureFonts(s);
            float d = 40 * s;
            float cx = Width / 2f, cy = Height / 2f;
            var circle = new RectangleF(cx - d / 2, cy - d / 2, d, d);
            var gray = Color.FromArgb(140, 160, 170);

            if (IsAddButton)
            {
                var color = hover ? Color.White : gray;
                using (var pen = new Pen(color, 1.5f * s) { DashStyle = DashStyle.Dash })
                    g.DrawEllipse(pen, circle);
                using (var brush = new SolidBrush(color))
                    g.DrawString("+", fontAdd, brush, circle, CenterFormat);
                return;
            }

            // Active marker on the left edge.
            if (Active)
                g.FillRectangle(Brushes.White, 0, cy - 14 * s, 3 * s, 28 * s);

            float opacity = Active ? 1f : (hover ? 0.85f : 0.55f);

            if (SiteIcon != null)
            {
                // The messenger's own site icon (favicon), dimmed when not selected.
                DrawSiteIcon(g, circle, opacity);
            }
            else
            {
                // Fallback until the site icon is saved: circle in the service color + label.
                var fill = Color.FromArgb((int)(255 * opacity), Accent);
                using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, circle);
                g.DrawString(Label, fontLabel, Brushes.White, circle, CenterFormat);
            }

            // Service letter badge (bottom-right): W / T / M — only in the fallback look.
            if (SiteIcon == null && !string.IsNullOrEmpty(Letter))
            {
                float b = 17 * s;
                var r = new RectangleF(circle.Right - b + 4 * s, circle.Bottom - b + 4 * s, b, b);
                using (var brush = new SolidBrush(ControlPaint.Dark(Accent, 0.1f)))
                using (var border = new Pen(BackColor, 2 * s))
                {
                    g.FillEllipse(brush, r);
                    g.DrawEllipse(border, r);
                    g.DrawString(Letter, fontLetter, Brushes.White, r, CenterFormat);
                }
            }

            // Silent mark (bottom-left): small speaker with an X.
            if (Silent) DrawSilentMark(g, new RectangleF(circle.Left - 4 * s, circle.Bottom - 13 * s, 17 * s, 17 * s), s);

            // Unread badge (top-right).
            if (Unread > 0)
            {
                string text = Unread > 99 ? "99+" : Unread.ToString();
                var size = g.MeasureString(text, fontBadge);
                float h = 18 * s;
                float w = Math.Max(h, size.Width + 6 * s);
                var rect = new RectangleF(circle.Right - w + 8 * s, circle.Top - 4 * s, w, h);
                using (var path = Pill(rect))
                using (var border = new Pen(BackColor, 2 * s))
                {
                    g.FillPath(BadgeRedBrush, path);
                    g.DrawPath(border, path);
                }
                g.DrawString(text, fontBadge, Brushes.White, rect, CenterFormat);
            }
        }

        private void DrawMarkBackground(Graphics g, RectangleF r, float s)
        {
            using (var border = new Pen(BackColor, 2 * s))
            {
                g.FillEllipse(MarkBgBrush, r);
                g.DrawEllipse(border, r);
            }
        }

        private void DrawSilentMark(Graphics g, RectangleF r, float s)
        {
            DrawMarkBackground(g, r, s);
            float x = r.Left + r.Width * 0.18f, y = r.Top + r.Height / 2f, u = r.Width / 17f;
            using (var pen = new Pen(Color.White, 1.3f * s))
            {
                // speaker
                g.FillPolygon(Brushes.White, new[]
                {
                    new PointF(x, y - 2 * u), new PointF(x + 2.5f * u, y - 2 * u), new PointF(x + 5.5f * u, y - 5 * u),
                    new PointF(x + 5.5f * u, y + 5 * u), new PointF(x + 2.5f * u, y + 2 * u), new PointF(x, y + 2 * u),
                });
                // X
                float xs = x + 7.5f * u;
                g.DrawLine(pen, xs, y - 2.5f * u, xs + 5 * u, y + 2.5f * u);
                g.DrawLine(pen, xs, y + 2.5f * u, xs + 5 * u, y - 2.5f * u);
            }
        }

        private static GraphicsPath Pill(RectangleF r)
        {
            var p = new GraphicsPath();
            float dia = r.Height;
            p.AddArc(r.Left, r.Top, dia, dia, 90, 180);
            p.AddArc(r.Right - dia, r.Top, dia, dia, 270, 180);
            p.CloseFigure();
            return p;
        }
    }
}
