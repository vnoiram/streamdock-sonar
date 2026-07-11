(function () {
  'use strict';

  var websocket = null;
  var actionContext = null;
  var propertyInspectorContext = null;
  var currentAction = '';
  var outputDevices = [];
  var profiles = [];
  var deviceRequestTimer = null;
  var profileRequestTimer = null;
  var settings = {
    sonarMode: 'normal',
    targetRole: 'game',
    streamMix: 'monitoring',
    overviewTargets: ['master', 'game', 'chatRender', 'media', 'aux', 'chatCapture'],
    chatMixMode: 'chat',
    chatMixStep: 10,
    deviceId: '',
    targetProfileId: '',
    rotationMode: 'target',
    streamerOutput: 'monitoring',
    rotateTicks: 3,
    step: 2,
    titleLabel: '',
    allowExcludedDevices: false,
    invert: false
  };

  function byId(id) {
    return document.getElementById(id);
  }

  function connectElgatoStreamDeckSocket(inPort, inPluginUUID, inRegisterEvent, inInfo, inActionInfo) {
    var actionInfo = parseJson(inActionInfo, {});
    actionContext = actionInfo.context || '';
    propertyInspectorContext = inPluginUUID;
    currentAction = actionInfo.action || '';
    settings = Object.assign(settings, normalizeSettings(actionInfo.payload && actionInfo.payload.settings || {}));

    websocket = new WebSocket('ws://127.0.0.1:' + inPort);
    websocket.onopen = function () {
      websocket.send(JSON.stringify({ event: inRegisterEvent, uuid: inPluginUUID }));
      render();
      requestDiagnostics();
      requestModeInfo();
      requestDevices();
      requestProfiles();
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
    normalized.targetProfileId = normalized.targetProfileId || '';
    normalized.rotationMode = normalizeRotationMode(normalized.rotationMode);
    normalized.sonarMode = normalizeSonarMode(normalized.sonarMode || sonarModeFromRotationMode(normalized.rotationMode));
    normalized.streamerOutput = normalizeStreamMix(normalized.streamerOutput || streamMixFromRotationMode(normalized.rotationMode) || normalized.streamMix);
    normalized.rotateTicks = Math.max(1, Math.min(20, Number(normalized.rotateTicks) || 3));
    normalized.step = normalizeStep(normalized.step || normalized.volumeStep || 2);
    normalized.titleLabel = normalized.titleLabel || '';
    normalized.allowExcludedDevices = normalized.allowExcludedDevices === true || normalized.allowExcludedDevices === 'true';
    normalized.invert = normalized.invert === true || normalized.invert === 'true' ||
      normalized.invertKnob === true || normalized.invertKnob === 'true';
    delete normalized.invertKnob;
    return normalized;
  }

  function legacyTargetToStreamMix(target) {
    var parts = String(target || '').split(':').filter(Boolean);
    return parts[0] === 'streamer' ? parts[1] : '';
  }

  function normalizeStreamMix(streamMix) {
    return streamMix === 'streaming' ? 'streaming' : 'monitoring';
  }

  function normalizeSonarMode(mode) {
    return mode === 'streamer' ? 'streamer' : 'normal';
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

  function normalizeRotationMode(mode) {
    return mode === 'all-auto-detect' || mode === 'all-classic' || mode === 'all-monitoring' || mode === 'all-streaming' ? mode : 'target';
  }

  function sonarModeFromRotationMode(rotationMode) {
    return rotationMode === 'all-monitoring' || rotationMode === 'all-streaming' ? 'streamer' : 'normal';
  }

  function streamMixFromRotationMode(rotationMode) {
    return rotationMode === 'all-streaming' ? 'streaming' : rotationMode === 'all-monitoring' ? 'monitoring' : '';
  }

  function rotationModeFromUi() {
    if (!isRotateOutputAction()) return normalizeRotationMode(byId('rotationMode').value);
    if (byId('sonarMode').value === 'streamer') {
      return byId('streamerOutput').value === 'streaming' ? 'all-streaming' : 'all-monitoring';
    }
    return 'target';
  }

  function normalizeOutputTargetRole(targetRole) {
    return ['game', 'chatRender', 'media', 'aux'].indexOf(targetRole) !== -1 ? targetRole : 'game';
  }

  function normalizeStep(value) {
    var step = Number(value) || 2;
    step = Math.max(-20, Math.min(20, step));
    return step === 0 ? 1 : step;
  }

  function isOverviewAction() {
    return currentAction === 'local.streamdock.sonar.overview';
  }

  function isVolumeAction() {
    return currentAction === 'local.streamdock.sonar.volume';
  }

  function isMuteAction() {
    return currentAction === 'local.streamdock.sonar.mute';
  }

  function isChatMixAction() {
    return currentAction === 'local.streamdock.sonar.chatmix';
  }

  function isChatMixDialAction() {
    return currentAction === 'local.streamdock.sonar.chatmix-dial';
  }

  function isOutputDeviceAction() {
    return currentAction === 'local.streamdock.sonar.output-device';
  }

  function isInputDeviceAction() {
    return currentAction === 'local.streamdock.sonar.input-device';
  }

  function isDeviceAction() {
    return isOutputDeviceAction() || isInputDeviceAction();
  }

  function isProfileAction() {
    return currentAction === 'local.streamdock.sonar.profile';
  }

  function isRotateOutputAction() {
    return currentAction === 'local.streamdock.sonar.rotate-output-device';
  }

  function isRotateInputAction() {
    return currentAction === 'local.streamdock.sonar.rotate-input-device';
  }

  function isModeAwareAction() {
    return isVolumeAction() || isMuteAction() || isOutputDeviceAction() || isRotateOutputAction() || isInputDeviceAction() || isRotateInputAction();
  }

  function usesStreamerOutputRoute() {
    return (isOutputDeviceAction() || isRotateOutputAction()) && settings.sonarMode === 'streamer';
  }

  function render() {
    byId('sonarMode').value = settings.sonarMode;
    if ((isRotateOutputAction() || isOutputDeviceAction()) && settings.sonarMode === 'normal') {
      settings.targetRole = normalizeOutputTargetRole(settings.targetRole);
    }
    byId('targetRole').value = settings.targetRole;
    byId('streamMix').value = settings.streamMix;
    byId('step').value = settings.step;
    byId('chatMixMode').value = settings.chatMixMode;
    byId('chatMixStep').value = settings.chatMixStep;
    byId('deviceId').value = settings.deviceId;
    byId('rotationMode').value = settings.rotationMode;
    byId('streamerOutput').value = settings.streamerOutput;
    byId('rotateTicks').value = settings.rotateTicks;
    renderDeviceOptions();
    renderProfileOptions();
    byId('titleLabel').value = settings.titleLabel;
    byId('allowExcludedDevices').checked = !!settings.allowExcludedDevices;
    byId('invertKnob').checked = !!settings.invert;
    Array.prototype.forEach.call(document.querySelectorAll('input[name="overviewTarget"]'), function (input) {
      input.checked = settings.overviewTargets.indexOf(input.value) !== -1;
    });

    var isOverview = isOverviewAction();
    var isChatMix = isChatMixAction();
    var isChatMixDial = isChatMixDialAction();
    var isOutputDevice = isOutputDeviceAction();
    var isInputDevice = isInputDeviceAction();
    var isProfile = isProfileAction();
    var isRotateOutput = isRotateOutputAction();
    var isStreamerOutputRoute = usesStreamerOutputRoute();
    var isRotateInput = isRotateInputAction();
    var isDiagnostics = currentAction === 'local.streamdock.sonar.diagnostics';
    Array.prototype.forEach.call(document.querySelectorAll('.sonar-mode-settings'), function (element) {
      element.classList.toggle('is-hidden', !isModeAwareAction());
    });
    Array.prototype.forEach.call(document.querySelectorAll('.single-target'), function (element) {
      element.classList.toggle('is-hidden', isOverview || isChatMix || isChatMixDial || isInputDevice || isRotateInput || isStreamerOutputRoute || isDiagnostics);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.overview-targets'), function (element) {
      element.classList.toggle('is-hidden', !isOverview);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.chatmix-settings'), function (element) {
      element.classList.toggle('is-hidden', !isChatMix && !isChatMixDial);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.streammix-settings'), function (element) {
      element.classList.toggle('is-hidden', isChatMix || isChatMixDial || isInputDevice || isRotateInput || isOutputDevice || isRotateOutput || isDiagnostics || !isModeAwareAction() || settings.sonarMode !== 'streamer');
    });
    Array.prototype.forEach.call(document.querySelectorAll('.rotation-settings'), function (element) {
      element.classList.toggle('is-hidden', true);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.streamer-output-settings'), function (element) {
      element.classList.toggle('is-hidden', !isStreamerOutputRoute);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.rotation-mode-status'), function (element) {
      element.classList.toggle('is-hidden', !isModeAwareAction());
    });
    Array.prototype.forEach.call(document.querySelectorAll('.rotation-common-settings'), function (element) {
      element.classList.toggle('is-hidden', !isRotateOutput && !isRotateInput);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.device-settings'), function (element) {
      element.classList.toggle('is-hidden', !isDeviceAction());
    });
    Array.prototype.forEach.call(document.querySelectorAll('.profile-settings'), function (element) {
      element.classList.toggle('is-hidden', !isProfile);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.volume-settings'), function (element) {
      element.classList.toggle('is-hidden', isChatMix || isOverview || isDeviceAction() || isProfile || isRotateOutput || isRotateInput || isDiagnostics);
    });
    byId('chatMixMode').closest('.sdpi-item').classList.toggle('is-hidden', !isChatMix);
    byId('invertKnob').closest('.sdpi-item').classList.toggle('is-hidden', isChatMix || isOverview || isDeviceAction() || isProfile || isRotateOutput || isRotateInput || isDiagnostics);
    byId('titleLabel').closest('.sdpi-item').classList.toggle('is-hidden', isChatMixDial);
    byId('step').closest('.sdpi-item').classList.toggle('is-hidden', isChatMixDial);
    Array.prototype.forEach.call(byId('targetRole').options, function (option) {
      option.hidden = (isRotateOutput || isOutputDevice) && settings.sonarMode === 'normal' && ['master', 'chatCapture'].indexOf(option.value) !== -1;
      option.disabled = option.hidden;
    });

    Array.prototype.forEach.call(document.querySelectorAll('.diagnostics'), function (element) {
      element.classList.toggle('is-hidden', !isDiagnostics);
    });
  }

  function update() {
    if (!websocket || websocket.readyState !== WebSocket.OPEN || !actionContext) return;
    settings.sonarMode = normalizeSonarMode(byId('sonarMode').value);
    settings.targetRole = (isRotateOutputAction() || isOutputDeviceAction()) && settings.sonarMode === 'normal'
      ? normalizeOutputTargetRole(byId('targetRole').value)
      : byId('targetRole').value;
    settings.streamMix = (isRotateOutputAction() || isOutputDeviceAction()) && settings.sonarMode === 'streamer'
      ? normalizeStreamMix(byId('streamerOutput').value)
      : byId('streamMix').value;
    settings.overviewTargets = selectedOverviewTargets();
    settings.chatMixMode = byId('chatMixMode').value;
    settings.chatMixStep = Math.max(1, Math.min(100, Number(byId('chatMixStep').value) || 10));
    settings.deviceId = byId('deviceId').value.trim();
    settings.targetProfileId = byId('targetProfileId').value;
    settings.streamerOutput = normalizeStreamMix(byId('streamerOutput').value);
    settings.rotationMode = rotationModeFromUi();
    settings.rotateTicks = Math.max(1, Math.min(20, Number(byId('rotateTicks').value) || 3));
    settings.step = normalizeStep(byId('step').value);
    settings.titleLabel = byId('titleLabel').value.trim();
    settings.allowExcludedDevices = byId('allowExcludedDevices').checked;
    settings.invert = byId('invertKnob').checked;
    delete settings.invertKnob;
    websocket.send(JSON.stringify({ event: 'setSettings', context: actionContext, payload: settings }));
    if (isModeAwareAction()) requestModeInfo();
    if (isProfileAction()) requestProfiles();
  }

  function updateFromDeviceSelect() {
    var selected = byId('deviceSelect').value;
    if (selected) byId('deviceId').value = selected;
    update();
  }

  function selectedOverviewTargets() {
    return normalizeOverviewTargets(Array.prototype.filter.call(
      document.querySelectorAll('input[name="overviewTarget"]'),
      function (input) { return input.checked; }
    ).map(function (input) { return input.value; }));
  }

  function requestDiagnostics() {
    if (!websocket || websocket.readyState !== WebSocket.OPEN || !propertyInspectorContext) return;
    if (currentAction !== 'local.streamdock.sonar.diagnostics') return;
    websocket.send(JSON.stringify({
      event: 'sendToPlugin',
      action: currentAction,
      context: propertyInspectorContext,
      payload: { command: 'diagnostics', replyContext: actionContext }
    }));
    byId('status').textContent = 'checking';
  }

  function requestModeInfo() {
    if (!websocket || websocket.readyState !== WebSocket.OPEN || !propertyInspectorContext) return;
    if (!isModeAwareAction()) return;
    websocket.send(JSON.stringify({
      event: 'sendToPlugin',
      action: currentAction,
      context: propertyInspectorContext,
      payload: {
        command: 'diagnostics',
        targetRole: settings.targetRole,
        streamMix: settings.streamMix,
        replyContext: actionContext
      }
    }));
    byId('modeStatus').textContent = 'checking';
    byId('routeStatus').textContent = 'checking';
  }

  function requestDevices() {
    if (!websocket || websocket.readyState !== WebSocket.OPEN || !propertyInspectorContext) return;
    if (!isDeviceAction()) return;
    websocket.send(JSON.stringify({
      event: 'sendToPlugin',
      action: currentAction,
      context: propertyInspectorContext,
      payload: { command: 'devices', dataFlow: isInputDeviceAction() ? 'capture' : 'render', replyContext: actionContext }
    }));
    byId('deviceStatus').textContent = 'loading';
    if (deviceRequestTimer) clearTimeout(deviceRequestTimer);
    deviceRequestTimer = setTimeout(function () {
      if (byId('deviceStatus').textContent === 'loading') {
        byId('deviceStatus').textContent = 'no response';
      }
    }, 10000);
  }

  function requestProfiles() {
    if (!websocket || websocket.readyState !== WebSocket.OPEN || !propertyInspectorContext) return;
    if (!isProfileAction()) return;
    websocket.send(JSON.stringify({
      event: 'sendToPlugin',
      action: currentAction,
      context: propertyInspectorContext,
      payload: { command: 'profiles', targetRole: byId('targetRole').value || settings.targetRole, replyContext: actionContext }
    }));
    byId('profileStatus').textContent = 'loading';
    if (profileRequestTimer) clearTimeout(profileRequestTimer);
    profileRequestTimer = setTimeout(function () {
      if (byId('profileStatus').textContent === 'loading') {
        byId('profileStatus').textContent = 'no response';
      }
    }, 10000);
  }

  function handlePluginMessage(payload) {
    if (payload.type === 'diagnostics') {
      byId('status').textContent = payload.diagnostics && payload.diagnostics.mode ? 'ok' : 'error';
      byId('diagnosticsOutput').textContent = JSON.stringify(payload.diagnostics, null, 2);
      renderModeInfo(payload.diagnostics || {});
    } else if (payload.type === 'devices') {
      if (deviceRequestTimer) clearTimeout(deviceRequestTimer);
      outputDevices = Array.isArray(payload.devices) ? payload.devices : [];
      byId('deviceStatus').textContent = outputDevices.length ? outputDevices.length + ' devices' : 'none';
      renderDeviceOptions();
    } else if (payload.type === 'profiles') {
      if (profileRequestTimer) clearTimeout(profileRequestTimer);
      profiles = Array.isArray(payload.profiles) ? payload.profiles : [];
      if (!settings.targetProfileId && payload.selectedProfileId) settings.targetProfileId = payload.selectedProfileId;
      byId('profileStatus').textContent = profiles.length ? profiles.length + ' profiles' : 'none';
      renderProfileOptions();
    } else if (payload.type === 'error') {
      byId('status').textContent = payload.message || 'error';
      if (isDeviceAction() || payload.source === 'devices') {
        if (deviceRequestTimer) clearTimeout(deviceRequestTimer);
        byId('deviceStatus').textContent = payload.message || 'error';
      }
      if (isProfileAction() || payload.source === 'profiles') {
        if (profileRequestTimer) clearTimeout(profileRequestTimer);
        byId('profileStatus').textContent = payload.message || 'error';
      }
    }
  }

  function renderModeInfo(diagnostics) {
    if (!isModeAwareAction()) return;
    var mode = diagnostics.mode || '';
    byId('modeStatus').textContent = mode === 'classic' ? 'Normal' : mode === 'stream' ? 'Streamer' : 'unknown';
    if (mode === 'classic') {
      if (settings.sonarMode === 'streamer') {
        byId('routeStatus').textContent = 'GG is Normal; switch GG to Streamer';
      } else if (isInputDeviceAction() || isRotateInputAction()) {
        byId('routeStatus').textContent = 'Normal: microphone';
      } else {
        byId('routeStatus').textContent = 'Normal: ' + settings.targetRole;
      }
    } else if (mode === 'stream') {
      if (settings.sonarMode !== 'streamer') {
        byId('routeStatus').textContent = 'GG is Streamer; switch UI to Streamer';
      } else if (isOutputDeviceAction() || isRotateOutputAction()) {
        byId('routeStatus').textContent = 'Streamer: all ' + settings.streamerOutput;
      } else if (isInputDeviceAction() || isRotateInputAction()) {
        byId('routeStatus').textContent = 'Streamer: microphone';
      } else {
        byId('routeStatus').textContent = 'Streamer: ' + settings.targetRole + ' / ' + settings.streamMix;
      }
    } else {
      byId('routeStatus').textContent = 'unknown';
    }
  }

  function renderDeviceOptions() {
    var select = byId('deviceSelect');
    if (!select) return;
    var current = byId('deviceId').value || settings.deviceId;
    select.innerHTML = '';
    var custom = document.createElement('option');
    custom.value = '';
    custom.textContent = outputDevices.length ? 'Manual deviceId' : 'No devices loaded';
    select.appendChild(custom);
    outputDevices.forEach(function (device) {
      var option = document.createElement('option');
      option.value = device.id || '';
      option.textContent = device.name || device.id || 'Unknown device';
      select.appendChild(option);
    });
    select.value = outputDevices.some(function (device) { return device.id === current; }) ? current : '';
  }

  function renderProfileOptions() {
    var select = byId('targetProfileId');
    if (!select) return;
    var current = settings.targetProfileId;
    select.innerHTML = '';
    var empty = document.createElement('option');
    empty.value = '';
    empty.textContent = profiles.length ? 'Select profile' : 'No profiles loaded';
    select.appendChild(empty);
    profiles.forEach(function (profile) {
      var option = document.createElement('option');
      option.value = profile.id || '';
      option.textContent = profile.name || profile.id || 'Unknown profile';
      select.appendChild(option);
    });
    select.value = profiles.some(function (profile) { return profile.id === current; }) ? current : '';
  }

  document.addEventListener('DOMContentLoaded', function () {
    ['sonarMode', 'targetRole', 'streamMix', 'step', 'titleLabel', 'invertKnob', 'chatMixMode', 'chatMixStep', 'deviceId', 'targetProfileId', 'rotationMode', 'streamerOutput', 'rotateTicks', 'allowExcludedDevices'].forEach(function (id) {
      byId(id).addEventListener('change', update);
      byId(id).addEventListener('input', update);
    });
    byId('deviceSelect').addEventListener('change', updateFromDeviceSelect);
    Array.prototype.forEach.call(document.querySelectorAll('input[name="overviewTarget"]'), function (input) {
      input.addEventListener('change', update);
    });
    byId('refreshDiagnostics').addEventListener('click', requestDiagnostics);
    byId('refreshDevices').addEventListener('click', requestDevices);
    byId('refreshProfiles').addEventListener('click', requestProfiles);
    render();
  });

  window.connectElgatoStreamDeckSocket = connectElgatoStreamDeckSocket;
}());
