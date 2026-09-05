/* ==================================================================
   /api/validate — public endpoint the Casium client calls on launch
   POST { "key": "casium-…" }   (or GET ?key=casium-…)

   → { valid, reason, lifetime, expiresAt, remainingSeconds, remaining,
       serverTime }

   No session required: this is what the executor talks to. Answers are
   recomputed from storage on every call, so a revoked or expired key
   stops working immediately — no new build needed.
   ================================================================== */

import { json, fail, readJson } from "./lib/http.mjs";
import { readState, withState } from "./lib/store.mjs";
import { decorateKey, findRecord, touchKey } from "./lib/records.mjs";
import { humanLabel } from "./lib/duration.mjs";

export const config = {
  path: "/api/validate",
};

const CORS = {
  "access-control-allow-origin": "*",
  "access-control-allow-methods": "GET, POST, OPTIONS",
  "access-control-allow-headers": "content-type",
  "access-control-max-age": "600",
};

/* Light per-instance rate limit: 60 checks / minute / IP. */
const hits = new Map();
const WINDOW_MS = 60_000;
const WINDOW_MAX = 60;

function rateLimited(ip) {
  const now = Date.now();
  const entry = hits.get(ip) || { count: 0, startedAt: now };
  if (now - entry.startedAt > WINDOW_MS) {
    entry.count = 0;
    entry.startedAt = now;
  }
  entry.count += 1;
  hits.set(ip, entry);
  if (hits.size > 5000) hits.clear(); // never grow without bound
  return entry.count > WINDOW_MAX;
}

function remainingLabel(seconds) {
  if (seconds === null) return "lifetime";
  if (seconds <= 0) return "expired";
  const units = [
    ["year", 31_536_000],
    ["month", 2_592_000],
    ["day", 86_400],
    ["hour", 3_600],
    ["minute", 60],
    ["second", 1],
  ];
  for (const [name, size] of units) {
    if (seconds >= size) {
      const value = Math.floor(seconds / size);
      return `${value} ${name}${value === 1 ? "" : "s"}`;
    }
  }
  return "0 seconds";
}

export default async (req, context) => {
  if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: CORS });
  if (req.method !== "GET" && req.method !== "POST") {
    return json({ ok: false, error: "method_not_allowed", message: "Use GET or POST." }, 405, CORS);
  }

  const url = new URL(req.url);
  const ip = context?.ip || req.headers.get("x-forwarded-for")?.split(",")[0]?.trim() || "unknown";

  let key = url.searchParams.get("key") || "";
  if (req.method === "POST") {
    const body = await readJson(req);
    if (!body.ok) return fail(400, body.error, body.message, CORS);
    key = String(body.value.key ?? key ?? "").trim();
  }

  const serverTime = new Date().toISOString();
  const answer = (status, payload) => json({ ok: status < 400, ...payload, serverTime }, status, CORS);

  if (!key) {
    return answer(400, { valid: false, reason: "missing_key", message: "No key supplied." });
  }
  if (rateLimited(ip)) {
    return answer(429, { valid: false, reason: "rate_limited", message: "Too many checks. Wait a minute." });
  }

  const state = await readState();
  const record = findRecord(state, key);

  if (!record) {
    return answer(200, { valid: false, reason: "unknown_key", message: "That key does not exist.", key });
  }

  const decorated = decorateKey(record);

  if (decorated.status === "revoked") {
    return answer(200, { valid: false, reason: "revoked", message: "This key has been revoked.", key });
  }
  if (decorated.status === "expired") {
    return answer(200, {
      valid: false,
      reason: "expired",
      message: "This key expired.",
      key,
      expiresAt: decorated.expiresAt,
    });
  }

  /* Bookkeeping: record the check, but at most once a minute per key so a
     chatty client can't turn every launch into a storage write. */
  const lastSeen = record.lastSeen ? new Date(record.lastSeen).getTime() : 0;
  if (Date.now() - lastSeen > 60_000) {
    await withState(async (fresh) => {
      const target = findRecord(fresh, key);
      if (target) touchKey(target);
      return { persist: !!target };
    }).catch(() => {});
  }

  return answer(200, {
    valid: true,
    reason: null,
    message: "Key accepted.",
    key,
    lifetime: decorated.duration.lifetime,
    duration: decorated.duration.label || humanLabel(decorated.duration),
    createdAt: decorated.createdAt,
    expiresAt: decorated.expiresAt,
    remainingSeconds: decorated.remainingSeconds,
    remaining: remainingLabel(decorated.remainingSeconds),
    uses: decorated.uses,
  });
};
