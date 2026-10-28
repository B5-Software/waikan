const JSON_TYPE = "application/json; charset=utf-8";
export const SNAPSHOT_KEY = "snapshot:v1";
export const VERSION = "1.0.0";
export const MAX_EVENTS_PER_DAY = 500;
export const RETENTION_DAYS = 1095;
export const DETAIL_RETENTION_DAYS = 180;

export function getPairName(env) {
  return String(env.PAIR_NAME || "外刊 · 云端计数").trim();
}

export function getTimeZone(env) {
  return String(env.TIMEZONE || "Asia/Shanghai").trim() || "Asia/Shanghai";
}

export function json(data, status = 200, headers = {}) {
  const result = new Headers(headers);
  result.set("content-type", JSON_TYPE);
  if (!result.has("cache-control")) result.set("cache-control", "no-store");
  return new Response(JSON.stringify(data), { status, headers: result });
}

export function error(message, status = 400) {
  return json({ error: message }, status);
}

export async function sha256Hex(value) {
  const digest = await crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(value),
  );
  return Array.from(new Uint8Array(digest))
    .map((byte) => byte.toString(16).padStart(2, "0"))
    .join("");
}

function timingSafeStringEqual(a, b) {
  const left = String(a || "");
  const right = String(b || "");
  if (left.length !== right.length) return false;
  let diff = 0;
  for (let index = 0; index < left.length; index += 1) {
    diff |= left.charCodeAt(index) ^ right.charCodeAt(index);
  }
  return diff === 0;
}

export async function isAuthorized(request, env) {
  const authorization = request.headers.get("authorization") || "";
  const match = authorization.match(/^Bearer\s+(.+)$/i);
  if (!match) return false;
  const expected = String(env.CLIENT_KEY_HASH || "").trim().toLowerCase();
  if (!/^[a-f0-9]{64}$/.test(expected)) return false;
  const actual = await sha256Hex(match[1].trim());
  return timingSafeStringEqual(actual, expected);
}

export function validDateKey(value) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(String(value || ""))) return false;
  const time = Date.parse(`${value}T00:00:00Z`);
  if (!Number.isFinite(time)) return false;
  return new Date(time).toISOString().slice(0, 10) === value;
}

export function dateKeyToUtcMs(value) {
  const [year, month, day] = value.split("-").map(Number);
  return Date.UTC(year, month - 1, day);
}

export function addDays(value, delta) {
  return new Date(dateKeyToUtcMs(value) + delta * 86400000)
    .toISOString()
    .slice(0, 10);
}

export function dayOfWeek(value) {
  return new Date(dateKeyToUtcMs(value)).getUTCDay();
}

export function dateKeyInTimeZone(instant, timeZone) {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(instant);
  const values = Object.fromEntries(parts.map((part) => [part.type, part.value]));
  return `${values.year}-${values.month}-${values.day}`;
}

export function todayKeyInTimeZone(timeZone) {
  return dateKeyInTimeZone(new Date(), timeZone);
}

export function hourInTimeZone(instant, timeZone) {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone,
    hour: "2-digit",
    hourCycle: "h23",
  }).formatToParts(instant);
  const value = parts.find((part) => part.type === "hour")?.value;
  const hour = Number(value);
  return Number.isInteger(hour) && hour >= 0 && hour <= 23 ? hour : 0;
}

export function rangeDateKeys(endDate, days) {
  const count = Math.max(1, Math.min(366, Math.trunc(days) || 30));
  const result = [];
  for (let offset = count - 1; offset >= 0; offset -= 1) {
    result.push(addDays(endDate, -offset));
  }
  return result;
}

export function normalizeEvent(raw, timeZone) {
  if (!raw || typeof raw !== "object") throw new Error("事件格式无效");
  const id = String(raw.id || "").trim();
  if (!/^[A-Za-z0-9_-]{6,100}$/.test(id)) throw new Error("事件 ID 无效");

  const atDate = new Date(String(raw.at || ""));
  if (!Number.isFinite(atDate.getTime())) throw new Error("事件时间无效");

  const localDay = validDateKey(raw.localDay)
    ? String(raw.localDay)
    : dateKeyInTimeZone(atDate, timeZone);

  const hour = Number.isInteger(raw.hour) && raw.hour >= 0 && raw.hour <= 23
    ? raw.hour
    : hourInTimeZone(atDate, timeZone);

  const source = raw.source === "system" ? "system" : "microphone";
  return {
    id,
    at: atDate.toISOString(),
    localDay,
    hour,
    source,
  };
}

export function emptySnapshot() {
  return {
    version: 1,
    updatedAt: null,
    days: {},
  };
}

export async function loadSnapshot(env) {
  if (!env.COUNTS) {
    throw new Error("缺少 KV 绑定 COUNTS");
  }
  const value = await env.COUNTS.get(SNAPSHOT_KEY, { type: "json" });
  if (!value || typeof value !== "object" || !value.days) {
    return emptySnapshot();
  }
  return {
    version: 1,
    updatedAt: value.updatedAt || null,
    days: value.days,
  };
}

export async function saveSnapshot(env, snapshot) {
  if (!env.COUNTS) {
    throw new Error("缺少 KV 绑定 COUNTS");
  }
  await env.COUNTS.put(SNAPSHOT_KEY, JSON.stringify(snapshot));
}

export function pruneSnapshot(snapshot, today, retentionDays = RETENTION_DAYS) {
  const threshold = addDays(today, -retentionDays);
  const detailThreshold = addDays(today, -DETAIL_RETENTION_DAYS);
  for (const day of Object.keys(snapshot.days)) {
    if (!validDateKey(day) || day < threshold) {
      delete snapshot.days[day];
      continue;
    }

    // Keep long-term daily totals while bounding the KV value size. Detailed
    // timestamps are only needed by the 90-day viewer and recent list.
    if (day < detailThreshold && Array.isArray(snapshot.days[day].events)) {
      delete snapshot.days[day].events;
    }
  }
}

export function dailySummary(day) {
  return {
    count: Number(day?.count) || 0,
    mic: Number(day?.mic) || 0,
    system: Number(day?.system) || 0,
    firstAt: day?.firstAt || null,
    lastAt: day?.lastAt || null,
  };
}

export function sortEvents(events) {
  return [...(events || [])].sort((a, b) => String(a.at).localeCompare(String(b.at)));
}

export function clampDays(value) {
  const parsed = Number.parseInt(value || "30", 10);
  if (!Number.isFinite(parsed)) return 30;
  return Math.max(1, Math.min(366, parsed));
}

export function securityHeadersForApi(headers = {}) {
  const result = new Headers(headers);
  result.set("x-content-type-options", "nosniff");
  result.set("referrer-policy", "no-referrer");
  return result;
}
