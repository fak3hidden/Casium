/* ==================================================================
   Casium keys API — key records
   Pure functions over the state document; the handlers do the I/O.
   ================================================================== */

import { expiresAt, humanLabel, isValidKeyFormat, keyStatus, normalizeDuration, remainingSeconds } from "./duration.mjs";

export function decorateKey(record, now = Date.now()) {
  const status = keyStatus(record, now);
  return {
    key: record.key,
    note: record.note || "",
    createdAt: record.createdAt,
    duration: {
      lifetime: !!record.duration?.lifetime,
      amount: record.duration?.amount ?? null,
      unit: record.duration?.unit ?? null,
      label: record.duration?.label || humanLabel(record.duration),
    },
    expiresAt: record.expiresAt ?? null,
    revoked: !!record.revoked,
    revokedAt: record.revokedAt ?? null,
    status,
    remainingSeconds: status === "expired" ? 0 : remainingSeconds(record, now),
    lastSeen: record.lastSeen ?? null,
    uses: record.uses ?? 0,
  };
}

export function listKeys(state, now = Date.now()) {
  return state.keys.map((record) => decorateKey(record, now)).sort((a, b) => (a.createdAt < b.createdAt ? 1 : -1));
}

export function findRecord(state, key) {
  const wanted = String(key ?? "").trim();
  return state.keys.find((record) => record.key === wanted) || null;
}

/**
 * Create a key.
 * @returns {{ok:true, key:object} | {ok:false, error:string, message:string, status:number}}
 */
export function createKeyRecord(state, { key, duration, note }) {
  const trimmedKey = String(key ?? "").trim();
  if (!isValidKeyFormat(trimmedKey)) {
    return {
      ok: false,
      status: 400,
      error: "invalid_key",
      message: "Keys must follow the format casium-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx.",
    };
  }
  if (findRecord(state, trimmedKey)) {
    return { ok: false, status: 409, error: "duplicate_key", message: "That key already exists — reroll and try again." };
  }

  const parsed = normalizeDuration(duration);
  if (!parsed.ok) return { ok: false, status: 400, error: "invalid_duration", message: parsed.error };

  const createdAt = new Date().toISOString();
  const record = {
    key: trimmedKey,
    note: String(note ?? "").trim().slice(0, 60),
    createdAt,
    duration: parsed.value,
    expiresAt: expiresAt(createdAt, parsed.value),
    revoked: false,
    uses: 0,
    lastSeen: null,
  };

  state.keys.push(record);
  return { ok: true, key: decorateKey(record) };
}

export function setRevoked(state, key, revoked) {
  const record = findRecord(state, key);
  if (!record) return { ok: false, status: 404, error: "not_found", message: "No such key." };
  record.revoked = !!revoked;
  record.revokedAt = record.revoked ? new Date().toISOString() : null;
  return { ok: true, key: decorateKey(record) };
}

export function deleteKey(state, key) {
  const wanted = String(key ?? "").trim();
  const index = state.keys.findIndex((record) => record.key === wanted);
  if (index === -1) return { ok: false, status: 404, error: "not_found", message: "No such key." };
  const [removed] = state.keys.splice(index, 1);
  return { ok: true, key: decorateKey(removed) };
}

/** Called by /api/validate — throttled by the handler to avoid write storms. */
export function touchKey(record, now = new Date()) {
  record.uses = (record.uses || 0) + 1;
  record.lastSeen = now.toISOString();
}
