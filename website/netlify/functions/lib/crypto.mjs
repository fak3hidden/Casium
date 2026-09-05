/* ==================================================================
   Casium keys API — password hashing + signed session tokens
   node:crypto only, no dependencies.
   ================================================================== */

import crypto from "node:crypto";
import { envCredentials } from "./config.mjs";

const SCRYPT = { N: 16384, r: 8, p: 1, maxmem: 64 * 1024 * 1024 };
const KEY_LENGTH = 64;

export function hashPassword(password, existing = null, fixedSaltHex = null) {
  const salt = fixedSaltHex
    ? Buffer.from(fixedSaltHex, "hex")
    : existing?.salt
      ? Buffer.from(existing.salt, "hex")
      : crypto.randomBytes(16);
  const derived = crypto.scryptSync(String(password), salt, KEY_LENGTH, SCRYPT);
  return {
    algorithm: "scrypt",
    iterations: SCRYPT.N,
    salt: salt.toString("hex"),
    hash: derived.toString("hex"),
    updatedAt: new Date().toISOString(),
  };
}

/**
 * Deterministic salt for environment-provided passwords.
 * Serverless instances must agree on the same hash without sharing state —
 * a random salt here would mint a different session secret on every cold
 * start and invalidate tokens between requests (the "kicked out" bug).
 */
export function envSaltHex(password) {
  return crypto.createHash("sha256").update(`casium-env-v1:${String(password)}`).digest("hex").slice(0, 32);
}

export function verifyPassword(password, record) {
  if (!record?.hash || !record?.salt) return false;
  try {
    const salt = Buffer.from(record.salt, "hex");
    const derived = crypto.scryptSync(String(password), salt, KEY_LENGTH, SCRYPT);
    const expected = Buffer.from(record.hash, "hex");
    return derived.length === expected.length && crypto.timingSafeEqual(derived, expected);
  } catch {
    return false;
  }
}

/* ---------------------------------------------------------- session token
   token = base64url(payload) + "." + base64url(hmac)
   The signing secret is derived from a random per-installation salt plus the
   current credential hash, so changing the password invalidates every token. */

function hmac(value, secret) {
  return crypto.createHmac("sha256", secret).update(value).digest();
}

export function sessionSecret(state, backendKind = "durable") {
  const base = process.env.CASIUM_SESSION_SECRET || "casium-development-secret";

  let material;
  if (backendKind === "memory") {
    /* The memory backend regenerates state (and tokenSalt) on every cold start,
       so state MUST NOT feed the secret there — otherwise every restart would
       invalidate every session ("session expired" kicks). Derive from the
       deploy configuration instead: identical on every instance of this
       deploy, so tokens survive restarts. Rotating the env password still
       invalidates them. */
    const { username, password } = envCredentials();
    const digest = crypto.createHash("sha256").update(`${username}\u0000${password}`).digest("hex");
    material = `env|${digest}`;
  } else {
    /* Durable storage (Blobs / file): state is shared, so the per-site salt and
       the epoch counter (bumped on password change) can safely feed the secret. */
    material = `${state.tokenSalt || ""}|v${state.sessionEpoch || 0}`;
  }
  return hmac(material, base);
}

export function signToken(payload, secret, ttlSeconds) {
  const now = Math.floor(Date.now() / 1000);
  const body = { ...payload, iat: now, exp: now + ttlSeconds };
  const encoded = Buffer.from(JSON.stringify(body), "utf8").toString("base64url");
  return `${encoded}.${hmac(encoded, secret).toString("base64url")}`;
}

export function verifyToken(token, secret) {
  if (typeof token !== "string" || !token.includes(".")) return null;
  const [encoded, signature] = token.split(".");
  if (!encoded || !signature) return null;
  const expected = hmac(encoded, secret).toString("base64url");
  const a = Buffer.from(signature);
  const b = Buffer.from(expected);
  if (a.length !== b.length || !crypto.timingSafeEqual(a, b)) return null;
  try {
    const payload = JSON.parse(Buffer.from(encoded, "base64url").toString("utf8"));
    if (!payload.exp || payload.exp < Math.floor(Date.now() / 1000)) return null;
    return payload;
  } catch {
    return null;
  }
}

export function randomId(bytes = 16) {
  return crypto.randomBytes(bytes).toString("hex");
}
