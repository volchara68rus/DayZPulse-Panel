'use strict';

/* ============================================================
   DayZPulse — SteamCMD
   1. На странице создания владельца: папка сервера задаёт каталог
      SteamCMD, загрузка начинается сразу после её выбора.
   2. В панели: модальное окно входа в Steam со Steam Guard.
   ============================================================ */

const SteamUI = (() => {
    const CSRF_COOKIE = 'dayzpulse_csrf';
    const CSRF_HEADER = 'X-CSRF-Token';

    let pollTimer = null;
    let loginPollTimer = null;
    let lastDirectory = null;
    let lastState = '';

    /** Вызывается после успешного входа в Steam (панель открывает добавление сервера). */
    let onLoginComplete = null;

    function setLoginCompleteHandler(handler) {
        onLoginComplete = handler;
    }

    function $(id) { return document.getElementById(id); }

    function csrf() {
        const prefix = CSRF_COOKIE + '=';
        for (const part of document.cookie.split(';')) {
            const value = part.trim();
            if (value.startsWith(prefix)) return decodeURIComponent(value.slice(prefix.length));
        }
        return '';
    }

    async function call(path, options = {}) {
        const method = options.method ?? 'GET';
        const opts = { method, credentials: 'same-origin', headers: { Accept: 'application/json' } };

        if (options.body !== undefined) {
            opts.headers['Content-Type'] = 'application/json';
            opts.headers[CSRF_HEADER] = csrf();
            opts.body = JSON.stringify(options.body);
        }

        const response = await fetch(path, opts);
        const text = await response.text();

        let data = null;
        if (text) { try { data = JSON.parse(text); } catch { data = { error: text }; } }

        if (!response.ok) {
            const error = new Error(data?.error ?? `Ошибка ${response.status}`);
            error.data = data;
            throw error;
        }

        return data ?? {};
    }

    /* ------------------------------------------------ шаг «SteamCMD» при setup */

    /** Блокирует создание владельца, пока SteamCMD не готов. */
    let setupWanted = null;

    function setSetupEnabled(enabled, reason) {
        setupWanted = { enabled, reason };
        applySetupState();
    }

    /** Повторно применяет состояние кнопки (после сброса со стороны auth.js). */
    function applySetupState() {
        const button = $('setupBtn');
        const text = $('setupBtnText');
        if (!button || !setupWanted) return;

        button.disabled = !setupWanted.enabled;
        if (text) {
            text.textContent = setupWanted.enabled
                ? 'Создать владельца и войти'
                : (setupWanted.reason ?? 'Ожидание загрузки…');
        }
    }

    /* Порядок шагов установки для индикатора. */
    const PHASES = ['download', 'unpack', 'warmup', 'server', 'done'];

    /** Определяет текущий шаг по тексту фазы от сервера. */
    function phaseStep(phase) {
        const text = (phase ?? '').toLowerCase();

        if (text.includes('готов')) return 'done';
        if (text.includes('сервер')) return 'server';
        if (text.includes('настрой') || text.includes('обновлен')) return 'warmup';
        if (text.includes('распаков') || text.includes('скачан')) return 'unpack';
        if (text.includes('скач') || text.includes('подготов')) return 'download';
        return 'download';
    }

    function setProgress(percent, phase) {
        const bar = $('steamBar');
        const fill = $('steamBarFill');
        const label = $('steamBarLabel');
        const value = $('steamBarValue');
        if (!bar || !fill || !label || !value) return;

        bar.classList.remove('hidden');

        const clamped = Math.max(0, Math.min(100, Math.round(percent)));
        fill.style.width = clamped + '%';
        label.textContent = phase || 'подготовка';
        value.textContent = clamped + '%';

        // Подсвечиваем пройденные шаги.
        const current = PHASES.indexOf(phaseStep(phase));
        bar.querySelectorAll('.node-progress-steps span').forEach((step) => {
            const index = PHASES.indexOf(step.dataset.step);
            step.classList.toggle('active', index === current);
            step.classList.toggle('done', index >= 0 && index < current);
        });
    }

    function hideProgress() {
        $('steamBar')?.classList.add('hidden');
    }

    async function initSetupBlock() {
        const block = $('steamBlock');
        if (!block) return;

        // Папки нет — ничего не ждём, работаем как обычно.
        setSetupEnabled(true);
        setHint('папка не выбрана');
        lastDirectory = currentDirectory();

        // Папка задаёт каталог сервера DayZ: как только она выбрана, проверяем её.
        const input = $('serverDirectory');
        input?.addEventListener('input', () => scheduleDirectoryCheck());
        input?.addEventListener('change', () => scheduleDirectoryCheck());

        // «Обзор…» может выбрать папку без события input — проверяем ещё раз.
        $('serverDirectoryBrowse')?.addEventListener('click', () => {
            const timer = setInterval(() => {
                if (currentDirectory() !== lastDirectory) {
                    clearInterval(timer);
                    scheduleDirectoryCheck(true);
                }
            }, 400);

            // Опрос прекращаем, когда окно выбора закроется.
            setTimeout(() => clearInterval(timer), 60000);
        });
    }

    function currentDirectory() {
        return ($('serverDirectory')?.value ?? '').trim();
    }

    let directoryTimer = null;

    /** Реагирует на выбор папки: проверяет сервер и запускает установку SteamCMD. */
    function scheduleDirectoryCheck(force = false) {
        clearTimeout(directoryTimer);
        directoryTimer = setTimeout(async () => {
            const directory = currentDirectory();

            if (directory === lastDirectory && !force) {
                return;
            }

            lastDirectory = directory;

            if (directory.length < 3) {
                setHint('папка не выбрана');
                hideProgress();
                setSetupEnabled(true);
                return;
            }

            const status = await call('/api/setup/steam?directory=' + encodeURIComponent(directory)).catch(() => null);
            if (!status) return;

            // Папка не существует — это ошибка ввода, ждать нечего.
            if (!status.directoryExists) {
                setHint('папка не найдена');
                hideProgress();
                setSetupEnabled(false, 'Папка не найдена');
                return;
            }

            // SteamCMD готов — владельца можно создавать. Сервер DayZ
            // скачается позже, из панели, после входа в Steam.
            if (status.steamReady) {
                setHint(status.serverExists ? 'готово: сервер найден' : 'готово: SteamCMD установлен');
                document.querySelector('.steam-icon')?.classList.add('ok');
                hideProgress();
                setSetupEnabled(true);
                return;
            }

            // Уже идёт загрузка — просто подключаемся к прогрессу.
            if (status.running) {
                setProgress(status.progress ?? 0, status.phase);
                setSetupEnabled(false, 'Ожидание SteamCMD…');
                startPolling();
                return;
            }

            // SteamCMD ещё нет — качаем его в папку панели.
            await install(directory);
        }, 500);
    }

    /** Применяет событие Steam, присланное сервером по live-каналу. */
    function applyPush(data) {
        if (!data) return;

        const hint = $('steamHint');
        const block = $('steamBlock');
        if (!block) return;

        // На странице setup интересует только установка SteamCMD.
        if (data.stage === 'server') return;

        if (data.error) {
            if (hint) hint.textContent = 'ошибка: ' + data.error;
            hideProgress();
            setSetupEnabled(false, 'Ошибка загрузки');
            return;
        }

        if (data.running) {
            if (hint) hint.textContent = data.phase || 'подготовка…';
            setProgress(data.progress ?? 0, data.phase);
            setSetupEnabled(false, 'Ожидание SteamCMD…');
            return;
        }

        if (data.warm) {
            if (hint) hint.textContent = 'готово: SteamCMD установлен';
            document.querySelector('.steam-icon')?.classList.add('ok');
            hideProgress();
            setSetupEnabled(true);
        }
    }

    function setHint(text) {
        const hint = $('steamHint');
        if (hint) hint.textContent = text;
    }

    /** Установка SteamCMD в папку панели. Запускается после выбора папки сервера. */
    async function install(directory) {
        setHint('подготовка SteamCMD…');
        document.querySelector('.steam-icon')?.classList.remove('ok');
        setProgress(0, 'Скачивание SteamCMD');
        setSetupEnabled(false, 'Ожидание SteamCMD…');

        // Сразу начинаем опрашивать прогресс: запрос запуска возвращается мгновенно.
        startPolling();

        // По live-каналу сервер сам присылает прогресс, опрос — только страховка.
        try {
            await call('/api/setup/steam/install', {
                method: 'POST',
                body: { serverDirectory: directory }
            });
        } catch (error) {
            stopPolling();
            setHint('ошибка: ' + error.message);
            hideProgress();
            // SteamCMD не критичен: владельца можно создать и без него.
            setSetupEnabled(true);
        }
    }

    /** Итоговое состояние после загрузки. */
    async function refreshSetupStatus() {
        const directory = currentDirectory();

        const data = await call('/api/setup/steam' + (directory ? '?directory=' + encodeURIComponent(directory) : ''))
            .catch(() => null);

        if (!data) {
            setSetupEnabled(true);
            return;
        }

        if (data.steamReady) {
            document.querySelector('.steam-icon')?.classList.add('ok');
            setHint(data.serverExists ? 'готово: сервер найден' : 'готово: SteamCMD установлен');
            hideProgress();
            setSetupEnabled(true);
            stopPolling();
            return;
        }

        if (data.running) {
            setHint(data.phase || 'подготовка…');
            setProgress(data.progress ?? 0, data.phase);
            setSetupEnabled(false, 'Ожидание SteamCMD…');
            startPolling();
            return;
        }

        if (data.error) {
            setHint('ошибка: ' + data.error);
        }

        hideProgress();
        setSetupEnabled(true);
    }

    function startPolling() {
        if (pollTimer) return;

        pollTimer = setInterval(async () => {
            const directory = currentDirectory();
            const data = await call('/api/setup/steam' + (directory ? '?directory=' + encodeURIComponent(directory) : ''))
                .catch(() => null);

            if (!data) return;

            // Готово, когда SteamCMD скачан и прогрет.
            if (data.steamReady) {
                stopPolling();
                setHint(data.serverExists ? 'готово: сервер найден' : 'готово: SteamCMD установлен');
                document.querySelector('.steam-icon')?.classList.add('ok');
                setProgress(100, 'Готово');
                setSetupEnabled(true);

                setTimeout(() => hideProgress(), 1200);
                return;
            }

            if (data.running) {
                setHint(data.phase || 'подготовка…');
                setProgress(data.progress ?? 0, data.phase);
                setSetupEnabled(false, 'Ожидание SteamCMD…');
                return;
            }

            // Процесс не идёт и не готов — значит остановился с ошибкой.
            stopPolling();
            if (data.error) {
                setHint('ошибка: ' + data.error);
            } else {
                setHint('загрузка не завершена');
            }

            hideProgress();
            setSetupEnabled(true);
        }, 700);
    }

    function stopPolling() {
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    /* ---------------------------------------------- окно входа в Steam в панели */

    async function checkLogin() {
        try {
            const data = await call('/api/steam/status');

            // Вход нужен только один раз: дальше сессия SteamCMD переиспользуется.
            if (data.loggedIn || !data.installed) {
                return;
            }

            // Сервера ещё нет (сразу после создания владельца) — вход всё равно нужен:
            // без него панель не сможет скачать сервер DayZ.
            const serverId = window.dayzCurrentServerId?.();

            if (serverId) {
                // Окно входа нельзя закрыть, поэтому не навязываем его, если
                // в папке сервера уже лежит готовый сервер DayZ — Steam не нужен.
                const server = await call('/api/steam/server/status?serverId=' + encodeURIComponent(serverId))
                    .catch(() => null);

                if (server?.installed) {
                    return;
                }
            }

            openLogin(data.username ?? '');
        } catch {
            // Панель может быть недоступна в момент старта.
        }
    }

    function openLogin(username) {
        const modal = $('steamModal');
        if (!modal) return;

        $('steamUser').value = username ?? '';
        $('steamPass').value = '';
        $('steamGuard').value = '';
        $('steamGuardRow')?.classList.add('hidden');
        $('steamConfirmRow')?.classList.add('hidden');
        $('steamModalError')?.classList.add('hidden');
        $('steamProgress')?.classList.add('hidden');

        // Отмечаем переключатель сохранённым выбором: так не нужно
        // указывать наличие Steam Guard при каждом входе.
        const guardToggle = $('steamUseGuard');
        if (guardToggle) guardToggle.checked = loadGuardChoice();

        const loginBtn = $('steamModalSubmit');
        if (loginBtn) { loginBtn.disabled = false; loginBtn.textContent = 'Войти'; loginBtn.style.display = ''; }
        const guardBtn = $('steamGuardSubmit');
        if (guardBtn) guardBtn.style.display = 'none';

        modal.classList.add('open');
        setTimeout(() => { (username ? $('steamPass') : $('steamUser')).focus(); }, 60);
    }

    const GUARD_KEY = 'dayzpulse.steam.useGuard';

    function loadGuardChoice() {
        try { return localStorage.getItem(GUARD_KEY) === '1'; } catch { return false; }
    }

    function saveGuardChoice(value) {
        try { localStorage.setItem(GUARD_KEY, value ? '1' : '0'); } catch { /* недоступно */ }
    }

    function closeLogin() {
        $('steamModal')?.classList.remove('open');
        stopLoginPolling();
    }

    async function submitLogin() {
        const errorBox = $('steamModalError');
        errorBox?.classList.add('hidden');

        const username = $('steamUser').value.trim();
        const password = $('steamPass').value;
        const expectsGuard = !!$('steamUseGuard')?.checked;

        if (!username || !password) {
            showError('Укажите логин и пароль Steam.');
            return;
        }

        // Запоминаем выбор, чтобы не отмечать его при каждом входе.
        saveGuardChoice(expectsGuard);

        const button = $('steamModalSubmit');
        if (button) { button.disabled = true; button.textContent = 'Вход…'; }

        // Поле кода пока не показываем: ждём, пока Steam его запросит.
        $('steamGuardRow')?.classList.add('hidden');
        $('steamProgress')?.classList.remove('hidden');
        setProgressText(expectsGuard
            ? 'Ожидание запроса кода Steam Guard…'
            : 'Обращение к Steam…');

        try {
            await call('/api/steam/login', { method: 'POST', body: { username, password, expectsGuard } });
            startLoginPolling(expectsGuard);
        } catch (error) {
            if (button) { button.disabled = false; button.textContent = 'Войти'; }
            $('steamProgress')?.classList.add('hidden');
            showError(error.data?.code === 'not_installed'
                ? 'SteamCMD ещё не установлен. Дождитесь загрузки.'
                : error.message);
        }
    }

    function setProgressText(text) {
        const el = $('steamProgressText');
        if (el) el.textContent = text;
    }

    async function submitGuard() {
        const code = $('steamGuard').value.trim();
        if (code.length < 4) {
            showError('Введите код Steam Guard.');
            return;
        }

        try {
            await call('/api/steam/guard', { method: 'POST', body: { code } });
            $('steamGuardRow')?.classList.add('hidden');
            $('steamGuard').value = '';
            startLoginPolling();
        } catch (error) {
            showError(error.message);
        }
    }

    function startLoginPolling(expectsGuard) {
        stopLoginPolling();
        lastState = '';
        if (expectsGuard) setProgressText('Ожидание запроса кода Steam Guard…');
        loginPollTimer = setInterval(pollLogin, 1200);
        pollLogin();
    }

    function stopLoginPolling() {
        if (loginPollTimer) {
            clearInterval(loginPollTimer);
            loginPollTimer = null;
        }
    }

    async function pollLogin() {
        let data;
        try {
            data = await call('/api/steam/login/status');
        } catch {
            return;
        }

        const guardRow = $('steamGuardRow');
        const confirmRow = $('steamConfirmRow');
        const progress = $('steamProgress');
        const loginBtn = $('steamModalSubmit');
        const guardBtn = $('steamGuardSubmit');

        // Кнопки показываем по состоянию: вход либо отправка кода.
        function showButtons(guard) {
            if (loginBtn) loginBtn.style.display = guard ? 'none' : '';
            if (guardBtn) guardBtn.style.display = guard ? '' : 'none';
        }

        switch (data.state) {
            case 'Running':
                showButtons(false);
                setProgressText('Обращение к Steam…');
                break;

            case 'WaitingConfirm':
                // Steam ждёт подтверждения в мобильном приложении — код не нужен.
                // Панель продолжит вход автоматически после «Одобрить».
                progress?.classList.remove('hidden');
                setProgressText('Ожидание подтверждения в приложении Steam…');
                guardRow?.classList.add('hidden');
                confirmRow?.classList.remove('hidden');
                showButtons(false);
                break;

            case 'WaitingGuard':
                // Требуется код из приложения или письма Steam.
                progress?.classList.add('hidden');
                confirmRow?.classList.add('hidden');
                guardRow?.classList.remove('hidden');
                showButtons(true);
                $('steamGuard')?.focus();
                break;

            case 'Done':
                stopLoginPolling();
                progress?.classList.add('hidden');
                confirmRow?.classList.add('hidden');
                showButtons(false);
                finish(true, 'Вход выполнен. Сессия сохранена — код больше не потребуется.');

                // После входа открываем добавление сервера с папкой из первого запуска.
                onLoginComplete?.();
                return;

            case 'Failed':
                stopLoginPolling();
                progress?.classList.add('hidden');
                confirmRow?.classList.add('hidden');
                guardRow?.classList.add('hidden');
                showButtons(false);
                if (loginBtn) { loginBtn.disabled = false; loginBtn.textContent = 'Повторить'; }
                showError(data.error ?? 'Не удалось войти.');
                return;
        }

        if (data.state !== lastState) {
            lastState = data.state;
        }
    }

    function finish(success, message) {
        const errorBox = $('steamModalError');
        if (errorBox) {
            errorBox.textContent = message;
            errorBox.className = success ? 'auth-alert ok' : 'auth-alert';
        }

        if (success) {
            setTimeout(closeLogin, 1800);
        }
    }

    function showError(message) {
        const box = $('steamModalError');
        if (!box) return;
        box.textContent = message;
        box.className = 'auth-alert';
    }

    function bind(panel) {
        $('steamModalSubmit')?.addEventListener('click', submitLogin);
        $('steamGuardSubmit')?.addEventListener('click', submitGuard);

        $('steamGuard')?.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') { event.preventDefault(); submitGuard(); }
        });

        $('steamPass')?.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') { event.preventDefault(); submitLogin(); }
        });

        // Переключатель Steam Guard: подсказка меняется под выбранный режим.
        $('steamUseGuard')?.addEventListener('change', (event) => {
            const hint = $('steamGuardHint');
            if (!hint) return;
            hint.textContent = event.target.checked
                ? 'Панель дождётся запроса кода Steam Guard и покажет поле для ввода (или подтверждения в приложении).'
                : 'Вход выполнится сразу по логину и паролю, без запроса кода.';
            saveGuardChoice(event.target.checked);
        });

        // Окно входа нельзя закрыть до успешного входа: убираем и крестик,
        // и «Позже», и закрытие по клику вне и по Esc. Остаётся только вход.

        // Пока Steam ждёт подтверждения, можно перейти на ввод кода.
        $('steamConfirmUseCode')?.addEventListener('click', () => {
            $('steamConfirmRow')?.classList.add('hidden');
            $('steamGuardRow')?.classList.remove('hidden');
            $('steamGuardSubmit').style.display = '';
            $('steamModalSubmit').style.display = 'none';
            $('steamProgress')?.classList.add('hidden');
            $('steamGuard')?.focus();
        });

        // Повторить запрос подтверждения: Steam пришлёт новый пуш.
        $('steamConfirmRetry')?.addEventListener('click', async () => {
            const errorBox = $('steamModalError');
            errorBox?.classList.add('hidden');

            const button = $('steamConfirmRetry');
            if (button) { button.disabled = true; button.textContent = 'Отправка…'; }

            $('steamConfirmRow')?.classList.add('hidden');
            $('steamProgress')?.classList.remove('hidden');
            setProgressText('Повторный запрос подтверждения…');

            try {
                await call('/api/steam/login', {
                    method: 'POST',
                    body: {
                        username: $('steamUser').value.trim(),
                        password: $('steamPass').value,
                        expectsGuard: !!$('steamUseGuard')?.checked,
                        restart: true
                    }
                });

                startLoginPolling(true);
            } catch (error) {
                showError(error.message);
                $('steamProgress')?.classList.add('hidden');
                $('steamConfirmRow')?.classList.remove('hidden');
            } finally {
                if (button) { button.disabled = false; button.textContent = 'Отправить запрос ещё раз'; }
            }
        });

        $('btnSteamLogin')?.addEventListener('click', async () => {
            const status = await call('/api/steam/status').catch(() => null);
            openLogin(status?.username ?? '');
        });

        if (panel) {
            checkLogin();
        }
    }

    return { initSetupBlock, bind, checkLogin, openLogin, setSetupEnabled, applySetupState, applyPush, stopPolling, setLoginCompleteHandler };
})();

// Страница создания владельца использует тот же файл.
SteamUI.initSetupBlock();
