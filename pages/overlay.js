// Shared connection code for KbmOverlay pages.
//
//   Overlay.connect(['key', 'button', 'wheel', 'move'])
//       .on('key', function (name, down) { ... })
//       .on('button', function (name, down) { ... })
//       .on('wheel', function (dx, dy) { ... })      // dy > 0: wheel up
//       .on('move', function (dx, dy) { ... })       // summed raw mouse counts
//       .on('status', function (connected) { ... });
//
// Reconnects by itself when KbmOverlay restarts, so OBS never needs a refresh.
// After a reconnect (or a page reload while keys are held) the pressed state is resent,
// and everything is released while disconnected, so nothing stays lit.
// Written for older OBS versions too: no ?. ?? ||= syntax.
(function () {
    'use strict';

    var params = new URLSearchParams(location.search);

    // ?bg=1 (or ?bg=%23336699) gives the page a background, for checking it in a normal browser.
    if (params.has('bg')) {
        var bg = params.get('bg');
        document.documentElement.style.background = bg && bg !== '1' ? bg : '#202124';
    }

    // ?pressed=2DA026&released=96CF13&glow=23ADFF&line=FFFFFF (hex without #) win over the colors in
    // theme.css, so each OBS source can have its own colors. The settings page builds these.
    var COLOR_PARAMS = { pressed: '--pressed', released: '--released', glow: '--glow', line: '--line' };
    Object.keys(COLOR_PARAMS).forEach(function (name) {
        var value = params.get(name);
        if (value && /^[0-9A-Fa-f]{6}$/.test(value)) document.documentElement.style.setProperty(COLOR_PARAMS[name], '#' + value);
    });

    function connect(want) {
        var handlers = {};
        var keys = new Set();
        var buttons = new Set();
        var retryDelay = 500;
        var connected = false;
        var socket = null;

        function emit(type) {
            var list = handlers[type];
            if (!list) return;
            var args = Array.prototype.slice.call(arguments, 1);
            for (var i = 0; i < list.length; i++) list[i].apply(null, args);
        }

        function set(pressed, type, name, down) {
            if (down === pressed.has(name)) return;
            if (down) pressed.add(name); else pressed.delete(name);
            emit(type, name, down);
        }

        function sync(pressed, type, names) {
            var next = new Set(names);
            Array.from(pressed).forEach(function (name) {
                if (!next.has(name)) set(pressed, type, name, false);
            });
            next.forEach(function (name) { set(pressed, type, name, true); });
        }

        function open() {
            var url = 'ws://' + location.host + '/ws' + (want ? '?want=' + want.join(',') : '');
            var ws = new WebSocket(url);
            socket = ws;
            ws.onopen = function () {
                retryDelay = 500;
                connected = true;
                emit('status', true);
            };
            ws.onmessage = function (event) {
                var m = JSON.parse(event.data);
                switch (m.type) {
                    case 'key': set(keys, 'key', m.name, m.down); break;
                    case 'button': set(buttons, 'button', m.name, m.down); break;
                    case 'wheel': emit('wheel', m.dx, m.dy); break;
                    case 'move': emit('move', m.dx, m.dy); break;
                    case 'state':
                        sync(keys, 'key', m.keys);
                        sync(buttons, 'button', m.buttons);
                        break;
                    case 'relay': emit('relay', m); break;           // two-PC status (settings page)
                    case 'relayError': emit('relayError', m.message); break;
                }
            };
            ws.onclose = function () {
                if (connected) {
                    connected = false;
                    emit('status', false);
                }
                sync(keys, 'key', []);
                sync(buttons, 'button', []);
                setTimeout(open, retryDelay);
                retryDelay = Math.min(retryDelay * 2, 3000);
            };
        }

        // Let the page attach its handlers first.
        setTimeout(open, 0);

        return {
            on: function (type, fn) {
                (handlers[type] = handlers[type] || []).push(fn);
                return this;
            },
            // Sends a command to KbmOverlay (used by the settings page).
            send: function (command) {
                if (socket && socket.readyState === 1) socket.send(JSON.stringify(command));
            }
        };
    }

    // Sets the page size and tells the settings page (when shown there as a preview).
    function reportSize(width, height) {
        width = Math.ceil(width);
        height = Math.ceil(height);
        document.body.style.width = width + 'px';
        document.body.style.height = height + 'px';
        if (window.parent !== window) {
            window.parent.postMessage({ kbmOverlaySize: [width, height] }, location.origin);
        }
    }

    function showError(text) {
        var div = document.createElement('div');
        div.className = 'overlay-error';
        div.textContent = text;
        document.body.appendChild(div);
        reportSize(Math.max(320, div.offsetWidth), div.offsetHeight);
    }

    // ---- translations ----
    // Same files as the console (pages/i18n/<code>.json). The language is ?lang=, else the one
    // KbmOverlay uses (info.json), else English. Missing keys fall back to English.
    var i18nPromise = null;

    function loadJson(url) {
        return fetch(url)
            .then(function (response) { return response.ok ? response.json() : {}; })
            .catch(function () { return {}; });
    }

    function escapeHtml(value) {
        return String(value).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function i18n() {
        if (i18nPromise) return i18nPromise;
        i18nPromise = loadJson('info.json').then(function (info) {
            var lang = (params.get('lang') || info.lang || 'en').replace(/[^A-Za-z0-9-]/g, '');
            return Promise.all([loadJson('i18n/en.json'), lang === 'en' ? null : loadJson('i18n/' + lang + '.json')])
                .then(function (files) {
                    var english = files[0];
                    var current = files[1] || english;
                    function text(key) {
                        if (current[key] != null) return current[key];
                        return english[key] != null ? english[key] : key;
                    }
                    function fill(template, vars, escape) {
                        return template.replace(/\{(\w+)\}/g, function (match, name) {
                            if (!vars || !(name in vars)) return match;
                            return escape ? escapeHtml(vars[name]) : String(vars[name]);
                        });
                    }
                    // t(key, vars) gives plain text; t.html(key, vars) gives markup with vars escaped.
                    var t = function (key, vars) { return fill(text(key), vars, false); };
                    t.html = function (key, vars) { return fill(text(key), vars, true); };
                    t.lang = lang;
                    t.info = info;
                    return t;
                });
        });
        return i18nPromise;
    }

    // Fills elements marked with data-i18n (text), data-i18n-html (markup from the translation
    // file), data-i18n-placeholder, data-i18n-title and data-i18n-aria.
    function translate(t, root) {
        function each(attr, apply) {
            Array.prototype.forEach.call(root.querySelectorAll('[' + attr + ']'), function (el) {
                apply(el, el.getAttribute(attr));
            });
        }
        each('data-i18n', function (el, key) { el.textContent = t(key); });
        each('data-i18n-html', function (el, key) { el.innerHTML = t.html(key); });
        each('data-i18n-placeholder', function (el, key) { el.placeholder = t(key); });
        each('data-i18n-title', function (el, key) { el.title = t(key); });
        each('data-i18n-aria', function (el, key) { el.setAttribute('aria-label', t(key)); });
        document.documentElement.lang = t.lang;
    }

    function number(name, fallback, min, max) {
        var value = parseFloat(params.get(name));
        if (!isFinite(value)) return fallback;
        return Math.min(max, Math.max(min, value));
    }

    window.Overlay = {
        params: params,
        number: number,
        connect: connect,
        reportSize: reportSize,
        showError: showError,
        i18n: i18n,
        translate: translate
    };
})();
