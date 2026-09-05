/* ==================================================================
   Casium — local dev server
   ------------------------------------------------------------------
   Serves website/public AND runs the real Netlify Functions against a
   JSON file store, so the keys console behaves exactly like production
   without a Netlify account:

       cd website
       npm install
       npm run dev          →  http://localhost:8888

   Keys land in website/tools/.data/state.json (git-ignored).
   Set CASIUM_ADMIN_USER / CASIUM_ADMIN_PASS in the environment, or in
   website/.env, to test your real credentials.
   ================================================================== */

import http from "node:http";
import { createReadStream } from "node:fs";
import { access, mkdir, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const siteRoot = path.resolve(here, "..");
const publicDir = path.join(siteRoot, "public");
const functionsDir = path.join(siteRoot, "netlify", "functions");
const dataDir = path.join(here, ".data");

const PORT = Number(process.env.PORT || 8888);
const HOST = process.env.HOST || "0.0.0.0";

/* ---------------------------------------------------------- environment */

async function loadDotEnv() {
  try {
    const text = await readFile(path.join(siteRoot, ".env"), "utf8");
    for (const line of text.split("\n")) {
      const match = /^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/i.exec(line);
      if (!match || line.trim().startsWith("#")) continue;
      const [, key, rawValue] = match;
      const value = rawValue.replace(/^["']|["']$/g, "");
      if (process.env[key] === undefined) process.env[key] = value;
    }
  } catch {
    /* no .env — that's fine */
  }
}

await loadDotEnv();
await mkdir(dataDir, { recursive: true });

// Force the file backend: no Netlify site context exists locally.
process.env.CASIUM_STORAGE = "file";
process.env.CASIUM_DATA_FILE = path.join(dataDir, "state.json");

/* ---------------------------------------------------------- functions */

/** route → handler, mirroring how Netlify mounts each function. */
const routes = new Map();

async function loadFunctions() {
  const { readdir } = await import("node:fs/promises");
  const entries = await readdir(functionsDir, { withFileTypes: true });

  for (const entry of entries) {
    if (!entry.isFile() || !entry.name.endsWith(".mjs")) continue;
    const module = await import(pathToFileURL(path.join(functionsDir, entry.name)).href);
    if (typeof module.default !== "function") continue;

    const name = entry.name.replace(/\.mjs$/, "");
    routes.set(module.config?.path || `/.netlify/functions/${name}`, module.default);
    routes.set(`/.netlify/functions/${name}`, module.default); // classic URL always works
  }
}

await loadFunctions();

/* ---------------------------------------------------------- static files */

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".webp": "image/webp",
  ".ico": "image/x-icon",
  ".woff2": "font/woff2",
  ".txt": "text/plain; charset=utf-8",
  ".lua": "text/plain; charset=utf-8",
  ".zip": "application/zip",
  ".xml": "application/xml; charset=utf-8",
};

const PRETTY = {
  "/thisismyveryownkeyspage": "/thisismyveryownkeyspage.html",
  "/thisismyveryownkeyspage/": "/thisismyveryownkeyspage.html",
};

async function exists(file) {
  try {
    await access(file);
    return true;
  } catch {
    return false;
  }
}

async function sendFile(res, file, status = 200) {
  const ext = path.extname(file).toLowerCase();
  res.writeHead(status, {
    "content-type": MIME[ext] || "application/octet-stream",
    "cache-control": "no-store",
    "x-content-type-options": "nosniff",
  });
  if (res.req?.method === "HEAD") return res.end();
  createReadStream(file).pipe(res);
}

/* ---------------------------------------------------------- server */

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host || "localhost"}`);
  let pathname = decodeURIComponent(url.pathname);

  try {
    /* ---- API */
    const handler = routes.get(pathname) || routes.get(pathname.replace(/\/$/, ""));
    if (handler) {
      const request = new Request(url.href, {
        method: req.method,
        headers: Object.fromEntries(Object.entries(req.headers)),
        body: ["GET", "HEAD"].includes(req.method) ? undefined : await readBody(req),
      });
      const context = {
        ip: req.socket.remoteAddress || "127.0.0.1",
        params: {},
        geo: {},
        cookies: new Map(),
        requestId: `${Date.now()}`,
      };

      const response = await handler(request, context);
      const headers = Object.fromEntries(response.headers.entries());
      res.writeHead(response.status, headers);
      res.end(Buffer.from(await response.arrayBuffer()));
      log(req.method, pathname, response.status);
      return;
    }

    if (pathname.startsWith("/api/")) {
      res.writeHead(404, { "content-type": "application/json" });
      res.end(JSON.stringify({ ok: false, error: "not_found", message: `No function mounted at ${pathname}` }));
      log(req.method, pathname, 404);
      return;
    }

    /* ---- static */
    if (PRETTY[pathname]) pathname = PRETTY[pathname];
    if (pathname === "/") pathname = "/index.html";

    const candidate = path.join(publicDir, pathname);
    if (!candidate.startsWith(publicDir)) {
      res.writeHead(403).end("Forbidden");
      return;
    }
    if (await exists(candidate) && !candidate.endsWith("/")) {
      return sendFile(res, candidate);
    }
    if (await exists(`${candidate}.html`)) {
      return sendFile(res, `${candidate}.html`);
    }
    if (await exists(path.join(candidate, "index.html"))) {
      return sendFile(res, path.join(candidate, "index.html"));
    }

    const notFound = path.join(publicDir, "404.html");
    if (await exists(notFound)) return sendFile(res, notFound, 404);
    res.writeHead(404, { "content-type": "text/plain" }).end("Not found");
    log(req.method, pathname, 404);
  } catch (error) {
    console.error(`[casium] ${req.method} ${pathname} failed:`, error);
    if (!res.headersSent) res.writeHead(500, { "content-type": "application/json" });
    res.end(JSON.stringify({ ok: false, error: "server_error", message: String(error?.message || error) }));
  }
});

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on("data", (chunk) => chunks.push(chunk));
    req.on("end", () => resolve(Buffer.concat(chunks)));
    req.on("error", reject);
  });
}

function log(method, pathname, status) {
  const colour = status >= 500 ? "\x1b[31m" : status >= 400 ? "\x1b[33m" : "\x1b[90m";
  console.log(`${colour}${String(status).padEnd(3)}\x1b[0m ${method.padEnd(6)} ${pathname}`);
}

server.listen(PORT, HOST, () => {
  const user = process.env.CASIUM_ADMIN_USER || "admin";
  const pass = process.env.CASIUM_ADMIN_PASS || "casium-keys";
  console.log(`
\x1b[1mCasium dev server\x1b[0m
  site      http://localhost:${PORT}
  console   http://localhost:${PORT}/thisismyveryownkeyspage
  api       ${[...new Set([...routes.keys()].filter((r) => r.startsWith("/api/")))].join(", ")}
  storage   ${process.env.CASIUM_DATA_FILE}
  login     ${user} / ${pass}${pass === "casium-keys" ? "   \x1b[33m(default — set CASIUM_ADMIN_USER / CASIUM_ADMIN_PASS)\x1b[0m" : ""}
`);
});
