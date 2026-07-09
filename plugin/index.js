const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const https = require('node:https');
const net = require('node:net');
const path = require('node:path');
const { spawn } = require('node:child_process');

const DEFAULT_SETTINGS = {
  endpoint: 'ws://127.0.0.1:41922',
  targetKind: 'device',
  target: '',
  targetId: '',
  volumeStep: 2,
  pollMs: 1000,
  presetJson: '',
  presetsJson: '',
  presetName: '',
  presetDialMode: 'volume',
  presetApplyMode: 'both',
  presetApplyDelayMs: 700,
  displayMode: 'volume',
  batteryName: '',
  titleLabel: '',
  invertKnob: false,
  minVolume: 0,
  maxVolume: 100,
  generatedImages: true,
  batteryWarnPercent: 20
};

const SONAR_SUBAPPS_URLS = [
  'https://127.0.0.1:6327/subApps'
];

const contexts = {};
const helperState = { connected: false, targets: {}, batteries: {}, lastError: '', lastUpdatedAt: 0 };
const sonarState = { url: '', connected: false, lastError: '', lastUpdatedAt: 0, retryAfter: 0, polls: {} };
const presetDialState = {};
const keyEventTimes = {};
const pluginLogFile = path.join(__dirname, 'streamdock-sonar-plugin.log');

let streamDock = null;
let pluginUuid = '';
let helperSocket = null;
let helperProcess = null;
let reconnectTimer = null;
let reconnectDelay = 2000;
let sonarPollTimer = null;
let shuttingDown = false;

function parseArgs(argv) {
  const args = {};
  for (let index = 2; index < argv.length - 1; index += 2) {
    if (String(argv[index]).startsWith('-')) {
      args[String(argv[index]).replace(/^-+/, '')] = argv[index + 1];
    }
  }
  return {
    port: args.port || argv[3],
    uuid: args.pluginUUID || args.uuid || argv[5],
    registerEvent: args.registerEvent || argv[7]
  };
}

function parseJson(value, fallback) {
  try {
    return typeof value === 'string' ? JSON.parse(value) : value;
  } catch {
    return fallback;
  }
}

class SimpleWebSocket {
  constructor(url) {
    const parsed = new URL(url);
    this.url = parsed;
    this.socket = null;
    this.buffer = Buffer.alloc(0);
    this.readyState = SimpleWebSocket.CONNECTING;
    this.onopen = null;
    this.onmessage = null;
    this.onclose = null;
    this.onerror = null;
  }

  connect() {
    const key = crypto.randomBytes(16).toString('base64');
    this.socket = net.createConnection(Number(this.url.port), this.url.hostname, () => {
      this.socket.write([
        `GET ${this.url.pathname || '/'} HTTP/1.1`,
        `Host: ${this.url.host}`,
        'Upgrade: websocket',
        'Connection: Upgrade',
        `Sec-WebSocket-Key: ${key}`,
        'Sec-WebSocket-Version: 13',
        '',
        ''
      ].join('\r\n'));
    });
    this.socket.on('data', chunk => this.handleData(chunk));
    this.socket.on('close', () => {
      this.readyState = SimpleWebSocket.CLOSED;
      this.onclose && this.onclose();
    });
    this.socket.on('error', error => {
      this.onerror && this.onerror(error);
    });
    return this;
  }

  handleData(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    if (this.readyState === SimpleWebSocket.CONNECTING) {
      const headerEnd = this.buffer.indexOf('\r\n\r\n');
      if (headerEnd === -1) return;
      const header = this.buffer.slice(0, headerEnd).toString('utf8');
      this.buffer = this.buffer.slice(headerEnd + 4);
      if (!/^HTTP\/1\.1 101/i.test(header)) {
        this.onerror && this.onerror(new Error('WebSocket upgrade failed'));
        this.close();
        return;
      }
      this.readyState = SimpleWebSocket.OPEN;
      this.onopen && this.onopen();
    }
    this.readFrames();
  }

  readFrames() {
    while (this.buffer.length >= 2) {
      const first = this.buffer[0];
      const second = this.buffer[1];
      const opcode = first & 0x0f;
      const masked = (second & 0x80) !== 0;
      let length = second & 0x7f;
      let offset = 2;
      if (length === 126) {
        if (this.buffer.length < offset + 2) return;
        length = this.buffer.readUInt16BE(offset);
        offset += 2;
      } else if (length === 127) {
        if (this.buffer.length < offset + 8) return;
        const high = this.buffer.readUInt32BE(offset);
        const low = this.buffer.readUInt32BE(offset + 4);
        length = high * 2 ** 32 + low;
        offset += 8;
      }
      let mask;
      if (masked) {
        if (this.buffer.length < offset + 4) return;
        mask = this.buffer.slice(offset, offset + 4);
        offset += 4;
      }
      if (this.buffer.length < offset + length) return;
      let payload = this.buffer.slice(offset, offset + length);
      this.buffer = this.buffer.slice(offset + length);
      if (masked) {
        payload = Buffer.from(payload.map((byte, index) => byte ^ mask[index % 4]));
      }
      if (opcode === 0x8) {
        this.close();
        return;
      }
      if (opcode === 0x1) {
        this.onmessage && this.onmessage({ data: payload.toString('utf8') });
      }
    }
  }

  send(text) {
    if (this.readyState !== SimpleWebSocket.OPEN || !this.socket) return false;
    const payload = Buffer.from(String(text), 'utf8');
    let header;
    if (payload.length < 126) {
      header = Buffer.alloc(2);
      header[1] = 0x80 | payload.length;
    } else if (payload.length < 65536) {
      header = Buffer.alloc(4);
      header[1] = 0x80 | 126;
      header.writeUInt16BE(payload.length, 2);
    } else {
      header = Buffer.alloc(10);
      header[1] = 0x80 | 127;
      header.writeUInt32BE(0, 2);
      header.writeUInt32BE(payload.length, 6);
    }
    header[0] = 0x81;
    const mask = crypto.randomBytes(4);
    const masked = Buffer.from(payload.map((byte, index) => byte ^ mask[index % 4]));
    this.socket.write(Buffer.concat([header, mask, masked]));
    return true;
  }

  close() {
    if (this.socket) {
      this.socket.destroy();
    }
  }
}
SimpleWebSocket.CONNECTING = 0;
SimpleWebSocket.OPEN = 1;
SimpleWebSocket.CLOSED = 3;

function sendToStreamDock(message) {
  if (streamDock && streamDock.readyState === SimpleWebSocket.OPEN) {
    streamDock.send(JSON.stringify(message));
  }
}

function setTitle(context, title) {
  sendToStreamDock({ event: 'setTitle', context, payload: { title } });
}

function setImage(context, image) {
  sendToStreamDock({ event: 'setImage', context, payload: { image } });
}

function showOk(context) {
  sendToStreamDock({ event: 'showOk', context });
}

function showAlert(context) {
  sendToStreamDock({ event: 'showAlert', context });
}

function logMessage(message) {
  const line = `${new Date().toISOString()} ${message}`;
  try {
    fs.appendFileSync(pluginLogFile, line + '\n');
  } catch {
    // File logging is best-effort; keep Stream Dock logging available.
  }
  sendToStreamDock({ event: 'logMessage', payload: { message: `[streamdock-sonar] ${message}` } });
}

function settingsFor(context) {
  const action = contexts[context] && contexts[context].action;
  return normalizeSettingsForAction(Object.assign({}, DEFAULT_SETTINGS, contexts[context] && contexts[context].settings || {}), action);
}

function normalizeSettingsForAction(settings, action) {
  const normalized = Object.assign({}, settings);
  if (isDirectSonarAction(action)) {
    normalized.targetKind = 'sonar';
  }
  if (action === 'local.streamdock.sonar.profile') {
    normalized.presetDialMode = 'select';
    normalized.presetApplyMode = normalized.presetApplyMode || 'press';
  }
  if (action === 'local.streamdock.sonar.helper.volume' && String(normalized.targetKind || '').toLowerCase() === 'sonar') {
    normalized.targetKind = 'device';
  }
  return normalized;
}

function isDirectSonarAction(action) {
  return action === 'local.streamdock.sonar.volume' ||
    action === 'local.streamdock.sonar.mute' ||
    action === 'local.streamdock.sonar.profile';
}

function targetState(settings) {
  let state = helperState.targets[settings.targetId || settings.target] || helperState.targets[settings.target];
  if (!state && String(settings.targetKind || '').toLowerCase() === 'sonar') {
    const fallbackTarget = sonarFallbackTargetName(settings.target);
    state = fallbackTarget && helperState.targets[fallbackTarget];
  }
  return state || {};
}

function isTargetOnline(settings) {
  return String(settings.targetKind || '').toLowerCase() === 'sonar'
    ? (sonarState.connected || helperState.connected)
    : helperState.connected;
}

function titleFor(context) {
  const settings = settingsFor(context);
  const action = contexts[context] && contexts[context].action;
  if (action === 'local.streamdock.sonar.diagnostics') {
    return `Sonar\n${sonarState.connected ? 'direct' : helperState.connected ? 'helper' : 'offline'}\n${sonarState.lastError || helperState.lastError || settings.endpoint}`;
  }
  if (action === 'local.streamdock.sonar.profile') {
    return `Profile\n${settings.presetName || 'unset'}`;
  }
  if (action === 'local.streamdock.sonar.battery' || settings.displayMode === 'battery') {
    const battery = helperState.batteries[settings.batteryName || settings.target] || helperState.batteries.default || {};
    if (!helperState.connected) return cachedTitle('Battery\noffline');
    if (typeof battery.percent === 'number') {
      return `${settings.titleLabel || battery.name || settings.batteryName || 'Headset'}\n${Math.round(battery.percent)}%${battery.charging ? '\ncharging' : ''}`;
    }
    return 'Battery\nunknown';
  }
  if (!settings.target) return 'Sonar\nunset';
  if (action === 'local.streamdock.sonar.micmute' || action === 'local.streamdock.sonar.mute') {
    return `Mute\n${targetState(settings).muted ? 'muted' : 'ready'}`;
  }
  if (!isTargetOnline(settings)) return cachedTitle('Sonar\noffline');
  const current = targetState(settings);
  if (current.available === false) return `${settings.titleLabel || settings.target}\nmissing`;
  if (current.muted) return `${settings.titleLabel || settings.target}\nmuted`;
  if (typeof current.volume === 'number') return `${settings.titleLabel || settings.target}\n${Math.round(current.volume)}%`;
  return settings.titleLabel || settings.target;
}

function cachedTitle(title) {
  const lastUpdatedAt = Math.max(helperState.lastUpdatedAt || 0, sonarState.lastUpdatedAt || 0);
  if (!lastUpdatedAt) return title;
  return `${title}\ncache ${Math.max(0, Math.round((Date.now() - lastUpdatedAt) / 1000))}s`;
}

function refreshTitles() {
  Object.keys(contexts).forEach(context => {
    setTitle(context, titleFor(context));
    if (settingsFor(context).generatedImages !== false) {
      setImage(context, imageFor(context));
    }
  });
}

function imageFor(context) {
  const settings = settingsFor(context);
  if (!isTargetOnline(settings)) return svgImage('#343a40', '#adb5bd', 'SON', 'OFF', 0);
  if ((contexts[context] && contexts[context].action) === 'local.streamdock.sonar.battery' || settings.displayMode === 'battery') {
    const battery = helperState.batteries[settings.batteryName || settings.target] || helperState.batteries.default || {};
    const percent = Number(battery.percent);
    const warn = clampBatteryWarn(settings.batteryWarnPercent);
    return svgImage(Number.isFinite(percent) && percent <= warn ? '#7f1d1d' : '#1f4f46', '#ffffff', Number.isFinite(percent) ? `${Math.round(percent)}%` : 'BAT', battery.charging ? 'CHG' : '', Number.isFinite(percent) ? percent : 0);
  }
  const state = targetState(settings);
  if (state.available === false) return svgImage('#4a3a22', '#facc15', 'MISS', settings.titleLabel || settings.target || '', 0);
  if (state.muted) return svgImage('#742a2a', '#ffffff', 'MUTE', settings.titleLabel || settings.target || '', 100);
  const volume = Number(state.volume);
  return svgImage('#234e52', '#ffffff', Number.isFinite(volume) ? `${Math.round(volume)}%` : 'SON', settings.titleLabel || settings.target || '', Number.isFinite(volume) ? volume : 40);
}

function svgImage(background, foreground, main, sub, fillPercent) {
  const fill = Math.max(0, Math.min(100, Number(fillPercent) || 0));
  const barHeight = Math.round(116 * fill / 100);
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 144 144"><rect width="144" height="144" rx="20" fill="${background}"/><rect x="14" y="${124 - barHeight}" width="116" height="${barHeight}" rx="10" fill="${foreground}" opacity="0.16"/><text x="72" y="67" text-anchor="middle" font-family="Arial, sans-serif" font-size="32" font-weight="700" fill="${foreground}">${escapeSvg(main)}</text><text x="72" y="101" text-anchor="middle" font-family="Arial, sans-serif" font-size="15" font-weight="700" fill="${foreground}">${escapeSvg(truncateImageText(sub))}</text></svg>`;
  return `data:image/svg+xml;charset=utf8,${encodeURIComponent(svg)}`;
}

function truncateImageText(value) {
  value = String(value || '');
  return value.length > 10 ? value.slice(0, 10) : value;
}

function escapeSvg(value) {
  return String(value || '').replace(/[&<>"]/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[ch]);
}

function sendTargetCommand(payload) {
  if (payload.command !== 'subscribe') {
    logMessage(`command ${payload.command || 'unknown'} kind=${payload.targetKind || ''} target=${payload.target || ''}`);
  }
  if (String(payload.targetKind || '').toLowerCase() === 'sonar') {
    return sendSonarDirect(payload).catch(error => {
      sonarState.connected = false;
      logMessage(`sonar direct failed: ${error && error.message || error}`);
      const fallback = sonarFallbackPayload(payload);
      if (String(fallback.targetKind || '').toLowerCase() === 'sonar') {
        sonarState.lastError = 'direct failed: no helper fallback';
        logMessage(`sonar helper fallback unavailable target=${payload.target || ''}`);
        return false;
      }
      sonarState.lastError = 'direct failed: helper fallback';
      logMessage(`sonar helper fallback ${payload.target || ''} -> ${fallback.target || ''}`);
      return helperSend(fallback);
    });
  }
  return Promise.resolve(helperSend(payload));
}

async function sendSonarDirect(payload) {
  const state = await sonarExecute(payload);
  if (state) publishSonarState(payload.target, state);
  return true;
}

async function sonarExecute(payload) {
  if (payload.command === 'subscribe') return getSonarTargetState(payload.target);
  if (payload.command === 'set_volume') {
    await setSonarVolume(payload.target, payload.value);
    return getSonarTargetState(payload.target);
  }
  if (payload.command === 'set_mute') {
    await setSonarMute(payload.target, payload.value === 1 || payload.value === true || payload.value === 'true');
    return getSonarTargetState(payload.target);
  }
  if (payload.command === 'toggle_mute') {
    const state = await getSonarTargetState(payload.target);
    await setSonarMute(payload.target, !state.muted);
    return getSonarTargetState(payload.target);
  }
  if (payload.command === 'volume_delta') {
    const state = await getSonarTargetState(payload.target);
    await setSonarVolume(payload.target, Math.max(0, Math.min(100, state.volume + Number(payload.amount || 0))));
    return getSonarTargetState(payload.target);
  }
  throw new Error(`unsupported direct command: ${payload.command}`);
}

function publishSonarState(target, state) {
  sonarState.connected = true;
  sonarState.lastError = '';
  sonarState.lastUpdatedAt = Date.now();
  helperState.targets[target] = { volume: state.volume, muted: state.muted, available: true, source: 'sonar' };
  refreshTitles();
}

async function getSonarTargetState(target) {
  const parsed = parseSonarTarget(target);
  const settings = await sonarRequest(parsed.mode === 'classic' ? '/VolumeSettings/classic' : '/VolumeSettings/streamer', 'GET');
  let node;
  if (parsed.channel === 'master') {
    node = parsed.mode === 'classic' ? settings.masters && settings.masters.classic : settings.masters && settings.masters.stream && settings.masters.stream[parsed.mix];
  } else {
    const device = settings.devices && settings.devices[parsed.apiChannel];
    node = parsed.mode === 'classic' ? device && device.classic : device && device.stream && device.stream[parsed.mix];
  }
  if (!node || typeof node.volume !== 'number') throw new Error(`missing Sonar state for ${target}`);
  return { volume: node.volume * 100, muted: !!node.muted };
}

function setSonarVolume(target, value) {
  const parsed = parseSonarTarget(target);
  const scalar = Math.max(0, Math.min(1, Number(value) / 100)).toFixed(2);
  if (parsed.mode === 'classic') return sonarRequest(`/VolumeSettings/classic/${parsed.httpChannel}/Volume/${scalar}`, 'PUT');
  return sonarRequest(`/VolumeSettings/streamer/${parsed.mix}/${parsed.httpChannel}/volume/${scalar}`, 'PUT');
}

function setSonarMute(target, muted) {
  const parsed = parseSonarTarget(target);
  const value = muted ? 'true' : 'false';
  if (parsed.mode === 'classic') return sonarRequest(`/VolumeSettings/classic/${parsed.httpChannel}/Mute/${value}`, 'PUT');
  return sonarRequest(`/VolumeSettings/streamer/${parsed.mix}/${parsed.httpChannel}/isMuted/${value}`, 'PUT');
}

function parseSonarTarget(target) {
  const parts = String(target || '').split(':');
  const mode = parts[0];
  let mix = '';
  let channel = '';
  if (mode === 'classic' && parts.length === 2) {
    channel = parts[1];
  } else if (mode === 'streamer' && parts.length === 3) {
    mix = parts[1];
    channel = parts[2];
  } else {
    throw new Error(`invalid Sonar target: ${target}`);
  }
  if (mode === 'streamer' && mix !== 'monitoring' && mix !== 'streaming') throw new Error(`invalid Sonar mix: ${mix}`);
  const apiChannel = sonarApiChannel(channel);
  return { mode, mix, channel, apiChannel, httpChannel: channel === 'master' ? (mode === 'classic' ? 'Master' : 'master') : apiChannel };
}

function sonarApiChannel(channel) {
  const map = { master: 'master', game: 'game', chat: 'chatRender', media: 'media', aux: 'aux', mic: 'chatCapture' };
  if (!map[channel]) throw new Error(`invalid Sonar channel: ${channel}`);
  return map[channel];
}

async function sonarRequest(route, method) {
  const baseUrl = await getSonarUrl();
  const parsed = new URL(route, baseUrl.replace(/\/+$/, '') + '/');
  const client = parsed.protocol === 'https:' ? https : http;
  const options = {
    method,
    hostname: parsed.hostname,
    port: parsed.port,
    path: parsed.pathname + parsed.search,
    rejectUnauthorized: false,
    timeout: 2500
  };
  return new Promise((resolve, reject) => {
    const request = client.request(options, response => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', chunk => { body += chunk; });
      response.on('end', () => {
        if (response.statusCode < 200 || response.statusCode >= 300) {
          reject(new Error(`Sonar HTTP ${response.statusCode}`));
          return;
        }
        if (method === 'PUT' && !body) {
          resolve({});
          return;
        }
        resolve(parseJson(body, {}));
      });
    });
    request.on('error', reject);
    request.on('timeout', () => request.destroy(new Error('Sonar request timeout')));
    request.end();
  });
}

async function getSonarUrl() {
  if (sonarState.url) return sonarState.url;
  const endpoints = Array.from(new Set([...candidateSubAppsUrls(), ...SONAR_SUBAPPS_URLS]));
  let lastError = null;
  for (const endpoint of endpoints) {
    try {
      const payload = await requestJson(endpoint, { rejectUnauthorized: false });
      const sonar = payload && payload.subApps && payload.subApps.sonar;
      if (!sonar || sonar.isEnabled === false || sonar.isReady === false || sonar.isRunning === false) {
        throw new Error('Sonar is not ready');
      }
      const url = sonar.metadata && sonar.metadata.webServerAddress;
      if (!isLoopbackHttpUrl(url)) throw new Error('Sonar URL is not loopback');
      sonarState.url = url;
      return url;
    } catch (error) {
      lastError = error;
    }
  }
  throw lastError || new Error('Sonar endpoint not found');
}

function candidateSubAppsUrls() {
  const urls = [];
  for (const file of candidateCorePropsFiles()) {
    try {
      if (!fs.existsSync(file)) continue;
      const props = JSON.parse(fs.readFileSync(file, 'utf8'));
      for (const key of ['ggEncryptedAddress', 'encryptedAddress', 'address']) {
        if (props[key]) urls.push(`${String(props[key]).startsWith('http') ? props[key] : `https://${props[key]}`}/subApps`);
      }
    } catch {
      // Try the next known coreProps location.
    }
  }
  return urls;
}

function candidateCorePropsFiles() {
  const programData = process.env.ProgramData || 'C:\\ProgramData';
  const local = process.env.LOCALAPPDATA || '';
  return [
    path.join(programData, 'SteelSeries', 'GG', 'coreProps.json'),
    path.join(programData, 'SteelSeries', 'SteelSeries Engine 3', 'coreProps.json'),
    local ? path.join(local, 'SteelSeries', 'GG', 'coreProps.json') : ''
  ].filter(Boolean);
}

function requestJson(url, options) {
  const parsed = new URL(url);
  const client = parsed.protocol === 'https:' ? https : http;
  return new Promise((resolve, reject) => {
    const request = client.request({
      method: 'GET',
      hostname: parsed.hostname,
      port: parsed.port,
      path: parsed.pathname + parsed.search,
      rejectUnauthorized: options && options.rejectUnauthorized === false ? false : true,
      timeout: 2500
    }, response => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', chunk => { body += chunk; });
      response.on('end', () => {
        if (response.statusCode < 200 || response.statusCode >= 300) {
          reject(new Error(`HTTP ${response.statusCode}`));
          return;
        }
        resolve(parseJson(body, {}));
      });
    });
    request.on('error', reject);
    request.on('timeout', () => request.destroy(new Error('request timeout')));
    request.end();
  });
}

function isLoopbackHttpUrl(value) {
  try {
    const url = new URL(value);
    return (url.protocol === 'http:' || url.protocol === 'https:') &&
      ['localhost', '127.0.0.1', '::1', '[::1]'].includes(url.hostname);
  } catch {
    return false;
  }
}

function sonarFallbackPayload(payload) {
  const fallbackTarget = sonarFallbackTargetName(payload.target);
  if (!fallbackTarget) return payload;
  return Object.assign({}, payload, { targetKind: 'device', target: fallbackTarget, targetId: '' });
}

function sonarFallbackTargetName(target) {
  try {
    const parsed = parseSonarTarget(target);
    if (parsed.mode !== 'classic') return '';
    return { game: 'Sonar - Gaming', chat: 'Sonar - Chat', media: 'Sonar - Media', aux: 'Sonar - Aux' }[parsed.channel] || '';
  } catch {
    return '';
  }
}

function helperSend(payload) {
  if (!helperSocket || helperSocket.readyState !== SimpleWebSocket.OPEN) {
    connectHelper();
    return false;
  }
  helperSocket.send(JSON.stringify(payload));
  return true;
}

function connectHelper(settings) {
  settings = Object.assign({}, DEFAULT_SETTINGS, settings || {});
  startBundledHelper(settings);
  if (helperSocket && (helperSocket.readyState === SimpleWebSocket.OPEN || helperSocket.readyState === SimpleWebSocket.CONNECTING)) return;
  clearTimeout(reconnectTimer);
  helperSocket = new SimpleWebSocket(settings.endpoint).connect();
  helperSocket.onopen = () => {
    helperState.connected = true;
    helperState.lastError = '';
    reconnectDelay = 2000;
    refreshTitles();
    Object.keys(contexts).forEach(context => {
      const contextSettings = settingsFor(context);
      if (contextSettings.target) {
        helperSend({ command: 'subscribe', targetKind: contextSettings.targetKind, target: contextSettings.target, targetId: contextSettings.targetId, pollMs: clampPollMs(contextSettings.pollMs) });
      }
      if (contextSettings.displayMode === 'battery' || (contexts[context] && contexts[context].action) === 'local.streamdock.sonar.battery') {
        helperSend({ command: 'battery', target: contextSettings.batteryName || contextSettings.target, pollMs: clampPollMs(contextSettings.pollMs) });
      }
    });
  };
  helperSocket.onmessage = event => {
    const message = parseJson(event.data, {});
    if (message.event === 'state' && message.target) {
      helperState.lastUpdatedAt = Date.now();
      helperState.targets[message.target] = Object.assign({}, helperState.targets[message.target] || {}, message.payload || {});
      if (message.targetId) helperState.targets[message.targetId] = helperState.targets[message.target];
    }
    if (message.event === 'battery') {
      helperState.lastUpdatedAt = Date.now();
      const key = message.name || message.target || 'default';
      helperState.batteries[key] = { name: key, percent: message.percent, charging: message.charging };
      helperState.batteries.default = helperState.batteries[key];
    }
    if (message.event === 'unavailable' && message.target) {
      helperState.targets[message.target] = { available: false };
    }
    refreshTitles();
  };
  helperSocket.onclose = () => {
    helperState.connected = false;
    helperState.lastError = 'helper closed';
    refreshTitles();
    if (shuttingDown) return;
    const delay = reconnectDelay;
    reconnectDelay = Math.min(30000, reconnectDelay * 2);
    reconnectTimer = setTimeout(() => connectHelper(settings), delay);
  };
  helperSocket.onerror = () => {
    helperState.connected = false;
    helperState.lastError = 'helper error';
    refreshTitles();
  };
}

function startBundledHelper(settings) {
  if (helperProcess) return;
  const helperPath = bundledHelperPath();
  if (!helperPath) {
    helperState.lastError = 'helper exe not found';
    logMessage('helper exe not found');
    return;
  }
  const prefix = websocketEndpointToHttpPrefix(settings.endpoint || DEFAULT_SETTINGS.endpoint);
  const logFile = path.join(path.dirname(helperPath), 'SonarAudioHelper.log');
  const args = prefix ? [`--prefix=${prefix}`] : [];
  args.push(`--log-file=${logFile}`);
  logMessage(`starting helper: ${helperPath}`);
  helperProcess = spawn(helperPath, args, {
    cwd: path.dirname(helperPath),
    stdio: 'ignore',
    windowsHide: true
  });
  helperProcess.on('error', error => {
    helperProcess = null;
    helperState.connected = false;
    helperState.lastError = `helper start failed: ${error && error.message || error}`;
    logMessage(helperState.lastError);
    refreshTitles();
  });
  helperProcess.on('exit', code => {
    helperProcess = null;
    if (code && code !== 0) {
      helperState.connected = false;
      helperState.lastError = `helper exited: ${code}`;
      logMessage(helperState.lastError);
      refreshTitles();
    }
  });
}

function stopBundledHelper() {
  clearTimeout(reconnectTimer);
  reconnectTimer = null;
  if (helperSocket) {
    helperSocket.close();
    helperSocket = null;
  }
  if (!helperProcess) return;
  const child = helperProcess;
  helperProcess = null;
  try {
    if (child.pid) {
      logMessage(`stopping helper: ${child.pid}`);
      child.kill();
    }
  } catch (error) {
    logMessage(`helper stop failed: ${error && error.message || error}`);
  }
}

function shutdown(exitCode) {
  if (shuttingDown) return;
  shuttingDown = true;
  clearInterval(sonarPollTimer);
  sonarPollTimer = null;
  stopBundledHelper();
  process.exit(exitCode);
}

function bundledHelperPath() {
  const candidates = [
    path.join(__dirname, 'helper', 'SonarAudioHelper.exe'),
    path.join(__dirname, '..', 'helper', 'SonarAudioHelper.exe'),
    path.join(__dirname, '..', 'dist', 'helper', 'SonarAudioHelper.exe')
  ];
  return candidates.find(candidate => fs.existsSync(candidate)) || '';
}

function websocketEndpointToHttpPrefix(endpoint) {
  try {
    const parsed = new URL(endpoint);
    if (parsed.protocol !== 'ws:' && parsed.protocol !== 'wss:') return '';
    parsed.protocol = parsed.protocol === 'wss:' ? 'https:' : 'http:';
    if (!parsed.pathname || parsed.pathname === '/') parsed.pathname = '/';
    return parsed.toString();
  } catch {
    return '';
  }
}

function needsHelper(settings, action) {
  if (action === 'local.streamdock.sonar.battery') return true;
  if (action === 'local.streamdock.sonar.helper.volume') return true;
  if (action === 'local.streamdock.sonar.micmute') return true;
  if (settings.displayMode === 'battery') return true;
  if (isDirectSonarAction(action)) return false;
  return String(settings.targetKind || '').toLowerCase() !== 'sonar';
}

function presetTargets(settings, fallbackTicks) {
  const namedPreset = namedPresetTargets(settings);
  if (namedPreset) return namedPreset;
  if (!settings.presetJson) {
    return [{ targetKind: settings.targetKind, target: settings.target, targetId: settings.targetId, amount: fallbackTicks * (Number(settings.volumeStep) || 2) }];
  }
  try {
    const parsed = JSON.parse(settings.presetJson);
    if (!Array.isArray(parsed)) return [];
    return parsed.map(item => ({
      targetKind: item.targetKind || settings.targetKind,
      target: item.target || '',
      targetId: item.targetId || '',
      amount: Number(item.amount) || fallbackTicks * (Number(settings.volumeStep) || 2)
    })).filter(item => item.target);
  } catch {
    return [];
  }
}

function namedPresetTargets(settings) {
  if (!settings.presetsJson || !settings.presetName) return null;
  try {
    const presets = JSON.parse(settings.presetsJson);
    const preset = Array.isArray(presets) ? presets.find(item => item && item.name === settings.presetName) : presets[settings.presetName];
    const targets = preset && (preset.targets || preset);
    if (!Array.isArray(targets)) return null;
    return targets.map(item => ({
      targetKind: item.targetKind || settings.targetKind,
      target: item.target || '',
      targetId: item.targetId || '',
      amount: Number(item.amount) || 0,
      setVolume: item.setVolume === undefined ? null : clampVolume(Number(item.setVolume), settings),
      mute: item.mute
    })).filter(item => item.target || item.targetId);
  } catch {
    return null;
  }
}

function presetNames(settings) {
  if (!settings.presetsJson) return [];
  try {
    const presets = JSON.parse(settings.presetsJson);
    if (Array.isArray(presets)) return presets.map(item => item && item.name).filter(Boolean);
    return Object.keys(presets || {});
  } catch {
    return [];
  }
}

function applyPreset(context) {
  const settings = settingsFor(context);
  const targets = presetTargets(settings, 1);
  if (targets.length === 0 || (!settings.target && !settings.presetJson && !(settings.presetsJson && settings.presetName))) {
    refreshTitles();
    showAlert(context);
    return;
  }
  const tasks = targets.map(target => {
    if ((target.setVolume !== undefined && target.setVolume !== null) || target.mute !== undefined) {
      return applyPresetTarget(target, settings);
    }
    return sendTargetCommand({ command: 'toggle_mute', targetKind: target.targetKind, target: target.target, targetId: target.targetId || settings.targetId });
  });
  Promise.all(tasks).then(results => results.every(Boolean) ? showOk(context) : showAlert(context)).catch(() => showAlert(context));
}

function applyPresetTarget(target, settings) {
  const tasks = [];
  if (target.setVolume !== undefined && target.setVolume !== null && Number.isFinite(target.setVolume)) {
    tasks.push(sendTargetCommand({ command: 'set_volume', targetKind: target.targetKind, target: target.target, targetId: target.targetId || settings.targetId, value: clampVolume(target.setVolume, settings) }));
  }
  if (target.mute !== undefined) {
    tasks.push(sendTargetCommand({ command: 'set_mute', targetKind: target.targetKind, target: target.target, targetId: target.targetId || settings.targetId, value: target.mute === true || target.mute === 'true' ? 1 : 0 }));
  }
  return Promise.all(tasks).then(results => results.every(Boolean));
}

function adjustVolume(context, ticks) {
  const settings = settingsFor(context);
  if (settings.invertKnob === true || settings.invertKnob === 'true') ticks = -ticks;
  const targets = presetTargets(settings, ticks);
  if (targets.length === 0 || !ticks) {
    refreshTitles();
    if (targets.length === 0) showAlert(context);
    return;
  }
  const tasks = targets.map(target => {
    const current = helperState.targets[target.targetId || target.target] || helperState.targets[target.target] || {};
    let targetSetVolume = target.setVolume;
    if ((targetSetVolume === null || !Number.isFinite(targetSetVolume)) && typeof current.volume === 'number') {
      targetSetVolume = clampVolume(Number(current.volume) + Number(target.amount || 0), settings);
    }
    return sendTargetCommand({ command: targetSetVolume !== null && Number.isFinite(targetSetVolume) ? 'set_volume' : 'volume_delta', targetKind: target.targetKind, target: target.target, targetId: target.targetId || settings.targetId, amount: target.amount, value: targetSetVolume });
  });
  Promise.all(tasks).then(results => results.every(Boolean) ? showOk(context) : showAlert(context)).catch(() => showAlert(context));
}

function selectPresetByDial(context, ticks) {
  const settings = settingsFor(context);
  const names = presetNames(settings);
  if (!names.length || !ticks) {
    showAlert(context);
    return;
  }
  const currentName = presetDialState[context] && presetDialState[context].name || settings.presetName || names[0];
  let index = names.indexOf(currentName);
  if (index === -1) index = 0;
  index = (index + (ticks > 0 ? 1 : -1) + names.length) % names.length;
  presetDialState[context] = presetDialState[context] || {};
  presetDialState[context].name = names[index];
  contexts[context].settings = Object.assign({}, contexts[context].settings || {}, { presetName: names[index] });
  setTitle(context, `Preset\n${names[index]}\nready`);
  clearTimeout(presetDialState[context].timer);
  if (settings.presetApplyMode === 'rotateEnd' || settings.presetApplyMode === 'both') {
    presetDialState[context].timer = setTimeout(() => contexts[context] && applyPreset(context), Math.max(100, Number(settings.presetApplyDelayMs) || 700));
  }
}

function clampVolume(value, settings) {
  let min = Number(settings.minVolume);
  let max = Number(settings.maxVolume);
  if (!Number.isFinite(min)) min = 0;
  if (!Number.isFinite(max)) max = 100;
  if (max < min) [min, max] = [max, min];
  return Math.max(min, Math.min(max, Number(value) || 0));
}

function clampPollMs(value) {
  const ms = Number(value) || 1000;
  return Math.max(250, Math.min(60000, ms));
}

function clampBatteryWarn(value) {
  const pct = Number(value);
  if (!Number.isFinite(pct)) return 20;
  return Math.max(1, Math.min(100, Math.round(pct)));
}

function rememberContext(message) {
  contexts[message.context] = {
    action: message.action,
    settings: message.payload && message.payload.settings || {}
  };
  const settings = settingsFor(message.context);
  if (needsHelper(settings, message.action)) {
    connectHelper(settings);
  }
  if (settings.target) {
    sendTargetCommand({ command: 'subscribe', targetKind: settings.targetKind, target: settings.target, targetId: settings.targetId, pollMs: clampPollMs(settings.pollMs) });
  }
  if (settings.displayMode === 'battery' || message.action === 'local.streamdock.sonar.battery') {
    helperSend({ command: 'battery', target: settings.batteryName || settings.target, pollMs: clampPollMs(settings.pollMs) });
  }
  setTitle(message.context, titleFor(message.context));
}

function pollSonarTargets() {
  Object.keys(contexts).forEach(context => {
    const settings = settingsFor(context);
    if (String(settings.targetKind || '').toLowerCase() !== 'sonar' || !settings.target) return;
    const pollMs = clampPollMs(settings.pollMs);
    const key = settings.target;
    const lastPoll = sonarState.polls[key] || 0;
    if (Date.now() - lastPoll < pollMs) return;
    sonarState.polls[key] = Date.now();
    sendTargetCommand({ command: 'subscribe', targetKind: 'sonar', target: settings.target, pollMs }).catch(() => {});
  });
}

function handleMessage(event) {
  const message = parseJson(event.data, {});
  if (message.event === 'willAppear' || message.event === 'didReceiveSettings') {
    rememberContext(message);
  } else if (message.event === 'willDisappear') {
    if (presetDialState[message.context] && presetDialState[message.context].timer) clearTimeout(presetDialState[message.context].timer);
    delete presetDialState[message.context];
    delete contexts[message.context];
  } else if (message.event === 'keyDown' || message.event === 'touchTap' || message.event === 'dialDown' || message.event === 'dialPress') {
    const lastKeyEvent = keyEventTimes[message.context] || 0;
    keyEventTimes[message.context] = Date.now();
    if (Date.now() - lastKeyEvent < 150) return;
    const action = contexts[message.context] && contexts[message.context].action;
    logMessage(`input ${message.event} action=${action || 'unknown'}`);
    if (action === 'local.streamdock.sonar.volume' || action === 'local.streamdock.sonar.helper.volume') {
      refreshTitles();
    } else if (action === 'local.streamdock.sonar.battery') {
      const batterySettings = settingsFor(message.context);
      helperSend({ command: 'battery', target: batterySettings.batteryName || batterySettings.target, pollMs: Number(batterySettings.pollMs) || 1000 });
    } else if (settingsFor(message.context).presetDialMode === 'select' && settingsFor(message.context).presetApplyMode === 'rotateEnd') {
      refreshTitles();
    } else {
      applyPreset(message.context);
    }
  } else if (message.event === 'dialRotate') {
    const ticks = Number(message.payload && (message.payload.ticks || message.payload.delta || message.payload.rotation)) || 0;
    logMessage(`input dialRotate ticks=${ticks}`);
    if (settingsFor(message.context).presetDialMode === 'select' && presetNames(settingsFor(message.context)).length) {
      selectPresetByDial(message.context, ticks);
    } else {
      adjustVolume(message.context, ticks);
    }
  }
}

function main() {
  const args = parseArgs(process.argv);
  pluginUuid = args.uuid;
  logMessage(`plugin starting log=${pluginLogFile}`);
  logMessage(`plugin args port=${args.port || ''} uuid=${args.uuid || ''} registerEvent=${args.registerEvent || ''}`);
  streamDock = new SimpleWebSocket(`ws://127.0.0.1:${args.port}`).connect();
  streamDock.onopen = () => sendToStreamDock({ event: args.registerEvent, uuid: pluginUuid });
  streamDock.onmessage = handleMessage;
  streamDock.onclose = () => shutdown(0);
  streamDock.onerror = error => {
    console.error(error);
  };
  sonarPollTimer = setInterval(pollSonarTargets, 250);
}

process.once('SIGINT', () => shutdown(0));
process.once('SIGTERM', () => shutdown(0));
process.once('exit', () => {
  shuttingDown = true;
  stopBundledHelper();
});

main();
