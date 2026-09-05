/* ==================================================================
   POST /api/credentials — change the console username/password
   Body: { currentPassword, username, password }
   Requires a session. Rotating the password invalidates every existing
   token, so a fresh one is returned with the response.
   ================================================================== */

import { json, fail, readJson, methodNotAllowed } from "./lib/http.mjs";
import { readState, withState, storageKind } from "./lib/store.mjs";
import { authenticate, checkLogin, createSession } from "./lib/auth.mjs";
import { hashPassword } from "./lib/crypto.mjs";

export const config = {
  path: "/api/credentials",
  method: "POST",
};

const USERNAME_PATTERN = /^[A-Za-z0-9._-]{3,32}$/;
const MIN_PASSWORD = 8;

export default async (req) => {
  if (req.method !== "POST") return methodNotAllowed(["POST"]);

  const state = await readState();
  const auth = await authenticate(req, state);
  if (!auth.ok) return fail(auth.status, auth.error, auth.message);

  const body = await readJson(req);
  if (!body.ok) return fail(400, body.error, body.message);

  const currentPassword = String(body.value.currentPassword ?? "");
  const username = String(body.value.username ?? "").trim();
  const password = String(body.value.password ?? "");

  if (!USERNAME_PATTERN.test(username)) {
    return fail(400, "invalid_username", "Username must be 3–32 characters: letters, numbers, dot, dash or underscore.");
  }
  if (password.length < MIN_PASSWORD) {
    return fail(400, "weak_password", `Password must be at least ${MIN_PASSWORD} characters.`);
  }
  if (password.length > 200) {
    return fail(400, "weak_password", "Password is too long.");
  }

  if (!checkLogin(state, auth.username, currentPassword)) {
    return fail(403, "wrong_password", "Current password is incorrect.");
  }

  const storage = await storageKind();
  if (storage === "memory") {
    return fail(507, "storage_unavailable", "Storage is read-only here, so credentials can’t be saved.");
  }

  await withState(async (fresh) => {
    fresh.credentials = { ...hashPassword(password), username };
    fresh.sessionEpoch = (fresh.sessionEpoch || 0) + 1; // invalidate old tokens
    return { persist: true };
  });

  const updated = await readState();
  const session = await createSession(updated, username);

  return json({
    ok: true,
    username,
    token: session.token,
    expiresAt: session.expiresAt,
    storage,
    message: "Credentials updated.",
  });
};
