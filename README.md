# Casium

The public site and private keys console for the Casium Windows executor.

```
website/         casium.top — product page + keys console (Netlify)
netlify.toml     build settings, headers, CSP (base directory: website)
```

## The website

Landing page at `/` — a clean one-pager: hero, facts strip, features,
setup steps, download, FAQ.

Private keys console at `/thisismyveryownkeyspage` (username + password,
configurable; issues lifetime / 1-year / 1-month / custom-duration keys and
validates them server-side for the client).

Full setup — local dev, Netlify deploy, DNS for casium.top, credentials, and
the keys API the executor calls — is documented in
**[website/README.md](website/README.md)**.

```bash
cd website && npm install && npm run dev   # http://localhost:8888
```

## Legal

Independent project. Not affiliated with or endorsed by Roblox Corporation.
Use of executors violates Roblox’s Terms of Use and can get accounts banned.
