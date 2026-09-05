/* ==================================================================
   GET /api/status
   Public, no secrets: lets the console know whether server storage is
   live and whether the built-in default credentials are still in use.
   ================================================================== */

import { json, fail } from "./lib/http.mjs";
import { readState, storageKind } from "./lib/store.mjs";
import { credentialsFor } from "./lib/auth.mjs";

export const config = {
  path: "/api/status",
  method: "GET",
};

export default async () => {
  try {
    const state = await readState();
    const credentials = credentialsFor(state);

    return json({
      ok: true,
      service: "casium-keys",
      version: 1,
      storage: await storageKind(),
      credentialsSource: credentials.source,
      usingDefaultCredentials: credentials.isDefault,
      keyCount: state.keys.length,
      time: new Date().toISOString(),
    });
  } catch (error) {
    console.error("[casium] /api/status failed", error);
    return fail(500, "storage_error", "Key storage is unavailable.");
  }
};
