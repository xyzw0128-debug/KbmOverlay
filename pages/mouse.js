// Draws the mouse for mouse.html and combo.html.
//
//   Overlay.mouse(holder).then(function (mouse) {
//       // mouse.width, mouse.height: px of holder (0 while the page is not shown)
//       connection.on('button', mouse.button).on('wheel', mouse.wheel);
//   }, function (error) { Overlay.showError(error.message); });
//
// The page's CSS sizes the picture (holder svg { width: 200px; height: auto }), so custom.css can change it.
// holder must be in the page: the buttons are measured for the press effects. mouse.svg (from
// Zergatul.Obs.InputOverlay) has elements with ids Mouse1..Mouse5, MouseScrollUp and MouseScrollDown;
// theme.css colors them.
(function () {
    'use strict';

    var SVG_NS = 'http://www.w3.org/2000/svg';

    // Glow filter from Zergatul.Obs.InputOverlay's common.js, as wide as --glow-size (theme.css / ?glowsize=).
    function addGlowFilter(svg) {
        var size = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--glow-size'));
        var blur = 10 * (isFinite(size) ? Math.max(0, size) : 1);
        var defs = document.createElementNS(SVG_NS, 'defs');
        defs.innerHTML =
            '<filter id="glow" filterUnits="userSpaceOnUse" x="-50%" y="-50%" width="200%" height="200%">' +
            '<feGaussianBlur in="SourceGraphic" stdDeviation="' + blur + '" result="blur20"/>' +
            '<feColorMatrix in="blur20" type="matrix" values="1 0 0 2 0 0 1 0 2 0 0 0 1 2 0 0 0 0 1 0" result="blur-colored"/>' +
            '<feMerge><feMergeNode in="blur-colored"/><feMergeNode in="SourceGraphic"/></feMerge>' +
            '</filter>';
        svg.appendChild(defs);
    }

    // Starts the animation of cls again and ends it by a timer too (see Overlay.duration).
    function restart(el, cls, time) {
        el.classList.remove('up', 'down', 'released');
        void el.getBoundingClientRect(); // restart the animation
        el.classList.add(cls);
        clearTimeout(el.endTimer);
        el.endTimer = setTimeout(function () { el.classList.remove(cls); }, time);
    }

    function build(holder, svgText) {
        holder.innerHTML = svgText.replace(/stroke:#ffffff/gi, 'stroke:var(--line)'); // outline follows theme.css / ?line=
        var svg = holder.querySelector('svg');
        svg.removeAttribute('width');
        svg.removeAttribute('height');
        addGlowFilter(svg);

        var buttons = {};
        ['Mouse1', 'Mouse2', 'Mouse3', 'Mouse4', 'Mouse5'].forEach(function (id) {
            var el = svg.getElementById(id);
            if (!el) return;
            el.style.removeProperty('fill'); // the svg says fill:none inline; theme.css decides
            el.style.removeProperty('opacity'); // the wheel says opacity:1 inline, which kept it shown with ?idle=0
            el.classList.add('mouse-button');
            var box = el.getBoundingClientRect(); // px, for the press effects in theme.css
            el.style.setProperty('--width', box.width);
            el.style.setProperty('--height', box.height);
            el.style.setProperty('--size', Math.max(box.width, box.height));
            buttons[id] = el;
        });
        var scrollUp = svg.getElementById('MouseScrollUp');
        var scrollDown = svg.getElementById('MouseScrollDown');
        scrollUp.classList.add('scroll');
        scrollDown.classList.add('scroll');
        // The rest of the outline is .mouse-part, which ?idle= (theme.css --idle-opacity) can fade out.
        Array.prototype.forEach.call(svg.querySelectorAll('path, rect'), function (part) {
            if (part.classList.contains('mouse-button') || scrollUp.contains(part) || scrollDown.contains(part)) return;
            part.style.removeProperty('opacity'); // some parts say opacity:1 inline
            part.classList.add('mouse-part');
        });

        var releaseTime = Overlay.duration('--mouse-release-time') + 100;
        var scrollTime = Overlay.duration('--scroll-time') + 100;
        var rippleTime = Overlay.duration('--ripple-time') + 100;

        // ?press=ripple: a copy of the button's outline spreads out from it (.mouse-ripple in theme.css).
        function ripple(el) {
            var ring = el.cloneNode(false);
            ring.removeAttribute('id');
            ring.removeAttribute('style'); // the svg's own stroke and fill; theme.css decides
            ring.setAttribute('class', 'mouse-ripple');
            ['--width', '--height'].forEach(function (name) { ring.style.setProperty(name, el.style.getPropertyValue(name)); });
            el.parentNode.appendChild(ring);
            setTimeout(function () { ring.parentNode.removeChild(ring); }, rippleTime);
        }

        return {
            width: holder.offsetWidth,
            height: holder.offsetHeight,
            button: function (name, down) {
                var el = buttons[name];
                if (!el) return;
                if (down) {
                    el.classList.remove('released');
                    el.classList.add('pressed');
                    if (Overlay.press === 'ripple') ripple(el);
                } else {
                    el.classList.remove('pressed');
                    restart(el, 'released', releaseTime);
                }
            },
            wheel: function (dx, dy) {
                if (dy > 0) restart(scrollUp, 'up', scrollTime);
                else if (dy < 0) restart(scrollDown, 'down', scrollTime);
            }
        };
    }

    Overlay.mouse = function (holder) {
        var missing = false;
        return fetch('mouse.svg')
            .then(function (response) {
                missing = !response.ok;
                if (missing) throw new Error('mouse.svg');
                return response.text();
            })
            .catch(function (error) {
                return Overlay.fail(function (t) {
                    return t('overlay.mouseFailed') + '\n' + (missing ? t('overlay.mouseMissing') : error.message);
                });
            })
            .then(function (svgText) { return build(holder, svgText); });
    };
})();
