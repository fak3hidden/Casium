/* ==================================================================
   Casium keys API — request/response helpers
   ================================================================== */

const MAX_BODY_BYTES = 64 * 1024;

export function json(data, status = 200, headers = {}) {
  return new Response(JSON.stringify(data), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store",
      "x-content-type-options": "nosniff",
      ...headers,
    },
  });
}

export function fail(status, error, message, extra = {}) {
  return json({ ok: false, error, message, ...extra }, status);
}

/** Parse a JSON body without blowing up on junk, empties or oversized posts. */
export async function readJson(req) {
  const length = Number(req.headers.get("content-length") || 0);
  if (length > MAX_BODY_BYTES) return { ok: false, error: "payload_too_large", message: "Request body is too large." };

  let text = "";
  try {
    text = await req.text();
  } catch {
    return { ok: false, error: "bad_request", message: "Could not read the request body." };
  }
  if (!text) return { ok: true, value: {} };
  if (text.length > MAX_BODY_BYTES) return { ok: false, error: "payload_too_large", message: "Request body is too large." };

  try {
    const value = JSON.parse(text);
    if (!value || typeof value !== "object" || Array.isArray(value)) {
      return { ok: false, error: "bad_request", message: "Expected a JSON object." };
    }
    return { ok: true, value };
  } catch {
    return { ok: false, error: "bad_json", message: "Body is not valid JSON." };
  }
}

export function methodNotAllowed(allowed) {
  return json({ ok: false, error: "method_not_allowed", message: `Use ${allowed.join(" or ")}.` }, 405, {
    allow: allowed.join(", "),
  });
}

export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Relative URLs survive previews, proxies and custom domains alike. */
export function originOf(req) {
  try {
    return new URL(req.url).origin;
  } catch {
    return "";
  }
}
