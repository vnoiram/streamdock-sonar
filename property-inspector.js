(function () {
  'use strict';

  var websocket = null;
  var context = null;
  var currentAction = '';
  var settings = {
    targetRole: 'game',
    step: 2,
    titleLabel: '',
    invertKnob: false
  };

  function byId(id) {
    return document.getElementById(id);
  }

  function connectElgatoStreamDeckSocket(inPort, inPluginUUID, inRegisterEvent, inInfo, inActionInfo) {
    var actionInfo = parseJson(inActionInfo, {});
    context = actionInfo.context || inPluginUUID;
    currentAction = actionInfo.action || '';
    settings = Object.assign(settings, normalizeSettings(actionInfo.payload && actionInfo.payload.settings || {}));

    websocket = new WebSocket('ws://127.0.0.1:' + inPort);
    websocket.onopen = function () {
      websocket.send(JSON.stringify({ event: inRegisterEvent, uuid: inPluginUUID }));
      render();
      requestDiagnostics();
    };
    websocket.onmessage = function (event) {
      var message = parseJson(event.data, {});
      if (message.event === 'didReceiveSettings') {
        settings = Object.assign(settings, normalizeSettings(message.payload && message.payload.settings || {}));
        render();
      } else if (message.event === 'sendToPropertyInspector') {
        handlePluginMessage(message.payload || {});
      }
    };
  }

  function parseJson(value, fallback) {
    try {
      return typeof value === 'string' ? JSON.parse(value) : value;
    } catch {
      return fallback;
    }
  }

  function normalizeSettings(raw) {
    var normalized = Object.assign({}, raw);
    if (!normalized.targetRole && normalized.target) {
      normalized.targetRole = legacyTargetToRole(normalized.target);
    }
    normalized.targetRole = normalized.targetRole || 'game';
    normalized.step = Number(normalized.step || normalized.volumeStep || 2) || 2;
    normalized.titleLabel = normalized.titleLabel || '';
    normalized.invertKnob = normalized.invertKnob === true || normalized.invertKnob === 'true';
    return normalized;
  }

  function legacyTargetToRole(target) {
    var channel = String(target || '').split(':').filter(Boolean).pop();
    var map = {
      master: 'master',
      game: 'game',
      chat: 'chatRender',
      chatRender: 'chatRender',
      media: 'media',
      aux: 'aux',
      mic: 'chatCapture',
      chatCapture: 'chatCapture'
    };
    return map[channel] || 'game';
  }

  function render() {
    byId('targetRole').value = settings.targetRole;
    byId('step').value = settings.step;
    byId('titleLabel').value = settings.titleLabel;
    byId('invertKnob').checked = !!settings.invertKnob;

    var isDiagnostics = currentAction === 'local.streamdock.sonar.diagnostics';
    Array.prototype.forEach.call(document.querySelectorAll('.diagnostics'), function (element) {
      element.classList.toggle('is-hidden', !isDiagnostics);
    });
  }

  function update() {
    if (!websocket || websocket.readyState !== WebSocket.OPEN || !context) return;
    settings.targetRole = byId('targetRole').value;
    settings.step = Math.max(1, Math.min(20, Number(byId('step').value) || 2));
    settings.titleLabel = byId('titleLabel').value.trim();
    settings.invertKnob = byId('invertKnob').checked;
    websocket.send(JSON.stringify({ event: 'setSettings', context: context, payload: settings }));
  }

  function requestDiagnostics() {
    if (!websocket || websocket.readyState !== WebSocket.OPEN || !context) return;
    if (currentAction !== 'local.streamdock.sonar.diagnostics') return;
    websocket.send(JSON.stringify({
      event: 'sendToPlugin',
      action: currentAction,
      context: context,
      payload: { command: 'diagnostics' }
    }));
    byId('status').textContent = 'checking';
  }

  function handlePluginMessage(payload) {
    if (payload.type === 'diagnostics') {
      byId('status').textContent = payload.diagnostics && payload.diagnostics.mode ? 'ok' : 'error';
      byId('diagnosticsOutput').textContent = JSON.stringify(payload.diagnostics, null, 2);
    } else if (payload.type === 'error') {
      byId('status').textContent = payload.message || 'error';
    }
  }

  document.addEventListener('DOMContentLoaded', function () {
    ['targetRole', 'step', 'titleLabel', 'invertKnob'].forEach(function (id) {
      byId(id).addEventListener('change', update);
      byId(id).addEventListener('input', update);
    });
    byId('refreshDiagnostics').addEventListener('click', requestDiagnostics);
    render();
  });

  window.connectElgatoStreamDeckSocket = connectElgatoStreamDeckSocket;
}());
