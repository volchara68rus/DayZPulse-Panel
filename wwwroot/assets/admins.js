/* ============================================================
   DayZPulse — администраторы панели: список, добавление, права.
   Работает только для владельца панели.
   ============================================================ */
'use strict';

const Admins = (() => {
    const el = (id) => document.getElementById(id);

    // Разделы, которые можно выдать администратору (совпадают с ключами VIEWS).
    const SECTION_LIST = [
        ['dashboard', 'Дашборд'],
        ['console', 'Консоль'],
        ['configs', 'Конфигурация'],
        ['mods', 'Моды'],
        ['players', 'Игроки'],
        ['livemap', 'Живая карта'],
        ['chat', 'Чат'],
        ['anticheat', 'Античит'],
        ['backups', 'Резервные копии'],
        ['schedule', 'Расписание'],
        ['service', 'Служба'],
        ['settings', 'Настройки'],
        ['security', 'Безопасность']
    ];

    let editingId = null;       // null = создание
    let adminsCache = [];
    let bound = false;

    /* --------------------------------------------------------------- утилиты */

    const text = (id, value) => {
        const node = el(id);
        if (node) node.textContent = value ?? '—';
    };

    function clear(node) {
        while (node && node.firstChild) node.removeChild(node.firstChild);
    }

    /* --------------------------------------------------------------- список */

    async function open() {
        el('adminsModal')?.classList.add('open');
        await load();
    }

    function close() {
        el('adminsModal')?.classList.remove('open');
    }

    async function load() {
        try {
            const data = await App.api('/admins');
            adminsCache = data.admins ?? [];
            render(data.owner);
        } catch (error) {
            App.toast('Не удалось загрузить администраторов', error.message, 'error');
        }
    }

    function render(owner) {
        const body = el('adminRows');
        if (!body) return;
        clear(body);

        // Строка владельца — только для чтения.
        if (owner) {
            const tr = document.createElement('tr');
            tr.appendChild(cell(owner.username + ' — владелец', 'strong'));
            tr.appendChild(cell('все разделы', 'muted'));
            tr.appendChild(cell('все серверы', 'muted'));
            tr.appendChild(onlineCell(owner.online));
            tr.appendChild(cell('', ''));

            const actions = document.createElement('td');
            tr.appendChild(actions);
            body.appendChild(tr);
        }

        if (!adminsCache.length) {
            const tr = document.createElement('tr');
            const td = cell('Администраторов пока нет.', 'muted');
            td.colSpan = 5;
            tr.appendChild(td);
            body.appendChild(tr);
            return;
        }

        for (const admin of adminsCache) {
            const tr = document.createElement('tr');

            const name = document.createElement('td');
            name.textContent = admin.username;
            const state = document.createElement('span');
            state.className = 'tag ' + (admin.enabled ? 'on' : 'off');
            state.style.marginLeft = '8px';
            state.textContent = admin.enabled ? 'активен' : 'выключен';
            name.appendChild(state);
            tr.appendChild(name);

            tr.appendChild(cell(
                admin.allSections
                    ? 'все'
                    : (admin.sections?.length ? admin.sections.map(sectionLabel).join(', ') : 'нет'),
                admin.allSections ? '' : 'muted'));

            tr.appendChild(cell(
                admin.allServers
                    ? 'все'
                    : (admin.servers?.length ? admin.servers.join(', ') : 'нет'),
                admin.allServers ? '' : 'muted'));

            tr.appendChild(onlineCell(admin.online));

            const actions = document.createElement('td');
            actions.className = 'actions';
            const box = document.createElement('span');
            box.className = 'cell-actions';
            box.appendChild(button('Изменить', () => openEdit(admin), ''));
            box.appendChild(button('Удалить', () => remove(admin), 'danger'));
            actions.appendChild(box);
            tr.appendChild(actions);

            body.appendChild(tr);
        }
    }

    function onlineCell(online) {
        const td = document.createElement('td');
        const tag = document.createElement('span');
        tag.className = 'tag ' + (online ? 'on' : 'off');
        tag.textContent = online ? 'в сети' : 'не в сети';
        td.appendChild(tag);
        return td;
    }

    function sectionLabel(key) {
        const found = SECTION_LIST.find(([k]) => k === key);
        return found ? found[1] : key;
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
        b.addEventListener('click', handler);
        return b;
    }

    /* -------------------------------------------------------------- редактор */

    function openCreate() {
        editingId = null;
        text('adminEditTitle', 'Новый администратор');
        el('adminUsername').value = '';
        el('adminUsername').disabled = false;
        el('adminPassword').value = '';
        el('adminPassHint').style.display = 'none';
        el('adminAllSections').checked = false;
        el('adminAllServers').checked = true;
        renderSections([]);
        renderServers([], true);
        text('adminEditNote', '');
        el('adminEditModal')?.classList.add('open');
    }

    function openEdit(admin) {
        editingId = admin.id;
        text('adminEditTitle', 'Изменить: ' + admin.username);
        el('adminUsername').value = admin.username;
        el('adminUsername').disabled = true;
        el('adminPassword').value = '';
        el('adminPassHint').style.display = '';
        el('adminAllSections').checked = !!admin.allSections;
        el('adminAllServers').checked = !!admin.allServers;
        renderSections(admin.sections ?? []);
        renderServers(admin.servers ?? [], admin.allServers);
        text('adminEditNote', admin.online ? 'Сейчас в сети' : '');
        el('adminEditModal')?.classList.add('open');
    }

    function closeEdit() {
        el('adminEditModal')?.classList.remove('open');
    }

    function renderSections(selected) {
        const wrap = el('adminSections');
        if (!wrap) return;
        clear(wrap);

        const all = el('adminAllSections').checked;
        wrap.style.display = all ? 'none' : 'flex';

        for (const [key, label] of SECTION_LIST) {
            const chip = document.createElement('label');
            chip.className = 'admin-chip';
            const input = document.createElement('input');
            input.type = 'checkbox';
            input.checked = selected.includes(key);
            input.dataset.section = key;
            chip.appendChild(input);
            chip.appendChild(document.createTextNode(label));
            wrap.appendChild(chip);
        }
    }

    function renderServers(selected, all) {
        const wrap = el('adminServers');
        if (!wrap) return;
        clear(wrap);
        wrap.style.display = all ? 'none' : '';

        for (const server of App.servers ?? []) {
            const label = document.createElement('label');
            label.className = 'admin-chip';
            const input = document.createElement('input');
            input.type = 'checkbox';
            input.checked = selected.includes(server.id);
            input.dataset.server = server.id;
            label.appendChild(input);
            label.appendChild(document.createTextNode(server.name || server.id));
            wrap.appendChild(label);
        }

        if (!(App.servers ?? []).length) {
            wrap.textContent = 'Серверов нет.';
            wrap.className = 'muted';
        }
    }

    async function save() {
        const username = el('adminUsername').value.trim();
        const password = el('adminPassword').value;
        const allSections = el('adminAllSections').checked;
        const allServers = el('adminAllServers').checked;

        const sections = [...document.querySelectorAll('#adminSections input[data-section]')]
            .filter((i) => i.checked).map((i) => i.dataset.section);
        const servers = [...document.querySelectorAll('#adminServers input[data-server]')]
            .filter((i) => i.checked).map((i) => i.dataset.server);

        try {
            if (editingId) {
                await App.api('/admins/' + encodeURIComponent(editingId), {
                    method: 'PUT',
                    body: {
                        password: password || null,
                        enabled: true,
                        allSections,
                        sections,
                        allServers,
                        servers
                    }
                });
                App.toast('Сохранено', 'Права администратора обновлены.', 'info');
            } else {
                if (!username || !password) {
                    App.toast('Заполните поля', 'Нужны имя и пароль.', 'warn');
                    return;
                }

                await App.api('/admins', {
                    method: 'POST',
                    body: { username, password, allSections, sections, allServers, servers }
                });
                App.toast('Администратор создан', `«${username}» может входить в панель.`, 'info');
            }

            closeEdit();
            await load();
        } catch (error) {
            App.toast('Не удалось сохранить', error.message, 'error', 8000);
        }
    }

    function remove(admin) {
        UI.confirm(
            `Удалить администратора «${admin.username}»? Его сессия перестанет работать.`,
            async () => {
                try {
                    await App.api('/admins/' + encodeURIComponent(admin.id), { method: 'DELETE' });
                    App.toast('Удалено', 'Администратор удалён.', 'info');
                    await load();
                } catch (error) {
                    App.toast('Не удалось удалить', error.message, 'error');
                }
            },
            'Удалить');
    }

    /* --------------------------------------------------------------- привязка */

    function bind() {
        if (bound) return;
        bound = true;

        el('btnAdmins')?.addEventListener('click', open);
        el('adminsX')?.addEventListener('click', close);
        el('adminsModal')?.addEventListener('click', (e) => { if (e.target.id === 'adminsModal') close(); });

        el('adminAddBtn')?.addEventListener('click', openCreate);
        el('adminEditX')?.addEventListener('click', closeEdit);
        el('adminEditCancel')?.addEventListener('click', closeEdit);
        el('adminEditModal')?.addEventListener('click', (e) => { if (e.target.id === 'adminEditModal') closeEdit(); });
        el('adminEditSave')?.addEventListener('click', save);

        el('adminAllSections')?.addEventListener('change', () => {
            renderSections([]);
        });
        el('adminAllServers')?.addEventListener('change', () => {
            renderServers([], el('adminAllServers').checked);
        });
    }

    return { bind, open };
})();

window.Admins = Admins;
