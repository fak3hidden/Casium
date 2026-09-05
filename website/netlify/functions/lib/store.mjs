/* ==================================================================
   Casium keys API — storage
   ------------------------------------------------------------------
   1. Netlify Blobs  (used automatically once deployed — survives redeploys)
   2. JSON file      (local dev server; path from CASIUM_DATA_FILE)
   3. Memory         (last resort, so a broken deploy still answers requests)

   The whole console state lives in one small JSON document:
   { tokenSalt, credentials, keys[], loginAttempts{}, updatedAt }
   ================================================================== */

import path from "node:path";
import { fileURLToPath } from "node:url";
import crypto from "node:crypto";

const BLOB_STORE = "casium-keys";
const BLOB_KEY = "state.json";

const here = path.dirname(fileURLToPath(import.meta.url));
const DEFAULT_FILE = path.resolve(here, "../../../tools/.data/state.json");

let backend = null;

function emptyState() {
  return {
    version: 1,
    tokenSalt: crypto.randomBytes(16).toString("hex"),
    credentials: null,
    keys: [],
    loginAttempts: {},
    updatedAt: null,
  };
}

function withTimeout(promise, ms) {
  let timer;
  return Promise.race([
    promise,
    new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(`timed out after ${ms}ms`)), ms);
    }),
  ]).finally(() => clearTimeout(timer));
}

async function blobsBackend() {
  const { getStore } = await import("@netlify/blobs");
  const options = { name: BLOB_STORE };
  if (process.env.CASIUM_SITE_ID) options.siteID = process.env.CASIUM_SITE_ID;
  if (process.env.CASIUM_BLOB_TOKEN) options.token = process.env.CASIUM_BLOB_TOKEN;

  const store = getStore(options);
  /* proves the store is reachable before we trust it — bounded, so an
     unreachable/misconfigured Blobs endpoint degrades to the next backend
     instead of stalling every request */
  await withTimeout(store.getJSON(BLOB_KEY), 2500);

  return {
    kind: "netlify-blobs",
    read: () => store.getJSON(BLOB_KEY),
    write: (state) => store.setJSON(BLOB_KEY, state),
  };
}

async function fileBackend() {
  const { mkdir, readFile, writeFile, rename, rm } = await import("node:fs/promises");
  const file = process.env.CASIUM_DATA_FILE || DEFAULT_FILE;
  await mkdir(path.dirname(file), { recursive: true });

  /* Prove the location is writable BEFORE trusting it as durable storage: a
     read-only directory would otherwise mint a fresh state on every read and
     invalidate every session. Failing here falls through to the next backend. */
  const probe = `${file}.probe`;
  await writeFile(probe, "ok", "utf8");
  await readFile(probe, "utf8");
  await rm(probe, { force: true });

  return {
    kind: "file",
    location: file,
    read: async () => {
      try {
        return JSON.parse(await readFile(file, "utf8"));
      } catch {
        return null;
      }
    },
    write: async (state) => {
      const tmp = `${file}.${process.pid}.tmp`;
      await writeFile(tmp, JSON.stringify(state, null, 2), "utf8");
      await rename(tmp, file); // atomic on the same filesystem
    },
  };
}

function memoryBackend() {
  let data = null;
  return {
    kind: "memory",
    read: async () => data,
    write: async (state) => { data = state; },
  };
}

async function resolveBackend() {
  if (backend) return backend;
  if (process.env.CASIUM_STORAGE === "file") {
    backend = await fileBackend();
    return backend;
  }
  try {
    backend = await blobsBackend();
  } catch {
    try {
      backend = await fileBackend();
    } catch {
      backend = memoryBackend();
    }
  }
  return backend;
}

export async function storageKind() {
  return (await resolveBackend()).kind;
}

/** Read state, creating and persisting a fresh one the first time it is asked for. */
export async function readState() {
  const store = await resolveBackend();
  const stored = await store.read();
  if (stored && typeof stored === "object") {
    return {
      ...emptyState(),
      ...stored,
      keys: Array.isArray(stored.keys) ? stored.keys : [],
      loginAttempts: stored.loginAttempts && typeof stored.loginAttempts === "object" ? stored.loginAttempts : {},
    };
  }
  const fresh = emptyState();
  try {
    await store.write(fresh);
  } catch {
    /* read-only storage: keep going in memory for this invocation */
  }
  return fresh;
}

export async function writeState(state) {
  const store = await resolveBackend();
  state.updatedAt = new Date().toISOString();
  await store.write(state);
  return state;
}

/**
 * Read → mutate → write. Netlify Blobs has no transactions, so the console
 * serialises its own writes with a per-instance promise chain. That is enough
 * for a single-admin console; do not use it as a high-write datastore.
 */
let queue = Promise.resolve();

export function withState(mutator) {
  const run = queue.then(async () => {
    const state = await readState();
    const result = await mutator(state);
    if (result?.persist !== false) await writeState(state);
    return result?.value !== undefined ? result.value : result;
  });
  queue = run.catch(() => {});
  return run;
}
