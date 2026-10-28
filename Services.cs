using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace MessengerHub
{
    /// <summary>A supported messenger (WhatsApp, Telegram, Facebook Messenger).</summary>
    internal class ServiceType
    {
        public string Key;          // saved in settings.ini
        public string Name;         // shown to the user
        public string Url;          // start page
        public string[] Domains;    // pages allowed to load inside the app
        public Color Color;         // icon color
        public string Letter;       // small badge letter on the icon
        public bool AllowPopups;    // open same-site pop-up windows inside the app (e.g. calls)
        public string IconPage;     // only save the site icon on pages containing this (null = any page)
        public string IdentityScript; // returns the logged-in phone number / name, or null (auto-naming)
        public string UnreadScript;   // returns the unread count from the DOM; null = read it from the tab title
        public string ReadyScript;    // returns 'true' when the page is usable; null = fall back to a text check
        public int CacheLimitMB = 300; // clear this account's media cache on exit once it passes this size

        public static readonly ServiceType WhatsApp = new ServiceType
        {
            Key = "whatsapp",
            Name = "WhatsApp",
            Url = "https://web.whatsapp.com/",
            Domains = new[] { "whatsapp.com", "whatsapp.net" },
            Color = Color.FromArgb(37, 211, 102),
            Letter = "W",
            IdentityScript = Scripts.WhatsAppNumber,
            ReadyScript = Scripts.WhatsAppReady, // detect a half-loaded first load (chat list missing)
        };

        public static readonly ServiceType Telegram = new ServiceType
        {
            Key = "telegram",
            Name = "Telegram",
            Url = "https://web.telegram.org/a/",
            Domains = new[] { "telegram.org" },
            Color = Color.FromArgb(42, 171, 238),
            Letter = "T",
            IdentityScript = Scripts.TelegramNumber,
            UnreadScript = Scripts.TelegramUnread, // its tab title doesn't carry the unread count reliably
        };

        // messenger.com was shut down by Meta in April 2026; Messenger now lives on facebook.com.
        public static readonly ServiceType Messenger = new ServiceType
        {
            Key = "messenger",
            Name = "Messenger",
            Url = "https://www.facebook.com/messages/",
            Domains = new[] { "facebook.com", "messenger.com", "meta.com" },
            Color = Color.FromArgb(163, 52, 250),
            Letter = "M",
            AllowPopups = true,
            IconPage = "/messages", // the Facebook login page shows the Facebook icon instead
            IdentityScript = Scripts.FacebookName,
        };

        // WeChat Web works by QR scan, but only for accounts Tencent allows — some accounts get
        // "WeChat Web Service Unavailable for This Account" and must use the WeChat desktop app instead.
        public static readonly ServiceType WeChat = new ServiceType
        {
            Key = "wechat",
            Name = "WeChat",
            Url = "https://web.wechat.com/",
            Domains = new[] { "wechat.com", "weixin.qq.com", "qq.com" },
            Color = Color.FromArgb(7, 193, 96),
            Letter = "C",
        };

        // Google Messages for web: QR-paired with the phone, which must stay online (it relays texts).
        public static readonly ServiceType GoogleMessages = new ServiceType
        {
            Key = "gmessages",
            Name = "Google Messages",
            Url = "https://messages.google.com/web",
            // Google sign-in / account setup bounces across several Google hosts; allow them in-app.
            Domains = new[] { "messages.google.com", "google.com", "gstatic.com", "googleusercontent.com", "googleapis.com" },
            Color = Color.FromArgb(26, 115, 232),
            Letter = "G",
            AllowPopups = true, // let Google's sign-in/consent popups open inside the app, not the external browser
            UnreadScript = Scripts.GoogleMessagesUnread,
        };

        public static readonly ServiceType Slack = new ServiceType
        {
            Key = "slack",
            Name = "Slack",
            Url = "https://app.slack.com/client",
            Domains = new[] { "slack.com", "slack-edge.com", "slack-msgs.com", "slackb.com" },
            Color = Color.FromArgb(74, 21, 75),
            Letter = "S",
            // Its heavy part is the app shell, which we keep (not trimmed), so the default 300 is plenty;
            // calibration adjusts it anyway.
            AllowPopups = true, // let Slack's SSO sign-in popups open inside the app
            UnreadScript = Scripts.SlackUnread, // its tab title doesn't carry the unread count reliably
        };

        public static readonly ServiceType[] All = { WhatsApp, Telegram, Messenger, WeChat, GoogleMessages, Slack };

        public static ServiceType ByKey(string key) =>
            All.FirstOrDefault(s => s.Key.Equals(key ?? "", StringComparison.OrdinalIgnoreCase)) ?? WhatsApp;

        /// <summary>Does this URL belong to the service (so it may load inside the app)?</summary>
        public bool Owns(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
            foreach (var d in Domains)
                if (u.Host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                    u.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Small round colored icon with the service letter (for menus).</summary>
        public Bitmap MenuIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(Color))
            using (var font = new Font("Segoe UI", size * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.FillEllipse(brush, 0, 0, size - 1, size - 1);
                g.DrawString(Letter, font, Brushes.White, new RectangleF(0, 0, size, size), center);
            }
            return bmp;
        }
    }
}
