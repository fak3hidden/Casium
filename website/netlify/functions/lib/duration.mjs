/* ==================================================================
   Casium keys API — key format + duration maths
   ================================================================== */

import crypto from "node:crypto";

/** casium-7bfv8-Hf7KF-7bfow-78FBv-7bfjd-7bf9a-87DBf */
export const KEY_PATTERN = /^casium(?:-[A-Za-z0-9]{5}){7}$/;

export const KEY_GROUPS = 7;
export const KEY_GROUP_LENGTH = 5;
const KEY_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

export function generateKey() {
  const group = () =>
    Array.from({ length: KEY_GROUP_LENGTH }, () => KEY_ALPHABET[crypto.randomInt(KEY_ALPHABET.length)]).join("");
  return `casium-${Array.from({ length: KEY_GROUPS }, group).join("-")}`;
}

export function isValidKeyFormat(key) {
  return typeof key === "string" && KEY_PATTERN.test(key.trim());
}

/* ------------------------------------------------------------------ units */

export const UNITS = ["seconds", "minutes", "hours", "days", "months", "years"];

const UNIT_SECONDS = { seconds: 1, minutes: 60, hours: 3600, days: 86400 };

/** Sanity ceilings so a typo can’t create a ten-thousand-year key. */
export const UNIT_LIMITS = {
  seconds: 315_360_000, // 10 years
  minutes: 5_256_000,
  hours: 87_600,
  days: 3_650,
  months: 1_200,
  years: 100,
};

const PRESETS = {
  lifetime: { lifetime: true },
  "1y": { amount: 1, unit: "years" },
  "1mo": { amount: 1, unit: "months" },
};

/** Turn anything the client sends into one canonical duration object. */
export function normalizeDuration(input) {
  if (!input) return error("duration is required");
  if (typeof input === "string") input = PRESETS[input] ? { preset: input } : {};

  if (input.preset && PRESETS[input.preset]) input = { ...PRESETS[input.preset] };
  if (input.lifetime === true || input.kind === "lifetime" || input.type === "lifetime") {
    return ok({ lifetime: true, amount: null, unit: null, seconds: null, label: "Lifetime" });
  }

  const unit = String(input.unit ?? "").trim().toLowerCase();
  const amount = Number(input.amount ?? input.value);

  if (!UNITS.includes(unit)) return error(`unit must be one of: ${UNITS.join(", ")}`);
  if (!Number.isFinite(amount) || !Number.isInteger(amount) || amount < 1) {
    return error("amount must be a whole number of 1 or more");
  }
  if (amount > UNIT_LIMITS[unit]) return error(`amount too large for ${unit} (max ${UNIT_LIMITS[unit]})`);

  const seconds = UNIT_SECONDS[unit] ? amount * UNIT_SECONDS[unit] : null;
  return ok({
    lifetime: false,
    amount,
    unit,
    seconds,
    label: humanLabel({ amount, unit }),
  });
}

export function humanLabel(duration) {
  if (!duration) return "—";
  if (duration.lifetime) return "Lifetime";
  const { amount, unit } = duration;
  if (!amount || !unit) return "—";
  const singular = { seconds: "second", minutes: "minute", hours: "hour", days: "day", months: "month", years: "year" };
  return `${amount} ${amount === 1 ? singular[unit] : unit}`;
}

/* ------------------------------------------------------------------ expiry */

function addCalendar(date, amount, unit) {
  const next = new Date(date.getTime());
  if (unit === "years") next.setUTCFullYear(next.getUTCFullYear() + amount);
  else next.setUTCMonth(next.getUTCMonth() + amount);

  // clamp 31 Jan + 1 month → 28/29 Feb instead of rolling into March
  if (next.getUTCDate() !== date.getUTCDate()) next.setUTCDate(0);
  return next;
}

/** Absolute expiry for a key created at `createdAtISO`, or null for lifetime keys. */
export function expiresAt(createdAtISO, duration) {
  if (!duration || duration.lifetime) return null;
  const created = new Date(createdAtISO);
  if (Number.isNaN(created.getTime())) return null;

  if (duration.unit === "months" || duration.unit === "years") {
    return addCalendar(created, duration.amount, duration.unit).toISOString();
  }
  if (UNIT_SECONDS[duration.unit]) {
    return new Date(created.getTime() + duration.amount * UNIT_SECONDS[duration.unit] * 1000).toISOString();
  }
  return null;
}

/** Live status of a key, recomputed on every read so nothing goes stale. */
export function keyStatus(record, now = Date.now()) {
  if (record.revoked) return "revoked";
  if (!record.expiresAt) return "lifetime";
  const expires = new Date(record.expiresAt).getTime();
  if (Number.isNaN(expires)) return "active";
  if (expires <= now) return "expired";
  if (expires - now <= 7 * 86_400_000) return "expiring";
  return "active";
}

export function remainingSeconds(record, now = Date.now()) {
  if (!record.expiresAt) return null;
  const expires = new Date(record.expiresAt).getTime();
  if (Number.isNaN(expires)) return null;
  return Math.max(0, Math.round((expires - now) / 1000));
}

function ok(value) {
  return { ok: true, value };
}
function error(message) {
  return { ok: false, error: message };
}
