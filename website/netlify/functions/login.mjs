/* ==================================================================
   POST /api/login   { username, password }  →  { token, expiresAt }
   8 failed attempts from one IP = 10 minute lockout.
   ================================================================== */

import { json, fail, readJson, methodNotAllowed, sleep } from "./lib/http.mjs";
import { readState, withState, storageKind } from "./lib/store.mjs";
import {
  checkLogin,
  createSession,
  isLockedOut,
  lockoutRemaining,
  recordFailure,
  clearFailures,
  throttleKey,
} from "./lib/auth.mjs";
import { MAX_LOGIN_ATTEMPTS, LOCKOUT_SECONDS } from "./lib/config.mjs";

export const config = {
  path: "/api/login",
  method: "POST",
};

export default async (req, context) => {
  if (req.method !== "POST") return methodNotAllowed(["POST"]);

  const body = await readJson(req);
  if (!body.ok) return fail(400, body.error, body.message);

  const username = String(body.value.username ?? "").trim();
  const password = String(body.value.password ?? "");
  if (!username || !password) return fail(400, "missing_fields", "Enter both a username and a password.");

  const id = throttleKey(req, context);
  const state = await readState();

  if (isLockedOut(state, id)) {
    const seconds = lockoutRemaining(state, id);
    return fail(429, "locked_out", `Too many failed attempts. Try again in ${Math.ceil(seconds / 60)} min.`, {
      retryAfterSeconds: seconds,
    });
  }

  const credentials = checkLogin(state, username, password);

  if (!credentials) {
    const attempt = await withState(async (fresh) => ({ value: recordFailure(fresh, id) }));
    await sleep(400); // blunt timing equaliser

    if (attempt.lockedUntil) {
      return fail(429, "locked_out", `Too many failed attempts. Locked for ${LOCKOUT_SECONDS / 60} minutes.`, {
        retryAfterSeconds: LOCKOUT_SECONDS,
      });
    }
    return fail(401, "invalid_credentials", "Invalid username or password.", {
      attemptsRemaining: Math.max(0, MAX_LOGIN_ATTEMPTS - attempt.count),
    });
  }

  // clear the counter without touching anything else → no write
  await withState(async (fresh) => {
    clearFailures(fresh, id);
    return { persist: false };
  });

  const session = await createSession(state, credentials.username);

  return json({
    ok: true,
    token: session.token,
    expiresAt: session.expiresAt,
    username: credentials.username,
    usingDefaultCredentials: credentials.isDefault,
    storage: await storageKind(),
  });
};
