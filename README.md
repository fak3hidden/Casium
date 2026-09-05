# Casium

A Windows executor for Roblox, built on the BubbleAPI injection core — plus the
public site and private keys console that ship with it.

```
Casium/          WPF client (.NET Framework 4.8) — editor, injector, chrome
BubbleAPI/       injection core: API surface, button map, Monaco bridge
website/         casium.top — product page + keys console (Netlify)
```

## The website

Landing page at `/`, private keys console at `/thisismyveryownkeyspage`
(username + password, configurable; issues lifetime / 1-year / 1-month /
custom-duration keys and validates them server-side for the client).

Full setup — local dev, Netlify deploy, DNS for casium.top, credentials, and
the keys API the executor calls — is documented in
**[website/README.md](website/README.md)**.

```bash
cd website && npm install && npm run dev   # http://localhost:8888
```

## The client

`Casium/Casium.sln` — Visual Studio 2019+, .NET Framework 4.8. The UI hosts the
Monaco bridge from `BubbleAPI/Drop/bin/Monaco.txt`; button handlers are listed
in `BubbleAPI/Code/Butons.txt`. Key validation on launch: call
`POST https://casium.top/api/validate` (snippet in website/README.md §5).

## Legal

Independent project. Not affiliated with or endorsed by Roblox Corporation.
Use of executors violates Roblox’s Terms of Use and can get accounts banned.
