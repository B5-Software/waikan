import {
  VERSION,
  addDays,
  clampDays,
  dailySummary,
  dayOfWeek,
  getPairName,
  getTimeZone,
  json,
  loadSnapshot,
  rangeDateKeys,
  todayKeyInTimeZone,
  validDateKey,
} from "../_lib.js";

function sumCounts(days, keys) {
  let total = 0;
  for (const key of keys) total += Number(days[key]?.count) || 0;
  return total;
}

function calculateLongestStreak(dayKeys) {
  if (dayKeys.length === 0) return 0;
  const sorted = [...dayKeys].sort();
  let best = 1;
  let current = 1;
  for (let index = 1; index < sorted.length; index += 1) {
    if (sorted[index] === addDays(sorted[index - 1], 1)) {
      current += 1;
    } else {
      current = 1;
    }
    best = Math.max(best, current);
  }
  return best;
}

function calculateCurrentStreak(days, today) {
  let cursor = today;
  let streak = 0;
  while ((Number(days[cursor]?.count) || 0) > 0) {
    streak += 1;
    cursor = addDays(cursor, -1);
  }
  return streak;
}

export async function onRequestGet(context) {
  const url = new URL(context.request.url);
  const daysRequested = clampDays(url.searchParams.get("days"));
  const timezone = getTimeZone(context.env);
  const today = todayKeyInTimeZone(timezone);

  let snapshot;
  try {
    snapshot = await loadSnapshot(context.env);
  } catch (cause) {
    return json({ error: cause.message }, 500);
  }

  const days = snapshot.days || {};
  const keys = rangeDateKeys(today, daysRequested);
  const daily = keys.map((date) => {
    const value = dailySummary(days[date]);
    const hourly = Array.isArray(days[date]?.hourly) && days[date].hourly.length === 24
      ? days[date].hourly.map((count) => Math.max(0, Number(count) || 0))
      : Array(24).fill(0);
    return { date, hourly, ...value };
  });

  const rangeTotal = daily.reduce((sum, item) => sum + item.count, 0);
  const activeDays = daily.filter((item) => item.count > 0).length;
  const best = daily.reduce(
    (winner, item) => (!winner || item.count > winner.count ? item : winner),
    null,
  );
  const activeKeys = Object.keys(days)
    .filter((date) => validDateKey(date) && (Number(days[date]?.count) || 0) > 0)
    .sort();
  const allKeys = Object.keys(days).filter(validDateKey).sort();
  const totalAll = allKeys.reduce(
    (sum, date) => sum + (Number(days[date]?.count) || 0),
    0,
  );

  const hourly = Array(24).fill(0);
  const weekdays = Array(7).fill(0);
  const sources = { microphone: 0, system: 0 };
  let lastEventAt = null;

  for (const date of keys) {
    const value = days[date];
    if (!value) continue;
    if (Array.isArray(value.hourly) && value.hourly.length === 24) {
      value.hourly.forEach((count, index) => {
        hourly[index] += Number(count) || 0;
      });
    }
    weekdays[dayOfWeek(date)] += Number(value.count) || 0;
    sources.microphone += Number(value.mic) || 0;
    sources.system += Number(value.system) || 0;
  }

  for (const date of allKeys) {
    const value = days[date];
    if (value?.lastAt && (!lastEventAt || value.lastAt > lastEventAt)) {
      lastEventAt = value.lastAt;
    }
  }

  const recent = [];
  for (const date of [...allKeys].sort().reverse()) {
    const events = Array.isArray(days[date]?.events) ? days[date].events : [];
    for (const event of [...events].sort((a, b) => String(b.at).localeCompare(String(a.at)))) {
      recent.push({ date, at: event.at, source: event.source || "microphone" });
      if (recent.length >= 12) break;
    }
    if (recent.length >= 12) break;
  }

  const payload = {
    pair: getPairName(context.env),
    timezone,
    version: VERSION,
    today,
    generatedAt: new Date().toISOString(),
    updatedAt: snapshot.updatedAt || null,
    summary: {
      todayCount: Number(days[today]?.count) || 0,
      rangeTotal,
      totalAll,
      activeDays,
      activeDaysAll: activeKeys.length,
      currentStreak: calculateCurrentStreak(days, today),
      longestStreak: calculateLongestStreak(activeKeys),
      bestDay: best && best.count > 0 ? { date: best.date, count: best.count } : null,
      firstEver: activeKeys[0] || null,
      lastEventAt,
      microphone: sources.microphone,
      system: sources.system,
    },
    daily,
    hourly,
    weekdays,
    recent,
  };

  return json(payload, 200, {
    "cache-control": "public, max-age=15, s-maxage=30",
  });
}
