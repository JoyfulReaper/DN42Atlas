// Run with: node --test DN42Atlas.Tests/ViewerTests.cjs
// Exercise the bundled script with a small DOM fixture; no browser or packages required.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const template = fs.readFileSync(path.join(__dirname, '../DN42Atlas/Reporting/viewer.html'), 'utf8');
const script = template.match(/<script>([\s\S]*?)<\/script>/)[1].replace('{{scanJson}}', '{}');

function element(id, text = '') {
    const attributes = new Map();
    const classes = new Set();
    return {
        id, textContent: text, value: '', hidden: false, disabled: false, events: {},
        classList: { toggle(name, active) { classes[active ? 'add' : 'delete'](name); },
            add(name) { classes.add(name); }, remove(name) { classes.delete(name); }, contains(name) { return classes.has(name); } },
        addEventListener(type, callback) { this.events[type] = callback; },
        setAttribute(name, value) { attributes.set(name, value); },
        removeAttribute(name) { attributes.delete(name); },
        getAttribute(name) { return attributes.get(name); },
        getBoundingClientRect() { return { height: this.height ?? 140 }; },
        focus() { this.focused = true; }, scrollIntoView() { this.scrolledIntoView = true; }, select() {},
        click() { if (!this.disabled) return this.events.click?.(); }
    };
}

function viewer({ hash = '', empty = false, restored = {}, initialize = true, complete = true, failSort = false, offlineOnly = false } = {}) {
    const ids = ['services', 'search', 'scheme', 'robots', 'reachability', 'homepage-response',
        'has-title', 'has-links', 'port-kind', 'discovery-count', 'discovery-filters', 'previous', 'next', 'random',
        'controls', 'copy-link', 'copy-status', 'copy-fallback', 'copy-url', 'counter',
        'directory-count', 'no-matches', 'no-selection', 'reset'];
    const ui = Object.fromEntries(ids.map(id => [id, element(id)]));
    ui.controls.hidden = true;
    // Match the browser's initial select values, including explicit selected options.
    for (const [, id, options] of template.matchAll(/<select id="([^"]+)"[^>]*>([\s\S]*?)<\/select>/g)) {
        const initial = options.match(/<option value="([^"]*)" selected>/) ?? options.match(/<option value="([^"]*)"/);
        ui[id].value = initial[1];
    }
    for (const [id, value] of Object.entries(restored)) ui[id].value = value;
    const data = empty ? [] : [
        ['a', 'http://a.dn42/', 'Zulu', 'http', 80, 'true', 'true', 'true', 'true', 'standard', 'Reachable · HTTP 200'],
        ['b', 'http://b.dn42:8080/', 'Alpha', 'http', 8080, 'true', 'true', 'true', 'false', 'other', 'Reachable · HTTP 404'],
        ['c', 'https://c.dn42/', 'Beta', 'https', 443, 'false', 'false', 'true', 'true', 'standard', 'Unreachable'],
        ['d', 'http://d.dn42:900/', '(no title)', 'http', 900, 'true', 'false', 'false', 'false', 'other', 'Reachable · homepage not fetched']
    ];
    const rows = data.map(([id, origin, title, scheme, port, reachable, response, hasTitle, links, portKind, status]) => {
        const row = element('row-' + id);
        row.dataset = { service: 'service-' + id, search: `${origin} ${title} text/html`,
            scheme, port: String(port), robots: 'Allowed', reachable: offlineOnly ? 'false' : reachable,
            response, title: hasTitle, links, portKind };
        row.cells = [origin, title, `${scheme.toUpperCase()} / ${port}`, status + ' robots: Allowed']
            .map(text => ({ textContent: text, querySelector: () => ({ textContent: text }) }));
        const link = element('link-' + id);
        row.querySelector = () => link;
        return row;
    });
    const panels = rows.map(row => element(row.dataset.service));
    for (const panel of panels) ui[panel.id] = panel;
    const headers = ['origin', 'title', 'scheme', 'status'].map((key, index) => {
        const header = element(key);
        header.dataset = { sort: key };
        const button = element(key + '-button', ['Origin', 'Title', 'Scheme / port', 'Status'][index] + ' ↕');
        const indicator = element(key + '-indicator', '↕');
        header.querySelector = selector => selector === 'button' ? button : indicator;
        return header;
    });
    // appendChild moves an existing node, just as it does in a real tbody.
    ui.services.children = [...rows];
    ui.services.appendChild = row => {
        if (failSort) throw new Error('Forced initialization failure');
        ui.services.children.splice(ui.services.children.indexOf(row), 1);
        ui.services.children.push(row);
    };
    const root = element('html');
    root.style = { setProperty() {} };
    const document = { events: {}, documentElement: root,
        querySelectorAll(selector) {
            if (selector === '#services tr') return rows;
            if (selector === '.service-detail') return panels;
            if (selector === '#services tr, .service-detail') return [...rows, ...panels];
            if (selector === '.sort-button') return headers.map(header => header.querySelector('button'));
            return headers;
        },
        getElementById: id => ui[id], addEventListener(type, callback) { this.events[type] = callback; } };
    const location = new URL('https://atlas.example/view.html?keep=yes' + hash);
    const history = { calls: [], state: null, pushState(state, b, url) { this.state = state; this.calls.push('push'); location.href = new URL(url, location).href; },
        replaceState(state, b, url) { this.state = state; this.calls.push('replace'); location.href = new URL(url, location).href; } };
    const window = { innerWidth: 1200, innerHeight: 900, events: {}, addEventListener(type, callback) { this.events[type] = callback; } };
    const math = Object.create(Math);
    let randomValue = 0;
    math.random = () => randomValue;
    const frames = [];
    vm.runInNewContext(script, { document, window, location, history, URL, Math: math,
        navigator: {}, clearTimeout() {}, setTimeout() {}, requestAnimationFrame(fn) { frames.push(fn); },
        console: { error() {} } });
    if (initialize) document.events.DOMContentLoaded();
    if (initialize && complete) {
        window.events.load?.();
        while (frames.length) frames.shift()();
    }
    return {
        ui, headers, window, location, history, panels, root,
        initialize() { document.events.DOMContentLoaded(); },
        complete() { window.events.load?.(); while (frames.length) frames.shift()(); },
        beforePaint() { while (frames.length) frames.shift()(); },
        sort(key) { headers.find(header => header.dataset.sort === key).querySelector('button').click(); },
        filter(id, value) { ui[id].value = value; ui[id].events[id === 'search' ? 'input' : 'change'](); },
        visible() { return ui.services.children.filter(row => !row.hidden).map(row => row.dataset.service.slice(8)); },
        selected() { return panels.find(panel => !panel.hidden)?.id.slice(8); },
        random(value) { randomValue = value; ui.random.click(); },
        visit(id, event = 'hashchange') { location.hash = '#service-' + id; window.events[event](); }
    };
}

test('all four headers toggle direction, expose one aria-sort and retain selection/history', () => {
    const v = viewer({ hash: '#service-b' });
    v.filter('reachability', '');
    const initialHistory = [...v.history.calls];
    assert.equal(v.headers[0].getAttribute('aria-sort'), 'ascending');
    assert.deepEqual(v.visible(), ['a', 'b', 'd', 'c']);
    for (const [key, ascending] of [
        ['title', ['d', 'b', 'c', 'a']], ['scheme', ['a', 'd', 'b', 'c']],
        ['status', ['d', 'a', 'b', 'c']]
    ]) {
        v.sort(key);
        assert.deepEqual(v.visible(), ascending);
        const header = v.headers.find(header => header.dataset.sort === key);
        assert.equal(header.getAttribute('aria-sort'), 'ascending');
        assert.match(header.querySelector('button').getAttribute('aria-label'), /sort descending$/);
        v.sort(key);
        assert.deepEqual(v.visible(), [...ascending].reverse());
        assert.equal(header.getAttribute('aria-sort'), 'descending');
        assert.equal(v.headers.filter(h => h.getAttribute('aria-sort')).length, 1);
        assert.equal(v.selected(), 'b');
        assert.equal(v.location.hash, '#service-b');
    }
    v.sort('origin');
    assert.deepEqual(v.visible(), ['a', 'b', 'd', 'c']);
    v.sort('origin');
    assert.deepEqual(v.visible(), ['c', 'd', 'b', 'a']);
    assert.deepEqual(v.history.calls, initialHistory);
});

test('initial status is Reachable, reset shows all and unreachable deep links still work', () => {
    const v = viewer();
    assert.equal(v.ui.reachability.value, 'true');
    assert.deepEqual(v.visible(), ['a', 'b', 'd']);
    assert.equal(v.selected(), 'a');
    assert.equal(v.location.hash, '#service-a');
    v.ui.reset.click();
    assert.equal(v.ui.reachability.value, '');
    assert.deepEqual(v.visible(), ['a', 'b', 'd', 'c']);

    const linked = viewer({ hash: '#service-c' });
    assert.equal(linked.selected(), 'c');
    assert.equal(linked.ui.reachability.value, '');
    assert.deepEqual(linked.visible(), ['a', 'b', 'd', 'c']);
    assert.deepEqual(linked.history.calls, ['replace', 'replace']);
});

test('search and existing filters compose with sorting', () => {
    const v = viewer();
    v.filter('search', 'http://');
    v.sort('title');
    assert.deepEqual(v.visible(), ['d', 'b', 'a']);
    v.filter('reachability', 'true');
    v.filter('scheme', 'http');
    v.filter('robots', 'Allowed');
    v.filter('search', 'text/html');
    assert.deepEqual(v.visible(), ['d', 'b', 'a']);
    v.sort('title');
    assert.deepEqual(v.visible(), ['a', 'b', 'd']);
    assert.equal(v.selected(), 'a');
});

test('discovery filters combine, count active choices and retain order on change', () => {
    const v = viewer();
    v.sort('title');
    v.filter('homepage-response', 'true');
    assert.deepEqual(v.visible(), ['b', 'a']);
    v.filter('has-title', 'true');
    v.filter('has-links', 'true');
    v.filter('port-kind', 'standard');
    assert.deepEqual(v.visible(), ['a']);
    assert.equal(v.ui['discovery-count'].textContent, '(4 active)');
    v.ui.reset.click();
    v.filter('homepage-response', 'false');
    assert.deepEqual(v.visible(), ['d', 'c']);
    v.filter('has-title', 'false');
    v.filter('has-links', 'false');
    v.filter('port-kind', 'other');
    assert.deepEqual(v.visible(), ['d']);
});

test('deep links relax conflicting predicates only, retaining matching filters and sorting', () => {
    for (const event of ['hashchange', 'popstate']) {
        const v = viewer();
        v.sort('title');
        for (const [id, value] of [['search', 'a.dn42'], ['scheme', 'http'], ['robots', 'Allowed'],
            ['reachability', 'true'], ['homepage-response', 'true'], ['has-title', 'true'],
            ['has-links', 'true'], ['port-kind', 'standard']]) v.filter(id, value);
        assert.deepEqual(v.visible(), ['a']);
        const before = v.history.calls.length;
        v.visit('c', event);
        assert.equal(v.selected(), 'c');
        assert.deepEqual(v.visible(), ['c', 'a']);
        assert.equal(v.history.calls.length, before);
        for (const id of ['search', 'scheme', 'reachability', 'homepage-response'])
            assert.equal(v.ui[id].value, '');
        for (const [id, value] of [['robots', 'Allowed'], ['has-title', 'true'], ['has-links', 'true'], ['port-kind', 'standard']])
            assert.equal(v.ui[id].value, value);
        assert.equal(v.ui['discovery-count'].textContent, '(3 active)');
    }
});

test('fresh documents explicitly ignore restored form values and select only reachable services', () => {
    const v = viewer({ restored: { reachability: 'false', search: 'host-that-is-not-present',
        scheme: 'https', robots: 'Disallowed', 'has-title': 'false', 'has-links': 'false' } });
    assert.equal(v.ui.reachability.value, 'true');
    for (const id of ['search', 'scheme', 'robots', 'homepage-response', 'has-title', 'has-links', 'port-kind'])
        assert.equal(v.ui[id].value, '');
    assert.deepEqual(v.visible(), ['a', 'b', 'd']);
    assert.equal(v.selected(), 'a');
    const offline = viewer({ offlineOnly: true });
    assert.equal(offline.ui.reachability.value, 'true');
    assert.deepEqual(offline.visible(), []);
    assert.equal(offline.selected(), undefined);
});

test('direct reachable deep links keep Reachable and ignore stale browser form restoration', () => {
    const v = viewer({ hash: '#service-b', restored: { reachability: '', search: 'no match' } });
    assert.equal(v.ui.reachability.value, 'true');
    assert.equal(v.ui.search.value, '');
    assert.equal(v.selected(), 'b');
    assert.deepEqual(v.visible(), ['a', 'b', 'd']);
    assert.equal(v.location.hash, '#service-b');
});

test('an unreachable history target relaxes only Status when every other predicate matches', () => {
    const v = viewer();
    for (const [id, value] of [['search', 'text/html'], ['robots', 'Allowed'], ['has-title', 'true'],
        ['has-links', 'true'], ['port-kind', 'standard']]) v.filter(id, value);
    assert.deepEqual(v.visible(), ['a']);
    v.visit('c', 'popstate');
    assert.equal(v.ui.reachability.value, '');
    for (const [id, value] of [['search', 'text/html'], ['robots', 'Allowed'], ['has-title', 'true'],
        ['has-links', 'true'], ['port-kind', 'standard']]) assert.equal(v.ui[id].value, value);
    assert.equal(v.selected(), 'c');
    assert.deepEqual(v.visible(), ['a', 'c']);
});

test('history and BFCache resume current applied filters rather than resetting fresh defaults', () => {
    const v = viewer();
    v.ui.reset.click();
    v.filter('has-title', 'true');
    v.filter('search', 'text/html');
    v.sort('title');
    // Simulate a browser resetting/restoring controls without user input events.
    v.ui.reachability.value = 'true'; v.ui.search.value = 'stale'; v.ui['has-title'].value = '';
    v.window.events.pageshow({ persisted: true });
    assert.equal(v.ui.reachability.value, '');
    assert.equal(v.ui.search.value, 'text/html');
    assert.equal(v.ui['has-title'].value, 'true');
    v.visit('c', 'popstate');
    assert.equal(v.selected(), 'c');
    assert.deepEqual(v.visible(), ['b', 'c', 'a']);
    // Restoration may also run after popstate/pageshow; reconcile before the next paint.
    v.ui.reachability.value = 'false'; v.ui.search.value = 'another stale value';
    v.beforePaint();
    assert.equal(v.ui.reachability.value, '');
    assert.equal(v.ui.search.value, 'text/html');
    assert.equal(v.headers[1].getAttribute('aria-sort'), 'ascending');
});

test('head bootstrap suppresses partial enhanced output and positions a deep link only after initialization', () => {
    const v = viewer({ hash: '#service-c', initialize: false });
    assert.equal(v.root.classList.contains('js-pending'), true);
    assert.equal(v.location.hash, '', 'The parser must not anchor to the unenhanced detail stack.');
    assert.equal(v.ui.controls.hidden, true);
    assert.equal(v.panels.some(panel => panel.scrolledIntoView), false);
    v.initialize();
    assert.equal(v.root.classList.contains('js-pending'), false);
    assert.equal(v.ui.controls.hidden, false);
    assert.equal(v.selected(), 'c');
    assert.equal(v.panels.filter(panel => !panel.hidden).length, 1);
    assert.equal(v.panels.find(panel => panel.id === 'service-c').scrolledIntoView, true);
    assert.equal(v.location.hash, '#service-c');
});

test('initialization failures reveal the complete static directory instead of stranding a hidden page', () => {
    const v = viewer({ failSort: true });
    assert.equal(v.root.classList.contains('js-pending'), false);
    assert.equal(v.ui.controls.hidden, true);
    assert.equal(v.panels.every(panel => !panel.hidden), true);
    assert.deepEqual(v.visible(), ['a', 'b', 'c', 'd']);
});

test('Previous/Next/Random update history without scrolling or moving focus', () => {
    const v = viewer();
    v.ui.next.click();
    v.ui.previous.click();
    v.random(0.999);
    assert.equal(v.history.calls.filter(call => call === 'push').length, 3);
    assert.equal(v.panels.some(panel => panel.scrolledIntoView || panel.focused), false);
});

test('fresh-load selection is final before paint without supplying a fragment to the browser during parsing', () => {
    const v = viewer({ complete: false });
    assert.equal(v.root.classList.contains('js-pending'), false);
    assert.equal(v.selected(), 'a');
    assert.deepEqual(v.visible(), ['a', 'b', 'd']);
    assert.equal(v.location.hash, '');
    v.complete();
    assert.equal(v.location.hash, '#service-a');
    assert.equal(v.panels.some(panel => panel.scrolledIntoView || panel.focused), false);
});

test('reset clears filters while retaining sort and any still-selected service', () => {
    const v = viewer();
    v.sort('title'); v.sort('title');
    v.filter('has-links', 'false');
    assert.equal(v.selected(), 'b');
    v.ui.reset.click();
    assert.deepEqual(v.visible(), ['a', 'c', 'b', 'd']);
    assert.equal(v.selected(), 'b');
    assert.equal(v.ui['discovery-count'].textContent, '');
    assert.equal(v.headers[1].getAttribute('aria-sort'), 'descending');
});

test('previous/next wrap in displayed filtered/sorted order; random uses only that set', () => {
    const v = viewer();
    v.sort('scheme');
    v.filter('port-kind', 'other');
    assert.deepEqual(v.visible(), ['d', 'b']);
    assert.equal(v.selected(), 'd');
    v.ui.next.click(); assert.equal(v.selected(), 'b');
    v.ui.next.click(); assert.equal(v.selected(), 'd');
    v.ui.previous.click(); assert.equal(v.selected(), 'b');
    v.random(0); assert.equal(v.selected(), 'd');
    v.random(0.999); assert.equal(v.selected(), 'b');
    v.sort('scheme');
    assert.deepEqual(v.visible(), ['b', 'd']);
    assert.equal(v.selected(), 'b');
    v.ui.next.click(); assert.equal(v.selected(), 'd');
});

test('no matches and empty reports remain safe through sorting, navigation and reset', () => {
    for (const empty of [false, true]) {
        const v = viewer({ empty });
        v.filter('search', 'no matching origin');
        v.sort('status');
        assert.deepEqual(v.visible(), []);
        assert.equal(v.selected(), undefined);
        for (const id of ['previous', 'random', 'next', 'copy-link']) assert.equal(v.ui[id].disabled, true);
        v.random(0.5);
        v.ui.reset.click();
        assert.equal(v.visible().length, empty ? 0 : 4);
    }
});

test('opening discovery filters keeps them visible when the toolbar grows too tall to pin', () => {
    const v = viewer();
    v.ui.controls.height = 350;
    v.ui['discovery-filters'].open = true;
    v.ui['discovery-filters'].events.toggle();
    assert.equal(v.ui['discovery-filters'].scrolledIntoView, true);
});
