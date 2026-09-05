/* ==================================================================
   Casium — keys console client
   ------------------------------------------------------------------
   Talks to the Netlify Functions API. If the API isn't reachable (for
   example when the site was deployed by drag-and-drop without
   functions), the console falls back to a browser-local store and says
   so loudly in a banner — nothing silently pretends to be server-side.
   ================================================================== */
(() => {
  "use strict";

  const $  = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));

  /* ---------------------------------------------------------------- config */

  const DEFAULT_USER = "admin";
  const DEFAULT_PASS = "casium-keys";
  const TOKEN_KEY = "casium.console.token";
  const LOCAL_KEY = "casium.console.local.v1";

  const KEY_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
  const KEY_GROUPS = 7;
  const KEY_GROUP_LENGTH = 5;

  const UNITS = {
    seconds: { label: "Seconds", seconds: 1, max: 315360000 },
    minutes: { label: "Minutes", seconds: 60, max: 5256000 },
    hours:   { label: "Hours",   seconds: 3600, max: 87600 },
    days:    { label: "Days",    seconds: 86400, max: 3650 },
    months:  { label: "Months",  calendar: true, max: 1200 },
    years:   { label: "Years",   calendar: true, max: 100 },
  };

  const PRESETS = {
    lifetime: { lifetime: true },
    "1y":  { amount: 1, unit: "years" },
    "1mo": { amount: 1, unit: "months" },
  };

  /* ---------------------------------------------------------------- state */

  const state = {
    mode: "server",        // "server" | "local"
    apiBase: "/api",
    token: null,
    username: null,
    keys: [],
    filter: "",
    status: "all",
    sort: { field: "createdAt", dir: "desc" },
    usingDefaults: false,
    storage: "unknown",
    pendingDelete: null,
    lastCreated: null,
  };

  /* ---------------------------------------------------------------- helpers */

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  function generateKey() {
    const bytes = new Uint32Array(KEY_GROUPS * KEY_GROUP_LENGTH);
    (window.crypto || window.msCrypto).getRandomValues(bytes);
    const groups = [];
    for (let g = 0; g < KEY_GROUPS; g += 1) {
      let group = "";
      for (let i = 0; i < KEY_GROUP_LENGTH; i += 1) {
        group += KEY_ALPHABET[bytes[g * KEY_GROUP_LENGTH + i] % KEY_ALPHABET.length];
      }
      groups.push(group);
    }
    return `casium-${groups.join("-")}`;
  }

  function normalizeDuration(input) {
    const preset = PRESETS[input?.preset ?? input];
    const source = preset || input || {};
    if (source.lifetime) return { lifetime: true, amount: null, unit: null, label: "Lifetime" };

    const unit = String(source.unit || "").toLowerCase();
    const amount = Number(source.amount);
    if (!UNITS[unit]) throw new Error(`Unknown unit "${unit}".`);
    if (!Number.isInteger(amount) || amount < 1) throw new Error("Amount must be a whole number of 1 or more.");
    if (amount > UNITS[unit].max) throw new Error(`Amount too large for ${unit} (max ${UNITS[unit].max}).`);

    return {
      lifetime: false,
      amount,
      unit,
      label: `${amount} ${amount === 1 ? unit.replace(/s$/, "") : unit}`,
    };
  }

  function expiresAtFrom(createdAtISO, duration) {
    if (!duration || duration.lifetime) return null;
    const created = new Date(createdAtISO);
    const spec = UNITS[duration.unit];

    if (spec.calendar) {
      const next = new Date(created.getTime());
      if (duration.unit === "years") next.setFullYear(next.getFullYear() + duration.amount);
      else next.setMonth(next.getMonth() + duration.amount);
      if (next.getDate() !== created.getDate()) next.setDate(0); // clamp 31 Jan + 1 month
      return next.toISOString();
    }
    return new Date(created.getTime() + duration.amount * spec.seconds * 1000).toISOString();
  }

  function statusOf(key, now = Date.now()) {
    if (key.revoked) return "revoked";
    if (!key.expiresAt) return "lifetime";
    const expires = new Date(key.expiresAt).getTime();
    if (Number.isNaN(expires)) return "active";
    if (expires <= now) return "expired";
    if (expires - now <= 7 * 86400000) return "expiring";
    return "active";
  }

  const STATUS_LABEL = {
    active: "Active",
    expiring: "Expiring soon",
    expired: "Expired",
    revoked: "Revoked",
    lifetime: "Lifetime",
  };
  const STATUS_PILL = {
    active: "pill-ok",
    expiring: "pill-warn",
    expired: "pill-bad",
    revoked: "pill-neutral",
    lifetime: "pill-life",
  };

  const dateFormatter = new Intl.DateTimeFormat("en-GB", { day: "2-digit", month: "short", year: "numeric" });
  const timeFormatter = new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit", hour12: false });

  function formatDate(iso) {
    if (!iso) return "—";
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return "—";
    return `${dateFormatter.format(date)} · ${timeFormatter.format(date)}`;
  }

  function spanText(ms) {
    const units = [["year", 31536000000], ["month", 2592000000], ["day", 86400000], ["hour", 3600000], ["minute", 60000]];
    const abs = Math.abs(ms);
    for (const [name, size] of units) {
      if (abs >= size) {
        const value = Math.round(abs / size);
        return `${value} ${name}${value === 1 ? "" : "s"}`;
      }
    }
    return null;
  }

  /** "3 days ago" · "moments ago" */
  function relativeTime(iso) {
    if (!iso) return null;
    const diff = new Date(iso).getTime() - Date.now();
    const text = spanText(diff);
    if (!text) return "moments ago";
    return diff >= 0 ? `in ${text}` : `${text} ago`;
  }

  /** "29 days" — always positive, used for countdowns */
  function timeLeft(iso) {
    if (!iso) return null;
    const diff = new Date(iso).getTime() - Date.now();
    if (diff <= 0) return "0 minutes";
    const text = spanText(diff);
    if (!text) return "under a minute";
    return text;
  }

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>"']/g, (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  }

  /* ---------------------------------------------------------------- toasts */

  const ICONS = {
    ok: '<svg viewBox="0 0 16 16" fill="none"><path d="m3 8.4 3.2 3.2L13 4.8" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>',
    error: '<svg viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="6.4" stroke="currentColor" stroke-width="1.4"/><path d="M8 4.9v3.6" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/><circle cx="8" cy="11" r=".85" fill="currentColor"/></svg>',
    info: '<svg viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="6.4" stroke="currentColor" stroke-width="1.4"/><path d="M8 7.3v3.8" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/><circle cx="8" cy="5" r=".85" fill="currentColor"/></svg>',
  };

  function toast(message, kind = "ok", ttl = 3600) {
    const host = $("#toasts");
    const node = document.createElement("div");
    node.className = "toast";
    node.dataset.kind = kind;
    node.innerHTML = `${ICONS[kind] || ICONS.info}<span>${escapeHtml(message)}</span>`;
    host.appendChild(node);
    setTimeout(() => {
      node.classList.add("out");
      node.addEventListener("animationend", () => node.remove(), { once: true });
      setTimeout(() => node.remove(), 400);
    }, ttl);
  }

  /* ---------------------------------------------------------------- API */

  async function rawFetch(path, options = {}) {
    const headers = { "content-type": "application/json", ...(options.headers || {}) };
    if (state.token) headers.authorization = `Bearer ${state.token}`;
    const response = await fetch(`${state.apiBase}${path}`, { ...options, headers, credentials: "same-origin" });
    let data = null;
    try { data = await response.json(); } catch { data = null; }
    return { status: response.status, data };
  }

  /** /api/* is the declared path; /.netlify/functions/* is the safety net. */
  async function apiFetch(path, options) {
    const first = await rawFetch(path, options);
    if (first.status === 404 && state.apiBase === "/api") {
      state.apiBase = "/.netlify/functions";
      return rawFetch(path, options);
    }
    return first;
  }

  /* ------------------------------------------------- local (browser) store */

  function localRead() {
    try {
      const parsed = JSON.parse(localStorage.getItem(LOCAL_KEY) || "null");
      if (parsed && typeof parsed === "object") {
        return {
          credentials: parsed.credentials || { username: DEFAULT_USER, password: DEFAULT_PASS },
          keys: Array.isArray(parsed.keys) ? parsed.keys : [],
        };
      }
    } catch { /* corrupt storage — start clean */ }
    return { credentials: { username: DEFAULT_USER, password: DEFAULT_PASS }, keys: [] };
  }

  function localWrite(data) {
    localStorage.setItem(LOCAL_KEY, JSON.stringify(data));
  }

  function localDecorate(record) {
    const status = statusOf(record);
    const remaining = record.expiresAt
      ? Math.max(0, Math.round((new Date(record.expiresAt).getTime() - Date.now()) / 1000))
      : null;
    return { ...record, status, remainingSeconds: status === "expired" ? 0 : remaining };
  }

  const local = {
    status: async () => ({ ok: true, storage: "browser", usingDefaultCredentials: localRead().credentials.password === DEFAULT_PASS }),
    login: async (username, password) => {
      await sleep(260);
      const { credentials } = localRead();
      if (username.trim() !== credentials.username || password !== credentials.password) {
        return { status: 401, data: { message: "Invalid username or password." } };
      }
      return { status: 200, data: { ok: true, token: "local", username: credentials.username, storage: "browser" } };
    },
    list: async () => {
      const data = localRead();
      return { status: 200, data: { ok: true, keys: data.keys.map(localDecorate), username: data.credentials.username, storage: "browser", usingDefaultCredentials: data.credentials.password === DEFAULT_PASS } };
    },
    create: async (payload) => {
      const data = localRead();
      if (data.keys.some((k) => k.key === payload.key)) {
        return { status: 409, data: { message: "That key already exists — reroll and try again." } };
      }
      const createdAt = new Date().toISOString();
      const duration = normalizeDuration(payload.duration);
      const record = {
        key: payload.key,
        note: String(payload.note || "").slice(0, 60),
        createdAt,
        duration,
        expiresAt: expiresAtFrom(createdAt, duration),
        revoked: false,
        uses: 0,
        lastSeen: null,
      };
      data.keys.push(record);
      localWrite(data);
      return { status: 201, data: { ok: true, key: localDecorate(record) } };
    },
    patch: async (payload) => {
      const data = localRead();
      const record = data.keys.find((k) => k.key === payload.key);
      if (!record) return { status: 404, data: { message: "No such key." } };
      record.revoked = !!payload.revoked;
      record.revokedAt = record.revoked ? new Date().toISOString() : null;
      localWrite(data);
      return { status: 200, data: { ok: true, key: localDecorate(record) } };
    },
    remove: async (key) => {
      const data = localRead();
      const index = data.keys.findIndex((k) => k.key === key);
      if (index === -1) return { status: 404, data: { message: "No such key." } };
      data.keys.splice(index, 1);
      localWrite(data);
      return { status: 200, data: { ok: true } };
    },
    credentials: async (payload) => {
      await sleep(240);
      const data = localRead();
      if (payload.currentPassword !== data.credentials.password) {
        return { status: 403, data: { message: "Current password is incorrect." } };
      }
      if (!/^[A-Za-z0-9._-]{3,32}$/.test(payload.username)) {
        return { status: 400, data: { message: "Username must be 3–32 characters: letters, numbers, dot, dash or underscore." } };
      }
      if (String(payload.password).length < 8) {
        return { status: 400, data: { message: "Password must be at least 8 characters." } };
      }
      data.credentials = { username: payload.username.trim(), password: payload.password };
      localWrite(data);
      return { status: 200, data: { ok: true, username: data.credentials.username, token: "local" } };
    },
  };

  const server = {
    status: async () => apiFetch("/status"),
    login: async (username, password) => apiFetch("/login", { method: "POST", body: JSON.stringify({ username, password }) }),
    list: async () => apiFetch("/keys"),
    create: async (payload) => apiFetch("/keys", { method: "POST", body: JSON.stringify(payload) }),
    patch: async (payload) => apiFetch("/keys", { method: "PATCH", body: JSON.stringify(payload) }),
    remove: async (key) => apiFetch(`/keys?key=${encodeURIComponent(key)}`, { method: "DELETE" }),
    credentials: async (payload) => apiFetch("/credentials", { method: "POST", body: JSON.stringify(payload) }),
  };

  const api = () => (state.mode === "local" ? local : server);

  /* ---------------------------------------------------------------- dialogs */

  let lastFocused = null;

  function openDialog(selector, focusSelector) {
    const dialog = $(selector);
    lastFocused = document.activeElement;
    dialog.hidden = false;
    document.body.style.overflow = "hidden";

    const focusables = () =>
      $$('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])', dialog).filter(
        (el) => !el.disabled && el.offsetParent !== null
      );

    dialog._onKeydown = (event) => {
      if (event.key === "Escape") { event.preventDefault(); closeDialog(selector); return; }
      if (event.key !== "Tab") return;
      const items = focusables();
      if (!items.length) return;
      const first = items[0];
      const last = items[items.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    };
    dialog._onMousedown = (event) => {
      if (event.target === dialog) closeDialog(selector);
    };
    dialog.addEventListener("keydown", dialog._onKeydown);
    dialog.addEventListener("mousedown", dialog._onMousedown);

    const target = focusSelector ? $(focusSelector, dialog) : focusables()[0];
    setTimeout(() => target && target.focus(), 30);
  }

  function closeDialog(selector) {
    const dialog = $(selector);
    if (dialog.hidden) return;
    dialog.hidden = true;
    document.body.style.overflow = "";
    if (dialog._onKeydown) dialog.removeEventListener("keydown", dialog._onKeydown);
    if (dialog._onMousedown) dialog.removeEventListener("mousedown", dialog._onMousedown);
    if (lastFocused && document.contains(lastFocused)) lastFocused.focus();
  }

  $$("[data-close]").forEach((btn) =>
    btn.addEventListener("click", () => closeDialog(`#${btn.closest(".overlay").id}`))
  );

  /* ---------------------------------------------------------------- views */

  function showGate() {
    $("#gate").hidden = false;
    $("#console").hidden = true;
  }

  function showConsole() {
    $("#gate").hidden = true;
    $("#console").hidden = false;
  }

  function setBanner(message, show) {
    const banner = $("#banner");
    banner.dataset.show = String(!!show);
    if (message) $("#bannertext").innerHTML = message;
  }

  function describeStorage() {
    if (state.mode === "local") return "browser storage";
    if (state.storage === "netlify-blobs") return "netlify blobs";
    if (state.storage === "file") return "file store";
    return state.storage;
  }

  function paintChrome() {
    $("#whoami").textContent = state.username ? `signed in as ${state.username}` : "";
    $("#storagechip").textContent = describeStorage();
    $("#footstorage").textContent = `storage: ${describeStorage()}`;
    $("#storagebadge").textContent =
      state.mode === "local" ? "browser storage" : `storage: ${describeStorage()}`;
  }

  function paintWarningBanner() {
    if (state.mode === "local") {
      setBanner(
        `<b>Local mode.</b> The keys API isn’t reachable here, so keys are stored in this browser only
         (localStorage) and <b>cannot</b> be validated by the Casium client. Deploy with Netlify
         Functions to get server-side keys — see <code>website/README.md</code>.`,
        true
      );
      return;
    }
    if (state.usingDefaults) {
      setBanner(
        `<b>Default credentials are still active.</b> Click <b>Credentials</b> and set your own username and
         password, or define <code>CASIUM_ADMIN_USER</code> and <code>CASIUM_ADMIN_PASS</code> in
         Netlify → Site configuration → Environment variables.`,
        true
      );
      return;
    }
    setBanner(null, false);
  }

  /* ---------------------------------------------------------------- table */

  function visibleKeys() {
    const term = state.filter.trim().toLowerCase();
    let rows = state.keys.map((key) => ({ ...key, status: statusOf(key) }));

    if (term) {
      rows = rows.filter(
        (key) => key.key.toLowerCase().includes(term) || (key.note || "").toLowerCase().includes(term)
      );
    }
    if (state.status !== "all") {
      rows = rows.filter((key) =>
        state.status === "expiring" ? key.status === "expiring" : key.status === state.status
      );
    }

    const { field, dir } = state.sort;
    const factor = dir === "asc" ? 1 : -1;
    rows.sort((a, b) => {
      let left, right;
      if (field === "key") { left = a.key; right = b.key; }
      else if (field === "duration") { left = a.duration?.label || ""; right = b.duration?.label || ""; }
      else if (field === "expires") { left = a.expiresAt || "9999"; right = b.expiresAt || "9999"; }
      else if (field === "status") { left = a.status; right = b.status; }
      else { left = a.createdAt; right = b.createdAt; }
      return String(left).localeCompare(String(right)) * factor;
    });
    return rows;
  }

  function paintSort() {
    $$(".keys-table thead th.sortable").forEach((th) => {
      if (th.dataset.sort === state.sort.field) {
        th.setAttribute("aria-sort", state.sort.dir === "asc" ? "ascending" : "descending");
        $(".arrow", th).textContent = state.sort.dir === "asc" ? "▲" : "▼";
      } else {
        th.removeAttribute("aria-sort");
        $(".arrow", th).textContent = "▼";
      }
    });
  }

  function renderTable() {
    const body = $("#keybody");
    const rows = visibleKeys();
    paintSort();

    $("#keycount").textContent = `${state.keys.length} issued`;
    $("#footcount").textContent = `${rows.length} shown / ${state.keys.length} total`;
    $("#footupdated").textContent = state.updatedAt ? `updated ${relativeTime(state.updatedAt)}` : "";

    if (!rows.length) {
      const first = !state.keys.length;
      body.innerHTML = `
        <tr><td colspan="5">
          <div class="empty">
            <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
              <circle cx="9" cy="9" r="5.2" stroke="currentColor" stroke-width="1.5"/>
              <path d="m12.9 12.9 6.4 6.4M16.4 16.4l1.8-1.8" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>
            </svg>
            <h3>${first ? "No keys yet" : "Nothing matches that filter"}</h3>
            <p>${first
              ? "Issue your first licence key with the button below — lifetime, a year, a month, or any custom length."
              : "Try a different search term or switch the status filter back to “All statuses”."}</p>
          </div>
        </td></tr>`;
      return;
    }

    body.innerHTML = rows
      .map((key) => {
        const status = key.status;
        const sub = key.revoked
          ? key.revokedAt ? `revoked ${relativeTime(key.revokedAt)}` : "revoked"
          : !key.expiresAt
            ? "no expiry"
            : status === "expired"
              ? `expired ${relativeTime(key.expiresAt)}`
              : `${timeLeft(key.expiresAt)} left`;
        return `
        <tr data-key="${escapeHtml(key.key)}" data-state="${status}"${state.lastCreated === key.key ? ' data-flash="true"' : ""}>
          <td>
            <div class="keycell">
              <span class="keytext" title="${escapeHtml(key.key)}">${escapeHtml(key.key)}</span>
              ${key.note ? `<span class="tag" title="Note">${escapeHtml(key.note)}</span>` : ""}
            </div>
          </td>
          <td>
            <span class="dur">${escapeHtml(key.duration?.label || "—")}<span class="sub">${escapeHtml(sub)}</span></span>
          </td>
          <td><span class="expires">${key.expiresAt ? formatDate(key.expiresAt) : "never"}</span></td>
          <td><span class="pill ${STATUS_PILL[status]}">${STATUS_LABEL[status]}</span></td>
          <td>
            <div class="rowactions">
              <button class="icon-btn" type="button" data-act="copy" title="Copy key" aria-label="Copy key">
                <svg viewBox="0 0 16 16" fill="none" aria-hidden="true"><rect x="5.4" y="5.4" width="8.2" height="8.2" rx="1.6" stroke="currentColor" stroke-width="1.4"/><path d="M10.6 3.2A1.6 1.6 0 0 0 9.1 2.4H4a1.6 1.6 0 0 0-1.6 1.6v5.1c0 .62.35 1.15.86 1.4" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>
              </button>
              <button class="icon-btn" type="button" data-act="revoke" data-on="${key.revoked}" title="${key.revoked ? "Reinstate key" : "Revoke key"}" aria-label="${key.revoked ? "Reinstate key" : "Revoke key"}">
                <svg viewBox="0 0 16 16" fill="none" aria-hidden="true"><circle cx="8" cy="8" r="6.2" stroke="currentColor" stroke-width="1.4"/><path d="m3.8 3.8 8.4 8.4" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>
              </button>
              <button class="icon-btn" type="button" data-act="delete" title="Delete key" aria-label="Delete key">
                <svg viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M2.8 4.4h10.4M6.2 4.4V3.2a.8.8 0 0 1 .8-.8h2a.8.8 0 0 1 .8.8v1.2M4.4 4.4l.6 8.2a1 1 0 0 0 1 .9h4a1 1 0 0 0 1-.9l.6-8.2" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/></svg>
              </button>
            </div>
          </td>
        </tr>`;
      })
      .join("");

    if (state.lastCreated) {
      setTimeout(() => { state.lastCreated = null; }, 2400);
    }
  }

  /* ---------------------------------------------------------------- data */

  async function loadKeys() {
    const body = $("#keybody");
    body.innerHTML = `
      <tr class="skeleton"><td colspan="5"><div class="bar" style="width:42%"></div></td></tr>
      <tr class="skeleton"><td colspan="5"><div class="bar" style="width:64%"></div></td></tr>
      <tr class="skeleton"><td colspan="5"><div class="bar" style="width:31%"></div></td></tr>`;

    const res = await api().list();
    if (res.status === 401) return signOut("Your session expired — sign in again.");
    if (!res.data?.ok) {
      body.innerHTML = "";
      toast(res.data?.message || "Could not load keys.", "error");
      return;
    }

    state.keys = res.data.keys || [];
    state.username = res.data.username || state.username;
    state.storage = res.data.storage || state.storage;
    state.usingDefaults = !!res.data.usingDefaultCredentials;
    state.updatedAt = res.data.updatedAt || null;

    paintChrome();
    paintWarningBanner();
    renderTable();
  }

  /* ---------------------------------------------------------------- auth */

  async function detectMode() {
    try {
      const res = await server.status();
      if (res.status === 200 && res.data?.ok) {
        state.mode = "server";
        state.storage = res.data.storage;
        state.usingDefaults = !!res.data.usingDefaultCredentials;
        return;
      }
      if (res.status === 404) {
        // declared path missing → try the classic functions URL once
        state.apiBase = "/.netlify/functions";
        const retry = await server.status();
        if (retry.status === 200 && retry.data?.ok) {
          state.mode = "server";
          state.storage = retry.data.storage;
          state.usingDefaults = !!retry.data.usingDefaultCredentials;
          return;
        }
      }
      state.mode = "local";
    } catch {
      state.mode = "local";
    }
    state.apiBase = "/api";
    state.storage = "browser";
  }

  function storeToken(token, remember) {
    const store = remember ? localStorage : sessionStorage;
    const other = remember ? sessionStorage : localStorage;
    store.setItem(TOKEN_KEY, token);
    other.removeItem(TOKEN_KEY);
  }

  function readToken() {
    return sessionStorage.getItem(TOKEN_KEY) || localStorage.getItem(TOKEN_KEY);
  }

  function signOut(message) {
    state.token = null;
    sessionStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(TOKEN_KEY);
    showGate();
    $("#password").value = "";
    $("#loginalert").dataset.show = "false";
    paintChrome();
    if (message) toast(message, "info");
  }

  async function submitLogin(event) {
    event.preventDefault();
    const username = $("#username").value.trim();
    const password = $("#password").value;
    const button = $("#loginbtn");
    const alert = $("#loginalert");
    const alertText = $("#loginalerttext");

    alert.dataset.show = "false";
    if (!username || !password) {
      alertText.textContent = "Enter both a username and a password.";
      alert.dataset.show = "true";
      return;
    }

    const label = button.querySelector("span");
    const original = label.textContent;
    button.disabled = true;
    label.textContent = "Checking…";
    button.insertAdjacentHTML("afterbegin", '<span class="spinner" aria-hidden="true"></span>');

    try {
      const res = await api().login(username, password);
      if (res.status === 200 && res.data?.token) {
        state.token = res.data.token;
        state.username = res.data.username || username;
        state.storage = res.data.storage || state.storage;
        storeToken(state.token, $("#remember")?.checked ?? false);
        showConsole();
        paintChrome();
        await loadKeys();
        toast(`Signed in as ${state.username}.`);
      } else {
        const data = res.data || {};
        alertText.textContent =
          data.message ||
          (res.status === 429 ? "Too many attempts — wait a few minutes." : "Invalid username or password.");
        alert.dataset.show = "true";
        $("#password").value = "";
        $("#password").focus();
      }
    } catch {
      alertText.textContent = "Network error — the console API did not respond.";
      alert.dataset.show = "true";
    } finally {
      button.disabled = false;
      label.textContent = original;
      button.querySelector(".spinner")?.remove();
    }
  }

  /* ---------------------------------------------------------------- actions */

  async function copyText(text, successMessage = "Copied to clipboard.") {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      const area = document.createElement("textarea");
      area.value = text;
      area.style.cssText = "position:fixed;opacity:0;top:0;left:0";
      document.body.appendChild(area);
      area.select();
      try { document.execCommand("copy"); } catch { toast("Clipboard blocked by the browser.", "error"); }
      area.remove();
    }
    toast(successMessage);
  }

  async function onTableClick(event) {
    const button = event.target.closest("button[data-act]");
    if (!button) return;
    const row = button.closest("tr");
    const key = row?.dataset.key;
    if (!key) return;

    const action = button.dataset.act;
    if (action === "copy") return copyText(key, "Key copied.");

    if (action === "revoke") {
      const record = state.keys.find((k) => k.key === key);
      const next = !record?.revoked;
      const res = await api().patch({ key, revoked: next });
      if (!res.data?.ok) return toast(res.data?.message || "Could not update the key.", "error");
      Object.assign(record, res.data.key);
      renderTable();
      toast(next ? "Key revoked — it will fail validation immediately." : "Key reinstated.", next ? "info" : "ok");
      return;
    }

    if (action === "delete") {
      state.pendingDelete = key;
      $("#confirmkey").textContent = key;
      openDialog("#confirmdialog", "#confirmdeletebtn");
    }
  }

  async function confirmDelete() {
    const key = state.pendingDelete;
    closeDialog("#confirmdialog");
    if (!key) return;
    const res = await api().remove(key);
    if (res.status === 401) return signOut("Your session expired — sign in again.");
    if (!res.data?.ok) return toast(res.data?.message || "Could not delete the key.", "error");
    state.keys = state.keys.filter((k) => k.key !== key);
    state.pendingDelete = null;
    renderTable();
    toast("Key deleted.", "info");
  }

  /* ------------------------------------------------------- new key dialog */

  function updatePreview() {
    const selection = $("#duration").value;
    const custom = selection === "custom";
    $("#customrow").hidden = !custom;
    $("#customamount").required = custom;

    let duration;
    try {
      duration = normalizeDuration(custom
        ? { amount: Number($("#customamount").value), unit: $("#customunit").value }
        : { preset: selection });
    } catch (error) {
      $("#previewexpires").textContent = "—";
      $("#previewseconds").textContent = error.message;
      $("#createkeybtn").disabled = true;
      return;
    }
    $("#createkeybtn").disabled = false;

    const expiry = expiresAtFrom(new Date().toISOString(), duration);
    $("#previewexpires").textContent = expiry ? formatDate(expiry) : "never";
    $("#previewseconds").textContent = expiry
      ? `${Math.round((new Date(expiry).getTime() - Date.now()) / 1000).toLocaleString("en-GB")} seconds`
      : "no expiry";
  }

  function openNewKey() {
    $("#keyvalue").value = generateKey();
    $("#duration").value = "1mo";
    $("#customamount").value = "12";
    $("#customunit").value = "hours";
    $("#keynote").value = "";
    updatePreview();
    openDialog("#newkeydialog", "#duration");
  }

  async function submitNewKey(event) {
    event.preventDefault();
    const button = $("#createkeybtn");
    const payload = {
      key: $("#keyvalue").value.trim(),
      note: $("#keynote").value.trim(),
      duration: $("#duration").value === "custom"
        ? { amount: Number($("#customamount").value), unit: $("#customunit").value }
        : { preset: $("#duration").value },
    };

    button.disabled = true;
    const original = button.innerHTML;
    button.innerHTML = '<span class="spinner" aria-hidden="true"></span> Creating…';

    try {
      const res = await api().create(payload);
      if (res.status === 401) { closeDialog("#newkeydialog"); return signOut("Your session expired — sign in again."); }
      if (!res.data?.ok) {
        toast(res.data?.message || "Could not create the key.", "error", 5200);
        if (res.status === 409) $("#keyvalue").value = generateKey();
        return;
      }
      state.keys.unshift(res.data.key);
      state.lastCreated = res.data.key.key;
      closeDialog("#newkeydialog");
      renderTable();
      toast(`Key created · ${res.data.key.duration?.label || ""}`);
      copyText(res.data.key.key, "Key copied to clipboard.");
    } catch {
      toast("Network error — the key was not created.", "error");
    } finally {
      button.disabled = false;
      button.innerHTML = original;
    }
  }

  /* --------------------------------------------------- credentials dialog */

  function openCredentials() {
    $("#pwuser").value = state.username || "";
    $("#pwcurrent").value = "";
    $("#pwnew").value = "";
    $("#pwalert").dataset.show = "false";
    $("#pwmode").textContent =
      state.mode === "local"
        ? "Local mode: stored in this browser only."
        : `Saved to ${describeStorage()}. Changing the password signs out every other session.`;
    openDialog("#pwdialog", "#pwcurrent");
  }

  async function submitCredentials(event) {
    event.preventDefault();
    const alert = $("#pwalert");
    const alertText = $("#pwalerttext");
    const payload = {
      currentPassword: $("#pwcurrent").value,
      username: $("#pwuser").value.trim(),
      password: $("#pwnew").value,
    };

    alert.dataset.show = "false";
    const res = await api().credentials(payload);
    if (!res.data?.ok) {
      alertText.textContent = res.data?.message || "Could not save credentials.";
      alert.dataset.show = "true";
      return;
    }
    if (res.data.token && res.data.token !== "local") {
      state.token = res.data.token;
      storeToken(state.token, localStorage.getItem(TOKEN_KEY) !== null);
    }
    state.username = res.data.username || payload.username;
    state.usingDefaults = false;
    closeDialog("#pwdialog");
    paintChrome();
    paintWarningBanner();
    toast("Credentials updated. Use them next time you sign in.");
  }

  /* ---------------------------------------------------------------- export */

  function exportKeys() {
    const rows = visibleKeys().map((key) => ({
      key: key.key,
      duration: key.duration?.label || "",
      lifetime: !!key.duration?.lifetime,
      createdAt: key.createdAt,
      expiresAt: key.expiresAt,
      status: statusOf(key),
      note: key.note || "",
      uses: key.uses || 0,
      lastSeen: key.lastSeen || null,
    }));
    const blob = new Blob([JSON.stringify({ exportedAt: new Date().toISOString(), count: rows.length, keys: rows }, null, 2)], {
      type: "application/json",
    });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = `casium-keys-${new Date().toISOString().slice(0, 10)}.json`;
    anchor.click();
    URL.revokeObjectURL(url);
    toast(`Exported ${rows.length} keys.`);
  }

  /* ---------------------------------------------------------------- wiring */

  function bind() {
    $("#loginform").addEventListener("submit", submitLogin);

    const password = $("#password");
    password.addEventListener("keyup", (event) => {
      $("#capswarn").dataset.show = String(event.getModifierState && event.getModifierState("CapsLock"));
    });
    password.addEventListener("blur", () => { $("#capswarn").dataset.show = "false"; });

    $("#signoutbtn").addEventListener("click", () => signOut("Signed out."));
    $("#changepwbtn").addEventListener("click", openCredentials);
    $("#pwform").addEventListener("submit", submitCredentials);

    $("#newkeybtn").addEventListener("click", openNewKey);
    $("#newkeyform").addEventListener("submit", submitNewKey);
    $("#regenbtn").addEventListener("click", () => {
      $("#keyvalue").value = generateKey();
      toast("New key generated.", "info", 1800);
    });
    $("#duration").addEventListener("change", updatePreview);
    $("#customamount").addEventListener("input", updatePreview);
    $("#customunit").addEventListener("change", updatePreview);

    $("#confirmdeletebtn").addEventListener("click", confirmDelete);
    $("#keybody").addEventListener("click", onTableClick);

    let searchTimer = null;
    $("#search").addEventListener("input", (event) => {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => {
        state.filter = event.target.value;
        renderTable();
      }, 120);
    });

    $("#statusfilter").addEventListener("change", (event) => {
      state.status = event.target.value;
      renderTable();
    });

    $("#refreshbtn").addEventListener("click", async () => {
      await loadKeys();
      toast("Reloaded from storage.", "info", 1800);
    });
    $("#exportbtn").addEventListener("click", exportKeys);

    $$(".keys-table thead th.sortable").forEach((th) => {
      th.addEventListener("click", () => {
        const field = th.dataset.sort;
        if (state.sort.field === field) {
          state.sort.dir = state.sort.dir === "asc" ? "desc" : "asc";
        } else {
          state.sort = { field, dir: field === "key" ? "asc" : "desc" };
        }
        $$(".keys-table thead th.sortable").forEach((other) => other.removeAttribute("aria-sort"));
        th.setAttribute("aria-sort", state.sort.dir === "asc" ? "ascending" : "descending");
        renderTable();
      });
    });

    // re-check expiry labels while the console sits open
    setInterval(() => { if (!$("#console").hidden) renderTable(); }, 60_000);
  }

  async function boot() {
    bind();
    state.token = readToken();
    await detectMode();
    paintChrome();
    paintWarningBanner();

    if (state.token) {
      const res = await api().list();
      if (res.status === 200 && res.data?.ok) {
        state.keys = res.data.keys || [];
        state.username = res.data.username || state.username;
        state.storage = res.data.storage || state.storage;
        state.usingDefaults = !!res.data.usingDefaultCredentials;
        showConsole();
        paintChrome();
        paintWarningBanner();
        renderTable();
        return;
      }
      state.token = null;
      sessionStorage.removeItem(TOKEN_KEY);
      localStorage.removeItem(TOKEN_KEY);
    }

    showGate();
    $("#username").focus({ preventScroll: true });
  }

  boot();
})();
