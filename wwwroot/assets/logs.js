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

    // Архивы логов открываются прямо внутри zip, без распаковки.
    let archiveName = null;  // имя открытого архива
    let archiveEntry = null; // выбранный файл внутри архива
    let liveFiles = [];      // текущие логи сервера
    let archiveFiles = [];   // сохранённые архивы логов

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
            liveFiles = data.files ?? [];
            archiveFiles = data.backups ?? [];

            const dirHint = el('logBackupDir');
            if (dirHint) {
                dirHint.textContent = data.backupDirectory
                    ? `Копии логов: ${data.backupDirectory}` + (archiveFiles.length ? ` · архивов: ${archiveFiles.length}` : '')
                    : '—';
            }

            // Архив открыт — перечитываем его содержимое (мог появиться новый).
            if (archiveName) {
                await openArchive(archiveName);
                return;
            }

            renderFiles(liveFiles);

            // Если файл ещё не выбран — открываем самый свежий.
            if (!current && liveFiles.length) {
                await open(liveFiles[0].name);
            } else if (current) {
                await open(current);
            }
        } catch (error) {
            App.toast('Логи недоступны', error.message, 'error');
        }
    }

    /** Список: текущие логи + сохранённые архивы (открываются на месте). */
    function renderFiles(files) {
        const box = el('logFiles');
        if (!box) return;
        clear(box);

        if (!files.length && !archiveFiles.length) {
            const hint = document.createElement('div');
            hint.className = 'muted';
            hint.style.fontSize = '12px';
            hint.textContent = 'Логов нет. Они появятся после запуска сервера.';
            box.appendChild(hint);
            return;
        }

        for (const file of files) {
            box.appendChild(fileRow({
                icon: kindIcon(file.kind),
                kind: file.kind,
                title: file.name,
                meta: `${formatBytes(file.sizeBytes)} · ${formatTime(file.modified)}`,
                active: file.name === current && !archiveName,
                onClick: () => open(file.name)
            }));
        }

        // Архивы логов: клик открывает содержимое прямо из zip.
        if (archiveFiles.length) {
            const caption = document.createElement('div');
            caption.className = 'muted';
            caption.style.fontSize = '11px';
            caption.style.margin = '8px 2px 4px';
            caption.textContent = 'Архивы логов (открываются внутри)';
            box.appendChild(caption);

            for (const file of archiveFiles) {
                box.appendChild(fileRow({
                    icon: '📦',
                    kind: 'archive',
                    title: file.fileName,
                    meta: `${formatBytes(file.sizeBytes)} · ${formatTime(file.createdAt)}`,
                    active: file.fileName === archiveName,
                    onClick: () => openArchive(file.fileName)
                }));
            }
        }
    }

    /** Одна строка списка файлов. */
    function fileRow({ icon, kind, title, meta, active, onClick }) {
        const row = document.createElement('button');
        row.type = 'button';
        row.className = 'log-item' + (active ? ' active' : '');
        row.dataset.name = title;

        const iconNode = document.createElement('span');
        iconNode.className = 'log-kind log-kind-' + (kind || 'log');
        iconNode.textContent = icon;

        const info = document.createElement('span');
        info.className = 'log-item-info';
        const name = document.createElement('b');
        name.textContent = title;
        const metaNode = document.createElement('span');
        metaNode.className = 'muted';
        metaNode.style.fontSize = '11px';
        metaNode.textContent = meta;
        info.appendChild(name);
        info.appendChild(metaNode);

        row.appendChild(iconNode);
        row.appendChild(info);
        row.addEventListener('click', onClick);
        return row;
    }

    /** Список файлов внутри открытого архива + кнопка возврата. */
    function renderArchiveEntries(entries) {
        const box = el('logFiles');
        if (!box) return;
        clear(box);

        const back = document.createElement('button');
        back.type = 'button';
        back.className = 'btn ghost sm';
        back.style.marginBottom = '8px';
        back.textContent = '← К списку логов';
        back.addEventListener('click', closeArchive);
        box.appendChild(back);

        const caption = document.createElement('div');
        caption.className = 'muted mono';
        caption.style.fontSize = '11px';
        caption.style.margin = '0 2px 6px';
        caption.textContent = archiveName;
        box.appendChild(caption);

        for (const name of entries) {
            box.appendChild(fileRow({
                icon: kindIcon(archiveKind(name)),
                kind: archiveKind(name),
                title: name,
                meta: '',
                active: name === archiveEntry,
                onClick: () => openArchiveEntry(name)
            }));
        }
    }

    function archiveKind(name) {
        const lower = (name || '').toLowerCase();
        if (lower.endsWith('.rpt')) return 'rpt';
        if (lower.endsWith('.adm')) return 'adm';
        if (lower.includes('script')) return 'script';
        if (lower.includes('error')) return 'error';
        return 'log';
    }

    function kindIcon(kind) {
        switch (kind) {
            case 'rpt': return '📄';
            case 'adm': return '🛡';
            case 'script': return '📜';
            case 'error': return '⚠';
            case 'archive': return '📦';
            default: return '📁';
        }
    }

    /* -------------------------------------------------- архивы логов (zip) */

    /** Открывает архив: показывает список файлов внутри, не распаковывая. */
    async function openArchive(name) {
        archiveName = name;
        archiveEntry = null;
        current = null;
        stopFollow();

        text('logTitle', name);

        try {
            const data = await App.api(apiPath(`/logs/archive/${encodeURIComponent(name)}`));
            const entries = data.entries ?? [];
            renderArchiveEntries(entries);

            if (!entries.length) {
                showContent(['В архиве нет файлов.'], true);
                return;
            }

            // Сразу открываем первый файл — чаще всего это RPT.
            const preferred = entries.find((n) => n.toLowerCase().endsWith('.rpt')) ?? entries[0];
            await openArchiveEntry(preferred);
        } catch (error) {
            App.toast('Не удалось открыть архив', error.message, 'error');
        }
    }

    /** Возвращает список к текущим логам сервера. */
    function closeArchive() {
        archiveName = null;
        archiveEntry = null;
        renderFiles(liveFiles);
        if (liveFiles.length) open(liveFiles[0].name);
    }

    /** Показывает файл внутри архива (читается из zip). */
    async function openArchiveEntry(name) {
        archiveEntry = name;
        lastLine = '';
        text('logTitle', `${archiveName} · ${name}`);

        for (const item of document.querySelectorAll('#logFiles .log-item')) {
            item.classList.toggle('active', item.dataset.name === name);
        }

        await refresh();
    }

    /* ------------------------------------------------------------- просмотр */

    async function open(name) {
        current = name;
        archiveName = null;
        archiveEntry = null;
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
        if (!current && !archiveEntry) return;

        const tail = Number(el('logTail')?.value ?? 500);
        const filter = (el('logFilter')?.value ?? '').trim();
        const box = el('logContent');

        try {
            const query = new URLSearchParams({ tail: String(tail) });
            if (filter) query.set('filter', filter);

            // Файл внутри архива читается из zip, обычный лог — с диска.
            const url = archiveEntry
                ? apiPath(`/logs/archive/${encodeURIComponent(archiveName)}/entry?` +
                    new URLSearchParams({ name: archiveEntry, tail: String(tail), ...(filter ? { filter } : {}) }))
                : apiPath(`/logs/${encodeURIComponent(current)}?${query}`);

            const data = await App.api(url);
            const lines = data.lines ?? [];

            if (!lines.length) {
                showContent([filter ? 'Ничего не найдено по фильтру.' : 'Файл пуст.'], true);
                lastLine = '';
                return;
            }

            showContent(lines, false);
            lastLine = lines[lines.length - 1];
            box.scrollTop = box.scrollHeight;
        } catch (error) {
            App.toast('Не удалось прочитать лог', error.message, 'error');
        }
    }

    /** Рисует строки в панели просмотра (или заглушку). */
    function showContent(lines, muted) {
        const box = el('logContent');
        if (!box) return;
        clear(box);

        for (const line of lines) {
            const row = document.createElement('span');
            row.className = muted ? 'log-line muted' : 'log-line' + lineClass(line);
            row.textContent = line;
            box.appendChild(row);
        }
    }

    /** Автообновление: дописывает только новые строки, не сбрасывая прокрутку. */
    async function follow() {
        // Архив неизменяем и живёт на диске: следить не за чем.
        if (archiveEntry || !current || el('logFollow')?.checked === false) return;

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
        archiveName = null;
        archiveEntry = null;
        lastLine = '';
        stopFollow();
    }

    return { bind, load, reset, setSettings, refresh };
})();

window.Logs = Logs;
