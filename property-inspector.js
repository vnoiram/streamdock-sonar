(function () {
  'use strict';

  var websocket = null;
  var context = null;
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

  function byId(id) {
    return document.getElementById(id);
  }

  function update() {
    if (!websocket || websocket.readyState !== WebSocket.OPEN || !context) {
      return;
    }
    settings.endpoint = byId('endpoint').value.trim();
    settings.targetKind = byId('targetKind').value;
    settings.target = byId('target').value.trim();
    settings.targetId = byId('targetId').value.trim();
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

  function isLoopbackEndpoint(endpoint) {
    try {
      var url = new URL(endpoint);
      return ['localhost', '127.0.0.1', '::1', '[::1]'].indexOf(url.hostname) !== -1;
    } catch (error) {
      return false;
    }
  }

  function refreshTargets() {
    if (helperSocket && (helperSocket.readyState === WebSocket.OPEN || helperSocket.readyState === WebSocket.CONNECTING)) {
      return;
    }
    setStatus('connecting');
    helperSocket = new WebSocket(settings.endpoint || 'ws://127.0.0.1:41922');
    helperSocket.onopen = function () {
      setStatus('requesting');
      helperSocket.send(JSON.stringify({ command: 'list_targets' }));
    };
    helperSocket.onmessage = function (event) {
      var message = JSON.parse(event.data);
      if (message.event === 'targets') {
        lastSnapshot.deviceStates = message.deviceStates || [];
        lastSnapshot.sessionStates = message.sessionStates || [];
        renderTargets(message);
        setStatus('targets loaded');
        helperSocket.close();
      }
    };
    helperSocket.onerror = function () {
      setStatus('helper offline');
    };
    helperSocket.onclose = function () {
      helperSocket = null;
    };
  }

  function renderTargets(message) {
    var list = byId('targets');
    list.innerHTML = '';
    var values = settings.targetKind === 'session' ? message.sessionDetails || message.sessions || [] : message.deviceDetails || message.devices || [];
    values.forEach(function (value) {
      var option = document.createElement('option');
      option.value = typeof value === 'string' ? value : value.name;
      if (typeof value !== 'string' && value.id) {
        option.label = value.name + ' [' + value.id + ']';
      }
      list.appendChild(option);
    });
    if (message.batteries && message.batteries.length) {
      var batteryList = byId('batteries');
      batteryList.innerHTML = '';
      message.batteries.forEach(function (item) {
        var option = document.createElement('option');
        option.value = item.name || item.target || 'Headset';
        batteryList.appendChild(option);
      });
    }
  }

  function applySettings(next) {
    settings = Object.assign({}, settings, next || {});
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
    if (!settings.endpoint) issues.push('missing endpoint');
    if (!/^wss?:\/\//i.test(settings.endpoint)) issues.push('invalid endpoint');
    if (!isLoopbackEndpoint(settings.endpoint)) issues.push('remote helper');
    if (!settings.target && settings.displayMode !== 'battery' && !settings.presetsJson && !settings.presetJson) issues.push('missing target');
    if (Number(settings.maxVolume) < Number(settings.minVolume)) issues.push('volume range reversed');
    if (settings.displayMode === 'battery' && !settings.batteryName && !settings.target) issues.push('battery target unset');
    setStatus(issues.join(', ') || 'diagnostics ok');
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
  });
}());
