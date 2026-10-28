import {
  getPairName,
  getTimeZone,
  isAuthorized,
  json,
  todayKeyInTimeZone,
} from "../_lib.js";

export async function onRequestGet(context) {
  if (!(await isAuthorized(context.request, context.env))) {
    return json({ error: "密钥无效" }, 401);
  }

  return json({
    ok: true,
    pair: getPairName(context.env),
    timezone: getTimeZone(context.env),
    today: todayKeyInTimeZone(getTimeZone(context.env)),
    serverTime: new Date().toISOString(),
  }, 200, { "cache-control": "no-store" });
}
