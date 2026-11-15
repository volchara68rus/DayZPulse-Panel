'use strict';

/* ============================================================
   DayZPulse — вход и создание владельца
   Никаких inline-скриптов: всё в этом файле, внешних ресурсов нет.
   ============================================================ */

const CSRF_COOKIE = 'dayzpulse_csrf';
const CSRF_HEADER = 'X-CSRF-Token';

// Страница создания владельца тоже подписывается на прогресс SteamCMD.
if (document.getElementById('steamBlock')) {
    (function connectSetupLive() {
        const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        let socket;

        const open = () => {
            socket = new WebSocket(`${protocol}//${location.host}/ws/events`);

            socket.onmessage = (event) => {
                let message;
                try { message = JSON.parse(event.data); } catch { return; }

                if (message.type === 'steam') {
                    SteamUI.applyPush(message.data);
                }
            };

            socket.onclose = () => setTimeout(open, 2500);
            socket.onerror = () => socket?.close();
        };

        open();
    })();
}

function $(id) { return document.getElementById(id); }

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

function csrfToken() {
    let token = readCookie(CSRF_COOKIE);
    if (!token) {
        token = (crypto.randomUUID?.() ?? String(Date.now())).replace(/-/g, '');
    }
    return token;
}

async function api(path, options = {}) {
    const opts = {
        method: options.method ?? 'GET',
        credentials: 'same-origin',
        headers: { 'Accept': 'application/json' }
    };

    if (options.body !== undefined) {
        opts.headers['Content-Type'] = 'application/json';
        opts.headers[CSRF_HEADER] = csrfToken();
        opts.body = JSON.stringify(options.body);
    }

    const response = await fetch(path, opts);
    const text = await response.text();
    let data = null;
    if (text) {
        try { data = JSON.parse(text); } catch { data = { error: text }; }
    }

    return { ok: response.ok, status: response.status, data: data ?? {} };
}

function showAlert(element, message, kind) {
    element.textContent = message;
    element.className = 'auth-alert' + (kind ? ' ' + kind : '');
}

function hideAlert(element) {
    element.className = 'auth-alert hidden';
    element.textContent = '';
}

/** Состояние SteamCMD и сервера DayZ для выбранной папки. */
async function callSetupSteam(directory) {
    const result = await api('/api/setup/steam?directory=' + encodeURIComponent(directory));
    return result.ok ? result.data : null;
}

/* ============================================================
   Проверка пароля на клиенте (дублирует серверные правила)
   ============================================================ */
const COMMON = ['password', 'passw0rd', '123456', '12345678', '123456789', 'qwerty',
    'qwerty123', 'admin', 'admin123', 'administrator', 'letmein', 'welcome', 'dayz',
    'dayzserver', 'root', 'toor', 'test1234', 'iloveyou', 'monkey', 'dragon', 'master', 'shadow'];

function evaluatePassword(password, confirm, username) {
    const lower = password.toLowerCase();
    const checks = {
        length: password.length >= 8 && password.length <= 200,
        lower: /[a-zа-я]/.test(password),
        upper: /[A-ZА-Я]/.test(password),
        digit: /[0-9]/.test(password),
        symbol: /[^A-Za-zА-Яа-я0-9]/.test(password),
        nodictionary: password.length > 0 &&
            !COMMON.some((w) => lower === w) &&
            !(username && username.length >= 3 && lower.includes(username.toLowerCase())),
        match: password.length > 0 && password === confirm
    };

    const classes = [checks.lower, checks.upper, checks.digit, checks.symbol].filter(Boolean).length;
    checks.classes = classes >= 2;

    let score = 0;
    if (password.length >= 8) score++;
    if (password.length >= 12) score++;
    if (classes >= 3) score++;
    if (checks.nodictionary && password.length >= 10) score++;

    const valid = checks.length && checks.classes && checks.nodictionary && checks.match;
    return { checks, score: Math.min(4, score), valid };
}

function bindPasswordValidation(usernameEl, passwordEl, confirmEl) {
    const strength = $('strength');
    const label = $('strengthLabel');
    const list = $('reqList');
    if (!passwordEl || !list) return () => ({ valid: false });

    const update = () => {
        const result = evaluatePassword(passwordEl.value, confirmEl?.value ?? passwordEl.value,
            usernameEl?.value.trim() ?? '');

        for (const li of list.querySelectorAll('li')) {
            const key = li.dataset.req;
            // «classes» — сводная проверка групп символов.
            const ok = key === 'classes' ? result.checks.classes : result.checks[key];
            li.classList.toggle('ok', !!ok);
        }

        if (strength) strength.className = 'strength s' + (passwordEl.value ? Math.max(1, result.score) : 0);
        if (label) {
            const texts = ['Минимум 8 символов, две группы символов',
                'Слабый пароль', 'Средний пароль', 'Хороший пароль', 'Надёжный пароль'];
            label.textContent = passwordEl.value ? texts[Math.max(1, result.score)] : texts[0];
        }

        return result;
    };

    passwordEl.addEventListener('input', update);
    confirmEl?.addEventListener('input', update);
    usernameEl?.addEventListener('input', update);
    update();
    return update;
}

/* ============================================================
   Страница входа
   ============================================================ */
function initLogin() {
    const form = $('loginForm');
    if (!form) return;

    const errorBox = $('loginError');
    const button = $('loginBtn');
    const buttonText = $('loginBtnText');
    const foot = $('authFoot');

    (async function check() {
        const result = await api('/api/auth/status');
        const status = result.data ?? {};

        if (status.setupRequired) {
            window.location.replace('/setup.html');
            return;
        }

        if (status.authenticated) {
            window.location.replace('/');
            return;
        }

        if (status.user) $('username').value = status.user;
        if (status.productName) document.title = `DayZPulse — вход`;

        applyAccessNotice(status);

        if (!status.authEnabled) {
            foot.textContent = 'Авторизация отключена в настройках — панель доступна без входа.';
        }
    })();

    /** Показывает, откуда открыта панель и нужен ли защищённый туннель. */
    function applyAccessNotice(status) {
        if (!foot) return;

        switch (status.access) {
            case 'external':
                // Доступ извне: без туннеля логин и пароль передаются открыто.
                foot.className = 'auth-alert';
                foot.innerHTML = 'Доступ извне сети. Вход и данные передаются без шифрования — ' +
                    'используйте защищённый туннель (HTTPS) или VPN.';
                return;

            case 'privatenetwork':
                foot.className = 'auth-foot';
                foot.textContent = 'Доступ из локальной сети. Внутри своей сети панель защищена ' +
                    'сессией и CSRF; для доступа извне используйте защищённый туннель.';
                return;

            default:
                foot.className = 'auth-foot';
                foot.textContent = 'Панель работает локально на этой машине. ' +
                    'При доступе извне используйте защищённый туннель (HTTPS) или VPN.';
        }
    }

    form.addEventListener('submit', async (event) => {
        event.preventDefault();
        hideAlert(errorBox);

        const username = $('username').value.trim();
        const password = $('password').value;

        if (username.length < 1 || password.length < 1) {
            showAlert(errorBox, 'Заполните имя владельца и пароль.');
            return;
        }

        button.disabled = true;
        buttonText.textContent = 'Проверка…';

        try {
            const result = await api('/api/auth/login', { method: 'POST', body: { username, password } });

            if (result.ok) {
                window.location.replace('/');
                return;
            }

            if (result.data.code === 'setup_required') {
                window.location.replace('/setup.html');
                return;
            }

            showAlert(errorBox, result.data.error ?? 'Не удалось войти.');
            $('password').value = '';
            $('password').focus();
        } catch (error) {
            showAlert(errorBox, 'Ошибка соединения: ' + error.message);
        } finally {
            button.disabled = false;
            buttonText.textContent = 'Войти';
        }
    });

    $('username')?.focus();
}

/* ============================================================
   Страница создания владельца
   ============================================================ */
function initSetup() {
    const form = $('setupForm');
    if (!form) return;

    const errorBox = $('setupError');
    const button = $('setupBtn');
    const buttonText = $('setupBtnText');
    const usernameEl = $('username');
    const passwordEl = $('password');
    const confirmEl = $('passwordConfirm');
    const directoryEl = $('serverDirectory');
    const portEl = $('webPort');
    const portHint = $('webPortHint');
    const portCheck = $('webPortCheck');

    const validate = bindPasswordValidation(usernameEl, passwordEl, confirmEl);

    /** Показывает подсказку о порте, окрашивая её и поле по результату проверки. */
    function describePort(available, suggested) {
        portEl.classList.toggle('port-ok', available);
        portEl.classList.toggle('port-bad', !available && portEl.value.length > 0);

        if (available) {
            portHint.textContent = `Порт свободен. Панель откроется по адресу http://127.0.0.1:${portEl.value}.`;
            portHint.style.color = 'var(--acc)';
            return;
        }

        portHint.textContent = suggested
            ? `Порт ${portEl.value} занят. Свободен, например, ${suggested}.`
            : `Порт ${portEl.value} занят. Укажите другой.`;
        portHint.style.color = 'var(--red)';
    }

    async function checkPort() {
        const port = Number(portEl.value);
        if (!Number.isFinite(port) || port < 1 || port > 65535) {
            portHint.textContent = 'Порт должен быть в диапазоне 1–65535.';
            portHint.style.color = 'var(--red)';
            portEl.classList.remove('port-ok');
            portEl.classList.add('port-bad');
            return false;
        }

        try {
            const result = await api('/api/setup/port?port=' + port);
            describePort(result.data.available, result.data.suggested);
            return result.data.available;
        } catch (error) {
            portHint.textContent = 'Не удалось проверить порт: ' + error.message;
            portHint.style.color = 'var(--red)';
            return false;
        }
    }

    (async function check() {
        const result = await api('/api/setup/status');
        const status = result.data ?? {};

        if (!status.setupRequired) {
            window.location.replace('/login.html');
            return;
        }

        if (status.defaultServerDirectory) {
            directoryEl.value = status.defaultServerDirectory;
        }

        if (status.webPort) {
            portEl.value = status.webPort;
        }

        describePort(status.portAvailable !== false, status.suggestedPort);
    })();

    portCheck?.addEventListener('click', async () => {
        portCheck.disabled = true;
        portCheck.textContent = 'Проверка…';
        await checkPort();
        portCheck.disabled = false;
        portCheck.textContent = 'Проверить';
    });

    // Выбор папки системным диалогом Windows.
    const browse = $('serverDirectoryBrowse');
    browse?.addEventListener('click', () => {
        FolderPicker.open('serverDirectory');
    });

    portEl?.addEventListener('input', () => {
        // До проверки поле нейтральное: результат неизвестен.
        portEl.classList.remove('port-ok', 'port-bad');
        portHint.textContent = `Панель откроется по адресу http://127.0.0.1:${portEl.value || '…'}.`;
        portHint.style.color = '';
    });

    form.addEventListener('submit', async (event) => {
        event.preventDefault();
        hideAlert(errorBox);

        const username = usernameEl.value.trim();
        const password = passwordEl.value;
        const confirm = confirmEl.value;
        const directory = directoryEl.value.trim();
        const port = Number(portEl.value);

        if (!/^[A-Za-z0-9._-]{3,32}$/.test(username)) {
            showAlert(errorBox, 'Имя владельца: 3–32 символа, латиница, цифры, «_», «-» и «.»');
            usernameEl.focus();
            return;
        }

        const evaluation = validate();
        if (!evaluation.valid) {
            showAlert(errorBox, 'Пароль не соответствует требованиям.');
            passwordEl.focus();
            return;
        }

        if (directory.length < 3) {
            showAlert(errorBox, 'Укажите папку для сервера DayZ, например C:\\DayZServer');
            directoryEl.focus();
            return;
        }

        // Папка должна существовать: сервер скачается в неё позже, после входа в Steam.
        const steamStatus = await callSetupSteam(directory);
        if (steamStatus && !steamStatus.directoryExists) {
            showAlert(errorBox, 'Папка не найдена. Выберите существующую папку или создайте её.');
            directoryEl.focus();
            return;
        }

        // Пока SteamCMD не готов, владельца создавать нельзя: он нужен панели.
        if (steamStatus && !steamStatus.steamReady) {
            showAlert(errorBox, 'Дождитесь окончания установки SteamCMD.');
            return;
        }

        if (!Number.isFinite(port) || port < 1 || port > 65535) {
            showAlert(errorBox, 'Порт панели должен быть в диапазоне 1–65535.');
            portEl.focus();
            return;
        }

        button.disabled = true;
        buttonText.textContent = 'Создание…';

        try {
            const result = await api('/api/setup', {
                method: 'POST',
                body: {
                    username,
                    password,
                    passwordConfirm: confirm,
                    serverDirectory: directory,
                    webPort: port
                }
            });

            if (result.ok) {
                const target = `http://127.0.0.1:${result.data.webPort ?? port}`;

                if (result.data.restartRequired) {
                    // Порт сменился: панель перезапустится на новом адресе.
                    showAlert(errorBox,
                        `Владелец создан. Панель перезапускается на порту ${result.data.webPort ?? port}…`, 'ok');
                    buttonText.textContent = 'Перезапуск…';
                    await waitForPortThenOpen(target);
                    return;
                }

                showAlert(errorBox, 'Владелец создан. Открываем панель управления.', 'ok');
                window.location.replace('/');
                return;
            }

            showAlert(errorBox, result.data.error ?? 'Не удалось создать владельца.');
        } catch (error) {
            showAlert(errorBox, 'Ошибка соединения: ' + error.message);
        } finally {
            button.disabled = false;
            buttonText.textContent = 'Создать владельца и войти';
            // Steam мог ещё не скачаться — возвращаем блокировку.
            if (typeof SteamUI !== 'undefined') SteamUI.applySetupState();
        }
    });

    /** Ждёт ответа панели на новом порту и открывает её. */
    async function waitForPortThenOpen(target) {
        // Служба Windows перезапускается не мгновенно: ждём до 3 минут, иначе
        // пользователь видит «панель не ответила» и решает, что всё сломалось.
        for (let attempt = 0; attempt < 180; attempt++) {
            await new Promise((resolve) => setTimeout(resolve, 1000));

            try {
                const response = await fetch(target + '/api/auth/status', {
                    credentials: 'include',
                    cache: 'no-store'
                });
                if (response.ok) {
                    window.location.replace(target + '/');
                    return;
                }
            } catch {
                // Панель ещё не поднялась — ждём дальше.
            }
        }

        showAlert(errorBox,
            `Панель не ответила на ${target}. Откройте адрес вручную.`, 'ok');
    }

    usernameEl?.focus();
}

initLogin();
initSetup();
