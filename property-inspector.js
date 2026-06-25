(function () {
  'use strict';

  var websocket = null;
  var context = null;
  var settings = {
    endpoint: 'ws://127.0.0.1:41922',
    targetKind: 'device',
    target: '',
    volumeStep: 2,
    pollMs: 1000,
    presetJson: ''
  };
  var helperSocket = null;

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
    settings.volumeStep = Number(byId('volumeStep').value) || 2;
    settings.pollMs = Number(byId('pollMs').value) || 1000;
    settings.presetJson = byId('presetJson').value.trim();
    websocket.send(JSON.stringify({ event: 'setSettings', context: context, payload: settings }));
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
    var values = settings.targetKind === 'session' ? message.sessions || [] : message.devices || [];
    values.forEach(function (value) {
      var option = document.createElement('option');
      option.value = value;
      list.appendChild(option);
    });
  }

  function applySettings(next) {
    settings = Object.assign({}, settings, next || {});
    Object.keys(settings).forEach(function (key) {
      if (byId(key)) {
        byId(key).value = settings[key];
      }
    });
  }

  function exportSettings() {
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
    ['endpoint', 'targetKind', 'target', 'volumeStep'].forEach(function (id) {
      byId(id).addEventListener('input', update);
      byId(id).addEventListener('change', update);
    });
    ['pollMs', 'presetJson'].forEach(function (id) {
      byId(id).addEventListener('input', update);
      byId(id).addEventListener('change', update);
    });
    byId('refreshTargets').addEventListener('click', refreshTargets);
    byId('exportSettings').addEventListener('click', exportSettings);
    byId('importSettings').addEventListener('change', importSettings);
  });
}());
