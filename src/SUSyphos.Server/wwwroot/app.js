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
        input.addEventListener('input', () => {
            clearTimeout(timer);

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
                        if (group.privileged) {
                            // Filtered by User Account Control: shown with the reason, but it cannot be picked.
                            choice.disabled = true;
                            choice.title = 'Filtered by UAC: Windows filters this group by User Account Control, so its members would get no role. Map a dedicated group. ' + group.sid;
                            const reason = document.createElement('span');
                            reason.className = 'pill fail uac-tag';
                            reason.textContent = 'Filtered by UAC';
                            choice.append(reason);
                        }

                        choice.addEventListener('click', () => {
                            input.value = group.name;
                            close();
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

    // Every pop-up of a disclosure (drop-down and row menus, column filters, multi-selects) floats above the page while
    // open, fixed to the window, so the scroll container of a table or dialog cannot cut it off; it opens upwards when
    // there is no room below, to the left at the right edge, and always stays inside the window.
    const popups = 'details[data-menu], details.col-filter, details.multi';
    const popOf = (details) => details.querySelector(':scope > .menu-pop, :scope > .filter-pop, :scope > .options');
    const placePopup = (details) => {
        const pop = popOf(details);
        if (!pop) {
            return;
        }

        if (!details.open) {
            pop.classList.remove('floating');
            for (const property of ['top', 'left', 'width']) {
                pop.style.removeProperty(property);
            }

            return;
        }

        const anchor = details.querySelector(':scope > summary').getBoundingClientRect();
        pop.classList.add('floating');
        if (details.matches('.multi')) {
            // A multi-select list is as wide as its field.
            pop.style.width = `${Math.round(anchor.width)}px`;
        }

        const width = pop.offsetWidth;
        const height = pop.offsetHeight;
        const right = document.documentElement.clientWidth - 8;
        const bottom = window.innerHeight - 8;
        const left = details.matches('.align-right') || anchor.left + width > right ? anchor.right - width : anchor.left;
        let top = anchor.bottom + 4;
        if (top + height > bottom && anchor.top - 4 - height >= 8) {
            top = anchor.top - 4 - height;
        }

        pop.style.left = `${Math.round(Math.max(8, Math.min(left, right - width)))}px`;
        pop.style.top = `${Math.round(Math.max(8, Math.min(top, bottom - height)))}px`;
    };
    // The toggle event does not bubble; a listener in the capture phase still sees it for every pop-up.
    document.addEventListener('toggle', (event) => {
        if (event.target instanceof HTMLDetailsElement && event.target.matches(popups)) {
            placePopup(event.target);
        }
    }, true);
    const placeOpenMenus = () => document.querySelectorAll(popups.split(', ').map((s) => `${s}[open]`).join(', ')).forEach(placePopup);
    window.addEventListener('scroll', placeOpenMenus, { capture: true, passive: true });
    window.addEventListener('resize', placeOpenMenus);
    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            const open = document.querySelector('details[data-menu][open]');
            closeMenus(null);
            open?.querySelector('summary')?.focus();
        }
    });

    // Hints while typing, from the same rules the server checks (minlength comes from the server's constant): how many
    // characters are missing, whether the repeated password matches, a weak password (only a hint, it is accepted) and
    // the form of a folder. The server still checks everything on saving.
    const liveHint = (input) => {
        let hint = input.parentElement.querySelector(':scope > .live-hint');
        if (!hint) {
            hint = document.createElement('span');
            hint.className = 'live-hint';
            hint.setAttribute('aria-live', 'polite');
            input.after(hint);
        }

        return hint;
    };
    const showHint = (input, text, kind) => {
        const hint = liveHint(input);
        hint.textContent = text;
        hint.dataset.kind = kind;
        hint.hidden = text.length === 0;
    };
    const fieldNamed = (input, name) => (input.form ?? document).querySelector(`[name="${CSS.escape(name)}"]`);
    const weakness = (input) => {
        const value = input.value;
        const user = input.dataset.userField ? fieldNamed(input, input.dataset.userField)?.value.trim().toLowerCase() : '';
        if (/^\d+$/.test(value)) {
            return 'only digits';
        }

        if (new Set(value.toLowerCase()).size <= 2) {
            return 'repeats one or two characters';
        }

        if (user && user.length >= 3 && value.toLowerCase().includes(user)) {
            return 'contains the user name';
        }

        return null;
    };
    const checkPassword = (input) => {
        const min = Number(input.getAttribute('minlength')) || 0;
        const length = input.value.length;
        if (length === 0) {
            showHint(input, '', '');
        } else if (length < min) {
            showHint(input, `Too short: ${min - length} more ${min - length === 1 ? 'character' : 'characters'} (at least ${min})`, 'error');
        } else if (weakness(input)) {
            showHint(input, `Weak: ${weakness(input)}; a passphrase of several words is safer`, 'warning');
        } else {
            showHint(input, 'Long enough', 'ok');
        }
    };
    const checkRepeat = (input) => {
        const original = fieldNamed(input, input.dataset.match);
        const differs = input.value.length > 0 && original && input.value !== original.value;
        input.setCustomValidity(differs ? 'The passwords do not match.' : '');
        showHint(input, input.value.length === 0 ? '' : differs ? 'The passwords do not match' : 'The passwords match', differs ? 'error' : 'ok');
    };
    const folderPattern = /^([A-Za-z]:\\|\\\\[A-Za-z0-9._-]+\\[A-Za-z0-9$._ -]+)/;
    const checkFolder = (input) => {
        const value = input.value.trim();
        const valid = value.length === 0 || (folderPattern.test(value) && !value.split('\\').some((part) => part === '.' || part === '..'));
        showHint(input, valid ? '' : 'Enter an absolute folder, e.g. D:\\Backup\\SUSyphos or \\\\fileserver\\backup\\susyphos', valid ? '' : 'error');
    };
    for (const input of document.querySelectorAll('input[type="password"][minlength]')) {
        input.addEventListener('input', () => {
            checkPassword(input);
            for (const repeat of (input.form ?? document).querySelectorAll(`input[data-match="${CSS.escape(input.name)}"]`)) {
                if (repeat.value.length > 0) {
                    checkRepeat(repeat);
                }
            }
        });
    }
    for (const input of document.querySelectorAll('input[data-match]')) {
        input.addEventListener('input', () => checkRepeat(input));
    }
    for (const input of document.querySelectorAll('input[data-folder]')) {
        input.addEventListener('input', () => checkFolder(input));
    }

    // An option with its own fields (rings to tick, a server to pick): using one of them chooses the option too.
    document.addEventListener('change', (event) => {
        const field = event.target instanceof Element ? event.target : null;
        const choice = field?.closest('.choice');
        const radio = choice?.querySelector(':scope > input[type="radio"]');
        if (radio && field !== radio) {
            radio.checked = true;
        }

        // Uninstall dialog: blocking is preselected only when the update goes everywhere, until it was ticked by hand.
        const block = field?.form?.querySelector('input[name="UninstallBlock"]:not([disabled])');
        if (block && field.name === 'UninstallBlock') {
            block.dataset.touched = 'true';
        } else if (block && !block.dataset.touched && field.form.querySelector('input[name="UninstallTarget"]:checked')) {
            block.checked = field.form.querySelector('input[name="UninstallTarget"]:checked').value === 'all';
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
    // Pages with live state reload themselves (LiveRefresh). The reload waits while a menu, filter or multi-select is open,
    // a field has the focus or text is selected, and it keeps the scroll position and the sections opened by hand.
    const openKey = 'susyphos.open';
    const sections = () => [...document.querySelectorAll('main details:not([data-menu]):not(.multi):not(.col-filter)')];
    const live = document.querySelector('meta[name="susyphos-refresh"]');
    if (live) {
        const [secondsText, ...rest] = live.content.split(';');
        const seconds = Number(secondsText);
        const target = rest.join(';').trim().replace(/^url=/i, '') || location.href;
        const inUse = () => document.querySelector('details[data-menu][open], details.multi[open], details.col-filter[open]')
            || document.activeElement?.matches('input, select, textarea')
            || String(window.getSelection() ?? '').length > 0;
        if (Number.isFinite(seconds) && seconds > 0) {
            const due = Date.now() + seconds * 1000;
            window.setInterval(() => {
                if (Date.now() < due || inUse()) {
                    return;
                }

                rememberScroll();
                try {
                    sessionStorage.setItem(openKey, JSON.stringify({ path: location.pathname, open: sections().map((s, i) => s.open ? i : -1).filter(i => i >= 0) }));
                } catch {
                    // Storage blocked: the sections come back in their default state.
                }

                location.replace(target);
            }, 1000);
        }
    }

    try {
        const saved = JSON.parse(sessionStorage.getItem(openKey) || 'null');
        sessionStorage.removeItem(openKey);
        if (saved && saved.path === location.pathname && Array.isArray(saved.open)) {
            const all = sections();
            all.forEach((section, i) => { section.open = saved.open.includes(i); });
        }
    } catch {
        // Nothing saved or storage blocked.
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
