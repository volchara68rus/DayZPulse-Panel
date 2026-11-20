/* ============================================================
   DayZPulse — моды сервера: список, Workshop, локальные папки,
   установка/обновление, настройки.
   ============================================================ */
'use strict';

const Mods = (() => {
    const el = (id) => document.getElementById(id);

    let items = [];
    let bound = false;
    let trackerConfig = null;
    let installTimer = null;

    const apiPath = (path) => {
        const id = App.currentServerId;
        return id ? `/servers/${encodeURIComponent(id)}${path}` : path;
    };

    const clear = (node) => { while (node && node.firstChild) node.removeChild(node.firstChild); };

    /* --------------------------------------------------------------- загрузка */

    async function load() {
        if (!App.currentServerId) return;

        try {
            const data = await App.api(apiPath('/mods'));
            items = data.items ?? [];
            lastSettings = data.settings ?? {};
            render(data);

            // Прогресс установки приходит прямо в ответе /mods.
            if (data.busy || (data.progress > 0 && data.progress < 100)) {
                showProgress(true);
                setBar(el('modsFill'), el('modsPercent'), data.progress ?? 0);
                text('modsPhase', data.phase || 'подготовка');
                watchInstall();
            } else if (installTimer) {
                hideProgressSoon();
            }
        } catch {
            // Моды — не критичный раздел.
        }

        // Наш мод: обновляем подписи из состояния трекера.
        if (trackerConfig) {
            text('modConfigPath2', trackerConfig.modConfigPath || '—');
            text('modLaunchMods', trackerConfig.modLaunchMods || '—');
        }

        // Карточка нашего мода (версия, скачивание с сайта).
        await loadPanelMod();
    }

    function setTracker(config) {
        trackerConfig = config;
        text('modConfigPath2', config.modConfigPath || '—');
        text('modLaunchMods', config.modLaunchMods || '—');
    }

    function text(id, value) {
        const node = el(id);
        if (node) node.textContent = value ?? '—';
    }

    /* ------------------------------------------------ наш мод DayZPulse */

    let panelModTimer = null;

    async function loadPanelMod() {
        if (!App.currentServerId) return;

        try {
            const data = await App.api(apiPath('/panel-mod'));

            el('modsGate')?.classList.toggle('hidden', data.installed);
            el('modsOwnInfo')?.classList.toggle('hidden', !data.installed);

            text('panelModVersion', data.installedVersion || data.globalVersion || '—');
            text('panelModRemote', data.remoteVersion || '—');

            // Метка обновления в шапке карточки.
            document.querySelector('[data-dirty="panelmod"]')?.classList.toggle('on', data.installed && !data.upToDate && data.available);

            const status = el('panelModStatus');
            if (status) {
                if (!data.available) {
                    status.textContent = 'Сайт с модом недоступен: ' + data.baseUrl;
                } else if (data.upToDate) {
                    status.textContent = 'Установлена последняя версия.';
                } else {
                    status.textContent = `Доступно обновление: ${data.installedVersion || '—'} → ${data.remoteVersion}.`;
                }
            }

            const remoteHint = el('modsInstallHint');
            if (remoteHint && !data.installed) {
                remoteHint.textContent = data.remoteVersion
                    ? `На сайте доступна версия ${data.remoteVersion}.`
                    : 'Не удалось получить версию с сайта.';
            }

            // Прогресс скачивания.
            if (data.busy || (data.progress > 0 && data.progress < 100)) {
                showPanelModBar(true);
                paintPanelModBar(data.progress, data.phase);
                watchPanelMod();
            } else if (panelModTimer) {
                hidePanelModSoon();
            }
        } catch {
            // Карточка мода — не критична.
        }
    }

    async function installPanelMod() {
        const button = el('modsInstall') || el('panelModUpdate');
        if (button) button.disabled = true;

        showPanelModBar(true);
        watchPanelMod();

        try {
            const result = await App.api(apiPath('/panel-mod/install'), { method: 'POST' });
            App.toast(
                'Мод установлен',
                `Версия ${result.version || '?'}.` + (result.restartRequired ? ' Перезапустите сервер.' : ''),
                'info', 8000);
            await loadPanelMod();
        } catch (error) {
            App.toast('Не удалось установить мод', error.message, 'error', 9000);
            hidePanelModSoon();
        } finally {
            if (button) button.disabled = false;
        }
    }

    function watchPanelMod() {
        if (panelModTimer) return;
        panelModTimer = setInterval(loadPanelMod, 1500);
    }

    function hidePanelModSoon() {
        setTimeout(() => {
            showPanelModBar(false);
            if (panelModTimer) { clearInterval(panelModTimer); panelModTimer = null; }
        }, 2500);
    }

    function showPanelModBar(show) {
        el('panelModBar')?.classList.toggle('hidden', !show);
    }

    function paintPanelModBar(percent, phase) {
        const value = Math.max(0, Math.min(100, Math.round(percent ?? 0)));
        const fill = el('panelModFill');
        if (fill) fill.style.width = value + '%';
        text('panelModPercent', value + '%');
        text('panelModPhase', phase || 'подготовка');
    }

    function openPanelModUrl() {
        UI.prompt({
            title: 'Адрес сайта с модом',
            label: 'Базовый адрес (манифест и архив)',
            value: lastPanelModUrl || 'https://dayzpulse.ru',
            onOk: async (value) => {
                try {
                    const result = await App.api('/panel-mod/url', { method: 'PUT', body: { baseUrl: value } });
                    lastPanelModUrl = result.baseUrl;
                    App.toast('Сохранено', 'Адрес обновлён.', 'info');
                    await loadPanelMod();
                } catch (error) {
                    App.toast('Не удалось сохранить', error.message, 'error');
                }
            }
        });
    }

    let lastPanelModUrl = null;

    /* ---------------------------------------------------------------- список */

    function render(data) {
        const body = el('modRows');
        if (!body) return;
        clear(body);

        el('modsEmptyHint') && (el('modsEmptyHint').style.display = items.length ? 'none' : '');

        if (!items.length) {
            const tr = document.createElement('tr');
            const td = document.createElement('td');
            td.className = 'muted';
            td.colSpan = 8;
            td.textContent = 'Модов нет.';
            tr.appendChild(td);
            body.appendChild(tr);
            text('modLaunchMods', data.launchMods || '—');
            return;
        }

        for (const mod of items) {
            const tr = document.createElement('tr');
            tr.className = 'clickable-row';

            // Переключатель включения.
            const toggle = document.createElement('td');
            const sw = document.createElement('label');
            sw.className = 'sw';
            const input = document.createElement('input');
            input.type = 'checkbox';
            input.checked = !!mod.enabled;
            input.addEventListener('change', () => update(mod.id, { enabled: input.checked }));
            sw.appendChild(input);
            sw.appendChild(document.createElement('span'));
            toggle.appendChild(sw);
            tr.appendChild(toggle);

            // Название + ID.
            const name = document.createElement('td');
            const strong = document.createElement('b');
            strong.textContent = mod.name || mod.folder || '(без имени)';
            name.appendChild(strong);
            if (mod.workshopId) {
                const idLine = document.createElement('div');
                idLine.className = 'muted mono';
                idLine.style.fontSize = '11px';
                idLine.textContent = 'Workshop ' + mod.workshopId;
                name.appendChild(idLine);
            }
            tr.appendChild(name);

            tr.appendChild(cell(mod.source === 'local' ? 'локальный' : 'Workshop', 'muted'));

            // Тип мода: серверный (-serverMod) или клиентский (-mod).
            const type = document.createElement('td');
            const typeToggle = document.createElement('label');
            typeToggle.className = 'sw mod-type-sw';
            typeToggle.title = 'Включено — серверный мод (-serverMod=), иначе клиентский (-mod=)';
            const typeInput = document.createElement('input');
            typeInput.type = 'checkbox';
            typeInput.checked = !!mod.serverOnly;
            typeInput.addEventListener('change', () => update(mod.id, { serverOnly: typeInput.checked }));
            typeToggle.appendChild(typeInput);
            typeToggle.appendChild(document.createElement('span'));
            type.appendChild(typeToggle);
            const typeLabel = document.createElement('span');
            typeLabel.className = 'muted mod-type-label';
            typeLabel.textContent = mod.serverOnly ? 'серверный' : 'клиентский';
            type.appendChild(typeLabel);
            tr.appendChild(type);

            tr.appendChild(cell(mod.folder || '—', 'mono'));

            // Автообновление.
            const auto = document.createElement('td');
            const autoSw = document.createElement('label');
            autoSw.className = 'sw';
            const autoInput = document.createElement('input');
            autoInput.type = 'checkbox';
            autoInput.checked = !!mod.autoUpdate;
            autoInput.disabled = mod.source === 'local';
            autoInput.addEventListener('change', () => update(mod.id, { autoUpdate: autoInput.checked }));
            autoSw.appendChild(autoInput);
            autoSw.appendChild(document.createElement('span'));
            auto.appendChild(autoSw);
            tr.appendChild(auto);

            // Состояние.
            const state = document.createElement('td');
            const tag = document.createElement('span');
            const installed = !!mod.installed;
            tag.className = 'tag ' + (installed ? 'on' : 'off');
            tag.textContent = installed ? 'установлен' : 'не установлен';
            state.appendChild(tag);
            tr.appendChild(state);

            // Действия.
            const actions = document.createElement('td');
            actions.className = 'actions';
            const box = document.createElement('span');
            box.className = 'cell-actions';
            if (mod.source === 'workshop') {
                box.appendChild(installed
                    ? button('Обновить', () => installOne(mod.id))
                    : button('Установить', () => installOne(mod.id), 'primary'));
            }
            box.appendChild(button('Удалить', () => remove(mod), 'danger'));
            actions.appendChild(box);
            tr.appendChild(actions);

            body.appendChild(tr);
        }

        text('modLaunchMods', data.launchMods || '—');
        if (data.lastError) text('modsPhase', data.lastError);
    }

    function cell(value, className) {
        const td = document.createElement('td');
        if (className) td.className = className;
        td.textContent = value ?? '';
        return td;
    }

    function button(label, handler, kind) {
        const b = document.createElement('button');
        b.type = 'button';
        b.className = 'btn sm' + (kind ? ' ' + kind : '');
        b.textContent = label;
        b.addEventListener('click', (e) => { e.stopPropagation(); handler(); });
        return b;
    }

    /* --------------------------------------------------------------- действия */

    async function update(id, patch) {
        try {
            await App.api(apiPath('/mods/' + encodeURIComponent(id)), { method: 'PUT', body: patch });
            await load();
        } catch (error) {
            App.toast('Не удалось изменить мод', error.message, 'error');
        }
    }

    async function remove(mod) {
        UI.confirm(
            `Удалить мод «${mod.name}» из списка?`,
            () => confirmRemoveFiles(mod),
            'Удалить из списка');
    }

    function confirmRemoveFiles(mod) {
        UI.confirm(
            'Удалить также папку мода с диска? Это необратимо.',
            () => doRemove(mod, true),
            'Удалить и папку',
            {
                label: 'Оставить файлы',
                action: () => doRemove(mod, false)
            });
    }

    async function doRemove(mod, deleteFiles) {
        try {
            await App.api(apiPath(`/mods/${encodeURIComponent(mod.id)}?deleteFiles=${deleteFiles}`), { method: 'DELETE' });
            App.toast('Мод удалён', deleteFiles ? 'Папка мода стёрта с диска.' : 'Файлы мода оставлены.', 'info');
            await load();
        } catch (error) {
            App.toast('Не удалось удалить', error.message, 'error');
        }
    }

    async function installOne(id) {
        await runInstall({ ids: [id] });
    }

    async function installAll() {
        if (!items.length) {
            App.toast('Список пуст', 'Сначала добавьте моды.', 'warn');
            return;
        }
        await runInstall({});
    }

    async function runInstall(body) {
        showProgress(true);
        watchInstall();

        try {
            const result = await App.api(apiPath('/mods/install'), { method: 'POST', body });
            const failed = (result.results ?? []).filter((r) => !r.ok);

            if (result.note) {
                App.toast('Установка', result.note, 'info', 6000);
            } else if (failed.length) {
                // Показываем причину, иначе при ошибке качалки видно только «0/N».
                const first = failed[0];
                App.toast(
                    'Мод не установлен',
                    `${first.name || 'Мод'}: ${first.error || 'неизвестная ошибка'}` +
                        (failed.length > 1 ? ` (ещё ${failed.length - 1})` : ''),
                    'error', 12000);
            } else {
                App.toast('Установка', `Готово: ${result.installed}/${result.total}.`, 'info', 6000);
            }

            await load();
        } catch (error) {
            App.toast('Не удалось установить', error.message, 'error', 9000);
        } finally {
            hideProgressSoon();
        }
    }

    /** Следим за прогрессом установки, опрашивая /mods. */
    function watchInstall() {
        if (installTimer) return;
        installTimer = setInterval(() => { load(); }, 1500);
    }

    function hideProgressSoon() {
        setTimeout(() => {
            showProgress(false);
            if (installTimer) { clearInterval(installTimer); installTimer = null; }
        }, 2500);
    }

    function showProgress(show) {
        const card = el('modsProgressCard');
        if (card) card.style.display = show ? '' : 'none';
    }

    function setBar(fill, label, percent) {
        const value = Math.max(0, Math.min(100, Math.round(percent)));
        if (fill) fill.style.width = value + '%';
        if (label) label.textContent = value + '%';
    }

    /* ---------------------------------------------------------------- модалки */

    function openAdd() {
        el('modAddLink').value = '';
        el('modAddSearch').value = '';
        renderSearch([]);
        el('modAddModal')?.classList.add('open');
        el('modAddSearch')?.focus();
    }

    /** Поиск модов в Steam Workshop по названию. */
    async function searchWorkshop() {
        const query = el('modAddSearch').value.trim();
        if (query.length < 2) {
            App.toast('Введите название', 'Минимум два символа.', 'warn');
            return;
        }

        const box = el('modSearchResults');
        box.textContent = 'Поиск…';

        try {
            const data = await App.api(apiPath('/mods/search?q=' + encodeURIComponent(query)));
            renderSearch(data.items ?? []);
        } catch (error) {
            box.textContent = '';
            App.toast('Поиск не удался', error.message, 'error');
        }
    }

    function renderSearch(items) {
        const box = el('modSearchResults');
        if (!box) return;
        box.textContent = '';

        if (!items.length) {
            return;
        }

        for (const item of items) {
            const row = document.createElement('div');
            row.className = 'mod-search-item';

            if (item.previewUrl) {
                const img = document.createElement('img');
                img.src = item.previewUrl;
                img.alt = '';
                img.loading = 'lazy';
                row.appendChild(img);
            }

            const info = document.createElement('div');
            info.className = 'mod-search-info';
            const title = document.createElement('b');
            title.textContent = item.title;
            const id = document.createElement('span');
            id.className = 'muted mono';
            id.style.fontSize = '11px';
            id.textContent = 'Workshop ' + item.id;
            info.appendChild(title);
            info.appendChild(id);
            row.appendChild(info);

            const add = document.createElement('button');
            add.type = 'button';
            add.className = 'btn primary sm';
            add.textContent = 'Добавить';
            add.addEventListener('click', () => addByWorkshop(item.id, item.title, add));
            row.appendChild(add);

            box.appendChild(row);
        }
    }

    /** Добавляет мод по Workshop ID в список. */
    async function addByWorkshop(workshopId, name, button) {
        if (button) { button.disabled = true; button.textContent = 'Добавление…'; }

        try {
            await App.api(apiPath('/mods'), {
                method: 'POST',
                body: { source: 'workshop', workshop: workshopId, name }
            });
            App.toast('Добавлено', `«${name}» в списке. Нажмите «Установить».`, 'info');
            if (button) { button.textContent = 'В списке'; }
            await load();
        } catch (error) {
            if (button) { button.disabled = false; button.textContent = 'Добавить'; }
            App.toast('Не удалось добавить', error.message, 'error', 8000);
        }
    }

    async function saveAdd() {
        const link = el('modAddLink').value.trim();

        if (!link) {
            App.toast('Пусто', 'Вставьте ссылку или ID.', 'warn');
            return;
        }

        try {
            await App.api(apiPath('/mods'), { method: 'POST', body: { source: 'workshop', workshop: link } });
            el('modAddModal')?.classList.remove('open');
            App.toast('Добавлено', 'Мод в списке. Нажмите «Установить».', 'info');
            await load();
        } catch (error) {
            App.toast('Не удалось добавить', error.message, 'error', 8000);
        }
    }

    function openLocal() {
        el('modLocalPath').value = '';
        el('modLocalName').value = '';
        el('modLocalModal')?.classList.add('open');
    }

    async function saveLocal() {
        const path = el('modLocalPath').value.trim();
        const name = el('modLocalName').value.trim();

        if (!path) {
            App.toast('Укажите папку', 'Выберите папку мода на диске.', 'warn');
            return;
        }

        try {
            await App.api(apiPath('/mods'), { method: 'POST', body: { source: 'local', path, name } });
            el('modLocalModal')?.classList.remove('open');
            App.toast('Добавлено', 'Локальный мод подключён.', 'info');
            await load();
        } catch (error) {
            App.toast('Не удалось добавить', error.message, 'error', 8000);
        }
    }

    function openSettings() {
        const s = lastSettings ?? {};
        el('modSetUpdateOnStart').checked = s.updateOnStart !== false;
        el('modSetAutoDaily').checked = s.autoUpdateDaily !== false;
        el('modSetCleanup').checked = s.cleanupAfterDownload !== false;
        el('modSettingsModal')?.classList.add('open');
    }

    async function saveSettings() {
        try {
            await App.api(apiPath('/mods/settings'), {
                method: 'PUT',
                body: {
                    updateOnStart: el('modSetUpdateOnStart').checked,
                    autoUpdateDaily: el('modSetAutoDaily').checked,
                    cleanupAfterDownload: el('modSetCleanup').checked
                }
            });
            el('modSettingsModal')?.classList.remove('open');
            App.toast('Сохранено', 'Настройки модов обновлены.', 'info');
        } catch (error) {
            App.toast('Не удалось сохранить', error.message, 'error');
        }
    }

    let lastSettings = null;

    /* --------------------------------------------------------------- привязка */

    function bind() {
        if (bound) return;
        bound = true;

        el('modsAdd')?.addEventListener('click', openAdd);
        el('modsAddLocal')?.addEventListener('click', openLocal);
        el('modsInstallAll')?.addEventListener('click', installAll);

        // Наш мод DayZPulse.
        el('modsInstall')?.addEventListener('click', installPanelMod);
        el('panelModUpdate')?.addEventListener('click', installPanelMod);
        el('panelModUrlBtn')?.addEventListener('click', openPanelModUrl);
        el('modsSettingsBtn')?.addEventListener('click', openSettings);

        el('modAddX')?.addEventListener('click', () => el('modAddModal')?.classList.remove('open'));
        el('modAddCancel')?.addEventListener('click', () => el('modAddModal')?.classList.remove('open'));
        el('modAddSave')?.addEventListener('click', saveAdd);
        el('modAddSearchBtn')?.addEventListener('click', searchWorkshop);
        el('modAddSearch')?.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') { e.preventDefault(); searchWorkshop(); }
        });
        el('modAddModal')?.addEventListener('click', (e) => { if (e.target.id === 'modAddModal') el('modAddModal').classList.remove('open'); });

        el('modLocalX')?.addEventListener('click', () => el('modLocalModal')?.classList.remove('open'));
        el('modLocalCancel')?.addEventListener('click', () => el('modLocalModal')?.classList.remove('open'));
        el('modLocalSave')?.addEventListener('click', saveLocal);
        el('modLocalBrowse')?.addEventListener('click', () => {
            if (typeof FolderPicker !== 'undefined') {
                FolderPicker.open('modLocalPath');
            }
        });
        el('modLocalModal')?.addEventListener('click', (e) => { if (e.target.id === 'modLocalModal') el('modLocalModal').classList.remove('open'); });

        el('modSettingsX')?.addEventListener('click', () => el('modSettingsModal')?.classList.remove('open'));
        el('modSettingsSave')?.addEventListener('click', saveSettings);
        el('modSettingsModal')?.addEventListener('click', (e) => { if (e.target.id === 'modSettingsModal') el('modSettingsModal').classList.remove('open'); });
    }

    /** Сохранить настройки, пришедшие в ответе /mods. */
    function setSettings(settings) {
        lastSettings = settings ?? {};
    }

    return { bind, load, setTracker, setSettings };
})();

window.Mods = Mods;
