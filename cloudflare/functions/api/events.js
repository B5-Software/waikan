import {
  MAX_EVENTS_PER_DAY,
  getTimeZone,
  isAuthorized,
  json,
  loadSnapshot,
  normalizeEvent,
  pruneSnapshot,
  saveSnapshot,
  sortEvents,
  todayKeyInTimeZone,
  validDateKey,
} from "../_lib.js";

const MAX_BODY_BYTES = 1024 * 1024;
const MAX_EVENTS_PER_REQUEST = 500;

function makeDay() {
  return {
    count: 0,
    mic: 0,
    system: 0,
    hourly: Array(24).fill(0),
    firstAt: null,
    lastAt: null,
    events: [],
  };
}

function normalizeDay(day) {
  if (!day || typeof day !== "object") return makeDay();
  const hourly = Array.isArray(day.hourly) && day.hourly.length === 24
    ? day.hourly.map((value) => Math.max(0, Number(value) || 0))
    : Array(24).fill(0);
  return {
    count: Math.max(0, Number(day.count) || 0),
    mic: Math.max(0, Number(day.mic) || 0),
    system: Math.max(0, Number(day.system) || 0),
    hourly,
    firstAt: day.firstAt || null,
    lastAt: day.lastAt || null,
    events: Array.isArray(day.events) ? day.events : [],
  };
}

export async function onRequestPost(context) {
  const timeZone = getTimeZone(context.env);
  if (!(await isAuthorized(context.request, context.env))) {
    return json({ error: "密钥无效" }, 401);
  }

  const contentLength = Number(context.request.headers.get("content-length") || 0);
  if (contentLength > MAX_BODY_BYTES) {
    return json({ error: "请求体过大" }, 413);
  }

  let body;
  try {
    const text = await context.request.text();
    if (text.length > MAX_BODY_BYTES) {
      return json({ error: "请求体过大" }, 413);
    }
    body = JSON.parse(text);
  } catch {
    return json({ error: "JSON 请求体无效" }, 400);
  }

  if (!Array.isArray(body?.events) || body.events.length === 0) {
    return json({ error: "events 必须是非空数组" }, 400);
  }
  if (body.events.length > MAX_EVENTS_PER_REQUEST) {
    return json({ error: `单次最多接收 ${MAX_EVENTS_PER_REQUEST} 条事件` }, 413);
  }

  let incoming;
  try {
    incoming = body.events.map((event) => normalizeEvent(event, timeZone));
  } catch (cause) {
    return json({ error: cause.message }, 400);
  }

  let snapshot;
  try {
    snapshot = await loadSnapshot(context.env);
  } catch (cause) {
    return json({ error: cause.message }, 500);
  }

  const byDay = new Map();
  for (const event of incoming) {
    if (!byDay.has(event.localDay)) byDay.set(event.localDay, []);
    byDay.get(event.localDay).push(event);
  }

  let accepted = 0;
  let duplicates = 0;

  for (const [dayKey, events] of byDay) {
    if (!validDateKey(dayKey)) continue;
    const day = normalizeDay(snapshot.days[dayKey] || makeDay());
    const ids = new Set(day.events.map((event) => String(event.id)));
    const seenInRequest = new Set();

    for (const event of events) {
      if (seenInRequest.has(event.id) || ids.has(event.id)) {
        duplicates += 1;
        continue;
      }
      seenInRequest.add(event.id);
      ids.add(event.id);

      day.count += 1;
      if (event.source === "system") {
        day.system += 1;
      } else {
        day.mic += 1;
      }
      day.hourly[event.hour] += 1;
      if (!day.firstAt || event.at < day.firstAt) day.firstAt = event.at;
      if (!day.lastAt || event.at > day.lastAt) day.lastAt = event.at;
      day.events.push({
        id: event.id,
        at: event.at,
        source: event.source,
      });
      accepted += 1;
    }

    day.events = sortEvents(day.events).slice(-MAX_EVENTS_PER_DAY);
    snapshot.days[dayKey] = day;
  }

  // Do not burn a KV write for a batch that only contained duplicates.
  if (accepted === 0) {
    return json({
      ok: true,
      received: incoming.length,
      accepted: 0,
      duplicates,
      updatedAt: snapshot.updatedAt,
    });
  }

  const today = todayKeyInTimeZone(timeZone);
  pruneSnapshot(snapshot, today);
  snapshot.updatedAt = new Date().toISOString();

  try {
    await saveSnapshot(context.env, snapshot);
  } catch (cause) {
    return json({ error: cause.message }, 500);
  }

  return json({
    ok: true,
    received: incoming.length,
    accepted,
    duplicates,
    updatedAt: snapshot.updatedAt,
  });
}
