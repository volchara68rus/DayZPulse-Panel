/* ============================================================
   DayZPulse — раздел «Игроки»: живые данные от мода, база,
   килфид, чат, нарушения и очередь команд.
   ============================================================ */
'use strict';

const Players = (() => {
    const LIVE_INTERVAL = 2000;
    const ONLINE_WINDOW = 30;

    let liveTimer = null;
    let livePlayers = [];
    let liveVehicles = [];
    let liveObjects = [];
    let selectedPlayerId = null;
    let databaseRows = [];
    let databaseFilter = { search: '', watchOnly: false, sort: 'lastSeen' };
    let currentPlayerId = null;
    let currentPlayerOnline = false;
    // Игрок, чья карточка сейчас открыта (для живой подгрузки событий).
    let currentCardId = null;
    // Номер последнего запроса событий: применяем только ответ на самый свежий,
    // иначе устаревший ответ мог бы перерисовать список поверх нового.
    let eventsReqSeq = 0;
    // Отпечаток отрисованного списка. null = список ещё не рисовали,
    // поэтому пустой список тоже должен отрисоваться (заглушкой).
    let eventsSignature = null;

    /* ------------------------------------------------------------ утилиты */

    const el = (id) => document.getElementById(id);

    /** Путь API трекера в контексте активного сервера. */
    function apiPath(path) {
        const id = App.currentServerId;
        return id ? `/servers/${encodeURIComponent(id)}${path}` : path;
    }

    /** Сбрасывает состояние при переключении сервера. */
    function reset() {
        livePlayers = [];
        liveVehicles = [];
        liveObjects = [];
        selectedPlayerId = null;
        databaseRows = [];

        for (const node of ['liveRows', 'dbRows', 'killRows', 'violationRows', 'commandRowsLogs']) {
            const element = el(node);
            if (element) clear(element);
        }

        const chat = el('chatLog');
        if (chat) clear(chat);

        const mapPlayers = el('liveMapPlayers');
        if (mapPlayers) clear(mapPlayers);

        const mapVehicles = el('liveMapVehicles');
        if (mapVehicles) clear(mapVehicles);

        const mapObjects = el('liveMapObjects');
        if (mapObjects) clear(mapObjects);

        const mapTrack = el('mapTrackOverlay');
        if (mapTrack) clear(mapTrack);

        // Трек, показанный на карте, тоже сбрасываем.
        playerTrack = null;
        updateTrackControls();

        // Карта у каждого сервера своя: сбрасываем метаданные и тайлы,
        // но сам объект карты переиспользуем (иначе на одном элементе
        // копились бы обработчики мыши).
        mapInfo = null;
        mapReady = false;
        liveMap?.reset();

        loadConfig();
        loadLive();
    }

    function text(id, value) {
        const node = el(id);
        if (node) node.textContent = value ?? '—';
    }

    function fmtDuration(seconds) {
        if (!seconds || seconds < 0) return '—';
        const s = Math.floor(seconds);
        const d = Math.floor(s / 86400);
        const h = Math.floor((s % 86400) / 3600);
        const m = Math.floor((s % 3600) / 60);
        if (d > 0) return `${d} д ${h} ч`;
        if (h > 0) return `${h} ч ${m} мин`;
        if (m > 0) return `${m} мин`;
        return `${s} с`;
    }

    function fmtTime(value) {
        if (!value) return '—';
        const date = new Date(value);
        return isNaN(date) ? '—' : date.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', year: '2-digit', hour: '2-digit', minute: '2-digit' });
    }

    function fmtCoord(value) {
        return Number(value ?? 0).toFixed(0);
    }

    function healthClass(player) {
        if (!player.alive) return 'off';
        const ratio = player.maxHealth > 0 ? player.health / player.maxHealth : 0;
        if (ratio > 0.75) return 'on';
        if (ratio > 0.35) return 'warn';
        return 'off';
    }

    function cell(value, className) {
        const td = document.createElement('td');
        if (className) td.className = className;
        td.textContent = value ?? '';
        return td;
    }

    /**
     * Ячейка SteamID64: если это настоящий SteamID (17 цифр), id ведёт
     * ссылкой на профиль Steam. Иначе показываем как обычный текст.
     */
    function steamCell(value) {
        const text = value ?? '';
        if (!/^\d{17}$/.test(text)) return cell(text, 'mono');

        const td = document.createElement('td');
        td.className = 'mono';
        const link = document.createElement('a');
        link.className = 'steam-link';
        link.href = `https://steamcommunity.com/profiles/${text}`;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.title = 'Открыть профиль в Steam';
        link.textContent = text;
        // Ссылка не должна открывать карточку игрока по клику на строку.
        link.addEventListener('click', (event) => event.stopPropagation());
        td.appendChild(link);
        return td;
    }

    function clear(node) {
        while (node.firstChild) node.removeChild(node.firstChild);
    }

    function command(action, args) {
        return App.api(apiPath('/tracker/commands'), { method: 'POST', body: { action, args: args ?? {} } })
            .then((result) => {
                App.toast('Команда отправлена', describeCommand(action, args), 'info');
                loadCommands();
                return result;
            })
            .catch((error) => {
                App.toast('Команда не отправлена', error.message, 'error');
                throw error;
            });
    }

    function describeCommand(action, args) {
        const names = {
            ping: 'проверка связи', spawn: 'создание объекта', give: 'выдача предмета',
            preset: 'выдача набора', delete: 'удаление объекта', weather: 'смена погоды',
            time: 'установка времени', teleport: 'телепортация', heal: 'лечение',
            damage: 'урон', kill: 'убийство', message: 'личное сообщение'
        };
        const parts = [names[action] ?? action];
        if (args) {
            for (const [key, value] of Object.entries(args)) {
                if (key !== 'id') parts.push(`${key}=${value}`);
            }
        }
        return parts.join(' · ');
    }

    /* ------------------------------------------------------------- живые */

    async function loadLive() {
        try {
            const data = await App.api(apiPath('/tracker/players'));
            livePlayers = data.players ?? [];
            // Машины и объекты приходят отдельным запросом карты, чтобы не грузить снимок дважды.
            try {
                const map = await App.api(apiPath('/tracker/map'));
                liveVehicles = map.vehicles ?? [];
                liveObjects = map.worldObjects ?? [];
            } catch {
                liveVehicles = [];
                liveObjects = [];
            }

            // «Онлайн» — все подключённые игроки, включая тех, кто ждёт респавн:
            // мод присылает их в снимке, но у них alive=false. Раньше счётчик
            // считал только живых, поэтому при одном подключённом показывал 0.
            const onlineCount = livePlayers.length;

            text('playersConnection', data.connected
                ? `мод на связи · ${onlineCount} в сети`
                : 'нет данных от мода');

            text('ovPlayersOnline', onlineCount);
            text('statPlayersOnline', onlineCount);
            text('statPlayersOnlineCard', onlineCount);
            text('statPlayersTotal', livePlayers.length);
            text('acPlayers', livePlayers.length);
            App.setPlayerCount(onlineCount);

            const server = data.server ?? {};
            text('liveVehicles', server.vehicles ?? 0);
            text('liveInfected', `заражённых: ${server.infected ?? 0}`);
            text('liveObjects', server.worldObjects ?? 0);
            text('mapState', data.connected ? 'мод на связи' : 'нет данных');
            const mapDot = el('mapDot');
            if (mapDot) mapDot.className = 'dot ' + (data.connected ? 'online' : '');

            renderLive();
            renderPlayerPickers();
            updateLiveMap();

            // Карточка игрока открыта — обновляем её «Последние события» на
            // лету, иначе они появлялись бы только после повторного открытия.
            const card = el('playerModal');
            if (currentCardId && card?.classList.contains('open')) {
                renderPlayerEvents(currentCardId);
            }

            // Показан трек игрока — подтягиваем новые точки, чтобы линия
            // на карте «дорисовывалась» по мере движения (не чаще раза в 5 с).
            refreshShownTrack();
        } catch (error) {
            text('playersConnection', 'ошибка: ' + error.message);
        }
    }

    let trackRefreshAt = 0;

    /** Обновляет точки уже показанного трека, если игрок ещё в сети. */
    async function refreshShownTrack() {
        if (!playerTrack || !playerTrack.points.length) return;

        const now = Date.now();
        if (now - trackRefreshAt < 5000) return;
        trackRefreshAt = now;

        const online = livePlayers.some((p) => p.id === playerTrack.id);
        if (!online) return;

        try {
            const data = await App.api(
                apiPath(`/tracker/players/${encodeURIComponent(playerTrack.id)}/track?points=300`));
            if (!playerTrack || data.id !== playerTrack.id || !data.count) return;

            playerTrack.points = data.points;
            playerTrack.count = data.count;
            updateTrackControls();
            drawMapOverlay();
        } catch {
            // Обновление трека — не критично.
        }
    }

    function renderLive() {
        const body = el('liveRows');
        if (!body) return;
        clear(body);

        if (!livePlayers.length) {
            const tr = document.createElement('tr');
            const td = cell('Игроков онлайн нет.', 'muted');
            td.colSpan = 8;
            tr.appendChild(td);
            body.appendChild(tr);
            return;
        }

        for (const player of livePlayers) {
            const tr = document.createElement('tr');
            tr.className = 'clickable-row';
            if (!player.alive) tr.style.opacity = '.6';

            // Клик по всей строке открывает карточку игрока.
            tr.addEventListener('click', () => openPlayer(player.id));

            const name = document.createElement('td');
            name.textContent = player.name || '(без имени)';

            // Метка списка наблюдения (★) — как в базе игроков.
            if (player.watch) {
                const star = document.createElement('span');
                star.className = 'watch-mark';
                star.textContent = '★';
                star.title = 'В списке наблюдения';
                name.prepend(star);
            }

            if (player.note) {
                const note = document.createElement('div');
                note.className = 'muted';
                note.style.fontSize = '11.5px';
                note.textContent = player.note;
                name.appendChild(note);
            }

            tr.appendChild(name);

            tr.appendChild(steamCell(player.plainId || player.id));

            // Пинг есть у мода, но точнее он от RCON; показываем, если известен.
            tr.appendChild(cell(player.ping > 0 ? `${player.ping} ms` : '—', 'mono'));

            // IP-адрес приходит только от RCON.
            tr.appendChild(cell(player.ip || '—', 'mono muted'));

            const health = document.createElement('td');
            const tag = document.createElement('span');
            tag.className = 'tag ' + healthClass(player);
            tag.textContent = `${Math.round(player.health)} / ${Math.round(player.maxHealth)}`;
            health.appendChild(tag);
            tr.appendChild(health);

            tr.appendChild(cell(`${fmtCoord(player.pos.x)} : ${fmtCoord(player.pos.z)}`, 'mono'));

            tr.appendChild(cell(player.stale ? 'устарело' : 'сейчас', player.stale ? 'muted' : ''));

            // Действия прямо в строке; клик по ним не открывает карточку.
            const actions = document.createElement('td');
            actions.className = 'actions';
            const actionsBox = document.createElement('span');
            actionsBox.className = 'cell-actions';
            actionsBox.addEventListener('click', (event) => event.stopPropagation());
            actionsBox.appendChild(actionButton('Трек', () => showPlayerTrackById(player.id, player.name)));
            actionsBox.appendChild(actionButton('Лечить', () => command('heal', { id: player.id })));
            actionsBox.appendChild(actionButton('Телепорт', () => promptTeleport(player)));
            actionsBox.appendChild(actionButton('Кикнуть', () => kickPlayer(player), 'danger'));
            actions.appendChild(actionsBox);
            tr.appendChild(actions);

            body.appendChild(tr);
        }
    }

    function actionButton(label, handler, kind) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'btn sm' + (kind ? ' ' + kind : '');
        button.textContent = label;
        button.addEventListener('click', handler);
        return button;
    }

    function promptMessage(player) {
        UI.prompt({
            title: 'Личное сообщение',
            label: `Текст для «${player.name}»`,
            placeholder: 'Сообщение игроку',
            onOk: (value) => command('message', { id: player.id, text: value })
        });
    }

    function promptTeleport(player) {
        UI.prompt({
            title: 'Телепортация',
            label: 'Координаты «X Z» (метры)',
            placeholder: '7500 7500',
            value: `${fmtCoord(player.pos.x)} ${fmtCoord(player.pos.z)}`,
            onOk: (value) => {
                const [x, z] = value.split(/[\s,;]+/).map(Number);
                if (!Number.isFinite(x) || !Number.isFinite(z)) {
                    App.toast('Неверные координаты', 'Укажите два числа: X и Z.', 'error');
                    return;
                }
                return command('teleport', { id: player.id, x, z });
            }
        });
    }

    function kickPlayer(player) {
        UI.confirm(`Кикнуть «${player.name}» с сервера?`, async () => {
            try {
                await App.api(apiPath(`/tracker/players/${encodeURIComponent(player.id)}/kick`), {
                    method: 'POST', body: {}
                });
                App.toast('Кик отправлен', `Игрок «${player.name}» отключён.`, 'info');
            } catch (error) {
                App.toast('Не удалось кикнуть', error.message, 'error', 8000);
            }
        }, 'Кикнуть');
    }

    function banPlayer(player) {
        UI.confirm(
            `Забанить «${player.name}»? Бан блокирует вход по GUID.`,
            () => promptBan(player),
            'Забанить');
    }

    function promptBan(player) {
        UI.prompt({
            title: `Бан «${player.name}»`,
            label: 'Длительность в минутах (0 — навсегда)',
            placeholder: '0',
            value: '0',
            onOk: async (value) => {
                const minutes = Number((value || '0').trim()) || 0;
                try {
                    await App.api(apiPath(`/tracker/players/${encodeURIComponent(player.id)}/ban`), {
                        method: 'POST', body: { minutes }
                    });
                    App.toast('Бан выдан', minutes === 0 ? 'Бессрочно.' : `На ${minutes} мин.`, 'info');
                } catch (error) {
                    App.toast('Не удалось забанить', error.message, 'error', 8000);
                }
            }
        });
    }

    /* ------------------------------------------------------ выбор игрока */

    function renderPlayerPickers() {
        for (const id of ['spawnPlayerTarget', 'presetPlayerTarget']) {
            const select = el(id);
            if (!select) continue;

            const previous = select.value;
            clear(select);

            const empty = document.createElement('option');
            empty.value = '';
            empty.textContent = livePlayers.length ? '— выберите игрока —' : 'нет игроков онлайн';
            select.appendChild(empty);

            for (const player of livePlayers) {
                const option = document.createElement('option');
                option.value = player.id;
                option.textContent = `${player.name} (${player.plainId || player.id})`;
                if (player.id === previous) option.selected = true;
                select.appendChild(option);
            }
        }
    }

    /* --------------------------------------------------------- база игроков */

    async function loadDatabase() {
        try {
            const params = new URLSearchParams();
            if (databaseFilter.search) params.set('search', databaseFilter.search);
            if (databaseFilter.watchOnly) params.set('watch', 'true');

            databaseRows = await App.api(apiPath('/tracker/database?' + params.toString()));

            const sorters = {
                lastSeen: (a, b) => new Date(b.lastSeen) - new Date(a.lastSeen),
                playtime: (a, b) => b.playtimeSeconds - a.playtimeSeconds,
                kills: (a, b) => b.kills - a.kills,
                kd: (a, b) => b.kd - a.kd,
                sessions: (a, b) => b.sessions - a.sessions
            };
            databaseRows.sort(sorters[databaseFilter.sort] ?? sorters.lastSeen);

            text('dbTotal', databaseRows.length);
            text('dbOnline', databaseRows.filter((p) => p.online).length);
            text('dbWatch', databaseRows.filter((p) => p.watch).length);
            text('dbBanned', '—');

            renderDatabase();
        } catch (error) {
            App.toast('База игроков недоступна', error.message, 'error');
        }
    }

    function renderDatabase() {
        const body = el('dbRows');
        if (!body) return;
        clear(body);

        if (!databaseRows.length) {
            const tr = document.createElement('tr');
            const td = cell('Записей нет — данных от мода пока не было.', 'muted');
            td.colSpan = 10;
            tr.appendChild(td);
            body.appendChild(tr);
            return;
        }

        for (const player of databaseRows) {
            const tr = document.createElement('tr');
            tr.className = 'clickable-row';
            if (player.online) tr.style.background = '#12281a55';

            // Клик по всей строке открывает профиль игрока.
            tr.addEventListener('click', () => openPlayer(player.id));

            const watch = document.createElement('td');
            const star = document.createElement('button');
            star.type = 'button';
            star.className = 'star' + (player.watch ? ' active' : '');
            star.textContent = player.watch ? '★' : '☆';
            star.title = 'Список наблюдения';
            star.addEventListener('click', (event) => { event.stopPropagation(); toggleWatch(player); });
            watch.appendChild(star);
            tr.appendChild(watch);

            const name = document.createElement('td');
            name.textContent = player.name || '(без имени)';
            if (player.online) {
                const badge = document.createElement('span');
                badge.className = 'tag on';
                badge.style.marginLeft = '8px';
                badge.textContent = 'в сети';
                name.appendChild(badge);
            }
            if (player.aliases?.length > 1) {
                const alias = document.createElement('div');
                alias.className = 'muted mono';
                alias.style.fontSize = '11px';
                alias.textContent = 'также известен как: ' + player.aliases.filter((a) => a !== player.name).join(', ');
                name.appendChild(alias);
            }
            if (player.note) {
                const note = document.createElement('div');
                note.className = 'muted';
                note.style.fontSize = '11.5px';
                note.textContent = player.note;
                name.appendChild(note);
            }
            tr.appendChild(name);

            tr.appendChild(steamCell(player.plainId || player.id));
            tr.appendChild(cell(fmtTime(player.lastSeen)));
            tr.appendChild(cell(player.sessions));
            tr.appendChild(cell(fmtDuration(player.playtimeSeconds)));
            tr.appendChild(cell(player.kills));
            tr.appendChild(cell(player.deaths));
            tr.appendChild(cell(player.kd));

            // Трек доступен и для оффлайн-игрока: он строится из базы.
            const actions = document.createElement('td');
            actions.className = 'actions';
            const trackButton = actionButton('Трек', () => showPlayerTrackById(player.id, player.name));
            trackButton.addEventListener('click', (event) => event.stopPropagation());
            actions.appendChild(trackButton);
            tr.appendChild(actions);

            body.appendChild(tr);
        }
    }

    async function toggleWatch(player) {
        try {
            await App.api(apiPath(`/tracker/database/${encodeURIComponent(player.id)}/note`), {
                method: 'POST',
                body: { note: player.note ?? '', watch: !player.watch }
            });
            player.watch = !player.watch;
            renderDatabase();
        } catch (error) {
            App.toast('Не удалось обновить метку', error.message, 'error');
        }
    }

    /* ------------------------------------------------------------ карточка */

    async function openPlayer(id) {
        try {
            const data = await App.api(apiPath(`/tracker/players/${encodeURIComponent(id)}`));
            const live = data.live;
            const record = data.record ?? {};
            const name = live?.name || record.name || id;

            currentCardId = id;
            // Новый игрок — прошлый список событий не наш.
            eventsSignature = null;

            text('playerModalName', name);

            // Ссылка на профиль Steam: у онлайн-игрока — из мода, иначе из базы.
            const steamId = live?.plainId || record.plainId || '';
            const steam = el('playerModalSteam');
            text('playerModalId', steamId || id);
            if (steam) {
                // SteamID64 — 17 цифр. Для BI id ссылка не имеет смысла.
                const isSteam = /^\d{17}$/.test(steamId);
                steam.href = isSteam ? `https://steamcommunity.com/profiles/${steamId}` : '#';
                steam.classList.toggle('disabled', !isSteam);
                steam.title = isSteam ? 'Открыть профиль в Steam' : 'Профиль Steam недоступен';
            }

            // Статус игрока в шапке.
            const stateTag = el('playerModalStateTag');
            if (stateTag) {
                stateTag.style.display = '';
                stateTag.className = 'tag ' + (live ? (live.alive ? 'on' : 'warn') : 'off');
                stateTag.textContent = live ? (live.alive ? 'в сети' : 'мёртв') : 'не в сети';
            }

            const stats = el('playerModalStats');
            clear(stats);
            const rows = [
                ['Статус', live ? (live.alive ? 'в сети, жив' : 'в сети, мёртв') : 'не в сети'],
                ['Здоровье', live ? `${Math.round(live.health)} / ${Math.round(live.maxHealth)}` : '—'],
                ['Позиция', live ? `${fmtCoord(live.pos.x)} : ${fmtCoord(live.pos.z)}` : '—'],
                ['BI id', record.id ?? id],
                ['Первый вход', fmtTime(record.firstSeen)],
                ['Последний вход', fmtTime(record.lastSeen)],
                ['Сессий', record.sessions ?? 0],
                ['Наиграно', fmtDuration(record.playtimeSeconds)],
                ['Убийств / смертей', `${record.kills ?? 0} / ${record.deaths ?? 0}`],
                ['K/D', record.kd ?? 0],
                ['Ники', record.aliases?.join(', ') ?? '—']
            ];

            for (const [key, value] of rows) {
                const item = document.createElement('div');
                item.className = 'pm-fact';

                const k = document.createElement('span');
                k.className = 'k';
                k.textContent = key;

                const v = document.createElement('b');
                v.textContent = value ?? '—';

                item.appendChild(k);
                item.appendChild(v);
                stats.appendChild(item);
            }

            const mods = el('playerModalMods');
            clear(mods);
            for (const mod of live?.mods ?? []) {
                const tag = document.createElement('span');
                tag.className = 'tag info';
                tag.textContent = mod;
                mods.appendChild(tag);
            }
            if (!(live?.mods ?? []).length) {
                mods.textContent = 'модификаторов нет';
                mods.className = 'muted';
            } else {
                mods.className = 'row';
            }

            renderSurvivalStats(live?.stats);
            await renderPlayerTrack(id, name);
            await renderPlayerEvents(id);

            const note = el('playerModalNote');
            note.value = record.note ?? '';
            el('playerModalWatch').checked = !!record.watch;
            el('playerModalSaveNote').onclick = async () => {
                try {
                    await App.api(apiPath(`/tracker/database/${encodeURIComponent(id)}/note`), {
                        method: 'POST',
                        body: { note: note.value, watch: el('playerModalWatch').checked }
                    });
                    App.toast('Сохранено', 'Заметка обновлена.', 'info');
                    loadDatabase();
                } catch (error) {
                    App.toast('Не удалось сохранить', error.message, 'error');
                }
            };

            const actions = el('playerModalQuick');
            clear(actions);

            // Инвентарь теперь отдельным окном — кнопка есть всегда.
            actions.appendChild(actionButton('🎒 Инвентарь', () => openInventory(id, name, !!live)));

            if (live) {
                actions.appendChild(actionButton('Лечить', () => command('heal', { id })));
                actions.appendChild(actionButton('Сказать', () => promptMessage({ id, name })));
                actions.appendChild(actionButton('Телепорт', () => promptTeleport({ id, pos: live.pos })));
                actions.appendChild(actionButton('Кикнуть', () => kickPlayer({ id, name }), 'danger'));
                actions.appendChild(actionButton('Забанить', () => banPlayer({ id, name }), 'danger'));
            } else {
                const hint = document.createElement('span');
                hint.className = 'muted';
                hint.style.fontSize = '12.5px';
                hint.textContent = 'Игрок не в сети — действия доступны только онлайн, инвентарь — из последнего снимка.';
                actions.appendChild(hint);
            }

            el('playerModal').classList.add('open');
        } catch (error) {
            App.toast('Игрок не найден', error.message, 'error');
        }
    }

    /**
     * Открывает отдельное окно инвентаря. Выдача предметов доступна
     * только онлайн-игрокам, оффлайн — просмотр последнего снимка.
     */
    async function openInventory(id, name, online) {
        currentPlayerId = id;
        currentPlayerOnline = !!online;
        paletteDragTarget = null;

        text('playerInvWho', name || id);

        await ensureCatalog();
        updatePaletteFilterHint();
        renderPalette(el('invSearch')?.value ?? '');
        await loadInventory(id);

        el('playerInvModal')?.classList.add('open');

        // У онлайн-игрока просим свежий снимок у мода: без этого окно
        // показывало бы пустоту, пока мод сам не пришлёт инвентарь.
        if (currentPlayerOnline) {
            requestInventory(id);
        }
    }

    /** Просит мод прислать свежий снимок инвентаря, затем перечитывает его. */
    function requestInventory(id) {
        App.api(apiPath(`/tracker/inventory/${encodeURIComponent(id)}/refresh`), { method: 'POST' })
            .then(() => {
                // Мод отвечает на следующем опросе (до пары секунд).
                setTimeout(() => {
                    if (currentPlayerId === id) loadInventory(id);
                }, 2500);
                setTimeout(() => {
                    if (currentPlayerId === id) loadInventory(id);
                }, 5000);
            })
            .catch(() => { /* мод может быть не на связи — просто нет снимка */ });
    }

    /* ------------------------------------------------------------ инвентарь */

    // Слоты экипировки: слева и справа от фигуры — аксессуары, которые
    // визуально не привязаны к зонам тела.
    const SLOT_LAYOUT = {
        left: [
            ['Mask', 'Маска', '😷'], ['Eyewear', 'Очки', '🕶'], ['Armband', 'Нарукавник', '🎗']
        ],
        right: [
            ['Back', 'Рюкзак', '🎒'], ['Gloves', 'Перчатки', '🧤']
        ]
    };

    // Зоны силуэта: формируют «стоящего человечка».
    // Ключи — это имена слотов движка (Slot_<key>), а не человеческие подписи.
    const BODY_ROWS = [
        ['Headgear', 'Голова', 'head'], ['Body', 'Торс', 'torso'], ['Vest', 'Жилет', 'vest'],
        ['Hips', 'Пояс', 'hips'], ['Legs', 'Ноги', 'legs'], ['Feet', 'Стопы', 'feet']
    ];

    /**
     * SVG-силуэт выжившего (вид спереди): графическая «кукла» для
     * накидывания экипировки. Зоны слотов накладываются поверх рисунка.
     */
    const CHARACTER_SVG = `
<svg class="inv-silhouette" viewBox="0 0 200 320" preserveAspectRatio="xMidYMid meet" aria-hidden="true" focusable="false">
  <defs>
    <linearGradient id="sil-fill" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#1e2c3a"/>
      <stop offset="100%" stop-color="#141c24"/>
    </linearGradient>
    <linearGradient id="sil-fill-soft" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#223140"/>
      <stop offset="100%" stop-color="#18222c"/>
    </linearGradient>
  </defs>
  <g class="sil-body" fill="url(#sil-fill)" stroke="#3b4e63" stroke-width="1.6" stroke-linejoin="round">
    <!-- Голова и шея -->
    <ellipse cx="100" cy="32" rx="18" ry="22"/>
    <rect x="92" y="52" width="16" height="10" rx="3" fill="url(#sil-fill-soft)"/>
    <!-- Торс и плечи -->
    <path d="M100 62
             C72 64, 52 74, 46 92
             L42 138
             C42 144, 48 150, 56 150
             L144 150
             C152 150, 158 144, 158 138
             L154 92
             C148 74, 128 64, 100 62 Z"/>
    <!-- Руки (вдоль корпуса, под перчатки-слоты) -->
    <path d="M46 92 C36 110, 32 140, 34 170 L40 198 C42 206, 50 208, 54 202 L58 150 Z" fill="url(#sil-fill-soft)" stroke="#36485c"/>
    <path d="M154 92 C164 110, 168 140, 166 170 L160 198 C158 206, 150 208, 146 202 L142 150 Z" fill="url(#sil-fill-soft)" stroke="#36485c"/>
    <!-- Пояс / бёдра -->
    <path d="M56 150 L144 150 L138 184 C136 188, 130 190, 122 190 L78 190 C70 190, 64 188, 62 184 Z" fill="url(#sil-fill-soft)"/>
    <!-- Ноги -->
    <path d="M78 190 L64 268 C62 276, 66 282, 74 284 L90 284 C96 284, 100 278, 100 270 L100 190 Z"/>
    <path d="M122 190 L136 268 C138 276, 134 282, 126 284 L110 284 C104 284, 100 278, 100 270 L100 190 Z"/>
    <!-- Стопы -->
    <path d="M58 282 L92 282 L94 300 C94 306, 88 310, 80 310 L60 310 C52 310, 50 304, 52 298 Z"/>
    <path d="M142 282 L108 282 L106 300 C106 306, 112 310, 120 310 L140 310 C148 310, 150 304, 148 298 Z"/>
  </g>
  <!-- Лёгкие контуры-разделители зон для читаемости -->
  <g class="sil-lines" fill="none" stroke="#4b6278" stroke-width="1" stroke-dasharray="3 3" opacity=".7">
    <line x1="58" y1="100" x2="142" y2="100"/>
    <line x1="56" y1="150" x2="144" y2="150"/>
    <line x1="78" y1="190" x2="122" y2="190"/>
    <line x1="62" y1="268" x2="138" y2="268"/>
  </g>
</svg>`.trim();

    /** Добавляет SVG-силуэт в контейнер фигурки, если его ещё нет. */
    function ensureCharacterFigure(figure) {
        if (!figure || figure.querySelector('.inv-silhouette')) return;
        figure.insertAdjacentHTML('afterbegin', CHARACTER_SVG);
    }

    /**
     * Встроенный набор популярных предметов с группами — используется, пока
     * не загружен полный каталог сервера (или если types.xml недоступен).
     * Формат: [class, label, icon, group].
     */
    const ITEM_FALLBACK = [
        ['Bandage', 'Бинт', '🩹', 'Medical'], ['SalineBagIV', 'Физраствор', '💉', 'Medical'],
        ['Morphine', 'Морфин', '💉', 'Medical'], ['TetracyclineAntibiotics', 'Антибиотик', '💊', 'Medical'],
        ['Apple', 'Яблоко', '🍎', 'Food'], ['BakedBeansCan', 'Бобы', '🥫', 'Food'],
        ['WaterBottle', 'Вода', '🍼', 'Food'], ['Canteen', 'Фляга', '🫗', 'Food'],
        ['AK101', 'АК-101', '🔫', 'Weapon'], ['M4A1', 'M4A1', '🔫', 'Weapon'],
        ['Mosin9130', 'Мосин', '🔫', 'Weapon'], ['Ammo_762x39', 'Патроны 7.62×39', '🎯', 'Ammo'],
        ['Hatchet', 'Топор', '🪓', 'Tool'], ['Crowbar', 'Лом', '🪛', 'Tool'],
        ['HuntingBackpack', 'Рюкзак охотника', '🎒', 'Back'], ['PlateCarrierVest', 'Бронежилет', '🦺', 'Vest'],
        ['GorkaHelmet', 'Шлем Горка', '🪖', 'Head'], ['HikingBoots_Black', 'Ботинки', '🥾', 'Feet'],
        ['TacticalGloves_Black', 'Тактические перчатки', '🧤', 'Gloves'], ['GorkaPants_Summer', 'Штаны Горка', '👖', 'Legs'],
        ['GorkaEJacket_Summer', 'Куртка Горка', '👕', 'Body'], ['BalaclavaMask_Black', 'Балаклава', '😷', 'Mask'],
        ['Compass', 'Компас', '🧭', 'Gear'], ['Flashlight', 'Фонарь', '🔦', 'Gear'],
        ['Matchbox', 'Спички', '🔥', 'Gear']
    ];

    // Полный каталог сервера (из types.xml) + встроенный запас.
    let catalogItems = [];
    let catalogLoaded = false;

    // Человеческие названия групп (для подписей категорий).
    // Ключи — это либо группа предмета (Head, Body, …), либо имя слота
    // движка (Headgear, Armband, …): обе используются для подписей в UI.
    const GROUP_LABELS = {
        Head: 'Головные уборы', Headgear: 'Головные уборы', Mask: 'Маски',
        Eyewear: 'Очки', Neck: 'Шея', Armband: 'Нарукавники',
        Vest: 'Разгрузки и бронежилеты', Body: 'Верхняя одежда', Back: 'Рюкзаки',
        Hips: 'Пояса', Legs: 'Штаны', Feet: 'Обувь', Gloves: 'Перчатки',
        Weapon: 'Оружие', Ammo: 'Патроны и магазины', WeaponParts: 'Обвес оружия',
        Medical: 'Медицина', Food: 'Еда и вода', Tool: 'Инструменты',
        Base: 'База и хранение', Gear: 'Снаряжение', Vehicle: 'Запчасти авто',
        VehicleObj: 'Транспорт', Animal: 'Животные', Explosive: 'Взрывчатка', Other: 'Прочее'
    };

    // Какие ГРУППЫ предметов допустимы в каждом СЛОТЕ экипировки
    // (ключи — имена слотов движка).
    const SLOT_GROUPS = {
        Headgear: ['Head'], Mask: ['Mask'], Eyewear: ['Eyewear'], Armband: ['Gear'],
        Vest: ['Vest'], Body: ['Body'], Back: ['Back'], Hips: ['Gear', 'Hips'],
        Legs: ['Legs'], Feet: ['Feet'], Gloves: ['Gloves']
    };

    // Носимая экипировка: её нельзя класть в карго «на себя».
    const WEARABLE_GROUPS = ['Head', 'Mask', 'Eyewear', 'Vest', 'Body', 'Back', 'Legs', 'Feet', 'Gloves'];

    /** Совместима ли группа предмета с целевым слотом ('' = карго). */
    function groupFitsSlot(group, slot) {
        if (!slot) {
            return !WEARABLE_GROUPS.includes(group);
        }

        const allowed = SLOT_GROUPS[slot];
        return allowed ? allowed.includes(group) : true;
    }

    /** Загружает каталог предметов сервера один раз. */
    async function ensureCatalog() {
        if (catalogLoaded) return;

        try {
            const data = await App.api(apiPath('/tracker/catalog'));
            catalogItems = (data.items ?? []).map((i) => [i.cls, i.cls, i.icon || '🔹', i.group || 'Other']);
        } catch {
            catalogItems = [];
        }

        if (catalogItems.length === 0) {
            catalogItems = ITEM_FALLBACK.slice();
        }

        catalogLoaded = true;
    }

    /** Иконка предмета по classname. */
    function iconFor(cls) {
        const c = (cls || '').toLowerCase();
        if (/bandage|rag/.test(c)) return '🩹';
        if (/saline|blood|iv|epine|morphine/.test(c)) return '💉';
        if (/antibiotic|tetra|charcoal|painkiller|vitamin|pill|tablet/.test(c)) return '💊';
        if (/can|bean|sardine|tuna|peach|spaghet|cereal|apple|pear|food|meat/.test(c)) return '🥫';
        if (/water|bottle|canteen|soda|cola|spite|flask/.test(c)) return '🍼';
        if (/ammo|mag|bullet/.test(c)) return '🎯';
        if (/ak|m4|mosin|sks|winchester|shotgun|pistol|glock|cz75|makarov|rifle|weapon|revolver/.test(c)) return '🔫';
        if (/knife|hatchet|axe|crowbar|hammer|shovel|wrench|screwdriver|saw|drill|pliers/.test(c)) return '🔪';
        if (/helmet|gorka|nbc_head|cap|ushanka/.test(c)) return '🪖';
        if (/vest|plate|carrier|press/.test(c)) return '🦺';
        if (/boot|shoe|sneaker/.test(c)) return '🥾';
        if (/glove/.test(c)) return '🧤';
        if (/backpack|bag|hunting/.test(c)) return '🎒';
        if (/pants|shorts|legs/.test(c)) return '👖';
        if (/jacket|shirt|hoodie|torso|gorka|tactical/.test(c)) return '👕';
        if (/map|compass|gps|radio|binocular|flashlight|torch|lighter|match/.test(c)) return '📻';
        if (/tent|shelter/.test(c)) return '⛺';
        if (/barrel|canister/.test(c)) return '🛢';
        if (/crate|chest|case|box/.test(c)) return '📦';
        return '🔹';
    }

    /** Слот экипировки (боковые колонки). */
    function buildSlot(slotKey, label, icon, item) {
        const node = document.createElement('div');
        node.className = 'inv-slot' + (item ? ' filled' : '');
        node.dataset.slot = slotKey;
        node.title = label;

        const ic = document.createElement('span');
        ic.className = 'icon';
        ic.textContent = item ? iconFor(item.class) : icon;

        const nm = document.createElement('span');
        nm.className = 'nm';
        nm.textContent = item ? item.class : label;

        node.appendChild(ic);
        node.appendChild(nm);
        return node;
    }

    /** Крупная зона силуэта. */
    function buildBodyPart(slotKey, label, shape, item) {
        const node = document.createElement('div');
        node.className = 'bp bp-' + shape + (item ? ' filled' : '');
        node.dataset.slot = slotKey;
        node.title = label;

        if (item) {
            const ic = document.createElement('span'); ic.className = 'icon'; ic.textContent = iconFor(item.class);
            const nm = document.createElement('span'); nm.className = 'nm'; nm.textContent = item.class;
            node.appendChild(ic); node.appendChild(nm);
        } else {
            const nm = document.createElement('span'); nm.className = 'nm'; nm.textContent = label;
            node.appendChild(nm);
        }

        return node;
    }

    /** Строка предмета в карго. */
    function buildCargoItem(item) {
        const node = document.createElement('div');
        node.className = 'inv-item';
        node.title = item.class;

        const ic = document.createElement('span'); ic.className = 'icon'; ic.textContent = iconFor(item.class);
        const nm = document.createElement('span'); nm.className = 'nm'; nm.textContent = item.class;
        node.appendChild(ic); node.appendChild(nm);

        if (item.qty && item.qty > 1) {
            const qty = document.createElement('span'); qty.className = 'qty'; qty.textContent = '×' + item.qty;
            node.appendChild(qty);
        }

        return node;
    }

    let lastInventory = null;

    /** Загружает и рисует инвентарь игрока. */
    async function loadInventory(playerId) {
        try {
            const data = await App.api(apiPath(`/tracker/inventory/${encodeURIComponent(playerId)}`));
            lastInventory = data.available ? normalizeInventory(data.items) : null;
        } catch {
            lastInventory = null;
        }

        renderInventory(lastInventory);
        updateInventoryControls();
    }

    /** Показывает/скрывает управление инвентарём в зависимости от онлайна. */
    function updateInventoryControls() {
        const refresh = el('playerModalInvRefresh');
        if (refresh) refresh.classList.toggle('hidden', !currentPlayerOnline);

        const palette = el('playerInvPalette');
        if (palette) palette.classList.toggle('hidden', !currentPlayerOnline);

        const hint = el('playerInvHint');
        if (hint) {
            hint.classList.toggle('hidden', currentPlayerOnline);
            hint.textContent = 'Игрок не в сети — можно смотреть, но не выдавать предметы.';
        }
    }

    /** Приводит сырой JSON мода к форме {slots, cargo, hands}. */
    function normalizeInventory(raw) {
        const slots = {};
        const cargo = [];
        let hands = null;

        if (!raw || typeof raw !== 'object') {
            return { slots, cargo, hands };
        }

        hands = raw.hands ?? null;

        for (const entry of raw.slots ?? []) {
            if (entry && entry.slot) slots[entry.slot] = entry.item ?? null;
        }
        // Содержимое контейнера может быть вложенным (рюкзак → рюкзак внутри).
        // Раскладываем рекурсивно, чтобы показать предметы из карманов и сумок.
        const flatten = (list, owner) => {
            for (const entry of list ?? []) {
                const item = entry?.item ?? entry;
                if (!item || !item.class) continue;

                cargo.push({
                    class: item.class,
                    qty: item.qty ?? 1,
                    health: item.health,
                    owner: owner || entry?.owner || ''
                });

                if (Array.isArray(item.cargo)) {
                    flatten(item.cargo, item.class);
                }
            }
        };

        flatten(raw.cargo, '');

        // Содержимое надетого (рюкзак/жилет/карманы) лежит внутри item слота.
        for (const [slot, item] of Object.entries(slots)) {
            if (item && Array.isArray(item.cargo)) {
                flatten(item.cargo, item.class || slot);
            }
        }

        return { slots, cargo, hands };
    }

    /** Рисует инвентарь: силуэт, слоты и карго. */
    function renderInventory(inv) {
        const left = el('playerInvLeft');
        const right = el('playerInvRight');
        const body = el('playerInvBody');
        const cargoList = el('playerInvCargo');
        if (!left || !right || !body || !cargoList) return;

        // Графический силуэт персонажа для накидывания экипировки.
        ensureCharacterFigure(body.closest('.inv-figure'));

        clear(left); clear(right); clear(body); clear(cargoList);

        const slots = inv?.slots ?? {};

        for (const [key, label, shape] of BODY_ROWS) {
            body.appendChild(buildBodyPart(key, label, shape, slots[key] ?? null));
        }
        for (const [key, label, icon] of SLOT_LAYOUT.left) {
            left.appendChild(buildSlot(key, label, icon, slots[key] ?? null));
        }
        for (const [key, label, icon] of SLOT_LAYOUT.right) {
            right.appendChild(buildSlot(key, label, icon, slots[key] ?? null));
        }

        if (!inv || (inv.cargo.length === 0 && !inv.hands)) {
            const hint = document.createElement('div');
            hint.className = 'muted';
            hint.style.fontSize = '12px';
            hint.textContent = inv
                ? 'Пусто.'
                : (currentPlayerOnline
                    ? 'Мод ещё не присылал инвентарь. Нажмите «Обновить».'
                    : 'Инвентарь этого игрока ещё не сохранялся.');
            cargoList.appendChild(hint);
        } else {
            if (inv.hands) cargoList.appendChild(buildCargoItem(inv.hands));
            for (const item of inv.cargo) cargoList.appendChild(buildCargoItem(item));
        }

        bindInventoryDropZones();
    }

    /** Строит палитру предметов (с фильтром по поиску). */
    /**
     * Рисует палитру предметов, сгруппированную по категориям.
     * При активном слоте (dragTarget) показывает только совместимые предметы.
     */
    let paletteDragTarget = '';   // '' = карго, 'Head' = конкретный слот, null = без фильтра
    let draggedClass = '';        // класс предмета, который сейчас перетаскивают

    function renderPalette(filter = '') {
        const list = el('playerInvPaletteList');
        if (!list) return;

        clear(list);
        const term = filter.trim().toLowerCase();
        const target = paletteDragTarget;

        // Отбираем предметы: по совместимости со слотом и по строке поиска.
        const items = catalogItems.filter(([cls, label, , group]) => {
            if (target !== null && !groupFitsSlot(group, target)) return false;
            if (term && !(cls.toLowerCase().includes(term) || (label || '').toLowerCase().includes(term))) return false;
            return true;
        });

        if (items.length === 0) {
            const hint = document.createElement('div');
            hint.className = 'muted';
            hint.style.fontSize = '12px';
            hint.style.padding = '4px';
            hint.textContent = catalogLoaded
                ? 'Ничего не подходит под этот слот.'
                : 'Загрузка каталога…';
            list.appendChild(hint);
            return;
        }

        // Группируем по категории — с заголовками и встроенной сортировкой.
        const groups = new Map();
        for (const item of items) {
            const group = item[3] || 'Other';
            if (!groups.has(group)) groups.set(group, []);
            groups.get(group).push(item);
        }

        for (const [group, groupItems] of groups) {
            const head = document.createElement('div');
            head.className = 'inv-palette-group';
            head.textContent = GROUP_LABELS[group] ?? group;
            list.appendChild(head);

            const row = document.createElement('div');
            row.className = 'inv-palette-row';

            // Ограничиваем отрисовку, чтобы не рисовать тысячи чипов разом.
            for (const [cls, label, icon] of groupItems.slice(0, 200)) {
                row.appendChild(buildPaletteChip(cls, label, icon));
            }

            list.appendChild(row);
        }
    }

    /** Чип предмета в палитре (перетаскивается). */
    function buildPaletteChip(cls, label, icon) {
        const chip = document.createElement('span');
        chip.className = 'inv-chip';
        chip.draggable = currentPlayerOnline;
        chip.dataset.cls = cls;
        chip.title = cls;

        const ic = document.createElement('span'); ic.className = 'icon'; ic.textContent = icon || '🔹';
        const nm = document.createElement('span'); nm.textContent = label && label !== cls ? label : cls;
        chip.appendChild(ic); chip.appendChild(nm);

        if (!currentPlayerOnline) {
            chip.classList.add('disabled');
            chip.title = 'Игрок не в сети — выдача недоступна';
            return chip;
        }

        chip.addEventListener('dragstart', (event) => {
            event.dataTransfer.setData('text/plain', cls);
            event.dataTransfer.effectAllowed = 'copy';
            draggedClass = cls;
            chip.classList.add('dragging');
            el('playerInvStage')?.classList.add('drop-active');
        });
        chip.addEventListener('dragend', () => {
            draggedClass = '';
            chip.classList.remove('dragging');
            el('playerInvStage')?.classList.remove('drop-active');
            for (const n of document.querySelectorAll('.slot-hover')) n.classList.remove('slot-hover');
            for (const n of document.querySelectorAll('.slot-bad')) n.classList.remove('slot-bad');
        });

        return chip;
    }

    /** Разрешает перетаскивание предметов на слоты и карго. */
    function bindInventoryDropZones() {
        const areas = document.querySelectorAll('#playerInvStage [data-slot], #playerInvCargo');

        for (const area of areas) {
            // Карго-список — постоянный элемент, а сливка карточки вызывает
            // перерисовку много раз. Без этой защиты обработчики копились бы,
            // и один drop выдавал бы предмет несколько раз (дубли).
            if (area.dataset.dzBound === '1') {
                continue;
            }
            area.dataset.dzBound = '1';

            const slot = area.dataset.slot || '';

            // Клик по слоту фильтрует палитру под этот слот (повторный — сброс).
            area.addEventListener('click', () => {
                if (!currentPlayerOnline) return;
                paletteDragTarget = paletteDragTarget === slot ? null : slot;
                updatePaletteFilterHint();
                renderPalette(el('invSearch')?.value ?? '');
            });

            area.addEventListener('dragover', (event) => {
                const item = catalogItems.find((i) => i[0] === draggedClass);
                const fits = !item || groupFitsSlot(item[3], slot);

                if (!fits) {
                    event.dataTransfer.dropEffect = 'none';
                    area.classList.add('slot-bad');
                    return;
                }

                event.preventDefault();
                event.dataTransfer.dropEffect = 'copy';
                area.classList.add('slot-hover');
            });

            area.addEventListener('dragleave', () => {
                area.classList.remove('slot-hover');
                area.classList.remove('slot-bad');
            });

            area.addEventListener('drop', (event) => {
                event.preventDefault();
                area.classList.remove('slot-hover');
                area.classList.remove('slot-bad');

                const cls = event.dataTransfer.getData('text/plain') || draggedClass;
                if (!cls) return;

                const item = catalogItems.find((i) => i[0] === cls);
                if (item && !groupFitsSlot(item[3], slot)) {
                    App.toast('Не подходит', `«${cls}» нельзя надеть в этот слот.`, 'warn', 5000);
                    return;
                }

                giveItemToPlayer(cls, slot);
            });
        }
    }

    /** Показывает подсказку, под какой слот отфильтрована палитра. */
    function updatePaletteFilterHint() {
        const hint = el('invFilterHint');
        if (!hint) return;

        if (paletteDragTarget === null) {
            hint.classList.add('hidden');
            return;
        }

        hint.classList.remove('hidden');
        const label = paletteDragTarget
            ? 'Только для слота: ' + (GROUP_LABELS[paletteDragTarget] ?? paletteDragTarget)
            : 'Только предметы в карманы';
        hint.textContent = label + ' · нажмите слот ещё раз, чтобы показать все';
    }

    /** Выдаёт предмет игроку через мод (в слот или в инвентарь). */
    function giveItemToPlayer(cls, slot) {
        if (!currentPlayerId) return;

        const args = { id: currentPlayerId, class: cls };
        if (slot) args.slot = slot;

        command('give', args)
            .then(() => setTimeout(() => loadInventory(currentPlayerId), 1500))
            .catch(() => { /* ошибка уже показана */ });
    }

    function renderSurvivalStats(stats) {
        const box = el('playerModalSurvival');
        clear(box);
        if (!stats) {
            box.textContent = 'Данных нет.';
            box.className = 'muted';
            return;
        }        box.className = '';

        const definitions = [
            ['water', 'Вода'], ['energy', 'Еда'], ['heatComfort', 'Тепло'],
            ['stamina', 'Станина'], ['wet', 'Сырость'], ['toxicity', 'Токсины'],
            ['health', 'Здоровье'], ['blood', 'Кровь'], ['shock', 'Шок']
        ];

        const grid = document.createElement('div');
        grid.className = 'stat-grid';

        for (const [key, label] of definitions) {
            const value = stats[key];
            if (!value || typeof value !== 'object') continue;

            const current = value.v ?? 0;
            const max = value.max || value.n || 1;
            const ratio = key === 'heatComfort'
                ? Math.min(1, Math.abs(current) / (max * 0.3 || 1))
                : Math.max(0, Math.min(1, current / (max || 1)));

            const item = document.createElement('div');
            item.className = 'stat-cell';

            const title = document.createElement('div');
            title.className = 'stat-cell-label';
            title.textContent = label;

            const number = document.createElement('div');
            number.className = 'stat-cell-value';
            number.textContent = Number(current).toFixed(1);

            const bar = document.createElement('div');
            bar.className = 'bar';
            const fill = document.createElement('i');
            fill.style.width = (ratio * 100).toFixed(0) + '%';
            fill.className = ratio < 0.25 ? 'crit' : ratio < 0.5 ? 'warn' : '';
            bar.appendChild(fill);

            item.appendChild(title);
            item.appendChild(number);
            item.appendChild(bar);
            grid.appendChild(item);
        }

        const flags = document.createElement('div');
        flags.className = 'row';
        flags.style.marginTop = '10px';
        const flagList = [
            ['alive', 'жив'], ['bleeding', 'кровотечение'], ['unconscious', 'без сознания'], ['restrained', 'связан']
        ];
        for (const [key, label] of flagList) {
            if (stats[key] !== true) continue;
            const tag = document.createElement('span');
            tag.className = 'tag ' + (key === 'alive' ? 'on' : 'warn');
            tag.textContent = label;
            flags.appendChild(tag);
        }

        if (stats.temperature) {
            const temp = document.createElement('span');
            temp.className = 'tag info';
            temp.textContent = `температура ${Number(stats.temperature.v).toFixed(1)} °C`;
            flags.appendChild(temp);
        }

        box.appendChild(grid);
        box.appendChild(flags);
    }

    /**
     * Карточка игрока: вместо встроенного графика — короткая сводка по треку
     * и кнопка «Показать на карте». Сам трек перенесён на живую карту, где он
     * рисуется в реальных координатах (см. showTrackOnMap).
     */
    async function renderPlayerTrack(id, name) {
        const box = el('playerModalTrack');
        clear(box);
        try {
            const data = await App.api(apiPath(`/tracker/players/${encodeURIComponent(id)}/track?points=300`));
            const track = { id, name: name || id, points: data.points ?? [], count: data.count ?? 0, visible: true };
            lastTrack = track;

            if (!track.count) {
                const hint = document.createElement('span');
                hint.className = 'muted';
                hint.textContent = 'Трек пуст — данных пока недостаточно.';
                box.appendChild(hint);
                return;
            }

            const xs = track.points.map((p) => p.x);
            const zs = track.points.map((p) => p.z);
            const minX = Math.min(...xs), maxX = Math.max(...xs);
            const minZ = Math.min(...zs), maxZ = Math.max(...zs);
            const span = Math.max(maxX - minX, maxZ - minZ);

            const facts = document.createElement('div');
            facts.className = 'pm-facts';
            const rows = [
                ['Откуда', `${fmtCoord(minX)} : ${fmtCoord(minZ)}`],
                ['Куда', `${fmtCoord(maxX)} : ${fmtCoord(maxZ)}`],
                ['Разброс', `${fmtCoord(span)} м`]
            ];
            for (const [key, value] of rows) {
                const item = document.createElement('div');
                item.className = 'pm-fact';
                const k = document.createElement('span');
                k.className = 'k';
                k.textContent = key;
                const v = document.createElement('b');
                v.textContent = value;
                item.appendChild(k);
                item.appendChild(v);
                facts.appendChild(item);
            }
            box.appendChild(facts);

            const show = document.createElement('button');
            show.type = 'button';
            show.className = 'btn primary sm';
            show.textContent = '🗺 Показать трек на карте';
            show.addEventListener('click', () => showTrackOnMap(track));
            box.appendChild(show);
        } catch (error) {
            const hint = document.createElement('span');
            hint.className = 'muted';
            hint.textContent = 'Не удалось получить трек: ' + error.message;
            box.appendChild(hint);
        }
    }

    /* -------------------------------------------- трек на живой карте */

    // Последний загруженный трек (для кнопки «Показать на карте» из карточки).
    let lastTrack = null;

    /** Открывает карту и показывает переданный трек (или загружает по id). */
    async function showTrackOnMap(track) {
        const payload = track ?? lastTrack;
        if (!payload?.points?.length) {
            App.toast('Трек пуст', 'Для этого игрока пока нет точек перемещения.', 'warn');
            return;
        }

        // По умолчанию трек показан; переключатель в панели скрывает его.
        playerTrack = { ...payload, visible: payload.visible !== false };

        // Карточка игрока могла быть открыта — закрываем, иначе она закроет карту.
        el('playerModal')?.classList.remove('open');

        App.showView('livemap');
        updateTrackControls();

        // Карта могла быть ещё не создана: сначала создаём её (initLiveMap),
        // затем ждём метаданные и размер контейнера, чтобы правильно
        // отцентрировать вид на треке.
        initLiveMap();
        await ensureMapInfo();

        // Контейнер карты получает размер не мгновенно после переключения
        // вкладки — дожидаемся его, иначе fit() посчитает вид по нулю.
        for (let i = 0; i < 40 && !liveMap?.hasSize; i++) {
            await new Promise((resolve) => setTimeout(resolve, 50));
            liveMap?.invalidateSize();
        }

        fitTrackView(payload);
        drawMapOverlay();
    }

    /** Показывает трек игрока по id (загружает и кладёт на карту). */
    async function showPlayerTrackById(id, name) {
        try {
            const data = await App.api(apiPath(`/tracker/players/${encodeURIComponent(id)}/track?points=300`));
            if (!data.count) {
                App.toast('Трек пуст', 'Для этого игрока пока нет точек.', 'warn');
                return;
            }
            await showTrackOnMap({ id, name: name || id, points: data.points, count: data.count });
        } catch (error) {
            App.toast('Не удалось получить трек', error.message, 'error');
        }
    }

    /**
     * Обновляет панель трека: имя игрока, видимость кнопок и подпись
     * переключателя. Панель видна только когда трек выбран.
     */
    function updateTrackControls() {
        const bar = el('mapTrackBar');
        if (!bar) return;

        bar.classList.toggle('hidden', !playerTrack);

        if (!playerTrack) return;

        text('mapTrackName', playerTrack.name || playerTrack.id);

        // Время начала и конца трека — сколько назад он записан.
        const range = el('mapTrackRange');
        if (range) {
            const points = playerTrack.points ?? [];
            const first = points[0]?.at;
            const last = points[points.length - 1]?.at;
            range.textContent = first && last
                ? `${fmtTime(first)} → ${fmtTime(last)}`
                : '';
        }

        const toggle = el('mapTrackToggle');
        if (toggle) toggle.textContent = playerTrack.visible ? 'Скрыть трек' : 'Показать трек';
    }

    /** Включает/выключает показ трека, не убирая данные. */
    function toggleTrack() {
        if (!playerTrack) return;
        playerTrack.visible = !playerTrack.visible;
        updateTrackControls();
        drawMapOverlay();
    }

    /** Убирает трек с карты. */
    function clearTrack() {
        playerTrack = null;
        updateTrackControls();
        drawMapOverlay();
    }

    /** Последние события игрока (смерти, убийства, чат) в карточке. */
    async function renderPlayerEvents(id) {
        const box = el('playerModalEvents');
        if (!box) return;

        // Не перерисовываем список, пока идёт предыдущий запрос: без этого
        // модалка «дёргалась» бы каждые пару секунд (список на миг исчезал).
        const myReq = ++eventsReqSeq;

        try {
            const events = await App.api(apiPath(`/tracker/players/${encodeURIComponent(id)}/events?limit=40`));

            // Карточку сменили/закрыли или пришёл ответ более свежего запроса.
            if (currentCardId !== id || myReq !== eventsReqSeq) {
                return;
            }

            // Отпечаток содержимого: если список не изменился, DOM не трогаем —
            // иначе браузер каждый раз перерисовывал бы одинаковый список.
            const signature = events
                .map((e) => `${e.at}|${e.kind}|${e.text}`)
                .join('\n');

            if (signature === eventsSignature) {
                return;
            }

            eventsSignature = signature;

            const rows = document.createDocumentFragment();

            if (!events.length) {
                const hint = document.createElement('span');
                hint.className = 'muted';
                hint.textContent = 'Событий пока нет (киллы и чат читаются из админ-лога).';
                rows.appendChild(hint);
            } else {
                const icons = { death: '☠', kill: '🎯', chat: '💬' };

                for (const event of events) {
                    const row = document.createElement('div');
                    row.className = 'pm-event';

                    const ic = document.createElement('span');
                    ic.className = 'pm-event-ic';
                    ic.textContent = icons[event.kind] ?? '•';

                    const textNode = document.createElement('span');
                    textNode.className = 'pm-event-text';
                    textNode.textContent = event.text;

                    const time = document.createElement('span');
                    time.className = 'pm-event-time mono muted';
                    time.textContent = fmtTime(event.at);

                    row.appendChild(ic);
                    row.appendChild(textNode);
                    row.appendChild(time);
                    rows.appendChild(row);
                }
            }

            // Заменяем содержимое одним движением, после получения данных.
            box.replaceChildren(rows);
        } catch (error) {
            if (currentCardId !== id || myReq !== eventsReqSeq) return;

            const hint = document.createElement('span');
            hint.className = 'muted';
            hint.textContent = 'Не удалось получить события: ' + error.message;
            box.replaceChildren(hint);
        }
    }

    /* ------------------------------------------------------------- килфид */

    async function loadKills() {
        try {
            const kills = await App.api(apiPath('/tracker/kills?limit=60'));
            const body = el('killRows');
            clear(body);

            if (!kills.length) {
                const tr = document.createElement('tr');
                const td = cell('Событий пока нет. Килфид читается из админ-лога сервера (.ADM).', 'muted');
                td.colSpan = 5;
                tr.appendChild(td);
                body.appendChild(tr);
                return;
            }

            for (const kill of kills) {
                const tr = document.createElement('tr');
                tr.appendChild(cell(new Date(kill.at).toLocaleTimeString('ru-RU')));

                const killer = document.createElement('td');
                killer.textContent = kill.isSuicide ? '—' : (kill.killerName || kill.killerId || 'неизвестно');
                if (kill.isEnvironment) killer.textContent = 'окружение';
                tr.appendChild(killer);

                const victim = document.createElement('td');
                victim.textContent = kill.victimName || kill.victimId || 'неизвестно';
                tr.appendChild(victim);

                tr.appendChild(cell(kill.weapon || '—'));
                tr.appendChild(cell(kill.distance > 0 ? `${kill.distance.toFixed(0)} м` : '—'));
                body.appendChild(tr);
            }
        } catch (error) {
            App.toast('Килфид недоступен', error.message, 'error');
        }
    }

    /* ------------------------------------------------------ события игроков */

    // Значки типов событий для ленты.
    const EVENT_ICONS = {
        session_in: '🟢', session_out: '🔴', kill: '⚔', suicide: '☠', env_death: '☠',
        hit: '💥', item_into_hands: '✋', item_out_hands: '🫳', item_attach: '🧥',
        item_detach: '🧦', item_cargo_in: '📥', item_cargo_out: '📤',
        build: '🏗', dismantle: '🔧', destroy: '💣', chat: '💬'
    };

    /** Лента событий игроков из базы (их пишет мод через events.jsonl). */
    async function loadEvents() {
        const body = el('eventRows');
        if (!body) return;

        const filter = el('eventFilter')?.value ?? '';

        try {
            const query = filter ? `?type=${encodeURIComponent(filter)}&limit=300` : '?limit=300';
            const events = await App.api(apiPath('/tracker/events' + query));
            clear(body);

            if (!events.length) {
                const tr = document.createElement('tr');
                const td = cell('Событий пока нет. Они появятся, когда мод пришлёт журнал.', 'muted');
                td.colSpan = 3;
                tr.appendChild(td);
                body.appendChild(tr);
                return;
            }

            for (const event of events) {
                const tr = document.createElement('tr');

                tr.appendChild(cell(new Date(event.at).toLocaleTimeString('ru-RU'), 'mono'));

                const who = document.createElement('td');
                who.textContent = event.playerName || event.playerId || '—';
                tr.appendChild(who);

                const what = document.createElement('td');
                const icon = document.createElement('span');
                icon.textContent = (EVENT_ICONS[event.type] ?? '•') + ' ';
                what.appendChild(icon);
                what.appendChild(document.createTextNode(event.text ?? ''));
                tr.appendChild(what);

                body.appendChild(tr);
            }
        } catch (error) {
            App.toast('События недоступны', error.message, 'error');
        }
    }

    /* --------------------------------------------------------------- чат */

    async function loadChat() {
        try {
            const messages = await App.api(apiPath('/tracker/chat?limit=200'));
            const box = el('chatLog');
            clear(box);

            if (!messages.length) {
                const hint = document.createElement('span');
                hint.className = 'muted';
                hint.textContent = 'Сообщений нет. Чат читается из админ-лога сервера (файлы .ADM).';
                box.appendChild(hint);
                return;
            }

            for (const message of messages) {
                const row = document.createElement('div');
                row.className = 'chat-line';

                // Сообщения администрации из панели помечаем каналом.
                const isAdmin = (message.channel || '').toLowerCase() === 'admin';

                const time = document.createElement('span');
                time.className = 'ts';
                time.textContent = new Date(message.at).toLocaleTimeString('ru-RU');

                const author = document.createElement('b');
                author.textContent = message.playerName || message.playerId || 'сервер';
                if (isAdmin) author.className = 'chat-admin';

                row.appendChild(time);
                row.appendChild(author);

                if (isAdmin) {
                    const badge = document.createElement('span');
                    badge.className = 'chat-badge';
                    badge.textContent = 'панель';
                    row.appendChild(badge);
                }

                const body = document.createElement('span');
                body.textContent = ' ' + (message.text ?? '');

                row.appendChild(body);
                box.appendChild(row);
            }

            box.scrollTop = box.scrollHeight;

            text('chatState', `${messages.length} сообщений`);
        } catch (error) {
            App.toast('Чат недоступен', error.message, 'error');
        }
    }

    async function broadcast(text) {
        const value = (text ?? '').trim();
        if (!value) return;

        const button = el('chatSend');
        if (button) button.disabled = true;

        try {
            // Отправка идёт через RCON (say -1): не грузит скрипты сервера.
            await App.api(apiPath('/tracker/chat'), { method: 'POST', body: { text: value } });
            el('chatInput').value = '';
            await loadChat();
        } catch (error) {
            App.toast('Не удалось отправить', error.message, 'error', 8000);
        } finally {
            if (button) button.disabled = false;
        }
    }

    /* --------------------------------------------------------- нарушения */

    async function loadViolations() {
        try {
            const type = el('violationFilter')?.value ?? '';
            const query = type ? `?type=${encodeURIComponent(type)}` : '';
            const violations = await App.api(apiPath('/tracker/violations' + query));

            const body = el('violationRows');
            clear(body);

            if (!violations.length) {
                const tr = document.createElement('tr');
                const td = cell('Нарушений не зафиксировано.', 'muted');
                td.colSpan = 7;
                tr.appendChild(td);
                body.appendChild(tr);
                return;
            }

            for (const violation of violations.slice().reverse()) {
                const tr = document.createElement('tr');
                tr.appendChild(cell(new Date(violation.at).toLocaleTimeString('ru-RU')));

                const player = document.createElement('td');
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'link';
                button.textContent = violation.playerName || violation.playerId || '—';
                button.addEventListener('click', () => openPlayer(violation.playerId));
                player.appendChild(button);
                tr.appendChild(player);

                tr.appendChild(cell(violation.type));
                tr.appendChild(cell(`${fmtCoord(violation.x)} : ${fmtCoord(violation.z)}`, 'mono'));
                tr.appendChild(cell(violation.points));
                tr.appendChild(cell(violation.totalPoints));

                const action = document.createElement('td');
                const tag = document.createElement('span');
                tag.className = 'tag ' + (violation.action === 'ban' ? 'off' : violation.action === 'kick' ? 'warn' : 'info');
                tag.textContent = violation.action === 'ban' ? 'бан' : violation.action === 'kick' ? 'кик' : 'запись';
                action.appendChild(tag);
                tr.appendChild(action);

                tr.appendChild(cell(violation.details));
                body.appendChild(tr);
            }
        } catch (error) {
            App.toast('Нарушения недоступны', error.message, 'error');
        }
    }

    /* ------------------------------------------------------------ команды */

    async function loadCommands() {
        try {
            const commands = await App.api(apiPath('/tracker/commands?limit=60'));
            const body = el('commandRowsLogs');
            clear(body);

            if (!commands.length) {
                const tr = document.createElement('tr');
                const td = cell('Команд не было.', 'muted');
                td.colSpan = 6;
                tr.appendChild(td);
                body.appendChild(tr);
                return;
            }

            const statusText = { pending: 'в очереди', dispatched: 'выдана моду', done: 'выполнена', failed: 'ошибка', expired: 'просрочена', cancelled: 'отменена' };
            const statusTag = { pending: 'info', dispatched: 'warn', done: 'on', failed: 'off', expired: 'off', cancelled: 'off' };

            for (const entry of commands) {
                const tr = document.createElement('tr');
                tr.appendChild(cell(entry.id));
                tr.appendChild(cell(entry.action));
                tr.appendChild(cell(Object.entries(entry.args ?? {}).map(([k, v]) => `${k}=${v}`).join(' '), 'mono'));

                const status = document.createElement('td');
                const tag = document.createElement('span');
                tag.className = 'tag ' + (statusTag[entry.status] ?? 'info');
                tag.textContent = statusText[entry.status] ?? entry.status;
                status.appendChild(tag);
                tr.appendChild(status);

                tr.appendChild(cell(fmtTime(entry.createdAt)));
                tr.appendChild(cell(entry.message || '—'));

                body.appendChild(tr);
            }
        } catch (error) {
            App.toast('Очередь команд недоступна', error.message, 'error');
        }
    }

    /* -------------------------------------------------------- живая карта */

    // Карта мира: тайлы проксируются панелью с dayz.xam.nu (см. map.js).
    let liveMap = null;
    let mapInfo = null;
    let mapReady = false;
    let mapInfoPromise = null;
    let mapControlsBound = false;

    // Трек, наложенный на живую карту: { id, name, points:[{x,z}], last:{x,z} }.
    let playerTrack = null;

    // Фильтры слоёв карты. Ключ — категория, значение — показывать ли.
    const FILTERS_KEY = 'dayzpulse.map.filters';
    const mapFilters = {
        alive: true,
        dead: false,
        vehicle: true,
        barrel: true,
        tent: true,
        crate: true,
        stash: true,
        flag: true,
        watchtower: true,
        fence: true
    };

    // Восстанавливаем выбор фильтров, сделанный ранее.
    try {
        const savedFilters = JSON.parse(localStorage.getItem(FILTERS_KEY) ?? 'null');
        if (savedFilters && typeof savedFilters === 'object') {
            Object.assign(mapFilters, savedFilters);
        }
    } catch { /* localStorage может быть недоступен */ }

    function saveMapFilters() {
        try { localStorage.setItem(FILTERS_KEY, JSON.stringify(mapFilters)); } catch { /* игнорируем */ }
    }

    /**
     * Тип транспорта по классу DayZ. Возвращает { key, label, icon, scale }:
     * у каждого класса-семейства своя иконка (машина, вертолёт, лодка…),
     * а не одинаковый квадратик.
     */
    function vehicleArchetype(className) {
        const c = String(className || '').toLowerCase();
        const has = (...parts) => parts.some((p) => c.includes(p));

        if (has('boat', 'ship', 'sailing', 'raft', 'lhd', 'fishingship')) return { key: 'boat', label: 'Лодка', icon: '⛵', scale: 0.9 };
        if (has('helicopter', 'heli', 'mh6', 'uh1y', 'mi8', 'uh60')) return { key: 'heli', label: 'Вертолёт', icon: '🚁', scale: 1.05 };
        if (has('truck', 'v3s', 'kamaz', 'm3s', 'bus', 'ural')) return { key: 'truck', label: 'Грузовик', icon: '🚚', scale: 1.15 };
        if (has('motorcycle', 'motorbike', 'moto')) return { key: 'moto', label: 'Мотоцикл', icon: '🏍️', scale: 0.75 };
        if (has('bicycle', 'bike')) return { key: 'bike', label: 'Велосипед', icon: '🚲', scale: 0.7 };
        if (has('tractor', 'harvester')) return { key: 'tractor', label: 'Трактор', icon: '🚜', scale: 1 };
        if (has('sedan', 'hatchback', 'suv', 'offroad', 'car', 'lada', 'golf', 'volha', 'van', 'police', 'ambulance', 'fire', 'olga', 'niva', 'sarka')) {
            return { key: 'car', label: 'Автомобиль', icon: '🚗', scale: 1 };
        }
        return { key: 'vehicle', label: 'Транспорт', icon: '🚙', scale: 1 };
    }

    /** Иконка объекта мира по его категории (единый стиль с транспортом). */
    const OBJECT_ICON = {
        barrel: '🛢️',
        tent: '⛺',
        crate: '📦',
        stash: '🗄️',
        flag: '🚩',
        watchtower: '🗼',
        fence: '🧱',
        build: '🧱'
    };

    /** Ставит иконку (эмодзи) в SVG-группу с невидимой зоной для подсказок. */
    function placeMapIcon(ns, layer, at, glyph, size) {
        const group = document.createElementNS(ns, 'g');

        const text = document.createElementNS(ns, 'text');
        text.setAttribute('x', at.x);
        text.setAttribute('y', at.y);
        text.setAttribute('text-anchor', 'middle');
        text.setAttribute('dominant-baseline', 'central');
        text.setAttribute('font-size', String(size));
        text.setAttribute('style', 'pointer-events:none;user-select:none');
        text.textContent = glyph;
        group.appendChild(text);

        // Прозрачная зона под иконкой: по ней работает наведение/клик и
        // показывается <title>, не мешая отрисовке самого эмодзи.
        const hit = document.createElementNS(ns, 'circle');
        hit.setAttribute('cx', at.x);
        hit.setAttribute('cy', at.y);
        hit.setAttribute('r', String(Math.max(8, size * 0.6)));
        hit.setAttribute('fill', 'transparent');
        group.appendChild(hit);

        layer.appendChild(group);
        return group;
    }

    // Трек может выходить за экран: подбираем зум и центр так, чтобы линия
    // целиком помещалась в контейнере.
    function fitTrackView(track) {
        if (!liveMap || !track.points.length) return;

        let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
        for (const point of track.points) {
            minX = Math.min(minX, point.x); maxX = Math.max(maxX, point.x);
            minZ = Math.min(minZ, point.z); maxZ = Math.max(maxZ, point.z);
        }

        const span = Math.max(maxX - minX, maxZ - minZ, 60);
        const need = Math.max(liveMap.containerWidth, liveMap.containerHeight) * 0.85;
        const ideal = Math.log2(need / (256 * (span / liveMap.size)));
        liveMap.setView((minX + maxX) / 2, (minZ + maxZ) / 2, ideal);
    }

    /** Создаёт карту один раз и подключает элементы управления. */
    function initLiveMap() {
        const wrap = el('mapWrap');
        const tiles = el('mapTiles');
        if (!wrap || !tiles) return;

        if (!liveMap && window.TileMap) {
            liveMap = new window.TileMap(wrap, tiles, drawMapOverlay);
            bindMapControls();
        }

        liveMap?.invalidateSize();
        ensureMapInfo();
    }

    /**
     * Загружает метаданные карты (размер мира, тип, доступность).
     * Возвращает общий промис, поэтому параллельные вызовы ждут одну загрузку.
     */
    function ensureMapInfo() {
        if (mapInfoPromise) return mapInfoPromise;
        if (mapReady || !liveMap) return Promise.resolve(mapInfo);

        mapReady = true;
        mapInfoPromise = loadMapInfo().finally(() => { mapInfoPromise = null; });
        return mapInfoPromise;
    }

    async function loadMapInfo() {
        try {
            mapInfo = await App.api(apiPath('/tracker/map/info'));

            if (!mapInfo.available) {
                showMapNote('Для этой карты нет тайлов на xam.nu — показаны только позиции.');
                return mapInfo;
            }

            text('mapTitle', mapInfo.label || mapInfo.map || '');

            // Ключ карты и версия тайлов — в адресе: при смене карты кэш браузера
            // не подменит новые тайлы старыми.
            const tileUrl = (urlLayer, z, x, y) =>
                '/api' + apiPath(
                    `/tracker/map/tiles/${encodeURIComponent(mapInfo.map)}/${encodeURIComponent(mapInfo.tilev || '0')}` +
                    `/${urlLayer}/${z}/${x}/${y}`);

            liveMap.configure({
                size: mapInfo.size,
                maxZoom: mapInfo.maxZoom ?? 8,
                layer: liveMap.layer,
                tileUrl
            });
            liveMap.fit();
            preloadMapTiles(mapInfo);

            const attribution = el('mapAttribution');
            if (attribution) {
                attribution.textContent = 'Карта: ' + (mapInfo.attribution || 'XAM · dayz.xam.nu');
            }
        } catch (error) {
            mapReady = false;
            showMapNote('Не удалось загрузить карту: ' + error.message);
        }

        return mapInfo;
    }

    function showMapNote(message) {
        const note = el('mapNote');
        if (!note) return;
        note.textContent = message;
        note.classList.toggle('hidden', !message);
    }

    /**
     * Прогревает кэш браузера тайлами обзорного уровня. Карта выбирается
     * заранее, поэтому при первом открытии «Живой карты» она уже на месте:
     * загрузка идёт в фоне и не мешает интерфейсу.
     */
    function preloadMapTiles(info) {
        if (!info?.available || !info.map) return;

        const z = Math.max(0, Math.min(info.maxZoom ?? 8, 4));
        const count = Math.pow(2, z);

        for (let x = 0; x < count; x++) {
            for (let y = 0; y < count; y++) {
                const img = new Image();
                img.decoding = 'async';
                img.src = '/api' + apiPath(
                    `/tracker/map/tiles/${encodeURIComponent(info.map)}/${encodeURIComponent(info.tilev || '0')}` +
                    `/topographic/${z}/${x}/${y}`);
            }
        }
    }

    function bindMapControls() {
        if (mapControlsBound) return;
        mapControlsBound = true;

        el('mapZoomIn')?.addEventListener('click', () => liveMap?.zoomBy(1));
        el('mapZoomOut')?.addEventListener('click', () => liveMap?.zoomBy(-1));
        el('mapReset')?.addEventListener('click', () => liveMap?.fit());

        // Панель трека: показать/скрыть, целиком, убрать.
        el('mapTrackToggle')?.addEventListener('click', toggleTrack);
        el('mapTrackClose')?.addEventListener('click', clearTrack);
        el('mapTrackFit')?.addEventListener('click', () => {
            if (!playerTrack) return;
            // Если трек был скрыт — включаем, иначе кнопка «целиком» выглядит мёртвой.
            playerTrack.visible = true;
            updateTrackControls();
            fitTrackView(playerTrack);
        });

        el('mapLayerBtn')?.addEventListener('click', () => {
            if (!liveMap || !mapInfo?.available) return;
            const next = liveMap.layer === 'topographic' ? 'satellite' : 'topographic';
            liveMap.setLayer(next);
            const button = el('mapLayerBtn');
            if (button) button.textContent = next === 'topographic' ? 'Спутник' : 'Схема';
        });

        // Панель фильтров: сворачивание и переключение слоёв.
        el('mapFiltersToggle')?.addEventListener('click', () => {
            el('mapFiltersList')?.classList.toggle('hidden');
        });

        for (const input of document.querySelectorAll('#mapFiltersList [data-map-filter]')) {
            input.checked = mapFilters[input.dataset.mapFilter] ?? true;
            input.addEventListener('change', () => {
                mapFilters[input.dataset.mapFilter] = input.checked;
                saveMapFilters();
                drawMapOverlay();
            });
        }
    }

    /**
     * Рисует маркеры поверх тайлов. Позиции переводим из игровых метров
     * в экранные пиксели тем же масштабом, что и тайловый слой.
     */
    function updateLiveMap() {
        drawMapOverlay();
    }

    function drawMapOverlay() {
        const svg = el('liveMapSvg');
        if (!svg || !liveMap) return;

        // Скрытый/схлопнутый контейнер: размеров нет — рисовать нечего.
        if (!liveMap.hasSize) return;

        // SVG накрывает контейнер и работает в экранных координатах.
        svg.setAttribute('viewBox', `0 0 ${liveMap.containerWidth} ${liveMap.containerHeight}`);
        svg.setAttribute('preserveAspectRatio', 'none');

        const players = el('liveMapPlayers');
        const vehicleLayer = el('liveMapVehicles');
        const objectLayer = el('liveMapObjects');
        const trackLayer = el('mapTrackOverlay');
        if (!players || !vehicleLayer || !objectLayer) return;

        clear(players);
        clear(vehicleLayer);
        clear(objectLayer);
        if (trackLayer) clear(trackLayer);

        const ns = 'http://www.w3.org/2000/svg';
        let aliveOnMap = 0;
        let deadOnMap = 0;
        let vehiclesOnMap = 0;
        let objectsOnMap = 0;

        // --- игроки ---
        for (const player of livePlayers) {
            const visible = player.alive ? mapFilters.alive : mapFilters.dead;
            if (!visible) continue;

            const at = mapToScreen(player.pos.x, player.pos.z);
            if (!at) continue;

            if (player.alive) aliveOnMap++; else deadOnMap++;

            const group = document.createElementNS(ns, 'g');
            group.setAttribute('transform', `translate(${at.x} ${at.y})`);
            group.setAttribute('opacity', player.stale ? '0.45' : '1');

            const color = player.alive ? '#3fb950' : '#e5534b';
            const isTracked = !!playerTrack && playerTrack.id === player.id;

            const halo = document.createElementNS(ns, 'circle');
            halo.setAttribute('r', '13');
            halo.setAttribute('fill', color);
            halo.setAttribute('fill-opacity', '0.22');
            group.appendChild(halo);

            const dot = document.createElementNS(ns, 'circle');
            dot.setAttribute('r', '6.5');
            dot.setAttribute('fill', color);
            dot.setAttribute('stroke', '#0a0e13');
            dot.setAttribute('stroke-width', '2');
            dot.style.cursor = 'pointer';
            dot.addEventListener('click', () => openPlayer(player.id));
            group.appendChild(dot);

            // У игрока, чей трек сейчас показан, — заметное кольцо.
            if (isTracked) {
                const ring = document.createElementNS(ns, 'circle');
                ring.setAttribute('r', '15');
                ring.setAttribute('fill', 'none');
                ring.setAttribute('stroke', '#4a90d9');
                ring.setAttribute('stroke-width', '2.5');
                group.appendChild(ring);
            }

            const title = document.createElementNS(ns, 'title');
            title.textContent = `${player.name} · ${Math.round(player.health)} HP · ${fmtCoord(player.pos.x)}:${fmtCoord(player.pos.z)}`;
            group.appendChild(title);

            players.appendChild(group);
        }

        // --- транспорт (иконка по типу: машина, вертолёт, лодка…) ---
        if (mapFilters.vehicle) {
            for (const vehicle of liveVehicles) {
                const at = mapToScreen(vehicle.x, vehicle.z);
                if (!at) continue;
                vehiclesOnMap++;

                const arche = vehicleArchetype(vehicle.class);
                const node = placeMapIcon(ns, vehicleLayer, at, arche.icon, 17 * arche.scale);

                const title = document.createElementNS(ns, 'title');
                title.textContent = `${arche.label} · ${vehicle.class} · ` +
                    `топливо ${Math.round(vehicle.fuel ?? 0)}% · ${fmtCoord(vehicle.x)}:${fmtCoord(vehicle.z)}`;
                node.appendChild(title);
            }
        }

        // --- объекты мира (бочки, палатки, ящики, схроны и т.п.) ---
        for (const object of liveObjects) {
            const kind = object.kind || 'build';
            // Неизвестные категории не прячем — показываем всегда.
            if (kind in mapFilters && !mapFilters[kind]) continue;

            const at = mapToScreen(object.x, object.z);
            if (!at) continue;
            objectsOnMap++;

            const glyph = OBJECT_ICON[kind] ?? OBJECT_ICON.build;
            const node = placeMapIcon(ns, objectLayer, at, glyph, 15);

            const title = document.createElementNS(ns, 'title');
            title.textContent = `${object.label || kind}${object.class ? ' · ' + object.class : ''}` +
                ` · ${fmtCoord(object.x)}:${fmtCoord(object.z)}`;
            node.appendChild(title);
        }

        // --- трек выбранного игрока (под маркерами игроков, поверх объектов) ---
        drawTrack(ns, trackLayer);

        // Сводка под картой.
        const counts = el('mapCounts');
        if (counts) {
            const parts = [
                `живых ${aliveOnMap}`,
                deadOnMap ? `мёртвых ${deadOnMap}` : '',
                `транспорта ${vehiclesOnMap}`,
                `объектов ${objectsOnMap}`
            ].filter(Boolean);
            if (playerTrack && playerTrack.visible) {
                parts.push(`трек: ${playerTrack.name || playerTrack.id} (${playerTrack.points.length} точек)`);
            }
            counts.textContent = 'на карте: ' + parts.join(' · ');
        }
    }

    /** Рисует линию перемещений выбранного игрока и подписи начала/конца. */
    function drawTrack(ns, layer) {
        if (!layer || !playerTrack || !playerTrack.visible) return;
        if (playerTrack.points.length < 2) return;

        const screen = [];
        for (const point of playerTrack.points) {
            const at = mapToScreen(point.x, point.z);
            if (at) screen.push(at);
        }

        if (screen.length < 2) return;

        const polyline = document.createElementNS(ns, 'polyline');
        polyline.setAttribute('points', screen.map((p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' '));
        polyline.setAttribute('fill', 'none');
        polyline.setAttribute('stroke', '#4a90d9');
        polyline.setAttribute('stroke-width', '2.5');
        polyline.setAttribute('stroke-linejoin', 'round');
        polyline.setAttribute('stroke-linecap', 'round');
        polyline.setAttribute('stroke-opacity', '0.9');
        polyline.setAttribute('style', 'pointer-events:none');
        layer.appendChild(polyline);

        // Начало трека — зелёный кружок.
        const start = screen[0];
        const startDot = document.createElementNS(ns, 'circle');
        startDot.setAttribute('cx', start.x);
        startDot.setAttribute('cy', start.y);
        startDot.setAttribute('r', '4');
        startDot.setAttribute('fill', '#3fb950');
        startDot.setAttribute('stroke', '#0a0e13');
        startDot.setAttribute('stroke-width', '1.5');
        layer.appendChild(startDot);

        drawTrackEnd(ns, layer, screen[screen.length - 1]);
    }

    /** Маркер конца трека: точка + стрелка направления + подпись. */
    function drawTrackEnd(ns, layer, end) {
        if (!end) return;

        const color = '#4a90d9';
        const g = document.createElementNS(ns, 'g');
        g.setAttribute('transform', `translate(${end.x} ${end.y})`);
        g.setAttribute('style', 'pointer-events:none');

        const halo = document.createElementNS(ns, 'circle');
        halo.setAttribute('r', '11');
        halo.setAttribute('fill', color);
        halo.setAttribute('fill-opacity', '0.22');
        g.appendChild(halo);

        const dot = document.createElementNS(ns, 'circle');
        dot.setAttribute('r', '5');
        dot.setAttribute('fill', color);
        dot.setAttribute('stroke', '#0a0e13');
        dot.setAttribute('stroke-width', '2');
        g.appendChild(dot);

        const label = document.createElementNS(ns, 'text');
        label.setAttribute('x', '0');
        label.setAttribute('y', '-12');
        label.setAttribute('text-anchor', 'middle');
        label.setAttribute('font-size', '10');
        label.setAttribute('font-weight', '700');
        label.setAttribute('fill', color);
        label.setAttribute('stroke', '#0a0e13');
        label.setAttribute('stroke-width', '2.5');
        label.setAttribute('paint-order', 'stroke');
        label.textContent = 'сейчас';
        g.appendChild(label);

        layer.appendChild(g);
    }

    /** Игровые метры → экранные пиксели контейнера карты. */
    function mapToScreen(gameX, gameZ) {
        if (!liveMap) return null;
        const world = liveMap.project(gameX, gameZ);
        return liveMap.toScreen(world.x, world.y);
    }

    /* ------------------------------------------------------------- загрузка */

    async function loadConfig() {
        try {
            const config = await App.api(apiPath('/tracker/config'));
            text('trackerToken', config.tokenMasked || 'не задан');
            text('trackerModPath', config.modConfigPath || '—');

            // Карточка «Связь с модом» видна только если мод установлен.
            renderTrackerCard(config);

            el('trackerEnabled').checked = !!config.enabled;
            el('trackerServerId').value = config.serverId ?? '';
            el('trackerStale').value = config.staleSeconds ?? 30;
            el('trackerRetention').value = config.retentionDays ?? 14;
            el('trackerAutoConfig').checked = !!config.autoConfigureMod;
            el('trackerProfiles').value = config.profilesDirectory ?? '';
            text('trackerState', config.connected ? 'мод на связи' : 'нет данных');
            const stateTag = el('trackerStateTag');
            stateTag.className = 'tag ' + (config.connected ? 'on' : 'off');
            stateTag.textContent = config.connected ? 'на связи' : 'нет связи';
            text('trackerLastIngest', config.lastIngest ? fmtTime(config.lastIngest) : '—');

            // Карточку «Моды» заполняет модуль Mods.
            if (typeof Mods !== 'undefined') Mods.setTracker(config);
        } catch (error) {
            App.toast('Настройки трекера недоступны', error.message, 'error');
        }

        await loadLaunch();
    }

    /* --------------------------------------------- параметры запуска */

    let launchBaseline = null;

    async function loadLaunch() {
        const card = el('launchCard');
        if (!card || !App.currentServerId) return;

        try {
            const data = await App.api(apiPath('/launch'));
            launchBaseline = data;

            el('launchArgs').value = data.arguments ?? '';
            el('launchExtra').value = data.extraArguments ?? '';
            el('launchPortEdit').value = data.port ?? '';
            text('launchPort', data.port ?? '—');
            text('launchMods', data.mods || '—');
            text('launchServerMods', data.serverMods || '—');
            text('launchCommand', data.launchCommand || '—');

            const hint = el('launchHint');
            if (hint) {
                hint.className = 'tag ' + (data.running ? 'off' : 'on');
                hint.textContent = data.running
                    ? 'сервер запущен — изменения применятся при следующем запуске'
                    : 'сервер остановлен — можно менять';
            }

            markLaunchDirty();
        } catch {
            // Не критично.
        }
    }

    function launchChanged() {
        if (!launchBaseline) return false;
        return el('launchArgs').value !== (launchBaseline.arguments ?? '') ||
            el('launchExtra').value !== (launchBaseline.extraArguments ?? '') ||
            Number(el('launchPortEdit').value) !== Number(launchBaseline.port ?? 0);
    }

    function markLaunchDirty() {
        const dirty = launchChanged();
        document.querySelector('[data-dirty="launch"]')?.classList.toggle('on', dirty);
        el('launchCard')?.classList.toggle('changed', dirty);
        el('launchSave')?.classList.toggle('pulse', dirty);
    }

    async function saveLaunch() {
        if (!launchChanged()) {
            App.toast('Нечего сохранять', 'Параметры не менялись.', 'info');
            return;
        }

        try {
            const result = await App.api(apiPath('/launch'), {
                method: 'PUT',
                body: {
                    arguments: el('launchArgs').value,
                    extraArguments: el('launchExtra').value,
                    port: Number(el('launchPortEdit').value) || null
                }
            });
            App.toast(
                result.pending ? 'Правки отложены' : 'Сохранено',
                result.pending
                    ? 'Сервер запущен: параметры применятся при следующем запуске.'
                    : 'Параметры запуска обновлены.',
                'info');
            await loadLaunch();
        } catch (error) {
            App.toast('Не удалось сохранить', error.message, 'error');
        }
    }

    /* ------------------------------------ расширенные параметры запуска */

    /**
     * Известные аргументы запуска сервера DayZ. У каждого — флаг, описание
     * и (для параметров со значением) шаблон с подстановкой.
     */
    const LAUNCH_FLAGS = [
        { flag: '-dologs', label: 'Логи скриптов (.RPT)', desc: 'Писать логи скриптов в папку profiles.', group: 'Логи' },
        { flag: '-adminlog', label: 'Админ-лог (.ADM)', desc: 'Писать админ-лог: чат, смерти, подключения.', group: 'Логи' },
        { flag: '-netlog', label: 'Сетевой лог', desc: 'Писать сетевой лог (.RPT с пакетами).', group: 'Логи' },
        { flag: '-freezecheck', label: 'Проверка зависаний', desc: 'Перезапуск при зависании сервера.', group: 'Логи' },
        { flag: '-nosplash', label: 'Без заставки', desc: 'Не показывать сплэш при старте.', group: 'Производительность' },
        { flag: '-nopause', label: 'Без паузы', desc: 'Не останавливать процесс в фоне.', group: 'Производительность' },
        { flag: '-noPause', label: 'Без паузы (alt)', desc: 'То же, вариант написания.', group: 'Производительность' },
        { flag: '-cpuCount=', label: 'Ограничить ядра (cpuCount)', desc: 'Сколько ядер задействовать: cpuCount=4.', arg: '4', group: 'Производительность' },
        { flag: '-maxMem=', label: 'Лимит памяти (maxMem)', desc: 'Максимум памяти в МБ: maxMem=4096.', arg: '4096', group: 'Производительность' },
        { flag: '-scriptDebug', label: 'Отладка скриптов', desc: 'Включает отладку Enforce Script.', group: 'Отладка' },
        { flag: '-filePatching', label: 'FilePatching', desc: 'Разрешает моду с серверной частью без подписи (не для прода).', group: 'Отладка' },
        { flag: '-debug', label: 'Режим отладки', desc: 'Полноценный debug-режим (не для прода).', group: 'Отладка' },
        { flag: '-limitFPS=', label: 'Лимит FPS сервера', desc: 'Ограничить FPS сервера: limitFPS=60.', arg: '60', group: 'Производительность' },
        { flag: '-connect=', label: 'Подключение', desc: 'Автоподключение (только клиент).', arg: '', group: 'Прочее' }
    ];

    /** Разбирает строку доп. аргументов в набор флагов. */
    function parseExtraArgs(text) {
        return (text ?? "").split(/\s+/).filter((t) => t.startsWith('-'));
    }

    /** Включает/выключает флаг в строке доп. аргументов. */
    function toggleExtraFlag(text, flag, enabled) {
        const tokens = (text ?? "").split(/\s+/).filter((t) => t.length > 0);
        const prefix = flag.includes('=') ? flag.slice(0, flag.indexOf('=') + 1) : flag;

        // Убираем существующие вхождения этого флага (по префиксу).
        const kept = tokens.filter((t) => !(t === flag || t.startsWith(prefix) && prefix.includes('=')));

        if (enabled) {
            kept.push(flag);
        }

        return kept.join(' ');
    }

    function openLaunchModal() {
        const body = el('launchModalBody');
        if (!body) return;

        clear(body);
        const current = el('launchExtra')?.value ?? "";

        let group = "";
        for (const item of LAUNCH_FLAGS) {
            if (item.group !== group) {
                group = item.group;
                const head = document.createElement('div');
                head.className = 'adv-group';
                head.textContent = group;
                body.appendChild(head);
            }

            body.appendChild(buildLaunchField(item, current));
        }

        el('launchModal')?.classList.add('open');
    }

    function buildLaunchField(item, currentText) {
        const row = document.createElement('div');
        row.className = 'adv-field adv-bool';

        const label = document.createElement('label');
        label.textContent = item.label;
        label.title = item.desc;

        const right = document.createElement('div');
        right.className = 'row';
        right.style.gap = '8px';

        // Если параметр со значением — поле ввода.
        let input = null;
        if (item.arg !== undefined) {
            input = document.createElement('input');
            input.type = 'text';
            input.style.width = '120px';
            input.placeholder = 'значение';
            input.disabled = item.locked === true;

            const tokens = currentText.split(/\s+/);
            const found = tokens.find((t) => t.startsWith(item.flag));
            input.value = found && found.length > item.flag.length ? found.slice(item.flag.length) : (item.arg || '');
            right.appendChild(input);
        }

        const sw = document.createElement('label');
        sw.className = 'sw';
        const toggle = document.createElement('input');
        toggle.type = 'checkbox';
        toggle.disabled = item.locked === true;
        toggle.checked = parseExtraArgs(currentText).some((t) => t === item.flag || t.startsWith(item.flag));
        sw.appendChild(toggle);
        sw.appendChild(document.createElement('span'));
        right.appendChild(sw);

        row.appendChild(label);
        row.appendChild(right);

        const desc = document.createElement('div');
        desc.className = 'adv-desc';
        desc.innerHTML = '';

        const mark = document.createElement('span');
        mark.className = 'adv-q';
        mark.textContent = '?';
        const text = document.createElement('span');
        text.textContent = item.desc;
        desc.appendChild(mark);
        desc.appendChild(text);

        const wrap = document.createElement('div');
        wrap.className = 'adv-item';
        wrap.appendChild(row);
        wrap.appendChild(desc);

        // Применяем изменение к строке доп. аргументов сразу.
        const apply = () => {
            const target = el('launchExtra');
            const value = input && item.arg !== undefined ? item.flag + input.value.trim() : item.flag;
            target.value = toggleExtraFlag(target.value, value, toggle.checked);
            markLaunchDirty();
        };

        toggle.addEventListener('change', apply);
        input?.addEventListener('input', () => { if (toggle.checked) apply(); });

        return wrap;
    }

    /** Показывает карточку связи, только если мод установлен на сервере. */
    function renderTrackerCard(config) {
        const card = el('trackerCard');
        if (card) {
            card.classList.remove('hidden');
            const installed = !!config.modInstalled;
            el('trackerCardMissing')?.classList.toggle('hidden', installed);
            el('trackerCardSettings')?.classList.toggle('hidden', !installed);
        }

        text('modConfigPath2', config.modConfigPath || '—');
        text('modLaunchMods', config.modLaunchMods || '—');
    }

    async function saveConfig() {
        try {
            const result = await App.api(apiPath('/tracker/config'), {
                method: 'POST',
                body: {
                    enabled: el('trackerEnabled').checked,
                    serverId: el('trackerServerId').value.trim(),
                    staleSeconds: Number(el('trackerStale').value),
                    retentionDays: Number(el('trackerRetention').value),
                    autoConfigureMod: el('trackerAutoConfig').checked,
                    profilesDirectory: el('trackerProfiles').value.trim()
                }
            });
            App.toast('Сохранено', 'Настройки трекера обновлены.', 'info');
            loadConfig();
        } catch (error) {
            App.toast('Не удалось сохранить', error.message, 'error');
        }
    }

    async function rotateToken() {
        UI.confirm('Создать новый токен? Мод перестанет подключаться, пока не получит новый config.json.', async () => {
            const result = await App.api(apiPath('/tracker/config'), { method: 'POST', body: { token: '__rotate__' } });
            App.toast('Токен обновлён', 'Новый config.json записан в профили сервера.', 'info', 8000);
            loadConfig();
        }, 'Обновить токен');
    }

    /* -------------------------------------------------------------- формат */

    function formatPlayerCsv() {
        const header = ['Имя', 'SteamID64', 'BI id', 'Сессий', 'Наиграно', 'Убийств', 'Смертей', 'K/D', 'Первый вход', 'Последний вход'];
        const lines = [header.join(';')];
        for (const player of databaseRows) {
            lines.push([
                player.name, player.plainId, player.id, player.sessions,
                Math.round(player.playtimeSeconds), player.kills, player.deaths, player.kd,
                fmtTime(player.firstSeen), fmtTime(player.lastSeen)
            ].map((v) => `"${String(v ?? '').replace(/"/g, '""')}"`).join(';'));
        }
        return lines.join('\r\n');
    }

    function exportCsv() {
        const blob = new Blob(['\uFEFF' + formatPlayerCsv()], { type: 'text/csv;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = `dayzpulse-players-${new Date().toISOString().slice(0, 10)}.csv`;
        link.click();
        URL.revokeObjectURL(url);
        App.toast('Экспорт готов', `${databaseRows.length} записей сохранено в CSV.`, 'info');
    }

    /** Закрывает карточку игрока и перестаёт обновлять её события. */
    function closePlayerCard() {
        currentCardId = null;
        eventsSignature = null;
        el('playerModal')?.classList.remove('open');
    }

    /* -------------------------------------------------------------- публично */

    function start() {
        loadLive();
        initLiveMap();
        if (!liveTimer) liveTimer = setInterval(loadLive, LIVE_INTERVAL);
    }

    function stop() {
        if (liveTimer) {
            clearInterval(liveTimer);
            liveTimer = null;
        }
    }

    function bind() {
        const tabs = document.querySelectorAll('#playersTabs .subtab');
        tabs.forEach((tab) => {
            tab.addEventListener('click', () => {
                tabs.forEach((t) => t.classList.toggle('active', t === tab));
                document.querySelectorAll('.players-panel').forEach((panel) => {
                    panel.classList.toggle('hidden', panel.id !== 'players-panel-' + tab.dataset.playersPanel);
                });

                const panel = tab.dataset.playersPanel;
                if (panel === 'database') loadDatabase();
                if (panel === 'kills') loadKills();
                if (panel === 'chat') loadChat();
                if (panel === 'violations') loadViolations();
                if (panel === 'livemap') { initLiveMap(); liveMap?.invalidateSize(); }
            });
        });

        el('playersRefresh')?.addEventListener('click', () => { loadLive(); loadConfig(); });

        // Демо-игрок: добавляет/убирает тестового игрока в списке онлайн.
        el('playersDemo')?.addEventListener('click', () => {
            const button = el('playersDemo');
            const enabled = button.dataset.on !== '1';

            App.api(apiPath('/tracker/demo'), { method: 'POST', body: { enabled } })
                .then(() => {
                    button.dataset.on = enabled ? '1' : '0';
                    button.classList.toggle('primary', enabled);
                    App.toast(enabled ? 'Тестовый игрок добавлен' : 'Тестовый игрок убран', '', 'info');
                    loadLive();
                })
                .catch((error) => App.toast('Не удалось', error.message, 'error'));
        });

        // Инвентарь в карточке игрока.
        el('playerModalInvRefresh')?.addEventListener('click', () => {
            if (!currentPlayerId) return;
            App.api(apiPath(`/tracker/inventory/${encodeURIComponent(currentPlayerId)}/refresh`), { method: 'POST' })
                .then(() => App.toast('Запрошен инвентарь', 'Мод пришлёт снимок в течение пары секунд.', 'info'))
                .catch((error) => App.toast('Не удалось', error.message, 'error'));
        });
        el('invSearch')?.addEventListener('input', (event) => renderPalette(event.target.value));

        el('dbSearch')?.addEventListener('input', (event) => {
            databaseFilter.search = event.target.value.trim();
            clearTimeout(el('dbSearch').dataset.timer);
            el('dbSearch').dataset.timer = setTimeout(loadDatabase, 250);
        });

        el('dbWatchOnly')?.addEventListener('change', (event) => {
            databaseFilter.watchOnly = event.target.checked;
            loadDatabase();
        });

        el('dbSort')?.addEventListener('change', (event) => {
            databaseFilter.sort = event.target.value;
            loadDatabase();
        });

        el('dbExport')?.addEventListener('click', exportCsv);
        el('violationFilter')?.addEventListener('change', loadViolations);
        el('violationsRefresh')?.addEventListener('click', loadViolations);
        el('eventsRefresh')?.addEventListener('click', loadEvents);
        el('eventFilter')?.addEventListener('change', loadEvents);
        el('commandsRefresh')?.addEventListener('click', loadCommands);
        el('commandsClear')?.addEventListener('click', () => {
            UI.confirm('Удалить всю очередь команд?', async () => {
                try {
                    await App.api(apiPath('/tracker/commands/clear'), { method: 'POST' });
                    loadCommands();
                } catch (error) {
                    App.toast('Не удалось очистить', error.message, 'error');
                }
            }, 'Очистить');
        });

        el('chatSend')?.addEventListener('click', () => broadcast(el('chatInput').value));
        el('chatInput')?.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') { event.preventDefault(); broadcast(el('chatInput').value); }
        });

        el('playerModalClose')?.addEventListener('click', closePlayerCard);
        el('playerModal')?.addEventListener('click', (event) => {
            if (event.target.id === 'playerModal') closePlayerCard();
        });

        el('playerInvClose')?.addEventListener('click', () => el('playerInvModal').classList.remove('open'));
        el('playerInvModal')?.addEventListener('click', (event) => {
            if (event.target.id === 'playerInvModal') el('playerInvModal').classList.remove('open');
        });

        el('trackerSave')?.addEventListener('click', saveConfig);
        el('trackerRotate')?.addEventListener('click', rotateToken);
        el('trackerCardInstall')?.addEventListener('click', () => App.showView('mods'));
        el('modsOpenConfig')?.addEventListener('click', () => App.showView('configs'));

        el('launchSave')?.addEventListener('click', saveLaunch);
        el('launchReload')?.addEventListener('click', loadLaunch);
        el('launchArgs')?.addEventListener('input', markLaunchDirty);
        el('launchExtra')?.addEventListener('input', markLaunchDirty);
        el('launchPortEdit')?.addEventListener('input', markLaunchDirty);

        el('launchAdvanced')?.addEventListener('click', openLaunchModal);
        el('launchModalX')?.addEventListener('click', () => el('launchModal')?.classList.remove('open'));
        el('launchModalApply')?.addEventListener('click', () => {
            el('launchModal')?.classList.remove('open');
            App.toast('Применено', 'Нажмите «Сохранить», чтобы записать параметры.', 'info');
        });
        el('launchModalReset')?.addEventListener('click', () => {
            el('launchExtra').value = launchBaseline?.extraArguments ?? '';
            markLaunchDirty();
            openLaunchModal();
        });
        el('launchModal')?.addEventListener('click', (e) => { if (e.target.id === 'launchModal') el('launchModal').classList.remove('open'); });
    }

    /** Сбрасывает метаданные карты и грузит её заново для текущего сервера. */
    async function reloadMap() {
        mapInfo = null;
        mapReady = false;
        liveMap?.reset();
        initLiveMap();
        await ensureMapInfo();
    }

    return {
        start, stop, bind, reset, reloadMap, loadConfig, loadLive, loadDatabase,
        loadChat, loadViolations, loadKills, loadCommands, loadEvents,

        // Показ трека на живой карте (кнопка в карточке игрока и строке списка).
        showTrackOnMap, showPlayerTrackById,

        // Общие помощники для визуального редактора наборов (loadouts.js).
        livePlayers: () => livePlayers,
        database: () => databaseRows,
        catalog: () => catalogItems,
        ensureCatalog, iconFor, groupFitsSlot,
        SLOT_LAYOUT, BODY_ROWS, GROUP_LABELS, CHARACTER_SVG,
        ensureCharacterFigure,
        buildSlot, buildBodyPart, buildCargoItem
    };
})();
