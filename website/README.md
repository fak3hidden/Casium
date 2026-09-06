# casium.top — website + keys console

Everything the site needs lives in this folder. The product page is static
HTML/CSS/JS; the keys console (`/thisismyveryownkeyspage`) is backed by
Netlify Functions + Netlify Blobs, so keys survive redeploys and are validated
server-side by the Casium client.

```
website/
├── netlify.toml                 build settings + security headers
├── package.json                 one dependency: @netlify/blobs
├── public/                      ← what gets published
│   ├── index.html               product page
│   ├── thisismyveryownkeyspage.html   keys console (private, noindex)
│   ├── 404.html  robots.txt  sitemap.xml  favicon.svg
│   ├── loader.lua               starter Lua module the client loads
│   ├── assets/
│   │   ├── css/                 base.css (tokens) · site.css · panel.css · fonts.css
│   │   ├── js/                  site.js (landing) · panel.js (console)
│   │   ├── fonts/               self-hosted Archivo + IBM Plex (woff2, latin)
│   │   └── img/                 og.png · apple-touch-icon.png (regenerate: tools/build-images.py)
│   └── downloads/               put your release zips here (see below)
├── netlify/functions/
│   ├── status.mjs               GET  /api/status      public
│   ├── login.mjs                POST /api/login       → session token
│   ├── keys.mjs                 GET/POST/PATCH/DELETE /api/keys   (auth)
│   ├── credentials.mjs          POST /api/credentials (auth) — change username/password
│   ├── validate.mjs             GET/POST /api/validate public — the client calls this
│   └── lib/                     shared logic (storage, crypto, durations, records)
└── tools/
    ├── dev-server.mjs           runs the site + the real functions locally
    └── build-images.py          regenerates og.png / icons from the brand tokens
```

---

## 1 · Run it locally

```bash
cd website
npm install
npm run dev            # → http://localhost:8888
```

The dev server runs the **same function files** Netlify will run, against a
JSON file in `tools/.data/state.json` (git-ignored). Console login defaults:
`admin` / `casium-keys`. To test your real credentials locally, create
`website/.env`:

```env
CASIUM_ADMIN_USER=yourname
CASIUM_ADMIN_PASS=yourpassword
```

(`netlify dev` also works if you have the Netlify CLI and are logged in.)

---

## 2 · Publish on Netlify

### Option A — connect this GitHub repo (recommended)

1. Netlify → **Add new site → Import an existing project → GitHub** → pick the repo.
2. Netlify reads `netlify.toml`: base directory `website`, publish `public`,
   functions `netlify/functions`. No build command needed — leave the detected
   one blank if it guesses.
3. Deploy. You get a `*.netlify.app` URL immediately.

### Option B — drag & drop (static only, no keys server)

Drag the **`website/public`** folder onto Netlify. The landing page works, but
the console falls back to **local mode**: keys are stored in the visitor’s
browser and the client *cannot* validate them. The console shows a banner
saying so. Use this only as a quick preview; use Option A for real.

### Option C — Netlify CLI

```bash
npm i -g netlify-cli
cd website && netlify deploy --prod --dir=public --functions=netlify/functions
```

---

## 3 · Point casium.top at the site

Two ways, in **Site configuration → Domain management → Add a domain**.

**A. Netlify DNS (easiest, gives you HTTPS + DNS in one place)**
1. Add `casium.top` in Domain management and choose *Set up Netlify DNS*.
2. At your registrar (wherever you bought the domain), replace the
   nameservers with the four Netlify ones it shows you
   (`dns1.p0X.nsone.net` …). Propagation: usually < 1 h, worst case 48 h.
3. Netlify issues the Let’s Encrypt certificate automatically.

**B. Keep your current DNS provider**
Add these records at your registrar, then add the domain in Netlify:

| Type  | Name | Value                |
| ----- | ---- | -------------------- |
| `A`   | `@`  | `75.2.60.5`          |
| `CNAME` | `www` | `<your-site>.netlify.app` |

Either way, once the domain is verified the site is live at
`https://casium.top` and the console at
`https://casium.top/thisismyveryownkeyspage`.

---

## 4 · Configure the console login

The console needs a username + password. Two ways, both supported:

**In the console (recommended):** sign in once with the defaults, open
**Credentials**, set your own username and a password of 8+ characters. They
are stored **hashed with scrypt** in Netlify Blobs and win over environment
variables. Changing them invalidates every existing session.

**Environment variables:** Netlify → Site configuration → Environment
variables → add → redeploy:

| Variable              | Purpose                                        |
| --------------------- | ---------------------------------------------- |
| `CASIUM_ADMIN_USER`   | console username (default `admin`)             |
| `CASIUM_ADMIN_PASS`   | console password (default `casium-keys`)       |
| `CASIUM_SESSION_SECRET` | optional; long random string for tokens, e.g. `openssl rand -hex 32` |

While the defaults are still in use the console shows an amber banner and the
login page footer says so. Brute force is throttled: 8 wrong attempts lock an
IP out for 10 minutes.

> The keys page is deliberately **not linked from anywhere** on the site and
> sends `noindex` headers. Treat the URL itself as part of the password.

---

## 5 · Keys API (what the executor talks to)

### Validate — public, no auth

```bash
curl -X POST https://casium.top/api/validate \
     -H 'Content-Type: application/json' \
     -d '{"key":"casium-7bfv8-Hf7KF-7bfow-78FBv-7bfjd-7bf9a-87DBf"}'
```

```json
{
  "ok": true,
  "valid": true,
  "reason": null,
  "lifetime": false,
  "duration": "1 month",
  "createdAt": "2026-09-05T20:28:01.456Z",
  "expiresAt": "2026-10-05T20:28:01.456Z",
  "remainingSeconds": 2592000,
  "remaining": "30 days",
  "uses": 4,
  "serverTime": "2026-09-05T20:30:11.000Z"
}
```

`valid: false` comes with `reason` ∈ `unknown_key`, `revoked`, `expired`,
`missing_key`, `rate_limited`. Revocation and expiry take effect on the *next*
launch — no client update needed. `GET /api/validate?key=…` also works.
Every successful check bumps the key's `uses` counter and `lastSeen`, which the
console shows under each key ("api: N checks · last …"), so you can watch the
executor phone home in real time.

Full lifecycle in four curls (token from `/api/login`):

```bash
T=$(curl -s -X POST https://casium.top/api/login -H 'content-type: application/json' \
     -d '{"username":"admin","password":"YOUR-PASSWORD"}' | jq -r .token)
curl -s -X POST https://casium.top/api/keys -H "authorization: Bearer $T" \
     -H 'content-type: application/json' \
     -d '{"key":"casium-9WuY7-tEfsc-HTydZ-jv6vR-RPQVV-W9jZY-ezdiM","duration":{"preset":"1mo"}}'
curl -s -X PATCH https://casium.top/api/keys -H "authorization: Bearer $T" \
     -H 'content-type: application/json' \
     -d '{"key":"casium-9WuY7-tEfsc-HTydZ-jv6vR-RPQVV-W9jZY-ezdiM","revoked":true}'   # kill-switch
curl -s -X DELETE "https://casium.top/api/keys?key=casium-9WuY7-tEfsc-HTydZ-jv6vR-RPQVV-W9jZY-ezdiM" \
     -H "authorization: Bearer $T"                                                    # erase
```

### Console endpoints (Bearer token from `/api/login`)

| Method   | Path              | Body                                  |
| -------- | ----------------- | ------------------------------------- |
| `GET`    | `/api/status`     | —                                     |
| `POST`   | `/api/login`      | `{ "username", "password" }`          |
| `GET`    | `/api/keys`       | — (lists every key with live status)  |
| `POST`   | `/api/keys`       | `{ "key", "duration", "note" }`       |
| `PATCH`  | `/api/keys`       | `{ "key", "revoked": true }`          |
| `DELETE` | `/api/keys?key=…` | —                                     |
| `POST`   | `/api/credentials`| `{ "currentPassword", "username", "password" }` |

`duration` accepts `{ "preset": "lifetime" | "1y" | "1mo" }` or
`{ "amount": 12, "unit": "seconds|minutes|hours|days|months|years" }`.
Months/years are calendar-accurate (31 Jan + 1 month → 28/29 Feb).
Key format is fixed: `casium-` + seven groups of five characters.

### C# — call it from the WPF client

```csharp
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;

public static class CasiumKeyCheck
{
    private static readonly HttpClient Http = new HttpClient();

    public static async Task<(bool valid, string reason, string remaining)> ValidateAsync(string key)
    {
        var json = JsonSerializer.Serialize(new { key });
        var response = await Http.PostAsync(
            "https://casium.top/api/validate",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return (
            root.GetProperty("valid").GetBoolean(),
            root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
            root.TryGetProperty("remaining", out var m) ? m.GetString() ?? "" : "");
    }
}

// On startup, before BubbleAPI.Internal.Inject():
// var (valid, reason, remaining) = await CasiumKeyCheck.ValidateAsync(Settings.Default.LicenceKey);
// if (!valid) { MessageBox.Show($"Key rejected: {reason}"); Application.Current.Shutdown(); }
```

---

## 6 · Releases

`public/downloads/` currently contains a **placeholder zip** so the download
buttons don’t 404 — replace it with your real build, named exactly what the
buttons point at (`Casium-2.4.1.zip`), then update the version strings in
`index.html` (search for `2.4.1`) and the spec table (size / SHA-256).
Alternatively host the zip on GitHub Releases and change the two `href`s.

---

## 7 · Notes worth knowing

- **Storage:** keys + credentials live in one Netlify Blobs document
  (`casium-keys` / `state.json`). Free tier limits are far above what a
  single-admin console writes.
- **No analytics, no third-party scripts.** Fonts are self-hosted, so nothing
  loads from outside your domain. CSP is set in `netlify.toml`. The only cookie
  the site ever sets is the console's own first-party session token (same-site,
  30 days) — a fallback for browsers that block localStorage.
- **Console data never leaves the browser in local mode**, and the banner says
  so. If you see “browser storage” in the console header, functions aren’t
  deployed.
- Regenerate brand images after changing tokens:
  `python3 tools/build-images.py` (needs `pip install pillow fonttools brotli`).

---

## 8 · Troubleshooting

- **“storage: memory (volatile)” in the console** → the deploy can't reach
  Netlify Blobs (usually a drag-&-drop deploy, which doesn't bundle function
  dependencies). Keys created there disappear when Netlify restarts the
  function. Fix: deploy from Git (Option A) so Blobs activates. Login sessions
  survive restarts either way — they no longer depend on the store.
- **“Your session expired” right after signing in** → you're looking at an old
  deploy or a cached `panel.js`. The current build is recognisable by the
  rectangular buttons and the footer bar on the landing page; hard-reload
  (Ctrl/Cmd+Shift+R) and check Netlify → Deploys shows the latest commit.
  Sessions now live 30 days, in localStorage + sessionStorage + a cookie at
  once, and end only when you press **Sign out** (or rotate the password).
- **Login works locally but not on Netlify** → set `CASIUM_ADMIN_USER` /
  `CASIUM_ADMIN_PASS` under Site configuration → Environment variables, then
  redeploy; env changes only apply to new builds.
