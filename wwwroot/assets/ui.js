'use strict';

/* ============================================================
   Общие элементы интерфейса: модалка ввода и диалог подтверждения.
   Используются из app.js, players.js и других разделов.
   ============================================================ */
const UI = (() => {
    let submitHandler = null;

    function isMultiline() {
        return !document.getElementById('promptArea')?.classList.contains('hidden');
    }

    function readValue() {
        return isMultiline()
            ? document.getElementById('promptArea').value
            : document.getElementById('promptInput').value;
    }

    function submit() {
        const handler = submitHandler;
        const value = readValue();
        close();
        handler?.(value);
    }

    function close() {
        submitHandler = null;
        document.getElementById('promptModal')?.classList.remove('open');
    }

    function init() {
        const modal = document.getElementById('promptModal');
        if (!modal || modal.dataset.bound) return;
        modal.dataset.bound = '1';

        document.getElementById('promptOk')?.addEventListener('click', submit);
        document.getElementById('promptCancel')?.addEventListener('click', close);
        document.getElementById('promptX')?.addEventListener('click', close);
        modal.addEventListener('click', (event) => {
            if (event.target.id === 'promptModal') close();
        });
        document.getElementById('promptInput')?.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') {
                event.preventDefault();
                submit();
            }
        });
    }

    /**
     * Открывает модалку ввода.
     * @param {{title?: string, label?: string, placeholder?: string, value?: string,
     *          multiline?: boolean, okLabel?: string, onOk?: (value: string) => void}} options
     */
    function prompt(options) {
        const modal = document.getElementById('promptModal');
        if (!modal) return;

        document.getElementById('promptTitle').textContent = options.title ?? 'Ввод';
        document.getElementById('promptLabel').textContent = options.label ?? '';

        const input = document.getElementById('promptInput');
        const area = document.getElementById('promptArea');

        if (options.multiline) {
            input.classList.add('hidden');
            area.classList.remove('hidden');
            area.value = options.value ?? '';
            area.placeholder = options.placeholder ?? '';
            setTimeout(() => area.focus(), 50);
        } else {
            area.classList.add('hidden');
            input.classList.remove('hidden');
            input.value = options.value ?? '';
            input.placeholder = options.placeholder ?? '';
            setTimeout(() => { input.focus(); input.select(); }, 50);
        }

        document.getElementById('promptOk').textContent = options.okLabel ?? 'Отправить';
        submitHandler = (value) => options.onOk?.(value);
        modal.classList.add('open');
    }

    /**
     * Диалог подтверждения. Переиспользует общую модалку confirmModal.
     * options: { icon, title, sub, list, danger }.
     */
    function confirm(text, action, okLabel, alt, options = {}) {
        const modal = document.getElementById('confirmModal');
        if (!modal) return;

        document.getElementById('confirmText').textContent = text;
        document.getElementById('confirmOk').textContent = okLabel ?? 'Подтвердить';

        // Значок и подзаголовок: по умолчанию — предупреждение.
        const icon = document.getElementById('confirmIcon');
        if (icon) icon.textContent = options.icon ?? '⚠';

        const title = document.getElementById('confirmTitle');
        if (title) title.textContent = options.title ?? 'Подтверждение';

        const sub = document.getElementById('confirmSub');
        if (sub) sub.textContent = options.sub ?? 'Проверьте действие перед продолжением';

        // Список подробностей (например, какие настройки не сохранены).
        const list = document.getElementById('confirmList');
        if (list) {
            list.textContent = '';
            const items = options.list ?? [];

            list.classList.toggle('hidden', items.length === 0);
            for (const item of items) {
                const li = document.createElement('li');
                li.textContent = item;
                list.appendChild(li);
            }
        }

        // Основная кнопка: опасное действие — красная, обычное — синяя.
        const ok = document.getElementById('confirmOk');
        if (ok) ok.className = 'btn ' + (options.danger === false ? '' : 'danger');

        // Второй (мягкий) вариант: например «удалить только из панели».
        const altButton = document.getElementById('confirmAlt');
        if (altButton) {
            altButton.style.display = alt ? '' : 'none';
            altButton.textContent = alt?.label ?? '';
            altButton._action = alt?.action ?? null;
        }

        modal.dataset.action = '1';
        modal._action = action;
        modal.classList.add('open');
    }

    init();

    return { prompt, confirm, close };
})();
