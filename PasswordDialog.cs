using System;
using System.Drawing;
using System.Windows.Forms;

namespace MessengerHub
{
    internal enum UnlockResult { Unlocked, Reset, Cancel }

    /// <summary>The password screens shown before the main window opens.</summary>
    internal static class PasswordDialog
    {
        private const string AppName = "Messenger Hub";
        private const int MinLength = 4;

        private static Form NewForm(string title)
        {
            var f = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = true,
                StartPosition = FormStartPosition.CenterScreen,
                ClientSize = new Size(420, 10),
                Padding = new Padding(18),
                Font = new Font("Segoe UI", 9.5f),
                BackColor = Color.White,
            };
            f.AutoScaleDimensions = new SizeF(96f, 96f);
            f.AutoScaleMode = AutoScaleMode.Dpi;
            try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            return f;
        }

        private static Label Info(string text, int top) => new Label
        {
            Text = text,
            AutoSize = false,
            Location = new Point(18, top),
            Size = new Size(384, 60),
            ForeColor = Color.FromArgb(70, 70, 70),
        };

        /// <summary>First run: force the user to create a password. Returns null if they close the window.</summary>
        public static string CreatePassword()
        {
            using (var f = NewForm("Set a password"))
            {
                var info = Info(
                    "Messenger Hub locks your account logins with a password.\n" +
                    "You'll enter it each time you start the app.\n" +
                    "There is NO way to recover it — if you forget it, the only option is to reset.",
                    6);
                info.Size = new Size(384, 72);

                var lbl1 = new Label { Text = "Password:", AutoSize = true, Location = new Point(18, 88) };
                var box1 = new TextBox { UseSystemPasswordChar = true, Location = new Point(140, 85), Width = 262 };
                var lbl2 = new Label { Text = "Confirm password:", AutoSize = true, Location = new Point(18, 120) };
                var box2 = new TextBox { UseSystemPasswordChar = true, Location = new Point(140, 117), Width = 262 };

                var show = new CheckBox { Text = "Show password", AutoSize = true, Location = new Point(140, 146) };
                show.CheckedChanged += (s, e) => box1.UseSystemPasswordChar = box2.UseSystemPasswordChar = !show.Checked;

                var error = new Label { AutoSize = false, Location = new Point(18, 174), Size = new Size(384, 20), ForeColor = Color.Firebrick };

                var ok = new Button { Text = "Set password", DialogResult = DialogResult.None, Location = new Point(242, 200), Size = new Size(160, 30) };
                var cancel = new Button { Text = "Exit", DialogResult = DialogResult.Cancel, Location = new Point(156, 200), Size = new Size(80, 30) };

                ok.Click += (s, e) =>
                {
                    if (box1.Text.Length < MinLength) { error.Text = "Use at least " + MinLength + " characters."; return; }
                    if (box1.Text != box2.Text) { error.Text = "The two passwords don't match."; return; }
                    f.DialogResult = DialogResult.OK;
                };

                f.Controls.AddRange(new Control[] { info, lbl1, box1, lbl2, box2, show, error, ok, cancel });
                f.ClientSize = new Size(420, 244);
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                return f.ShowDialog() == DialogResult.OK ? box1.Text : null;
            }
        }

        /// <summary>
        /// Strong confirmation for a forgotten-password reset: the user must type RESET, so a stray
        /// click (or pressing Enter) can never wipe their accounts. Returns true only on explicit confirm.
        /// </summary>
        private static bool ConfirmReset(IWin32Window owner)
        {
            Func<string, bool> IsReset = t => t.Trim().Equals("RESET", StringComparison.OrdinalIgnoreCase);

            using (var f = NewForm("Confirm reset"))
            {
                var warn = new Label
                {
                    AutoSize = false,
                    Location = new Point(18, 16),
                    Size = new Size(384, 66),
                    ForeColor = Color.FromArgb(40, 40, 40),
                    Text = "This permanently removes ALL accounts, logins and settings and starts the " +
                           "app fresh. It cannot be undone. Only do this if you've truly forgotten your password.",
                };
                var instruction = new Label
                {
                    AutoSize = false,
                    Location = new Point(18, 92),
                    Size = new Size(384, 24),
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    Text = "To confirm, type the word  RESET  here:",
                };
                var box = new TextBox { Location = new Point(18, 120), Width = 384 };
                var hint = new Label
                {
                    AutoSize = false,
                    Location = new Point(18, 148),
                    Size = new Size(384, 20),
                    ForeColor = Color.Gray,
                    Text = "The \"Reset everything\" button turns on once you type it.",
                };

                var ok = new Button { Text = "Reset everything", Location = new Point(222, 178), Size = new Size(180, 32), Enabled = false, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Location = new Point(18, 178), Size = new Size(100, 32), DialogResult = DialogResult.Cancel };

                box.TextChanged += (s, e) => ok.Enabled = IsReset(box.Text);

                f.Controls.AddRange(new Control[] { warn, instruction, box, hint, ok, cancel });
                f.ClientSize = new Size(420, 226);
                f.AcceptButton = null;        // Enter alone can't confirm
                f.CancelButton = cancel;      // Esc cancels

                return f.ShowDialog(owner) == DialogResult.OK && IsReset(box.Text);
            }
        }

        /// <summary>Every launch: ask for the password. Offers a reset for a forgotten password.</summary>
        public static UnlockResult EnterPassword(Func<string, bool> tryUnlock)
        {
            using (var f = NewForm("Enter password"))
            {
                var lbl = new Label { Text = "Password:", AutoSize = true, Location = new Point(18, 20) };
                var box = new TextBox { UseSystemPasswordChar = true, Location = new Point(100, 17), Width = 302 };
                var show = new CheckBox { Text = "Show", AutoSize = true, Location = new Point(100, 46) };
                show.CheckedChanged += (s, e) => box.UseSystemPasswordChar = !show.Checked;

                var error = new Label { AutoSize = false, Location = new Point(18, 72), Size = new Size(384, 20), ForeColor = Color.Firebrick };

                var ok = new Button { Text = "Unlock", Location = new Point(322, 100), Size = new Size(80, 30) };
                var reset = new Button { Text = "Forgot? Reset", Location = new Point(18, 100), Size = new Size(120, 30) };
                var cancel = new Button { Text = "Exit", DialogResult = DialogResult.Cancel, Location = new Point(236, 100), Size = new Size(80, 30) };

                var result = UnlockResult.Cancel;

                ok.Click += (s, e) =>
                {
                    if (tryUnlock(box.Text)) { result = UnlockResult.Unlocked; f.DialogResult = DialogResult.OK; }
                    else { error.Text = "Wrong password. Try again."; box.SelectAll(); box.Focus(); }
                };
                reset.Click += (s, e) =>
                {
                    if (ConfirmReset(f)) { result = UnlockResult.Reset; f.DialogResult = DialogResult.OK; }
                };

                f.Controls.AddRange(new Control[] { lbl, box, show, error, ok, reset, cancel });
                f.ClientSize = new Size(420, 148);
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                f.ShowDialog();
                return result;
            }
        }
    }
}
