/* ==================================================================
   Casium keys API — credentials, sessions, brute-force throttling
   ================================================================== */

import crypto from "node:crypto";
import { envCredentials, usingDefaultPassword, SESSION_TTL_SECONDS, MAX_LOGIN_ATTEMPTS, LOCKOUT_SECONDS } from "./config.mjs";
import { hashPassword, verifyPassword, signToken, verifyToken, sessionSecret, envSaltHex } from "./crypto.mjs";
import { storageKind } from "./store.mjs";

/* Environment passwords are hashed once per warm instance — scrypt is not free.
   The salt is derived from the password itself so EVERY instance (and every
   cold start) produces the identical hash → identical session secrets. */
let envCache = null;

export function credentialsFor(state) {
  if (state.credentials) {
    return { username: state.credentials.username, record: state.credentials, source: "console", isDefault: false };
  }
  const { username, password } = envCredentials();
  if (!envCache || envCache.password !== password) {
    envCache = { password, record: hashPassword(password, null, envSaltHex(password)) };
  }
  return {
    username,
    record: { ...envCache.record, username },
    source: "environment",
    isDefault: usingDefaultPassword(state.credentials),
  };
}

function safeEqual(a, b) {
  const ha = crypto.createHash("sha256").update(String(a)).digest();
  const hb = crypto.createHash("sha256").update(String(b)).digest();
  return crypto.timingSafeEqual(ha, hb);
}

export function checkLogin(state, username, password) {
  const creds = credentialsFor(state);
  const userOk = safeEqual(String(username ?? "").trim(), creds.username);
  const passOk = verifyPassword(String(password ?? ""), creds.record);
  // evaluate both before returning so timing doesn't leak which one failed
  return userOk && passOk ? creds : null;
}

export async function createSession(state, username) {
  const secret = sessionSecret(state, await storageKind());
  const token = signToken({ sub: username, scope: "console" }, secret, SESSION_TTL_SECONDS);
  return { token, expiresIn: SESSION_TTL_SECONDS, expiresAt: new Date(Date.now() + SESSION_TTL_SECONDS * 1000).toISOString() };
}

/** Bearer-token guard used by every console endpoint. */
export async function authenticate(req, state) {
  const header = req.headers.get("authorization") || "";
  const token = header.startsWith("Bearer ") ? header.slice(7).trim() : req.headers.get("x-casium-token") || "";
  if (!token) return { ok: false, status: 401, error: "missing_token", message: "Sign in to use the keys console." };

  const secret = sessionSecret(state, await storageKind());
  const payload = verifyToken(token, secret);
  if (!payload || payload.scope !== "console") {
    return { ok: false, status: 401, error: "invalid_token", message: "Session expired or invalid — sign in again." };
  }
  return { ok: true, username: payload.sub, expiresAt: new Date(payload.exp * 1000).toISOString() };
}

/* ------------------------------------------------------------------ throttling */

export function throttleKey(req, context) {
  const ip = context?.ip || req.headers.get("x-forwarded-for")?.split(",")[0]?.trim() || "unknown";
  return `ip:${ip}`;
}

export function isLockedOut(state, id) {
  const entry = state.loginAttempts?.[id];
  if (!entry?.lockedUntil) return false;
  return new Date(entry.lockedUntil).getTime() > Date.now();
}

export function lockoutRemaining(state, id) {
  const entry = state.loginAttempts?.[id];
  if (!entry?.lockedUntil) return 0;
  return Math.max(0, Math.round((new Date(entry.lockedUntil).getTime() - Date.now()) / 1000));
}

export function recordFailure(state, id) {
  state.loginAttempts ||= {};
  const now = Date.now();
  const entry = state.loginAttempts[id] || { count: 0, firstAt: new Date(now).toISOString() };

  // forget attempts older than the lockout window
  if (now - new Date(entry.firstAt).getTime() > LOCKOUT_SECONDS * 1000) {
    entry.count = 0;
    entry.firstAt = new Date(now).toISOString();
  }

  entry.count += 1;
  entry.lastAt = new Date(now).toISOString();
  if (entry.count >= MAX_LOGIN_ATTEMPTS) {
    entry.lockedUntil = new Date(now + LOCKOUT_SECONDS * 1000).toISOString();
    entry.count = 0;
  }
  state.loginAttempts[id] = entry;

  pruneAttempts(state);
  return { count: entry.count, lockedUntil: entry.lockedUntil || null };
}

export function clearFailures(state, id) {
  if (state.loginAttempts?.[id]) delete state.loginAttempts[id];
}

function pruneAttempts(state) {
  const cutoff = Date.now() - LOCKOUT_SECONDS * 2000;
  for (const [id, entry] of Object.entries(state.loginAttempts)) {
    const last = new Date(entry.lastAt || entry.firstAt || 0).getTime();
    if (!entry.lockedUntil && last < cutoff) delete state.loginAttempts[id];
  }
}
