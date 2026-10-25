// Progressive enhancement for the dashboard (docs/06-dashboard.md). Every page works without this file: forms post to the
// server as before. The script only makes things more convenient: dependent fields become inert while their switch is off,
// column filters get a search box, the maintenance window picker redraws its week preview on every change, the first
// setup takes the token from the address the installer opened, and the AD group field searches the directory while typing.
// Served from the central server itself (Content-Security-Policy script-src 'self'); no inline code, no other sources.
(() => {
    'use strict';

    document.documentElement.classList.add('js');

    // Fields that only matter while a switch is on (.gated) or off (.gated.inverse). Inert fields cannot be focused or
    // clicked, but their values are still submitted, so switching back loses nothing.
    const applyGates = () => {
        for (const group of document.querySelectorAll('.gated')) {
            const input = group.querySelector(':scope > .gate input');
            const body = group.querySelector(':scope > .gated-body');
            if (input && body) {
                body.inert = group.classList.contains('inverse') ? input.checked : !input.checked;
            }
        }

        for (const group of document.querySelectorAll('.gated-select')) {
            const select = group.querySelector('select.gate');
            const off = select !== null && select.value === 'manual';
            for (const body of group.querySelectorAll('.gated-body')) {
                body.inert = off;
            }
        }
    };

    // Column filters: typing narrows the list of criteria.
    const filterOptions = (input) => {
        const term = input.value.trim().toLowerCase();
        const pop = input.closest('.filter-pop');
        for (const label of pop.querySelectorAll('.filter-options label')) {
            label.hidden = term.length > 0 && !label.textContent.toLowerCase().includes(term);
        }
    };

    // Maintenance window picker: the same coverage rule as WindowPickerForm.Coverage on the server.
    const minutes = (text) => {
        const match = /^(\d{2}):(\d{2})$/.exec(text ?? '');
        return match ? Number(match[1]) * 60 + Number(match[2]) : null;
    };

    const dayNames = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
    const weekOrder = [1, 2, 3, 4, 5, 6, 0];

    const readRows = (picker) => {
        const windows = [];
        for (const row of picker.querySelectorAll('.window-row')) {
            const days = [...row.querySelectorAll('.days input:checked')].map((box) => Number(box.value));
            const selects = row.querySelectorAll('select');
            const start = minutes(selects[0]?.value);
            const end = minutes(selects[1]?.value);
            const mode = selects[2]?.value ?? 'every';
            const weeks = [...row.querySelectorAll('.weeks input:checked')].map((box) => box.nextElementSibling?.textContent ?? box.value);
            if (days.length > 0 && start !== null && end !== null) {
                windows.push({ days, start, end, rule: mode === 'every' || weeks.length === 0 ? '' : mode === 'only' ? ` (${weeks.join(' and ')} week of the month)` : ` (except the ${weeks.join(' and ')} week)` });
            }
        }

        return windows;
    };

    const inheritedWindows = (picker) => {
        try {
            return JSON.parse(picker.dataset.inherited ?? '[]');
        } catch {
            return [];
        }
    };

    const format = (value) => `${String(Math.floor(value / 60)).padStart(2, '0')}:${String(value % 60).padStart(2, '0')}`;

    // Closed state of each row's week drop-down and the "select days" hint, as the server renders them.
    const updateRows = (picker) => {
        for (const row of picker.querySelectorAll('.window-row')) {
            const hint = row.querySelector('.select-days');
            if (hint) {
                hint.hidden = row.querySelector('.days input:checked') !== null;
            }

            const summary = row.querySelector('.week-summary');
            const mode = row.querySelector('.week-choice select')?.value ?? 'every';
            const weeks = [...row.querySelectorAll('.weeks input:checked')].map((box) => box.nextElementSibling?.textContent ?? box.value);
            if (summary) {
                summary.textContent = mode === 'every' || weeks.length === 0 ? 'every week' : `${mode === 'only' ? 'only' : 'all except'} ${weeks.join(' and ')} week`;
            }
        }
    };

    const updatePicker = (picker) => {
        updateRows(picker);
        const inherit = picker.querySelector(':scope > .gate input');
        const inherited = inherit !== null && inherit.checked;
        const windows = inherited ? inheritedWindows(picker) : readRows(picker);
        const cover = new Set();
        for (const window of windows) {
            for (const day of window.days) {
                const start = day * 1440 + window.start;
                let end = day * 1440 + window.end;
                if (window.end <= window.start) {
                    end += 1440;
                }

                for (let minute = start; minute < end; minute += 30) {
                    cover.add(Math.floor(minute / 60) % 168);
                }
            }
        }

        for (const cell of picker.querySelectorAll('.week .hour')) {
            cell.classList.toggle('on', cover.has(Number(cell.dataset.day) * 24 + Number(cell.dataset.hour)));
        }

        const summary = picker.querySelector('.window-summary');
        if (summary) {
            summary.textContent = windows.length === 0
                ? 'No window: installations never start.'
                : `${inherited ? 'Inherited: ' : ''}installations may start ${windows.map((w) => `${weekOrder.filter((d) => w.days.includes(d)).map((d) => dayNames[d]).join(', ')} ${format(w.start)}–${format(w.end)}${w.rule ?? ''}`).join('; ')} (server time).${inherited ? '' : ' Not saved yet.'}`;
        }

        // The week preview shows weekdays only; the list of next openings follows week rules and is calculated on the server.
        const openings = picker.querySelector('.window-openings');
        if (openings && !inherited) {
            openings.textContent = 'Next openings are calculated again when the windows are saved.';
        }
    };

    document.addEventListener('change', (event) => {
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }

        if (target.closest('.gate')) {
            applyGates();
        }

        if (target instanceof HTMLSelectElement && target.hasAttribute('data-auto-submit') && target.form) {
            target.form.requestSubmit();
        }

        const picker = target.closest('.window-picker');
        if (picker) {
            updatePicker(picker);
        }
    });

    document.addEventListener('input', (event) => {
        if (event.target instanceof HTMLInputElement && event.target.classList.contains('filter-search')) {
            filterOptions(event.target);
        }
    });

    for (const search of document.querySelectorAll('.filter-search')) {
        search.hidden = false;
    }

    for (const button of document.querySelectorAll('[data-auto-submit-button]')) {
        button.hidden = true;
    }

    // First setup opened by the installer: the token comes after # in the address, so it is never sent to the server or
    // written to its logs. It fills the field and is removed from the address (and so from the browser history).
    const fillFromAddress = () => {
        const fields = document.querySelectorAll('input[data-token-from-address]');
        if (fields.length === 0 || !location.hash) {
            return;
        }

        const values = new URLSearchParams(location.hash.slice(1));
        for (const field of fields) {
            const value = values.get(field.dataset.tokenFromAddress);
            if (value && /^[A-Za-z0-9_-]{32,128}$/.test(value) && field.value === '') {
                field.value = value;
            }
        }

        history.replaceState(null, '', location.pathname + location.search);
    };

    // AD group field: while typing, a list of matching groups from Active Directory; choosing one fills the field. The
    // field keeps working as plain text input when the directory cannot be reached.
    const groupSearch = (input) => {
        const list = document.createElement('ul');
        list.className = 'suggest';
        list.hidden = true;
        input.setAttribute('aria-autocomplete', 'list');
        input.insertAdjacentElement('afterend', list);
        let timer = 0;
        let request = 0;
        const close = () => { list.hidden = true; list.replaceChildren(); };
        const warning = input.closest('form')?.querySelector('[data-privileged-warning]');
        input.addEventListener('input', () => {
            clearTimeout(timer);
            if (warning) {
                warning.hidden = true;
            }

            const term = input.value.trim();
            if (term.length < 2) {
                close();
                return;
            }

            timer = setTimeout(async () => {
                const current = ++request;
                try {
                    const response = await fetch(`${input.dataset.adGroupSearch}?q=${encodeURIComponent(term)}`, { credentials: 'same-origin', headers: { Accept: 'application/json' } });
                    const groups = response.ok ? await response.json() : [];
                    if (current !== request) {
                        return;
                    }

                    list.replaceChildren(...groups.map((group) => {
                        const item = document.createElement('li');
                        const choice = document.createElement('button');
                        choice.type = 'button';
                        choice.textContent = group.name;
                        choice.title = group.sid;
                        choice.addEventListener('click', () => {
                            input.value = group.name;
                            close();
                            if (warning) {
                                warning.hidden = !group.privileged;
                            }

                            input.focus();
                        });
                        item.append(choice);
                        return item;
                    }));
                    list.hidden = groups.length === 0;
                } catch {
                    close();
                }
            }, 250);
        });
        input.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') {
                close();
            } else if (event.key === 'ArrowDown' && !list.hidden) {
                event.preventDefault();
                list.querySelector('button')?.focus();
            }
        });
        list.addEventListener('keydown', (event) => {
            const items = [...list.querySelectorAll('button')];
            const index = items.indexOf(document.activeElement);
            if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                event.preventDefault();
                items[Math.max(0, Math.min(items.length - 1, index + (event.key === 'ArrowDown' ? 1 : -1)))]?.focus();
            } else if (event.key === 'Escape') {
                close();
                input.focus();
            }
        });
        document.addEventListener('click', (event) => {
            if (!list.contains(event.target) && event.target !== input) {
                close();
            }
        });
    };

    // "Select all" in a table header: ticks or clears the row checkboxes of that table with the same name.
    for (const all of document.querySelectorAll('input[data-select-all]')) {
        const boxes = () => [...(all.closest('table')?.querySelectorAll(`tbody input[type="checkbox"][name="${all.dataset.selectAll}"]`) ?? [])];
        all.hidden = boxes().length === 0;
        all.addEventListener('change', () => {
            for (const box of boxes()) {
                box.checked = all.checked;
            }
        });
        all.closest('table')?.addEventListener('change', (event) => {
            if (event.target !== all && event.target instanceof HTMLInputElement && event.target.name === all.dataset.selectAll) {
                const list = boxes();
                all.checked = list.length > 0 && list.every((box) => box.checked);
                all.indeterminate = !all.checked && list.some((box) => box.checked);
            }
        });
    }

    // Drop-down buttons open without script (a disclosure); with it, only one is open at a time and a click elsewhere or
    // Esc closes them.
    const closeMenus = (except) => {
        for (const menu of document.querySelectorAll('details[data-menu][open]')) {
            if (menu !== except) {
                menu.open = false;
            }
        }
    };
    document.addEventListener('click', (event) => {
        const target = event.target instanceof Element ? event.target : null;
        closeMenus(target?.closest('details[data-menu]') ?? null);
    });
    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            const open = document.querySelector('details[data-menu][open]');
            closeMenus(null);
            open?.querySelector('summary')?.focus();
        }
    });

    for (const input of document.querySelectorAll('input[data-ad-group-search]')) {
        groupSearch(input);
    }

    // The result of an action is written at the top of the page. When the page keeps its scroll position after the
    // action, that line is out of sight: it is repeated in a small box at the top of the window. Errors stay until
    // closed, other messages fade after ten seconds.
    const showMessageInView = () => {
        const message = document.querySelector('.page-message');
        if (!message || message.getBoundingClientRect().bottom > 0) {
            return;
        }

        const toast = document.createElement('div');
        toast.className = 'toast ' + (message.classList.contains('error') ? 'error' : 'notice');
        toast.setAttribute('role', message.getAttribute('role') || 'status');
        const text = document.createElement('span');
        text.textContent = message.textContent;
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'toast-close';
        close.setAttribute('aria-label', 'Close message');
        close.textContent = '\u00d7';
        close.addEventListener('click', () => toast.remove());
        toast.append(text, close);
        document.body.append(toast);
        if (!message.classList.contains('error')) {
            window.setTimeout(() => toast.remove(), 10000);
        }
    };

    // Buttons that post a form and links that only change the address of the same page (add a maintenance window,
    // sort, filter, page, open a tab or dialog) load the page again; it comes back at the same scroll position instead
    // of the top. Kept for one load only, in the tab's session storage.
    const scrollKey = 'susyphos.scroll';
    const rememberScroll = () => {
        try {
            sessionStorage.setItem(scrollKey, JSON.stringify({ path: location.pathname, y: Math.round(window.scrollY) }));
        } catch {
            // Storage blocked: the page simply starts at the top.
        }
    };
    // The automatic refresh of a page that follows a running task (server detail) keeps the position as well.
    if (document.querySelector('meta[http-equiv="refresh"]')) {
        window.addEventListener('pagehide', rememberScroll);
    }

    document.addEventListener('submit', (event) => {
        const action = new URL(event.target.getAttribute('action') || location.href, location.href);
        if (action.origin === location.origin && action.pathname === location.pathname) {
            rememberScroll();
        }
    }, true);
    document.addEventListener('click', (event) => {
        const link = event.target instanceof Element ? event.target.closest('a[href]') : null;
        if (!link || link.target || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
            return;
        }

        const url = new URL(link.href, location.href);
        if (url.origin === location.origin && url.pathname === location.pathname && !url.hash) {
            rememberScroll();
        }
    }, true);
    try {
        const saved = JSON.parse(sessionStorage.getItem(scrollKey) || 'null');
        sessionStorage.removeItem(scrollKey);
        if (saved && saved.path === location.pathname && !location.hash && Number.isFinite(saved.y)) {
            window.scrollTo(0, saved.y);
            showMessageInView();
        }
    } catch {
        // Nothing to restore.
    }

    applyGates();
    fillFromAddress();
})();
