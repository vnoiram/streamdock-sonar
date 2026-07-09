(function () {
  'use strict';

  var websocket = null;
  var context = null;
  var currentAction = '';
  var settings = {
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
  var helperSocket = null;
  var lastSnapshot = { deviceStates: [], sessionStates: [] };
  var helperTargetStatus = '';
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
  var COMMON_FIELDS = ['modeInfo'];
  var TARGET_FIELDS = ['targetKind', 'target', 'titleLabel', 'pollMs', 'generatedImages'];
  var SONAR_TARGET_FIELDS = ['target', 'titleLabel', 'pollMs', 'generatedImages'];
  var ACTION_FIELDS = {
    'local.streamdock.sonar.control': TARGET_FIELDS.concat(['volumeStep', 'minVolume', 'maxVolume', 'invertKnob', 'presetJson', 'presetsJson', 'presetName', 'presetDialMode', 'presetApplyMode', 'presetApplyDelayMs']),
    'local.streamdock.sonar.volume': SONAR_TARGET_FIELDS.concat(['volumeStep', 'minVolume', 'maxVolume', 'invertKnob']),
    'local.streamdock.sonar.mute': SONAR_TARGET_FIELDS,
    'local.streamdock.sonar.profile': ['titleLabel', 'presetsJson', 'presetName', 'presetApplyMode', 'presetApplyDelayMs', 'minVolume', 'maxVolume', 'generatedImages'],
    'local.streamdock.sonar.helper.volume': TARGET_FIELDS.concat(['endpoint', 'volumeStep', 'minVolume', 'maxVolume', 'invertKnob']),
    'local.streamdock.sonar.micmute': TARGET_FIELDS.concat(['endpoint']),
    'local.streamdock.sonar.battery': ['endpoint', 'batteryName', 'titleLabel', 'pollMs', 'generatedImages', 'batteryWarnPercent'],
    'local.streamdock.sonar.diagnostics': ['endpoint', 'targetKind', 'target', 'batteryName', 'refreshTargets', 'capturePreset', 'dryRunPreset', 'diagnoseSettings', 'resetSettings', 'copySettings', 'pasteSettings', 'exportSettings', 'copyDiagnostics', 'importSettings']
  };

  function byId(id) {
    return document.getElementById(id);
  }

  function rowFor(id) {
    var element = byId(id);
    while (element && element !== document.body) {
      if (element.classList && element.classList.contains('sdpi-item')) return element;
      element = element.parentNode;
    }
    return null;
  }

  function setFieldVisible(id, visible) {
    var row = rowFor(id);
    if (row) row.classList.toggle('is-hidden', !visible);
  }

  function applyVisibility() {
    var visible = {};
    COMMON_FIELDS.concat(ACTION_FIELDS[currentAction] || []).forEach(function (id) {
      visible[id] = true;
    });
    if (usesHelperEndpoint()) {
      visible.endpoint = true;
    }
    if (usesHelperTargetId()) {
      visible.targetId = true;
    }
    if (isDirectSonarAction() || settings.targetKind === 'sonar') {
      visible.capturePreset = false;
    }
    if (isDirectSonarAction()) {
      visible.targetKind = false;
      visible.targetId = false;
      visible.endpoint = false;
    }
    Object.keys(settings).concat(['modeInfo', 'refreshTargets', 'capturePreset', 'dryRunPreset', 'diagnoseSettings', 'resetSettings', 'copySettings', 'pasteSettings', 'exportSettings', 'copyDiagnostics', 'importSettings']).forEach(function (id) {
      setFieldVisible(id, !!visible[id]);
    });
    renderModeInfo();
  }

  function isDirectSonarAction() {
    return currentAction === 'local.streamdock.sonar.volume' ||
      currentAction === 'local.streamdock.sonar.mute' ||
      currentAction === 'local.streamdock.sonar.profile';
  }

  function usesHelperEndpoint() {
    return currentAction === 'local.streamdock.sonar.battery' ||
      currentAction === 'local.streamdock.sonar.diagnostics' ||
      currentAction === 'local.streamdock.sonar.helper.volume' ||
      settings.targetKind !== 'sonar';
  }

  function usesHelperTargetId() {
    return currentAction !== 'local.streamdock.sonar.battery' &&
      currentAction !== 'local.streamdock.sonar.diagnostics' &&
      currentAction !== 'local.streamdock.sonar.volume' &&
      currentAction !== 'local.streamdock.sonar.mute' &&
      currentAction !== 'local.streamdock.sonar.profile' &&
      settings.targetKind !== 'sonar';
  }

  function update() {
    if (!websocket || websocket.readyState !== WebSocket.OPEN || !context) {
      return;
    }
    settings.endpoint = byId('endpoint').value.trim();
    settings.targetKind = isDirectSonarAction() ? 'sonar' : byId('targetKind').value;
    settings.target = byId('target').value.trim();
    settings.targetId = selectedTargetId();
    settings.titleLabel = byId('titleLabel').value.trim();
    settings.volumeStep = Number(byId('volumeStep').value) || 2;
    settings.minVolume = Number(byId('minVolume').value) || 0;
    settings.maxVolume = Number(byId('maxVolume').value) || 100;
    settings.invertKnob = byId('invertKnob').checked;
    settings.pollMs = Number(byId('pollMs').value) || 1000;
    settings.presetJson = byId('presetJson').value.trim();
    settings.presetsJson = byId('presetsJson').value.trim();
    settings.presetName = byId('presetName').value.trim();
    settings.presetDialMode = byId('presetDialMode').value;
    settings.presetApplyMode = byId('presetApplyMode').value;
    settings.presetApplyDelayMs = Number(byId('presetApplyDelayMs').value) || 700;
    settings.displayMode = byId('displayMode').value;
    settings.batteryName = byId('batteryName').value.trim();
    settings.generatedImages = byId('generatedImages').checked;
    settings.batteryWarnPercent = Number(byId('batteryWarnPercent').value) || 20;
    websocket.send(JSON.stringify({ event: 'setSettings', context: context, payload: settings }));
    renderPresetNames();
    renderEndpointStatus();
    if (isDirectSonarAction() || settings.targetKind === 'sonar') {
      renderSonarTargets();
    }
    applyVisibility();
  }

  function setStatus(text) {
    byId('status').textContent = text;
    appendDiagnostics(text);
  }

  function renderEndpointStatus() {
    var status = byId('endpointStatus');
    if (!status) return;
    var endpoint = byId('endpoint').value.trim();
    if (!endpoint) {
      status.textContent = 'missing helper endpoint';
      return;
    }
    if (!/^wss?:\/\//i.test(endpoint)) {
      status.textContent = 'invalid WebSocket endpoint';
      return;
    }
    status.textContent = isLoopbackEndpoint(endpoint) ? 'localhost helper' : 'remote helper: expose only on trusted networks';
  }

  function renderModeInfo() {
    var info = byId('modeInfo');
    if (!info) return;
    if (isDirectSonarAction()) {
      info.textContent = 'Direct Sonar API: helper not required';
    } else if (currentAction === 'local.streamdock.sonar.battery' ||
      currentAction === 'local.streamdock.sonar.helper.volume' ||
      currentAction === 'local.streamdock.sonar.micmute') {
      info.textContent = helperTargetStatus ? 'Uses bundled Windows helper: ' + helperTargetStatus : 'Uses bundled Windows helper';
    } else if (currentAction === 'local.streamdock.sonar.diagnostics') {
      info.textContent = helperTargetStatus ? 'Diagnostics: ' + helperTargetStatus : 'Diagnostics: helper tools and logs';
    } else if (settings.targetKind === 'sonar') {
      info.textContent = 'Direct Sonar API with helper fallback';
    } else {
      info.textContent = 'Advanced helper-backed Windows target';
    }
  }

  function isLoopbackEndpoint(endpoint) {
    try {
      var url = new URL(endpoint);
      return ['localhost', '127.0.0.1', '::1', '[::1]'].indexOf(url.hostname) !== -1;
    } catch (error) {
      return false;
    }
  }

  function refreshTargets() {
    if (settings.targetKind === 'sonar') {
      renderSonarTargets();
      helperTargetStatus = '';
      renderModeInfo();
      setStatus('sonar targets loaded');
      return;
    }
    if (helperSocket && (helperSocket.readyState === WebSocket.OPEN || helperSocket.readyState === WebSocket.CONNECTING)) {
      return;
    }
    helperTargetStatus = 'connecting';
    renderModeInfo();
    setStatus('connecting');
    helperSocket = new WebSocket(settings.endpoint || 'ws://127.0.0.1:41922');
    helperSocket.onopen = function () {
      helperTargetStatus = 'requesting targets';
      renderModeInfo();
      setStatus('requesting');
      helperSocket.send(JSON.stringify({ command: 'list_targets' }));
    };
    helperSocket.onmessage = function (event) {
      var message = JSON.parse(event.data);
      if (message.event === 'targets') {
        lastSnapshot.deviceStates = message.deviceStates || [];
        lastSnapshot.sessionStates = message.sessionStates || [];
        renderTargets(message);
        helperTargetStatus = 'targets loaded';
        renderModeInfo();
        setStatus('targets loaded');
        helperSocket.close();
      }
    };
    helperSocket.onerror = function () {
      helperTargetStatus = 'helper offline';
      renderModeInfo();
      setStatus('helper offline');
    };
    helperSocket.onclose = function () {
      helperSocket = null;
    };
  }

  function renderTargets(message) {
    var list = byId('target');
    list.innerHTML = '';
    addOption(list, '', 'Select target');
    var values = settings.targetKind === 'session' ? message.sessionDetails || message.sessions || [] : message.deviceDetails || message.devices || [];
    values.forEach(function (value) {
      var name = typeof value === 'string' ? value : value.name;
      var id = typeof value === 'string' ? '' : value.id || '';
      addOption(list, name, id ? name + ' [' + id + ']' : name, id);
    });
    ensureSelectedOption(list, settings.target, settings.targetId);
    list.value = settings.target || '';
    byId('targetId').value = selectedTargetId();
    var batteryList = byId('batteryName');
    batteryList.innerHTML = '';
    addOption(batteryList, '', 'Auto-detect');
    if (message.batteries && message.batteries.length) {
      message.batteries.forEach(function (item) {
        var name = item.name || item.target || 'Headset';
        addOption(batteryList, name, name);
      });
    }
    ensureSelectedOption(batteryList, settings.batteryName, '');
    batteryList.value = settings.batteryName || '';
  }

  function renderSonarTargets() {
    var list = byId('target');
    list.innerHTML = '';
    addOption(list, '', 'Select Sonar channel');
    SONAR_TARGETS.forEach(function (item) {
      addOption(list, item.target, item.label);
    });
    ensureSelectedOption(list, settings.target, settings.targetId);
    list.value = settings.target || '';
    byId('targetId').value = '';
  }

  function addOption(list, value, text, targetId) {
    var option = document.createElement('option');
    option.value = value;
    option.textContent = text;
    if (targetId) option.dataset.targetId = targetId;
    list.appendChild(option);
  }

  function ensureSelectedOption(list, value, targetId) {
    if (!list || !value) return;
    var exists = Array.prototype.some.call(list.options, function (option) {
      return option.value === value;
    });
    if (!exists) {
      addOption(list, value, targetId ? value + ' [' + targetId + ']' : value, targetId);
    }
  }

  function selectedTargetId() {
    var list = byId('target');
    var option = list && list.options[list.selectedIndex];
    return option && option.dataset ? option.dataset.targetId || '' : byId('targetId').value.trim();
  }

  function applySettings(next) {
    settings = Object.assign({}, settings, next || {});
    applyActionDefaults();
    if (isDirectSonarAction() || settings.targetKind === 'sonar') {
      renderSonarTargets();
    } else {
      ensureSelectedOption(byId('target'), settings.target, settings.targetId);
    }
    ensureSelectedOption(byId('batteryName'), settings.batteryName, '');
    Object.keys(settings).forEach(function (key) {
      if (byId(key)) {
        if (byId(key).type === 'checkbox') {
          byId(key).checked = settings[key] === true || settings[key] === 'true';
        } else {
          byId(key).value = settings[key];
        }
      }
    });
    renderPresetNames();
    renderEndpointStatus();
    byId('targetId').value = settings.targetId || '';
    if (currentAction === 'local.streamdock.sonar.helper.volume' ||
      currentAction === 'local.streamdock.sonar.micmute' ||
      currentAction === 'local.streamdock.sonar.battery') {
      refreshTargets();
    }
    applyVisibility();
  }

  function applyActionDefaults() {
    if (isDirectSonarAction()) {
      settings.targetKind = 'sonar';
    }
    if (currentAction === 'local.streamdock.sonar.profile') {
      settings.presetDialMode = 'select';
      settings.presetApplyMode = settings.presetApplyMode || 'press';
    }
    if (currentAction === 'local.streamdock.sonar.helper.volume' && settings.targetKind === 'sonar') {
      settings.targetKind = 'device';
    }
  }

  function parsePresets() {
    if (!settings.presetsJson) {
      return {};
    }
    var parsed = JSON.parse(settings.presetsJson);
    if (Array.isArray(parsed)) {
      return parsed.reduce(function (map, item) {
        if (item && item.name) {
          map[item.name] = item.targets || [];
        }
        return map;
      }, {});
    }
    return parsed && typeof parsed === 'object' ? parsed : {};
  }

  function renderPresetNames() {
    var list = byId('presetNames');
    if (!list) return;
    list.innerHTML = '';
    try {
      Object.keys(parsePresets()).forEach(function (name) {
        var option = document.createElement('option');
        option.value = name;
        list.appendChild(option);
      });
      setStatus('ready');
    } catch (error) {
      setStatus('invalid presets JSON');
    }
  }

  function exportSettings() {
    update();
    var blob = new Blob([JSON.stringify(backupPayload(), null, 2)], { type: 'application/json' });
    var link = document.createElement('a');
    link.href = URL.createObjectURL(blob);
    link.download = 'streamdock-sonar-settings.json';
    link.click();
    URL.revokeObjectURL(link.href);
  }

  function importSettings(event) {
    var file = event.target.files && event.target.files[0];
    if (!file) return;
    file.text().then(function (text) {
      applySettings(settingsFromImport(JSON.parse(text)));
      update();
    });
  }

  function copySettings() {
    update();
    navigator.clipboard.writeText(JSON.stringify(backupPayload(), null, 2)).then(function () {
      setStatus('settings copied');
    }).catch(function () {
      setStatus('copy failed');
    });
  }

  function pasteSettings() {
    navigator.clipboard.readText().then(function (text) {
      applySettings(settingsFromImport(JSON.parse(text)));
      update();
      setStatus('settings pasted');
    }).catch(function () {
      setStatus('paste failed');
    });
  }

  function backupPayload() {
    return {
      type: 'streamdock-plugin-backup',
      plugin: 'streamdock-sonar',
      version: 1,
      exportedAt: new Date().toISOString(),
      settings: settings
    };
  }

  function settingsFromImport(imported) {
    if (imported && imported.type === 'streamdock-plugin-backup') {
      return imported.settings || {};
    }
    return imported || {};
  }

  function diagnosticsKey() {
    return 'streamdock-sonar:diagnostics';
  }

  function diagnosticsLog() {
    try {
      return JSON.parse(localStorage.getItem(diagnosticsKey()) || '[]');
    } catch (error) {
      return [];
    }
  }

  function appendDiagnostics(text) {
    try {
      var items = diagnosticsLog();
      items.unshift({ time: new Date().toISOString(), message: String(text || '') });
      localStorage.setItem(diagnosticsKey(), JSON.stringify(items.slice(0, 50)));
    } catch (error) {
      // localStorage can be disabled in some plugin runtimes.
    }
  }

  function copyDiagnostics() {
    navigator.clipboard.writeText(JSON.stringify(diagnosticsLog(), null, 2)).then(function () {
      setStatus('diagnostics copied');
    }).catch(function () {
      setStatus('diagnostics copy failed');
    });
  }

  function capturePreset() {
    if (settings.targetKind === 'sonar') {
      setStatus('sonar capture unavailable');
      return;
    }
    var states = settings.targetKind === 'session' ? lastSnapshot.sessionStates : lastSnapshot.deviceStates;
    if (!states || !states.length) {
      setStatus('refresh targets first');
      refreshTargets();
      return;
    }
    var name = byId('presetName').value.trim() || 'Captured';
    var presets;
    try {
      presets = settings.presetsJson ? JSON.parse(settings.presetsJson) : {};
      if (!presets || Array.isArray(presets) || typeof presets !== 'object') {
        presets = {};
      }
    } catch (error) {
      setStatus('invalid presets JSON');
      return;
    }
    presets[name] = states.map(function (item) {
      return {
        targetKind: settings.targetKind,
        target: item.name,
        targetId: item.id || '',
        setVolume: Math.round(Number(item.state && item.state.volumePercent) || 0),
        mute: !!(item.state && item.state.muted)
      };
    });
    settings.presetsJson = JSON.stringify(presets, null, 2);
    settings.presetName = name;
    applySettings(settings);
    update();
    setStatus('preset captured');
  }

  function dryRunPreset() {
    update();
    var targets = [];
    try {
      if (settings.presetsJson && settings.presetName) {
        var presets = parsePresets();
        targets = presets[settings.presetName] || [];
      } else if (settings.presetJson) {
        targets = JSON.parse(settings.presetJson);
      } else if (settings.target || settings.targetId) {
        targets = [{ target: settings.target, targetId: settings.targetId, targetKind: settings.targetKind }];
      }
      if (!Array.isArray(targets)) targets = [];
      setStatus(targets.length ? 'dry-run ' + targets.length + ' target(s)' : 'dry-run no targets');
    } catch (error) {
      setStatus('dry-run invalid preset');
    }
  }

  function diagnoseSettings() {
    update();
    var issues = [];
    if (usesHelperEndpoint() && !settings.endpoint) issues.push('missing endpoint');
    if (usesHelperEndpoint() && !/^wss?:\/\//i.test(settings.endpoint)) issues.push('invalid endpoint');
    if (usesHelperEndpoint() && !isLoopbackEndpoint(settings.endpoint)) issues.push('remote helper');
    if (settings.targetKind === 'sonar' && settings.target && !isKnownSonarTarget(settings.target)) issues.push('unknown sonar target');
    if (currentAction === 'local.streamdock.sonar.battery') {
      if (!settings.batteryName && !settings.target) issues.push('battery target unset');
    } else if (!settings.target && settings.displayMode !== 'battery' && !settings.presetsJson && !settings.presetJson) {
      issues.push('missing target');
    }
    if (Number(settings.maxVolume) < Number(settings.minVolume)) issues.push('volume range reversed');
    if (settings.displayMode === 'battery' && !settings.batteryName && !settings.target) issues.push('battery target unset');
    setStatus(issues.join(', ') || 'diagnostics ok');
  }

  function isKnownSonarTarget(target) {
    return SONAR_TARGETS.some(function (item) {
      return item.target === target;
    });
  }

  function resetSettings() {
    applySettings({
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
    });
    update();
    setStatus('settings reset');
  }

  window.connectElgatoStreamDeckSocket = function (port, uuid, registerEvent, info, actionInfo) {
    var parsedActionInfo = JSON.parse(actionInfo || '{}');
    context = parsedActionInfo.context || uuid;
    currentAction = parsedActionInfo.action || '';
    applyVisibility();
    websocket = new WebSocket('ws://127.0.0.1:' + port);
    websocket.onopen = function () {
      websocket.send(JSON.stringify({ event: registerEvent, uuid: uuid }));
      websocket.send(JSON.stringify({ event: 'getSettings', context: context }));
    };
    websocket.onmessage = function (event) {
      var message = JSON.parse(event.data);
      if (message.event === 'didReceiveSettings') {
        applySettings(message.payload && message.payload.settings);
      }
    };
  };

  window.addEventListener('DOMContentLoaded', function () {
    ['endpoint', 'targetKind', 'target', 'targetId', 'titleLabel', 'volumeStep', 'minVolume', 'maxVolume', 'displayMode', 'batteryName', 'batteryWarnPercent'].forEach(function (id) {
      byId(id).addEventListener('input', update);
      byId(id).addEventListener('change', update);
    });
    byId('targetKind').addEventListener('change', function () {
      settings.target = '';
      settings.targetId = '';
      byId('target').value = '';
      byId('targetId').value = '';
      update();
      refreshTargets();
    });
    ['invertKnob', 'generatedImages'].forEach(function (id) {
      byId(id).addEventListener('change', update);
    });
    ['pollMs', 'presetJson', 'presetsJson', 'presetName', 'presetDialMode', 'presetApplyMode', 'presetApplyDelayMs'].forEach(function (id) {
      byId(id).addEventListener('input', update);
      byId(id).addEventListener('change', update);
    });
    byId('refreshTargets').addEventListener('click', refreshTargets);
    byId('capturePreset').addEventListener('click', capturePreset);
    byId('dryRunPreset').addEventListener('click', dryRunPreset);
    byId('diagnoseSettings').addEventListener('click', diagnoseSettings);
    byId('resetSettings').addEventListener('click', resetSettings);
    byId('copySettings').addEventListener('click', copySettings);
    byId('pasteSettings').addEventListener('click', pasteSettings);
    byId('exportSettings').addEventListener('click', exportSettings);
    byId('copyDiagnostics').addEventListener('click', copyDiagnostics);
    byId('importSettings').addEventListener('change', importSettings);
    renderEndpointStatus();
    applyVisibility();
  });
}());
