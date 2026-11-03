namespace MessengerHub
{
    /// <summary>JavaScript injected into every messenger page.</summary>
    internal static class Scripts
    {
        // ---------------------------------------------------------------- Auto-naming
        // Each returns the logged-in account's number/name as a string, or null if not logged in yet.
        // Double quotes inside these scripts are written as \x22.

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
    const direct = pick(localStorage.getItem('tt-global-state'));
    if (direct) return (w.__mhTgId = direct);
    for (let i = 0; i < localStorage.length; i++) {
      const k = localStorage.key(i);
      if (k === 'tt-global-state') continue;
      const r = pick(localStorage.getItem(k));
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
        /// Telegram Web unread count supporting both Telegram Web K (web.telegram.org/k/, .chatlist-chat /
        /// .dialog-subtitle-badge) and Telegram Web A (web.telegram.org/a/, legacy .ChatBadge and modern
        /// CSS-Modules builds), plus chat folders and @mention badges, while excluding muted non-mention
        /// chats and archived chats.
        /// </summary>
        public const string TelegramUnread = @"
(window.__mhTgUnread || (window.__mhTgUnread = () => {
  try {
    const parseNum = (s) => {
      const t = (s || '').trim();
      if (!t) return 0;
      const m = t.match(/^(\d+(?:[.,]\d+)?)\s*([KkMm])?\+?$/);
      if (m) {
        let v = parseFloat(m[1].replace(',', '.'));
        const u = (m[2] || '').toUpperCase();
        if (u === 'K') v *= 1000;
        else if (u === 'M') v *= 1000000;
        return Math.round(v);
      }
      const d = parseInt(t.replace(/[^0-9]/g, ''), 10);
      return isNaN(d) ? 0 : d;
    };

    const isShown = (el) => {
      if (!el || el.classList.contains('hide') || el.classList.contains('hidden')) return false;
      if (el.classList.contains('dialog-subtitle-badge') && !el.classList.contains('is-visible')) return false;
      const cs = getComputedStyle(el);
      return cs.display !== 'none' && cs.visibility !== 'hidden' && parseFloat(cs.opacity || '1') > 0.1;
    };

    const isColoredBadge = (el, rowMuted) => {
      const cs = getComputedStyle(el);
      const m = (cs.backgroundColor || '').match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*([\d.]+))?\s*\)/);
      if (!m) return false;
      const r = +m[1], g = +m[2], b = +m[3], a = m[4] === undefined ? 1 : +m[4];
      if (a <= 0.35) return false;
      const spread = Math.max(r, g, b) - Math.min(r, g, b);
      if (spread > 22) return true;
      return !rowMuted && r > 235 && g > 235 && b > 235 && a > 0.85;
    };

    let total = 0;

    // 1) Telegram Web K (web.telegram.org/k/): #folders-container .chatlist-chat rows
    const webKRoot = document.querySelector('#folders-container') || document.querySelector('#chatlist-container');
    const webKRows = webKRoot ? webKRoot.querySelectorAll('.chatlist-chat') : [];
    if (webKRows.length > 0) {
      const seen = new Set();
      for (let i = 0; i < webKRows.length; i++) {
        const row = webKRows[i];
        if (row.classList.contains('is-archive') || row.closest('.is-archive, #archive-chats')) continue;
        const pid = row.getAttribute('data-peer-id');
        if (pid === '0') continue;
        if (pid) { if (seen.has(pid)) continue; seen.add(pid); }

        const rowMuted = row.classList.contains('is-muted') || !!row.querySelector('.dialog-muted-icon, .icon-muted, .tgico-mute');
        let rowCount = 0;

        const ub = row.querySelector(
          '.dialog-subtitle-badge-unread, .unread-badge, .dialog-subtitle-badge.unread, ' +
          '.dialog-subtitle-badge:not(.dialog-subtitle-badge-mention):not(.dialog-subtitle-badge-reaction):not(.mention):not(.reaction)'
        );
        if (ub && isShown(ub)) {
          const badgeMuted = rowMuted || ub.classList.contains('is-muted') ||
            ub.classList.contains('badge-gray') || ub.classList.contains('back-gray');
          if (!badgeMuted && isColoredBadge(ub, rowMuted)) {
            const v = parseNum(ub.textContent);
            if (v > 0) {
              rowCount = v;
            } else if ((ub.classList.contains('unread') || ub.classList.contains('dialog-subtitle-badge-unread')) &&
                       !/pin/i.test(ub.className)) {
              rowCount = 1; // empty visible unmuted badge = marked as unread
            }
          }
        }

        const mb = row.querySelector('.dialog-subtitle-badge-mention, .mention-badge, .dialog-subtitle-badge.mention');
        if (mb && isShown(mb) && rowCount === 0) {
          rowCount = 1; // direct @mention or reply (counts even in muted groups)
        }

        total += rowCount;
      }

      // Check 'All Chats' folder tab unmuted badge in case unread chats are scrolled out of virtualized DOM
      const allTabBadge = document.querySelector(
        '#folders-tabs .menu-horizontal-div-item:first-child .badge:not(.badge-gray):not(.is-muted), ' +
        '.chatfolders-container .menu-horizontal-div-item:first-child .badge:not(.badge-gray):not(.is-muted)');
      if (allTabBadge && isShown(allTabBadge) && isColoredBadge(allTabBadge, false)) {
        const tv = parseNum(allTabBadge.textContent);
        if (tv > total) total = tv;
      }
    } else {
      // 2) Telegram Web A (web.telegram.org/a/): legacy .ChatBadge and modern CSS-Modules builds
      const leftRoot = document.querySelector('#LeftColumn-main') ||
                       document.querySelector('#LeftColumn') ||
                       document.querySelector('.chat-list');

      // 2a) Legacy global .ChatBadge classes
      const legacyBadges = (leftRoot || document).querySelectorAll('.chat-list .ChatBadge.unread:not(.muted)');
      if (legacyBadges.length > 0 || document.querySelector('.ChatBadge')) {
        for (let i = 0; i < legacyBadges.length; i++) {
          const el = legacyBadges[i];
          if (el.closest('.ArchivedChats, [id*=\x22archive\x22 i]')) continue;
          const v = parseNum(el.textContent);
          total += v > 0 ? v : 1;
        }
        const mentions = (leftRoot || document).querySelectorAll('.chat-list .ChatBadge.unread-mentions');
        for (let i = 0; i < mentions.length; i++) {
          const el = mentions[i];
          if (el.closest('.ArchivedChats, [id*=\x22archive\x22 i]')) continue;
          const row = el.closest('.ListItem, .Chat, .chat-item-clickable');
          if (!row || !row.querySelector('.ChatBadge.unread:not(.muted)')) total += 1;
        }
      } else if (leftRoot) {
        // 2b) Modern CSS-Modules Telegram Web A: inspect chat rows structurally + computed badge color
        const rows = leftRoot.querySelectorAll('.ListItem.Chat, .chat-item-clickable, .ListItem[data-peer-id], [class*=\x22chat-item-clickable\x22]');
        const seen = new Set();
        for (let i = 0; i < rows.length; i++) {
          const row = rows[i];
          if (row.closest('.ArchivedChats, [class*=\x22Archive\x22], [id*=\x22archive\x22 i]')) continue;
          const a = row.querySelector('a[href*=\x22#\x22]');
          const key = row.getAttribute('data-peer-id') || (a && a.getAttribute('href')) || '';
          if (key) { if (seen.has(key)) continue; seen.add(key); }

          const rowMuted = !!row.querySelector('.icon-muted, [class*=\x22icon-muted\x22]');
          let rowCount = 0;

          const nodes = row.querySelectorAll('div, span');
          for (let j = 0; j < nodes.length; j++) {
            const el = nodes[j];
            if (el.children.length > 0) continue;
            if (el.closest('[class*=\x22Avatar\x22], [class*=\x22avatar\x22]')) continue;
            const txt = (el.textContent || '').trim();
            if (txt && !/^\d+(?:[.,]\d+)?[KkMm]?\+?$/.test(txt)) continue;
            // Size first (cheap); only badge-sized elements get the computed-style checks.
            let h = el.offsetHeight, w = el.offsetWidth, cs = null;
            if (!h && !w) { cs = getComputedStyle(el); h = parseFloat(cs.height) || 0; w = parseFloat(cs.width) || 0; }
            if (h < 14 || h > 30 || w < 14 || w > 80) continue;
            if (!cs) cs = getComputedStyle(el);
            if (parseFloat(cs.borderTopLeftRadius) < 6) continue;
            if (!isShown(el) || !isColoredBadge(el, rowMuted)) continue;
            if (!rowMuted) {
              const v = parseNum(txt);
              rowCount += v > 0 ? v : 1;
            }
          }

          if (rowCount === 0 && row.querySelector('.icon-mention, [class*=\x22icon-mention\x22]')) {
            rowCount = 1;
          }
          total += rowCount;
        }
      }
    }

    // 3) Fallback from document.title if DOM has not rendered rows yet and Telegram set (N) in title
    if (total === 0) {
      const tm = (document.title || '').match(/\((\d+)\+?\)/);
      if (tm) {
        const tv = parseInt(tm[1], 10);
        if (!isNaN(tv) && tv > 0) total = tv;
      }
    }

    return total;
  } catch (e) { return -1; }
}))()
";

        // ---------------------------------------------------------------- Readiness
        // Returns 'true' when the app is actually usable (not a half-loaded/stuck screen).

        /// <summary>
        /// WhatsApp is ready once the chat list pane exists, the QR login screen is showing,
        /// or an active initial message download/sync is in progress (must not be interrupted).
        /// </summary>
        public const string WhatsAppReady = @"
(() => {
  try {
    if (document.querySelector('#pane-side, [data-testid=\x22chat-list\x22], [data-testid=\x22pane-side\x22]')) return true;  // chats loaded
    if (document.querySelector('canvas')) return true;      // QR login screen
    if (document.querySelector('progress, [role=\x22progressbar\x22], [data-testid=\x22app-progress-bar\x22], [data-testid=\x22loading-screen\x22]')) return true; // actively loading/syncing
    const text = (document.body && document.body.innerText) || '';
    if (/downloading|syncing|organizing/i.test(text)) return true; // active sync in progress
    return false;
  } catch (e) { return false; }
})()
";

        /// <summary>
        /// Slack: scrape unread counts across IA4 navigation rail tabs, active chat unread dividers,
        /// channel sidebar / DMs list items, and document title / rail notification dots.
        /// </summary>
        public const string SlackUnread = @"
(() => {
  try {
    const isDocBackgrounded = (typeof document.hidden === 'boolean' && document.hidden) ||
                              (!document.body || document.body.offsetWidth === 0);

    const isActive = (typeof window.__mhActive === 'boolean')
      ? window.__mhActive
      : (!isDocBackgrounded && document.hasFocus());

    const isVisible = el => {
      if (!el) return false;
      try {
        if (!isDocBackgrounded && el.offsetParent === null && el.offsetWidth === 0 && el.offsetHeight === 0) return false;
        if (el.classList.contains('c-mention_badge--hidden') || el.classList.contains('p-channel_sidebar__badge--hidden')) return false;
        const style = window.getComputedStyle(el);
        if (style.display === 'none' || style.visibility === 'hidden' || parseFloat(style.opacity || '1') === 0) return false;
        return true;
      } catch (e) { return true; }
    };

    let numericCount = 0;

    // 1. Document title numeric count: e.g. '(1) Slack' or '(*2) AG | Workspace | Slack'
    const t = (document.title || '').trim();
    const tm = t.match(/\(\*?(\d+)\+?\)/);
    if (tm) {
      const tc = parseInt(tm[1], 10);
      if (!isNaN(tc) && tc > 0) numericCount = Math.max(numericCount, tc);
    }

    // 2. IA4 Navigation Rail tab mention badges (Home, DMs, Activity, Later)
    const railTabs = document.querySelectorAll(
      '[data-qa=\x22tab_dms\x22], [data-qa=\x22tab_activity\x22], [data-qa=\x22tab_home\x22], [data-qa=\x22tab_later\x22], ' +
      '[data-qa^=\x22tab_\x22], .p-tab_rail [role=\x22tab\x22], .p-tab_rail button'
    );
    let railBadges = 0;
    for (let i = 0; i < railTabs.length; i++) {
      const tab = railTabs[i];
      const badges = tab.querySelectorAll('.c-mention_badge, .p-tab_rail__tab_badge, [data-qa$=\x22_badge\x22]');
      for (let j = 0; j < badges.length; j++) {
        const b = badges[j];
        if (!isVisible(b)) continue;
        const v = parseInt((b.textContent || '').replace(/[^0-9]/g, ''), 10);
        if (!isNaN(v) && v > 0) railBadges += v;
      }
      const al = tab.getAttribute('aria-label') || '';
      const m = al.match(/(\d+)\s+(?:unread|mention|new message)/i);
      if (m) {
        const v = parseInt(m[1], 10);
        if (!isNaN(v) && v > 0) railBadges = Math.max(railBadges, v);
      }
    }
    numericCount = Math.max(numericCount, railBadges);

    // 3. Channel sidebar and Direct Messages list mention badges:
    const sbBadges = document.querySelectorAll(
      '.p-channel_sidebar .c-mention_badge:not(.c-mention_badge--hidden), ' +
      '.p-channel_sidebar .p-channel_sidebar__badge--visible, ' +
      '[data-qa=\x22channel-sidebar\x22] .c-mention_badge'
    );
    let sidebarSum = 0;
    for (let i = 0; i < sbBadges.length; i++) {
      const b = sbBadges[i];
      if (!isVisible(b)) continue;
      if (b.closest('[class*=\x22avatar\x22 i], [class*=\x22presence\x22 i]')) continue;
      const v = parseInt((b.textContent || '').replace(/[^0-9]/g, ''), 10);
      if (!isNaN(v) && v > 0) sidebarSum += v;
    }
    numericCount = Math.max(numericCount, sidebarSum);

    // 4. Workspace switcher badges for multi-workspace setups
    const wsBadges = document.querySelectorAll(
      '[data-qa*=\x22team-button\x22 i] .c-mention_badge, [data-qa*=\x22team_button\x22 i] .c-mention_badge'
    );
    for (let i = 0; i < wsBadges.length; i++) {
      const b = wsBadges[i];
      if (!isVisible(b)) continue;
      const v = parseInt((b.textContent || '').replace(/[^0-9]/g, ''), 10);
      if (!isNaN(v) && v > 0) numericCount = Math.max(numericCount, v);
    }

    if (numericCount > 0) return numericCount;

    // 5. Unread channels or DM conversations (sidebar rows or dedicated DMs list).
    // The conversation that is open counts only while this tile is in the background
    // (while you are looking at it, it is being read).
    const unreadCandidates = document.querySelectorAll(
      '.p-channel_sidebar [data-qa-channel-sidebar-channel-is-unread=\x22true\x22]:not([data-qa-channel-sidebar-channel-is-muted=\x22true\x22]), ' +
      '.p-channel_sidebar__channel--unread:not(.p-channel_sidebar__channel--muted), ' +
      '[data-qa=\x22virtual-list-item\x22][aria-label*=\x22unread\x22 i]:not([aria-label*=\x22mark\x22 i]), ' +
      '[role=\x22listitem\x22][aria-label*=\x22unread\x22 i]:not([aria-label*=\x22mark\x22 i]), ' +
      '[role=\x22treeitem\x22][aria-label*=\x22unread\x22 i]:not([aria-label*=\x22mark\x22 i])'
    );
    const isSelected = el => !!el && (el.getAttribute('aria-selected') === 'true' ||
      /--selected\b/.test(el.className || '') || !!el.querySelector('[aria-selected=\x22true\x22], [class*=\x22--selected\x22]'));
    const seenRows = new Set();
    let unreadCount = 0;
    for (let i = 0; i < unreadCandidates.length; i++) {
      const el = unreadCandidates[i];
      if (!isVisible(el)) continue;
      const row = el.closest('[data-qa=\x22virtual-list-item\x22], [role=\x22listitem\x22], [role=\x22treeitem\x22], .p-channel_sidebar__channel') || el;
      if (seenRows.has(row)) continue;
      seenRows.add(row);
      if (isActive && (isSelected(el) || isSelected(row))) continue;
      unreadCount++;
    }

    // 6. Active/selected conversation unread check:
    // When Slack is running in the background (!isActive), an incoming message in the
    // currently selected conversation is unread to the user. Count it as 1 unread conversation.
    // When Slack IS active/foreground (isActive), the user is actively viewing this conversation,
    // so it must NEVER trigger or hold an unread badge.
    if (!isActive) {
      const activeUnreadDividers = document.querySelectorAll(
        '[data-qa=\x22unread_divider\x22], .c-message_list__unread_divider, [class*=\x22unread_divider\x22], ' +
        '[data-qa=\x22unread_pill\x22], [data-qa*=\x22unread_banner\x22], .c-message_list__unread_pill'
      );
      let hasActiveUnread = false;
      for (let i = 0; i < activeUnreadDividers.length; i++) {
        const ud = activeUnreadDividers[i];
        if (!isVisible(ud)) continue;
        const label = (ud.textContent || '').trim();
        if (/\b(?:new|unread)\b/i.test(label)) {
          hasActiveUnread = true;
          break;
        }
      }
      if (hasActiveUnread && unreadCount === 0) {
        unreadCount++;
      } else if (unreadCount === 0 && (/^\s*[\*•●!]/.test(t) || /[\*•●!]\s*$/.test(t))) {
        // Document title marker while backgrounded
        unreadCount++;
      }
    }

    if (unreadCount > 0) return unreadCount;

    // 7. Visible notification dot on rail tabs (Home, DMs, Activity)
    const railDots = document.querySelectorAll(
      '.p-tab_rail__notification_dot, .p-tab_rail__tab_dot, .p-ia4_home_button--unread'
    );
    for (let i = 0; i < railDots.length; i++) {
      if (isVisible(railDots[i])) return 1;
    }

    return 0;
  } catch (e) {
    return 0;
  }
})()
";

        /// <summary>Slack: check if the client workspace UI (navigation rail, channel sidebar, or message pane) is rendered.</summary>
        public const string SlackReady = @"
(() => {
  try {
    return !!document.querySelector('[data-qa*=\x22tab_\x22], .p-channel_sidebar, [data-qa=\x22channel-sidebar\x22], [data-qa*=\x22message\x22 i], .c-message_list');
  } catch (e) {
    return true;
  }
})()
";

        /// <summary>
        /// LinkedIn: check the top-nav Messaging badge (including aria-label counts) and unread conversation
        /// cards on /messaging/, falling back to document.title only if the messaging nav isn't rendered.
        /// </summary>
        public const string LinkedInUnread = @"
(window.__mhLiUnread || (window.__mhLiUnread = () => {
  try {
    let navCount = 0;
    const links = document.querySelectorAll('a[href*=\x22/messaging\x22]');
    for (let i = 0; i < links.length; i++) {
      const container = links[i].closest('li, .global-nav__primary-item') || links[i];
      const b = container.querySelector('.notification-badge--show .notification-badge__count, .notification-badge__count, [class*=\x22notification-badge\x22]');
      if (b) {
        const cs = getComputedStyle(b);
        if (cs.display !== 'none' && cs.visibility !== 'hidden') {
          const v = parseInt((b.textContent || '').replace(/[^0-9]/g, ''), 10);
          if (!isNaN(v) && v > navCount) navCount = v;
        }
      }
      const al = (links[i].getAttribute('aria-label') || '') + ' ' +
                 ((container.getAttribute && container.getAttribute('aria-label')) || '');
      const am = al.match(/(\d+)\s+(?:new|unread)/i);
      if (am) {
        const av = parseInt(am[1], 10);
        if (!isNaN(av) && av > navCount) navCount = av;
      }
    }

    let listCount = 0;
    const cards = document.querySelectorAll('.msg-conversation-card__unread-count, .msg-conversation-listitem .notification-badge__count');
    for (let i = 0; i < cards.length; i++) {
      const cs = getComputedStyle(cards[i]);
      if (cs.display === 'none' || cs.visibility === 'hidden') continue;
      const v = parseInt((cards[i].textContent || '').replace(/[^0-9]/g, ''), 10);
      if (!isNaN(v) && v > 0) listCount += v;
    }

    const best = Math.max(navCount, listCount);
    if (best > 0) return best;

    if (links.length === 0) {
      const m = (document.title || '').match(/\((\d+)\+?\)/);
      if (m) return parseInt(m[1], 10);
    }
    return 0;
  } catch (e) { return -1; }
}))()
";

        /// <summary>
        /// Facebook Messenger (facebook.com/messages): reads (N) from document.title and unread thread markers
        /// in the Chats list, keeping the count steady while Facebook alternates the title with "<Name> sent a message".
        /// </summary>
        public const string MessengerUnread = @"
(window.__mhFbUnread || (window.__mhFbUnread = () => {
  try {
    const t = (document.title || '').trim();
    const m = t.match(/\((\d+)\+?\)/);
    if (m) {
      const v = parseInt(m[1], 10);
      if (!isNaN(v) && v > 0) {
        window.__mhFbLast = v;
        window.__mhFbTs = Date.now();
        return v;
      }
    }

    let domUnread = 0;
    const rows = document.querySelectorAll(
      '[aria-label=\x22Chats\x22 i] [role=\x22row\x22], ' +
      '[role=\x22navigation\x22] [role=\x22row\x22], ' +
      '[role=\x22grid\x22] [role=\x22row\x22]'
    );
    for (let i = 0; i < rows.length; i++) {
      const r = rows[i];
      if (r.querySelector('[aria-label*=\x22Mark as read\x22 i], [aria-label*=\x22Unread message\x22 i], [aria-label*=\x22unread\x22 i]:not([role=\x22tab\x22]):not([role=\x22radio\x22])')) {
        domUnread++;
      }
    }
    if (domUnread > 0) {
      const c = Math.max(domUnread, window.__mhFbLast || 0);
      return c;
    }

    // If Facebook is mid-flash showing '<Sender> sent a message' instead of 'Messenger | Facebook', hold last count briefly.
    if (t && !/^(?:Messenger|Facebook)\b/i.test(t) && window.__mhFbLast > 0 && (Date.now() - (window.__mhFbTs || 0)) < 20000) {
      return window.__mhFbLast;
    }

    window.__mhFbLast = 0;
    return 0;
  } catch (e) { return -1; }
}))()
";

        /// <summary>
        /// Google Messages: count conversation rows marked unread (class, data-e2e attribute, or aria-label),
        /// with a fallback to (N) in document.title.
        /// </summary>
        public const string GoogleMessagesUnread = @"
(window.__mhGmUnread || (window.__mhGmUnread = () => {
  try {
    const items = document.querySelectorAll('mws-conversation-list-item');
    let n = 0;
    for (let i = 0; i < items.length; i++) {
      const it = items[i];
      if (it.classList.contains('unread') ||
          it.querySelector('.unread, [data-e2e-is-unread=\x22true\x22], [aria-label*=\x22unread\x22 i]')) {
        n++;
      }
    }
    if (n > 0) return n;
    const m = (document.title || '').match(/\((\d+)\+?\)/);
    return m ? parseInt(m[1], 10) : 0;
  } catch (e) { return -1; }
}))()
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
        /// Request persistent storage so the browser engine never evicts IndexedDB / LocalStorage
        /// data under quota or disk pressure, guaranteeing login sessions stay alive permanently.
        /// </summary>
        public const string EnsurePersistentStorage = @"
(() => {
  try {
    if (navigator.storage && navigator.storage.persist) {
      navigator.storage.persist().catch(() => {});
    }
  } catch (e) {}
})()
";

        /// <summary>
        /// Automatically clicks "Turn on" when WhatsApp Web shows its "Turn on background sync" banner
        /// on initial profile load, then records completion in localStorage and disconnects its observer
        /// so users don't have to enable it manually on every account (while still respecting manual toggles later).
        /// </summary>
        public const string EnableWhatsAppBackgroundSync = @"
(() => {
  if (window !== window.top || window.__mhEnableBgSync || !/(^|\.)whatsapp\.com$/.test(location.hostname)) return;
  window.__mhEnableBgSync = true;

  const DONE_KEY = '__mh_wa_bgsync_auto';
  const isDone = () => { try { return localStorage.getItem(DONE_KEY) === '1'; } catch (e) { return false; } };
  if (isDone()) return;

  let obs = null, timer = 0, settleTimer = 0, stopTimer = 0, clicks = 0, loggedInSeen = false;

  const cleanup = markDone => {
    if (timer) { clearTimeout(timer); timer = 0; }
    if (settleTimer) { clearTimeout(settleTimer); settleTimer = 0; }
    if (stopTimer) { clearTimeout(stopTimer); stopTimer = 0; }
    if (obs) { obs.disconnect(); obs = null; }
    if (markDone) {
      try { localStorage.setItem(DONE_KEY, '1'); } catch (e) {}
    }
  };

  const findTurnOnButton = () => {
    if (!document.body) return null;
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      if ((n.nodeValue || '').trim().toLowerCase() !== 'turn on background sync') continue;
      let card = n.parentElement;
      if (!card || card.closest('#main')) continue;

      // Climb only within the small banner card itself — never into #side or #pane-side.
      for (let depth = 0; depth < 5 && card.parentElement; depth++) {
        const p = card.parentElement;
        if (p === document.body || p.id === 'side' || p.id === 'pane-side') break;
        if (p.querySelector('#pane-side, [aria-label=\x22Chat list\x22]')) break;
        if ((p.textContent || '').length > 240) break;
        card = p;
      }

      const inner = document.createTreeWalker(card, NodeFilter.SHOW_TEXT);
      for (let tn = inner.nextNode(); tn; tn = inner.nextNode()) {
        if ((tn.nodeValue || '').trim().toLowerCase() === 'turn on') {
          const el = tn.parentElement;
          if (!el) continue;
          const btn = el.closest('button, [role=\x22button\x22], a, [tabindex]') || el;
          if (btn.offsetWidth > 0 && btn.offsetHeight > 0) return btn;
        }
      }
    }
    return null;
  };

  const check = () => {
    timer = 0;
    if (isDone()) { cleanup(false); return; }

    if (!loggedInSeen && document.querySelector('#side, #pane-side, [aria-label=\x22Chat list\x22]')) {
      loggedInSeen = true;
      stopTimer = setTimeout(() => cleanup(false), 45000);
    }

    const btn = findTurnOnButton();
    if (!btn || settleTimer) return;

    settleTimer = setTimeout(() => {
      settleTimer = 0;
      const target = findTurnOnButton();
      if (!target) return;
      clicks++;
      try { target.click(); } catch (e) {}

      setTimeout(() => {
        if (!findTurnOnButton() || clicks >= 2) cleanup(true);
      }, 1500);
    }, 400);
  };

  const schedule = () => {
    if (!timer && !settleTimer) timer = setTimeout(check, 400);
  };

  const start = () => {
    check();
    if (!isDone()) {
      obs = new MutationObserver(schedule);
      obs.observe(document.body, { childList: true, subtree: true });
    }
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

  const isVideo = el => el instanceof HTMLVideoElement;

  const nativePlay = HTMLMediaElement.prototype.play;
  const nativePause = HTMLMediaElement.prototype.pause;

  const isWhatsApp = /(^|\.)whatsapp\.com$/.test(location.hostname);
  const allowedVideos = new WeakSet();
  let lastUserClickTime = 0;
  let lastClickPoint = null;

  const onUserPointer = e => {
    if (e.isTrusted === false) return;
    const now = Date.now();
    lastUserClickTime = now;

    const target = e.target;
    if (!target) return;

    if (isVideo(target)) {
      allowedVideos.add(target);
      return;
    }

    const x = e.clientX, y = e.clientY;
    if (typeof x === 'number' && typeof y === 'number') {
      lastClickPoint = { x, y, time: now };
      const vids = document.querySelectorAll('video');
      for (let i = 0; i < vids.length; i++) {
        const v = vids[i];
        try {
          const r = v.getBoundingClientRect();
          if (r.width > 20 && r.height > 20 && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) {
            allowedVideos.add(v);
          }
        } catch (err) {}
      }
    }
  };

  window.addEventListener('pointerdown', onUserPointer, true);
  window.addEventListener('click', onUserPointer, true);

  const clearClickPoint = () => { lastClickPoint = null; };
  window.addEventListener('wheel', clearClickPoint, { capture: true, passive: true });
  window.addEventListener('scroll', clearClickPoint, { capture: true, passive: true });
  window.addEventListener('touchmove', clearClickPoint, { capture: true, passive: true });

  const isDedicatedMediaViewer = v => {
    try {
      return !!v.closest('[data-animate-media-viewer], .media-viewer-whole, #MediaViewer');
    } catch (e) {
      return false;
    }
  };

  window.addEventListener('emptied', e => {
    if (isVideo(e.target)) {
      allowedVideos.delete(e.target);
    }
  }, true);

  const mayPlay = v => {
    if (!isVideo(v)) return true; // Audio (voice notes, alerts) is never blocked
    if (v.srcObject) return true; // WebRTC live camera/call stream is never blocked
    if (document.hidden || window.__mhIdleBg) return false;
    if (allowedVideos.has(v)) return true;

    const now = Date.now();

    // Clicked directly on the video within the last 1200ms without scrolling
    if (lastClickPoint && now - lastClickPoint.time <= 1200) {
      try {
        const r = v.getBoundingClientRect();
        if (r.width > 20 && r.height > 20 &&
            lastClickPoint.x >= r.left && lastClickPoint.x <= r.right &&
            lastClickPoint.y >= r.top && lastClickPoint.y <= r.bottom) {
          allowedVideos.add(v);
          return true;
        }
      } catch (e) {}
    }

    // Dedicated chat media viewers (WhatsApp modal or Telegram viewer) opened after user clicked thumbnail
    if ((isDedicatedMediaViewer(v) || (isWhatsApp && v.controls)) && now - lastUserClickTime <= 15000) {
      allowedVideos.add(v);
      return true;
    }

    return false;
  };

  const safePlay = function () {
    if (isVideo(this) && !mayPlay(this)) {
      try { this.removeAttribute('autoplay'); } catch (e) {}
      try { nativePause.call(this); } catch (e) {}
      return Promise.reject(new DOMException('Autoplay is disabled', 'NotAllowedError'));
    }
    return nativePlay.apply(this, arguments);
  };

  try {
    Object.defineProperty(HTMLMediaElement.prototype, 'play', {
      value: safePlay,
      writable: true,
      configurable: true
    });
  } catch (e) {
    HTMLMediaElement.prototype.play = safePlay;
  }

  // Videos report autoplay as off and ignore attempts to turn it on (audio is untouched).
  try {
    if (window.HTMLVideoElement && HTMLVideoElement.prototype) {
      Object.defineProperty(HTMLVideoElement.prototype, 'autoplay', {
        get() { return false; },
        set(v) {},
        configurable: true,
        enumerable: true
      });
    }
  } catch (e) {}

  try {
    const origSetAttr = Element.prototype.setAttribute;
    Element.prototype.setAttribute = function (name, val) {
      if (isVideo(this) && typeof name === 'string' && name.toLowerCase() === 'autoplay' && !mayPlay(this)) {
        return;
      }
      return origSetAttr.apply(this, arguments);
    };
  } catch (e) {}

  const stripAutoplay = v => {
    if (!isVideo(v)) return;
    try {
      if (v.hasAttribute('autoplay')) v.removeAttribute('autoplay');
    } catch (e) {}
    if (!mayPlay(v) && !v.paused) {
      try { nativePause.call(v); } catch (e) {}
    }
  };

  const observer = new MutationObserver(mutations => {
    for (let i = 0; i < mutations.length; i++) {
      const m = mutations[i];
      if (m.type === 'childList') {
        for (let j = 0; j < m.addedNodes.length; j++) {
          const n = m.addedNodes[j];
          if (n.nodeType === 1) {
            if (isVideo(n)) stripAutoplay(n);
            else if (n.querySelectorAll) {
              const vids = n.querySelectorAll('video');
              for (let k = 0; k < vids.length; k++) stripAutoplay(vids[k]);
            }
          }
        }
      } else if (m.type === 'attributes' && m.attributeName === 'autoplay') {
        if (isVideo(m.target)) stripAutoplay(m.target);
      }
    }
  });

  const startObserver = () => {
    const root = document.documentElement || document.body;
    if (root) {
      observer.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['autoplay'] });
    }
    const vids = document.querySelectorAll('video');
    for (let i = 0; i < vids.length; i++) stripAutoplay(vids[i]);
  };

  if (document.documentElement) startObserver();
  else document.addEventListener('DOMContentLoaded', startObserver);

  const enforceBlock = e => {
    const v = e.target;
    if (isVideo(v) && !mayPlay(v)) {
      try {
        v.removeAttribute('autoplay');
        nativePause.call(v);
      } catch (err) {}
    }
  };

  window.addEventListener('play', enforceBlock, true);
  window.addEventListener('playing', enforceBlock, true);

  const ensureBgStyle = () => {
    if (document.getElementById('__mh_bg_style')) return;
    const h = document.head || document.documentElement;
    if (!h) return;
    const st = document.createElement('style');
    st.id = '__mh_bg_style';
    st.textContent = 'html[data-mh-bg] *, html[data-mh-bg] *::before, html[data-mh-bg] *::after { animation-play-state: paused !important; transition: none !important; }';
    h.appendChild(st);
  };

  const syncVisibility = () => {
    try {
      ensureBgStyle();
      if (document.hidden || window.__mhIdleBg) {
        document.documentElement.setAttribute('data-mh-bg', '1');
        const vids = document.querySelectorAll('video');
        for (let i = 0; i < vids.length; i++) {
          const v = vids[i];
          if (!v.srcObject && !v.paused) { try { nativePause.call(v); } catch (e) {} }
        }
      } else {
        document.documentElement.removeAttribute('data-mh-bg');
      }
    } catch (e) {}
  };

  window.__mhSetIdleBg = idle => { window.__mhIdleBg = !!idle; syncVisibility(); };

  document.addEventListener('visibilitychange', syncVisibility);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', syncVisibility);
  else syncVisibility();
})();
";

        /// <summary>
        /// Builds an event-driven MutationObserver script that pushes unread count changes to the host
        /// immediately via postMessage('__mh_unread:' + n). Runs ONLY in the top frame so child iframes
        /// never overwrite the unread count with 0, and uses leading-edge execution (at most once a second, once every 5 s for tabs that are not active).
        /// </summary>
        public static string BuildUnreadPushScript(string unreadExpr) => @"
(() => {
  if (window !== window.top || window.__mhUnreadObs) return;
  window.__mhUnreadObs = true;
  let last = -1, timer = 0, lastRun = 0;
  const check = () => {
    timer = 0;
    lastRun = Date.now();
    try {
      const n = " + unreadExpr.Trim() + @";
      if (typeof n === 'number' && n >= 0 && n !== last) {
        last = n;
        window.chrome.webview.postMessage('__mh_unread:' + n);
      }
    } catch (e) {}
  };
  const schedule = () => {
    // At most once a second for the tab you are looking at, once every 5 s for the others.
    const gap = window.__mhActive === false ? 5000 : 1000;
    const elapsed = Date.now() - lastRun;
    if (elapsed >= gap) {
      if (timer) { clearTimeout(timer); timer = 0; }
      check();
    } else if (!timer) {
      timer = setTimeout(check, gap - elapsed);
    }
  };
  const start = () => {
    check();
    new MutationObserver(schedule).observe(document.documentElement, {
      childList: true, subtree: true, attributes: true,
      attributeFilter: [
        'class', 'aria-label', 'data-qa', 'data-qa-unread',
        'data-qa-channel-sidebar-channel-is-unread',
        'data-qa-channel-sidebar-channel-is-muted',
        'data-e2e-is-unread', 'data-is-unread', 'title'
      ]
    });
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
  window.addEventListener('load', schedule);
})();
";

    }
}
