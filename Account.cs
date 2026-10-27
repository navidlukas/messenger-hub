using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
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
        public bool NeedsCalibration;   // true until its cache limit has been measured this install
        public WebView2 View;
        public AccountButton Button;
        public int Unread;
        public DateTime LoadedAt = DateTime.UtcNow; // last full page load (for auto-refresh)

        public string TooltipText()
        {
            string t = Name + "  (" + Service.Name + ")";
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
    /// unread badge (top-right), silent / notifications-off marks (left side).
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

        private bool hover;

        // The site icon scaled to the circle size and opacity; rebuilt only when one of those changes.
        private Bitmap scaledIcon;
        private Image scaledFrom;
        private int scaledSize;
        private float scaledOpacity;

        /// <summary>Draws the icon inside the circle with smooth (anti-aliased) round edges.</summary>
        private void DrawSiteIcon(Graphics g, RectangleF circle, float opacity)
        {
            int size = Math.Max(1, (int)Math.Ceiling(circle.Width));
            if (scaledIcon == null || scaledFrom != SiteIcon || scaledSize != size || scaledOpacity != opacity)
            {
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
                scaledFrom = SiteIcon;
                scaledSize = size;
                scaledOpacity = opacity;
            }

            using (var brush = new TextureBrush(scaledIcon))
            {
                brush.TranslateTransform(circle.X, circle.Y);
                g.FillEllipse(brush, circle.X, circle.Y, size, size);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) scaledIcon?.Dispose();
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
            float d = 40 * s;
            float cx = Width / 2f, cy = Height / 2f;
            var circle = new RectangleF(cx - d / 2, cy - d / 2, d, d);
            var gray = Color.FromArgb(140, 160, 170);

            using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                if (IsAddButton)
                {
                    var color = hover ? Color.White : gray;
                    using (var pen = new Pen(color, 1.5f * s) { DashStyle = DashStyle.Dash })
                        g.DrawEllipse(pen, circle);
                    using (var font = new Font("Segoe UI", 20 * s, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(color))
                        g.DrawString("+", font, brush, circle, center);
                    return;
                }

                // Active marker on the left edge.
                if (Active)
                    using (var bar = new SolidBrush(Color.White))
                        g.FillRectangle(bar, 0, cy - 14 * s, 3 * s, 28 * s);

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
                    using (var font = new Font("Segoe UI", 15 * s, FontStyle.Bold, GraphicsUnit.Pixel))
                        g.DrawString(Label, font, Brushes.White, circle, center);
                }

                // Service letter badge (bottom-right): W / T / M — only in the fallback look.
                if (SiteIcon == null && !string.IsNullOrEmpty(Letter))
                {
                    float b = 17 * s;
                    var r = new RectangleF(circle.Right - b + 4 * s, circle.Bottom - b + 4 * s, b, b);
                    using (var brush = new SolidBrush(ControlPaint.Dark(Accent, 0.1f)))
                    using (var border = new Pen(BackColor, 2 * s))
                    using (var font = new Font("Segoe UI", 10 * s, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        g.FillEllipse(brush, r);
                        g.DrawEllipse(border, r);
                        g.DrawString(Letter, font, Brushes.White, r, center);
                    }
                }

                // Silent mark (bottom-left): small speaker with an X.
                if (Silent) DrawSilentMark(g, new RectangleF(circle.Left - 4 * s, circle.Bottom - 13 * s, 17 * s, 17 * s), s);

                // Unread badge (top-right).
                if (Unread > 0)
                {
                    string text = Unread > 99 ? "99+" : Unread.ToString();
                    using (var font = new Font("Segoe UI", 11 * s, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        var size = g.MeasureString(text, font);
                        float h = 18 * s;
                        float w = Math.Max(h, size.Width + 6 * s);
                        var rect = new RectangleF(circle.Right - w + 8 * s, circle.Top - 4 * s, w, h);
                        using (var path = Pill(rect))
                        using (var red = new SolidBrush(Color.FromArgb(234, 67, 53)))
                        using (var border = new Pen(BackColor, 2 * s))
                        {
                            g.FillPath(red, path);
                            g.DrawPath(border, path);
                        }
                        g.DrawString(text, font, Brushes.White, rect, center);
                    }
                }
            }
        }

        private void DrawMarkBackground(Graphics g, RectangleF r, float s)
        {
            using (var bg = new SolidBrush(Color.FromArgb(70, 85, 95)))
            using (var border = new Pen(BackColor, 2 * s))
            {
                g.FillEllipse(bg, r);
                g.DrawEllipse(border, r);
            }
        }

        private void DrawSilentMark(Graphics g, RectangleF r, float s)
        {
            DrawMarkBackground(g, r, s);
            float x = r.Left + r.Width * 0.18f, y = r.Top + r.Height / 2f, u = r.Width / 17f;
            using (var white = new SolidBrush(Color.White))
            using (var pen = new Pen(Color.White, 1.3f * s))
            {
                // speaker
                g.FillPolygon(white, new[]
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
