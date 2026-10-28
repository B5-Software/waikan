import {
  getPairName,
  json,
  loadSnapshot,
  validDateKey,
} from "../_lib.js";

export async function onRequestGet(context) {
  const url = new URL(context.request.url);
  const date = String(url.searchParams.get("date") || "");
  if (!validDateKey(date)) {
    return json({ error: "date 必须是 YYYY-MM-DD" }, 400);
  }

  let snapshot;
  try {
    snapshot = await loadSnapshot(context.env);
  } catch (cause) {
    return json({ error: cause.message }, 500);
  }

  const day = snapshot.days?.[date];
  const events = Array.isArray(day?.events)
    ? [...day.events].sort((a, b) => String(a.at).localeCompare(String(b.at)))
    : [];

  return json({
    pair: getPairName(context.env),
    date,
    count: Number(day?.count) || 0,
    microphone: Number(day?.mic) || 0,
    system: Number(day?.system) || 0,
    firstAt: day?.firstAt || null,
    lastAt: day?.lastAt || null,
    events,
  }, 200, {
    "cache-control": "public, max-age=15, s-maxage=30",
  });
}
