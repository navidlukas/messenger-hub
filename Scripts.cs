namespace MessengerHub
{
    /// <summary>JavaScript injected into every messenger page.</summary>
    internal static class Scripts
    {
        // ---------------------------------------------------------------- Auto-naming
        // Each returns the logged-in account's number/name as a string, or null if not logged in yet.
        // (No double-quote characters inside these scripts: \x22 is used instead.)

        /// <summary>WhatsApp Web keeps your own number in localStorage ("last-wid-md"), e.g. 971501234567:12@c.us.</summary>
        public const string WhatsAppNumber = @"
(() => {
  try {
    for (const key of ['last-wid-md', 'last-wid']) {
      const v = (localStorage.getItem(key) || '').replace(/\x22/g, '');
      const m = v.match(/^(\d{6,15})[:@]/);
      if (m) return '+' + m[1];
    }
  } catch (e) {}
  return null;
})()
";

        /// <summary>
        /// Telegram Web caches its state (including your own user: phone, name) either in localStorage
        /// (older versions) or in IndexedDB (newer versions). localStorage is checked right away; the
        /// IndexedDB search runs in the background and its result is picked up on the next check (10 s).
        /// </summary>
        public const string TelegramNumber = @"
(() => {
  const w = window;

  // Finds { currentUserId, users: { byId } } in a value (object or JSON text) and returns phone or name.
  const pick = (v) => {
    try {
      if (typeof v === 'string') {
        if (v.indexOf('currentUserId') < 0) return null;
        v = JSON.parse(v);
      }
      if (!v || typeof v !== 'object') return null;
      const id = v.currentUserId;
      const u = id && v.users && v.users.byId && v.users.byId[id];
      if (!u) return null;
      if (u.phoneNumber) return '+' + String(u.phoneNumber).replace(/\D/g, '');
      const name = [u.firstName, u.lastName].filter(Boolean).join(' ').trim();
      return name || null;
    } catch (e) { return null; }
  };

  if (w.__mhTgId) return w.__mhTgId;

  // 1) localStorage (older Telegram Web versions)
  try {
    for (let i = 0; i < localStorage.length; i++) {
      const r = pick(localStorage.getItem(localStorage.key(i)));
      if (r) return (w.__mhTgId = r);
    }
  } catch (e) {}

  // 2) IndexedDB (newer versions): background search, at most every 30 s.
  const now = Date.now();
  if (!w.__mhTgScan || now - w.__mhTgScan > 30000) {
    w.__mhTgScan = now;
    (async () => {
      const req = (r) => new Promise((ok) => { r.onsuccess = () => ok(r.result); r.onerror = () => ok(null); });
      let dbs = [];
      try { dbs = indexedDB.databases ? await indexedDB.databases() : []; } catch (e) {}
      for (const info of dbs) {
        if (!info || !info.name || w.__mhTgId) continue;
        let db = null;
        try {
          db = await new Promise((ok) => {
            const r = indexedDB.open(info.name);
            r.onsuccess = () => ok(r.result);
            r.onerror = () => ok(null);
            r.onblocked = () => ok(null);
          });
          if (!db) continue;
          for (const storeName of Array.from(db.objectStoreNames)) {
            if (w.__mhTgId) break;
            const keys = (await req(db.transaction(storeName).objectStore(storeName).getAllKeys(null, 500))) || [];
            for (const key of keys) {
              if (keys.length > 10 && !/global|state/i.test(String(key))) continue;
              const r = pick(await req(db.transaction(storeName).objectStore(storeName).get(key)));
              if (r) { w.__mhTgId = r; break; }
            }
          }
        } catch (e) {
        } finally {
          try { if (db) db.close(); } catch (e) {}
        }
      }
    })();
  }
  return null;
})()
";

        // ---------------------------------------------------------------- Unread counts
        // Returns the total unread message count as a number, or -1 if it can't be read.
        // Used for services whose browser tab title doesn't carry the count reliably.

        /// <summary>
        /// Telegram Web (web.telegram.org/a/) often doesn't put the unread count in the tab title,
        /// so read it from the chat list instead: sum the unread (non-muted) badges on private and
        /// group chats. Same approach other messenger wrappers use.
        /// </summary>
        public const string TelegramUnread = @"
(() => {
  try {
    const sel = '.chat-list .ListItem.private .ChatBadge.unread:not(.muted), .chat-list .ListItem.group .ChatBadge.unread:not(.muted)';
    const els = document.querySelectorAll(sel);
    let n = 0;
    for (const el of els) {
      const v = parseInt((el.textContent || '').replace(/[^0-9]/g, ''), 10);
      if (!isNaN(v)) n += v;
    }
    return n;
  } catch (e) { return -1; }
})()
";

        // ---------------------------------------------------------------- Readiness
        // Returns 'true' when the app is actually usable (not a half-loaded/stuck screen).

        /// <summary>
        /// WhatsApp is ready once the chat list pane (#pane-side) exists, or the QR login screen is
        /// showing. The intro/loading screen has neither, so a stuck first load reads as not-ready.
        /// </summary>
        public const string WhatsAppReady = @"
(() => {
  try {
    if (document.querySelector('#pane-side')) return true;  // chats loaded
    if (document.querySelector('canvas')) return true;      // QR login screen
    return false;
  } catch (e) { return false; }
})()
";

        /// <summary>
        /// Slack unread count. The redesigned Slack keeps DMs/mentions in a part of the sidebar the
        /// old selector didn't cover, so read the tab title first: Slack shows a count in parentheses
        /// for DMs/mentions, e.g. '(3) Slack', and a leading * or bullet for other unread channels.
        /// The title updates even while the tab is in the background. Falls back to the sidebar DOM.
        /// </summary>
        public const string SlackUnread = @"
(() => {
  try {
    const t = document.title || '';
    const m = t.match(/\((\d+)\+?\)/);
    if (m) return parseInt(m[1], 10);          // mentions / DMs count
    if (/^\s*[\*•]/.test(t)) return 1;         // other unread channels (no number)
    const sels = [
      '.p-channel_sidebar__channel--unread:not(.p-channel_sidebar__channel--muted)',
      '.p-channel_sidebar__badge',
      '[data-qa-unread=true]'
    ];
    for (let i = 0; i < sels.length; i++) {
      try { const n = document.querySelectorAll(sels[i]).length; if (n > 0) return n; } catch (e) {}
    }
    return 0;
  } catch (e) { return -1; }
})()
";

        /// <summary>
        /// LinkedIn: prefer the messaging badge in the top nav (unread conversations); if that can't
        /// be found, fall back to the count in the tab title (which also includes notifications).
        /// </summary>
        public const string LinkedInUnread = @"
(() => {
  try {
    const a = document.querySelector('a[href*=\x22/messaging\x22]');
    if (a) {
      const b = a.querySelector('.notification-badge__count, [class*=notification-badge]');
      if (b) { const v = parseInt((b.textContent || '').replace(/[^0-9]/g, ''), 10); if (!isNaN(v)) return v; }
    }
    const m = (document.title || '').match(/\((\d+)\+?\)/);
    if (m) return parseInt(m[1], 10);
    return 0;
  } catch (e) { return -1; }
})()
";

        /// <summary>
        /// Google Messages: count conversation rows marked unread. Its markup isn't documented, so
        /// this is best-effort (host 'unread' class or the data-e2e-is-unread marker); if neither is
        /// present it returns 0 rather than a wrong number.
        /// </summary>
        public const string GoogleMessagesUnread = @"
(() => {
  try {
    const items = document.querySelectorAll('mws-conversation-list-item');
    let n = 0;
    for (let i = 0; i < items.length; i++) {
      const it = items[i];
      if (it.classList.contains('unread') || it.querySelector('[data-e2e-is-unread=true]')) n++;
    }
    return n;
  } catch (e) { return -1; }
})()
";

        /// <summary>Facebook puts your profile name in the page data (CurrentUserInitialData ... NAME). Email is never on the page.</summary>
        public const string FacebookName = @"
(() => {
  try {
    for (const sc of document.querySelectorAll('script')) {
      const t = sc.textContent || '';
      const i = t.indexOf('CurrentUserInitialData');
      if (i < 0) continue;
      const m = t.slice(i, i + 4000).match(/\x22NAME\x22:\x22((?:[^\x22\\]|\\.)*)\x22/);
      if (m) return JSON.parse('\x22' + m[1] + '\x22');
    }
  } catch (e) {}
  return null;
})()
";

        /// <summary>
        /// Hides WhatsApp's "Turn on background sync" banner and the "Background sync" switch in
        /// its settings, so users don't turn on a feature that costs extra RAM/CPU.
        /// Lightweight: skips anything inside the open chat (where most changes happen) and checks
        /// new page parts in one batch at most every half second.
        /// Matches the English UI text; add other languages to the list if needed.
        /// </summary>
        public const string HideWhatsAppBackgroundSync = @"
(() => {
  if (window.__mhHideBgSync || !/(^|\.)whatsapp\.com$/.test(location.hostname)) return;
  window.__mhHideBgSync = true;

  const phrases = ['turn on background sync', 'background sync'];
  const isMatch = text => phrases.includes((text || '').trim().toLowerCase());
  const inChat = node => {
    const el = node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
    return !el || !!el.closest('#main');
  };

  const hideFrom = textNode => {
    let el = textNode.parentElement;
    if (!el) return;
    // Climb to the whole banner / settings row, but stop before big containers.
    while (el.parentElement && el.parentElement !== document.body &&
           (el.parentElement.textContent || '').length < 300) {
      el = el.parentElement;
    }
    el.style.setProperty('display', 'none', 'important');
  };

  const scan = root => {
    if (!root.isConnected || inChat(root)) return; // never touch the open chat
    if (root.nodeType === Node.TEXT_NODE) { if (isMatch(root.nodeValue)) hideFrom(root); return; }
    if (root.nodeType !== Node.ELEMENT_NODE) return;
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) if (isMatch(n.nodeValue)) hideFrom(n);
  };

  // Collect changed nodes and check them together, at most every 500 ms.
  const pending = new Set();
  let timer = 0;
  const flush = () => { timer = 0; pending.forEach(scan); pending.clear(); };

  const start = () => {
    scan(document.body);
    new MutationObserver(list => {
      for (const m of list) {
        if (m.type === 'characterData') pending.add(m.target);
        else m.addedNodes.forEach(n => pending.add(n));
      }
      if (!timer && pending.size) timer = setTimeout(flush, 500);
    }).observe(document.body, { childList: true, subtree: true, characterData: true });
  };

  if (document.body) start();
  else document.addEventListener('DOMContentLoaded', start);
})();
";

        /// <summary>
        /// Stops videos and GIFs from playing on their own. A video plays only if you clicked
        /// on it (or its play button) just before. Audio is NOT blocked, so voice messages
        /// and notification sounds keep working.
        /// </summary>
        public const string BlockVideoAutoplay = @"
(() => {
  if (window.__mhNoAutoplay) return;
  window.__mhNoAutoplay = true;

  let lastClick = 0, lastTarget = null;
  const allowed = new WeakSet();

  const mark = e => { lastClick = Date.now(); lastTarget = e.target; };
  window.addEventListener('pointerdown', mark, true);
  window.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') mark(e); }, true);

  // Was there a click within 1.5s on this video or something next to it (e.g. a play button)?
  const clickedNear = video => {
    if (Date.now() - lastClick > 1500 || !(lastTarget instanceof Node)) return false;
    let node = video;
    for (let i = 0; i < 8 && node; i++, node = node.parentElement) {
      if (node.contains(lastTarget)) return true;
    }
    return false;
  };

  const mayPlay = video => {
    if (video.srcObject) return true; // live camera/call video is never blocked
    if (allowed.has(video)) return true;
    if (clickedNear(video)) { allowed.add(video); return true; }
    return false;
  };

  // Videos started from page code.
  const originalPlay = HTMLMediaElement.prototype.play;
  HTMLMediaElement.prototype.play = function () {
    if (this instanceof HTMLVideoElement && !mayPlay(this)) {
      try { this.autoplay = false; } catch (e) {}
      return Promise.reject(new DOMException('Autoplay is turned off', 'NotAllowedError'));
    }
    return originalPlay.apply(this, arguments);
  };

  // Videos with the autoplay attribute start without calling play().
  document.addEventListener('play', e => {
    const v = e.target;
    if (v instanceof HTMLVideoElement && !mayPlay(v)) {
      try { v.autoplay = false; v.pause(); } catch (err) {}
    }
  }, true);
})();
";
    }
}
