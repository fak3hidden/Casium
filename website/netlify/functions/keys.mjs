/* ==================================================================
   /api/keys — the console's key list
   GET    list every key with live status
   POST   create { key, duration, note }
   PATCH  revoke / reinstate { key, revoked }
   DELETE remove permanently ?key=…
   All of them require a console session token.
   ================================================================== */

import { json, fail, readJson, methodNotAllowed } from "./lib/http.mjs";
import { readState, withState, storageKind } from "./lib/store.mjs";
import { authenticate, credentialsFor } from "./lib/auth.mjs";
import { listKeys, createKeyRecord, setRevoked, deleteKey } from "./lib/records.mjs";

const ALLOWED = ["GET", "POST", "PATCH", "DELETE"];

export const config = {
  path: "/api/keys",
};

export default async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { status: 204, headers: { allow: ALLOWED.join(", ") } });
  }
  if (!ALLOWED.includes(req.method)) return methodNotAllowed(ALLOWED);

  const state = await readState();
  const auth = authenticate(req, state);
  if (!auth.ok) return fail(auth.status, auth.error, auth.message);

  switch (req.method) {
    case "GET": {
      const keys = listKeys(state);
      const credentials = credentialsFor(state);
      return json({
        ok: true,
        keys,
        count: keys.length,
        username: credentials.username,
        credentialsSource: credentials.source,
        usingDefaultCredentials: credentials.isDefault,
        storage: await storageKind(),
        sessionExpiresAt: auth.expiresAt,
        updatedAt: state.updatedAt,
      });
    }

    case "POST": {
      const body = await readJson(req);
      if (!body.ok) return fail(400, body.error, body.message);

      const result = await withState(async (fresh) => {
        const created = createKeyRecord(fresh, body.value);
        return created.ok ? { value: created } : { persist: false, value: created };
      });
      if (!result.ok) return fail(result.status, result.error, result.message);
      return json({ ok: true, key: result.key }, 201);
    }

    case "PATCH": {
      const body = await readJson(req);
      if (!body.ok) return fail(400, body.error, body.message);

      const result = await withState(async (fresh) => {
        const updated = setRevoked(fresh, body.value.key, body.value.revoked);
        return updated.ok ? { value: updated } : { persist: false, value: updated };
      });
      if (!result.ok) return fail(result.status, result.error, result.message);
      return json({ ok: true, key: result.key });
    }

    case "DELETE": {
      const key = new URL(req.url).searchParams.get("key");
      if (!key) return fail(400, "missing_key", "Pass ?key=… to delete a key.");

      const result = await withState(async (fresh) => {
        const removed = deleteKey(fresh, key);
        return removed.ok ? { value: removed } : { persist: false, value: removed };
      });
      if (!result.ok) return fail(result.status, result.error, result.message);
      return json({ ok: true, deleted: result.key.key });
    }
  }
};
