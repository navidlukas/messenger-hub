using System.Drawing;
using System.Windows.Forms;

namespace MessengerHub
{
    internal static class Dialogs
    {
        private static Form NewDialog(string title)
        {
            var f = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14),
                Font = new Font("Segoe UI", 9f),
            };
            f.AutoScaleDimensions = new SizeF(96f, 96f);
            f.AutoScaleMode = AutoScaleMode.Dpi;
            return f;
        }

        private static FlowLayoutPanel Buttons(Form f, string okText)
        {
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 14, 0, 0),
            };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
            var ok = new Button { Text = okText, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 0) };
            panel.Controls.Add(cancel);
            panel.Controls.Add(ok);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            return panel;
        }

        private static TableLayoutPanel Layout() =>
            new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };

        /// <summary>Simple text input. Returns null when cancelled.</summary>
        public static string Prompt(IWin32Window owner, string title, string text, string value)
        {
            using (var f = NewDialog(title))
            {
                var layout = Layout();
                var box = new TextBox { Text = value, Width = 300, Margin = new Padding(0, 6, 0, 0) };
                layout.Controls.Add(new Label { Text = text, AutoSize = true });
                layout.Controls.Add(box);
                layout.Controls.Add(Buttons(f, "OK"));
                f.Controls.Add(layout);
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }

        /// <summary>Prompts for a Custom website URL and optional display name. Returns false when cancelled.</summary>
        public static bool PromptCustomSite(IWin32Window owner, out string normalizedUrl, out string customName)
        {
            normalizedUrl = null;
            customName = null;
            using (var f = NewDialog("Add custom website"))
            {
                var layout = Layout();
                var urlBox = new TextBox { Width = 340, Margin = new Padding(0, 6, 0, 12) };
                var nameBox = new TextBox { Width = 340, Margin = new Padding(0, 6, 0, 0) };

                layout.Controls.Add(new Label { Text = "Website URL (e.g. discord.com, mail.google.com):", AutoSize = true });
                layout.Controls.Add(urlBox);
                layout.Controls.Add(new Label { Text = "Name (optional):", AutoSize = true });
                layout.Controls.Add(nameBox);
                layout.Controls.Add(Buttons(f, "Add"));
                f.Controls.Add(layout);

                string validUrl = null;
                f.FormClosing += (s, e) =>
                {
                    if (f.DialogResult != DialogResult.OK) return;
                    validUrl = Account.NormalizeUrl(urlBox.Text);
                    if (validUrl == null)
                    {
                        MessageBox.Show(f, "Please enter a valid website URL (for example: discord.com).",
                            "Messenger Hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        urlBox.Focus();
                        e.Cancel = true;
                    }
                };

                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                normalizedUrl = validUrl;
                customName = nameBox.Text.Trim();
                return true;
            }
        }

        /// <summary>Prompts to change an existing Custom tab's pinned starting URL. Returns null when cancelled.</summary>
        public static string PromptCustomUrl(IWin32Window owner, string currentUrl)
        {
            using (var f = NewDialog("Change website URL"))
            {
                var layout = Layout();
                var urlBox = new TextBox { Text = currentUrl ?? "", Width = 340, Margin = new Padding(0, 6, 0, 0) };
                layout.Controls.Add(new Label { Text = "Website URL:", AutoSize = true });
                layout.Controls.Add(urlBox);
                layout.Controls.Add(Buttons(f, "Save"));
                f.Controls.Add(layout);

                string validUrl = null;
                f.FormClosing += (s, e) =>
                {
                    if (f.DialogResult != DialogResult.OK) return;
                    validUrl = Account.NormalizeUrl(urlBox.Text);
                    if (validUrl == null)
                    {
                        MessageBox.Show(f, "Please enter a valid website URL (for example: discord.com).",
                            "Messenger Hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        urlBox.Focus();
                        e.Cancel = true;
                    }
                };

                return f.ShowDialog(owner) == DialogResult.OK ? validUrl : null;
            }
        }

        /// <summary>Asks before clearing the cache. Returns false when cancelled.</summary>
        public static bool ConfirmClearCache(IWin32Window owner, string dataSize)
        {
            using (var f = NewDialog("Clear cache"))
            {
                var layout = Layout();
                layout.Controls.Add(new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(380, 0),
                    Text =
                        "This deletes cached images, videos and files from all accounts.\n\n" +
                        "Your logins are kept, so you won't need to log in or scan QR codes again. " +
                        "Media downloads again when you open a chat.\n\n" +
                        "Current data folder size: " + dataSize,
                });
                layout.Controls.Add(Buttons(f, "Clear cache"));
                f.Controls.Add(layout);

                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }
}
