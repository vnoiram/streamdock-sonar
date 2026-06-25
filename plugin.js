(function () {
  'use strict';

  var DEFAULT_SETTINGS = {
    endpoint: 'ws://127.0.0.1:41922',
    targetKind: 'device',
    target: '',
    volumeStep: 2,
    pollMs: 1000,
    presetJson: ''
  };

  var streamDockSocket = null;
  var pluginUuid = null;
  var helperSocket = null;
  var reconnectTimer = null;
  var contexts = {};
  var helperState = { connected: false, targets: {}, lastError: '' };

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
    if (!settings.presetJson) {
      return [{ targetKind: settings.targetKind, target: settings.target, amount: fallbackTicks * (Number(settings.volumeStep) || 2) }];
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
          amount: Number(item.amount) || fallbackTicks * (Number(settings.volumeStep) || 2)
        };
      }).filter(function (item) { return item.target; });
    } catch (error) {
      return [];
    }
  }

  function targetState(settings) {
    return helperState.targets[settings.target] || {};
  }

  function titleFor(context) {
    var settings = settingsFor(context);
    if ((contexts[context] && contexts[context].action) === 'local.streamdock.sonar.diagnostics') {
      return 'Sonar\n' + (helperState.connected ? 'ok' : 'offline') + '\n' + (helperState.lastError || settings.endpoint);
    }
    if (!settings.target) {
      return 'Sonar\nunset';
    }
    if ((contexts[context] && contexts[context].action) === 'local.streamdock.sonar.micmute') {
      return 'Mic\n' + (targetState(settings).muted ? 'muted' : 'ready');
    }
    if (!helperState.connected) {
      return 'Sonar\noffline';
    }
    var current = targetState(settings);
    if (current.available === false) {
      return settings.target + '\nmissing';
    }
    if (current.muted) {
      return settings.target + '\nmuted';
    }
    if (typeof current.volume === 'number') {
      return settings.target + '\n' + Math.round(current.volume) + '%';
    }
    return settings.target;
  }

  function refreshTitles() {
    Object.keys(contexts).forEach(function (context) {
      setTitle(context, titleFor(context));
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
      refreshTitles();
      Object.keys(contexts).forEach(function (context) {
        var contextSettings = settingsFor(context);
        if (contextSettings.target) {
          helperSend({ command: 'subscribe', targetKind: contextSettings.targetKind, target: contextSettings.target, pollMs: Number(contextSettings.pollMs) || 1000 });
        }
      });
    };

    helperSocket.onmessage = function (event) {
      var message = parseJson(event.data, {});
      if (message.event === 'state' && message.target) {
        helperState.targets[message.target] = Object.assign({}, helperState.targets[message.target] || {}, message.payload || {});
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
      reconnectTimer = setTimeout(function () {
        connectHelper(settings);
      }, 2000);
    };

    helperSocket.onerror = function () {
      helperState.connected = false;
      helperState.lastError = 'helper error';
      logMessage('helper error');
      refreshTitles();
    };
  }

  function rememberContext(message) {
    contexts[message.context] = {
      action: message.action,
      settings: message.payload && message.payload.settings || {}
    };
    var settings = settingsFor(message.context);
    connectHelper(settings);
    if (settings.target) {
      helperSend({ command: 'subscribe', targetKind: settings.targetKind, target: settings.target, pollMs: Number(settings.pollMs) || 1000 });
    }
    setTitle(message.context, titleFor(message.context));
  }

  function toggleMute(context) {
    var settings = settingsFor(context);
    var targets = presetTargets(settings, 1);
    if (targets.length === 0 || (!settings.target && !settings.presetJson)) {
      refreshTitles();
      showAlert(context);
      return;
    }
    var ok = true;
    targets.forEach(function (target) {
      ok = helperSend({ command: 'toggle_mute', targetKind: target.targetKind, target: target.target }) && ok;
    });
    if (ok) {
      showOk(context);
    } else {
      showAlert(context);
    }
  }

  function adjustVolume(context, ticks) {
    var settings = settingsFor(context);
    var targets = presetTargets(settings, ticks);
    if (targets.length === 0 || !ticks) {
      refreshTitles();
      if (targets.length === 0) {
        showAlert(context);
      }
      return;
    }
    var ok = true;
    targets.forEach(function (target) {
      ok = helperSend({
        command: 'volume_delta',
        targetKind: target.targetKind,
        target: target.target,
        amount: target.amount
      }) && ok;
    });
    if (ok) {
      showOk(context);
    } else {
      showAlert(context);
    }
  }

  function handleMessage(event) {
    var message = parseJson(event.data, {});
    if (message.event === 'willAppear' || message.event === 'didReceiveSettings') {
      rememberContext(message);
    } else if (message.event === 'willDisappear') {
      delete contexts[message.context];
    } else if (message.event === 'keyDown') {
      toggleMute(message.context);
    } else if (message.event === 'dialRotate') {
      var ticks = Number(message.payload && (message.payload.ticks || message.payload.delta || message.payload.rotation)) || 0;
      adjustVolume(message.context, ticks);
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
}());
