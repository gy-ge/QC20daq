"use strict";

const byId = (id) => document.getElementById(id);
const sources = [...document.querySelectorAll('input[name="source"]')];
const canvas = byId("chart");
const context = canvas.getContext("2d");
const copy = {
  pageTitle: ["QC20 球杆采集台", "QC20 Ballbar Acquisition"],
  brandName: ["QC20 采集台", "QC20daq"],
  brandSubtitle: ["旋转轴测量", "Rotary axis measurement"],
  language: ["界面语言", "Interface language"],
  serviceConnecting: ["正在连接本机服务", "Connecting to local service"],
  serviceClosing: ["正在关闭服务", "Shutting down service"],
  serviceClosed: ["本机服务已关闭", "Local service stopped"],
  serviceOnline: ["本机服务在线", "Local service online"],
  serviceOffline: ["本机服务未响应", "Local service unavailable"],
  shutDown: ["关闭服务", "Shut down service"],
  confirmShutdown: ["正在记录。保存当前数据并关闭服务？", "Recording is in progress. Save the data and shut down the service?"],
  pageHeading: ["球杆实时采集", "Live ballbar acquisition"],
  waitForDevice: ["等待连接设备", "Waiting for a device"],
  idle: ["未连接", "Disconnected"],
  discovering: ["搜索中", "Searching"],
  reading: ["读取中", "Reading"],
  connecting: ["连接中", "Connecting"],
  connected: ["已连接", "Connected"],
  arming: ["准备记录", "Preparing to record"],
  stalled: ["读数停更", "Readings stalled"],
  recording: ["记录中", "Recording"],
  saving: ["保存中", "Saving"],
  disconnecting: ["断开中", "Disconnecting"],
  closing: ["关闭中", "Shutting down"],
  saved: ["已保存", "Saved"],
  interrupted: ["已中断", "Interrupted"],
  closed: ["已关闭", "Stopped"],
  liveMetrics: ["实时数值", "Live readings"],
  currentReading: ["当前读数", "Current reading"],
  lastReading: ["末次读数", "Last reading"],
  recorded: ["已记录", "Recorded"],
  samples: ["点", "samples"],
  recordTime: ["记录时间", "Recording time"],
  nominalRate: ["标称采样率", "Nominal sample rate"],
  liveChart: ["实时曲线", "Live chart"],
  chartHeading: ["球距变化", "Ballbar displacement"],
  chartWindow: ["最近 12 秒 · 相对预览起点", "Last 12 s · Time since preview start"],
  refreshInterval: ["刷新间隔", "Refresh interval"],
  pause: ["暂停预览", "Pause preview"],
  resume: ["恢复预览", "Resume preview"],
  chartDescription: ["球杆读数随时间的曲线", "Ballbar displacement over time"],
  connectForPreview: ["连接球杆后显示实时曲线", "Connect a ballbar to see the live chart"],
  waitForReading: ["等待球杆读数", "Waiting for ballbar readings"],
  noValidReadings: ["当前窗口暂无有效位移读数", "No valid readings in this window"],
  waitForData: ["等待数据", "Waiting for data"],
  peakToPeak: ["窗内峰峰值", "Window peak-to-peak"],
  waitForRange: ["量程待读取", "Measurement range pending"],
  range: ["量程 {low} ～ {high} µm", "Range {low} to {high} µm"],
  rangeLimit: ["读数触及量程边界", "Reading at range limit"],
  lostStream: ["断流 · 最后画面", "Stream interrupted · Last view"],
  paused: ["预览暂停", "Preview paused"],
  waitForNewReadings: ["等待新读数", "Waiting for new readings"],
  waitForContinuousReadings: ["等待连续读数", "Waiting for a continuous stream"],
  livePreview: ["实时预览", "Live preview"],
  previewEnded: ["预览已结束", "Preview ended"],
  captureControls: ["采集控制", "Acquisition controls"],
  deviceConnection: ["设备连接", "Device connection"],
  waitForConnection: ["等待连接", "Waiting to connect"],
  connectingDevice: ["正在连接", "Connecting"],
  dataSource: ["数据来源", "Data source"],
  device: ["实机", "Device"],
  simulation: ["模拟", "Simulation"],
  ballbarDevice: ["球杆设备", "Ballbar device"],
  traceSavedDevice: ["Trace 历史设备", "Trace saved device"],
  traceEmpty: ["Trace 尚无保存设备", "No device saved in Trace"],
  searchDevices: ["搜索设备", "Search devices"],
  sampleRate: ["采样率", "Sample rate"],
  nativeRate: ["设备原生", "Device native"],
  nativeRateValue: ["原生 {rate} Hz", "Native {rate} Hz"],
  firmware: ["固件 {version}", "Firmware {version}"],
  lowBattery: ["电量低", "Low battery"],
  lowSignal: ["信号弱", "Weak signal"],
  connect: ["连接", "Connect"],
  disconnect: ["断开", "Disconnect"],
  saveAndDisconnect: ["保存并断开", "Save and disconnect"],
  recordingHeading: ["记录", "Recording"],
  stopMode: ["停止方式", "Stop mode"],
  timed: ["按时长", "Timed"],
  manual: ["手动", "Manual"],
  targetDuration: ["目标时长", "Target duration"],
  startRecording: ["开始记录", "Start recording"],
  stopAndSave: ["停止并保存", "Stop and save"],
  waitForAction: ["等待操作", "Ready for input"],
  savedRecords: ["保存记录", "Saved records"],
  recentSessions: ["最近的本机 Web 采集会话", "Recent local acquisition sessions"],
  closedAt: ["结束时间", "End time"],
  source: ["来源", "Source"],
  result: ["结果", "Result"],
  sampleCount: ["点数", "Samples"],
  files: ["文件", "Files"],
  noRecords: ["暂无保存记录", "No saved records"],
  historyInterrupted: ["中断", "Interrupted"],
  downloadAfterSave: ["保存后可下载", "Available after saving"],
  report: ["报告", "Report"],
  sessionFolder: ["会话目录", "Session folder"],
  sessionRecording: ["{source}记录 · {id}", "{source} recording · {id}"],
  connectionInterrupted: ["连接已中断", "Connection interrupted"],
  previousRecording: ["上次记录 · {id}", "Last recording · {id}"],
  displayError: ["本机服务在线，网页状态显示失败（{detail}）。", "The local service is online. The page could not display its status ({detail})."],
  displayFailed: ["页面显示失败", "Page display failed"],
  relaunch: ["双击 QC20daq.exe 重新启动", "Double-click QC20daq.exe to restart"],
  serviceExited: ["服务已退出", "Service exited"],
  requestError: ["本机状态请求失败（{detail}），正在重试。", "Could not read the local service status ({detail}). Retrying."],
  waitForService: ["等待本机服务", "Waiting for local service"],
  invalidDuration: ["请输入大于 0 的时长。", "Enter a duration greater than zero."],
  actionFailed: ["操作未完成", "The operation did not complete"],
};

// Translate the existing service messages for display. Keep external diagnostics verbatim.
const englishServiceMessages = new Map([
  ["选择设备并连接", "Select a device and connect"],
  ["请选择实机或模拟采集。", "Select Device or Simulation."],
  ["采样率须为设备原生或不高于 1000 Hz 的正数。", "Select the device's native rate or a positive rate up to 1000 Hz."],
  ["正在连接球杆", "Connecting to the ballbar"],
  ["未知设备操作。", "Unknown device operation."],
  ["该连接已结束，或不是当前设备。", "The connection has ended or belongs to another device."],
  ["正在断开", "Disconnecting"],
  ["正在保存并断开", "Saving and disconnecting"],
  ["定时模式必须设置正数时长。", "Enter a positive duration for timed recording."],
  ["请等待球杆实时读数后开始记录。", "Wait for live ballbar readings before recording."],
  ["当前操作尚未结束。", "An operation is still in progress."],
  ["正在准备保存", "Preparing to save"],
  ["该记录已结束，或不是当前会话。", "The recording has ended or belongs to another session."],
  ["正在保存记录", "Saving the recording"],
  ["Trace 历史设备不唯一，请搜索后按序列号选择。", "Trace did not identify a unique saved device. Search for the ballbar and select its serial number."],
  ["请选择 Trace 已存设备，或使用搜索结果中的序列号。", "Select a saved Trace device or a serial number from the search results."],
  ["本机服务正在关闭。", "The local service is shutting down."],
  ["采集会话无效。", "Invalid acquisition session."],
  ["采集目录须为本机工作区中的实际目录。", "The recording folder must be a physical directory within the local workspace."],
  ["设备已断开", "Device disconnected"],
  ["正在记录并写入 CSV", "Recording and writing to CSV"],
  ["暂未收到新样本，等待读数恢复", "No new samples. Waiting for readings to resume"],
  ["设备已连接 · 实时预览中", "Device connected · Live preview"],
  ["正在确认连续读数", "Checking for a continuous stream"],
  ["采集进程已退出", "The acquisition process exited"],
  ["30 秒内未能连接球杆", "Could not connect to the ballbar within 30 seconds"],
  ["连续未收到新样本，球杆连接已中断", "No new samples arrived. The ballbar connection was interrupted"],
  ["球杆通信帧校验失败，连接已中断", "Ballbar frame verification failed. The connection was interrupted"],
  ["球杆提前停止发送样本，连接已中断", "The ballbar stopped sending samples. The connection was interrupted"],
  ["定时记录已保存", "Timed recording saved"],
  ["记录已保存", "Recording saved"],
  ["开录前已停止", "Stopped before recording started"],
  ["采集进程已退出，缺少结束报告", "The acquisition process exited without a capture report"],
  ["正在搜索附近球杆", "Searching for nearby ballbars"],
  ["正在读取球杆信息", "Reading ballbar information"],
  ["设备信息已读取，请连接设备", "Device information loaded. Connect to the device"],
  ["设备操作未返回结果", "The device operation returned no result"],
  ["正在保存记录，保存结束后关闭服务", "Saving the recording before shutting down"],
  ["正在释放球杆并关闭服务", "Disconnecting the ballbar and shutting down"],
  ["设备操作结束后关闭服务", "The service will shut down after the device operation finishes"],
  ["当前下载完成后关闭服务", "The service will shut down after the download finishes"],
  ["正在关闭本机服务", "Shutting down the local service"],
  ["请在记录保存后下载文件。", "Save the recording before downloading files."],
  ["找不到该采集文件。", "The recording file could not be found."],
  ["只允许本机访问。", "Only local access is allowed."],
  ["请求参数无法读取。", "The request parameters could not be read."],
  ["请求长度或格式异常。", "Invalid request length or format."],
  ["本机操作校验已失效，请刷新页面。", "The local control request could not be verified. Reload the page."],
  ["需要 JSON 请求。", "The request must use JSON."],
  ["请求长度异常。", "Invalid request length."],
  ["请求参数格式不正确。", "Invalid request parameters."],
  ["无法启动采集进程。", "Could not start the acquisition process."],
  copy.invalidDuration,
  copy.actionFailed,
]);

const languageSelect = byId("language");
let language = /^zh(?:-|$)/i.test(navigator.language) ? "zh-CN" : "en";
try {
  const saved = localStorage.getItem("qc20Language");
  if (saved === "zh-CN" || saved === "en") language = saved;
} catch {}
let integers;

const phaseNames = {
  idle: ["idle", "quiet"], discovering: ["discovering", "wait"], reading: ["reading", "wait"],
  connecting: ["connecting", "wait"], previewing: ["connected", "live"], arming: ["arming", "wait"],
  preview_stalled: ["stalled", "wait"],
  recording: ["recording", "live"], stopping: ["saving", "wait"], disconnecting: ["disconnecting", "wait"],
  shutting_down: ["closing", "wait"],
  complete: ["saved", "live"], interrupted: ["interrupted", "error"],
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
let statusError = null;

function text(id, value) {
  const node = byId(id);
  if (node.textContent !== String(value)) node.textContent = value;
}

function t(key, values) {
  const value = copy[key][language === "en" ? 1 : 0];
  return values ? value.replace(/\{(\w+)\}/g, (_, name) => values[name]) : value;
}

function displayMessage(message) {
  if (!message || language === "zh-CN") return message;
  const english = englishServiceMessages.get(message);
  if (english) return english;
  const search = /^搜索完成，找到 (\d+) 个设备$/.exec(message);
  if (search) {
    const count = Number(search[1]);
    return `Search complete. Found ${number(count)} ${count === 1 ? "device" : "devices"}.`;
  }
  const disconnected = "设备已断开 · ";
  if (message.startsWith(disconnected)) return `Device disconnected · ${displayMessage(message.slice(disconnected.length))}`;
  const interrupted = "记录中断 · ";
  if (message.startsWith(interrupted)) return `Recording interrupted · ${message.slice(interrupted.length)}`;
  return message;
}

function applyLanguage() {
  languageSelect.value = language;
  document.documentElement.lang = language;
  integers = new Intl.NumberFormat(language);
  for (const node of document.querySelectorAll("[data-i18n]")) node.textContent = t(node.dataset.i18n);
  for (const node of document.querySelectorAll("[data-i18n-label]")) node.setAttribute("aria-label", t(node.dataset.i18nLabel));
  if (state || statusError !== null) setOnline(online);
  if (state && !renderFailed) render(state);
  if (statusError !== null) renderStatusError(statusError);
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
  const label = shuttingDown ? (value ? "serviceClosing" : "serviceClosed") :
    (value ? "serviceOnline" : "serviceOffline");
  if (indicator.dataset.online !== String(value)) indicator.dataset.online = String(value);
  text("online-label", t(label));
}

function refreshDeviceOptions(data) {
  const active = data.connection;
  const target = active?.target;
  const key = JSON.stringify([language, data.trace_device, data.devices, target]);
  if (key === lastOptionsKey) return;
  lastOptionsKey = key;
  const select = byId("target");
  const selected = select.value;
  const trace = data.trace_device;
  const first = new Option(trace ? `${t("traceSavedDevice")} · ${trace.model || "QC20"}` : t("traceEmpty"), "trace");
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
  text("disconnect", t(recording && connection ? "saveAndDisconnect" : "disconnect"));
  byId("start").disabled = !available || state?.phase !== "previewing" || recording;
  byId("stop").disabled = !available || !recording || ["stopping", "disconnecting"].includes(state?.phase);
  const device = connection?.device;
  if (!device) {
    text("device-summary", t(connection ? "connectingDevice" : "waitForConnection"));
    return;
  }
  const parts = [device.model, device.serial, t("nativeRateValue", {rate: number(device.native_sample_rate_hz)})];
  if (device.firmware) parts.push(t("firmware", {version: device.firmware}));
  if (device.status?.includes("LowBattery")) parts.push(t("lowBattery"));
  if (device.status?.includes("LowSignal")) parts.push(t("lowSignal"));
  text("device-summary", parts.filter(Boolean).join(" · "));
}

function renderHistory(data) {
  const key = JSON.stringify([language, data.history, data.recording_active]);
  if (key === lastHistoryKey) return;
  lastHistoryKey = key;
  const body = byId("history");
  body.replaceChildren();
  if (!data.history.length) {
    const cell = body.insertRow().insertCell();
    cell.colSpan = 7;
    cell.className = "empty-history";
    cell.textContent = t("noRecords");
    return;
  }
  for (const item of data.history) {
    const row = body.insertRow();
    const cells = Array.from({length: 7}, () => row.insertCell());
    const date = new Date(item.closed_utc);
    cells[0].textContent = Number.isFinite(date.valueOf()) ? date.toLocaleString(language, {hour12: false}) : "—";
    cells[1].textContent = t(item.source === "simulated" ? "simulation" : "device");
    const badge = document.createElement("span");
    badge.className = "table-pill";
    badge.dataset.tone = item.state === "complete" ? "live" : "error";
    badge.textContent = t(item.state === "complete" ? "saved" : "historyInterrupted");
    cells[2].append(badge);
    cells[3].textContent = item.rate_hz === null ? "—" : `${number(item.rate_hz)} Hz`;
    cells[4].textContent = number(item.samples);
    cells[5].textContent = item.seconds === null ? "—" : `${number(item.seconds, 2)} s`;
    if (data.recording_active) cells[6].textContent = t("downloadAfterSave");
    else {
      for (const [file, label] of (item.has_csv ? [["csv", "CSV"], ["report", t("report")]] : [["report", t("report")]])) {
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
  text("phase", t(phaseLabel));
  byId("phase").dataset.tone = tone;
  const source = t((observed?.source ?? capture?.source) === "simulated" ? "simulation" : "device");
  text("session-label", data.recording_active ? t("sessionRecording", {source, id: capture.id}) :
    interrupted ? `${source} · ${t("connectionInterrupted")}` :
    connection ? `${source} · ${t(plot.stale ? "waitForNewReadings" : connection.ready ? "connected" : "waitForContinuousReadings")}` :
    capture?.report ? t("previousRecording", {id: capture.id}) : t("waitForDevice"));
  text("reading-label", t(plot.stale ? "lastReading" : "currentReading"));
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
  text("range", limit ? t("rangeLimit") : hasRange ? t("range", {low: number(low), high: number(high)}) : t("waitForRange"));
  byId("range").classList.toggle("limit", limit);
  text("plot-state", t(interrupted ? "lostStream" : paused ? "paused" : plot.stale ? "waitForNewReadings" :
      plot.count ? "livePreview" : "waitForData"));
  text("notice", displayMessage(actionError || data.message));
  byId("notice").dataset.tone = actionError ? "error" : tone;

  byId("pause").disabled = !plot.count || !connection;
  text("pause", t(paused ? "resume" : "pause"));
  const key = `${observed?.id}/${view.count}/${paused}`;
  if (key !== lastPlotKey) {
    lastPlotKey = key;
    lastPlot = view;
    drawChart();
  }
  byId("chart-empty").hidden = Number.isFinite(view.min_um);
  text("empty-text", t(view.count ? "noValidReadings" : connection ? "waitForReading" : "connectForPreview"));
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
    if (!response.ok) throw new Error(answer.error || copy.actionFailed[0]);
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

function renderStatusError(detail) {
  if (renderFailed) {
    text("notice", t("displayError", {detail}));
    text("plot-state", t("displayFailed"));
  } else if (shuttingDown) {
    text("phase", t("closed"));
    byId("phase").dataset.tone = "quiet";
    text("session-label", t("relaunch"));
    text("notice", t("serviceExited"));
    byId("notice").dataset.tone = "quiet";
    text("plot-state", t("previewEnded"));
    if (state?.preview.count) text("reading-label", t("lastReading"));
    byId("pause").disabled = true;
  } else {
    text("notice", t("requestError", {detail}));
    text("plot-state", t("waitForService"));
  }
  renderControls();
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
    statusError = null;
  } catch (error) {
    renderFailed = statusReceived;
    if (!statusReceived) setOnline(false);
    statusError = error.message;
    renderStatusError(statusError);
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
    actionError = copy.invalidDuration[0];
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
  if (state?.recording_active && !window.confirm(t("confirmShutdown"))) return;
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
languageSelect.addEventListener("change", () => {
  language = languageSelect.value;
  try { localStorage.setItem("qc20Language", language); } catch {}
  applyLanguage();
});
applyLanguage();
new ResizeObserver(drawChart).observe(byId("chart-wrap"));
poll();
