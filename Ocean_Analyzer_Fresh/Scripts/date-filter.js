// Shared behaviour for the dashboard filter bar: the date range field (Views/Shared/_DateFilter.cshtml)
// with quick presets and From/To validation, plus the Reset link and grey/blue Apply button state.
(function () {
    var BASIS_HELP = {
        ets: 'Estimated Time of Sailing: when the vessel leaves the port of loading.',
        eta: 'Estimated Time of Arrival: when the vessel reaches the destination port.',
        posted: "Date Posted: when the booking's costs and invoices were last posted."
    };
    var BASIS_TEXT = { ets: 'ETS', eta: 'ETA', posted: 'Posted' };
    var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

    function pad(n) { return n < 10 ? '0' + n : '' + n; }
    function iso(d) { return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()); }
    function addDays(d, n) { return new Date(d.getFullYear(), d.getMonth(), d.getDate() + n); }
    function parseIso(s) {
        var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s || '');
        return m ? new Date(+m[1], +m[2] - 1, +m[3]) : null;
    }
    function dayMonth(d) { return pad(d.getDate()) + ' ' + MONTHS[d.getMonth()]; }

    function presetRange(key) {
        var t = new Date();
        var today = new Date(t.getFullYear(), t.getMonth(), t.getDate());
        var y = today.getFullYear(), m = today.getMonth(), q = Math.floor(m / 3) * 3;
        switch (key) {
            case 'mtd': return [new Date(y, m, 1), today];
            case 'lastMonth': return [new Date(y, m - 1, 1), new Date(y, m, 0)];
            case 'qtd': return [new Date(y, q, 1), today];
            case 'lastQuarter': return [new Date(y, q - 3, 1), new Date(y, q, 0)];
            case 'ytd': return [new Date(y, 0, 1), today];
            case 'last30': return [addDays(today, -29), today];
            case 'last90': return [addDays(today, -89), today];
            case 'last12m': return [new Date(y - 1, m, today.getDate() + 1), today];
        }
        return null;
    }

    function formState(form) {
        var parts = [];
        new FormData(form).forEach(function (value, key) { parts.push(key + '=' + value); });
        return parts.join('&');
    }

    function initDateFilter(group) {
        var form = group.closest('form');
        var trigger = group.querySelector('[data-date-trigger]');
        var popover = group.querySelector('[data-date-popover]');
        var from = group.querySelector('[data-date-from]');
        var to = group.querySelector('[data-date-to]');
        var error = group.querySelector('[data-date-error]');
        var presets = group.querySelectorAll('[data-preset]');
        var help = group.querySelector('[data-date-basis-help]');

        function basis() {
            var checked = group.querySelector('input[name="dateType"]:checked');
            return checked ? checked.value : 'ets';
        }

        function validate() {
            var bad = from.value && to.value && from.value > to.value;
            from.classList.toggle('is-invalid', !!bad);
            to.classList.toggle('is-invalid', !!bad);
            error.hidden = !bad;
            return !bad;
        }

        // Refresh the trigger text, active preset, basis help and min/max limits from the inputs.
        function refresh() {
            var f = parseIso(from.value), t = parseIso(to.value);
            group.querySelector('[data-date-basis-text]').textContent = BASIS_TEXT[basis()];
            group.querySelector('[data-date-range-text]').textContent = f && t
                ? (f.getFullYear() === t.getFullYear() ? dayMonth(f) : dayMonth(f) + ' ' + f.getFullYear()) + ' – ' + dayMonth(t) + ' ' + t.getFullYear()
                : 'Select dates';
            Array.prototype.forEach.call(presets, function (btn) {
                var r = presetRange(btn.getAttribute('data-preset'));
                btn.classList.toggle('active', iso(r[0]) === from.value && iso(r[1]) === to.value);
            });
            help.textContent = BASIS_HELP[basis()];
            to.min = from.value;
            from.max = to.value;
            validate();
        }

        function open() {
            popover.hidden = false;
            trigger.setAttribute('aria-expanded', 'true');
            popover.classList.remove('align-right');
            if (popover.getBoundingClientRect().right > window.innerWidth - 16) popover.classList.add('align-right');
            var active = popover.querySelector('[data-preset].active') || popover.querySelector('.btn-check:checked');
            if (active) active.focus();
        }

        function close(returnFocus) {
            if (popover.hidden) return;
            popover.hidden = true;
            trigger.setAttribute('aria-expanded', 'false');
            if (returnFocus) trigger.focus();
        }

        trigger.addEventListener('click', function () {
            if (popover.hidden) open(); else close(false);
        });

        Array.prototype.forEach.call(presets, function (btn) {
            btn.addEventListener('click', function () {
                var r = presetRange(btn.getAttribute('data-preset'));
                from.value = iso(r[0]);
                to.value = iso(r[1]);
                refresh();
                form.dispatchEvent(new Event('change'));
                close(true);
            });
        });

        group.addEventListener('change', refresh);
        group.querySelector('[data-date-done]').addEventListener('click', function () { close(true); });

        popover.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') { e.preventDefault(); close(true); }
        });
        document.addEventListener('mousedown', function (e) {
            if (!group.contains(e.target)) close(false);
        });

        form.addEventListener('submit', function (e) {
            if (!validate()) {
                e.preventDefault();
                open();
                from.focus();
            }
        });

        refresh();
    }

    function initFilterActions(form) {
        var submit = form.querySelector('[type="submit"]');

        // The date field shows its own inline error instead of the browser's min/max popup.
        form.noValidate = true;

        // Reset goes back to the page defaults (all records, month to date by ETS).
        if (window.location.search) {
            var reset = document.createElement('a');
            reset.className = 'btn btn-sm btn-link filter-reset';
            reset.href = window.location.pathname;
            reset.textContent = 'Reset';
            submit.insertAdjacentElement('beforebegin', reset);
        }

        // Grey while the page shows the current filters, blue once there is something to apply.
        var applied = formState(form);
        function updateDirty() {
            var dirty = formState(form) !== applied;
            submit.classList.toggle('btn-primary', dirty);
            submit.classList.toggle('btn-applied', !dirty);
            submit.title = dirty ? 'Apply your filter changes' : 'These filters are applied';
        }
        form.addEventListener('input', updateDirty);
        form.addEventListener('change', updateDirty);
        updateDirty();
    }

    document.addEventListener('DOMContentLoaded', function () {
        Array.prototype.forEach.call(document.querySelectorAll('[data-date-filter]'), function (group) {
            initDateFilter(group);
            initFilterActions(group.closest('form'));
        });
    });
})();
