'use strict';

/* ============================================================
   DayZPulse — выбор папки прямо в браузере.
   Работает всегда, включая режим службы Windows, где системный
   диалог показать нельзя.
   ============================================================ */

const FolderPicker = (() => {
    let targetId = null;
    let current = '';
    let onPicked = null;

    function $(id) { return document.getElementById(id); }

    function csrf() {
        const prefix = 'dayzpulse_csrf=';
        for (const part of document.cookie.split(';')) {
            const value = part.trim();
            if (value.startsWith(prefix)) return decodeURIComponent(value.slice(prefix.length));
        }
        return '';
    }

    /** Открывает окно выбора папки и пишет результат в поле с указанным id. */
    function open(inputId, options = {}) {
        targetId = inputId;
        onPicked = options.onPicked ?? null;

        const modal = $('folderModal');
        if (!modal) {
            // Окна нет — пробуем системный диалог панели.
            return nativeFallback();
        }

        const start = ($(inputId)?.value ?? '').trim();
        modal.classList.add('open');
        hideError();
        current = '';

        return load(start || null);
    }

    /** Если окно выбора недоступно, используем диалог на стороне панели. */
    async function nativeFallback() {
        try {
            const response = await fetch('/api/system/pick-folder', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf() },
                body: JSON.stringify({ path: $(targetId)?.value ?? '' })
            });

            const data = await response.json();
            if (data.ok && data.path) {
                apply(data.path);
            } else if (data.error) {
                notify(data.error);
            }
        } catch (error) {
            notify('Не удалось выбрать папку: ' + error.message);
        }
    }

    async function load(path) {
        const list = $('folderList');
        if (!list) return;

        list.textContent = '';

        const loading = document.createElement('div');
        loading.className = 'folder-empty';
        loading.textContent = 'Загрузка…';
        list.appendChild(loading);

        try {
            const url = path
                ? '/api/system/browse?path=' + encodeURIComponent(path)
                : '/api/system/browse';

            const response = await fetch(url, { credentials: 'same-origin' });
            const data = await response.json();

            current = data.path ?? '';
            render(data);
        } catch (error) {
            showError('Не удалось прочитать каталог: ' + error.message);
        }
    }

    function render(data) {
        const list = $('folderList');
        list.textContent = '';

        if (data.error) {
            showError(data.error);
        } else {
            hideError();
        }

        // Кнопка возврата вверх по дереву.
        if (data.parent) {
            list.appendChild(row('↑  ..', data.parent, true, () => load(data.parent)));
        }

        // На верхнем уровне показываем диски, внутри каталога — подпапки.
        const entries = data.path ? data.directories : data.drives;

        if (!entries || entries.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'folder-empty';
            empty.textContent = data.path ? 'Подпапок нет' : 'Диски не найдены';
            list.appendChild(empty);
        } else {
            for (const dir of entries) {
                list.appendChild(row('📁  ' + dir.name, dir.path, false, () => load(dir.path)));
            }
        }

        // Внутри диска кнопка «Все диски» возвращает к списку томов,
        // а создавать папки можно только внутри каталога.
        $('folderDrives')?.classList.toggle('hidden', !data.path);
        $('folderNew')?.classList.toggle('hidden', !data.path);

        if (!data.path) {
            hideNewRow();
        }

        setText('folderCurrent', data.path || 'Диски');
    }

    function row(label, path, isUp, handler) {
        const element = document.createElement('button');
        element.type = 'button';
        element.className = 'folder-row' + (isUp ? ' folder-up' : '');
        element.title = path;

        // Иконку и название раскладываем отдельно: так длинное имя
        // аккуратно обрезается, а иконка остаётся на месте.
        const match = /^(\S+)\s+(.*)$/.exec(label);
        if (match) {
            const icon = document.createElement('i');
            icon.className = 'ico';
            icon.textContent = match[1];
            const text = document.createElement('span');
            text.textContent = match[2];
            element.appendChild(icon);
            element.appendChild(text);
        } else {
            const text = document.createElement('span');
            text.textContent = label;
            element.appendChild(text);
        }

        element.addEventListener('click', handler);
        return element;
    }

    function setText(id, value) {
        const element = $(id);
        if (element) element.textContent = value;
    }

    function apply(path) {
        const input = $(targetId);
        if (input) {
            input.value = path;
            input.dispatchEvent(new Event('input'));
        }

        onPicked?.(path);
        close();
    }

    function close() {
        $('folderModal')?.classList.remove('open');
        targetId = null;
        onPicked = null;
    }

    function showError(message) {
        const box = $('folderError');
        if (!box) return;
        box.textContent = message;
        box.classList.remove('hidden');
    }

    function hideError() {
        $('folderError')?.classList.add('hidden');
    }

    function notify(message) {
        // На странице панели есть тосты, на странице настройки — нет.
        if (typeof App !== 'undefined' && App.toast) {
            App.toast('Выбор папки', message, 'error');
        } else {
            showError(message);
        }
    }

    function bind() {
        const modal = $('folderModal');
        if (!modal || modal.dataset.bound) return;
        modal.dataset.bound = '1';

        $('folderCancel')?.addEventListener('click', close);
        $('folderX')?.addEventListener('click', close);

        $('folderDrives')?.addEventListener('click', () => {
            hideNewRow();
            load(null);
        });

        $('folderChoose')?.addEventListener('click', () => {
            if (current) {
                apply(current);
            } else {
                showError('Откройте диск или папку.');
            }
        });

        // Создание папки прямо в текущем каталоге.
        $('folderNew')?.addEventListener('click', () => toggleNewRow(true));
        $('folderNewCancel')?.addEventListener('click', () => toggleNewRow(false));

        $('folderNewOk')?.addEventListener('click', () => createFolder());

        $('folderNewName')?.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') { event.preventDefault(); createFolder(); }
            if (event.key === 'Escape') { event.preventDefault(); toggleNewRow(false); }
        });

        modal.addEventListener('click', (event) => {
            if (event.target.id === 'folderModal') close();
        });
    }

    /** Показывает или скрывает строку создания папки. */
    function toggleNewRow(show) {
        const row = $('folderNewRow');
        const input = $('folderNewName');
        if (!row) return;

        // Создавать папки можно только внутри каталога, не в списке дисков.
        if (show && !current) {
            showError('Сначала откройте диск или папку.');
            return;
        }

        row.classList.toggle('hidden', !show);

        if (show && input) {
            input.value = '';
            input.focus();
        }
    }

    function hideNewRow() {
        $('folderNewRow')?.classList.add('hidden');
    }

    async function createFolder() {
        const input = $('folderNewName');
        const name = (input?.value ?? '').trim();

        if (!current) {
            showError('Сначала откройте папку, где создать новую.');
            return;
        }

        if (name.length === 0) {
            showError('Укажите имя новой папки.');
            input?.focus();
            return;
        }

        const button = $('folderNewOk');
        if (button) { button.disabled = true; button.textContent = 'Создание…'; }

        try {
            const response = await fetch('/api/system/mkdir', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf() },
                body: JSON.stringify({ parent: current, name })
            });

            const data = await response.json();

            if (!response.ok || !data.ok) {
                showError(data.error ?? 'Не удалось создать папку.');
                return;
            }

            hideNewRow();
            hideError();

            // Открываем созданную папку, чтобы её сразу было видно.
            await load(data.path ?? current);
        } catch (error) {
            showError('Не удалось создать папку: ' + error.message);
        } finally {
            if (button) { button.disabled = false; button.textContent = 'Создать'; }
        }
    }

    bind();

    return { open, bind };
})();
