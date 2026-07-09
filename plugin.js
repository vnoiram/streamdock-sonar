(function () {
  'use strict';

  var DEFAULT_SETTINGS = {
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

  var streamDockSocket = null;
  var pluginUuid = null;
  var helperSocket = null;
  var reconnectTimer = null;
  var reconnectDelay = 2000;
  var contexts = {};
  var helperState = { connected: false, targets: {}, batteries: {}, lastError: '', lastUpdatedAt: 0 };
  var sonarDirect = { url: '', urlPromise: null, connected: false, lastError: '', lastUpdatedAt: 0, retryAfter: 0, polls: {} };
  var presetDialState = {};
  var SONAR_SUBAPPS_URL = 'https://127.0.0.1:6327/subApps';
  var SONAR_TARGETS = [
    { target: 'classic:master', label: 'Classic Master' },
    { target: 'classic:game', label: 'Classic Game' },
    { target: 'classic:chat', label: 'Classic Chat' },
    { target: 'classic:media', label: 'Classic Media' },
    { target: 'classic:aux', label: 'Classic Aux' },
    { target: 'classic:mic', label: 'Classic Mic' },
    { target: 'streamer:monitoring:master', label: 'Stream Personal Master' },
    { target: 'streamer:monitoring:game', label: 'Stream Personal Game' },
    { target: 'streamer:monitoring:chat', label: 'Stream Personal Chat' },
    { target: 'streamer:monitoring:media', label: 'Stream Personal Media' },
    { target: 'streamer:monitoring:aux', label: 'Stream Personal Aux' },
    { target: 'streamer:monitoring:mic', label: 'Stream Personal Mic' },
    { target: 'streamer:streaming:master', label: 'Stream Broadcast Master' },
    { target: 'streamer:streaming:game', label: 'Stream Broadcast Game' },
    { target: 'streamer:streaming:chat', label: 'Stream Broadcast Chat' },
    { target: 'streamer:streaming:media', label: 'Stream Broadcast Media' },
    { target: 'streamer:streaming:aux', label: 'Stream Broadcast Aux' },
    { target: 'streamer:streaming:mic', label: 'Stream Broadcast Mic' }
  ];

  function parseJson(value, fallback) {
    try {
      return typeof value === 'string' ? JSON.parse(value) : value;
    } catch (error) {
      return fallback;
    }
  }

  function sendToStreamDock(message) {
    if (streamDockSocket && streamDockSocket.readyState === WebSocket.OPEN) {
      streamDockSocket.send(JSON.stringify(message));
    }
  }

  function setTitle(context, title) {
    sendToStreamDock({ event: 'setTitle', context: context, payload: { title: title } });
  }

  function setImage(context, image) {
    sendToStreamDock({ event: 'setImage', context: context, payload: { image: image } });
  }

  function logMessage(message) {
    sendToStreamDock({ event: 'logMessage', payload: { message: '[streamdock-sonar] ' + message } });
  }

  function showOk(context) {
    sendToStreamDock({ event: 'showOk', context: context });
  }

  function showAlert(context) {
    sendToStreamDock({ event: 'showAlert', context: context });
  }

  function settingsFor(context) {
    return Object.assign({}, DEFAULT_SETTINGS, contexts[context] && contexts[context].settings || {});
  }

  function presetTargets(settings, fallbackTicks) {
    var namedPreset = namedPresetTargets(settings);
    if (namedPreset) {
      return namedPreset;
    }
    if (!settings.presetJson) {
      return [{ targetKind: settings.targetKind, target: settings.target, targetId: settings.targetId, amount: fallbackTicks * (Number(settings.volumeStep) || 2) }];
    }
    try {
      var parsed = JSON.parse(settings.presetJson);
      if (!Array.isArray(parsed)) {
        return [];
      }
      return parsed.map(function (item) {
        return {
          targetKind: item.targetKind || settings.targetKind,
          target: item.target || '',
          targetId: item.targetId || '',
          amount: Number(item.amount) || fallbackTicks * (Number(settings.volumeStep) || 2)
        };
      }).filter(function (item) { return item.target; });
    } catch (error) {
      return [];
    }
  }

  function namedPresetTargets(settings) {
    if (!settings.presetsJson || !settings.presetName) {
      return null;
    }
    try {
      var presets = JSON.parse(settings.presetsJson);
      var preset = Array.isArray(presets) ? presets.filter(function (item) { return item && item.name === settings.presetName; })[0] : presets[settings.presetName];
      var targets = preset && (preset.targets || preset);
      if (!Array.isArray(targets)) {
        return null;
      }
      return targets.map(function (item) {
        return {
          targetKind: item.targetKind || settings.targetKind,
          target: item.target || '',
          targetId: item.targetId || '',
          amount: Number(item.amount) || 0,
          setVolume: item.setVolume === undefined ? null : clampVolume(Number(item.setVolume), settings),
          mute: item.mute
        };
      }).filter(function (item) { return item.target || item.targetId; });
    } catch (error) {
      return null;
    }
  }

  function presetNames(settings) {
    if (!settings.presetsJson) {
      return [];
    }
    try {
      var presets = JSON.parse(settings.presetsJson);
      if (Array.isArray(presets)) {
        return presets.map(function (item) { return item && item.name; }).filter(Boolean);
      }
      return Object.keys(presets || {});
    } catch (error) {
      return [];
    }
  }

  function targetState(settings) {
    var state = helperState.targets[settings.targetId || settings.target] || helperState.targets[settings.target];
    if (!state && String(settings.targetKind || '').toLowerCase() === 'sonar') {
      var fallbackTarget = sonarFallbackTargetName(settings.target);
      state = fallbackTarget && helperState.targets[fallbackTarget];
    }
    return state || {};
  }

  function titleFor(context) {
    var settings = settingsFor(context);
    if ((contexts[context] && contexts[context].action) === 'local.streamdock.sonar.diagnostics') {
      return 'Sonar\n' + (sonarDirect.connected ? 'direct' : helperState.connected ? 'helper' : 'offline') + '\n' + (sonarDirect.lastError || helperState.lastError || settings.endpoint);
    }
    if ((contexts[context] && contexts[context].action) === 'local.streamdock.sonar.battery' || settings.displayMode === 'battery') {
      var battery = helperState.batteries[settings.batteryName || settings.target] || helperState.batteries.default || {};
      if (!helperState.connected) {
        return cachedTitle('Battery\noffline');
      }
      if (typeof battery.percent === 'number') {
        return (settings.titleLabel || battery.name || settings.batteryName || 'Headset') + '\n' + Math.round(battery.percent) + '%' + (battery.charging ? '\ncharging' : '');
      }
      return 'Battery\nunknown';
    }
    if (!settings.target) {
      return 'Sonar\nunset';
    }
    if ((contexts[context] && contexts[context].action) === 'local.streamdock.sonar.micmute') {
      return 'Mic\n' + (targetState(settings).muted ? 'muted' : 'ready');
    }
    if (!isTargetOnline(settings)) {
      return cachedTitle('Sonar\noffline');
    }
    var current = targetState(settings);
    if (current.available === false) {
      return (settings.titleLabel || settings.target) + '\nmissing';
    }
    if (current.muted) {
      return (settings.titleLabel || settings.target) + '\nmuted';
    }
    if (typeof current.volume === 'number') {
      return (settings.titleLabel || settings.target) + '\n' + Math.round(current.volume) + '%';
    }
    return settings.titleLabel || settings.target;
  }

  function cachedTitle(title) {
    var lastUpdatedAt = Math.max(helperState.lastUpdatedAt || 0, sonarDirect.lastUpdatedAt || 0);
    if (!lastUpdatedAt) {
      return title;
    }
    return title + '\ncache ' + Math.max(0, Math.round((Date.now() - lastUpdatedAt) / 1000)) + 's';
  }

  function refreshTitles() {
    Object.keys(contexts).forEach(function (context) {
      setTitle(context, titleFor(context));
      if (settingsFor(context).generatedImages !== false) {
        setImage(context, imageFor(context));
      }
    });
  }

  function imageFor(context) {
    var settings = settingsFor(context);
    if (!isTargetOnline(settings)) {
      return svgImage('#343a40', '#adb5bd', 'SON', 'OFF', 0);
    }
    if ((contexts[context] && contexts[context].action) === 'local.streamdock.sonar.battery' || settings.displayMode === 'battery') {
      var battery = helperState.batteries[settings.batteryName || settings.target] || helperState.batteries.default || {};
      var percent = Number(battery.percent);
      var warn = clampBatteryWarn(settings.batteryWarnPercent);
      return svgImage(Number.isFinite(percent) && percent <= warn ? '#7f1d1d' : '#1f4f46', '#ffffff', Number.isFinite(percent) ? Math.round(percent) + '%' : 'BAT', battery.charging ? 'CHG' : '', Number.isFinite(percent) ? percent : 0);
    }
    var state = targetState(settings);
    if (state.available === false) {
      return svgImage('#4a3a22', '#facc15', 'MISS', settings.titleLabel || settings.target || '', 0);
    }
    if (state.muted) {
      return svgImage('#742a2a', '#ffffff', 'MUTE', settings.titleLabel || settings.target || '', 100);
    }
    var volume = Number(state.volume);
    return svgImage('#234e52', '#ffffff', Number.isFinite(volume) ? Math.round(volume) + '%' : 'SON', settings.titleLabel || settings.target || '', Number.isFinite(volume) ? volume : 40);
  }

  function svgImage(background, foreground, main, sub, fillPercent) {
    var fill = Math.max(0, Math.min(100, Number(fillPercent) || 0));
    var barHeight = Math.round(116 * fill / 100);
    var svg = '<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 144 144">' +
      '<rect width="144" height="144" rx="20" fill="' + background + '"/>' +
      '<rect x="14" y="' + (124 - barHeight) + '" width="116" height="' + barHeight + '" rx="10" fill="' + foreground + '" opacity="0.16"/>' +
      '<text x="72" y="67" text-anchor="middle" font-family="Arial, sans-serif" font-size="32" font-weight="700" fill="' + foreground + '">' + escapeSvg(main) + '</text>' +
      '<text x="72" y="101" text-anchor="middle" font-family="Arial, sans-serif" font-size="15" font-weight="700" fill="' + foreground + '">' + escapeSvg(truncateImageText(sub)) + '</text>' +
      '</svg>';
    return 'data:image/svg+xml;charset=utf8,' + encodeURIComponent(svg);
  }

  function truncateImageText(value) {
    value = String(value || '');
    return value.length > 10 ? value.slice(0, 10) : value;
  }

  function escapeSvg(value) {
    return String(value || '').replace(/[&<>"]/g, function (ch) {
      return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[ch];
    });
  }

  function helperSend(payload) {
    if (!helperSocket || helperSocket.readyState !== WebSocket.OPEN) {
      connectHelper();
      return false;
    }
    helperSocket.send(JSON.stringify(payload));
    return true;
  }

  function sendTargetCommand(payload) {
    if (isSonarTarget(payload)) {
      if (Date.now() < sonarDirect.retryAfter) {
        return Promise.resolve(helperSend(sonarFallbackPayload(payload)));
      }
      return sendSonarDirect(payload).catch(function (error) {
        sonarDirect.connected = false;
        sonarDirect.lastError = 'direct failed: helper fallback';
        sonarDirect.retryAfter = Date.now() + 30000;
        logMessage('sonar direct failed: ' + (error && error.message || error));
        return helperSend(sonarFallbackPayload(payload));
      });
    }
    return Promise.resolve(helperSend(payload));
  }

  function isSonarTarget(payload) {
    return payload && String(payload.targetKind || '').toLowerCase() === 'sonar';
  }

  function sonarFallbackPayload(payload) {
    var fallbackTarget = sonarFallbackTargetName(payload.target);
    if (!fallbackTarget) {
      return payload;
    }
    return Object.assign({}, payload, {
      targetKind: 'device',
      target: fallbackTarget,
      targetId: ''
    });
  }

  function sonarFallbackTargetName(target) {
    try {
      var parsed = parseSonarTarget(target);
      if (parsed.mode !== 'classic') {
        return '';
      }
      return {
        game: 'Sonar - Gaming',
        chat: 'Sonar - Chat',
        media: 'Sonar - Media',
        aux: 'Sonar - Aux'
      }[parsed.channel] || '';
    } catch (error) {
      return '';
    }
  }

  function isTargetOnline(settings) {
    return String(settings.targetKind || '').toLowerCase() === 'sonar' ? (sonarDirect.connected || helperState.connected) : helperState.connected;
  }

  function sendSonarDirect(payload) {
    return sonarExecute(payload).then(function (state) {
      if (state) {
        publishSonarState(payload.target, state);
      }
      return true;
    });
  }

  function sonarExecute(payload) {
    var command = payload.command;
    if (command === 'subscribe') {
      return getSonarTargetState(payload.target);
    }
    if (command === 'set_volume') {
      return setSonarVolume(payload.target, payload.value).then(function () {
        return getSonarTargetState(payload.target);
      });
    }
    if (command === 'set_mute') {
      return setSonarMute(payload.target, payload.value === 1 || payload.value === true || payload.value === 'true').then(function () {
        return getSonarTargetState(payload.target);
      });
    }
    if (command === 'toggle_mute') {
      return getSonarTargetState(payload.target).then(function (state) {
        return setSonarMute(payload.target, !state.muted).then(function () {
          return getSonarTargetState(payload.target);
        });
      });
    }
    if (command === 'volume_delta') {
      return getSonarTargetState(payload.target).then(function (state) {
        return setSonarVolume(payload.target, Math.max(0, Math.min(100, state.volume + Number(payload.amount || 0)))).then(function () {
          return getSonarTargetState(payload.target);
        });
      });
    }
    throw new Error('unsupported direct command: ' + command);
  }

  function publishSonarState(target, state) {
    sonarDirect.connected = true;
    sonarDirect.lastError = '';
    sonarDirect.lastUpdatedAt = Date.now();
    sonarDirect.retryAfter = 0;
    helperState.targets[target] = {
      volume: state.volume,
      muted: state.muted,
      available: true,
      source: 'sonar'
    };
    refreshTitles();
  }

  function getSonarTargetState(target) {
    var parsed = parseSonarTarget(target);
    var route = parsed.mode === 'classic' ? '/VolumeSettings/classic' : '/VolumeSettings/streamer';
    return sonarFetch(route, 'GET').then(function (settings) {
      var node;
      if (parsed.channel === 'master') {
        node = parsed.mode === 'classic' ? settings.masters && settings.masters.classic : settings.masters && settings.masters.stream && settings.masters.stream[parsed.mix];
      } else {
        var device = settings.devices && settings.devices[parsed.apiChannel];
        node = parsed.mode === 'classic' ? device && device.classic : device && device.stream && device.stream[parsed.mix];
      }
      if (!node || typeof node.volume !== 'number') {
        throw new Error('missing Sonar state for ' + target);
      }
      return { volume: node.volume * 100, muted: !!node.muted };
    });
  }

  function setSonarVolume(target, value) {
    var parsed = parseSonarTarget(target);
    var scalar = Math.max(0, Math.min(1, Number(value) / 100));
    var vol = scalar.toFixed(2);
    if (parsed.mode === 'classic') {
      return sonarFetch('/VolumeSettings/classic/' + parsed.httpChannel + '/Volume/' + vol, 'PUT');
    }
    return sonarFetch('/VolumeSettings/streamer/' + parsed.mix + '/' + parsed.httpChannel + '/volume/' + vol, 'PUT');
  }

  function setSonarMute(target, muted) {
    var parsed = parseSonarTarget(target);
    var value = muted ? 'true' : 'false';
    if (parsed.mode === 'classic') {
      return sonarFetch('/VolumeSettings/classic/' + parsed.httpChannel + '/Mute/' + value, 'PUT');
    }
    return sonarFetch('/VolumeSettings/streamer/' + parsed.mix + '/' + parsed.httpChannel + '/isMuted/' + value, 'PUT');
  }

  function parseSonarTarget(target) {
    var parts = String(target || '').split(':');
    var mode = parts[0];
    var mix = '';
    var channel = '';
    if (mode === 'classic' && parts.length === 2) {
      channel = parts[1];
    } else if (mode === 'streamer' && parts.length === 3) {
      mix = parts[1];
      channel = parts[2];
    } else {
      throw new Error('invalid Sonar target: ' + target);
    }
    if (mode !== 'classic' && mode !== 'streamer') {
      throw new Error('invalid Sonar mode: ' + mode);
    }
    if (mode === 'streamer' && mix !== 'monitoring' && mix !== 'streaming') {
      throw new Error('invalid Sonar mix: ' + mix);
    }
    var apiChannel = sonarApiChannel(channel);
    return {
      mode: mode,
      mix: mix,
      channel: channel,
      apiChannel: apiChannel,
      httpChannel: channel === 'master' ? (mode === 'classic' ? 'Master' : 'master') : apiChannel
    };
  }

  function sonarApiChannel(channel) {
    var map = {
      master: 'master',
      game: 'game',
      chat: 'chatRender',
      media: 'media',
      aux: 'aux',
      mic: 'chatCapture'
    };
    if (!map[channel]) {
      throw new Error('invalid Sonar channel: ' + channel);
    }
    return map[channel];
  }

  function sonarFetch(route, method) {
    return getSonarUrl().then(function (baseUrl) {
      return fetch(baseUrl.replace(/\/+$/, '') + route, { method: method || 'GET' });
    }).then(function (response) {
      if (!response.ok) {
        throw new Error('Sonar HTTP ' + response.status);
      }
      if ((method || 'GET').toUpperCase() === 'PUT') {
        return response.text().then(function (text) {
          return text ? parseJson(text, {}) : {};
        });
      }
      return response.json();
    });
  }

  function getSonarUrl() {
    if (sonarDirect.url) {
      return Promise.resolve(sonarDirect.url);
    }
    if (sonarDirect.urlPromise) {
      return sonarDirect.urlPromise;
    }
    sonarDirect.urlPromise = fetch(SONAR_SUBAPPS_URL).then(function (response) {
      if (!response.ok) {
        throw new Error('subApps HTTP ' + response.status);
      }
      return response.json();
    }).then(function (payload) {
      var sonar = payload && payload.subApps && payload.subApps.sonar;
      if (!sonar || sonar.isEnabled === false || sonar.isReady === false || sonar.isRunning === false) {
        throw new Error('Sonar is not ready');
      }
      var url = sonar.metadata && sonar.metadata.webServerAddress;
      if (!isLoopbackHttpUrl(url)) {
        throw new Error('Sonar URL is not loopback');
      }
      sonarDirect.url = url;
      return url;
    }).catch(function (error) {
      sonarDirect.url = '';
      sonarDirect.urlPromise = null;
      throw error;
    });
    return sonarDirect.urlPromise;
  }

  function isLoopbackHttpUrl(value) {
    try {
      var url = new URL(value);
      return (url.protocol === 'http:' || url.protocol === 'https:') &&
        ['localhost', '127.0.0.1', '::1', '[::1]'].indexOf(url.hostname) !== -1;
    } catch (error) {
      return false;
    }
  }

  function connectHelper(settings) {
    settings = Object.assign({}, DEFAULT_SETTINGS, settings || {});
    if (helperSocket && (helperSocket.readyState === WebSocket.OPEN || helperSocket.readyState === WebSocket.CONNECTING)) {
      return;
    }
    clearTimeout(reconnectTimer);
    helperSocket = new WebSocket(settings.endpoint);

    helperSocket.onopen = function () {
      helperState.connected = true;
      helperState.lastError = '';
      reconnectDelay = 2000;
      refreshTitles();
      Object.keys(contexts).forEach(function (context) {
        var contextSettings = settingsFor(context);
        if (contextSettings.target) {
          sendTargetCommand({ command: 'subscribe', targetKind: contextSettings.targetKind, target: contextSettings.target, targetId: contextSettings.targetId, pollMs: clampPollMs(contextSettings.pollMs) });
        }
        if (contextSettings.displayMode === 'battery' || (contexts[context] && contexts[context].action) === 'local.streamdock.sonar.battery') {
          helperSend({ command: 'battery', target: contextSettings.batteryName || contextSettings.target, pollMs: clampPollMs(contextSettings.pollMs) });
        }
      });
    };

    helperSocket.onmessage = function (event) {
      var message = parseJson(event.data, {});
      if (message.event === 'state' && message.target) {
        helperState.lastUpdatedAt = Date.now();
        helperState.targets[message.target] = Object.assign({}, helperState.targets[message.target] || {}, message.payload || {});
        if (message.targetId) {
          helperState.targets[message.targetId] = helperState.targets[message.target];
        }
      }
      if (message.event === 'battery') {
        helperState.lastUpdatedAt = Date.now();
        var key = message.name || message.target || 'default';
        helperState.batteries[key] = { name: key, percent: message.percent, charging: message.charging };
        helperState.batteries.default = helperState.batteries[key];
      }
      if (message.event === 'unavailable' && message.target) {
        helperState.targets[message.target] = { available: false };
      }
      refreshTitles();
    };

    helperSocket.onclose = function () {
      helperState.connected = false;
      helperState.lastError = 'helper closed';
      logMessage('helper closed');
      refreshTitles();
      clearTimeout(reconnectTimer);
      var delay = reconnectDelay;
      reconnectDelay = Math.min(30000, reconnectDelay * 2);
      reconnectTimer = setTimeout(function () {
        connectHelper(settings);
      }, delay);
    };

    helperSocket.onerror = function () {
      helperState.connected = false;
      helperState.lastError = 'helper error';
      logMessage('helper error');
      refreshTitles();
    };
  }

  function needsHelper(settings, action) {
    if (action === 'local.streamdock.sonar.battery') return true;
    if (settings.displayMode === 'battery') return true;
    return String(settings.targetKind || '').toLowerCase() !== 'sonar';
  }

  function rememberContext(message) {
    contexts[message.context] = {
      action: message.action,
      settings: message.payload && message.payload.settings || {}
    };
    var settings = settingsFor(message.context);
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

  function applyPreset(context) {
    var settings = settingsFor(context);
    var targets = presetTargets(settings, 1);
    if (targets.length === 0 || (!settings.target && !settings.presetJson && !(settings.presetsJson && settings.presetName))) {
      refreshTitles();
      showAlert(context);
      return;
    }
    var tasks = targets.map(function (target) {
      if ((target.setVolume !== undefined && target.setVolume !== null) || target.mute !== undefined) {
        return applyPresetTarget(target, settings);
      }
      return sendTargetCommand({ command: 'toggle_mute', targetKind: target.targetKind, target: target.target, targetId: target.targetId || settings.targetId });
    });
    Promise.all(tasks).then(function (results) {
      if (results.every(Boolean)) {
        showOk(context);
      } else {
        showAlert(context);
      }
    }).catch(function () {
      showAlert(context);
    });
  }

  function toggleMute(context) {
    applyPreset(context);
  }

  function applyPresetTarget(target, settings) {
    var tasks = [];
    if (target.setVolume !== undefined && target.setVolume !== null && Number.isFinite(target.setVolume)) {
      tasks.push(sendTargetCommand({
        command: 'set_volume',
        targetKind: target.targetKind,
        target: target.target,
        targetId: target.targetId || settings.targetId,
        value: clampVolume(target.setVolume, settings)
      }));
    }
    if (target.mute !== undefined) {
      tasks.push(sendTargetCommand({
        command: 'set_mute',
        targetKind: target.targetKind,
        target: target.target,
        targetId: target.targetId || settings.targetId,
        value: target.mute === true || target.mute === 'true' ? 1 : 0
      }));
    }
    return Promise.all(tasks).then(function (results) {
      return results.every(Boolean);
    });
  }

  function adjustVolume(context, ticks) {
    var settings = settingsFor(context);
    if (settings.invertKnob === true || settings.invertKnob === 'true') {
      ticks = -ticks;
    }
    var targets = presetTargets(settings, ticks);
    if (targets.length === 0 || !ticks) {
      refreshTitles();
      if (targets.length === 0) {
        showAlert(context);
      }
      return;
    }
    var tasks = targets.map(function (target) {
      var current = helperState.targets[target.targetId || target.target] || helperState.targets[target.target] || {};
      var targetSetVolume = target.setVolume;
      if ((targetSetVolume === null || !Number.isFinite(targetSetVolume)) && typeof current.volume === 'number') {
        targetSetVolume = clampVolume(Number(current.volume) + Number(target.amount || 0), settings);
      }
      return sendTargetCommand({
        command: targetSetVolume !== null && Number.isFinite(targetSetVolume) ? 'set_volume' : 'volume_delta',
        targetKind: target.targetKind,
        target: target.target,
        targetId: target.targetId || settings.targetId,
        amount: target.amount,
        value: targetSetVolume
      });
    });
    Promise.all(tasks).then(function (results) {
      if (results.every(Boolean)) {
        showOk(context);
      } else {
        showAlert(context);
      }
    }).catch(function () {
      showAlert(context);
    });
  }

  function clampVolume(value, settings) {
    var min = Number(settings.minVolume);
    var max = Number(settings.maxVolume);
    if (!Number.isFinite(min)) min = 0;
    if (!Number.isFinite(max)) max = 100;
    if (max < min) {
      var tmp = max;
      max = min;
      min = tmp;
    }
    return Math.max(min, Math.min(max, Number(value) || 0));
  }

  function clampPollMs(value) {
    var ms = Number(value) || 1000;
    return Math.max(250, Math.min(60000, ms));
  }

  function clampBatteryWarn(value) {
    var pct = Number(value);
    if (!Number.isFinite(pct)) return 20;
    return Math.max(1, Math.min(100, Math.round(pct)));
  }

  function pollSonarTargets() {
    Object.keys(contexts).forEach(function (context) {
      var settings = settingsFor(context);
      if (String(settings.targetKind || '').toLowerCase() !== 'sonar' || !settings.target) {
        return;
      }
      var pollMs = clampPollMs(settings.pollMs);
      var key = settings.target;
      var lastPoll = sonarDirect.polls[key] || 0;
      if (Date.now() - lastPoll < pollMs) {
        return;
      }
      sonarDirect.polls[key] = Date.now();
      sendTargetCommand({ command: 'subscribe', targetKind: 'sonar', target: settings.target, pollMs: pollMs }).catch(function () {
        // sendTargetCommand already records direct failure and falls back where possible.
      });
    });
  }

  function selectPresetByDial(context, ticks) {
    var settings = settingsFor(context);
    var names = presetNames(settings);
    if (!names.length || !ticks) {
      showAlert(context);
      return;
    }
    var currentName = presetDialState[context] && presetDialState[context].name || settings.presetName || names[0];
    var index = names.indexOf(currentName);
    if (index === -1) {
      index = 0;
    }
    index = (index + (ticks > 0 ? 1 : -1) + names.length) % names.length;
    presetDialState[context] = presetDialState[context] || {};
    presetDialState[context].name = names[index];
    contexts[context].settings = Object.assign({}, contexts[context].settings || {}, { presetName: names[index] });
    setTitle(context, 'Preset\n' + names[index] + '\nready');

    clearTimeout(presetDialState[context].timer);
    if (settings.presetApplyMode === 'rotateEnd' || settings.presetApplyMode === 'both') {
      presetDialState[context].timer = setTimeout(function () {
        if (contexts[context]) {
          applyPreset(context);
        }
      }, Math.max(100, Number(settings.presetApplyDelayMs) || 700));
    }
  }

  function handleMessage(event) {
    var message = parseJson(event.data, {});
    if (message.event === 'willAppear' || message.event === 'didReceiveSettings') {
      rememberContext(message);
    } else if (message.event === 'willDisappear') {
      if (presetDialState[message.context] && presetDialState[message.context].timer) {
        clearTimeout(presetDialState[message.context].timer);
      }
      delete presetDialState[message.context];
      delete contexts[message.context];
    } else if (message.event === 'keyDown') {
      if (contexts[message.context] && contexts[message.context].action === 'local.streamdock.sonar.battery') {
        var batterySettings = settingsFor(message.context);
        helperSend({ command: 'battery', target: batterySettings.batteryName || batterySettings.target, pollMs: Number(batterySettings.pollMs) || 1000 });
      } else if (settingsFor(message.context).presetDialMode === 'select' && settingsFor(message.context).presetApplyMode === 'rotateEnd') {
        refreshTitles();
      } else {
        applyPreset(message.context);
      }
    } else if (message.event === 'dialRotate') {
      var ticks = Number(message.payload && (message.payload.ticks || message.payload.delta || message.payload.rotation)) || 0;
      if (settingsFor(message.context).presetDialMode === 'select' && presetNames(settingsFor(message.context)).length) {
        selectPresetByDial(message.context, ticks);
      } else {
        adjustVolume(message.context, ticks);
      }
    }
  }

  window.connectElgatoStreamDeckSocket = function (port, uuid, registerEvent) {
    pluginUuid = uuid;
    streamDockSocket = new WebSocket('ws://127.0.0.1:' + port);
    streamDockSocket.onopen = function () {
      sendToStreamDock({ event: registerEvent, uuid: pluginUuid });
    };
    streamDockSocket.onmessage = handleMessage;
  };

  setInterval(pollSonarTargets, 250);
}());
