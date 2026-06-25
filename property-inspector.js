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
    batteryName: ''
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
    settings.volumeStep = Number(byId('volumeStep').value) || 2;
    settings.pollMs = Number(byId('pollMs').value) || 1000;
    settings.presetJson = byId('presetJson').value.trim();
    settings.presetsJson = byId('presetsJson').value.trim();
    settings.presetName = byId('presetName').value.trim();
    settings.presetDialMode = byId('presetDialMode').value;
    settings.presetApplyMode = byId('presetApplyMode').value;
    settings.presetApplyDelayMs = Number(byId('presetApplyDelayMs').value) || 700;
    settings.displayMode = byId('displayMode').value;
    settings.batteryName = byId('batteryName').value.trim();
    websocket.send(JSON.stringify({ event: 'setSettings', context: context, payload: settings }));
    renderPresetNames();
  }

  function setStatus(text) {
    byId('status').textContent = text;
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
        byId(key).value = settings[key];
      }
    });
    renderPresetNames();
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
    var blob = new Blob([JSON.stringify(settings, null, 2)], { type: 'application/json' });
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
      applySettings(JSON.parse(text));
      update();
    });
  }

  function copySettings() {
    update();
    navigator.clipboard.writeText(JSON.stringify(settings, null, 2)).then(function () {
      setStatus('settings copied');
    }).catch(function () {
      setStatus('copy failed');
    });
  }

  function pasteSettings() {
    navigator.clipboard.readText().then(function (text) {
      applySettings(JSON.parse(text));
      update();
      setStatus('settings pasted');
    }).catch(function () {
      setStatus('paste failed');
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
    ['endpoint', 'targetKind', 'target', 'targetId', 'volumeStep', 'displayMode', 'batteryName'].forEach(function (id) {
      byId(id).addEventListener('input', update);
      byId(id).addEventListener('change', update);
    });
    ['pollMs', 'presetJson', 'presetsJson', 'presetName', 'presetDialMode', 'presetApplyMode', 'presetApplyDelayMs'].forEach(function (id) {
      byId(id).addEventListener('input', update);
      byId(id).addEventListener('change', update);
    });
    byId('refreshTargets').addEventListener('click', refreshTargets);
    byId('capturePreset').addEventListener('click', capturePreset);
    byId('copySettings').addEventListener('click', copySettings);
    byId('pasteSettings').addEventListener('click', pasteSettings);
    byId('exportSettings').addEventListener('click', exportSettings);
    byId('importSettings').addEventListener('change', importSettings);
  });
}());
