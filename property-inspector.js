(function () {
  'use strict';

  var websocket = null;
  var context = null;
  var currentAction = '';
  var settings = {
    targetRole: 'game',
    streamMix: 'monitoring',
    overviewTargets: ['master', 'game', 'chatRender', 'media', 'aux', 'chatCapture'],
    chatMixMode: 'chat',
    chatMixStep: 10,
    deviceId: '',
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
    normalized.streamMix = normalizeStreamMix(normalized.streamMix || legacyTargetToStreamMix(normalized.target));
    normalized.overviewTargets = normalizeOverviewTargets(normalized.overviewTargets);
    normalized.chatMixMode = normalizeChatMixMode(normalized.chatMixMode);
    normalized.chatMixStep = Math.max(1, Math.min(100, Number(normalized.chatMixStep) || 10));
    normalized.deviceId = normalized.deviceId || '';
    normalized.step = Number(normalized.step || normalized.volumeStep || 2) || 2;
    normalized.titleLabel = normalized.titleLabel || '';
    normalized.invertKnob = normalized.invertKnob === true || normalized.invertKnob === 'true';
    return normalized;
  }

  function legacyTargetToStreamMix(target) {
    var parts = String(target || '').split(':').filter(Boolean);
    return parts[0] === 'streamer' ? parts[1] : '';
  }

  function normalizeStreamMix(streamMix) {
    return streamMix === 'streaming' ? 'streaming' : 'monitoring';
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

  function normalizeOverviewTargets(targets) {
    var all = ['master', 'game', 'chatRender', 'media', 'aux', 'chatCapture'];
    if (!Array.isArray(targets)) return all.slice();
    var selected = [];
    targets.forEach(function (target) {
      if (all.indexOf(target) !== -1 && selected.indexOf(target) === -1) selected.push(target);
    });
    return selected.length ? selected.slice(0, 6) : ['game'];
  }

  function normalizeChatMixMode(mode) {
    return mode === 'game' || mode === 'reset' ? mode : 'chat';
  }

  function isOverviewAction() {
    return currentAction === 'local.streamdock.sonar.overview';
  }

  function isChatMixAction() {
    return currentAction === 'local.streamdock.sonar.chatmix';
  }

  function isOutputDeviceAction() {
    return currentAction === 'local.streamdock.sonar.output-device';
  }

  function render() {
    byId('targetRole').value = settings.targetRole;
    byId('streamMix').value = settings.streamMix;
    byId('step').value = settings.step;
    byId('chatMixMode').value = settings.chatMixMode;
    byId('chatMixStep').value = settings.chatMixStep;
    byId('deviceId').value = settings.deviceId;
    byId('titleLabel').value = settings.titleLabel;
    byId('invertKnob').checked = !!settings.invertKnob;
    Array.prototype.forEach.call(document.querySelectorAll('input[name="overviewTarget"]'), function (input) {
      input.checked = settings.overviewTargets.indexOf(input.value) !== -1;
    });

    var isOverview = isOverviewAction();
    var isChatMix = isChatMixAction();
    var isOutputDevice = isOutputDeviceAction();
    Array.prototype.forEach.call(document.querySelectorAll('.single-target'), function (element) {
      element.classList.toggle('is-hidden', isOverview || isChatMix);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.overview-targets'), function (element) {
      element.classList.toggle('is-hidden', !isOverview);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.chatmix-settings'), function (element) {
      element.classList.toggle('is-hidden', !isChatMix);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.streammix-settings'), function (element) {
      element.classList.toggle('is-hidden', isChatMix);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.device-settings'), function (element) {
      element.classList.toggle('is-hidden', !isOutputDevice);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.volume-settings'), function (element) {
      element.classList.toggle('is-hidden', isChatMix || isOverview || isOutputDevice);
    });

    var isDiagnostics = currentAction === 'local.streamdock.sonar.diagnostics';
    Array.prototype.forEach.call(document.querySelectorAll('.diagnostics'), function (element) {
      element.classList.toggle('is-hidden', !isDiagnostics);
    });
  }

  function update() {
    if (!websocket || websocket.readyState !== WebSocket.OPEN || !context) return;
    settings.targetRole = byId('targetRole').value;
    settings.streamMix = byId('streamMix').value;
    settings.overviewTargets = selectedOverviewTargets();
    settings.chatMixMode = byId('chatMixMode').value;
    settings.chatMixStep = Math.max(1, Math.min(100, Number(byId('chatMixStep').value) || 10));
    settings.deviceId = byId('deviceId').value.trim();
    settings.step = Math.max(1, Math.min(20, Number(byId('step').value) || 2));
    settings.titleLabel = byId('titleLabel').value.trim();
    settings.invertKnob = byId('invertKnob').checked;
    websocket.send(JSON.stringify({ event: 'setSettings', context: context, payload: settings }));
  }

  function selectedOverviewTargets() {
    return normalizeOverviewTargets(Array.prototype.filter.call(
      document.querySelectorAll('input[name="overviewTarget"]'),
      function (input) { return input.checked; }
    ).map(function (input) { return input.value; }));
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
    ['targetRole', 'streamMix', 'step', 'titleLabel', 'invertKnob', 'chatMixMode', 'chatMixStep', 'deviceId'].forEach(function (id) {
      byId(id).addEventListener('change', update);
      byId(id).addEventListener('input', update);
    });
    Array.prototype.forEach.call(document.querySelectorAll('input[name="overviewTarget"]'), function (input) {
      input.addEventListener('change', update);
    });
    byId('refreshDiagnostics').addEventListener('click', requestDiagnostics);
    render();
  });

  window.connectElgatoStreamDeckSocket = connectElgatoStreamDeckSocket;
}());
