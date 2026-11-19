'use strict';

/* ============================================================
   DayZPulse — панель управления
   Без inline-скриптов, без innerHTML с данными, CSRF на каждый
   изменяющий запрос.
   ============================================================ */

const CSRF_COOKIE = 'dayzpulse_csrf';
const CSRF_HEADER = 'X-CSRF-Token';
const VIEWS = ['servers', 'dashboard', 'console', 'configs', 'mods', 'logs', 'players', 'livemap', 'chat', 'anticheat',
    'backups', 'schedule', 'service', 'settings', 'security'];

const TITLES = {
    servers: ['Все серверы', 'список серверов DayZ'],
    dashboard: ['Дашборд', 'обзор сервера'],
    console: ['Консоль', 'живой вывод и команды'],
    configs: ['Конфигурация', 'файлы настроек сервера'],
    mods: ['Моды', 'состояние и установка мода DayZPulse'],
    logs: ['Логи', 'логи сервера, команды и резервные копии'],
    players: ['Игроки', 'онлайн, база игроков и команды'],
    livemap: ['Живая карта', 'позиции игроков и транспорта'],
    chat: ['Чат', 'внутриигровые сообщения'],
    anticheat: ['Античит', 'нарушения и килфид'],
    backups: ['Резервные копии', 'создание и восстановление'],
    schedule: ['Расписание', 'автоматические задачи'],
    service: ['Служба', 'Windows-служба и приложение'],
    settings: ['Настройки', 'параметры панели и сервера'],
    security: ['Безопасность', 'владелец, защита и аудит']
};

/** Разделы, которые требуют выбранного сервера. */
const SERVER_VIEWS = ['dashboard', 'console', 'configs', 'mods', 'logs', 'players', 'livemap', 'chat', 'anticheat',
    'backups', 'schedule', 'settings'];

const $ = (id) => document.getElementById(id);

const SERVER_KEY = 'dayzpulse.currentServer';

let currentView = 'dashboard';
let lastStatus = null;
let activeSocket = null;

// Права текущего пользователя (владелец — всё). Приходят из /api/auth/status.
let sessionIsOwner = true;
let sessionSections = null;      // null = все разделы
let sessionServers = null;       // null = все серверы
let reconnectTimer = null;
let lastConsoleServer = null;
let loadedSettings = null;
let editingJobId = null;
let confirmAction = null;

/** Список серверов и выбранный сейчас. */
let servers = [];
let currentServerId = null;

/** Текущий выбранный сервер доступен Steam-окну (см. steam.js). */
window.dayzCurrentServerId = () => currentServerId;

/* ============================================================ серверы */

function server(id) {
    return servers.find((s) => s.id === id) ?? null;
}

function currentServer() {
    return server(currentServerId) ?? servers[0] ?? null;
}

/** Путь API в контексте выбранного сервера. */
function sp(path) {
    const id = currentServerId;
    return id ? `/servers/${encodeURIComponent(id)}${path}` : path;
}

async function loadServers(options = {}) {
    try {
        const data = await api('/servers');

        // Администратору показываем только разрешённые ему серверы.
        const incoming = (data.servers ?? []).filter((s) => canUseServer(s.id));

        // Сравниваем состав: перерисовываем карточки только при изменениях,
        // иначе перетаскивание и позиции нод сбрасывались бы каждые 5 секунд.
        const sameSet = incoming.length === servers.length &&
            incoming.every((s) => servers.some((prev) => prev.id === s.id));

        servers = incoming;

        const fromHash = new URLSearchParams(location.hash.split('?')[1] ?? '').get('server');
        let preferred = fromHash ?? currentServerId;

        if (!preferred) {
            try { preferred = localStorage.getItem(SERVER_KEY); } catch { /* недоступно */ }
        }

        if (!preferred || !server(preferred)) {
            preferred = servers[0]?.id ?? null;
        }

        currentServerId = preferred;

        renderServersSummary(data);

        // Полная перерисовка нужна только при первом рендере или смене состава.
        if (options.force || !sameSet || !$('srvCards')?.childElementCount) {
            renderSidebarServers();
            renderServerCards();
        } else {
            updateServerStates();
        }
    } catch (error) {
        toast('Не удалось получить список серверов', error.message, 'error');
    }
}

/** Обновляет только состояние, не трогая позиции карточек. */
function updateServerStates() {
    const box = $('serverList');
    if (box) {
        const rows = [...box.querySelectorAll('.srv')];
        for (const row of rows) {
            const item = server(row.dataset.serverId);
            if (!item) continue;

            row.classList.toggle('active', item.id === currentServerId);
            const dot = row.querySelector('.sdot');
            if (dot) dot.className = 'sdot ' + serverDot(item);
            const meta = row.querySelector('.sname small');
            if (meta) meta.textContent = `${item.mapLabel ?? ''} · ${item.playersAlive}/${item.players} · ${ApiEndpointsText(item)}`;
        }
    }

    const cards = $('srvCards');
    if (cards) {
        for (const card of cards.querySelectorAll('.srv-card')) {
            const item = server(card.dataset.serverId);
            if (!item) continue;

            card.classList.toggle('active', item.id === currentServerId);
            const dot = card.querySelector('.head .sdot');
            if (dot) dot.className = 'sdot ' + serverDot(item);
            const tag = card.querySelector('.head .tag');
            if (tag) {
                tag.className = 'tag ' + (item.running ? 'on' : item.state === 'Crashed' ? 'off' : 'info');
                tag.textContent = item.stateText ?? (item.running ? 'Работает' : 'Остановлен');
            }

            const values = [...card.querySelectorAll('.metrics .v')];
            if (values[0]) values[0].textContent = `${item.playersAlive}/${item.players}`;
            if (values[1]) values[1].textContent = item.trackerConnected ? 'на связи' : 'нет';
            if (values[2]) values[2].textContent = item.running ? `${(item.cpuPercent ?? 0).toFixed(0)}%` : '—';
        }
    }

    scheduleWires();
}

function renderServersSummary(data) {
    const running = servers.filter((s) => s.running).length;

    setText('serversSummary', servers.length
        ? `серверов: ${servers.length} · работает ${running}`
        : 'серверов нет');

    setText('ovTotal', servers.length);
    setText('ovOnline', running);
    setText('ovPlayers', servers.reduce((sum, s) => sum + (s.playersAlive ?? 0), 0));

    // Заметки: бейдж с числом непрочитанных обновляется вместе со сводкой.
    refreshNotesBadge();
    setText('pageCrumb', servers.length
        ? `${servers.length} серв. · ${running} работает`
        : 'нет серверов');

    const welcome = $('ovWelcome');
    const stats = $('ovStats');
    if (welcome) welcome.style.display = servers.length === 0 ? 'block' : 'none';
    if (stats) stats.style.display = servers.length === 0 ? 'none' : 'block';

    // В шапке нет смысла показывать состояние, игроков и перезапуск,
    // пока не добавлен ни один сервер.
    const topbar = document.querySelector('.topbar');
    topbar?.classList.toggle('no-servers', servers.length === 0);
    topbar?.classList.toggle('all-servers', currentView === 'servers' || servers.length === 0);

    renderServerPill();
}

/**
 * Пилюля выбранного сервера в шапке. Видна на страницах конкретного
 * сервера; на «Все серверы» прячется, потому что там выбор неважен.
 */
function renderServerPill() {
    const pill = $('srvPill');
    if (!pill) return;

    const item = currentServer();
    const show = !!item && currentView !== 'servers';

    pill.classList.toggle('hidden', !show);
    // Когда видна пилюля сервера, подзаголовок раздела избыточен:
    // он занимает место, и шапка переполняется на средних экранах.
    document.querySelector('.topbar')?.classList.toggle('has-srv-pill', show);
    if (!show) return;

    setText('srvPillName', item.name ?? item.id);
    $('srvPillDot').className = 'sdot ' + serverDot(item);
    pill.title = `${item.name ?? item.id} · ${item.stateText ?? ''} — открыть список серверов`;

    // Пилюля меняет ширину шапки (прячет подзаголовок) — пересчитываем режим.
    if (typeof scheduleHeadCompact === 'function') scheduleHeadCompact();
}

/** Класс точки состояния по серверу. */
function serverDot(server) {
    if (server.state === 'Crashed' || server.state === 'Failed') return 'offline';
    if (server.running) return 'online';
    if (server.state === 'Starting' || server.state === 'Stopping' || server.state === 'Restarting') return 'restart';
    return 'offline';
}

/** Список серверов в боковой панели. */
function renderSidebarServers() {
    const box = $('serverList');
    if (!box) return;

    clearChildren(box);

    if (!servers.length) {
        const empty = document.createElement('div');
        empty.className = 'empty-mini';
        empty.textContent = 'Пока нет серверов';
        box.appendChild(empty);
        return;
    }

    for (const item of servers) {
        const row = document.createElement('div');
        row.className = 'srv' + (item.id === currentServerId ? ' active' : '');
        row.dataset.tip = item.name;
        row.dataset.serverId = item.id;

        const dot = document.createElement('span');
        dot.className = 'sdot ' + serverDot(item);
        row.appendChild(dot);

        const name = document.createElement('span');
        name.className = 'sname';
        name.textContent = item.name;

        const meta = document.createElement('small');
        meta.textContent = `${item.mapLabel ?? ''} · ${item.playersAlive}/${item.players} · ${ApiEndpointsText(item)}`;
        name.appendChild(meta);
        row.appendChild(name);

        const remove = document.createElement('span');
        remove.className = 'sdel';
        remove.title = 'Удалить сервер';
        remove.textContent = '✕';
        remove.addEventListener('click', (event) => {
            event.stopPropagation();
            confirmRemoveServer(item);
        });
        row.appendChild(remove);

        row.addEventListener('click', () => selectServer(item.id));
        box.appendChild(row);
    }
}

/** Краткое состояние сервера словами. */
function ApiEndpointsText(item) {
    if (item.stateText) return item.stateText.toLowerCase();
    return item.running ? 'работает' : 'остановлен';
}

/** Карточки серверов на странице «Все серверы». */
function renderServerCards() {
    const wrap = $('srvCards');
    if (!wrap) return;

    clearChildren(wrap);

    const positions = loadNodePositions();

    for (const item of servers) {
        const color = item.color || '#3fb950';
        const card = document.createElement('div');
        card.className = 'srv-card' + (item.id === currentServerId ? ' active' : '');
        card.dataset.serverId = item.id;
        card.dataset.directory = item.baseDirectory ?? item.directory ?? '';
        card.style.setProperty('--nh', hexRgba(color, 0.28));
        card.style.setProperty('--nh2', color);

        // Позиция, сохранённая после перетаскивания.
        const saved = positions[item.id];
        if (saved) {
            card.style.transform = `translate(${saved.x}px, ${saved.y}px)`;
        }

        // Разъёмы (пины) по бокам — как у нод.
        const pinsLeft = document.createElement('div');
        pinsLeft.className = 'pins-l';
        const pinsRight = document.createElement('div');
        pinsRight.className = 'pins-r';
        for (let i = 0; i < 2; i++) {
            const pin = document.createElement('span');
            pin.className = 'pinx';
            pin.style.setProperty('--pin', color);
            pinsLeft.appendChild(pin);
        }
        const rightPin = document.createElement('span');
        rightPin.className = 'pinx';
        rightPin.style.setProperty('--pin', color);
        pinsRight.appendChild(rightPin);
        card.appendChild(pinsLeft);
        card.appendChild(pinsRight);

        const inner = document.createElement('div');
        inner.className = 'inner';

        // заголовок
        const head = document.createElement('div');
        head.className = 'head';

        const dot = document.createElement('span');
        dot.className = 'sdot ' + serverDot(item);
        dot.style.background = color;
        head.appendChild(dot);

        const title = document.createElement('div');
        const name = document.createElement('b');
        name.textContent = item.name;
        const meta = document.createElement('small');
        meta.textContent = `${item.mapLabel ?? ''} · порт ${item.port}`;
        title.appendChild(name);
        title.appendChild(meta);
        head.appendChild(title);

        const tag = document.createElement('span');
        tag.className = 'tag ' + (item.running ? 'on' : item.state === 'Crashed' ? 'off' : 'info');
        tag.style.marginLeft = 'auto';
        tag.textContent = item.stateText ?? (item.running ? 'Работает' : 'Остановлен');
        head.appendChild(tag);

        inner.appendChild(head);

        // Пока сервер DayZ не установлен, вместо метрик показываем загрузку:
        // игроков, мода и CPU у ещё не скачанного сервера не бывает.
        if (!item.installed) {
            const download = document.createElement('div');
            download.className = 'card-download';

            const note = document.createElement('div');
            note.className = 'help';
            note.textContent = 'Сервер DayZ ещё не скачан в эту папку.';

            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'btn primary block';
            button.textContent = 'Скачать сервер';
            button.addEventListener('click', () => downloadServer(item, card));

            const bar = document.createElement('div');
            bar.className = 'node-progress hidden';
            bar.innerHTML =
                '<div class="node-progress-head">' +
                '<span class="node-progress-phase">подготовка</span>' +
                '<span class="node-progress-value">0%</span>' +
                '</div>' +
                '<div class="node-progress-rail"><i style="width:0%"></i></div>';

            download.appendChild(note);
            download.appendChild(button);
            download.appendChild(bar);
            inner.appendChild(download);
        } else {
            // метрики
            const metrics = document.createElement('div');
            metrics.className = 'metrics';
            const metricData = [
                ['Игроки', `${item.playersAlive}/${item.players}`],
                ['Мод', item.trackerConnected ? 'на связи' : 'нет'],
                ['CPU', item.running ? `${(item.cpuPercent ?? 0).toFixed(0)}%` : '—']
            ];
            for (const [key, value] of metricData) {
                const cell = document.createElement('div');
                const k = document.createElement('div');
                k.className = 'k';
                k.textContent = key;
                const v = document.createElement('div');
                v.className = 'v';
                v.textContent = value;
                cell.appendChild(k);
                cell.appendChild(v);
                metrics.appendChild(cell);
            }
            inner.appendChild(metrics);
        }

        // кнопки
        const foot = document.createElement('div');
        foot.className = 'foot';

        foot.appendChild(cardButton('Открыть', 'primary', () => {
            selectServer(item.id).then(() => showView('dashboard'));
        }));
        foot.appendChild(cardButton('↻', '', () => restartServer(item)));
        foot.appendChild(cardButton('⚙ Настроить', '', () => {
            selectServer(item.id).then(() => showView('settings'));
        }));
        foot.appendChild(cardButton('Удалить', 'danger', () => confirmRemoveServer(item)));

        inner.appendChild(foot);
        card.appendChild(inner);
        wrap.appendChild(card);
    }

    // карточка добавления
    const add = document.createElement('div');
    add.className = 'add-card';
    const addInner = document.createElement('div');
    addInner.style.textAlign = 'center';
    const plus = document.createElement('div');
    plus.className = 'plus';
    plus.textContent = '＋';
    const label = document.createElement('div');
    label.className = 'lbl';
    label.textContent = 'Добавить сервер';
    addInner.appendChild(plus);
    addInner.appendChild(label);
    add.appendChild(addInner);
    add.addEventListener('click', openServerCreate);
    wrap.appendChild(add);

    // Провода и перетаскивание включаются после отрисовки карточек.
    makeDraggable();
    requestAnimationFrame(() => {
        drawWires();
        makeDraggable();
    });
    applyBlueprintTransform();
}

function cardButton(text, kind, handler) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'btn sm' + (kind ? ' ' + kind : '');
    button.textContent = text;
    if (kind === 'danger') button.style.marginLeft = 'auto';
    button.addEventListener('click', handler);
    return button;
}

/** Цвет из HEX в rgba() для мягкого свечения карточки. */
function hexRgba(hex, alpha) {
    const value = (hex ?? '').replace('#', '');
    if (value.length !== 6) return `rgba(63,185,80,${alpha})`;
    const r = parseInt(value.slice(0, 2), 16);
    const g = parseInt(value.slice(2, 4), 16);
    const b = parseInt(value.slice(4, 6), 16);
    return `rgba(${r},${g},${b},${alpha})`;
}

async function restartServer(item) {
    UI.confirm(`Перезапустить «${item.name}»? Игроки будут отключены.`, async () => {
        if (currentServerId !== item.id) {
            await selectServer(item.id);
        }

        try {
            await api(sp('/restart'), { method: 'POST' });
            toast('Перезапуск', `«${item.name}»: команда выполнена.`, 'info');
            await loadServers();
        } catch (error) {
            toast('Не удалось перезапустить', error.message, 'error');
        }
    }, 'Перезапустить');
}

function confirmRemoveServer(item) {
    if (servers.length <= 1) {
        toast('Удаление невозможно', 'В панели должен остаться хотя бы один сервер.', 'warn');
        return;
    }

    const directory = item.baseDirectory || item.directory || '';
    const hasFiles = !!item.installed && directory.length >= 3;

    async function remove(deleteFiles) {
        try {
            const result = await api(
                '/servers/' + encodeURIComponent(item.id) + (deleteFiles ? '?deleteFiles=true' : ''),
                { method: 'DELETE' });

            toast('Сервер удалён',
                deleteFiles && result.filesRemoved
                    ? `${item.name} · папка ${result.directory} удалена`
                    : result.warning
                        ? result.warning
                        : `${item.name} · удалён только из панели`,
                result.warning ? 'warn' : 'info', 10000);

            if (currentServerId === item.id) currentServerId = null;
            await loadServers();
        } catch (error) {
            toast('Не удалось удалить', error.message, 'error', 9000);
        }
    }

    const question = hasFiles
        ? `Удалить сервер «${item.name}»?\n\n` +
          `Папка сервера со всеми файлами будет удалена безвозвратно:\n${directory}\n\n` +
          `Если нужно сохранить файлы — выберите «Только из панели».`
        : `Удалить сервер «${item.name}» из панели? Файлы сервера на диске останутся.`;

    UI.confirm(question, () => remove(hasFiles), hasFiles ? 'Удалить с файлами' : 'Удалить',
        hasFiles ? { label: 'Только из панели', action: () => remove(false) } : null);
}

function openServerCreate(prefillDirectory) {
    const modal = $('serverModal');
    if (!modal) return;

    // Может вызываться как обработчик клика: тогда аргумент — событие, а не путь.
    const prefill = typeof prefillDirectory === 'string' ? prefillDirectory : '';

    // Первый сервер создаётся по папке из первого запуска: её менять нельзя.
    // Для последующих серверов поле пустое, папку выбирает пользователь.
    const isFirst = servers.length === 0 && defaultServerDirectory.length >= 3;
    const directory = isFirst ? defaultServerDirectory : prefill;
    lockDirectoryField(isFirst, directory);

    $('serverModalTitle').textContent = 'Новый сервер DayZ';
    $('fName').value = '';
    $('fPort').value = nextFreePort();
    $('fColor').value = '#3fb950';
    $('serverModalError').classList.add('hidden');

    modal.classList.add('open');
    setTimeout(() => $('fName').focus(), 50);
}

/**
 * Первому серверу папка задаётся из первого запуска и не меняется.
 * Для остальных поле свободно.
 */
function lockDirectoryField(locked, directory) {
    const input = $('fDir');
    const browse = $('fDirBrowse');
    const help = $('fDirHelp');
    if (!input) return;

    input.value = locked ? directory : '';
    input.readOnly = locked;
    input.classList.toggle('locked', locked);

    if (browse) browse.style.display = locked ? 'none' : '';

    if (help) {
        help.textContent = locked
            ? 'Папка из первого запуска панели. Изменить её нельзя — она задаётся при создании владельца.'
            : 'Где лежит сервер DayZ: DayZServer_x64.exe, serverDZ.cfg и profiles.';
    }
}

/** Папка, выбранная при создании владельца: подставляется в новый сервер. */
let defaultServerDirectory = '';

function closeServerModal() {
    $('serverModal')?.classList.remove('open');
}

/** Подбирает свободный блок портов, не пересекающийся с другими серверами. */
function nextFreePort() {
    const used = new Set(servers.map((s) => Number(s.port)));
    for (let port = 2302; port <= 65000; port += 10) {
        if (used.has(port)) continue;
        if (used.size > 0 && port >= 2302 && port < 2402) continue; // блок первого сервера
        return port;
    }
    return 2302;
}

async function saveServerModal() {
    const error = $('serverModalError');
    error.classList.add('hidden');

    const name = $('fName').value.trim();
    const directory = $('fDir').value.trim();
    const port = Number($('fPort').value);

    if (name.length < 1) {
        showServerError('Укажите название сервера.');
        $('fName').focus();
        return;
    }

    if (directory.length < 3) {
        showServerError('Укажите папку сервера, например C:\\DayZServer');
        $('fDir').focus();
        return;
    }

    const button = $('serverModalSave');
    button.disabled = true;
    button.textContent = 'Создание…';

    try {
        const result = await api('/servers', {
            method: 'POST',
            body: {
                name,
                directory,
                port,
                color: $('fColor').value
            }
        });

        closeServerModal();
        toast('Сервер создан',
            `«${name}» · порт ${result.port}. Мод получит config.json автоматически.`, 'info', 8000);

        await loadServers();
        await selectServer(result.id);
        showView('dashboard');
    } catch (exception) {
        showServerError(exception.message);
    } finally {
        button.disabled = false;
        button.textContent = 'Создать';
    }
}

function showServerError(message) {
    const box = $('serverModalError');
    if (!box) return;
    box.textContent = message;
    box.className = 'auth-alert';
}

$('serverModalSave')?.addEventListener('click', saveServerModal);
$('serverModalCancel')?.addEventListener('click', closeServerModal);
$('serverModalX')?.addEventListener('click', closeServerModal);
$('fDirBrowse')?.addEventListener('click', () => FolderPicker.open('fDir'));
$('serverModal')?.addEventListener('click', (event) => {
    if (event.target.id === 'serverModal') closeServerModal();
});

/** Переключает активный сервер и перезагружает текущий раздел. */
async function selectServer(id) {
    if (!id || id === currentServerId) return;

    // Уходим с настроек другого сервера с несохранёнными правками —
    // спрашиваем так же, как при смене раздела.
    if (quickHasChanges() && !await confirmLeaveQuick()) {
        return;
    }

    currentServerId = id;
    try { localStorage.setItem(SERVER_KEY, id); } catch { /* недоступно */ }

    // Данные предыдущего сервера больше не показываем.
    clearChildren($('console'));
    clearChildren($('liveRows'));
    resetDashboard();

    // Переключение должно ощущаться мгновенным: сразу обновляем подсветку
    // выбранного сервера, а данные и статус подтягиваем параллельно.
    renderSidebarServers();
    renderServerCards();

    // Сводка настроек берётся из уже загруженных данных сервера — рисуем сразу.
    renderDashboardSettings(server(currentServerId) ?? servers[0]);

    // Пилюля сервера в шапке должна показать новый сервер сразу, не дожидаясь
    // опроса статуса (иначе оставалось прежнее имя).
    renderServerPill();
    scheduleHeadCompact();

    if (typeof Players !== 'undefined') Players.reset();

    await Promise.all([refreshStatus(), refreshCurrentViewAsync()]);
}

/** То же, что refreshCurrentView, но без ожидания: используется при переключении. */
function refreshCurrentViewAsync() {
    try { refreshCurrentView(); } catch { /* раздел сам покажет ошибку */ }
    return Promise.resolve();
}

function refreshCurrentView() {
    switch (currentView) {
        case 'servers': loadServers(); break;
        case 'dashboard': startDashboard(); refreshStatus(); break;
        case 'configs': loadQuickSettings(); break;
        case 'mods': Players?.loadConfig(); Mods?.load(); break;
        case 'logs': Logs?.load(); Players?.loadCommands(); break;
        case 'backups': loadBackups(); break;
        case 'schedule': loadSchedule(); break;
        case 'settings': loadSettings(); break;
        case 'players': if (typeof Players !== 'undefined') { Players.loadConfig(); Players.loadLive(); Players.loadDatabase(); } break;
        case 'livemap': if (typeof Players !== 'undefined') { Players.reset(); Players.start(); } break;
        case 'chat': Players?.loadChat(); break;
        case 'anticheat': Players?.loadViolations(); Players?.loadKills(); Players?.loadEvents(); break;
    }
}

/* ============================================================ ноды серверов */

/** Позиции нод и состояние холста сохраняются между запусками. */
const BP_KEY = 'dayzpulse.blueprint';
const NODE_KEY = 'dayzpulse.nodes';

let bpZoom = 1;
let bpPanX = 0;
let bpPanY = 0;
let dragState = null;

function loadBlueprint() {
    try {
        const saved = JSON.parse(localStorage.getItem(BP_KEY) ?? 'null');
        if (saved && typeof saved.zoom === 'number') {
            bpZoom = Math.min(1.8, Math.max(0.5, saved.zoom));
            bpPanX = Number(saved.panX) || 0;
            bpPanY = Number(saved.panY) || 0;
        }
    } catch { /* недоступно */ }
}

function saveBlueprint() {
    try {
        localStorage.setItem(BP_KEY, JSON.stringify({ zoom: bpZoom, panX: bpPanX, panY: bpPanY }));
    } catch { /* недоступно */ }
}

function loadNodePositions() {
    try {
        return JSON.parse(localStorage.getItem(NODE_KEY) ?? '{}') ?? {};
    } catch {
        return {};
    }
}

function saveNodePositions(positions) {
    try {
        localStorage.setItem(NODE_KEY, JSON.stringify(positions));
    } catch { /* недоступно */ }
}

function applyBlueprintTransform() {
    const cards = $('srvCards');
    const grid = $('bpGrid');
    if (!cards) return;

    cards.style.transformOrigin = '0 0';
    cards.style.transform = `translate(${bpPanX}px, ${bpPanY}px) scale(${bpZoom})`;

    // Сетка сдвигается медленнее карточек — создаётся ощущение глубины.
    // Сдвиг идёт через background-position, чтобы сетка не увеличивала
    // область прокрутки контента.
    if (grid) {
        grid.style.backgroundPosition = `${bpPanX * 0.4}px ${bpPanY * 0.4}px`;
        grid.style.transform = `scale(${bpZoom})`;
    }

    scheduleWires();
}

function setBlueprintZoom(zoom) {
    bpZoom = Math.min(1.8, Math.max(0.5, zoom));
    applyBlueprintTransform();
    saveBlueprint();
}

/** Провода между соседними нодами (порядок карточек). */
function scheduleWires() {
    requestAnimationFrame(drawWires);
}

function drawWires() {
    const cards = $('srvCards');
    const stage = $('bpStage');
    if (!cards || !stage) return;

    let svg = stage.querySelector('.bp-wire');
    if (!svg) {
        svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('class', 'bp-wire');
        stage.insertBefore(svg, stage.firstChild);
    }

    // У SVG нельзя очищать через firstChild: узлы созданы в пространстве имён.
    while (svg.firstChild) svg.removeChild(svg.firstChild);

    const stageRect = stage.getBoundingClientRect();
    svg.setAttribute('viewBox', `0 0 ${stageRect.width} ${stageRect.height}`);
    svg.style.width = stageRect.width + 'px';
    svg.style.height = stageRect.height + 'px';

    const list = [...cards.querySelectorAll('.srv-card')];
    if (list.length < 2) return;

    for (let i = 0; i < list.length - 1; i++) {
        const a = list[i];
        const b = list[i + 1];
        const ar = a.getBoundingClientRect();
        const br = b.getBoundingClientRect();

        const ax = ar.right - stageRect.left;
        const ay = ar.top - stageRect.top + ar.height / 2;
        const bx = br.left - stageRect.left;
        const by = br.top - stageRect.top + br.height / 2;

        const dx = Math.max(40, Math.abs(bx - ax) * 0.5);

        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', `M ${ax} ${ay} C ${ax + dx} ${ay}, ${bx - dx} ${by}, ${bx} ${by}`);

        const color = (a.style.getPropertyValue('--nh2') || '#3fb950').trim();
        path.style.setProperty('--wire', color);
        svg.appendChild(path);
    }
}

/** Разрешает перетаскивание нод. */
function makeDraggable() {
    const stage = $('srvCards');
    if (!stage) return;

    stage.querySelectorAll('.srv-card').forEach((card) => {
        if (card.dataset.dragReady) return;
        card.dataset.dragReady = '1';
        card.classList.add('draggable');

        card.addEventListener('pointerdown', (event) => {
            if (event.target.closest('button') || event.target.closest('input') || event.target.closest('select')) return;

            const match = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(card.style.transform || '');
            dragState = {
                card,
                startX: event.clientX,
                startY: event.clientY,
                baseX: match ? parseFloat(match[1]) || 0 : 0,
                baseY: match ? parseFloat(match[2]) || 0 : 0,
                moved: false
            };

            card.classList.add('drag');
            card.setPointerCapture(event.pointerId);
            event.preventDefault();
        });

        card.addEventListener('pointermove', (event) => {
            if (!dragState || dragState.card !== card) return;

            // Движение курсора переводим в координаты холста с учётом зума.
            const dx = (event.clientX - dragState.startX) / bpZoom;
            const dy = (event.clientY - dragState.startY) / bpZoom;

            if (Math.abs(dx) > 2 || Math.abs(dy) > 2) dragState.moved = true;

            card.style.transform = `translate(${dragState.baseX + dx}px, ${dragState.baseY + dy}px)`;
            card.style.zIndex = 30;
            scheduleWires();
        });

        const finish = () => {
            if (!dragState || dragState.card !== card) return;

            card.classList.remove('drag');
            card.style.zIndex = '';
            const moved = dragState.moved;
            dragState = null;

            // Запоминаем позицию, чтобы она сохранилась после перезагрузки.
            const match = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(card.style.transform || '');
            if (moved && match && card.dataset.serverId) {
                const positions = loadNodePositions();
                positions[card.dataset.serverId] = { x: parseFloat(match[1]) || 0, y: parseFloat(match[2]) || 0 };
                saveNodePositions(positions);
            }

            scheduleWires();
        };

        card.addEventListener('pointerup', finish);
        card.addEventListener('pointercancel', finish);
        card.addEventListener('dblclick', () => {
            card.style.transform = '';
            if (card.dataset.serverId) {
                const positions = loadNodePositions();
                delete positions[card.dataset.serverId];
                saveNodePositions(positions);
            }
            scheduleWires();
        });
    });
}

/** Возвращает все ноды в сетку. */
function resetNodes() {
    const cards = $('srvCards');
    if (!cards) return;

    cards.querySelectorAll('.srv-card').forEach((card) => {
        card.style.transform = '';
        card.style.zIndex = '';
    });

    saveNodePositions({});
    scheduleWires();
    toast('Позиции сброшены', 'Карточки вернулись в сетку.', 'info');
}

/** Зум колесом и сдвиг пустого места. */
function initBlueprintPanZoom() {
    const stage = $('bpStage');
    if (!stage || stage.dataset.pz) return;
    stage.dataset.pz = '1';

    stage.addEventListener('wheel', (event) => {
        // Внутри карточки колесо не перехватываем.
        if (event.target.closest('.srv-card') && !event.ctrlKey) return;

        event.preventDefault();
        const factor = event.deltaY < 0 ? 1.1 : 1 / 1.1;
        const rect = stage.getBoundingClientRect();
        const mouseX = event.clientX - rect.left;
        const mouseY = event.clientY - rect.top;

        const previous = bpZoom;
        bpZoom = Math.min(1.8, Math.max(0.5, bpZoom * factor));
        const k = bpZoom / previous;

        bpPanX = mouseX - k * (mouseX - bpPanX);
        bpPanY = mouseY - k * (mouseY - bpPanY);

        applyBlueprintTransform();
        saveBlueprint();
    }, { passive: false });

    let panning = null;

    stage.addEventListener('pointerdown', (event) => {
        if (event.target.closest('.srv-card') || event.target.closest('button')) return;
        panning = { x: event.clientX, y: event.clientY, px: bpPanX, py: bpPanY };
        stage.style.cursor = 'grabbing';
    });

    window.addEventListener('pointermove', (event) => {
        if (!panning) return;
        bpPanX = panning.px + (event.clientX - panning.x);
        bpPanY = panning.py + (event.clientY - panning.y);
        applyBlueprintTransform();
    });

    window.addEventListener('pointerup', () => {
        if (!panning) return;
        panning = null;
        stage.style.cursor = '';
        saveBlueprint();
    });

    // Двойной щелчок по пустому месту возвращает вид в исходный.
    stage.addEventListener('dblclick', (event) => {
        if (event.target.closest('.srv-card')) return;
        bpZoom = 1;
        bpPanX = 0;
        bpPanY = 0;
        applyBlueprintTransform();
        saveBlueprint();
    });
    window.addEventListener('resize', () => {
        applyBlueprintTransform();
        drawWires();
    });

    window.addEventListener('scroll', scheduleWires, true);
}

$('bpZoomReset')?.addEventListener('click', () => setBlueprintZoom(1));
$('bpResetNodes')?.addEventListener('click', () => {
    resetNodes();
    bpZoom = 1;
    bpPanX = 0;
    bpPanY = 0;
    applyBlueprintTransform();
    saveBlueprint();
});

/* ============================================================ live-канал */

let liveSocket = null;
let liveReconnect = null;

/**
 * Подписка на события панели. Сервер сам присылает изменения,
 * поэтому периодические запросы состояния не нужны.
 */
function connectLive() {
    if (liveSocket && (liveSocket.readyState === WebSocket.OPEN || liveSocket.readyState === WebSocket.CONNECTING)) {
        return;
    }

    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    liveSocket = new WebSocket(`${protocol}//${location.host}/ws/events`);

    liveSocket.onmessage = (event) => {
        let message;
        try { message = JSON.parse(event.data); } catch { return; }

        switch (message.type) {
            case 'servers':
                applyServersPush(message.data);
                break;

            case 'server-state':
                if (message.data?.serverId === currentServerId) {
                    applyStatus(message.data.status);

                    // Сервер остановился/запустился — дашборд и «Игроки» должны
                    // сразу показать актуальные цифры (или обнулить их).
                    if (currentView === 'dashboard') loadDashboard();
                    if (currentView === 'players') { Players.loadLive(); }
                }
                break;

            case 'steam':
                if (typeof SteamUI !== 'undefined') SteamUI.applyPush(message.data);
                applyServerInstallPush(message.data);
                break;

            case 'web-ui':
                // Панель нашла обновление веб-части: отмечаем точку и
                // показываем уведомление с кнопками.
                if (message.data?.available && !message.data?.upToDate) {
                    $('webUiDot')?.classList.add('on');
                    if (currentView === 'service') loadWebUi();
                    showWebUiUpdateNotice(message.data.remoteVersion);
                }
                break;
        }
    };

    liveSocket.onclose = () => {
        clearTimeout(liveReconnect);
        liveReconnect = setTimeout(connectLive, 2500);
    };

    liveSocket.onerror = () => liveSocket?.close();
}

/** Применяет присланную сводку серверов без запроса от браузера. */
function applyServersPush(data) {
    const incoming = data?.servers ?? [];
    const sameSet = incoming.length === servers.length &&
        incoming.every((s) => servers.some((prev) => prev.id === s.id));

    // Если у сервера изменилось наличие установленного сервера,
    // карточку нужно перерисовать: меняется набор блока (метрики ↔ загрузка).
    const sameLayout = sameSet &&
        incoming.every((s) => {
            const prev = servers.find((p) => p.id === s.id);
            return prev && !!prev.installed === !!s.installed;
        });

    servers = incoming;

    if (!currentServerId || !server(currentServerId)) {
        currentServerId = servers[0]?.id ?? null;
    }

    renderServersSummary(data);

    if (!sameLayout || !$('srvCards')?.childElementCount) {
        renderSidebarServers();
        renderServerCards();
    } else {
        updateServerStates();
    }

    if (currentServerId) {
        const active = server(currentServerId);
        if (active) applyStatus(active);
    }

    // Наличие сервера на диске могло измениться (загрузка завершилась).
    // Пересчитываем заглушку, иначе она останется висеть до смены вкладки.
    const wasGated = syncServerGate();
    if (wasGated) {
        // Заглушка скрылась — раздел под ней ещё не был загружен.
        loadCurrentView();
    }
}

/** Загружает данные активного раздела (без смены вкладки). */
function loadCurrentView() {
    const name = currentView;

    if (name === 'servers') loadServers();
    if (name === 'dashboard') { startDashboard(); refreshStatus(); }
    if (name === 'console') loadRconStatus();
    if (name === 'configs') { loadQuickSettings(); Players?.loadConfig(); }
    if (name === 'mods') { Players?.loadConfig(); Mods?.load(); }
    if (name === 'logs') { Logs?.load(); Players?.loadCommands(); }
    if (name === 'backups') loadBackups();
    if (name === 'schedule') loadSchedule();
    if (name === 'service') { loadService(); loadApplicationInfo(); loadWebUi(); }
    if (name === 'settings') loadSettings();
    if (name === 'security') { loadSecurity(); loadAudit(); }
    if (name === 'players') { Players.start(); Players.loadConfig(); Players.loadDatabase(); if (typeof Loadouts !== 'undefined') Loadouts.load(); }
    if (name === 'livemap') { Players.start(); Players.loadLive(); }
    if (name === 'chat') { Players.start(); Players.loadChat(); }
    if (name === 'anticheat') { Players.loadViolations(); Players.loadKills(); Players.loadEvents(); }
}

/**
 * Показывает или скрывает заглушку «сервер не установлен» по текущему
 * состоянию выбранного сервера. Возвращает true, если заглушка только что
 * исчезла (раздел нужно загрузить).
 */
function syncServerGate() {
    const gate = $('serverGate');
    if (!gate) return false;

    const item = currentServer();
    const gated = !!(SERVER_VIEWS.includes(currentView) && item && !item.installed);

    if (gated) {
        applyServerGate();
    }

    gate.classList.toggle('hidden', !gated);
    gate.style.display = gated ? 'block' : 'none';

    // Разделы сервера показываем только когда сервер установлен.
    for (const view of VIEWS) {
        $('view-' + view)?.classList.toggle('active', view === currentView && !gated);
    }

    const [title, crumb] = TITLES[currentView] ?? [];
    setText('pageTitle', gated ? 'Сервер не установлен' : title);
    setText('pageCrumb', gated ? 'сначала скачайте сервер DayZ' : crumb);
    renderServerPill();

    // Заглушка исчезла — раздел, который под ней, не был загружен.
    const unblocked = gateActive && !gated;
    gateActive = gated;

    return unblocked;
}

/** Была ли заглушка активна при прошлой отрисовке. */
let gateActive = false;

/**
 * Показывает прогресс загрузки сервера DayZ из события live-канала.
 * Обновляет и карточку сервера на странице «Все серверы», и панель настроек.
 */
function applyServerInstallPush(data) {
    // Событие относится к папке сервера: без данных о ней ничего не делаем.
    if (!data || data.serverDirectory === undefined) return;

    serverInstallState = data;
    updateCardDownload(data);
    updateSettingsServerInstall(data);
    refreshGate(data);
}

/** Обновляет заглушку «сервер не установлен» и разблокирует разделы после загрузки. */
function refreshGate(data) {
    const item = currentServer();
    if (!item) return;

    const sameServer = (data.serverDirectory ?? '').replace(/[\\/]+$/, '').toLowerCase() ===
        (item.baseDirectory ?? '').replace(/[\\/]+$/, '').toLowerCase();

    if (!sameServer) return;

    if (data.serverInstalled) {
        // Загрузка завершена: отмечаем сервер установленным. Заглушку снимает
        // общий syncServerGate — он же загрузит и раздел под ней.
        const first = !item.installed;
        item.installed = true;

        if (syncServerGate()) {
            loadCurrentView();

            // Уведомляем один раз — при первом переходе в готовое состояние.
            if (first) {
                toast('Сервер готов', 'Сервер DayZ скачан — разделы панели доступны.', 'info', 6000);
            }

            return;
        }
    }

    // Заглушка ещё видна — обновляем её текст (прогресс, проверка файлов).
    if (SERVER_VIEWS.includes(currentView) && !item.installed) {
        applyServerGate();
    }
}

/** Текст «сколько скачано и общий вес». */
function formatDownloadAmount(done, total) {
    if (!done || !total) return '';
    return `${formatBytes(done)} из ${formatBytes(total)}`;
}

/** Текст «скорость скачивания». */
function formatDownloadSpeed(bytesPerSecond) {
    if (!bytesPerSecond || bytesPerSecond <= 0) return '';
    return formatBytes(bytesPerSecond) + '/с';
}

/**
 * Заполняет полосу прогресса значением и дополнительной строкой
 * (скорость, скачано, общий вес, время). Используется в карточке,
 * заглушке и настройках.
 */
function paintProgress(container, data, options = {}) {
    if (!container) return;

    const percent = Math.max(2, Math.min(100, Math.round(data.progress ?? 0)));
    const fill = container.querySelector('.node-progress-rail i');
    const value = container.querySelector('.node-progress-value');
    const phase = container.querySelector('.node-progress-phase');

    if (fill) fill.style.width = percent + '%';
    if (value) value.textContent = percent + '%';
    if (phase) phase.textContent = data.phase || 'загрузка';

    // Вторая строка: сколько скачано, общий вес, скорость.
    // В заглушке она не нужна: те же данные уже есть в подзаголовке.
    if (options.detail === false) {
        container.querySelector('.node-progress-detail')?.remove();
        return;
    }

    let detail = container.querySelector('.node-progress-detail');
    if (!detail && container.classList.contains('node-progress')) {
        detail = document.createElement('div');
        detail.className = 'node-progress-detail';
        container.appendChild(detail);
    }

    if (!detail) return;

    const amount = formatDownloadAmount(data.bytesDone, data.bytesTotal);
    const speed = formatDownloadSpeed(data.bytesPerSecond);

    if (data.phase && data.phase.includes('Проверка')) {
        detail.textContent = amount ? 'Проверено ' + amount : '';
        return;
    }

    const parts = [];
    if (amount) parts.push(amount);
    if (speed) parts.push(speed);
    detail.textContent = parts.join(' · ');
}

/** Прогресс загрузки в карточке сервера (страница «Все серверы»). */
function updateCardDownload(data) {
    const wrap = $('srvCards');
    if (!wrap) return;

    const normalized = (data.serverDirectory ?? '').replace(/[\\/]+$/, '').toLowerCase();

    for (const card of wrap.querySelectorAll('.srv-card')) {
        const path = (card.dataset.directory ?? '').replace(/[\\/]+$/, '').toLowerCase();
        if (path !== normalized) continue;

        const block = card.querySelector('.card-download');
        if (!block) continue;

        const bar = block.querySelector('.node-progress');
        const button = block.querySelector('button');
        const note = block.querySelector('.help');

        if (data.running) {
            bar?.classList.remove('hidden');
            paintProgress(bar, data);
            if (button) { button.disabled = true; button.textContent = 'Загрузка…'; }
            return;
        }

        if (data.error) {
            if (note) note.textContent = 'Ошибка: ' + data.error;
            if (button) { button.disabled = false; button.textContent = 'Повторить'; }
            return;
        }

        if (data.serverInstalled) {
            // Сервер скачан: возвращаем обычные метрики.
            const list = servers.find((s) => s.id === card.dataset.serverId);
            if (list) list.installed = true;
            renderServerCards();
            return;
        }
    }
}

/** Прогресс загрузки в настройках сервера (Настройки → Steam). */
function updateSettingsServerInstall(data) {
    const bar = $('steamServerBar');
    if (!bar) return;

    if (data.stage === 'server' && data.running) {
        bar.classList.remove('hidden');
        paintProgress(bar, data);
        $('steamServerBarLabel').textContent = data.phase || 'загрузка';

        const amount = formatDownloadAmount(data.bytesDone, data.bytesTotal);
        const speed = formatDownloadSpeed(data.bytesPerSecond);
        setText('steamServerHint', [data.phase || 'загрузка…', amount, speed]
            .filter(Boolean).join(' · '));

        const button = $('btnSteamServerInstall');
        if (button) { button.disabled = true; button.textContent = 'Загрузка…'; }
        return;
    }

    if (data.stage === 'server' && !data.running) {
        bar.classList.add('hidden');

        if (data.error) {
            setText('steamServerHint', 'ошибка: ' + data.error);
            const button = $('btnSteamServerInstall');
            if (button) { button.disabled = false; button.textContent = 'Скачать сервер'; }
        } else if (data.serverInstalled) {
            setText('steamServerHint', 'установлен: ' + (data.serverDirectory ?? ''));
            const button = $('btnSteamServerInstall');
            if (button) { button.disabled = true; button.textContent = 'Сервер установлен'; }
        }
    }
}

/** Загрузка сервера DayZ по кнопке в карточке. */
async function downloadServer(item, card) {
    const button = card?.querySelector('.card-download button');
    if (button) { button.disabled = true; button.textContent = 'Загрузка…'; }

    try {
        await api('/steam/server/install', { method: 'POST', body: { serverId: item.id } });
        toast('Загрузка сервера', `«${item.name}»: скачивание началось.`, 'info', 6000);
    } catch (error) {
        if (button) { button.disabled = false; button.textContent = 'Скачать сервер'; }
        const note = card?.querySelector('.card-download .help');
        if (note) note.textContent = 'Ошибка: ' + error.message;
        toast('Сервер не скачан', error.message, 'error', 9000);
    }
}

/* ============================================================ утилиты */
function readCookie(name) {
    const prefix = name + '=';
    for (const part of document.cookie.split(';')) {
        const value = part.trim();
        if (value.startsWith(prefix)) {
            return decodeURIComponent(value.slice(prefix.length));
        }
    }
    return '';
}

async function api(path, options = {}) {
    const method = options.method ?? 'GET';
    const opts = {
        method,
        credentials: 'same-origin',
        headers: { 'Accept': 'application/json' }
    };

    if (options.body !== undefined && !(options.body instanceof FormData)) {
        opts.headers['Content-Type'] = 'application/json';
        opts.body = JSON.stringify(options.body);
    } else if (options.body instanceof FormData) {
        opts.body = options.body;
    }

    if (method !== 'GET' && method !== 'HEAD') {
        opts.headers[CSRF_HEADER] = readCookie(CSRF_COOKIE);
    }

    const response = await fetch('/api' + path, opts);

    if (response.status === 401) {
        window.location.replace('/login.html');
        throw new Error('Требуется авторизация.');
    }

    if (response.status === 409 && path.startsWith('/auth')) {
        window.location.replace('/setup.html');
        throw new Error('Требуется создание владельца.');
    }

    const text = await response.text();
    let data = null;
    if (text) {
        try { data = JSON.parse(text); } catch { data = { error: text }; }
    }

    if (!response.ok) {
        throw new Error(data?.error ?? `Ошибка ${response.status}`);
    }

    return data;
}

/**
 * Показывает всплывающее уведомление.
 * options.sticky — не закрывать по таймеру (закрывается крестиком).
 * options.actions — кнопки: [{ label, kind, onClick }].
 */
function toast(title, message = '', kind = 'info', timeout = 5000, options = {}) {
    const box = document.createElement('div');
    box.className = 'toast' + (kind === 'error' ? ' error' : kind === 'warn' ? ' warn' : '')
        + (options.sticky ? ' sticky' : '');

    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'toast-x';
    close.setAttribute('aria-label', 'Закрыть');
    close.textContent = '×';
    close.addEventListener('click', () => box.remove());
    box.appendChild(close);

    const strong = document.createElement('b');
    strong.textContent = title;
    box.appendChild(strong);

    if (message) {
        const p = document.createElement('p');
        p.textContent = message;
        box.appendChild(p);
    }

    if (options.actions?.length) {
        const row = document.createElement('div');
        row.className = 'toast-actions';

        for (const action of options.actions) {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'btn sm' + (action.kind ? ' ' + action.kind : '');
            button.textContent = action.label;
            button.addEventListener('click', () => action.onClick?.(button, box));
            row.appendChild(button);
        }

        box.appendChild(row);
    }

    $('toasts').appendChild(box);

    // Залипающие уведомления держатся, пока их не закроют.
    if (!options.sticky && timeout > 0) {
        setTimeout(() => box.remove(), timeout);
    }

    return box;
}

function setText(id, value) {
    const element = $(id);
    if (element) element.textContent = value ?? '—';
}

function formatBytes(bytes) {
    if (bytes === null || bytes === undefined) return '—';
    const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
    let value = Number(bytes);
    let index = 0;
    while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; }
    return `${value.toFixed(value < 10 && index > 0 ? 1 : 0)} ${units[index]}`;
}

function formatDuration(seconds) {
    if (seconds === null || seconds === undefined || seconds < 0) return '—';
    const total = Math.floor(seconds);
    const d = Math.floor(total / 86400);
    const h = Math.floor((total % 86400) / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = total % 60;
    if (d > 0) return `${d}д ${h}ч`;
    if (h > 0) return `${h}ч ${m}м`;
    if (m > 0) return `${m}м ${s}с`;
    return `${s}с`;
}

function formatTime(value) {
    if (!value) return '—';
    const date = new Date(value);
    return isNaN(date) ? '—' : date.toLocaleString('ru-RU');
}

function clearChildren(element) {
    while (element.firstChild) element.removeChild(element.firstChild);
}

function cell(text, className) {
    const td = document.createElement('td');
    if (className) td.className = className;
    td.textContent = text ?? '';
    return td;
}

function askConfirm(text, action, okLabel) {
    UI.confirm(text, action, okLabel);
}

/* ============================================================ навигация */

/** Доступен ли раздел текущему пользователю. */
function canUseSection(name) {
    if (sessionIsOwner || sessionSections === null) return true;
    return sessionSections.includes(name);
}

/** Доступен ли сервер текущему пользователю. */
function canUseServer(serverId) {
    if (sessionIsOwner || sessionServers === null) return true;
    return sessionServers.includes(serverId);
}

/** Скрывает в меню разделы, недоступные администратору. */
function applySectionPermissions() {
    document.querySelectorAll('#sidebar button[data-view]').forEach((button) => {
        const allowed = canUseSection(button.dataset.view);
        button.classList.toggle('hidden', !allowed);
    });

    // Кнопка «Администраторы» — только владельцу.
    $('btnAdmins')?.classList.toggle('hidden', !sessionIsOwner);
}

function showView(name) {
    if (!VIEWS.includes(name)) name = 'servers';

    // Раздел, недоступный администратору, не открываем.
    if (!canUseSection(name)) {
        toast('Нет доступа', 'Этот раздел недоступен вашей учётной записи.', 'warn');
        name = VIEWS.find(canUseSection) ?? 'servers';
    }

    currentView = name;

    // Разделы сервера работают в контексте выбранного сервера.
    if (SERVER_VIEWS.includes(name) && !currentServer()) {
        toast('Сервер не выбран', 'Добавьте сервер или выберите его в списке слева.', 'warn');
        name = 'servers';
        currentView = name;
    }

    // Пока сервер DayZ не скачан, разделы сервера недоступны: показываем
    // заглушку с предложением скачать сервер.
    const gated = SERVER_VIEWS.includes(name) && currentServer() && !currentServer().installed;

    if (gated) {
        currentView = name;
    }

    // syncServerGate сам загружает раздел, если заглушка только что исчезла.
    const unblocked = syncServerGate();

    // На общей вкладке «Все серверы» состояние, игроки и перезапуск
    // относятся к конкретному серверу и не имеют смысла.
    document.querySelector('.topbar')?.classList.toggle(
        'all-servers', name === 'servers' || servers.length === 0);

    document.querySelectorAll('#sidebar button[data-view]').forEach((button) => {
        button.classList.toggle('active', button.dataset.view === name);
        // Заблокированные разделы помечаем, но кликабельными оставляем:
        // так пользователь видит подсказку вместо молчаливого отказа.
        button.classList.toggle('locked',
            SERVER_VIEWS.includes(button.dataset.view) && !!currentServer() && !currentServer().installed);
    });

    if (location.hash !== '#' + name) history.replaceState(null, '', '#' + name);

    const content = $('content');
    if (content) content.scrollTop = 0;
    $('sidebar').classList.remove('open');

    // Для заблокированного раздела данные не грузим.
    if (gated) {
        if (name !== 'players' && name !== 'livemap' && name !== 'chat') Players.stop();
        return;
    }

    // Раздел уже загружен при снятии заглушки — повторно не грузим.
    if (!unblocked) {
        loadCurrentView();
    }

    if (name !== 'players' && name !== 'livemap' && name !== 'chat') Players.stop();
    if (name !== 'dashboard') stopDashboard();

    // Шапка на разных разделах разной ширины — пересчитываем компактный режим.
    scheduleHeadCompact();
}

/** Обновляет текст заглушки по состоянию загрузки сервера. */
function applyServerGate() {
    const item = currentServer();
    if (!item) return;

    const stage = serverInstallState;
    const sameServer = (stage?.serverDirectory ?? '').replace(/[\\/]+$/, '').toLowerCase() ===
        (item.baseDirectory ?? '').replace(/[\\/]+$/, '').toLowerCase();

    const bar = $('serverGateBar');
    const button = $('serverGateDownload');
    const icon = $('serverGateIcon');
    const title = $('serverGateTitle');
    const text = $('serverGateText');

    if (stage && sameServer && stage.running) {
        bar?.classList.remove('hidden');
        // В заглушке строка с объёмом и скоростью уже есть в подзаголовке,
        // поэтому под полосой её не дублируем.
        paintProgress(bar, stage, { detail: false });

        const amount = formatDownloadAmount(stage.bytesDone, stage.bytesTotal);
        const speed = formatDownloadSpeed(stage.bytesPerSecond);

        if (icon) icon.textContent = '⬇';
        if (title) title.textContent = 'Сервер DayZ скачивается';
        if (text) {
            text.textContent = [stage.phase || 'Загрузка…', amount, speed]
                .filter(Boolean).join(' · ') + ' — дождитесь окончания, разделы откроются сами.';
        }
        if (button) { button.disabled = true; button.textContent = 'Загрузка…'; }
        return;
    }

    if (stage && sameServer && stage.error) {
        bar?.classList.add('hidden');
        if (icon) icon.textContent = '⚠';
        if (title) title.textContent = 'Не удалось скачать сервер';
        if (text) text.textContent = stage.error;
        if (button) { button.disabled = false; button.textContent = 'Повторить'; }
        return;
    }

    bar?.classList.add('hidden');
    if (icon) icon.textContent = '⬇';
    if (title) title.textContent = 'Сервер DayZ не установлен';
    if (text) {
        text.textContent = 'Скачайте сервер DayZ в папку этого сервера — после этого откроются ' +
            'консоль, игроки, конфигурация и остальные разделы.';
    }
    if (button) { button.disabled = false; button.textContent = 'Скачать сервер'; }
}

/** Последнее событие загрузки сервера DayZ (для заглушки). */
let serverInstallState = null;

/** Обработчики заглушки. */
$('serverGateDownload')?.addEventListener('click', () => {
    const item = currentServer();
    if (!item) return;

    const button = $('serverGateDownload');
    if (button) { button.disabled = true; button.textContent = 'Загрузка…'; }

    api('/steam/server/install', { method: 'POST', body: { serverId: item.id } })
        .then(() => toast('Загрузка сервера', `«${item.name}»: скачивание началось.`, 'info', 6000))
        .catch((error) => {
            toast('Сервер не скачан', error.message, 'error', 9000);
            if (button) { button.disabled = false; button.textContent = 'Скачать сервер'; }
        });
});

$('serverGateServers')?.addEventListener('click', () => showView('servers'));

document.querySelectorAll('#sidebar button[data-view]').forEach((button) => {
    button.addEventListener('click', () => {
        guardNavigation(button.dataset.view);
        closeSysMenu();
    });
});

/** Предупреждает о несохранённых настройках при закрытии вкладки. */
window.addEventListener('beforeunload', (event) => {
    if (!quickHasChanges()) return;

    event.preventDefault();
    event.returnValue = '';
});

/**
 * Переход между разделами. Если в конфигурации есть несохранённые
 * правки и пользователь уходит с неё — спрашиваем подтверждение.
 */
function guardNavigation(target) {
    if (target === currentView || !quickHasChanges()) {
        showView(target);
        return;
    }

    confirmLeaveQuick().then((ok) => {
        if (ok) showView(target);
    });
}

/**
 * Спрашивает, что делать с несохранёнными настройками.
 * Возвращает true, если можно уходить (сохранили или отказались от правок).
 */
function confirmLeaveQuick() {
    // Перечисляем, какие разделы изменены, чтобы было понятно, что теряется.
    const changed = quickChangedSections();

    const names = { server: 'Сервер', weather: 'Погода', gameplay: 'Мир', globals: 'Население' };
    const list = changed.map((s) => names[s] ?? s);

    return new Promise((resolve) => {
        UI.confirm(
            'Изменения не записаны в файлы сервера. Если уйти сейчас, они будут потеряны.',
            () => {
                // Уходим — сбрасываем правки к сохранённому на диске.
                discardQuickChanges();
                resolve(true);
            },
            'Уйти без сохранения',
            {
                label: 'Сохранить и уйти',
                action: async () => {
                    const ok = await saveQuickSettings();
                    resolve(ok);
                }
            },
            {
                icon: '✎',
                title: 'Несохранённые изменения',
                sub: 'Настройки ещё не применены к серверу',
                list
            });
    });
}

/** Есть ли несохранённые правки в быстрых настройках. */
function quickHasChanges() {
    return quickChangedSections().length > 0;
}

/** Сбрасывает правки к сохранённому на диске состоянию. */
function discardQuickChanges() {
    if (!quickBaseline) return;

    quickState = JSON.parse(JSON.stringify(quickBaseline));
    renderQuick();
    renderAdvancedModal();
}

document.querySelectorAll('.subtab').forEach((tab) => {
    tab.addEventListener('click', () => {
        document.querySelectorAll('.subtab').forEach((t) => t.classList.toggle('active', t === tab));
        document.querySelectorAll('.panel').forEach((panel) => {
            panel.classList.toggle('hidden', panel.id !== 'panel-' + tab.dataset.panel);
        });
    });
});

/**
 * Открывает модалку настроек сервера и подсвечивает поле автоперезапуска —
 * сюда ведёт значок автоперезапуска в шапке.
 */
function openAutoRestartSettings() {
    openServerSettingsModal();

    // Небольшая задержка вместо requestAnimationFrame: rAF не вызывается,
    // когда вкладка не отрисовывается (свёрнутое окно), а подсветка нужна.
    setTimeout(() => {
        const row = $('setAutoRestart')?.closest('.check') ?? $('setAutoRestart');
        const field = $('setRestartDelay')?.closest('.field');
        for (const node of [row, field]) {
            if (!node) continue;
            node.classList.remove('flash');
            // Перезапуск анимации: без reflow повторный клик не сработал бы.
            void node.offsetWidth;
            node.classList.add('flash');
            setTimeout(() => node.classList.remove('flash'), 2600);
        }

        row?.scrollIntoView({ block: 'center', behavior: 'smooth' });
        $('setAutoRestart')?.focus({ preventScroll: true });
    }, 400);
}

/* сворачивание боковой панели */
const SIDEBAR_KEY = 'dayzpulse.sidebar.collapsed';

function positionCollapseButton() {
    const collapsed = $('sidebar').classList.contains('collapsed');
    $('collapseBtn').style.left = (collapsed ? 66 : 258) + 'px';
}

function applySidebar(collapsed) {
    $('sidebar').classList.toggle('collapsed', collapsed);
    $('collapseBtn').textContent = collapsed ? '>' : '<';
    positionCollapseButton();
}

$('collapseBtn').addEventListener('click', () => {
    const collapsed = !$('sidebar').classList.contains('collapsed');
    applySidebar(collapsed);
    try { localStorage.setItem(SIDEBAR_KEY, collapsed ? '1' : '0'); } catch { /* приватое хранение недоступно */ }
});

$('menuBtn').addEventListener('click', () => $('sidebar').classList.toggle('open'));

/* подсказки при свёрнутой панели */
const sideTip = $('sideTip');
document.addEventListener('mousemove', (event) => {
    const sidebar = $('sidebar');
    if (!sidebar.classList.contains('collapsed')) {
        sideTip.classList.remove('show');
        return;
    }

    let hit = null;
    for (const element of sidebar.querySelectorAll('[data-tip]')) {
        const r = element.getBoundingClientRect();
        if (event.clientX >= r.left && event.clientX <= r.right &&
            event.clientY >= r.top && event.clientY <= r.bottom) {
            hit = element;
            break;
        }
    }

    if (hit) {
        sideTip.textContent = hit.dataset.tip;
        const rect = hit.getBoundingClientRect();
        sideTip.style.left = (rect.right + 14) + 'px';
        sideTip.style.top = (rect.top + rect.height / 2) + 'px';
        sideTip.classList.add('show');
    } else {
        sideTip.classList.remove('show');
    }
});

document.addEventListener('keydown', (event) => {
    if ((event.ctrlKey || event.metaKey) && (event.key === 'b' || event.key === 'B' || event.key === 'и' || event.key === 'И')) {
        event.preventDefault();
        $('collapseBtn').click();
    }
    if (event.key === 'Escape') {
        $('jobModal').classList.remove('open');
        closeConfirm();
    }
});

/* ============================================================ статус */

function stateClass(state) {
    if (state === 'Running') return 'online';
    if (state === 'Starting' || state === 'Stopping' || state === 'Restarting') return 'busy';
    if (state === 'Crashed' || state === 'Failed') return 'error';
    return '';
}

/** Цвет рамки пилюли: включено — зелёное, выключено — красное,
    переход или ошибка — оранжевое. */
function statePillClass(state) {
    if (state === 'Running') return 'on';
    if (state === 'Starting' || state === 'Stopping' || state === 'Restarting') return 'warn';
    if (state === 'Crashed' || state === 'Failed') return 'warn';
    return 'off';
}

function stateTag(kind) {
    if (kind === 'online') return 'on';
    if (kind === 'busy') return 'warn';
    if (kind === 'error') return 'off';
    return 'info';
}

function applyStatus(status) {
    if (!status) return;

    lastStatus = status;
    const kind = stateClass(status.state);

    $('stateDot').className = 'dot ' + kind;
    $('statePill').className = 'pill hide-sm ' + statePillClass(status.state);
    setText('stateText', status.stateText);
    setText('pidValue', status.processId ?? '—');

    setText('statState', status.stateText);

    // Игроки онлайн приходят в статусе сервера: обновляем счётчик в шапке
    // сразу, не дожидаясь захода на вкладку «Игроки».
    setPlayerCount(status.players ?? 0);

    setText('dashUptime', formatDuration(status.uptimeSeconds));
    setText('dashStartedAt', status.startedAt ? 'с ' + formatTime(status.startedAt) : 'процесс не запущен');

    // Сервер остановлен — обнуляем всё, что зависит от процесса и мода,
    // иначе карточки висели бы с прошлыми значениями (онлайн, FPS).
    //
    // Но если трекер на связи (мод шлёт файл, сервер мог быть запущен
    // извне панели), данные живые: их не трогаем.
    const stopped = !status.running && !status.trackerConnected;
    if (stopped) {
        setText('dashOnline', 0);
        const onlineBar = $('dashOnlineBar');
        if (onlineBar) onlineBar.style.width = '0%';
        setText('dashFps', '—');
        fpsHistory = [];
        drawFpsChart('dashFpsSpark', [], '#4a90d9');
        setPlayerCount(0);
    }

    // Ресурсы процесса. Показываем прочерк, когда процесса нет: иначе память,
    // потоки и CPU «висели» бы на экране после остановки (метрики устарели).
    // Это не зависит от трекера — считаем по самому процессу сервера.
    const processDead = !status.running;
    if (processDead) {
        setText('dashCpuVal', '—');
        setText('dashRamVal', '—');
        setText('dashThreadsVal', '—');

        const cpuBarStopped = $('dashCpuBar');
        if (cpuBarStopped) cpuBarStopped.style.width = '0%';
        const ramBarStopped = $('dashRamBar');
        if (ramBarStopped) ramBarStopped.style.width = '0%';
    } else {
        setText('dashCpuVal', (status.cpuPercent ?? 0).toFixed(0) + '%');

        const cpu = Math.min(100, Number(status.cpuPercent ?? 0));
        const cpuBar = $('dashCpuBar');
        if (cpuBar) {
            cpuBar.style.width = cpu + '%';
            cpuBar.className = cpu > 85 ? 'crit' : cpu > 60 ? 'warn' : 'blue';
        }

        // Память считаем только по процессу сервера (не по всей машине).
        setText('dashRamVal', formatBytes(status.workingSetBytes));
        setText('dashThreadsVal', status.threadCount > 0 ? String(status.threadCount) : '—');

        // Полоса памяти — доля от всей памяти машины, чтобы масштаб был понятен.
        const ramPercent = hostTotalMemoryBytes > 0
            ? Math.min(100, (Number(status.workingSetBytes ?? 0) / hostTotalMemoryBytes) * 100)
            : 0;
        const ramBar = $('dashRamBar');
        if (ramBar) ramBar.style.width = ramPercent.toFixed(1) + '%';
    }

    const autoIcon = $('autoRestartBtn');
    const autoCount = $('autoRestartCount');
    const autoText = $('hmAutoRestartText');
    if (autoIcon) {
        let title, warn = false, badge = 0;

        if (status.autoRestartArmed) {
            const delay = loadedSettings?.server?.autoRestartDelaySeconds ?? '';
            title = `Автоперезапуск взведён: перезапуск через ${delay} с`;
            warn = true;
        } else if (status.autoRestartsInWindow > 0) {
            title = `Автоперезапусков в окне лимита: ${status.autoRestartsInWindow}`;
            warn = true;
            badge = status.autoRestartsInWindow;
        } else if (loadedSettings?.server?.autoRestartOnCrash) {
            title = 'Автоперезапуск включён: перезапускать после сбоя';
        } else {
            title = 'Автоперезапуск выключен — нажмите, чтобы настроить';
        }

        autoIcon.title = title;
        autoIcon.classList.toggle('warn', warn);
        if (autoCount) {
            autoCount.textContent = badge > 0 ? String(badge) : '';
            autoCount.classList.toggle('hidden', badge === 0);
        }

        const autoWord = status.autoRestartArmed ? 'взведён'
            : status.autoRestartsInWindow > 0 ? `перезапусков: ${status.autoRestartsInWindow}`
            : loadedSettings?.server?.autoRestartOnCrash ? 'включён' : 'выключен';
        setText('hmAutoRestartText', 'Автоперезапуск: ' + autoWord);
        const hmAuto = $('hmAutoRestart');
        hmAuto?.classList.toggle('active', warn);
    }

    const running = !!status.running;
    $('btnStart').disabled = running || status.state === 'Starting';
    $('btnStop').disabled = !running;
    $('btnRestart').disabled = !running;
    $('btnQuickRestart').disabled = !running;
    $('btnHeadStart').disabled = running || status.state === 'Starting';
    $('btnHeadStop').disabled = !running;
    $('btnSend').disabled = !running;
    $('commandInput').disabled = !running;

    // Узкий экран: обновляем сводку и кнопки в меню-кнопке.
    if (typeof syncHeadMenu === 'function') syncHeadMenu();

    setText('serverDirLabel', status.workingDirectory || 'C:\\DayZServer');
}

/** Сколько игроков онлайн — обновляет бейдж в меню и счётчик в шапке. */
let lastPlayerCount = null;

function setPlayerCount(value) {
    lastPlayerCount = Number(value) || 0;

    const badge = $('playersBadge');
    if (badge) {
        badge.textContent = String(lastPlayerCount);
        badge.classList.toggle('hot', lastPlayerCount > 0);
    }

    // Счётчик «Игроков» в шапке: обновляем тем же значением, чтобы он не
    // висел нулём до захода на вкладку «Игроки».
    setText('statPlayersOnline', lastPlayerCount);
}

async function refreshStatus() {
    // Без выбранного сервера состояние запрашивать не у чего.
    if (!currentServerId) {
        return;
    }

    try {
        applyStatus(await api(sp('/status')));
    } catch {
        // Временный сбой опроса.
    }
}

/* ============================================================ дашборд */

let hostTotalMemoryBytes = 0;
let dashboardTimer = null;
let fpsHistory = [];

/** Загружает данные дашборда: онлайн, FPS, пинг, историю, события. */
async function loadDashboard() {
    const item = currentServer();
    if (!item) return;

    // Запоминаем, для какого сервера запрос: быстрый ответ предыдущего
    // сервера не должен перезаписать данные текущего.
    const requestedId = currentServerId;

    try {
        const data = await api(sp('/tracker/dashboard'));

        if (requestedId !== currentServerId) {
            return;
        }

        setText('dashOnline', data.players ?? 0);
        setText('dashSlots', data.slots ?? 0);

        const percent = data.slots > 0
            ? Math.min(100, (data.players / data.slots) * 100)
            : (data.players > 0 ? 100 : 0);
        const bar = $('dashOnlineBar');
        if (bar) bar.style.width = percent.toFixed(1) + '%';

        // FPS: 0 означает «нет данных» — показываем прочерк.
        setText('dashFps', data.fps > 0 ? `${Math.round(data.fps)} fps` : '—');

        if (data.fps > 0) {
            fpsHistory.push(Math.round(data.fps));
            while (fpsHistory.length > 40) fpsHistory.shift();
        }

        // График под цвет карточки FPS: плавная линия с заливкой вниз.
        drawFpsChart('dashFpsSpark', fpsHistory, '#4a90d9');

        // Онлайн за 24 часа: график по числу игроков.
        const online = data.online24h ?? [];
        const peak = online.reduce((max, p) => Math.max(max, p.players ?? 0), 0);
        setText('dashOnline24', peak > 0 ? `${peak}` : '—');
        drawSpark('dashOnlineSpark', online.map((p) => p.players ?? 0), '#35b8c4');

        setText('dashThreadsVal', data.threadCount > 0 ? `${data.threadCount}` : '—');

        // Метрики движка из лога: те же данные, что показывает #monitor.
        renderEngineMetrics(data.engine, data.running);

        // Ресурсы: диск — заполнение тома, где лежит сервер.
        updateDiskUsage(item);

        renderDashboardSettings(item);

        await loadDashboardEvents();
    } catch {
        // Дашборд — не критичный раздел.
    }
}

/**
 * Рисует карточку «Метрики движка» — те же данные, что показывает команда
 * #monitor (средний FPS, память, число игроков, зомби и животных).
 * Панель читает их из лога сервера, поэтому данные приходят с задержкой
 * в несколько секунд (движок пишет их с заданным интервалом).
 */
function renderEngineMetrics(engine, running) {
    if (!engine) {
        for (const id of ['dashEngineFps', 'dashEngineMem', 'dashEnginePlayers',
            'dashEngineZombies', 'dashEngineAnimals']) {
            setText(id, '—');
        }
        setText('dashEngineAt', '');

        const hint = $('dashEngineHint');
        if (hint) {
            hint.textContent = running
                ? 'Данные появятся через несколько секунд: панель включает запись метрик при запуске сервера.'
                : 'Запустите сервер — метрики появятся автоматически.';
        }
        return;
    }

    setText('dashEngineFps', engine.fps > 0 ? `${Math.round(engine.fps)} fps` : '—');
    setText('dashEngineMem', engine.memoryBytes > 0 ? formatBytes(engine.memoryBytes) : '—');
    setText('dashEnginePlayers', String(engine.players ?? '—'));

    // Зомби и животные: «живые / всего».
    const z = engine.infected;
    setText('dashEngineZombies', z ? `${z.alive} / ${z.total}` : '—');

    const a = engine.animals;
    setText('dashEngineAnimals', a ? `${a.alive} / ${a.total}` : '—');

    setText('dashEngineAt', engine.at ? 'обновлено ' + formatTime(engine.at) : '');

    const hint = $('dashEngineHint');
    if (hint) {
        hint.innerHTML = 'Живые / всего. Те же данные, что выводит команда '
            + '<span class="mono">#monitor</span>.';
    }
}

/** Компактная сводка настроек сервера в карточке дашборда. */
function renderDashboardSettings(item) {
    const box = $('dashSettingsSummary');
    if (!box) return;

    box.textContent = '';

    // Список серверов отдаёт эти поля на верхнем уровне объекта.
    const rows = [
        ['Папка', item?.baseDirectory || '—'],
        ['Порт', item?.port ? String(item.port) : '—'],
        ['Карта', item?.mapLabel || '—']
    ];

    for (const [key, value] of rows) {
        box.appendChild(factCell(key, value));
    }

    // Цвет показываем образцом, а не строкой HEX.
    box.appendChild(colorFactCell('Цвет', item?.color));
}

/** Плитка факта: подпись + значение. */
function factCell(key, value) {
    const cell = document.createElement('div');
    cell.className = 'pm-fact';

    const k = document.createElement('span');
    k.className = 'k';
    k.textContent = key;

    const v = document.createElement('b');
    v.textContent = value ?? '—';
    v.title = value ?? '';

    cell.appendChild(k);
    cell.appendChild(v);
    return cell;
}

/** Плитка факта с образцом цвета. */
function colorFactCell(key, color) {
    const cell = document.createElement('div');
    cell.className = 'pm-fact';

    const k = document.createElement('span');
    k.className = 'k';
    k.textContent = key;

    const value = color || '#3fb950';

    const row = document.createElement('span');
    row.className = 'color-swatch-row';

    const swatch = document.createElement('span');
    swatch.className = 'color-swatch';
    swatch.style.background = value;

    const label = document.createElement('b');
    label.textContent = colorName(value);

    row.appendChild(swatch);
    row.appendChild(label);
    cell.appendChild(k);
    cell.appendChild(row);
    return cell;
}

/** Человеческое имя цвета по HEX. */
function colorName(color) {
    const map = {
        '#3fb950': 'Зелёный',
        '#4a90d9': 'Синий',
        '#a371f7': 'Фиолетовый',
        '#d9a94a': 'Янтарный',
        '#e5534b': 'Красный',
        '#35b8c4': 'Бирюзовый',
        '#e06c9f': 'Розовый'
    };

    return map[(color ?? '').toLowerCase()] ?? (color || 'по умолчанию');
}

/**
 * График FPS: плавная линия с заливкой вниз, в цвет карточки.
 * Высота 70 — занимает нижнюю часть карточки.
 */
function drawFpsChart(id, values, color) {
    const svg = $(id);
    if (!svg) return;

    svg.innerHTML = '';

    if (!values || values.length < 2) {
        return;
    }

    const width = 240;
    const height = 70;
    const pad = 8;
    const max = Math.max(...values, 1);
    const min = Math.min(...values, 0);
    const range = max - min || 1;

    const coords = values.map((v, i) => {
        const x = (i / (values.length - 1)) * width;
        const y = height - pad - ((v - min) / range) * (height - pad * 2);
        return [x, y];
    });

    // Сглаживание: кривые Безье через середины отрезков.
    let d = `M ${coords[0][0].toFixed(1)} ${coords[0][1].toFixed(1)}`;
    for (let i = 1; i < coords.length; i++) {
        const [x0, y0] = coords[i - 1];
        const [x1, y1] = coords[i];
        const cx = (x0 + x1) / 2;
        d += ` C ${cx.toFixed(1)} ${y0.toFixed(1)}, ${cx.toFixed(1)} ${y1.toFixed(1)}, ${x1.toFixed(1)} ${y1.toFixed(1)}`;
    }

    const areaD = `${d} L ${width} ${height} L 0 ${height} Z`;

    const gradientId = 'fpsGrad-' + id;
    const defs = document.createElementNS('http://www.w3.org/2000/svg', 'defs');
    defs.innerHTML =
        `<linearGradient id="${gradientId}" x1="0" y1="0" x2="0" y2="1">` +
        `<stop offset="0%" stop-color="${color}" stop-opacity=".32"/>` +
        `<stop offset="100%" stop-color="${color}" stop-opacity="0"/>` +
        '</linearGradient>';

    const area = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    area.setAttribute('d', areaD);
    area.setAttribute('fill', `url(#${gradientId})`);

    const line = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    line.setAttribute('d', d);
    line.setAttribute('fill', 'none');
    line.setAttribute('stroke', color);
    line.setAttribute('stroke-width', '2');
    line.setAttribute('stroke-linecap', 'round');
    line.setAttribute('stroke-linejoin', 'round');

    svg.appendChild(defs);
    svg.appendChild(area);
    svg.appendChild(line);
}

/** Мини-график («спарклайн») по массиву чисел. */
function drawSpark(id, values, color) {
    const svg = $(id);
    if (!svg) return;

    svg.innerHTML = '';

    if (!values || values.length < 2) {
        return;
    }

    const width = 120;
    const height = 40;
    const max = Math.max(...values, 1);
    const min = Math.min(...values, 0);
    const range = max - min || 1;
    const step = width / (values.length - 1);

    const points = values
        .map((v, i) => `${(i * step).toFixed(1)},${(height - ((v - min) / range) * (height - 6) - 3).toFixed(1)}`)
        .join(' ');

    const line = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
    line.setAttribute('points', points);
    line.setAttribute('fill', 'none');
    line.setAttribute('stroke', color);
    line.setAttribute('stroke-width', '2');
    line.setAttribute('stroke-linejoin', 'round');
    svg.appendChild(line);
}

/** Размер папки сервера на диске. */
async function updateDiskUsage(item) {
    try {
        const info = await api('/system/disk?path=' + encodeURIComponent(item.baseDirectory ?? ''));
        if (!info || !info.folderBytes) {
            setText('dashDiskVal', '—');
            const bar = $('dashDiskBar');
            if (bar) bar.style.width = '0%';
            return;
        }

        // Показываем, сколько занимает именно этот сервер.
        setText('dashDiskVal', formatBytes(info.folderBytes));

        // Полоса — доля папки сервера от объёма всего диска (для масштаба).
        const percent = info.totalBytes > 0
            ? Math.min(100, (info.folderBytes / info.totalBytes) * 100)
            : 0;

        const bar = $('dashDiskBar');
        if (bar) {
            bar.style.width = percent.toFixed(1) + '%';
            bar.className = '';
        }
    } catch {
        setText('dashDiskVal', '—');
    }
}

/** Последние события игроков (киллы и чат) для дашборда. */
async function loadDashboardEvents() {
    const box = $('dashEvents');
    if (!box) return;

    // Как и у дашборда, отбрасываем ответы не текущего сервера.
    const requestedId = currentServerId;

    const [kills, chat] = await Promise.all([
        api(sp('/tracker/kills?limit=8')).catch(() => []),
        api(sp('/tracker/chat?limit=8')).catch(() => [])
    ]);

    if (requestedId !== currentServerId) {
        return;
    }

    const events = [];

    for (const k of kills ?? []) {
        // API отдаёт camelCase: victimName / killerName / weapon.
        const victim = k.victimName || k.victim || 'неизвестно';
        const killer = k.killerName || k.killer || '';
        events.push({
            at: k.at,
            icon: k.isSuicide || k.isEnvironment ? '☠' : '🎯',
            title: victim,
            text: k.isSuicide
                ? 'покончил с собой'
                : k.isEnvironment
                    ? 'погиб (окружение)'
                    : (killer ? `убит игроком ${killer}${k.weapon ? ' (' + k.weapon + ')' : ''}` : 'погиб')
        });
    }

    for (const c of chat ?? []) {
        events.push({
            at: c.at,
            icon: '💬',
            title: c.playerName || c.name || 'игрок',
            text: c.text || c.message || ''
        });
    }

    events.sort((a, b) => new Date(b.at) - new Date(a.at));
    box.textContent = '';

    if (events.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'muted';
        empty.style.padding = '14px 0';
        empty.textContent = 'Событий пока нет.';
        box.appendChild(empty);
        return;
    }

    for (const ev of events.slice(0, 8)) {
        const row = document.createElement('div');
        row.className = 'dash-event';

        const avatar = document.createElement('div');
        avatar.className = 'avatar';
        avatar.textContent = (ev.title ?? '?').slice(0, 2).toUpperCase();

        const body = document.createElement('div');
        body.className = 'dash-event-body';
        const name = document.createElement('b');
        name.textContent = ev.title;
        const desc = document.createElement('small');
        desc.textContent = ev.text;
        body.appendChild(name);
        body.appendChild(desc);

        const time = document.createElement('span');
        time.className = 'mono muted';
        time.textContent = formatTime(ev.at);

        row.appendChild(avatar);
        row.appendChild(body);
        row.appendChild(time);
        box.appendChild(row);
    }
}

function startDashboard() {
    stopDashboard();
    loadDashboard();
    dashboardTimer = setInterval(() => {
        if (currentView === 'dashboard') loadDashboard();
    }, 5000);
}

function stopDashboard() {
    if (dashboardTimer) {
        clearInterval(dashboardTimer);
        dashboardTimer = null;
    }
}

/**
 * Очищает дашборд при переключении сервера: без этого старые значения
 * (онлайн, FPS, история) висели бы, пока не придут данные нового сервера.
 */
function resetDashboard() {
    stopDashboard();
    fpsHistory = [];

    setText('dashOnline', 0);
    setText('dashSlots', 0);
    const bar = $('dashOnlineBar');
    if (bar) bar.style.width = '0%';

    setText('dashFps', '—');
    setText('dashOnline24', '—');
    setText('dashUptime', '—');
    setText('dashStartedAt', 'с последнего рестарта');
    setText('dashCpuVal', '—');
    setText('dashRamVal', '—');
    setText('dashDiskVal', '—');
    setText('dashThreadsVal', '—');

    // Метрики движка относятся к конкретному серверу — обнуляем.
    renderEngineMetrics(null, false);

    drawFpsChart('dashFpsSpark', [], '#4a90d9');
    drawSpark('dashOnlineSpark', [], '#35b8c4');
    const cpuBar = $('dashCpuBar');
    if (cpuBar) cpuBar.style.width = '0%';
    const ramBar = $('dashRamBar');
    if (ramBar) ramBar.style.width = '0%';
    const diskBar = $('dashDiskBar');
    if (diskBar) diskBar.style.width = '0%';

    const events = $('dashEvents');
    if (events) clearChildren(events);
}

async function serverAction(action, label) {
    const name = currentServer()?.name ?? 'сервер';
    try {
        const status = await api(sp('/' + action), { method: 'POST' });
        applyStatus(status);
        toast(label, `«${name}»: команда выполнена.`, 'info');
        loadServers();
    } catch (error) {
        toast('Не удалось выполнить', error.message, 'error', 7000);
        refreshStatus();
    }
}

$('btnStart').addEventListener('click', () => serverAction('start', 'Запуск сервера'));
$('btnStop').addEventListener('click', () => {
    const name = currentServer()?.name ?? 'сервер';
    askConfirm(`Остановить «${name}»? Игроки будут отключены.`, () => serverAction('stop', 'Остановка сервера'), 'Остановить');
});
$('btnRestart').addEventListener('click', () => {
    const name = currentServer()?.name ?? 'сервер';
    askConfirm(`Перезапустить «${name}»? Игроки будут отключены.`, () => serverAction('restart', 'Перезапуск сервера'), 'Перезапустить');
});
$('btnQuickRestart').addEventListener('click', () => {
    const name = currentServer()?.name ?? 'сервер';
    askConfirm(`Перезапустить «${name}»?`, () => serverAction('restart', 'Перезапуск сервера'), 'Перезапустить');
});

// Кнопки в шапке: запуск, остановка и перезапуск текущего сервера.
$('btnHeadStart')?.addEventListener('click', () => serverAction('start', 'Запуск сервера'));
$('btnHeadStop')?.addEventListener('click', () => {
    const name = currentServer()?.name ?? 'сервер';
    askConfirm(`Остановить «${name}»? Игроки будут отключены.`, () => serverAction('stop', 'Остановка сервера'), 'Остановить');
});

// Те же действия в меню узкого экрана.
$('hmStart')?.addEventListener('click', () => { closeHeadMenu(); serverAction('start', 'Запуск сервера'); });
$('hmStop')?.addEventListener('click', () => {
    closeHeadMenu();
    const name = currentServer()?.name ?? 'сервер';
    askConfirm(`Остановить «${name}»? Игроки будут отключены.`, () => serverAction('stop', 'Остановка сервера'), 'Остановить');
});
$('hmRestart')?.addEventListener('click', () => {
    closeHeadMenu();
    const name = currentServer()?.name ?? 'сервер';
    askConfirm(`Перезапустить «${name}»?`, () => serverAction('restart', 'Перезапуск сервера'), 'Перезапустить');
});
$('hmAdmins')?.addEventListener('click', () => { closeHeadMenu(); showView('security'); });

// Значок автоперезапуска в шапке и его дубль в меню узкого экрана
// ведут к настройке автоперезапуска.
$('autoRestartBtn')?.addEventListener('click', openAutoRestartSettings);
$('hmAutoRestart')?.addEventListener('click', () => { closeHeadMenu(); openAutoRestartSettings(); });

/** Отправляет команду моду DayZPulse (быстрые действия дашборда). */
async function dashModCommand(action, args, label) {
    try {
        await api(sp('/tracker/commands'), {
            method: 'POST',
            body: { action, args: args ?? {} }
        });
        toast(label, 'Команда поставлена в очередь — мод выполнит её при следующем опросе.', 'info', 6000);
    } catch (error) {
        toast('Не удалось отправить', error.message, 'error', 8000);
    }
}

$('btnQuickBroadcast')?.addEventListener('click', () => {
    UI.prompt({
        title: 'Broadcast',
        label: 'Сообщение игрокам',
        okLabel: 'Отправить',
        onOk: (value) => {
            const text = (value ?? '').trim();
            if (text.length > 0) {
                dashModCommand('message', { text }, 'Broadcast');
            }
        }
    });
});

$('logoutBtn').addEventListener('click', async () => {
    try { await api('/auth/logout', { method: 'POST' }); } catch { /* выходим в любом случае */ }
    window.location.replace('/login.html');
});

/* ------------------------------------------------ системное меню (шестерёнка) */

const sysMenuBtn = $('sysMenuBtn');
const sysMenu = $('sysMenu');

function openSysMenu() {
    if (!sysMenu || !sysMenuBtn) return;

    // Меню position:fixed — кладём его над кнопкой, выравнивая по правому краю.
    // Так оно не обрезается сайдбаром (у него overflow:hidden).
    const menuRect = { width: 196 };
    const btnRect = sysMenuBtn.getBoundingClientRect();

    sysMenu.classList.remove('hidden');

    const height = sysMenu.offsetHeight;
    let left = btnRect.right - menuRect.width;
    let top = btnRect.top - height - 8;

    // Не даём выйти за края окна.
    left = Math.max(8, Math.min(left, window.innerWidth - menuRect.width - 8));
    if (top < 8) {
        top = btnRect.bottom + 8;
    }

    sysMenu.style.left = left + 'px';
    sysMenu.style.top = top + 'px';
    sysMenuBtn.setAttribute('aria-expanded', 'true');
}

function closeSysMenu() {
    if (!sysMenu) return;
    sysMenu.classList.add('hidden');
    sysMenuBtn?.setAttribute('aria-expanded', 'false');
}

function toggleSysMenu() {
    if (!sysMenu) return;
    if (sysMenu.classList.contains('hidden')) openSysMenu();
    else closeSysMenu();
}

sysMenuBtn?.addEventListener('click', (event) => {
    event.stopPropagation();
    toggleSysMenu();
});

// При изменении размера окна меню бы сместилось — закрываем.
window.addEventListener('resize', () => closeSysMenu());

// Клик вне меню и Esc закрывают его.
document.addEventListener('click', (event) => {
    if (sysMenu?.classList.contains('hidden')) return;
    if (event.target.closest('.sysmenu-wrap')) return;
    closeSysMenu();
});

document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') closeSysMenu();
});

/* -------------------------------------- меню шапки (узкий экран) */

// На узком экране состояние и управление сервером прячутся в одну кнопку,
// иначе кнопки переносились на вторую строку и вылезали за шапку.
const headMoreBtn = $('headMoreBtn');
const headMenu = $('headMenu');

function openHeadMenu() {
    if (!headMenu || !headMoreBtn) return;

    headMenu.classList.remove('hidden');

    // Меню fixed: кладём его под кнопкой, выравнивая по правому краю.
    const menuW = headMenu.offsetWidth;
    const menuH = headMenu.offsetHeight;
    const btn = headMoreBtn.getBoundingClientRect();

    let left = btn.right - menuW;
    let top = btn.bottom + 8;

    left = Math.max(8, Math.min(left, window.innerWidth - menuW - 8));
    if (top + menuH > window.innerHeight - 8) {
        top = btn.top - menuH - 8;
    }

    headMenu.style.left = left + 'px';
    headMenu.style.top = top + 'px';
    headMoreBtn.setAttribute('aria-expanded', 'true');
}

function closeHeadMenu() {
    if (!headMenu) return;
    headMenu.classList.add('hidden');
    headMoreBtn?.setAttribute('aria-expanded', 'false');
}

function toggleHeadMenu() {
    if (!headMenu) return;
    if (headMenu.classList.contains('hidden')) openHeadMenu();
    else closeHeadMenu();
}

headMoreBtn?.addEventListener('click', (event) => {
    event.stopPropagation();
    toggleHeadMenu();
});

window.addEventListener('resize', () => { closeHeadMenu(); scheduleHeadCompact(); });

document.addEventListener('click', (event) => {
    if (headMenu?.classList.contains('hidden')) return;
    if (event.target.closest('#headMoreBtn')) return;
    if (event.target.closest('#headMenu')) return;
    closeHeadMenu();
});

document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') closeHeadMenu();
});

/**
 * Сворачивает состояние и управление в одну кнопку, если они не помещаются.
 * Считаем по факту (а не по фиксированной ширине): элементы бывают разной
 * ширины («автоперезапуск через 60 с» шире, чем «выкл.»).
 */
function updateHeadCompact() {
    const bar = document.querySelector('.topbar');
    if (!bar) return;

    // На «Все серверы» и без серверов эти элементы и так скрыты.
    if (bar.classList.contains('no-servers') || bar.classList.contains('all-servers')) {
        bar.classList.remove('head-compact');
        closeHeadMenu();
        return;
    }

    // Пробуем широкий режим и смотрим, помещается ли содержимое.
    const wasCompact = bar.classList.contains('head-compact');
    bar.classList.remove('head-compact');

    if (bar.scrollWidth - bar.clientWidth > 1) {
        bar.classList.add('head-compact');
    } else if (wasCompact) {
        closeHeadMenu();
    }
}

/** Пересчитывает компактный режим после перерисовки раскладки. */
let headCompactPending = false;
function scheduleHeadCompact() {
    if (headCompactPending) return;
    headCompactPending = true;
    requestAnimationFrame(() => {
        headCompactPending = false;
        updateHeadCompact();
    });
}

/**
 * Дублирует состояние сервера и кнопки управления в меню узкого экрана.
 * Скрытые в шапке элементы остаются рабочими, поэтому просто копируем
 * значения, а обработчики навешиваем на кнопки меню отдельно.
 */
function syncHeadMenu() {
    setText('headMoreText', lastStatus?.stateText ?? '—');
    if ($('headMoreDot')) $('headMoreDot').className = 'dot ' + stateClass(lastStatus?.state ?? '');

    setText('hmStateText', lastStatus?.stateText ?? '—');
    if ($('hmStateDot')) $('hmStateDot').className = 'dot ' + stateClass(lastStatus?.state ?? '');
    if ($('hmStatePill')) $('hmStatePill').className = 'pill ' + statePillClass(lastStatus?.state ?? '');
    setText('hmPlayers', lastPlayerCount ?? lastStatus?.players ?? 0);

    const running = !!lastStatus?.running;
    if ($('hmStart')) $('hmStart').disabled = running || lastStatus?.state === 'Starting';
    if ($('hmStop')) $('hmStop').disabled = !running;
    if ($('hmRestart')) $('hmRestart').disabled = !running;
    $('hmAdmins')?.classList.toggle('hidden', !sessionIsOwner);

    scheduleHeadCompact();
}

/* ============================================================ консоль */

const MAX_LINES = 2500;

function appendLine(line, targetId) {
    const element = $(targetId);
    if (!element) return;

    const row = document.createElement('span');
    row.className = 'l';

    const time = document.createElement('span');
    time.className = 'ts';
    time.textContent = new Date(line.timestamp).toLocaleTimeString('ru-RU');

    const text = document.createElement('span');
    text.className = 'l-' + (line.stream ?? 'out');
    text.textContent = line.text ?? '';

    row.appendChild(time);
    row.appendChild(text);
    element.appendChild(row);

    while (element.childElementCount > MAX_LINES) element.removeChild(element.firstElementChild);

    if ($('autoscroll').checked) element.scrollTop = element.scrollHeight;
}

function connectConsole() {
    const target = currentServerId;

    // При смене сервера пересоздаём соединение с новой подпиской.
    if (activeSocket && lastConsoleServer !== target) {
        try { activeSocket.close(); } catch { /* игнорируем */ }
        activeSocket = null;
    }

    if (activeSocket && (activeSocket.readyState === WebSocket.OPEN || activeSocket.readyState === WebSocket.CONNECTING)) return;

    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    lastConsoleServer = target;

    activeSocket = new WebSocket(`${protocol}//${location.host}/ws/console?server=${encodeURIComponent(target ?? '')}`);

    activeSocket.onopen = () => loadRconStatus();

    activeSocket.onmessage = (event) => {
        let message;
        try { message = JSON.parse(event.data); } catch { return; }

        if (message.type === 'backlog') {
            clearChildren($('console'));
            message.lines.forEach((line) => appendLine(line, 'console'));
            return;
        }

        if (message.type === 'line') {
            appendLine(message.line, 'console');

            // Строка от RCON — значит связь жива, обновляем статус.
            if (message.line?.stream === 'rcon') markRconConnected();
        }
    };

    activeSocket.onclose = () => {
        clearTimeout(reconnectTimer);
        reconnectTimer = setTimeout(connectConsole, 2500);
    };

    activeSocket.onerror = () => activeSocket?.close();
}

/** Состояние RCON для шапки консоли. */
async function loadRconStatus() {
    if (!currentServerId) return;

    try {
        const data = await api(sp('/rcon/status'));
        const tag = $('rconStatusTag');

        setText('rconAddress', data.port ? `127.0.0.1:${data.port}` : '—');

        // Пояснение, почему RCON недоступен (например, на сборке без BattlEye).
        const help = $('rconHelp');
        if (help) {
            help.textContent = (!data.connected && data.hint)
                ? data.hint
                : 'Команды выполняются через BattlEye RCON. Порт и пароль берутся из ' +
                  'battleye\\beserver_x64.cfg; при первом запуске панель создаёт их сама.';
        }

        if (tag) {
            if (data.connected) {
                tag.textContent = 'RCON: подключено';
                tag.className = 'tag on';
            } else {
                tag.textContent = data.error ? 'RCON: ' + data.error : 'RCON: отключено';
                tag.className = 'tag off';
            }
        }
    } catch {
        // Панель может быть недоступна в момент старта.
    }
}

function markRconConnected() {
    const tag = $('rconStatusTag');
    if (tag && !tag.classList.contains('on')) {
        tag.textContent = 'RCON: подключено';
        tag.className = 'tag on';
    }
}

/** Переподключение к RCON по кнопке. */
async function reconnectRcon() {
    const button = $('btnRconReconnect');
    if (button) { button.disabled = true; button.textContent = 'Подключение…'; }

    try {
        const data = await api(sp('/rcon/reconnect'), { method: 'POST' });
        if (data.connected) {
            toast('RCON', `Подключено к 127.0.0.1:${data.port}`, 'info');
        } else {
            toast('RCON не подключён', data.error ?? 'Проверьте, запущен ли сервер.', 'warn', 8000);
        }
    } catch (error) {
        toast('Не удалось подключиться', error.message, 'error', 8000);
    } finally {
        if (button) { button.disabled = false; button.textContent = 'Переподключить'; }
        loadRconStatus();
    }
}

$('btnRconReconnect')?.addEventListener('click', reconnectRcon);

$('btnClearConsole').addEventListener('click', async () => {
    clearChildren($('console'));
    try { await api(sp('/clear-console'), { method: 'POST' }); } catch { /* не критично */ }
});

async function sendCommand() {
    const input = $('commandInput');
    const command = input.value.trim();
    if (!command) return;

    try {
        const result = await api(sp('/command'), { method: 'POST', body: { command } });
        input.value = '';

        // Ответ RCON показываем прямо в консоли.
        if (result?.response && result.response.trim().length > 0) {
            for (const line of result.response.split('\n')) {
                appendLine({ timestamp: new Date().toISOString(), stream: 'rcon', text: line }, 'console');
            }
        }

        markRconConnected();
    } catch (error) {
        toast('Команда не отправлена', error.message, 'error', 8000);
        loadRconStatus();
    }
}

$('btnSend').addEventListener('click', sendCommand);
$('commandInput').addEventListener('keydown', (event) => {
    if (event.key === 'Enter') { event.preventDefault(); sendCommand(); }
});

/* ============================================================ конфигурация */

async function loadConfigs() {
    // Раздел «Файлы» убран: редактирование конфигов делается карточками
    // быстрой настройки. Функция осталась как безопасная заглушка — её
    // вызывают общие места (смена сервера, инициализация).
}

/* ============================================================ быстрые настройки */

/**
 * Разделы конфигурации. Схема полей приходит с сервера (QuickSchema),
 * здесь — только заголовки, значки и поля, показанные прямо в карточке.
 */
const QUICK_SECTIONS = {
    server: { title: 'Расширенные: сервер', icon: '⚙', card: 'server' },
    gameplay: { title: 'Расширенные: мир', icon: '🌍', card: 'gameplay' },
    globals: { title: 'Расширенные: население и очистка', icon: '🗺', card: null },
    weather: { title: 'Расширенные: погода', icon: '🌦', card: 'weather' }
};

/**
 * Поля, показанные прямо в карточках. Формат: [section, key, elementId, invert].
 * invert — значение в файле обратное (например disableBaseDamage).
 */
const QUICK_CARD_FIELDS = [
    ['server', 'hostname', 'qcHostname', false],
    ['server', 'maxPlayers', 'qcMaxPlayers', false],
    ['server', '__rconEnabled', 'qcRconEnabled', false],
    ['weather', 'enabled', 'qwEnabled', false],
    ['weather', 'overcast', 'qwOvercast', false],
    ['weather', 'rain', 'qwRain', false],
    ['gameplay', 'GeneralData.disableBaseDamage', 'qmBaseDamage', true],
    ['gameplay', 'PlayerData.disablePersonalLight', 'qmPersonalLight', true],
    ['globals', 'ZombieMaxCount', 'qgZombieMax', false],
    ['globals', 'AnimalMaxCount', 'qgAnimalMax', false]
];

/**
 * Тумблер «Читать cfggameplay.json». Значение живёт в serverDZ.cfg,
 * поэтому пишется в раздел server, а не gameplay.
 */
const GAMEPLAY_FILE_TOGGLE = 'qmEnableGameplay';

let quickSchema = { server: [], gameplay: [], globals: [] };
let quickState = null;       // текущие редактируемые значения
let quickBaseline = null;    // что пришло с сервера
let quickEditable = false;
let quickPaths = null;
let quickMissions = [];
let advSection = 'server';

/** Загружает схему и значения, фиксирует исходное состояние. */
async function loadQuickSettings() {
    if (!currentServerId) return;

    try {
        const data = await api(sp('/quick'));

        quickSchema = data.schema ?? { server: [], gameplay: [], globals: [] };

        // Значения по всем разделам + погода в «отображаемых» единицах.
        quickState = {
            server: {
                ...(data.values?.server ?? {}),
                __rconEnabled: data.rcon?.enabled ? '1' : '0',
                __rconPort: String(data.rcon?.port ?? '')
            },
            gameplay: { ...(data.values?.gameplay ?? {}) },
            globals: { ...(data.values?.globals ?? {}) },
            weather: {
                enabled: data.weather?.enabled ? '1' : '0',
                overcast: String(Math.round((data.weather?.overcast ?? 0.45) * 100)),
                rain: String(Math.round((data.weather?.rain ?? 0) * 100)),
                fog: String(Math.round((data.weather?.fog ?? 0.05) * 100)),
                snow: String(Math.round((data.weather?.snow ?? 0) * 100)),
                wind: String(Math.round(data.weather?.wind ?? 8)),
                stormThreshold: String(Math.round((data.weather?.stormThreshold ?? 0.9) * 100)),
                stormDensity: String(Math.round((data.weather?.stormDensity ?? 1) * 100))
            }
        };

        quickBaseline = JSON.parse(JSON.stringify(quickState));
        quickPaths = data.paths;
        quickMissions = data.missions ?? [];
        quickServerRunning = !!data.running;
        quickPending = !!data.pending;
        quickEditable = true;
        quickGameplayFile = data.gameplayFile ?? { exists: false, enabled: false };

        setText('quickPaths', (data.paths?.serverCfg ?? '') + ' · ' + (data.paths?.weather ?? ''));
        setQuickEditable(quickEditable);
        renderQuick();
    } catch (error) {
        toast('Не удалось загрузить настройки', error.message, 'error');
    }
}

/** Сервер сейчас запущен (правки будут отложены). */
let quickServerRunning = false;

/** Есть отложенные (ещё не применённые) правки. */
let quickPending = false;

/** Настройки мира: существует ли файл и включено ли его чтение. */
let quickGameplayFile = { exists: false, enabled: false };

/** Показывает предупреждение, если cfggameplay.json не читается DayZ. */
function updateGameplayWarning() {
    const warn = $('qmWarn');
    if (!warn) return;

    // Учитываем и несохранённое значение тумблера, и то, что уже на диске.
    const live = quickState
        ? quickValue('server', 'enableCfgGameplayFile') === '1'
        : quickGameplayFile.enabled;

    const problem = !live;
    warn.classList.toggle('hidden', !problem);
}

/** Обновляет подсказку о том, когда применятся настройки. */
function setQuickEditable(editable) {
    quickEditable = editable;

    const hint = $('quickApplyHint');
    if (!hint) return;

    if (quickServerRunning) {
        hint.textContent = quickPending
            ? 'есть отложенные правки — применятся при следующем запуске'
            : 'сервер запущен: правки сохранятся и применятся при следующем запуске';
        hint.className = 'tag ' + (quickPending ? 'on' : 'warn');
    } else {
        hint.textContent = 'настройки применятся сразу';
        hint.className = 'tag on';
    }
}

/** Значение поля по разделу и ключу. */
function quickValue(section, key) {
    const raw = quickState?.[section]?.[key];
    return raw === undefined ? '' : raw;
}

/** Перерисовывает карточки, окно и отметки изменений. */
function renderQuick() {
    if (!quickState) return;

    // Поля карточек.
    for (const [section, key, elementId, invert] of QUICK_CARD_FIELDS) {
        const element = $(elementId);
        if (!element) continue;

        const raw = quickValue(section, key);
        const on = raw === '1';

        if (element.type === 'checkbox') element.checked = invert ? !on : on;
        else element.value = raw;

        element.disabled = !quickEditable;
    }

    // Селект миссии: список папок mpmissions + текущая (даже если её нет в списке).
    const mission = $('qcMission');
    if (mission) {
        const current = quickValue('server', '__mission') || '';
        const options = quickMissions.slice();
        if (current && !options.includes(current)) options.unshift(current);

        mission.textContent = '';
        for (const name of options) {
            const option = document.createElement('option');
            option.value = name;
            option.textContent = name;
            mission.appendChild(option);
        }

        mission.value = current;
        mission.disabled = !quickEditable;
    }

    // Тумблер «Читать cfggameplay.json» — значение из serverDZ.cfg.
    const enableGameplay = $(GAMEPLAY_FILE_TOGGLE);
    if (enableGameplay) {
        enableGameplay.checked = quickValue('server', 'enableCfgGameplayFile') === '1';
        enableGameplay.disabled = !quickEditable;
    }

    updateWeatherPreview();

    // Подписи-сводки.
    const view = (id, text) => {
        const el = document.querySelector(`[data-view="${id}"]`);
        if (el) el.textContent = text;
    };

    view('qmLighting', quickValue('gameplay', 'WorldsData.lightingConfig') === '1' ? 'темнее' : 'светлее');
    view('qmStamina', quickValue('gameplay', 'PlayerData.StaminaData.staminaMax') || '—');

    const label = $('qcWeatherLabel');
    if (label) label.textContent = weatherName();

    updateGameplayWarning();
    markDirty();
}

/** Человеческое имя текущей погоды по пресетам. */
function weatherName() {
    const w = quickState.weather;
    let best = 'своя';
    let bestDist = Infinity;

    for (const [name, preset] of Object.entries(WEATHER_PRESETS)) {
        const dist = Math.abs(preset.overcast * 100 - Number(w.overcast))
            + Math.abs(preset.rain * 100 - Number(w.rain))
            + Math.abs(preset.fog * 100 - Number(w.fog))
            + Math.abs(preset.wind - Number(w.wind));

        if (dist < bestDist) {
            bestDist = dist;
            best = PRESET_NAMES[name];
        }
    }

    return bestDist <= 6 ? best : 'своя';
}

const PRESET_NAMES = { clear: 'ясно', cloudy: 'облачно', rain: 'дождь', storm: 'гроза', fog: 'туман' };

/** Ставит отметки изменений на карточках и в окне. */
function markDirty() {
    if (!quickState || !quickBaseline) return;

    const changed = quickChangedSections();

    for (const section of Object.keys(quickState)) {
        const dirty = changed.includes(section);

        const dot = document.querySelector(`[data-dirty="${section}"]`);
        if (dot) dot.classList.toggle('on', dirty);

        const card = document.querySelector(`[data-card="${section}"]`);
        if (card) card.classList.toggle('changed', dirty);
    }

    setText('advDirty', changed.length === 0 ? 'изменений нет' : `изменено разделов: ${changed.length}`);

    const save = $('btnQuickSave');
    if (save) save.classList.toggle('pulse', changed.length > 0);
}

/** Разделы с несохранёнными правками. */
function quickChangedSections() {
    if (!quickState || !quickBaseline) return [];

    return Object.keys(quickState).filter((s) =>
        JSON.stringify(quickState[s]) !== JSON.stringify(quickBaseline[s]));
}

/**
 * Меняет одно значение и перерисовывает вид.
 * Правки из окна («Расширенные…») НЕ перестраивают окно: иначе ползунок,
 * который тянут мышью, и поле ввода, в котором печатают, теряли бы фокус
 * (элемент удалялся бы из DOM прямо во время взаимодействия).
 */
function setQuickValue(section, key, value, fromModal = false) {
    if (!quickState || !quickEditable) return;

    quickState[section][key] = value;
    renderQuick();

    if (!fromModal) renderAdvancedModal();
}

function setSlider(id, value, outKey, suffix) {
    const input = $(id);
    if (!input) return;
    input.value = value;
    const label = document.querySelector(`[data-out="${outKey}"]`);
    if (label) label.textContent = value + suffix;
}

/** Обновляет предпросмотр погоды в карточке и в окне. */
function updateWeatherPreview() {
    if (!quickState) return;

    paintWeather($('qcWeatherPreview'));
    paintWeather($('advWeatherPreview'));

    // Подписи с названием погоды.
    const name = weatherName();
    for (const label of document.querySelectorAll('#qcWeatherPreview .qc-label, #advWeatherPreview .qc-label')) {
        label.textContent = name;
    }

    // Ползунки карточки тоже должны отражать состояние.
    setSlider('qwOvercast', quickState.weather.overcast, 'overcast', '%');
    setSlider('qwRain', quickState.weather.rain, 'rain', '%');
}

/** Красит один предпросмотр погоды по текущему состоянию. */
function paintWeather(preview) {
    if (!preview || !quickState) return;

    const w = quickState.weather;
    const overcast = w.overcast / 100;
    const rain = w.rain / 100;
    const fog = w.fog / 100;
    const snow = w.snow / 100;
    const storm = (w.stormThreshold / 100) * overcast;

    preview.style.setProperty('--sky-top', mix('#4a90d9', '#2b3644', overcast));
    preview.style.setProperty('--sky-bottom', mix('#8fc4f0', '#3c4655', overcast));
    preview.style.setProperty('--fog', (fog * 0.75).toFixed(2));
    preview.style.setProperty('--rain', rain.toFixed(2));
    preview.style.setProperty('--snow', snow.toFixed(2));
    preview.style.setProperty('--storm', storm.toFixed(2));

    // Ветер ускоряет дрейф облаков: 20 м/с — 8 секунд на проход.
    const wind = Math.max(0, Math.min(20, Number(w.wind)));
    const duration = 34 - (wind / 20) * 26;
    preview.style.setProperty('--wind-dur', duration.toFixed(1) + 's');
}

/** Смешивает два HEX-цвета: t = 0 → a, t = 1 → b. */
function mix(a, b, t) {
    const parse = (hex) => [
        parseInt(hex.slice(1, 3), 16),
        parseInt(hex.slice(3, 5), 16),
        parseInt(hex.slice(5, 7), 16)
    ];

    const [ar, ag, ab] = parse(a);
    const [br, bg, bb] = parse(b);
    const k = Math.max(0, Math.min(1, t));

    const r = Math.round(ar + (br - ar) * k);
    const g = Math.round(ag + (bg - ag) * k);
    const bl = Math.round(ab + (bb - ab) * k);

    return `rgb(${r},${g},${bl})`;
}

const WEATHER_PRESETS = {
    clear: { overcast: 0.05, rain: 0, fog: 0.02, snow: 0, wind: 3, storm: 0.2 },
    cloudy: { overcast: 0.6, rain: 0, fog: 0.08, snow: 0, wind: 8, storm: 0.5 },
    rain: { overcast: 0.9, rain: 0.7, fog: 0.2, snow: 0, wind: 12, storm: 0.6 },
    storm: { overcast: 1.0, rain: 0.9, fog: 0.3, snow: 0, wind: 18, storm: 1.0 },
    fog: { overcast: 0.7, rain: 0.1, fog: 0.8, snow: 0, wind: 2, storm: 0.3 }
};

$('qcWeatherPresets')?.addEventListener('click', (event) => {
    const button = event.target.closest('[data-weather]');
    if (!button || !quickEditable) return;

    const preset = WEATHER_PRESETS[button.dataset.weather];
    if (!preset) return;

    Object.assign(quickState.weather, {
        enabled: quickState.weather.enabled,
        overcast: String(Math.round(preset.overcast * 100)),
        rain: String(Math.round(preset.rain * 100)),
        fog: String(Math.round(preset.fog * 100)),
        snow: String(Math.round(preset.snow * 100)),
        wind: String(preset.wind),
        stormThreshold: String(Math.round(preset.storm * 100))
    });

    renderQuick();
    renderAdvancedModal();
});

/* ---- изменения в карточках ---- */

/** Находит описание поля карточки по id элемента. */
function findCardField(elementId) {
    return QUICK_CARD_FIELDS.find((f) => f[2] === elementId) ?? null;
}

function onCardInput(event) {
    const input = event.target;

    // Селект миссии в карточке «Сервер».
    if (input instanceof HTMLSelectElement) {
        if (input.id === 'qcMission') {
            setQuickValue('server', '__mission', input.value);
        }
        return;
    }

    if (!(input instanceof HTMLInputElement)) return;

    // Тумблер «Читать cfggameplay.json» пишется в serverDZ.cfg (раздел server).
    if (input.id === GAMEPLAY_FILE_TOGGLE) {
        setQuickValue('server', 'enableCfgGameplayFile', input.checked ? '1' : '0');
        return;
    }

    const field = findCardField(input.id);
    if (!field) return;

    const [section, key, , invert] = field;

    if (input.type === 'checkbox') {
        // Тумблер показывает «человеческое» значение, в файл идёт 1/0.
        const on = invert ? !input.checked : input.checked;
        setQuickValue(section, key, on ? '1' : '0');
    } else {
        setQuickValue(section, key, input.value);
    }
}

$('quickGrid')?.addEventListener('input', onCardInput);
$('quickGrid')?.addEventListener('change', onCardInput);

/* ============================================================ расширенное окно */

$('quickGrid')?.addEventListener('click', (event) => {
    const button = event.target.closest('[data-advanced]');
    if (!button) return;

    advSection = button.dataset.advanced;
    openAdvancedModal();
});

/** Строит и открывает окно расширенных настроек выбранного раздела. */
function openAdvancedModal() {
    const modal = $('advModal');
    if (!modal) return;

    const info = QUICK_SECTIONS[advSection] ?? QUICK_SECTIONS.server;

    setText('advTitle', info.title);
    setText('advIcon', info.icon);

    renderAdvancedModal();
    modal.classList.add('open');
}

/** Поля погоды: их нет в схеме сервера, они задаются здесь. */
const WEATHER_FIELDS = [
    { key: 'enabled', label: 'Применять погоду', desc: 'Выключено — погода будет случайной, как задано в игре.', kind: 'Bool' },
    { key: 'overcast', label: 'Облачность', desc: '0% — ясное небо, 100% — полностью затянуто тучами.', kind: 'Int', min: 0, max: 100, suffix: '%', group: 'Небо' },
    { key: 'wind', label: 'Ветер', desc: 'Скорость ветра в метрах в секунду.', kind: 'Int', min: 0, max: 20, suffix: ' м/с', group: 'Небо' },
    { key: 'rain', label: 'Дождь', desc: 'Сила дождя. Для дождя нужна облачность выше порога грозы.', kind: 'Int', min: 0, max: 100, suffix: '%', group: 'Осадки' },
    { key: 'fog', label: 'Туман', desc: 'Плотность тумана: 0% — прозрачно, 100% — почти ничего не видно.', kind: 'Int', min: 0, max: 100, suffix: '%', group: 'Осадки' },
    { key: 'snow', label: 'Снег', desc: 'Сила снегопада. Для карт без снега не действует.', kind: 'Int', min: 0, max: 100, suffix: '%', group: 'Осадки' },
    { key: 'stormThreshold', label: 'Порог грозы', desc: 'При какой облачности начинается гроза. Ниже — грозы случаются чаще.', kind: 'Int', min: 0, max: 100, suffix: '%', group: 'Гроза' },
    { key: 'stormDensity', label: 'Плотность грозы', desc: 'Как часто сверкают молнии при грозе.', kind: 'Int', min: 0, max: 100, suffix: '%', group: 'Гроза' }
];

/** Поля раздела: схема с сервера либо встроенные наборы. */
function fieldsFor(section) {
    if (section === 'weather') return WEATHER_FIELDS;

    const schema = quickSchema?.[section];
    let fields = Array.isArray(schema) ? schema.slice() : [];

    if (section === 'server') {
        // Флаг enableCfgGameplayFile теперь живёт в карточке «Мир».
        fields = fields.filter((f) => f.key !== 'enableCfgGameplayFile');

        // Миссия перенесена в главную карточку «Сервер».
        fields = fields.filter((f) => f.key !== '__mission');

        // RCON хранится в профиле панели, а не в serverDZ.cfg.
        fields.push(
            { key: '__rconEnabled', label: 'Включить RCON', desc: 'Живая консоль и команды из панели. Требует перезапуска сервера.', kind: 'Bool', group: 'RCON панели' },
            { key: '__rconPort', label: 'Порт RCON', desc: 'Порт для RCON. Пусто — берётся порт игры + 4.', kind: 'Int', min: 1, max: 65535, group: 'RCON панели' }
        );
    }

    return fields;
}

/** Перерисовывает поля окна из текущего состояния. */
function renderAdvancedModal() {
    const body = $('advBody');
    if (!body || !quickState) return;

    const scroll = body.scrollTop;
    body.textContent = '';
    body.className = 'mbody' + (advSection === 'weather' ? ' adv-body-weather' : '');

    const fields = fieldsFor(advSection);

    if (advSection === 'weather') {
        // Сверху — анимированный предпросмотр, ниже — блоки по категориям в 2 колонки.
        const preview = createWeatherPreview('advWeatherPreview', true);
        preview.classList.add('adv-weather-preview');
        body.appendChild(preview);

        body.appendChild(buildWeatherGroups(fields));
        body.scrollTop = scroll;
        syncAdvPreview();
        return;
    }

    body.appendChild(buildGroupedFields(fields, ''));
    body.scrollTop = scroll;
    syncAdvPreview();
}

/** Группирует поля по названию категории, сохраняя порядок. */
function groupFields(fields) {
    const groups = [];

    for (const field of fields) {
        const name = field.group ?? '';
        const last = groups[groups.length - 1];
        if (last && last.name === name) last.fields.push(field);
        else groups.push({ name, fields: [field] });
    }

    return groups;
}

/** Создаёт блок-карточку категории с её полями. */
function buildBox(group, fields) {
    const box = document.createElement('div');
    box.className = 'adv-box';

    if (group) {
        const head = document.createElement('div');
        head.className = 'adv-group';
        head.textContent = group;
        box.appendChild(head);
    }

    for (const field of fields) {
        box.appendChild(buildAdvField(field, quickValue(advSection, field.key)));
    }

    return box;
}

/**
 * Погода: блоки раскладываются по двум независимым колонкам, чтобы короткие
 * категории не оставляли дыру под собой (в обычной сетке «Гроза» уезжала вниз
 * под высокие «Осадки»).
 */
function buildWeatherGroups(fields) {
    const wrapper = document.createElement('div');
    wrapper.className = 'adv-weather-groups';

    const groups = groupFields(fields);

    // «Применять погоду» (без категории) — отдельной строкой на всю ширину.
    for (const group of groups.filter((g) => g.name === '')) {
        const top = buildBox(group.name, group.fields);
        top.classList.add('adv-weather-top');
        wrapper.appendChild(top);
    }

    // Остальные категории балансируем по двум колонкам по числу полей.
    const columns = [[], []];
    const load = [0, 0];

    for (const group of groups.filter((g) => g.name !== '')) {
        const i = load[0] <= load[1] ? 0 : 1;
        columns[i].push(group);
        load[i] += group.fields.length;
    }

    for (const columnGroups of columns) {
        const column = document.createElement('div');
        column.className = 'adv-col';
        for (const group of columnGroups) column.appendChild(buildBox(group.name, group.fields));
        wrapper.appendChild(column);
    }

    return wrapper;
}

/**
 * Разбивает поля на блоки по категориям. Каждый блок — карточка с заголовком
 * и своими полями; блоки укладываются в сетку контейнера.
 */
function buildGroupedFields(fields, containerClass) {
    const container = document.createElement('div');
    container.className = 'adv-groups ' + containerClass;

    for (const group of groupFields(fields)) {
        container.appendChild(buildBox(group.name, group.fields));
    }

    if (fields.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'adv-empty';
        empty.textContent = 'Для этого раздела параметров не найдено.';
        container.appendChild(empty);
    }

    return container;
}

/**
 * Создаёт разметку анимированного предпросмотра погоды.
 * Повторно используется в карточке и в расширенном окне.
 */
function createWeatherPreview(id, withLabel) {
    const preview = document.createElement('div');
    preview.className = 'qc-preview';
    if (id) preview.id = id;
    preview.setAttribute('aria-hidden', 'true');

    const parts = [
        '<span class="qc-cloud"></span>',
        '<span class="qc-cloud qc-cloud2"></span>',
        '<span class="qc-rain"></span>',
        '<span class="qc-snow"></span>',
        '<span class="qc-sun">☀</span>',
        '<span class="qc-bolt">⚡</span>'
    ];

    if (withLabel) {
        parts.push('<span class="qc-label"></span>');
    }

    preview.innerHTML = parts.join('');
    return preview;
}

/**
 * Строит один пункт окна: строку с подписью и элементом, а под ней —
 * пояснение со значком «?». Булевы значения — наш тумблер.
 * Тип поля приходит с сервера в поле «kind» (Bool/Int/Float/Text/Select).
 */
function buildAdvField(field, value) {
    const item = document.createElement('div');
    item.className = 'adv-item';

    const row = document.createElement('div');
    row.className = 'adv-field';

    const label = document.createElement('label');
    label.textContent = field.label;

    const id = 'adv_' + field.key.replace(/\./g, '_');
    label.htmlFor = id;

    const kind = String(field.kind ?? 'Text');

    if (kind === 'Bool') {
        // Тумблер в нашем стиле: включено — зелёный, выключено — серый.
        row.classList.add('adv-bool');

        const sw = document.createElement('label');
        sw.className = 'sw';

        const input = document.createElement('input');
        input.type = 'checkbox';
        input.id = id;
        input.checked = value === '1';
        input.disabled = !quickEditable;
        input.addEventListener('change', () => setQuickValue(advSection, field.key, input.checked ? '1' : '0', true));

        const track = document.createElement('span');
        sw.appendChild(input);
        sw.appendChild(track);

        row.appendChild(label);
        row.appendChild(sw);
        item.appendChild(row);
        item.appendChild(describeField(field));
        return item;
    }

    if (kind === 'Select') {
        const select = document.createElement('select');
        select.id = id;
        select.disabled = !quickEditable;

        const options = (field.options ?? []).map((option) => {
            const [optionValue, optionLabel] = String(option).split('|');
            return { value: optionValue, label: optionLabel ?? optionValue };
        });

        // Текущее значение может отсутствовать в списке (частый случай — пустая миссия).
        if (!options.some((o) => o.value === String(value))) {
            options.unshift({ value: String(value), label: value === '' ? '— не задано —' : String(value) });
        }

        for (const option of options) {
            const el = document.createElement('option');
            el.value = option.value;
            el.textContent = option.label;
            select.appendChild(el);
        }

        select.value = String(value);
        select.addEventListener('change', () => setQuickValue(advSection, field.key, select.value, true));
        row.appendChild(label);
        row.appendChild(select);
        item.appendChild(row);
        item.appendChild(describeField(field));
        return item;
    }

    const numeric = kind === 'Int' || kind === 'Float';

    // Поля погоды показываем ползунками — так нагляднее.
    if (numeric && field.suffix) {
        row.classList.add('adv-range');

        const valueLabel = document.createElement('b');
        valueLabel.textContent = value + (field.suffix ?? '');

        const input = document.createElement('input');
        input.type = 'range';
        input.id = id;
        if (field.min != null) input.min = field.min;
        if (field.max != null) input.max = field.max;
        input.value = value;
        input.disabled = !quickEditable;
        input.addEventListener('input', () => {
            valueLabel.textContent = input.value + (field.suffix ?? '');
            setQuickValue(advSection, field.key, input.value, true);
        });

        const head = document.createElement('div');
        head.className = 'adv-range-head';
        head.appendChild(label);
        head.appendChild(valueLabel);

        row.appendChild(head);
        row.appendChild(input);
        item.appendChild(row);
        item.appendChild(describeField(field));
        return item;
    }

    const input = document.createElement('input');
    input.type = numeric ? 'number' : 'text';
    input.id = id;
    input.value = value ?? '';

    if (numeric) {
        if (field.min != null) input.min = field.min;
        if (field.max != null) input.max = field.max;
        if (kind === 'Float') input.step = '0.01';
    }

    input.disabled = !quickEditable;
    input.addEventListener('input', () => setQuickValue(advSection, field.key, input.value, true));
    input.addEventListener('change', () => setQuickValue(advSection, field.key, input.value, true));

    row.appendChild(label);
    row.appendChild(input);
    item.appendChild(row);
    item.appendChild(describeField(field));
    return item;
}

/** Пояснение под настройкой: значок «?» со всплывающей подсказкой. */
function describeField(field) {
    const wrap = document.createElement('div');
    wrap.className = 'adv-desc';

    const mark = document.createElement('span');
    mark.className = 'adv-q';
    mark.textContent = '?';
    mark.title = field.desc ?? '';
    mark.setAttribute('aria-label', field.desc ?? '');

    const text = document.createElement('span');
    text.textContent = field.desc ?? '';

    wrap.appendChild(mark);
    wrap.appendChild(text);
    return wrap;
}

/** Синхронизирует предпросмотр внутри окна. */
function syncAdvPreview() {
    paintWeather($('advWeatherPreview'));

    const label = document.querySelector('#advWeatherPreview .qc-label');
    if (label) label.textContent = weatherName();
}

function closeAdvancedModal() {
    $('advModal')?.classList.remove('open');
}

$('advX')?.addEventListener('click', closeAdvancedModal);
$('advModal')?.addEventListener('click', (event) => {
    if (event.target.id === 'advModal') closeAdvancedModal();
});

/** Сбрасывает раздел к значениям, загруженным с сервера. */
$('advReset')?.addEventListener('click', () => {
    if (!quickBaseline || !quickEditable) return;
    quickState[advSection] = JSON.parse(JSON.stringify(quickBaseline[advSection]));
    renderQuick();
    renderAdvancedModal();
});

/** «Применить» — закрывает окно: значения уже в состоянии, сохранит основная кнопка. */
$('advApply')?.addEventListener('click', () => {
    closeAdvancedModal();
    toast('Применено', 'Нажмите «Сохранить настройки», чтобы записать в файлы.', 'info', 5000);
});

/* ---- сохранение ---- */

$('btnQuickReload')?.addEventListener('click', loadQuickSettings);

/** Включает чтение cfggameplay.json в serverDZ.cfg по кнопке. */
$('qmEnableFile')?.addEventListener('click', async () => {
    const button = $('qmEnableFile');
    if (button) { button.disabled = true; button.textContent = 'Включение…'; }

    try {
        const result = await api(sp('/quick/gameplay/enable'), { method: 'POST' });
        quickGameplayFile = { exists: true, enabled: !!result.enabled };

        // Флаг enableCfgGameplayFile живёт в serverDZ.cfg — синхронизируем состояние.
        if (quickState?.server) {
            quickState.server.enableCfgGameplayFile = result.enabled ? '1' : '0';
            quickBaseline.server.enableCfgGameplayFile = result.enabled ? '1' : '0';
        }

        updateGameplayWarning();
        renderQuick();
        renderAdvancedModal();
        toast('Файл мира включён',
            'В serverDZ.cfg добавлен enableCfgGameplayFile = 1.', 'info', 7000);
    } catch (error) {
        toast('Не удалось включить', error.message, 'error', 8000);
    } finally {
        if (button) { button.disabled = false; button.textContent = 'Включить'; }
    }
});

$('btnQuickSave')?.addEventListener('click', () => saveQuickSettings(true));

/**
 * Сохраняет быстрые настройки в файлы сервера: по одному запросу на раздел.
 * Возвращает true при успехе. При <paramref name="showToast"/> — уведомляет.
 */
async function saveQuickSettings(showToast = false) {
    if (!quickState) return false;
    if (!quickHasChanges()) {
        if (showToast) toast('Нечего сохранять', 'Изменений нет.', 'info');
        return true;
    }

    const button = $('btnQuickSave');
    if (button) { button.disabled = true; button.textContent = 'Сохранение…'; }

    const changed = quickChangedSections();

    try {
        // serverDZ.cfg, cfggameplay.json и globals.xml — плоские словари по схеме.
        for (const section of ['server', 'gameplay', 'globals']) {
            if (!changed.includes(section)) continue;

            await api(sp('/quick/' + section), {
                method: 'PUT',
                body: quickState[section]
            });
        }

        // Погода: доли 0..1 и м/с.
        if (changed.includes('weather')) {
            const w = quickState.weather;
            await api(sp('/quick/weather'), {
                method: 'PUT',
                body: {
                    weather: {
                        enabled: w.enabled === '1',
                        overcast: Number(w.overcast) / 100,
                        rain: Number(w.rain) / 100,
                        fog: Number(w.fog) / 100,
                        snow: Number(w.snow) / 100,
                        wind: Number(w.wind),
                        stormDensity: Number(w.stormDensity) / 100,
                        stormThreshold: Number(w.stormThreshold) / 100,
                        freeze: false
                    }
                }
            });
        }

        // После сохранения исходное состояние — текущее.
        const missionChanged = quickState.server &&
            quickBaseline.server &&
            (quickBaseline.server.__mission !== quickState.server.__mission);
        quickBaseline = JSON.parse(JSON.stringify(quickState));
        markDirty();

        // Сменили миссию — карта у сервера другая: сбрасываем и грузим заново,
        // чтобы «Живая карта» не показывала прежнюю.
        if (missionChanged && typeof Players !== 'undefined') {
            Players.reset();
            await Players.reloadMap();
        }

        if (showToast) {
            toast(
                quickServerRunning ? 'Правки отложены' : 'Настройки сохранены',
                quickServerRunning
                    ? 'Сервер запущен: изменения применятся при следующем запуске.'
                    : 'Изменения записаны в файлы сервера.',
                'info', 7000);
        }

        return true;
    } catch (error) {
        toast('Не удалось сохранить', error.message, 'error', 9000);
        return false;
    } finally {
        if (button) { button.disabled = false; button.textContent = 'Сохранить настройки'; }
    }
}

/* ============================================================ резервные копии */

async function loadBackups() {
    try {
        const data = await api(sp('/backups'));
        setText('backupDirectory', 'Каталог: ' + data.directory);

        const tbody = $('backupRows');
        clearChildren(tbody);

        if (!data.items.length) {
            const tr = document.createElement('tr');
            const td = cell('Резервных копий пока нет.', 'muted');
            td.colSpan = 5;
            tr.appendChild(td);
            tbody.appendChild(tr);
            return;
        }

        for (const item of data.items) {
            const tr = document.createElement('tr');
            tr.appendChild(cell(item.fileName, 'mono'));
            tr.appendChild(cell(kindLabel(item.kind)));
            tr.appendChild(cell(formatBytes(item.sizeBytes)));
            tr.appendChild(cell(formatTime(item.createdAt)));

            const actions = document.createElement('td');
            actions.className = 'actions';

            const download = document.createElement('a');
            download.className = 'btn ghost sm';
            download.textContent = 'Скачать';
            download.href = '/api' + sp('/backups/download/' + encodeURIComponent(item.fileName));
            actions.appendChild(download);

            const restore = document.createElement('button');
            restore.type = 'button';
            restore.className = 'btn sm';
            restore.textContent = 'Восстановить';
            restore.addEventListener('click', () => {
                askConfirm(`Восстановить данные из «${item.fileName}»? Текущее состояние будет сохранено отдельной копией.`,
                    () => restoreBackup(item.fileName), 'Восстановить');
            });
            // Архивы логов не восстанавливаем — только скачивание и удаление.
            if (item.kind !== 'logs') actions.appendChild(restore);

            const remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'btn danger sm';
            remove.textContent = 'Удалить';
            remove.addEventListener('click', () => {
                askConfirm(`Удалить резервную копию «${item.fileName}»?`, () => deleteBackup(item.fileName), 'Удалить');
            });
            actions.appendChild(remove);

            tr.appendChild(actions);
            tbody.appendChild(tr);
        }
    } catch (error) {
        toast('Не удалось получить копии', error.message, 'error');
    }
}

async function createBackup() {
    toast('Резервное копирование', 'Создание копии…', 'info', 3000);
    try {
        const result = await api(sp('/backups'), { method: 'POST' });
        toast('Готово', result.message, 'info', 7000);
        loadBackups();
    } catch (error) {
        toast('Ошибка копирования', error.message, 'error', 8000);
    }
}

/** Понятное название типа копии. */
function kindLabel(kind) {
    switch (kind) {
        case 'manual': return 'Вручную';
        case 'schedule': return 'Расписание';
        case 'pre-restore': return 'Перед восстановлением';
        case 'logs': return 'Логи';
        default: return kind || '—';
    }
}

async function restoreBackup(fileName) {
    try {
        const result = await api(sp('/backups/restore'), { method: 'POST', body: { fileName } });
        toast('Восстановлено', result.message, 'info', 8000);
        loadBackups();
    } catch (error) {
        toast('Ошибка восстановления', error.message, 'error', 8000);
    }
}

async function deleteBackup(fileName) {
    try {
        await api(sp('/backups/' + encodeURIComponent(fileName)), { method: 'DELETE' });
        toast('Удалено', 'Резервная копия удалена.', 'info');
        loadBackups();
    } catch (error) {
        toast('Не удалось удалить', error.message, 'error');
    }
}

$('btnCreateBackup').addEventListener('click', createBackup);
$('btnReloadBackups').addEventListener('click', loadBackups);

/* ============================================================ расписание */

const JOB_TYPES = { start: 'Запуск', stop: 'Остановка', restart: 'Перезапуск', backup: 'Резервная копия', command: 'Команда' };

async function loadSchedule() {
    try {
        const data = await api(sp('/schedule'));
        const tbody = $('jobRows');
        clearChildren(tbody);

        if (!data.jobs.length) {
            const tr = document.createElement('tr');
            const td = cell('Задач нет. Добавьте первую задачу.', 'muted');
            td.colSpan = 6;
            tr.appendChild(td);
            tbody.appendChild(tr);
        }

        for (const job of data.jobs) {
            const tr = document.createElement('tr');
            tr.appendChild(cell(job.name));
            tr.appendChild(cell(JOB_TYPES[job.type] ?? job.type));
            tr.appendChild(cell(job.mode === 'daily' ? `ежедневно в ${job.time}` : `каждые ${job.intervalMinutes} мин`));

            const enabled = document.createElement('td');
            const tag = document.createElement('span');
            tag.className = 'tag ' + (job.enabled ? 'on' : 'off');
            tag.textContent = job.enabled ? 'вкл' : 'выкл';
            enabled.appendChild(tag);
            tr.appendChild(enabled);

            tr.appendChild(cell(job.lastRun ? formatTime(job.lastRun) : '—'));

            const actions = document.createElement('td');
            actions.className = 'actions';

            const run = document.createElement('button');
            run.type = 'button';
            run.className = 'btn sm';
            run.textContent = 'Выполнить';
            run.addEventListener('click', async () => {
                try {
                    const execution = await api(`/schedule/${job.id}/run`, { method: 'POST' });
                    toast('Задача выполнена', execution.message, execution.success ? 'info' : 'error', 7000);
                    loadSchedule();
                } catch (error) {
                    toast('Ошибка задачи', error.message, 'error');
                }
            });
            actions.appendChild(run);

            const edit = document.createElement('button');
            edit.type = 'button';
            edit.className = 'btn ghost sm';
            edit.textContent = 'Изменить';
            edit.addEventListener('click', () => openJobModal(job));
            actions.appendChild(edit);

            const remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'btn danger sm';
            remove.textContent = 'Удалить';
            remove.addEventListener('click', () => {
                askConfirm(`Удалить задачу «${job.name}»?`, async () => {
                    await api(sp('/schedule/' + job.id), { method: 'DELETE' });
                    loadSchedule();
                }, 'Удалить');
            });
            actions.appendChild(remove);

            tr.appendChild(actions);
            tbody.appendChild(tr);
        }

        const history = $('jobHistory');
        clearChildren(history);
        const entries = (data.history ?? []).slice().reverse();

        if (!entries.length) {
            const tr = document.createElement('tr');
            const td = cell('История пуста.', 'muted');
            td.colSpan = 5;
            tr.appendChild(td);
            history.appendChild(tr);
        }

        for (const entry of entries) {
            const tr = document.createElement('tr');
            tr.appendChild(cell(formatTime(entry.at)));
            tr.appendChild(cell(entry.name));
            tr.appendChild(cell(JOB_TYPES[entry.type] ?? entry.type));
            tr.appendChild(cell(entry.success ? 'успешно' : 'ошибка'));
            tr.appendChild(cell(entry.message));
            if (!entry.success) tr.style.color = 'var(--red)';
            history.appendChild(tr);
        }
    } catch (error) {
        toast('Не удалось получить расписание', error.message, 'error');
    }
}

function updateJobPayloadVisibility() {
    $('jobPayloadRow').classList.toggle('hidden', $('jobType').value !== 'command');
}

$('jobType').addEventListener('change', updateJobPayloadVisibility);

function openJobModal(job) {
    editingJobId = job?.id ?? null;
    setText('jobModalTitle', job ? 'Изменить задачу' : 'Новая задача');
    $('jobName').value = job?.name ?? '';
    $('jobType').value = job?.type ?? 'backup';
    $('jobMode').value = job?.mode ?? 'daily';
    $('jobTime').value = job?.time ?? '04:00';
    $('jobInterval').value = job?.intervalMinutes ?? 360;
    $('jobPayload').value = job?.payload ?? '';
    $('jobEnabled').checked = job?.enabled ?? true;
    updateJobPayloadVisibility();
    $('jobModal').classList.add('open');
    $('jobName').focus();
}

$('btnNewJob').addEventListener('click', () => openJobModal(null));
$('jobCancel').addEventListener('click', () => $('jobModal').classList.remove('open'));
$('jobCancelX').addEventListener('click', () => $('jobModal').classList.remove('open'));

$('jobSave').addEventListener('click', async () => {
    const job = {
        id: editingJobId ?? '',
        name: $('jobName').value.trim() || 'Задача',
        type: $('jobType').value,
        mode: $('jobMode').value,
        time: $('jobTime').value.trim(),
        intervalMinutes: Number($('jobInterval').value),
        payload: $('jobPayload').value,
        enabled: $('jobEnabled').checked
    };

    try {
        if (editingJobId) {
            await api(sp('/schedule/' + editingJobId), { method: 'PUT', body: job });
        } else {
            await api(sp('/schedule'), { method: 'POST', body: job });
        }
        $('jobModal').classList.remove('open');
        toast('Сохранено', 'Задача обновлена.', 'info');
        loadSchedule();
    } catch (error) {
        toast('Не удалось сохранить задачу', error.message, 'error');
    }
});

/* ============================================================ служба */

async function loadService() {
    try {
        const status = await api('/service/status');
        const info = $('serviceInfo');
        clearChildren(info);

        const rows = [
            ['Установлена', status.installed ? 'да' : 'нет'],
            ['Имя службы', status.name],
            ['Отображаемое имя', status.displayName],
            ['Состояние', status.state],
            ['Тип запуска', status.startMode],
            ['Права администратора', status.isElevated ? 'есть' : 'нет']
        ];

        for (const [key, value] of rows) {
            const dt = document.createElement('dt');
            dt.textContent = key;
            const dd = document.createElement('dd');
            dd.textContent = value ?? '—';
            info.appendChild(dt);
            info.appendChild(dd);
        }

        if (status.executablePath) {
            const dt = document.createElement('dt');
            dt.textContent = 'Команда';
            const dd = document.createElement('dd');
            dd.className = 'mono';
            dd.textContent = status.executablePath;
            info.appendChild(dt);
            info.appendChild(dd);
        }

        setText('serviceHelp', status.isElevated
            ? 'Права администратора есть — установка и удаление службы доступны.'
            : 'Для установки и удаления службы запустите приложение от имени администратора.');

        $('btnInstallService').disabled = !status.isElevated;
        $('btnUninstallService').disabled = !status.isElevated;
    } catch (error) {
        toast('Не удалось получить состояние службы', error.message, 'error');
    }
}

async function loadApplicationInfo() {
    try {
        const data = await api('/system/info');
        const paths = $('pathsInfo');
        const host = data.host;

        if (currentView === 'security') return;

        // Сводка по хосту на странице службы.
        const info = $('serviceInfo');
        const dt = document.createElement('dt');
        dt.textContent = 'Компьютер';
        const dd = document.createElement('dd');
        dd.textContent = `${host.machineName} · ${host.osArchitecture} · ${host.processorCount} ядер`;
        info.appendChild(dt);
        info.appendChild(dd);
    } catch {
        // Не критично.
    }
}

async function serviceAction(action, label) {
    try {
        const result = await api('/service/' + action, { method: 'POST', body: {} });
        toast(label, result.message ?? '', 'info', 7000);
        loadService();
    } catch (error) {
        toast('Не удалось выполнить', error.message, 'error', 9000);
    }
}

/* -------------------------------------------------------- заметки админов */

let notesState = { notes: [], unread: 0, user: '' };

/** Обновляет бейдж непрочитанных заметок на странице «Все серверы». */
async function refreshNotesBadge() {
    try {
        const data = await api('/notes');
        notesState = data ?? { notes: [], unread: 0, user: '' };
        applyNotesBadge();
    } catch {
        // Заметки — не критичный раздел.
    }
}

function applyNotesBadge() {
    const badge = $('ovNotesBadge');
    const count = $('ovNotesCount');

    // На карточке показываем число непрочитанных (или всего, если всё прочитано).
    setText('ovNotesCount', notesState.unread > 0 ? notesState.unread : notesState.notes.length);

    if (badge) {
        badge.textContent = String(notesState.unread);
        badge.classList.toggle('hidden', !notesState.unread);
        badge.title = notesState.unread > 0
            ? `Непрочитанных заметок: ${notesState.unread}`
            : 'Все заметки прочитаны';

        // Список прочитавших — в подсказке бейджа.
        badge.dataset.readers = notesState.notes
            .filter((n) => n.unread)
            .flatMap((n) => n.readBy ?? [])
            .join(', ');
    }
}

/** Открывает модалку заметок и отмечает их прочитанными. */
async function openNotes() {
    const modal = $('notesModal');
    if (!modal) return;

    await refreshNotesBadge();
    renderNotes();
    setText('notesSub', notesState.user ? `вы: ${notesState.user}` : '');
    modal.classList.add('open');

    // Открыл ленту — значит прочитал: отмечаем на сервере.
    if (notesState.unread > 0) {
        try {
            await api('/notes/read', { method: 'POST' });
            await refreshNotesBadge();
            renderNotes();
        } catch { /* отметка не критична */ }
    }
}

/** Рисует список заметок. */
function renderNotes() {
    const list = $('notesList');
    if (!list) return;

    list.textContent = '';

    if (!notesState.notes.length) {
        const empty = document.createElement('div');
        empty.className = 'empty-state';
        empty.textContent = 'Заметок пока нет. Напишите первую — её увидят все администраторы.';
        list.appendChild(empty);
        return;
    }

    for (const note of notesState.notes) {
        const row = document.createElement('div');
        row.className = 'note-row' + (note.unread ? ' unread' : '');

        const head = document.createElement('div');
        head.className = 'note-head';

        const author = document.createElement('b');
        author.textContent = note.author || 'админ';

        const time = document.createElement('span');
        time.className = 'muted mono';
        time.style.fontSize = '11.5px';
        time.textContent = formatTime(note.createdAt);

        head.appendChild(author);
        head.appendChild(time);

        if (note.unread) {
            const fresh = document.createElement('span');
            fresh.className = 'note-new';
            fresh.textContent = 'новая';
            head.appendChild(fresh);
        }

        // Кто прочитал — иконка со списком имён в подсказке.
        const readers = document.createElement('span');
        readers.className = 'note-readers';
        readers.textContent = '👁';
        readers.title = (note.readBy ?? []).length
            ? 'Прочитали: ' + note.readBy.join(', ')
            : 'Пока никто не прочитал';

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'note-del';
        remove.title = 'Удалить заметку';
        remove.textContent = '×';
        remove.addEventListener('click', async () => {
            try {
                await api('/notes/' + encodeURIComponent(note.id), { method: 'DELETE' });
                await refreshNotesBadge();
                renderNotes();
            } catch (error) {
                toast('Не удалось удалить', error.message, 'error');
            }
        });

        const tools = document.createElement('span');
        tools.className = 'note-tools';
        tools.appendChild(readers);
        tools.appendChild(remove);

        head.appendChild(tools);

        const text = document.createElement('div');
        text.className = 'note-text';
        text.textContent = note.text;

        row.appendChild(head);
        row.appendChild(text);
        list.appendChild(row);
    }
}

/** Отправляет новую заметку. */
async function addNote() {
    const input = $('noteInput');
    const text = (input?.value ?? '').trim();
    if (!text) return;

    const button = $('noteAdd');
    button.disabled = true;

    try {
        await api('/notes', { method: 'POST', body: { text } });
        input.value = '';
        await refreshNotesBadge();
        renderNotes();
    } catch (error) {
        toast('Не удалось добавить', error.message, 'error');
    } finally {
        button.disabled = false;
    }
}

$('ovNotesOpen')?.addEventListener('click', openNotes);
$('notesX')?.addEventListener('click', () => $('notesModal')?.classList.remove('open'));
$('notesModal')?.addEventListener('click', (event) => {
    if (event.target.id === 'notesModal') $('notesModal').classList.remove('open');
});
$('noteAdd')?.addEventListener('click', addNote);
$('noteInput')?.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        addNote();
    }
});

/* -------------------------------------------- веб-интерфейс (обновление) */

let webUiPollTimer = null;

// Ссылка на одно залипающее уведомление об обновлении: повторные проверки
// не должны плодить их десятками.
let webUiNotice = null;

/** Показывает залипающее уведомление об обновлении веб-части с кнопками. */
function showWebUiUpdateNotice() {
    if (webUiNotice && document.body.contains(webUiNotice)) {
        return;
    }

    webUiNotice = toast('Доступно обновление веб-интерфейса',
        'Обновите панель, чтобы применить новую вёрстку и скрипты. Служба останавливаться не будет.',
        'info', 0, {
            sticky: true,
            actions: [
                {
                    label: 'Обновить', kind: 'primary',
                    onClick: () => installWebUiUpdate()
                },
                {
                    label: 'Позже',
                    onClick: (_button, box) => box.remove()
                }
            ]
        });
}

/** Запускает установку обновления веб-части с прогрессом. */
async function installWebUiUpdate() {
    try {
        paintWebUiProgress(0, 'подготовка');
        startWebUiPoll();
        await api('/web-ui/install', { method: 'POST' });
    } catch (error) {
        clearInterval(webUiPollTimer);
        webUiPollTimer = null;
        $('webUiProgress')?.classList.add('hidden');
        toast('Не удалось обновить', error.message, 'error', 9000);
        loadWebUi();
    }
}

/** Загружает состояние веб-части и показывает доступность обновления. */
async function loadWebUi() {
    const info = $('webUiInfo');
    if (!info) return;

    try {
        const data = await api('/web-ui');

        const updateAvailable = data.available && !data.upToDate;

        // Статус словами — без номеров версий.
        const status = !data.available
            ? 'сайт недоступен'
            : updateAvailable ? 'доступно обновление' : 'актуальная версия';

        const rows = [
            ['Веб-интерфейс', data.overridden ? 'обновлённый' : 'встроенный'],
            ['Обновления', status],
            ['Источник', data.baseUrl || '—']
        ];

        info.textContent = '';
        for (const [key, value] of rows) {
            const dt = document.createElement('dt');
            dt.textContent = key;
            const dd = document.createElement('dd');
            dd.textContent = value ?? '—';
            info.appendChild(dt);
            info.appendChild(dd);
        }

        $('webUiInstall').disabled = !updateAvailable || data.busy;
        $('webUiInstall').textContent = updateAvailable
            ? 'Обновить веб-часть'
            : 'Обновление не требуется';
        $('webUiDot').classList.toggle('on', updateAvailable);
        $('webUiReset').disabled = !data.overridden;

        const autoCheck = $('webUiAutoCheck');
        if (autoCheck && document.activeElement !== autoCheck) autoCheck.checked = !!data.autoCheck;

        if (data.busy) {
            paintWebUiProgress(data.progress, data.phase);
            startWebUiPoll();
        }
    } catch (error) {
        info.textContent = 'Не удалось получить состояние: ' + error.message;
    }
}

function paintWebUiProgress(percent, phase) {
    $('webUiProgress')?.classList.remove('hidden');
    setText('webUiPhase', phase || 'обновление');
    setText('webUiPercent', (percent ?? 0) + '%');
    if ($('webUiFill')) $('webUiFill').style.width = (percent ?? 0) + '%';
}

function startWebUiPoll() {
    if (webUiPollTimer) return;
    webUiPollTimer = setInterval(async () => {
        try {
            const data = await api('/web-ui');
            paintWebUiProgress(data.progress, data.phase);

            if (!data.busy) {
                clearInterval(webUiPollTimer);
                webUiPollTimer = null;
                $('webUiProgress')?.classList.add('hidden');
                await loadWebUi();
                // Скрипты и стили изменились: перезагружаем страницу, чтобы
                // браузер взял новую веб-часть.
                toast('Веб-часть обновлена', 'Перезагружаем страницу…', 'info', 2500);
                setTimeout(() => window.location.reload(), 1200);
            }
        } catch {
            clearInterval(webUiPollTimer);
            webUiPollTimer = null;
        }
    }, 700);
}

$('webUiCheck')?.addEventListener('click', async () => {
    const button = $('webUiCheck');
    button.disabled = true;
    button.textContent = 'Проверка…';
    await loadWebUi();
    button.disabled = false;
    button.textContent = 'Проверить обновление';
});

$('webUiInstall')?.addEventListener('click', () => {
    askConfirm('Скачать и установить обновление веб-интерфейса? Служба останавливаться не будет.',
        installWebUiUpdate, 'Обновить');
});

$('webUiReset')?.addEventListener('click', () => {
    askConfirm('Удалить обновлённую веб-часть и вернуться к встроенной версии?',
        async () => {
            try {
                await api('/web-ui', { method: 'DELETE' });
                loadWebUi();
            } catch (error) {
                toast('Не удалось', error.message, 'error');
            }
        }, 'Вернуть встроенную');
});

$('webUiAutoCheck')?.addEventListener('change', async (event) => {
    try {
        await api('/web-ui/autocheck', { method: 'PUT', body: { enabled: event.target.checked } });
    } catch (error) {
        toast('Не удалось сохранить', error.message, 'error');
        event.target.checked = !event.target.checked;
    }
});

$('btnInstallService').addEventListener('click', () => {
    askConfirm('Установить DayZPulse как службу Windows с автозапуском?', () => serviceAction('install', 'Установка службы'), 'Установить');
});
$('btnUninstallService').addEventListener('click', () => {
    askConfirm('Удалить службу Windows DayZPulse?', () => serviceAction('uninstall', 'Удаление службы'), 'Удалить');
});
$('btnStartService').addEventListener('click', () => serviceAction('start', 'Запуск службы'));
$('btnStopService').addEventListener('click', () => serviceAction('stop', 'Остановка службы'));
$('btnReloadService').addEventListener('click', loadService);

async function restartApplication() {
    try {
        await api('/system/restart', { method: 'POST' });
        toast('Перезапуск', 'Приложение перезапускается…', 'warn', 6000);
    } catch (error) {
        toast('Не удалось перезапустить', error.message, 'error');
    }
}

$('btnRestartApp').addEventListener('click', () => {
    askConfirm('Перезапустить приложение DayZPulse?', restartApplication, 'Перезапустить');
});
$('btnRestartApp2').addEventListener('click', () => {
    askConfirm('Перезапустить приложение DayZPulse? Изменения веб-сервера применятся после перезапуска.', restartApplication, 'Перезапустить');
});

$('btnExportSettings').addEventListener('click', () => { window.location.href = '/api/settings/export'; });

$('btnImportSettings').addEventListener('click', () => $('importFile').click());

$('importFile').addEventListener('change', async (event) => {
    const file = event.target.files?.[0];
    if (!file) return;

    const formData = new FormData();
    formData.append('file', file);

    try {
        await api('/settings/import', { method: 'POST', body: formData });
        toast('Импортировано', 'Настройки загружены. Перезапустите приложение.', 'info', 8000);
        loadSettings();
    } catch (error) {
        toast('Ошибка импорта', error.message, 'error');
    } finally {
        event.target.value = '';
    }
});

/* ============================================================ настройки */

function fillSelect(id, values, current) {
    const select = $(id);
    clearChildren(select);
    for (const value of values ?? []) {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = value;
        select.appendChild(option);
    }
    if (current) select.value = current;
}

async function loadSettings() {
    try {
        const data = await api('/settings');
        const serverData = await api(sp('/settings'));

        loadedSettings = { global: data, server: serverData };
        const s = serverData.server;
        const profile = serverData.profile;

        $('setServerColor').value = profile.color ?? '#3fb950';
        $('setServerName').value = profile.name ?? '';
        $('setBaseDir').value = s.baseDirectory ?? 'C:\\DayZServer';
        $('setWorkDir').value = s.workingDirectory ?? '';
        $('setExecutableName').value = s.executableName ?? '';
        $('setExe').value = s.executablePath ?? '';
        $('setConfig').value = s.configFile ?? '';
        $('setProfiles').value = s.profilesDirectory ?? '';
        $('setStopCommand').value = s.stopCommand ?? '';
        $('setGraceful').value = s.gracefulStopSeconds;
        $('setRestartDelay').value = s.autoRestartDelaySeconds;
        $('setMaxRestarts').value = s.maxAutoRestarts;
        $('setRestartWindow').value = s.autoRestartWindowMinutes;

        $('setStartOnLaunch').checked = !!s.startOnLaunch;
        $('setAutoRestart').checked = !!s.autoRestartOnCrash;
        $('setStdIn').checked = !!s.redirectStandardInput;
        $('setUseStopCommand').checked = !!s.useStopCommand;
        $('setClearConsole').checked = !!s.clearConsoleOnStart;
        $('setAllowOutside').checked = !!s.allowDirectoryOutsideBase;

        fillSelect('setEncoding', serverData.encodings, s.consoleEncoding);
        fillSelect('setPriority', ['Normal', 'AboveNormal', 'High', 'BelowNormal', 'Idle'], s.processPriority);

        const global = data;
        $('setWebPort').value = global.web.port;
        $('setBind').value = global.web.bindAddress;
        $('setAllowRemote').checked = !!global.web.allowRemote;
        $('setOpenBrowser').checked = !!global.web.openBrowserOnStart;

        $('setRetention').value = serverData.backups.retentionCount;
        fillSelect('setCompression', ['Fastest', 'Optimal', 'SmallestSize', 'NoCompression'], serverData.backups.compressionLevel);
        $('setIncludes').value = (serverData.backups.includes ?? []).join('\n');
        $('setExcludes').value = (serverData.backups.excludePatterns ?? []).join('\n');
        $('setBackupEnabled').checked = !!serverData.backups.enabled;
        $('setStopForBackup').checked = !!serverData.backups.stopServerDuringBackup;

        $('setConsoleLines').value = global.logging.maxConsoleLines;
        fillSelect('setLogLevel', ['Debug', 'Information', 'Warning', 'Error'], global.logging.minimumLevel);
        $('setLogRetention').value = global.logging.logRetentionDays;
        $('setWriteServerLog').checked = !!global.logging.writeServerLogFile;
        $('setSessionHours').value = global.auth.sessionHours;

        $('setEnvVars').value = Object.entries(s.environmentVariables ?? {})
            .map(([k, v]) => `${k}=${v}`).join('\n');

        $('setConfigFiles').value = (serverData.configFiles ?? [])
            .map((c) => [c.name, c.path, c.description ?? ''].join('|')).join('\n');

        try {
            const autostart = await api('/system/autostart');
            $('setAutostart').checked = !!autostart.enabled;
        } catch { /* недоступно в режиме службы */ }

        // Состояние SteamCMD и вход в Steam.
        try {
            const steam = await api('/steam/status');
            setText('steamSettingsHint', !steam.installed
                ? 'SteamCMD не установлен — нажмите «Установить SteamCMD».'
                : steam.loggedIn
                    ? `Установлен · вход выполнен${steam.username ? ': ' + steam.username : ''}`
                    : 'Установлен · вход в Steam не выполнен');

            const steamDir = $('setSteamDir');
            if (steamDir) {
                steamDir.value = steam.directory ?? '';
            }
        } catch {
            setText('steamSettingsHint', 'не удалось получить состояние');
        }

        // Состояние сервера DayZ в папке текущего сервера.
        try {
            const server = await api('/steam/server/status?serverId=' + encodeURIComponent(currentServerId ?? ''));
            setText('steamServerHint', server.installed
                ? 'установлен: ' + (server.directory ?? '')
                : !server.directoryExists
                    ? 'папка сервера не найдена'
                    : !server.loggedIn
                        ? 'не установлен · сначала войдите в Steam'
                        : 'не установлен — нажмите «Скачать сервер»');

            const button = $('btnSteamServerInstall');
            if (button) {
                button.disabled = !!server.installed || !!server.running || !server.loggedIn;
                button.textContent = server.running ? 'Загрузка…' : server.installed ? 'Сервер установлен' : 'Скачать сервер';
            }

            applyServerInstallPush(server);
        } catch {
            setText('steamServerHint', 'не удалось получить состояние');
        }

        const paths = $('pathsInfo');
        clearChildren(paths);
        const entries = [
            ['Базовая папка сервера', serverData.paths.baseDirectory],
            ['Рабочий каталог', serverData.paths.workingDirectory],
            ['Исполняемый файл', serverData.paths.executable],
            ['Профили мода', serverData.paths.profiles],
            ['Резервные копии сервера', serverData.paths.backups],
            ['Каталог данных панели', serverData.paths.dataDirectory],
            ['Файл настроек', serverData.paths.settingsFile]
        ];

        for (const [key, value] of entries) {
            const dt = document.createElement('dt');
            dt.textContent = key;
            const dd = document.createElement('dd');
            dd.className = 'mono';
            dd.textContent = value ?? '—';
            paths.appendChild(dt);
            paths.appendChild(dd);
        }
    } catch (error) {
        toast('Не удалось загрузить настройки', error.message, 'error');
    }
}

function collectServerSettings() {
    const base = structuredClone(loadedSettings.server.server);
    base.baseDirectory = $('setBaseDir').value.trim();
    base.workingDirectory = $('setWorkDir').value.trim();
    base.executableName = $('setExecutableName').value.trim();
    base.executablePath = $('setExe').value.trim();
    base.configFile = $('setConfig').value.trim();
    base.profilesDirectory = $('setProfiles').value.trim();
    base.stopCommand = $('setStopCommand').value;
    base.gracefulStopSeconds = Number($('setGraceful').value);
    base.autoRestartDelaySeconds = Number($('setRestartDelay').value);
    base.maxAutoRestarts = Number($('setMaxRestarts').value);
    base.autoRestartWindowMinutes = Number($('setRestartWindow').value);
    base.startOnLaunch = $('setStartOnLaunch').checked;
    base.autoRestartOnCrash = $('setAutoRestart').checked;
    base.redirectStandardInput = $('setStdIn').checked;
    base.useStopCommand = $('setUseStopCommand').checked;
    base.clearConsoleOnStart = $('setClearConsole').checked;
    base.allowDirectoryOutsideBase = $('setAllowOutside').checked;
    base.consoleEncoding = $('setEncoding').value;
    base.processPriority = $('setPriority').value;

    base.environmentVariables = {};
    for (const line of $('setEnvVars').value.split('\n')) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith('#')) continue;
        const index = trimmed.indexOf('=');
        if (index <= 0) continue;
        base.environmentVariables[trimmed.slice(0, index).trim()] = trimmed.slice(index + 1).trim();
    }

    return base;
}

function collectConfigFiles() {
    const result = [];
    for (const line of $('setConfigFiles').value.split('\n')) {
        const trimmed = line.trim();
        if (!trimmed) continue;
        const parts = trimmed.split('|').map((p) => p.trim());
        if (parts.length < 2 || !parts[0] || !parts[1]) continue;
        result.push({ name: parts[0], path: parts[1], description: parts[2] ?? '', editable: true, readOnly: false });
    }
    return result;
}

async function saveAllSettings() {
    if (!loadedSettings) return;

    const serverData = loadedSettings.server;
    const global = loadedSettings.global;

    const web = structuredClone(global.web);
    web.port = Number($('setWebPort').value);
    web.bindAddress = $('setBind').value.trim();
    web.allowRemote = $('setAllowRemote').checked;
    web.openBrowserOnStart = $('setOpenBrowser').checked;

    const auth = structuredClone(global.auth);
    auth.sessionHours = Number($('setSessionHours').value);

    const logging = structuredClone(global.logging);
    logging.maxConsoleLines = Number($('setConsoleLines').value);
    logging.minimumLevel = $('setLogLevel').value;
    logging.logRetentionDays = Number($('setLogRetention').value);
    logging.writeServerLogFile = $('setWriteServerLog').checked;

    const backups = structuredClone(serverData.backups);
    backups.enabled = $('setBackupEnabled').checked;
    backups.retentionCount = Number($('setRetention').value);
    backups.compressionLevel = $('setCompression').value;
    backups.stopServerDuringBackup = $('setStopForBackup').checked;
    backups.includes = $('setIncludes').value.split('\n').map((v) => v.trim()).filter(Boolean);
    backups.excludePatterns = $('setExcludes').value.split('\n').map((v) => v.trim()).filter(Boolean);

    // Настройки конкретного сервера.
    await api(sp('/settings'), {
        method: 'PUT',
        body: {
            profile: {
                name: $('setServerName').value.trim() || undefined,
                enabled: true,
                color: $('setServerColor').value
            },
            server: collectServerSettings(),
            backups,
            configFiles: collectConfigFiles()
        }
    });

    // Общие настройки панели.
    const result = await api('/settings', {
        method: 'PUT',
        body: { web, auth, logging, defaultServerId: currentServerId }
    });

    // Каталог SteamCMD сохраняется отдельно: он общий для панели.
    try {
        await api('/steam/settings', {
            method: 'POST',
            body: { directory: ($('setSteamDir')?.value ?? '').trim() }
        });
    } catch (error) {
        toast('Каталог SteamCMD не сохранён', error.message, 'warn');
    }

    try {
        await api('/system/autostart', { method: 'POST', body: { enabled: $('setAutostart').checked } });
    } catch (error) {
        toast('Автозапуск не изменён', error.message, 'warn');
    }

    await loadServers();

    toast('Настройки сохранены', result?.restartRequired
        ? 'Изменения веб-сервера применятся после перезапуска панели.' : '', 'info', 7000);

    loadSettings();
}

$('btnSaveSettings').addEventListener('click', async () => {
    try { await saveAllSettings(); }
    catch (error) { toast('Не удалось сохранить', error.message, 'error', 8000); }
});

$('btnReloadSettings').addEventListener('click', loadSettings);

/* -------------------------------------- модалка настроек сервера */

// Карточка «Настройки» на дашборде открывает модалку со всеми полями сервера.
$('dashSettingsOpen')?.addEventListener('click', openServerSettingsModal);
$('srvSetX')?.addEventListener('click', () => $('serverSettingsModal')?.classList.remove('open'));
$('srvSetCancel')?.addEventListener('click', () => $('serverSettingsModal')?.classList.remove('open'));
$('serverSettingsModal')?.addEventListener('click', (event) => {
    if (event.target.id === 'serverSettingsModal') $('serverSettingsModal').classList.remove('open');
});

$('srvSetSave')?.addEventListener('click', async () => {
    const button = $('srvSetSave');
    button.disabled = true;
    button.textContent = 'Сохранение…';
    try {
        await saveAllSettings();
        $('serverSettingsModal')?.classList.remove('open');
    } catch (error) {
        toast('Не удалось сохранить', error.message, 'error', 8000);
    } finally {
        button.disabled = false;
        button.textContent = 'Сохранить';
    }
});

/** Открывает модалку настроек сервера, подтянув актуальные значения. */
async function openServerSettingsModal() {
    const modal = $('serverSettingsModal');
    if (!modal) return;

    setText('srvSetSub', currentServer()?.name ?? '');
    await loadSettings();
    modal.classList.add('open');
}

// Кнопки «Обзор…» в настройках сервера.
$('btnBrowseBaseDir')?.addEventListener('click', () => FolderPicker.open('setBaseDir'));
$('btnBrowseWorkDir')?.addEventListener('click', () => FolderPicker.open('setWorkDir'));
$('btnBrowseExe')?.addEventListener('click', () => FolderPicker.open('setExe'));
$('btnBrowseSteamDir')?.addEventListener('click', () => FolderPicker.open('setSteamDir'));

// Установка SteamCMD в папку панели.
$('btnSteamInstall')?.addEventListener('click', async () => {
    try {
        toast('SteamCMD', 'Загрузка началась. Следите за состоянием.', 'info', 6000);
        const result = await api('/setup/steam/install', { method: 'POST', body: {} });
        toast('SteamCMD', result.message ?? '', 'info', 8000);
    } catch (error) {
        toast('SteamCMD не установлен', error.message, 'error', 9000);
    } finally {
        loadSettings();
    }
});

// Загрузка сервера DayZ в папку текущего сервера (нужен вход в Steam).
$('btnSteamServerInstall')?.addEventListener('click', async () => {
    const item = currentServer();
    if (!item) {
        toast('Сервер не выбран', 'Выберите сервер в списке слева.', 'warn');
        return;
    }

    try {
        const result = await api('/steam/server/install', { method: 'POST', body: { serverId: item.id } });
        toast('Сервер DayZ', result.message ?? 'Загрузка началась.', 'info', 8000);
    } catch (error) {
        toast('Сервер не скачан', error.message, 'error', 9000);
    }
});

// Сохранение каталога SteamCMD и выход из Steam.
$('btnSteamLogout')?.addEventListener('click', () => {
    askConfirm('Выйти из Steam? Сессия будет удалена, при следующем входе потребуется код Steam Guard.',
        async () => {
            try {
                const result = await api('/steam/logout', { method: 'POST' });
                toast('Выход выполнен', result.message ?? '', 'info', 8000);
                loadSettings();
            } catch (error) {
                toast('Не удалось выйти', error.message, 'error');
            }
        }, 'Выйти');
});

$('btnDeleteServer')?.addEventListener('click', () => {
    const item = currentServer();
    if (!item) return;
    confirmRemoveServer(item);
});

/* ============================================================ новые серверы */

/* кнопки добавления сервера привязываются в runInit после загрузки всех скриптов */

/* ============================================================ безопасность */

async function loadSecurity() {
    try {
        const data = await api('/settings');
        const serverData = await api(sp('/settings'));
        const auth = data.auth;

        setText('secOwner', auth.ownerName ?? auth.username ?? '—');
        setText('secCreated', auth.ownerCreatedAt ? formatTime(auth.ownerCreatedAt) : '—');

        const tag = $('secAuthTag');
        tag.textContent = auth.enabled ? 'включена' : 'отключена';
        tag.className = 'tag ' + (auth.enabled ? 'on' : 'off');

        setText('secBind', data.web.allowRemote ? '0.0.0.0 (сеть)' : data.web.bindAddress);
        setText('secPaths', serverData.server.allowDirectoryOutsideBase ? 'без ограничения папки' : 'внутри базовой папки');

        const user = await api('/auth/status').catch(() => null);
        if (user?.owner || user?.user) {
            const label = user.owner ?? user.user;
            setText('userLabel', label);
            setText('userAvatar', label.slice(0, 2));
        }
    } catch (error) {
        toast('Не удалось загрузить данные безопасности', error.message, 'error');
    }
}

async function loadAudit() {
    try {
        const data = await api('/audit?lines=200');
        const log = $('auditLog');
        clearChildren(log);
        for (const line of data.lines ?? []) {
            const row = document.createElement('span');
            row.className = 'l';
            row.textContent = line;
            log.appendChild(row);
        }
        log.scrollTop = log.scrollHeight;
    } catch (error) {
        toast('Не удалось прочитать журнал аудита', error.message, 'error');
    }
}

$('btnReloadAudit').addEventListener('click', loadAudit);

$('btnChangePassword').addEventListener('click', async () => {
    const current = $('curPassword').value;
    const next = $('newPassword').value;
    const confirm = $('newPassword2').value;

    if (next.length < 8) { toast('Пароль слишком короткий', 'Минимум 8 символов.', 'error'); return; }
    if (next !== confirm) { toast('Пароли не совпадают', '', 'error'); return; }

    try {
        await api('/auth/password', { method: 'POST', body: { currentPassword: current, newPassword: next } });
        $('curPassword').value = '';
        $('newPassword').value = '';
        $('newPassword2').value = '';
        toast('Пароль изменён', 'Используйте новый пароль при следующем входе.', 'info', 7000);
    } catch (error) {
        toast('Пароль не изменён', error.message, 'error');
    }
});

/* ============================================================ хост */

async function loadHost() {
    try {
        const data = await api('/system/info');
        const host = data.host ?? {};

        // Общая память нужна карточке «Ресурсы» на дашборде.
        hostTotalMemoryBytes = Number(host.totalMemoryBytes) || 0;
    } catch {
        // Не критично.
    }
}

/* ============================================================ модалка подтверждения */

$('confirmOk').addEventListener('click', async () => {
    const action = $('confirmModal')._action;
    $('confirmModal')._action = null;
    $('confirmModal').classList.remove('open');
    if (!action) return;
    try { await action(); }
    catch (error) { toast('Ошибка', error.message, 'error'); }
});

$('confirmAlt').addEventListener('click', async () => {
    const action = $('confirmAlt')._action;
    $('confirmModal')._action = null;
    $('confirmAlt')._action = null;
    $('confirmModal').classList.remove('open');
    if (!action) return;
    try { await action(); }
    catch (error) { toast('Ошибка', error.message, 'error'); }
});

$('confirmCancel').addEventListener('click', () => {
    $('confirmModal')._action = null;
    $('confirmModal').classList.remove('open');
});
$('confirmX').addEventListener('click', () => {
    $('confirmModal')._action = null;
    $('confirmModal').classList.remove('open');
});
$('confirmModal').addEventListener('click', (event) => {
    if (event.target.id === 'confirmModal') {
        $('confirmModal')._action = null;
        $('confirmModal').classList.remove('open');
    }
});

/* ============================================================ модалка задачи */

$('jobModal').addEventListener('click', (event) => {
    if (event.target.id === 'jobModal') $('jobModal').classList.remove('open');
});

/* ============================================================ запуск */

/* Публичный интерфейс для других модулей панели. */
const App = {
    api,
    toast,
    showView,
    formatBytes,
    formatDuration,
    formatTime,
    setText,
    setPlayerCount,
    stateClass,
    $,
    refreshStatus,
    loadServers,
    selectServer,
    loadConfigs,
    loadBackups,
    loadSchedule,
    loadSettings,
    loadService,
    get currentServerId() { return currentServerId; },
    get currentServer() { return currentServer(); },
    get servers() { return servers; },
    get status() { return lastStatus; }
};

window.App = App;

// Инициализация запускается после загрузки всех скриптов страницы:
// app.js подключён раньше players.js, поэтому нужен именно load.
if (document.readyState === 'complete') {
    init();
} else {
    window.addEventListener('load', init, { once: true });
}

/** Точка входа. Вызывается после загрузки всех скриптов страницы. */
async function init() {
    window.__dzpInitStarted = true;
    try {
        await runInit();
        window.__dzpReady = true;
    } catch (error) {
        window.__dzpError = String(error) + ' :: ' + (error?.stack ?? '');
        console.error('DayZPulse: ошибка инициализации', error);
    }
}

async function runInit() {
    try { applySidebar(localStorage.getItem(SIDEBAR_KEY) === '1'); } catch { applySidebar(false); }

    loadBlueprint();

    try {
        const status = await fetch('/api/auth/status', { credentials: 'same-origin' }).then((r) => r.json());
        if (status.setupRequired) { window.location.replace('/setup.html'); return; }

        // Права текущего пользователя: разделы и серверы.
        sessionIsOwner = status.isOwner !== false;
        sessionSections = status.canUseAllSections ? null : (status.sections ?? []);
        sessionServers = status.canUseAllServers ? null : (status.servers ?? []);

        if (status.owner) {
            setText('userLabel', status.owner);
            setText('userAvatar', status.owner.slice(0, 2));
        } else if (status.user) {
            setText('userLabel', status.user);
            setText('userAvatar', status.user.slice(0, 2));
        }

        // Папка, выбранная при создании владельца: подставляется в новый сервер.
        if (status.defaultServerDirectory) {
            defaultServerDirectory = status.defaultServerDirectory;
        }
    } catch { /* продолжим */ }

    applySectionPermissions();

    // Раздел «Игроки» подключается отдельным файлом; привязка возможна
    // только когда все скрипты страницы загружены.
    if (typeof Players !== 'undefined') Players.bind();
    if (typeof Admins !== 'undefined') Admins.bind();
    if (typeof Mods !== 'undefined') Mods.bind();
    if (typeof Logs !== 'undefined') Logs.bind();
    if (typeof Loadouts !== 'undefined') Loadouts.bind();

    // Steam: вход в SteamCMD показывается один раз, после создания владельца.
    if (typeof SteamUI !== 'undefined') {
        SteamUI.bind(false);
        // После успешного входа сразу предлагаем добавить сервер.
        SteamUI.setLoginCompleteHandler(() => {
            setTimeout(() => openServerCreate(), 2200);
        });
    }

    $('btnNewServer')?.addEventListener('click', openServerCreate);
    $('btnWelcomeAdd')?.addEventListener('click', openServerCreate);

    // Пилюля выбранного сервера ведёт к списку серверов.
    $('srvPill')?.addEventListener('click', () => showView('servers'));

    initBlueprintPanZoom();

    await loadServers();

    // Проверку входа делаем после загрузки серверов: нужен выбранный сервер,
    // чтобы не показывать окно, когда сервер уже установлен.
    if (typeof SteamUI !== 'undefined') SteamUI.checkLogin();

    const hashParts = location.hash.replace('#', '').split('?');
    const hash = hashParts[0];
    showView(VIEWS.includes(hash) ? hash : 'servers');

    await refreshStatus();
    await loadHost();
    connectConsole();
    connectLive();

    // Первый рендер нод: провода считаются после раскладки.
    requestAnimationFrame(() => {
        drawWires();
        makeDraggable();
        applyBlueprintTransform();
    });

    // Изменения приходят по каналу событий, поэтому частый опрос не нужен.
    setInterval(loadHost, 30000);
    setInterval(() => { if (currentView === 'service') loadService(); }, 10000);
    setInterval(() => {
        if (currentView === 'chat') Players.loadChat();
        if (currentView === 'anticheat') Players.loadViolations();
        if (currentView === 'anticheat') Players.loadEvents();
        if (currentView === 'logs') Players.loadCommands();
    }, 5000);

    // Страховка на случай разрыва канала: обновляемся вручную.
    setInterval(() => {
        if (!liveSocket || liveSocket.readyState !== WebSocket.OPEN) {
            refreshStatus();
            if (currentView === 'servers') loadServers();
        }
    }, 3000);
}
