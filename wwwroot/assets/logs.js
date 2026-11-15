/* ============================================================
   DayZPulse — логи сервера: список, просмотр, фильтр, скачивание,
   резервные копии и настройки хранения.
   ============================================================ */
'use strict';

const Logs = (() => {
    const el = (id) => document.getElementById(id);

    let current = null;      // выбранный файл
    let bound = false;
    let followTimer = null;  // автообновление логов
    let lastLine = '';       // последняя показанная строка (для дописывания)

    const apiPath = (path) => {
        const id = App.currentServerId;
        return id ? `/servers/${encodeURIComponent(id)}${path}` : path;
    };

    const clear = (node) => { while (node && node.firstChild) node.removeChild(node.firstChild); };

    const text = (id, value) => {
        const node = el(id);
        if (node) node.textContent = value ?? '—';
    };

    /* --------------------------------------------------------------- список */

    async function load() {
        if (!App.currentServerId) return;

        try {
            const data = await App.api(apiPath('/logs'));
            text('logsDirectory', data.directory || '');

            lastSettings = data.settings ?? {};
            const dirHint = el('logBackupDir');
            if (dirHint) {
                const count = (data.backups ?? []).length;
                dirHint.textContent = data.backupDirectory
                    ? `Копии логов: ${data.backupDirectory}` + (count ? ` · архивов: ${count}` : '')
                    : '—';
            }

            renderFiles(data.files ?? []);

            // Если файл ещё не выбран — открываем самый свежий.
            if (!current && (data.files ?? []).length) {
                await open(data.files[0].name);
            } else if (current) {
                await open(current);
            }
        } catch (error) {
            App.toast('Логи недоступны', error.message, 'error');
        }
    }

    function renderFiles(files) {
        const box = el('logFiles');
        if (!box) return;
        clear(box);

        if (!files.length) {
            const hint = document.createElement('div');
            hint.className = 'muted';
            hint.style.fontSize = '12px';
            hint.textContent = 'Логов нет. Они появятся после запуска сервера.';
            box.appendChild(hint);
            return;
        }

        for (const file of files) {
            const row = document.createElement('button');
            row.type = 'button';
            row.className = 'log-item' + (file.name === current ? ' active' : '');
            row.dataset.name = file.name;

            const icon = document.createElement('span');
            icon.className = 'log-kind log-kind-' + (file.kind || 'log');
            icon.textContent = kindIcon(file.kind);

            const info = document.createElement('span');
            info.className = 'log-item-info';
            const name = document.createElement('b');
            name.textContent = file.name;
            const meta = document.createElement('span');
            meta.className = 'muted';
            meta.style.fontSize = '11px';
            meta.textContent = `${formatBytes(file.sizeBytes)} · ${formatTime(file.modified)}`;
            info.appendChild(name);
            info.appendChild(meta);

            row.appendChild(icon);
            row.appendChild(info);
            row.addEventListener('click', () => open(file.name));

            box.appendChild(row);
        }
    }

    function kindIcon(kind) {
        switch (kind) {
            case 'rpt': return '📄';
            case 'adm': return '🛡';
            case 'script': return '📜';
            case 'error': return '⚠';
            default: return '📁';
        }
    }

    /* ------------------------------------------------------------- просмотр */

    async function open(name) {
        current = name;
        lastLine = '';
        text('logTitle', name);

        // Подсветим активный файл.
        for (const item of document.querySelectorAll('#logFiles .log-item')) {
            item.classList.toggle('active', item.dataset.name === name);
        }

        await refresh();
        startFollow();
    }

    async function refresh() {
        if (!current) return;

        const tail = Number(el('logTail')?.value ?? 500);
        const filter = (el('logFilter')?.value ?? '').trim();
        const box = el('logContent');

        try {
            const query = new URLSearchParams({ tail: String(tail) });
            if (filter) query.set('filter', filter);

            const data = await App.api(apiPath(`/logs/${encodeURIComponent(current)}?${query}`));
            clear(box);

            const lines = data.lines ?? [];
            if (!lines.length) {
                const hint = document.createElement('span');
                hint.className = 'muted';
                hint.textContent = filter ? 'Ничего не найдено по фильтру.' : 'Лог пуст.';
                box.appendChild(hint);
                lastLine = '';
                return;
            }

            for (const line of lines) {
                const row = document.createElement('span');
                row.className = 'log-line' + lineClass(line);
                row.textContent = line;
                box.appendChild(row);
            }

            lastLine = lines[lines.length - 1];
            box.scrollTop = box.scrollHeight;
        } catch (error) {
            App.toast('Не удалось прочитать лог', error.message, 'error');
        }
    }

    /** Автообновление: дописывает только новые строки, не сбрасывая прокрутку. */
    async function follow() {
        if (!current || el('logFollow')?.checked === false) return;

        const tail = Number(el('logTail')?.value ?? 500);
        const filter = (el('logFilter')?.value ?? '').trim();
        const box = el('logContent');
        if (!box) return;

        try {
            const query = new URLSearchParams({ tail: String(tail) });
            if (filter) query.set('filter', filter);

            const data = await App.api(apiPath(`/logs/${encodeURIComponent(current)}?${query}`));
            const lines = data.lines ?? [];
            if (!lines.length) return;

            // Ищем ранее показанную строку и дописываем всё, что после неё.
            let from = 0;
            if (lastLine) {
                const index = lines.lastIndexOf(lastLine);
                if (index >= 0) {
                    from = index + 1;
                } else {
                    // Строка уехала из окна — перерисовываем целиком.
                    from = 0;
                    clear(box);
                }
            } else {
                clear(box);
            }

            const atBottom = box.scrollTop + box.clientHeight >= box.scrollHeight - 24;
            let appended = 0;

            for (let i = from; i < lines.length; i++) {
                const row = document.createElement('span');
                row.className = 'log-line' + lineClass(lines[i]);
                row.textContent = lines[i];
                box.appendChild(row);
                appended++;
            }

            // Ограничиваем число узлов, чтобы страница не разрасталась.
            while (box.childElementCount > tail + 200) {
                box.removeChild(box.firstChild);
            }

            lastLine = lines[lines.length - 1];

            // Прокручиваем вниз, только если пользователь и так был внизу.
            if (appended > 0 && atBottom) {
                box.scrollTop = box.scrollHeight;
            }
        } catch {
            // Тихий сбой автообновления — повторим на следующем тике.
        }
    }

    function startFollow() {
        stopFollow();
        followTimer = setInterval(follow, 2000);
    }

    function stopFollow() {
        if (followTimer) {
            clearInterval(followTimer);
            followTimer = null;
        }
    }

    function lineClass(line) {
        const lower = line.toLowerCase();
        if (lower.includes('error') || lower.includes('ошибк')) return ' is-error';
        if (lower.includes('warning') || lower.includes('warn')) return ' is-warn';
        return '';
    }

    function download() {
        if (!current) return;
        window.location.href = '/api' + apiPath(`/logs/${encodeURIComponent(current)}/download`);
    }

    /* -------------------------------------------------------------- бэкапы */

    async function backup() {
        const button = el('logsBackup');
        if (button) button.disabled = true;

        try {
            const result = await App.api(apiPath('/logs/backup'), { method: 'POST' });
            App.toast('Копия создана', result.fileName, 'info', 6000);
        } catch (error) {
            App.toast('Не удалось создать копию', error.message, 'error');
        } finally {
            if (button) button.disabled = false;
        }
    }

    function openSettings() {
        const s = lastSettings ?? {};
        el('logSetEnabled').checked = !!s.enabled;
        el('logSetOnStart').checked = !!s.onStart;
        el('logSetInterval').value = s.intervalHours ?? 12;
        el('logSetRetention').value = s.retentionCount ?? 5;
        el('logSetDays').value = s.logRetentionDays ?? 14;
        el('logSetCompress').checked = s.compress !== false;
        el('logSettingsModal')?.classList.add('open');
    }

    async function saveSettings() {
        try {
            await App.api(apiPath('/logs/settings'), {
                method: 'PUT',
                body: {
                    enabled: el('logSetEnabled').checked,
                    onStart: el('logSetOnStart').checked,
                    intervalHours: Number(el('logSetInterval').value) || 12,
                    retentionCount: Number(el('logSetRetention').value) || 5,
                    logRetentionDays: Number(el('logSetDays').value) || 14,
                    compress: el('logSetCompress').checked
                }
            });
            el('logSettingsModal')?.classList.remove('open');
            App.toast('Сохранено', 'Настройки логов обновлены.', 'info');
        } catch (error) {
            App.toast('Не удалось сохранить', error.message, 'error');
        }
    }

    let lastSettings = null;

    /* --------------------------------------------------------------- привязка */

    function bind() {
        if (bound) return;
        bound = true;

        el('logsRefresh')?.addEventListener('click', load);
        el('logsBackup')?.addEventListener('click', backup);
        el('logsSettingsBtn')?.addEventListener('click', openSettings);
        el('logDownload')?.addEventListener('click', download);
        el('logTail')?.addEventListener('change', refresh);
        el('logFollow')?.addEventListener('change', () => {
            if (el('logFollow').checked) startFollow();
            else stopFollow();
        });
        el('logFilter')?.addEventListener('input', () => {
            clearTimeout(el('logFilter').dataset.timer);
            el('logFilter').dataset.timer = setTimeout(() => { lastLine = ''; refresh(); }, 300);
        });

        el('logSettingsX')?.addEventListener('click', () => el('logSettingsModal')?.classList.remove('open'));
        el('logSettingsCancel')?.addEventListener('click', () => el('logSettingsModal')?.classList.remove('open'));
        el('logSettingsSave')?.addEventListener('click', saveSettings);
        el('logSettingsModal')?.addEventListener('click', (e) => { if (e.target.id === 'logSettingsModal') el('logSettingsModal').classList.remove('open'); });
    }

    /** Настройки из ответа /logs. */
    function setSettings(settings) {
        lastSettings = settings ?? {};
    }

    function reset() {
        current = null;
        lastLine = '';
        stopFollow();
    }

    return { bind, load, reset, setSettings, refresh };
})();

window.Logs = Logs;
