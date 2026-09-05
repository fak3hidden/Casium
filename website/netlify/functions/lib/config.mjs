/* ==================================================================
   Casium keys API — configuration
   ------------------------------------------------------------------
   Every value here can be overridden from the Netlify dashboard:
   Site configuration → Environment variables → add → deploy.

   CASIUM_ADMIN_USER       username for the keys console   (default: admin)
   CASIUM_ADMIN_PASS       password for the keys console   (default: casium-keys)
   CASIUM_SESSION_SECRET   long random string for session tokens (recommended)
   CASIUM_DATA_FILE        file used when Netlify Blobs is unavailable (dev)
   ================================================================== */

export const DEFAULT_USERNAME = "admin";
export const DEFAULT_PASSWORD = "casium-keys";

export const SESSION_TTL_SECONDS = 12 * 60 * 60; // 12 hours
export const MAX_LOGIN_ATTEMPTS = 8;
export const LOCKOUT_SECONDS = 10 * 60; // 10 minutes

export function env(name, fallback = undefined) {
  const value = process.env[name];
  return value === undefined || value === "" ? fallback : value;
}

/** The credentials from the environment (used until the owner changes them in the console). */
export function envCredentials() {
  return {
    username: env("CASIUM_ADMIN_USER", DEFAULT_USERNAME),
    password: env("CASIUM_ADMIN_PASS", DEFAULT_PASSWORD),
    source: "environment",
  };
}

/** True while the built-in default password is still in use — the console warns about this. */
export function usingDefaultPassword(stored) {
  if (stored) return false;
  return env("CASIUM_ADMIN_PASS", DEFAULT_PASSWORD) === DEFAULT_PASSWORD;
}
