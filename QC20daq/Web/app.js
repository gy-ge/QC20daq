"use strict";

const byId = (id) => document.getElementById(id);
const sources = [...document.querySelectorAll('input[name="source"]')];
const canvas = byId("chart");
const context = canvas.getContext("2d");
const integers = new Intl.NumberFormat("zh-CN");
const phaseNames = {
  idle: ["未连接", "quiet"], discovering: ["搜索中", "wait"], reading: ["读取中", "wait"],
  connecting: ["连接中", "wait"], previewing: ["已连接", "live"], arming: ["准备记录", "wait"],
  preview_stalled: ["读数停更", "wait"],
  recording: ["记录中", "live"], stopping: ["保存中", "wait"], disconnecting: ["断开中", "wait"],
  shutting_down: ["关闭中", "wait"],
  complete: ["已保存", "live"], interrupted: ["已中断", "error"],
};
let state = null;
let online = false;
let renderFailed = false;
let pending = false;
let shuttingDown = false;
let paused = false;
let pausedPlot = null;
let lastPlot = null;
let lastPlotKey = "";
let lastOptionsKey = "";
let lastHistoryKey = "";
let actionError = "";

function text(id, value) {
  const node = byId(id);
  if (node.textContent !== String(value)) node.textContent = value;
}

function number(value, digits = 0) {
  if (!Number.isFinite(value)) return "—";
  return digits === 0 ? integers.format(value) : value.toFixed(digits);
}

function sourceValue() {
  return sources.find((radio) => radio.checked).value;
}

function setOnline(value) {
  online = value;
  const indicator = byId("online");
  const label = shuttingDown ? (value ? "正在关闭服务" : "本机服务已关闭") :
    (value ? "本机服务在线" : "本机服务未响应");
  if (indicator.dataset.online !== String(value)) indicator.dataset.online = String(value);
  if (indicator.lastChild.nodeValue !== label) indicator.lastChild.nodeValue = label;
}

function refreshDeviceOptions(data) {
  const active = data.connection;
  const target = active?.target;
  const key = JSON.stringify([data.trace_device, data.devices, target]);
  if (key === lastOptionsKey) return;
  lastOptionsKey = key;
  const select = byId("target");
  const selected = select.value;
  const trace = data.trace_device;
  const first = new Option(trace ? `Trace 历史设备 · ${trace.model || "QC20"}` : "Trace 尚无保存设备", "trace");
  first.disabled = !trace;
  select.replaceChildren(first);
  for (const device of data.devices) {
    if (!device.serial) continue;
    select.append(new Option(`${device.model} · ${device.serial}`, device.serial));
  }
  if (target && ![...select.options].some((option) => option.value === target)) {
    select.append(new Option(active.device?.serial || target, target));
  }
  const usable = [...select.options].filter((option) => !option.disabled);
  if (target) select.value = target;
  else if (usable.some((option) => option.value === selected)) select.value = selected;
  else if (usable.length) select.value = usable[0].value;
}

function restoreConnectionSettings(connection) {
  const source = sources.find((radio) => radio.value === connection.source);
  if (source) source.checked = true;
  const rate = byId("rate");
  const rateValue = String(connection.rate);
  if (![...rate.options].some((option) => option.value === rateValue)) {
    rate.append(new Option(`${rateValue} Hz`, rateValue));
  }
  rate.value = rateValue;
}

function restoreRecordSettings(capture) {
  const timed = Number.isFinite(capture.target_seconds);
  byId("mode").value = timed ? "timed" : "manual";
  if (timed) byId("duration").value = capture.target_seconds;
}

function renderControls() {
  const available = !pending && online && !renderFailed && !shuttingDown;
  const locked = !available || Boolean(state?.busy);
  const recording = Boolean(state?.recording_active);
  const connection = state?.connection;
  const simulated = sourceValue() === "simulated";
  const manual = byId("mode").value === "manual";
  const target = byId("target").selectedOptions[0];
  const haveTarget = !!target && !target.disabled;
  byId("device-fields").hidden = simulated;
  byId("duration-field").hidden = manual;
  byId("mode").disabled = !available || recording || state?.phase === "disconnecting";
  byId("duration").disabled = manual || byId("mode").disabled;
  for (const id of ["rate", "target", "discover"]) byId(id).disabled = locked;
  for (const radio of sources) radio.disabled = locked;
  byId("connect").disabled = locked || (!simulated && !haveTarget);
  byId("shutdown").disabled = !available;
  byId("disconnect").disabled = !available || !connection || state.phase === "disconnecting";
  text("disconnect", recording && connection ? "保存并断开" : "断开");
  byId("start").disabled = !available || state?.phase !== "previewing" || recording;
  byId("stop").disabled = !available || !recording || ["stopping", "disconnecting"].includes(state?.phase);
  const device = connection?.device;
  if (!device) {
    text("device-summary", connection ? "正在连接" : "等待连接");
    return;
  }
  const parts = [device.model, device.serial, `原生 ${number(device.native_sample_rate_hz)} Hz`];
  if (device.firmware) parts.push(`固件 ${device.firmware}`);
  if (device.status?.includes("LowBattery")) parts.push("电量低");
  if (device.status?.includes("LowSignal")) parts.push("信号弱");
  text("device-summary", parts.filter(Boolean).join(" · "));
}

function renderHistory(data) {
  const key = JSON.stringify([data.history, data.recording_active]);
  if (key === lastHistoryKey) return;
  lastHistoryKey = key;
  const body = byId("history");
  body.replaceChildren();
  if (!data.history.length) {
    const cell = body.insertRow().insertCell();
    cell.colSpan = 7;
    cell.className = "empty-history";
    cell.textContent = "暂无保存记录";
    return;
  }
  for (const item of data.history) {
    const row = body.insertRow();
    const cells = Array.from({length: 7}, () => row.insertCell());
    const date = new Date(item.closed_utc);
    cells[0].textContent = Number.isFinite(date.valueOf()) ? date.toLocaleString("zh-CN", {hour12: false}) : "—";
    cells[1].textContent = item.source === "simulated" ? "模拟" : "实机";
    const badge = document.createElement("span");
    badge.className = "table-pill";
    badge.dataset.tone = item.state === "complete" ? "live" : "error";
    badge.textContent = item.state === "complete" ? "已保存" : "中断";
    cells[2].append(badge);
    cells[3].textContent = item.rate_hz === null ? "—" : `${number(item.rate_hz)} Hz`;
    cells[4].textContent = number(item.samples);
    cells[5].textContent = item.seconds === null ? "—" : `${number(item.seconds, 2)} s`;
    if (data.recording_active) cells[6].textContent = "保存后可下载";
    else {
      for (const [file, label] of (item.has_csv ? [["csv", "CSV"], ["report", "报告"]] : [["report", "报告"]])) {
        const link = document.createElement("a");
        link.href = `/api/download?${new URLSearchParams({id: item.id, file})}`;
        link.textContent = label;
        cells[6].append(link);
      }
    }
  }
}

function render(data) {
  const capture = data.capture;
  const connection = data.connection;
  const interrupted = data.interrupted_connection;
  const observed = connection ?? interrupted;
  const plot = data.preview;
  const view = paused && pausedPlot ? pausedPlot : plot;
  const [phaseLabel, tone] = phaseNames[data.phase] || phaseNames.idle;
  text("phase", phaseLabel);
  byId("phase").dataset.tone = tone;
  const source = (observed?.source ?? capture?.source) === "simulated" ? "模拟" : "实机";
  text("session-label", data.recording_active ? `${source}记录 · ${capture.id}` :
    interrupted ? `${source} · 连接已中断` :
    connection ? `${source} · ${plot.stale ? "等待新读数" : connection.ready ? "已连接" : "等待连续读数"}` :
    capture?.report ? `上次记录 · ${capture.id}` : "等待连接设备");
  text("reading-label", plot.stale ? "末次读数" : "当前读数");
  text("reading", number(plot.latest_um, 3));
  text("count", number(capture?.samples_written ?? 0));
  text("elapsed", number(capture?.elapsed_s, 2));
  text("sample-rate", number(observed?.rate_hz ?? capture?.report?.nominal_sample_rate_hz));
  text("pkpk", number(Number.isFinite(view.min_um) && Number.isFinite(view.max_um) ? view.max_um - view.min_um : null, 3));

  const device = observed?.device ?? capture?.report?.device;
  const low = device?.measurement_min_mm * 1000;
  const high = device?.measurement_max_mm * 1000;
  const hasRange = Number.isFinite(device?.measurement_min_mm) && Number.isFinite(device?.measurement_max_mm);
  const limit = hasRange &&
    ((Number.isFinite(view.min_um) && view.min_um <= low) || (Number.isFinite(view.max_um) && view.max_um >= high));
  text("range", limit ? "读数触及量程边界" : (hasRange ? `量程 ${number(low)} ～ ${number(high)} µm` : "量程待读取"));
  byId("range").classList.toggle("limit", limit);
  text("plot-state", interrupted ? "断流 · 最后画面" : paused ? "预览暂停" : plot.stale ? "等待新读数" :
      plot.count ? "实时预览" : "等待数据");
  const message = actionError || data.message;
  text("notice", message);
  byId("notice").dataset.tone = actionError ? "error" : tone;

  byId("pause").disabled = !plot.count || !connection;
  text("pause", paused ? "恢复预览" : "暂停预览");
  const key = `${observed?.id}/${view.count}/${paused}`;
  if (key !== lastPlotKey) {
    lastPlotKey = key;
    lastPlot = view;
    drawChart();
  }
  byId("chart-empty").hidden = Number.isFinite(view.min_um);
  text("empty-text", view.count ? "当前窗口暂无有效位移读数" :
    connection ? "等待球杆读数" : "连接球杆后显示实时曲线");
  byId("session-path-wrap").hidden = !capture?.folder;
  if (capture?.folder) text("session-path", capture.folder);
  refreshDeviceOptions(data);
  renderHistory(data);
  renderControls();
}

function tickStep(value) {
  const scale = 10 ** Math.floor(Math.log10(value));
  const normalized = value / scale;
  return scale * (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10);
}

function drawChart() {
  const bounds = canvas.getBoundingClientRect();
  if (!bounds.width || !bounds.height) return;
  const scale = Math.min(devicePixelRatio || 1, 2);
  const width = bounds.width;
  const height = bounds.height;
  canvas.width = Math.round(width * scale);
  canvas.height = Math.round(height * scale);
  context.setTransform(scale, 0, 0, scale, 0, 0);
  const points = lastPlot?.points || [];
  const seconds = lastPlot?.window_s || 12;
  const last = lastPlot?.elapsed_s || 0;
  const xmin = Math.max(0, last - seconds);
  const xmax = last <= seconds ? Math.min(seconds, Math.max(2, Math.ceil(last / 2) * 2)) : last;
  const ymaxInput = Number.isFinite(lastPlot?.max_um) ? lastPlot.max_um : 20;
  const yminInput = Number.isFinite(lastPlot?.min_um) ? lastPlot.min_um : -20;
  const span = Math.max(ymaxInput - yminInput, 2);
  const stepY = tickStep(span / 4);
  const ymax = Math.ceil((ymaxInput + span * .12) / stepY) * stepY;
  const ymin = Math.floor((yminInput - span * .12) / stepY) * stepY;
  const left = width < 440 ? 46 : 55, right = width - 12, top = 19, bottom = height - 31;
  const px = (x) => left + (x - xmin) / (xmax - xmin) * (right - left);
  const py = (y) => bottom - (y - ymin) / (ymax - ymin) * (bottom - top);
  context.font = '11px "Cascadia Mono", Consolas, monospace';
  context.fillStyle = "#516978";
  context.lineWidth = 1;
  context.textBaseline = "middle";
  context.textAlign = "right";
  for (let y = ymin; y <= ymax + stepY * .001; y += stepY) {
    const p = py(y);
    context.strokeStyle = Math.abs(y) < stepY / 1000 ? "#b6cbd6" : "#dee8ef";
    context.beginPath(); context.moveTo(left, p); context.lineTo(right, p); context.stroke();
    const digits = stepY < 1 ? 1 : 0;
    context.fillText(Math.abs(y) < stepY / 1000 ? "0" : y.toFixed(digits), left - 7, p);
  }
  context.textAlign = "center";
  const stepX = tickStep((xmax - xmin) / (width < 440 ? 4 : 6));
  context.strokeStyle = "#dee8ef";
  for (let x = Math.ceil(xmin / stepX) * stepX; x <= xmax; x += stepX) {
    const p = px(x);
    context.beginPath(); context.moveTo(p, top); context.lineTo(p, bottom); context.stroke();
    context.fillText(x.toFixed(stepX < 1 ? 1 : 0), p, bottom + 13);
  }
  context.textAlign = "left";
  context.fillText("µm", 8, 7);
  context.textAlign = "right";
  context.fillText("s", right, height - 5);

  context.save();
  context.beginPath(); context.rect(left, top, right - left, bottom - top); context.clip();
  context.strokeStyle = "#087f76";
  context.fillStyle = context.strokeStyle;
  context.lineWidth = 1.7;
  context.beginPath();
  let previousTime = null;
  for (const [time, reading] of points) {
    if (!Number.isFinite(reading)) { previousTime = null; continue; }
    const x = px(time), y = py(reading);
    if (previousTime === null || time - previousTime > .3) {
      context.fillRect(x - 1, y - 1, 2, 2);
      context.moveTo(x, y);
    } else context.lineTo(x, y);
    previousTime = time;
  }
  context.stroke();
  context.restore();
}

async function requestJson(path, options, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(path, {...options, signal: controller.signal});
    return {response, data: await response.json()};
  } finally {
    clearTimeout(timer);
  }
}

async function post(path, body) {
  if (!state || pending) return;
  pending = true;
  actionError = "";
  renderControls();
  try {
    const {response, data: answer} = await requestJson(path, {
      method: "POST", headers: {"Content-Type": "application/json", "X-QC20-Token": state.token},
      body: JSON.stringify(body)
    }, 4500);
    if (!response.ok) throw new Error(answer.error || "操作未完成");
    if (path === "/api/shutdown") {
      shuttingDown = true;
      setOnline(true);
    }
  } catch (error) {
    actionError = error.message;
  } finally {
    pending = false;
    await refresh();
  }
}

let refreshing = false;
async function refresh() {
  if (refreshing) return;
  refreshing = true;
  let statusReceived = false;
  try {
    const {response, data} = await requestJson("/api/status", {cache: "no-store"}, 3500);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    statusReceived = true;
    renderFailed = false;
    if (state?.token !== data.token) shuttingDown = false;
    if (data.shutting_down) shuttingDown = true;
    setOnline(true);
    if (state?.connection?.id !== data.connection?.id) {
      paused = false;
      pausedPlot = null;
      if (data.connection) restoreConnectionSettings(data.connection);
    }
    if (data.recording_active && state?.capture?.id !== data.capture?.id) {
      restoreRecordSettings(data.capture);
    }
    state = data;
    render(data);
  } catch (error) {
    renderFailed = statusReceived;
    if (statusReceived) {
      text("notice", `本机服务在线，网页状态显示失败（${error.message}）。`);
      text("plot-state", "页面显示失败");
    } else {
      setOnline(false);
      if (shuttingDown) {
        text("phase", "已关闭");
        byId("phase").dataset.tone = "quiet";
        text("session-label", "双击 QC20daq.exe 重新启动");
        text("notice", "服务已退出");
        byId("notice").dataset.tone = "quiet";
        text("plot-state", "预览已结束");
        if (state?.preview.count) text("reading-label", "末次读数");
        byId("pause").disabled = true;
      } else {
        text("notice", `本机状态请求失败（${error.message}），正在重试。`);
        text("plot-state", "等待本机服务");
      }
    }
    renderControls();
  } finally {
    refreshing = false;
  }
}

const refreshDelay = byId("refresh-delay");
let pollTimer;
try {
  const saved = localStorage.getItem("qc20PreviewInterval");
  if ([...refreshDelay.options].some((option) => option.value === saved)) refreshDelay.value = saved;
} catch {}

async function poll() {
  clearTimeout(pollTimer);
  await refresh();
  clearTimeout(pollTimer);
  const delay = !online || renderFailed ? 1000 : document.hidden ? 1500 : Number(refreshDelay.value);
  pollTimer = setTimeout(poll, delay);
}

refreshDelay.addEventListener("change", () => {
  try { localStorage.setItem("qc20PreviewInterval", refreshDelay.value); } catch {}
  poll();
});
document.addEventListener("visibilitychange", poll);

byId("capture-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const seconds = byId("mode").value === "manual" ? null : Number(byId("duration").value);
  if (seconds !== null && (!Number.isFinite(seconds) || seconds <= 0)) {
    actionError = "请输入大于 0 的时长。";
    if (state) render(state);
    return;
  }
  post("/api/record", {connection_id: state?.connection?.id, seconds});
});
byId("connect").addEventListener("click", () => {
  const rate = byId("rate").value;
  post("/api/connect", {source: sourceValue(), target: byId("target").value, rate: rate === "native" ? rate : Number(rate)});
});
byId("disconnect").addEventListener("click", () => post("/api/disconnect", {id: state?.connection?.id}));
byId("shutdown").addEventListener("click", () => {
  if (state?.recording_active && !window.confirm("正在记录。保存当前数据并关闭服务？")) return;
  post("/api/shutdown", {});
});
byId("discover").addEventListener("click", () => post("/api/device", {operation: "discover"}));
byId("stop").addEventListener("click", () => post("/api/stop", {id: state?.capture?.id}));
byId("pause").addEventListener("click", () => {
  paused = !paused;
  pausedPlot = paused ? state.preview : null;
  render(state);
});
for (const element of [...sources, byId("mode"), byId("target")]) element.addEventListener("change", renderControls);
new ResizeObserver(drawChart).observe(byId("chart-wrap"));
poll();
