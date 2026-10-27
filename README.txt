MESSENGER HUB  v0.8  (WhatsApp, Telegram, Messenger, WeChat, Google Messages, Slack)
=============================================================

BUILD (one time)
  1. Install the .NET SDK: https://dotnet.microsoft.com/download
  2. Double-click build.bat
  3. The finished app appears in: dist\MessengerHub

USING IT
  - Left sidebar: one round icon per account, showing the messenger's own site icon
    (the one on its browser tab). Hover an icon to see the account's name.
    Until the icon is saved the first time, a colored circle with a letter shows instead
    (W=WhatsApp, T=Telegram, M=Messenger, C=WeChat, G=Google Messages, S=Slack).
  - Drag an icon up or down to change the order.
  - Click "+" and choose WhatsApp, Telegram, Messenger, WeChat, Google Messages or Slack to add an account.
    After you log in, it is named automatically: phone number for WhatsApp and
    Telegram, profile name for Messenger. Names you choose yourself are never changed.
    Adding the same account twice is blocked: if a newly added WhatsApp/Telegram/Messenger
    logs in as one you already have, the duplicate is removed automatically.
  - Right-click an icon for:
      Silent (no sound)       - the account makes no sound while in the background
      Rename / Reload / Remove
    The same options are in the top menu under "Account" (for the selected account).
  - Notifications are handled by each messenger itself (native pop-ups with preview); turn an
    individual one on/off from its own notification settings or the gear on its pop-up.
  - Menu > Silent (also on the tray right-click) mutes ALL accounts at once; turn it off
    to restore each account's own setting. It mutes sound only; the red dot still shows. The tray icon shows a crescent-moon badge while on.
  - Downloads: you are asked where to save each file.
  - The tray icon shows a red dot (top-right) whenever any account has an unread message.
  - The X button hides the app to the system tray. Tray icon > right-click > Exit to quit.
  - Saving RAM/CPU: background accounts use low-memory mode, refresh themselves every
    3 hours, and videos/GIFs only play when you click them.
  - Cache stays bounded on its own: the app measures each account's cache after a 2-minute
    warm-up and sets that account's limit to about twice that size (never below 300 MB). On
    exit, an account over its limit has its media cache cleared (app files and logins are kept).
    It re-measures after a manual Clear cache. Nothing to configure.
  - Menu > Clear cache...  deletes cached media but KEEPS your logins.
  - Menu > Reset Messenger Hub...  removes all accounts and settings (back to defaults).

AUTOMATIC UPDATES
  - The app checks for a new version about once a week, in the background. If one is
    found it downloads quietly (into data\update) while you keep working - no pop-ups.
  - The new version is installed only when you quit (tray > Exit), or the next time you
    start the app. Your accounts, logins and settings in data\ are never touched.
  - If an install ever fails it rolls back to the current version automatically. A log
    is kept at data\update\update.log.
  - Updates come from this app's own GitHub releases; nothing else is sent anywhere.
  - NOTE: automatic updating starts working from v0.8 onward. To move from an older
    build to v0.8, download it once from the Releases page and unzip over your folder
    (keep your data\ folder). After that, updates are hands-off.

PASSWORD & MOVING TO ANOTHER PC
  - First run asks you to set a password. You enter it each time you START the app
    (not when reopening from the tray). There is NO recovery: if you forget it, the
    only option is Reset, which wipes all accounts and starts fresh.
  - While the app is closed, your account logins (cookies, Local Storage, IndexedDB)
    are encrypted with that password. Someone who copies your folder cannot open your
    accounts or read messages without the password.
  - Because of this you can copy the whole folder to another PC and it just works:
    logins (including Facebook) survive the move once you enter your password.
  - Protection applies only while the app is CLOSED. While it is running (including in
    the tray), the logins are unlocked on disk.

  IMPORTANT: test with ONE throwaway account first before trusting it with all of them.

PORTABLE
  Copy the whole "MessengerHub" folder anywhere (USB stick, another PC).
  Everything is kept inside it:

    MessengerHub\
      MessengerHub.exe + DLL files
      data\
        WebView2\     <- logins, cookies, cache (one profile per account)
        icons\        <- saved site icons for the sidebar
        settings.ini  <- accounts, names, options, window size/position
                         (settings.ini.bak = automatic backup of the previous version)
        security.dat  <- password check + encryption key (safe to copy; useless without your password)
        webview.key   <- portable cookie key, so Facebook stays logged in after a move
        update\       <- temporary space used by the auto-updater (safe to delete)

  Close the app completely (tray > Exit) before copying the folder.

NOTES
  - Facebook Messenger opens facebook.com/messages (Meta closed messenger.com in 2026).

REQUIREMENTS
  Windows 10 or 11 (64-bit) with the Microsoft Edge WebView2 Runtime
  (already included in Windows 11 and most Windows 10 PCs).
  Don't put the folder in "Program Files" (the app must be able to write next to itself).
