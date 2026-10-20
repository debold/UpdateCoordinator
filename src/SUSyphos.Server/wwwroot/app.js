// Progressive enhancement for the dashboard (docs/06-dashboard.md). Every page works without this file: forms post to the
// server as before. The script only makes things more convenient: dependent fields become inert while their switch is off,
// column filters get a search box, and the maintenance window picker redraws its week preview on every change.
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

    const updatePicker = (picker) => {
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

    applyGates();
})();
