const REFRESH_MS = 45_000;
const STATS_DAYS = 90;
const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

const state = {
  data: null,
  days: Number(localStorage.getItem("waikan-cloud-range")) || 30,
  busy: false,
  lastFetch: 0,
  dayCache: new Map(),
  controller: null,
};

if (![7, 30, 90].includes(state.days)) state.days = 30;

const el = {
  connection: document.querySelector("#connection"),
  connectionText: document.querySelector("#connectionText"),
  pairPill: document.querySelector("#pairPill"),
  refreshButton: document.querySelector("#refreshButton"),
  themeButton: document.querySelector("#themeButton"),
  todayLabel: document.querySelector("#todayLabel"),
  todayCount: document.querySelector("#todayCount"),
  heroNote: document.querySelector("#heroNote"),
  lastSync: document.querySelector("#lastSync"),
  timezoneLabel: document.querySelector("#timezoneLabel"),
  weekTotal: document.querySelector("#weekTotal"),
  miniBars: document.querySelector("#miniBars"),
  totalAll: document.querySelector("#totalAll"),
  activeDaysAll: document.querySelector("#activeDaysAll"),
  metricGrid: document.querySelector("#metricGrid"),
  rangeSwitch: document.querySelector("#rangeSwitch"),
  exportButton: document.querySelector("#exportButton"),
  dailyChart: document.querySelector("#dailyChart"),
  chartLabels: document.querySelector("#chartLabels"),
  heatmap: document.querySelector("#heatmap"),
  hourChart: document.querySelector("#hourChart"),
  hoursSub: document.querySelector("#hoursSub"),
  recentList: document.querySelector("#recentList"),
  openTodayButton: document.querySelector("#openTodayButton"),
  sourceBlock: document.querySelector("#sourceBlock"),
  summaryList: document.querySelector("#summaryList"),
  endpointDisplay: document.querySelector("#endpointDisplay"),
  footerPair: document.querySelector("#footerPair"),
  footerUpdated: document.querySelector("#footerUpdated"),
  dayDialog: document.querySelector("#dayDialog"),
  closeDialog: document.querySelector("#closeDialog"),
  dialogTitle: document.querySelector("#dialogTitle"),
  dialogSummary: document.querySelector("#dialogSummary"),
  dialogBody: document.querySelector("#dialogBody"),
  toast: document.querySelector("#toast"),
};

const numberFormatter = new Intl.NumberFormat("zh-CN");
const dateFormatter = new Intl.DateTimeFormat("zh-CN", {
  month: "long",
  day: "numeric",
  weekday: "short",
  timeZone: "UTC",
});
const shortDateFormatter = new Intl.DateTimeFormat("zh-CN", {
  month: "numeric",
  day: "numeric",
  timeZone: "UTC",
});
const weekdayFormatter = new Intl.DateTimeFormat("zh-CN", {
  weekday: "short",
  timeZone: "UTC",
});

initTheme();
syncRangeButtons();
bindEvents();
setConnection("", "连接中");
loadStats();

function initTheme() {
  const saved = localStorage.getItem("waikan-cloud-theme");
  const theme = saved === "light" || saved === "dark"
    ? saved
    : (window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
  document.documentElement.dataset.theme = theme;
  updateThemeColor();
}

function updateThemeColor() {
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) {
    meta.content = document.documentElement.dataset.theme === "light" ? "#f4f0e9" : "#0b0e13";
  }
}

function syncRangeButtons() {
  el.rangeSwitch.querySelectorAll("button[data-days]").forEach((button) => {
    button.classList.toggle("is-active", Number(button.dataset.days) === state.days);
  });
}

function bindEvents() {
  el.refreshButton.addEventListener("click", () => loadStats({ manual: true }));
  el.themeButton.addEventListener("click", () => {
    const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
    document.documentElement.dataset.theme = next;
    localStorage.setItem("waikan-cloud-theme", next);
    updateThemeColor();
  });
  el.rangeSwitch.addEventListener("click", (event) => {
    const button = event.target.closest("button[data-days]");
    if (!button) return;
    const days = Number(button.dataset.days);
    if (![7, 30, 90].includes(days) || days === state.days) return;
    state.days = days;
    localStorage.setItem("waikan-cloud-range", String(days));
    el.rangeSwitch.querySelectorAll("button").forEach((item) => {
      item.classList.toggle("is-active", item === button);
    });
    if (state.data) render();
  });
  el.exportButton.addEventListener("click", exportCsv);
  el.openTodayButton.addEventListener("click", () => {
    if (state.data?.today) openDay(state.data.today);
  });
  el.closeDialog.addEventListener("click", () => el.dayDialog.close());
  el.dayDialog.addEventListener("click", (event) => {
    if (event.target === el.dayDialog) el.dayDialog.close();
  });
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden && Date.now() - state.lastFetch > 30_000) {
      loadStats({ silent: true });
    }
  });
  window.setInterval(() => {
    if (!document.hidden) loadStats({ silent: true });
  }, REFRESH_MS);
}

async function loadStats({ manual = false, silent = false } = {}) {
  if (state.busy) return;
  state.busy = true;
  state.controller?.abort();
  state.controller = new AbortController();
  el.refreshButton.classList.add("is-spinning");
  if (!silent && !state.data) setConnection("", "正在读取");
  if (manual && state.data) showToast("正在刷新…");

  try {
    const response = await fetch(`/api/stats?days=${STATS_DAYS}`, {
      headers: { accept: "application/json" },
      cache: "no-store",
      signal: state.controller.signal,
    });
    if (!response.ok) {
      let message = `HTTP ${response.status}`;
      try {
        const payload = await response.json();
        if (payload?.error) message = payload.error;
      } catch {
        // Keep the status code fallback.
      }
      throw new Error(message);
    }
    const data = await response.json();
    state.data = data;
    state.dayCache.clear();
    state.lastFetch = Date.now();
    render();
    setConnection("ok", "已连接");
    if (manual) showToast("数据已刷新");
  } catch (cause) {
    if (cause.name === "AbortError") return;
    setConnection("error", "连接失败");
    if (!silent) showToast(cause.message || "读取云端数据失败", true);
  } finally {
    state.busy = false;
    el.refreshButton.classList.remove("is-spinning");
  }
}

function render() {
  const data = state.data;
  if (!data) return;
  const todayCount = Number(data.summary?.todayCount) || 0;
  animateNumber(el.todayCount, todayCount);
  el.todayLabel.textContent = formatFullDate(data.today, { includeWeekday: true });
  el.pairPill.textContent = data.pair || "外刊 · 云端计数";
  el.timezoneLabel.textContent = data.timezone || "Asia/Shanghai";
  el.totalAll.textContent = numberFormatter.format(Number(data.summary?.totalAll) || 0);
  el.activeDaysAll.textContent = numberFormatter.format(Number(data.summary?.activeDaysAll) || 0);
  el.lastSync.textContent = data.updatedAt
    ? `最近同步 ${relativeTime(data.updatedAt)}`
    : "等待第一次同步";
  el.footerPair.textContent = data.pair || "外刊 · 云端计数";
  el.footerUpdated.textContent = data.updatedAt
    ? `同步于 ${formatEventDateTime(data.updatedAt, data.timezone)}`
    : "等待第一次同步";
  el.endpointDisplay.textContent = window.location.origin;

  if ((Number(data.summary?.totalAll) || 0) === 0) {
    el.heroNote.textContent = "这里还没有数据。在桌面端设置中填入此站点地址和配对密钥，打开同步后，第一声「外刊」就会出现在这里。";
  } else {
    el.heroNote.textContent = "这是一份只记录关键词的安静档案。所有识别都在本机完成，这里只接收时间与计数。";
  }

  renderWeek(data);
  renderMetrics(data);
  renderChart(data);
  renderHeatmap(data);
  renderHours(data);
  renderRecent(data);
  renderOverview(data);
}

function renderWeek(data) {
  const daily = (data.daily || []).slice(-7);
  const max = Math.max(1, ...daily.map((day) => day.count));
  el.weekTotal.textContent = numberFormatter.format(daily.reduce((sum, day) => sum + day.count, 0));
  el.miniBars.textContent = "";
  for (const day of daily) {
    const bar = document.createElement("div");
    bar.className = "mini-bar";
    bar.title = `${formatFullDate(day.date)} · ${day.count} 次`;
    if (day.date === data.today) bar.classList.add("is-today");
    const fill = document.createElement("span");
    const height = day.count === 0 ? 2 : Math.max(7, Math.round((day.count / max) * 100));
    fill.style.height = `${height}%`;
    bar.append(fill);
    const mark = document.createElement("em");
    mark.textContent = day.count > 0 ? String(day.count) : "";
    bar.append(mark);
    el.miniBars.append(bar);
  }
  if (daily.length === 0) {
    el.miniBars.textContent = "";
  }
}

function renderMetrics(data) {
  const daily = (data.daily || []).slice(-state.days);
  const rangeTotal = daily.reduce((sum, day) => sum + day.count, 0);
  const active = daily.filter((day) => day.count > 0).length;
  const best = daily.reduce(
    (winner, day) => (!winner || day.count > winner.count ? day : winner),
    null,
  );
  const values = [
    ["范围总次数", numberFormatter.format(rangeTotal), `近 ${state.days} 天`],
    ["有记录天数", numberFormatter.format(active), `${state.days} 天中`],
    ["当前连续", numberFormatter.format(Number(data.summary?.currentStreak) || 0), "连续出现天数"],
    ["单日最高", numberFormatter.format(best?.count || 0), best?.date ? formatShortDate(best.date) : "暂无记录"],
  ];
  el.metricGrid.textContent = "";
  for (const [label, value, note] of values) {
    const card = document.createElement("article");
    card.className = "metric";
    const labelEl = document.createElement("span");
    labelEl.className = "metric-label";
    labelEl.textContent = label;
    const valueEl = document.createElement("strong");
    valueEl.className = "metric-value";
    valueEl.textContent = value;
    if (label !== "有记录天数") {
      const unit = document.createElement("small");
      unit.textContent = label === "当前连续" || label === "单日最高" ? "天" : "次";
      valueEl.append(unit);
    }
    const noteEl = document.createElement("span");
    noteEl.className = "metric-note";
    noteEl.textContent = note;
    card.append(labelEl, valueEl, noteEl);
    el.metricGrid.append(card);
  }
}

function renderChart(data) {
  const daily = (data.daily || []).slice(-state.days);
  const max = Math.max(1, ...daily.map((day) => day.count));
  el.dailyChart.textContent = "";
  el.dailyChart.setAttribute("aria-label", `近 ${state.days} 天外刊出现次数`);
  el.chartLabels.textContent = "";

  daily.forEach((day, index) => {
    const bar = document.createElement("button");
    bar.type = "button";
    bar.className = "bar";
    if (day.count > 0) bar.classList.add("has-value");
    if (day.date === data.today) bar.classList.add("is-today");
    bar.style.setProperty("--bar-height", `${day.count === 0 ? 1.5 : Math.max(3, (day.count / max) * 100)}%`);
    bar.title = `${formatFullDate(day.date)} · ${day.count} 次`;
    bar.setAttribute("aria-label", `${formatFullDate(day.date)}，${day.count} 次`);
    bar.addEventListener("click", () => openDay(day.date));

    const fill = document.createElement("span");
    fill.className = "bar-fill";
    const value = document.createElement("span");
    value.className = "bar-value";
    value.textContent = String(day.count);
    bar.append(fill, value);
    el.dailyChart.append(bar);

    const label = document.createElement("span");
    const shouldLabel = state.days === 7
      || (state.days === 30 && (index % 5 === 0 || index === daily.length - 1))
      || (state.days === 90 && (index % 15 === 0 || index === daily.length - 1));
    label.textContent = shouldLabel ? formatShortDate(day.date) : "";
    el.chartLabels.append(label);
  });
}

function renderHeatmap(data) {
  const daily = (data.daily || []).slice(-90);
  const max = Math.max(1, ...daily.map((day) => day.count));
  const first = daily[0]?.date;
  if (!first) {
    el.heatmap.textContent = "";
    return;
  }
  const firstWeekday = (new Date(`${first}T00:00:00Z`).getUTCDay() + 6) % 7;
  const totalCells = firstWeekday + daily.length;
  const weeks = Math.ceil(totalCells / 7);
  el.heatmap.textContent = "";
  for (let weekIndex = 0; weekIndex < weeks; weekIndex += 1) {
    const week = document.createElement("div");
    week.className = "heat-week";
    for (let dayIndex = 0; dayIndex < 7; dayIndex += 1) {
      const cellIndex = weekIndex * 7 + dayIndex;
      const itemIndex = cellIndex - firstWeekday;
      if (itemIndex < 0 || itemIndex >= daily.length) {
        const blank = document.createElement("span");
        blank.className = "heat-cell";
        blank.style.visibility = "hidden";
        week.append(blank);
        continue;
      }
      const day = daily[itemIndex];
      const button = document.createElement("button");
      button.type = "button";
      button.className = `heat-cell level-${heatLevel(day.count, max)}`;
      if (day.date === data.today) button.classList.add("is-today");
      button.title = `${formatFullDate(day.date)} · ${day.count} 次`;
      button.setAttribute("aria-label", `${formatFullDate(day.date)}，${day.count} 次`);
      button.addEventListener("click", () => openDay(day.date));
      week.append(button);
    }
    el.heatmap.append(week);
  }
}

function heatLevel(count, max) {
  if (count <= 0) return 0;
  const ratio = count / Math.max(1, max);
  if (ratio <= 0.2) return 1;
  if (ratio <= 0.45) return 2;
  if (ratio <= 0.75) return 3;
  return 4;
}

function renderHours(data) {
  const daily = (data.daily || []).slice(-state.days);
  const hours = Array(24).fill(0);
  for (const day of daily) {
    if (Array.isArray(day.hourly)) {
      day.hourly.forEach((count, index) => {
        hours[index] += Number(count) || 0;
      });
    }
  }
  const max = Math.max(1, ...hours);
  el.hoursSub.textContent = `近 ${state.days} 天 · 按小时汇总`;
  el.hourChart.textContent = "";
  hours.forEach((count, index) => {
    const bar = document.createElement("div");
    bar.className = "hour-bar";
    if (count === max && count > 0) bar.classList.add("is-peak");
    bar.style.setProperty("--hour-height", `${count === 0 ? 1.5 : Math.max(3, (count / max) * 100)}%`);
    bar.title = `${String(index).padStart(2, "0")}:00 · ${count} 次`;
    const fill = document.createElement("i");
    const label = document.createElement("span");
    label.textContent = index % 3 === 0 ? `${index}时` : "";
    bar.append(fill, label);
    el.hourChart.append(bar);
  });
}

function renderRecent(data) {
  const recent = data.recent || [];
  el.recentList.textContent = "";
  if (!recent.length) {
    const empty = document.createElement("div");
    empty.className = "empty-recent";
    empty.textContent = "还没有出现记录";
    el.recentList.append(empty);
    return;
  }
  recent.forEach((event, index) => {
    const row = document.createElement("button");
    row.type = "button";
    row.className = "recent-row";
    row.style.width = "100%";
    row.style.background = "none";
    row.style.border = "0";
    row.style.textAlign = "left";
    row.style.cursor = "pointer";
    row.title = "查看当天明细";
    row.addEventListener("click", () => openDay(event.date));

    const idx = document.createElement("span");
    idx.className = "recent-index";
    idx.textContent = String(index + 1).padStart(2, "0");
    const main = document.createElement("span");
    main.className = "recent-main";
    const time = document.createElement("span");
    time.className = "recent-time";
    time.textContent = formatEventTime(event.at, data.timezone);
    const date = document.createElement("span");
    date.className = "recent-date";
    date.textContent = formatFullDate(event.date, { includeWeekday: true });
    main.append(time, document.createElement("br"), date);
    const source = document.createElement("span");
    source.className = `source-chip${event.source === "system" ? " system" : ""}`;
    source.textContent = event.source === "system" ? "系统声音" : "麦克风";
    row.append(idx, main, source);
    el.recentList.append(row);
  });
}

function renderOverview(data) {
  const daily = (data.daily || []).slice(-state.days);
  const mic = daily.reduce((sum, day) => sum + (Number(day.mic) || 0), 0);
  const system = daily.reduce((sum, day) => sum + (Number(day.system) || 0), 0);
  const total = mic + system;
  el.sourceBlock.textContent = "";
  const sources = [
    ["麦克风", mic],
    ["系统声音", system],
  ];
  for (const [label, count] of sources) {
    const line = document.createElement("div");
    line.className = "source-line";
    const name = document.createElement("span");
    name.textContent = label;
    const track = document.createElement("span");
    track.className = "source-track";
    const fill = document.createElement("i");
    fill.style.setProperty("--source-width", total ? `${Math.round((count / total) * 100)}%` : "0%");
    track.append(fill);
    const value = document.createElement("b");
    value.textContent = numberFormatter.format(count);
    line.append(name, track, value);
    el.sourceBlock.append(line);
  }

  const entries = [
    ["累计总数", `${numberFormatter.format(Number(data.summary?.totalAll) || 0)} 次`],
    ["有记录天数", `${numberFormatter.format(Number(data.summary?.activeDaysAll) || 0)} 天`],
    ["最长连续", `${numberFormatter.format(Number(data.summary?.longestStreak) || 0)} 天`],
    ["首次记录", data.summary?.firstEver ? formatShortDate(data.summary.firstEver) : "—"],
    ["最近事件", data.summary?.lastEventAt ? formatEventDateTime(data.summary.lastEventAt, data.timezone) : "—"],
    ["时区", data.timezone || "Asia/Shanghai"],
  ];
  el.summaryList.textContent = "";
  for (const [label, value] of entries) {
    const wrapper = document.createElement("div");
    const dt = document.createElement("dt");
    dt.textContent = label;
    const dd = document.createElement("dd");
    dd.textContent = value;
    wrapper.append(dt, dd);
    el.summaryList.append(wrapper);
  }
}

async function openDay(date) {
  if (!date) return;
  el.dayDialog.showModal();
  el.dialogTitle.textContent = formatFullDate(date, { includeWeekday: true });
  el.dialogSummary.textContent = "正在读取当天明细…";
  el.dialogBody.textContent = "";
  if (state.dayCache.has(date)) {
    renderDayDetail(state.dayCache.get(date));
    return;
  }
  try {
    const response = await fetch(`/api/day?date=${encodeURIComponent(date)}`, {
      cache: "no-store",
      headers: { accept: "application/json" },
    });
    if (!response.ok) {
      const payload = await response.json().catch(() => ({}));
      throw new Error(payload.error || `HTTP ${response.status}`);
    }
    const data = await response.json();
    state.dayCache.set(date, data);
    renderDayDetail(data);
  } catch (cause) {
    el.dialogSummary.textContent = "";
    el.dialogBody.innerHTML = `<div class="dialog-empty">读取失败：${escapeHtml(cause.message || "未知错误")}</div>`;
  }
}

function renderDayDetail(data) {
  const timezone = state.data?.timezone || "Asia/Shanghai";
  el.dialogSummary.textContent = `${data.count} 次 · 麦克风 ${data.microphone} · 系统声音 ${data.system}`;
  el.dialogBody.textContent = "";
  if (!data.events?.length) {
    const empty = document.createElement("div");
    empty.className = "dialog-empty";
    empty.textContent = data.count > 0
      ? "这一天的逐条明细已归档，只保留每日计数。"
      : "这一天没有记录";
    el.dialogBody.append(empty);
    return;
  }
  const list = document.createElement("div");
  list.className = "dialog-day-events";
  data.events.forEach((event) => {
    const row = document.createElement("div");
    row.className = "dialog-event";
    const time = document.createElement("time");
    time.textContent = formatEventTime(event.at, timezone);
    const source = document.createElement("span");
    source.textContent = event.source === "system" ? "系统声音" : "麦克风";
    const index = document.createElement("span");
    index.textContent = "";
    row.append(time, source, index);
    list.append(row);
  });
  el.dialogBody.append(list);
}

function exportCsv() {
  if (!state.data) return;
  const daily = (state.data.daily || []).slice(-state.days);
  const rows = [
    ["日期", "次数", "麦克风", "系统声音", "首次出现", "最后出现"],
    ...daily.map((day) => [
      day.date,
      day.count,
      day.mic,
      day.system,
      day.firstAt || "",
      day.lastAt || "",
    ]),
  ];
  const csv = rows.map((row) => row.map(csvCell).join(",")).join("\r\n");
  const blob = new Blob([`\uFEFF${csv}`], { type: "text/csv;charset=utf-8" });
  const anchor = document.createElement("a");
  anchor.href = URL.createObjectURL(blob);
  anchor.download = `waikan-cloud-${state.data.today}-${state.days}d.csv`;
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(anchor.href);
  showToast("CSV 已导出");
}

function csvCell(value) {
  const text = String(value ?? "");
  return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

function setConnection(kind, text) {
  el.connection.classList.remove("ok", "error");
  if (kind) el.connection.classList.add(kind);
  el.connectionText.textContent = text;
}

let toastTimer;
function showToast(message, isError = false) {
  el.toast.textContent = message;
  el.toast.classList.toggle("is-error", isError);
  el.toast.classList.add("is-visible");
  clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => el.toast.classList.remove("is-visible"), 2800);
}

function formatFullDate(dateKey, { includeWeekday = false } = {}) {
  if (!dateKey) return "—";
  const date = new Date(`${dateKey}T12:00:00Z`);
  if (!Number.isFinite(date.getTime())) return dateKey;
  if (includeWeekday) return dateFormatter.format(date);
  return shortDateFormatter.format(date);
}

function formatShortDate(dateKey) {
  return formatFullDate(dateKey).replace("月", "/").replace("日", "");
}

function formatEventTime(iso, timeZone) {
  const date = new Date(iso);
  if (!Number.isFinite(date.getTime())) return "—";
  return new Intl.DateTimeFormat("zh-CN", {
    timeZone,
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).format(date);
}

function formatEventDateTime(iso, timeZone) {
  const date = new Date(iso);
  if (!Number.isFinite(date.getTime())) return "—";
  return new Intl.DateTimeFormat("zh-CN", {
    timeZone,
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  }).format(date);
}

function relativeTime(iso) {
  const date = new Date(iso);
  if (!Number.isFinite(date.getTime())) return "—";
  const seconds = Math.max(0, Math.round((Date.now() - date.getTime()) / 1000));
  if (seconds < 45) return "刚刚";
  if (seconds < 3600) return `${Math.round(seconds / 60)} 分钟前`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)} 小时前`;
  return `${Math.round(seconds / 86400)} 天前`;
}

function animateNumber(target, value) {
  if (reduceMotion) {
    target.textContent = String(value);
    return;
  }
  const start = Number(target.textContent) || 0;
  if (start === value) return;
  const startedAt = performance.now();
  const duration = 460;
  const tick = (now) => {
    const progress = Math.min(1, (now - startedAt) / duration);
    const eased = 1 - Math.pow(1 - progress, 3);
    target.textContent = String(Math.round(start + (value - start) * eased));
    if (progress < 1) requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}
