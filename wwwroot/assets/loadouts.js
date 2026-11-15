/* ============================================================
   DayZPulse — стартовый набор и пресеты: графический редактор
   набора (фигурка + слоты + карго + каталог предметов).
   ============================================================ */
'use strict';

const Loadouts = (() => {
    const el = (id) => document.getElementById(id);

    /** Путь API в контексте активного сервера. */
    function apiPath(path) {
        const id = App.currentServerId;
        return id ? `/servers/${encodeURIComponent(id)}${path}` : path;
    }

    // Текущее состояние: стартовые наборы и список пресетов.
    let state = {
        spawn: { common: empty('Общий'), male: null, female: null },
        presets: []
    };

    let activeKit = 'common';   // common | male | female
    let editingPreset = null;   // объект пресета при редактировании
    let presetIsNew = false;

    /* ------------------------------------------------------------ утилиты */

    function empty(name) {
        return { name: name || '', description: '', items: [] };
    }

    /** Приводит загруженный набор к безопасному виду. */
    function normalizeLoadout(raw, fallbackName) {
        const loadout = empty(raw?.name || fallbackName);
        loadout.description = raw?.description || '';
        loadout.items = Array.isArray(raw?.items) ? raw.items.map((i) => ({
            className: i.className || '',
            quantity: Number(i.quantity) > 0 ? Number(i.quantity) : 1,
            health: typeof i.health === 'number' ? i.health : 1,
            slot: i.slot || '',
            target: i.target || 'Cargo',
            attachments: Array.isArray(i.attachments) ? i.attachments.slice() : []
        })) : [];
        return loadout;
    }

    /** Считает предметы набора. */
    function countItems(loadout) {
        return (loadout?.items ?? []).length;
    }

    /* ------------------------------------------------------- drag-состояние */

    let draggedClass = '';

    /* =====================================================================
       Графический редактор набора.
       Возвращает контроллер: { setLoadout, getLoadout }.
       ===================================================================== */

    function createEditor(container) {
        let loadout = empty();
        let filterTarget = null;   // null — показать все, '' — карго, 'Head' — слот
        let selected = -1;         // индекс выбранного предмета в loadout.items
        let search = '';

        /** Разбирает items на визуальные слоты/карго/руки. */
        function toVisual() {
            const slots = {};
            const cargo = [];
            let hands = null;

            loadout.items.forEach((item, index) => {
                const node = { item, index };
                if (item.target === 'Slot' && item.slot) slots[item.slot] = node;
                else if (item.target === 'Hands') hands = node;
                else cargo.push(node);
            });

            return { slots, cargo, hands };
        }

        /** Добавляет предмет: слот — заменяет, иначе добавляет в карго/руки. */
        function place(className, target, slot) {
            if (target === 'Slot' && slot) {
                loadout.items = loadout.items.filter((i) => !(i.target === 'Slot' && i.slot === slot));
            }
            loadout.items.push({
                className, quantity: 1, health: 1,
                slot: slot || '', target, attachments: []
            });
            selected = loadout.items.length - 1;
            render();
        }

        function removeAt(index) {
            if (index < 0 || index >= loadout.items.length) return;
            loadout.items.splice(index, 1);
            selected = -1;
            render();
        }

        /* ------------------------------------------------------------ render */

        function render() {
            container.replaceChildren(buildStage(), buildDetails(), buildPalette());
        }

        function buildStage() {
            const stage = document.createElement('div');
            stage.className = 'inv-stage';
            stage.id = 'loadoutStage';

            const figure = document.createElement('div');
            figure.className = 'inv-figure';

            const body = document.createElement('div');
            body.className = 'inv-body';

            const visual = toVisual();

            for (const [key, label, shape] of Players.BODY_ROWS) {
                body.appendChild(buildBodyPart(key, label, shape, visual.slots[key]));
            }

            const left = document.createElement('div');
            left.className = 'inv-side inv-side-left';
            for (const [key, label, icon] of Players.SLOT_LAYOUT.left) {
                left.appendChild(buildSideSlot(key, label, icon, visual.slots[key]));
            }

            const right = document.createElement('div');
            right.className = 'inv-side inv-side-right';
            for (const [key, label, icon] of Players.SLOT_LAYOUT.right) {
                right.appendChild(buildSideSlot(key, label, icon, visual.slots[key]));
            }

            figure.append(body, left, right);

            // Карго + руки.
            const cargo = document.createElement('div');
            cargo.className = 'inv-cargo';

            const title = document.createElement('div');
            title.className = 'inv-cargo-title';
            title.textContent = 'Рюкзак и карманы';
            cargo.appendChild(title);

            // Зона «в руки».
            const hands = document.createElement('div');
            hands.className = 'loadout-hands' + (visual.hands ? ' filled' : '');
            hands.dataset.slot = '';
            hands.dataset.target = 'Hands';
            hands.title = 'В руки (панель быстрого доступа)';
            const handsIcon = document.createElement('span');
            handsIcon.className = 'icon';
            const handsName = document.createElement('span');
            handsName.className = 'nm';
            if (visual.hands) {
                const item = visual.hands.item;
                handsIcon.textContent = Players.iconFor(item.className);
                handsName.textContent = item.className;
                hands.addEventListener('click', () => select(visual.hands.index));
            } else {
                handsIcon.textContent = '✋';
                handsName.textContent = 'В руки';
            }
            hands.append(handsIcon, handsName);
            cargo.appendChild(hands);

            const list = document.createElement('div');
            list.className = 'inv-cargo-list';
            list.dataset.slot = '';
            list.dataset.target = 'Cargo';

            if (visual.cargo.length === 0) {
                const hint = document.createElement('div');
                hint.className = 'muted';
                hint.style.fontSize = '12px';
                hint.textContent = 'Пусто. Перетащите предметы из каталога.';
                list.appendChild(hint);
            } else {
                for (const node of visual.cargo) {
                    list.appendChild(buildCargoRow(node));
                }
            }

            cargo.appendChild(list);

            stage.append(figure, cargo);
            bindDropZones(stage);
            return stage;
        }

        /** Зона силуэта. */
        function buildBodyPart(slotKey, label, shape, node) {
            const box = document.createElement('div');
            box.className = 'bp bp-' + shape + (node ? ' filled' : '') + (selected === node?.index ? ' selected' : '');
            box.dataset.slot = slotKey;
            box.dataset.target = 'Slot';
            box.title = label;

            const ic = document.createElement('span');
            ic.className = 'icon';
            ic.textContent = node ? Players.iconFor(node.item.className) : '';

            const nm = document.createElement('span');
            nm.className = 'nm';
            nm.textContent = node ? node.item.className : label;

            box.append(ic, nm);
            if (node) box.addEventListener('click', () => select(node.index));
            return box;
        }

        /** Боковой слот-аксессуар. */
        function buildSideSlot(slotKey, label, icon, node) {
            const box = document.createElement('div');
            box.className = 'inv-slot' + (node ? ' filled' : '') + (selected === node?.index ? ' selected' : '');
            box.dataset.slot = slotKey;
            box.dataset.target = 'Slot';
            box.title = label;

            const ic = document.createElement('span');
            ic.className = 'icon';
            ic.textContent = node ? Players.iconFor(node.item.className) : icon;

            const nm = document.createElement('span');
            nm.className = 'nm';
            nm.textContent = node ? node.item.className : label;

            box.append(ic, nm);
            if (node) box.addEventListener('click', () => select(node.index));
            return box;
        }

        /** Строка предмета в карго. */
        function buildCargoRow(node) {
            const row = document.createElement('div');
            row.className = 'inv-item' + (selected === node.index ? ' selected' : '');
            row.title = node.item.className;

            const ic = document.createElement('span');
            ic.className = 'icon';
            ic.textContent = Players.iconFor(node.item.className);

            const nm = document.createElement('span');
            nm.className = 'nm';
            nm.textContent = node.item.className;

            row.append(ic, nm);

            if (node.item.quantity > 1) {
                const qty = document.createElement('span');
                qty.className = 'qty';
                qty.textContent = '×' + node.item.quantity;
                row.appendChild(qty);
            }

            const del = document.createElement('button');
            del.type = 'button';
            del.className = 'icon-x';
            del.textContent = '×';
            del.title = 'Убрать предмет';
            del.addEventListener('click', (event) => {
                event.stopPropagation();
                removeAt(node.index);
            });
            row.appendChild(del);

            row.addEventListener('click', (event) => {
                event.stopPropagation();
                select(node.index);
            });
            return row;
        }

        /** Панель настроек выбранного предмета. */
        function buildDetails() {
            const bar = document.createElement('div');
            bar.className = 'loadout-details';

            if (selected < 0 || selected >= loadout.items.length) {
                bar.classList.add('muted');
                bar.textContent = 'Выберите предмет, чтобы задать количество и состояние.';
                return bar;
            }

            const item = loadout.items[selected];

            const name = document.createElement('b');
            name.className = 'mono';
            name.textContent = item.className;
            bar.appendChild(name);

            // Количество.
            const qtyWrap = document.createElement('label');
            qtyWrap.className = 'loadout-field';
            qtyWrap.textContent = 'Кол-во';
            const qty = document.createElement('input');
            qty.type = 'number';
            qty.min = '1';
            qty.max = '99';
            qty.value = item.quantity;
            qty.style.width = '68px';
            qty.addEventListener('change', () => {
                item.quantity = Math.max(1, Math.min(99, Number(qty.value) || 1));
                render();
            });
            qtyWrap.appendChild(qty);
            bar.appendChild(qtyWrap);

            // Состояние (доля 0..1).
            const healthWrap = document.createElement('label');
            healthWrap.className = 'loadout-field';
            healthWrap.textContent = 'Состояние';
            const health = document.createElement('input');
            health.type = 'range';
            health.min = '0';
            health.max = '1';
            health.step = '0.05';
            health.value = item.health < 0 ? 1 : item.health;
            health.style.width = '120px';
            const healthLbl = document.createElement('span');
            healthLbl.className = 'muted';
            healthLbl.style.fontSize = '12px';
            healthLbl.textContent = Math.round((item.health < 0 ? 1 : item.health) * 100) + '%';
            health.addEventListener('input', () => {
                healthLbl.textContent = Math.round(Number(health.value) * 100) + '%';
            });
            health.addEventListener('change', () => {
                item.health = Number(health.value);
            });
            healthWrap.append(health, healthLbl);
            bar.appendChild(healthWrap);

            const remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'btn ghost sm';
            remove.style.marginLeft = 'auto';
            remove.textContent = 'Убрать';
            remove.addEventListener('click', () => removeAt(selected));
            bar.appendChild(remove);

            return bar;
        }

        /** Палитра предметов. */
        function buildPalette() {
            const wrap = document.createElement('div');
            wrap.className = 'inv-palette';

            const head = document.createElement('div');
            head.className = 'row between';
            head.style.marginBottom = '8px';

            const hint = document.createElement('span');
            hint.className = 'muted';
            hint.style.fontSize = '12px';
            hint.textContent = 'Перетащите предмет на фигурку, в руки или в рюкзак';

            const input = document.createElement('input');
            input.type = 'text';
            input.placeholder = 'Поиск класса';
            input.style.width = '180px';
            input.value = search;
            input.addEventListener('input', () => { search = input.value; renderPaletteInto(list, term()); });

            head.append(hint, input);
            wrap.appendChild(head);

            if (filterTarget !== null) {
                const fhint = document.createElement('div');
                fhint.className = 'inv-filter-hint';
                fhint.textContent = filterTarget
                    ? 'Только для слота: ' + (Players.GROUP_LABELS[filterTarget] ?? filterTarget)
                    : 'Только предметы в карманы · нажмите слот ещё раз, чтобы показать все';
                wrap.appendChild(fhint);
            }

            const list = document.createElement('div');
            list.className = 'inv-palette-list';
            renderPaletteInto(list, term());
            wrap.appendChild(list);

            return wrap;
        }

        function term() {
            return (search || '').trim().toLowerCase();
        }

        function renderPaletteInto(list, text) {
            list.replaceChildren();

            const items = Players.catalog().filter(([cls, label, , group]) => {
                if (filterTarget !== null && !Players.groupFitsSlot(group, filterTarget)) return false;
                if (text && !(cls.toLowerCase().includes(text) || (label || '').toLowerCase().includes(text))) return false;
                return true;
            });

            if (items.length === 0) {
                const hint = document.createElement('div');
                hint.className = 'muted';
                hint.style.fontSize = '12px';
                hint.style.padding = '4px';
                hint.textContent = 'Ничего не найдено.';
                list.appendChild(hint);
                return;
            }

            const groups = new Map();
            for (const item of items) {
                const group = item[3] || 'Other';
                if (!groups.has(group)) groups.set(group, []);
                groups.get(group).push(item);
            }

            for (const [group, groupItems] of groups) {
                const head = document.createElement('div');
                head.className = 'inv-palette-group';
                head.textContent = Players.GROUP_LABELS[group] ?? group;
                list.appendChild(head);

                const row = document.createElement('div');
                row.className = 'inv-palette-row';
                for (const [cls, label, icon] of groupItems.slice(0, 200)) {
                    row.appendChild(buildChip(cls, label, icon));
                }
                list.appendChild(row);
            }
        }

        function buildChip(cls, label, icon) {
            const chip = document.createElement('span');
            chip.className = 'inv-chip';
            chip.draggable = true;
            chip.dataset.cls = cls;
            chip.title = cls;

            const ic = document.createElement('span');
            ic.className = 'icon';
            ic.textContent = icon || '🔹';
            const nm = document.createElement('span');
            nm.textContent = label && label !== cls ? label : cls;
            chip.append(ic, nm);

            chip.addEventListener('dragstart', (event) => {
                event.dataTransfer.setData('text/plain', cls);
                event.dataTransfer.effectAllowed = 'copy';
                draggedClass = cls;
                chip.classList.add('dragging');
            });
            chip.addEventListener('dragend', () => {
                draggedClass = '';
                chip.classList.remove('dragging');
                for (const n of container.querySelectorAll('.slot-hover')) n.classList.remove('slot-hover');
                for (const n of container.querySelectorAll('.slot-bad')) n.classList.remove('slot-bad');
            });

            // Двойной клик — быстрый слот по эвристике группы.
            chip.addEventListener('dblclick', () => quickAdd(cls));
            return chip;
        }

        /** Быстрое добавление: группа → подходящий слот, иначе карго. */
        function quickAdd(cls) {
            const item = Players.catalog().find((i) => i[0] === cls);
            const group = item ? item[3] : 'Other';
            for (const [key, , ] of Players.BODY_ROWS) {
                if (Players.groupFitsSlot(group, key)) return place(cls, 'Slot', key);
            }
            for (const [key] of Players.SLOT_LAYOUT.left) {
                if (Players.groupFitsSlot(group, key)) return place(cls, 'Slot', key);
            }
            for (const [key] of Players.SLOT_LAYOUT.right) {
                if (Players.groupFitsSlot(group, key)) return place(cls, 'Slot', key);
            }
            place(cls, 'Cargo', '');
        }

        /** Клик по слоту: выбрать заполненный / отфильтровать пустой. */
        function select(index) {
            if (selected === index) {
                selected = -1;
            } else {
                selected = index;
            }
            render();
        }

        /** Клик по пустому слоту фильтрует палитру. */
        function filterBySlot(slot) {
            filterTarget = filterTarget === slot ? null : slot;
            render();
        }

        /** Разрешает перетаскивание на слоты, руки и карго. */
        function bindDropZones(stage) {
            const areas = stage.querySelectorAll('[data-slot]');

            for (const area of areas) {
                const slot = area.dataset.slot || '';
                const target = area.dataset.target || (slot ? 'Slot' : 'Cargo');

                area.addEventListener('click', (event) => {
                    // Клик обработан выше (выбор предмета) — здесь только фильтр.
                    if (area.classList.contains('filled')) return;
                    event.stopPropagation();
                    filterBySlot(target === 'Hands' ? null : slot);
                });

                area.addEventListener('dragover', (event) => {
                    const item = Players.catalog().find((i) => i[0] === draggedClass);
                    const fits = target === 'Slot'
                        ? (!item || Players.groupFitsSlot(item[3], slot))
                        : true;

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
                    area.classList.remove('slot-hover', 'slot-bad');
                });

                area.addEventListener('drop', (event) => {
                    event.preventDefault();
                    event.stopPropagation();
                    area.classList.remove('slot-hover', 'slot-bad');

                    const cls = event.dataTransfer.getData('text/plain') || draggedClass;
                    if (!cls) return;

                    const item = Players.catalog().find((i) => i[0] === cls);
                    if (target === 'Slot' && item && !Players.groupFitsSlot(item[3], slot)) {
                        App.toast('Не подходит', `«${cls}» нельзя надеть в этот слот.`, 'warn', 5000);
                        return;
                    }

                    if (target === 'Slot') place(cls, 'Slot', slot);
                    else if (target === 'Hands') place(cls, 'Hands', '');
                    else place(cls, 'Cargo', '');
                });
            }
        }

        return {
            setLoadout(value) {
                loadout = normalizeLoadout(value, '');
                selected = -1;
                filterTarget = null;
                render();
            },
            getLoadout() { return loadout; },
            render() { render(); }
        };
    }

    /* =====================================================================
       Стартовый набор
       ===================================================================== */

    let kitEditor = null;

    async function load() {
        try {
            const data = await App.api(apiPath('/loadouts'));

            state.spawn = {
                common: normalizeLoadout(data.spawn?.common, 'Общий'),
                male: data.spawn?.male ? normalizeLoadout(data.spawn.male, 'Мужской') : null,
                female: data.spawn?.female ? normalizeLoadout(data.spawn.female, 'Женский') : null
            };
            state.presets = Array.isArray(data.presets) ? data.presets : [];

            updateCards();
        } catch {
            // Не критично: вкладка может быть недоступна.
        }
    }

    /** Обновляет счётчики в карточках на вкладке «Игроки». */
    function updateCards() {
        const common = countItems(state.spawn.common);
        const male = state.spawn.male ? countItems(state.spawn.male) : 0;
        const female = state.spawn.female ? countItems(state.spawn.female) : 0;

        const count = el('spawnKitCount');
        if (count) count.textContent = String(common);

        const hint = el('spawnKitHint');
        if (hint) {
            const parts = [];
            if (male) parts.push('мужской: ' + male);
            if (female) parts.push('женский: ' + female);
            hint.textContent = parts.length
                ? 'для всех: ' + common + ' · ' + parts.join(' · ')
                : 'предметов для новых игроков';
        }

        const presetCount = el('presetCount');
        if (presetCount) presetCount.textContent = String(state.presets.length);

        const presetHint = el('presetHint');
        if (presetHint) {
            const active = state.presets.filter((p) => p.active).length;
            presetHint.textContent = active
                ? `${state.presets.length} наборов · активных: ${active}`
                : 'наборов для выдачи';
        }
    }

    function openSpawnKit() {
        const modal = el('spawnKitModal');
        if (!modal) return;

        modal.classList.add('open');
        setKitTab(activeKit || 'common');
        Players.ensureCatalog().then(() => {
            if (el('spawnKitModal')?.classList.contains('open')) kitEditor?.render();
        });
    }

    function setKitTab(kit) {
        activeKit = kit;

        for (const tab of document.querySelectorAll('#spawnKitTabs .subtab')) {
            tab.classList.toggle('active', tab.dataset.kit === kit);
        }

        const container = el('spawnKitEditor');
        if (!container) return;

        if (!kitEditor) kitEditor = createEditor(container);

        // Набор для пола может ещё не существовать — создаём «на лету».
        let loadout = state.spawn[kit];
        if (!loadout) {
            loadout = empty(kit === 'male' ? 'Мужской' : kit === 'female' ? 'Женский' : 'Общий');
            state.spawn[kit] = loadout;
        }

        kitEditor.setLoadout(loadout);
        kitEditor._kit = kit;

        const sub = el('spawnKitSub');
        if (sub) {
            sub.textContent = kit === 'common'
                ? 'для всех новых игроков'
                : (kit === 'male' ? 'только для мужских персонажей' : 'только для женских персонажей');
        }
        setStatus('spawnKitStatus', '');
    }

    /** Сохранение всех трёх наборов. */
    async function saveSpawnKit() {
        try {
            const body = {
                common: state.spawn.common ?? empty('Общий'),
                male: countItems(state.spawn.male) ? state.spawn.male : null,
                female: countItems(state.spawn.female) ? state.spawn.female : null
            };

            await App.api(apiPath('/loadouts/spawn'), { method: 'PUT', body });
            App.toast('Стартовый набор сохранён',
                'Применится к новым персонажам при следующем входе.', 'info');
            el('spawnKitModal')?.classList.remove('open');
            await load();
        } catch (error) {
            App.toast('Не удалось сохранить набор', error.message, 'error', 8000);
        }
    }

    function clearKit() {
        const kit = kitEditor?._kit ?? activeKit;
        if (!state.spawn[kit]) return;
        state.spawn[kit].items = [];
        kitEditor?.setLoadout(state.spawn[kit]);
    }

    /* =====================================================================
       Пресеты
       ===================================================================== */

    let presetEditor = null;

    function openPresets() {
        if (!el('presetModal')) return;
        el('presetModal').classList.add('open');
        showPresetList();
        Players.ensureCatalog().then(() => {
            if (!el('presetEditorView')?.classList.contains('hidden')) presetEditor?.render();
        });
    }

    function showPresetList() {
        editingPreset = null;
        presetIsNew = false;
        el('presetListView')?.classList.remove('hidden');
        el('presetEditorView')?.classList.add('hidden');
        el('presetFooterList')?.classList.remove('hidden');
        el('presetFooterEdit')?.classList.add('hidden');
        renderPresetList();
    }

    function renderPresetList() {
        const box = el('presetList');
        if (!box) return;
        box.replaceChildren();

        if (state.presets.length === 0) {
            const hint = document.createElement('div');
            hint.className = 'muted';
            hint.style.fontSize = '12.5px';
            hint.textContent = 'Пресетов пока нет. Создайте первый — и сможете выдать набор конкретным игрокам.';
            box.appendChild(hint);
            return;
        }

        for (const preset of state.presets) {
            const row = document.createElement('div');
            row.className = 'preset-row';

            const info = document.createElement('div');
            info.style.minWidth = '0';

            const title = document.createElement('b');
            title.textContent = preset.name || 'Без названия';

            const meta = document.createElement('div');
            meta.className = 'muted';
            meta.style.fontSize = '11.5px';
            const when = preset.expiresAt ? 'до ' + fmtTime(preset.expiresAt) : 'бессрочно';
            meta.textContent = `${countItems(preset)} предм. · игроков: ${preset.players?.length ?? 0} · ${when}`;

            info.append(title, meta);

            const badge = document.createElement('span');
            badge.className = 'tag ' + (preset.active ? 'on' : 'off');
            badge.textContent = preset.active ? 'активен' : 'не активен';

            const actions = document.createElement('div');
            actions.className = 'preset-actions';

            const edit = document.createElement('button');
            edit.type = 'button';
            edit.className = 'btn ghost sm';
            edit.textContent = 'Изменить';
            edit.addEventListener('click', () => editPreset(preset));

            const del = document.createElement('button');
            del.type = 'button';
            del.className = 'btn ghost sm';
            del.textContent = 'Удалить';
            del.addEventListener('click', () => deletePreset(preset));

            actions.append(edit, del);
            row.append(info, badge, actions);
            box.appendChild(row);
        }
    }

    function newPreset() {
        editingPreset = {
            id: '', name: '', items: [], players: [],
            expiresAt: null, enabled: true
        };
        presetIsNew = true;
        showPresetEditor();
    }

    function editPreset(preset) {
        editingPreset = {
            id: preset.id,
            name: preset.name || '',
            items: (preset.items ?? []).map((i) => ({ ...i })),
            players: (preset.players ?? []).slice(),
            expiresAt: preset.expiresAt || null,
            enabled: preset.enabled !== false
        };
        presetIsNew = false;
        showPresetEditor();
    }

    function showPresetEditor() {
        el('presetListView')?.classList.add('hidden');
        el('presetEditorView')?.classList.remove('hidden');
        el('presetFooterList')?.classList.add('hidden');
        el('presetFooterEdit')?.classList.remove('hidden');
        el('presetDelete')?.classList.toggle('hidden', presetIsNew);

        const name = el('presetName');
        if (name) name.value = editingPreset.name || '';

        const expires = el('presetExpires');
        if (expires) expires.value = editingPreset.expiresAt ? toLocalInput(editingPreset.expiresAt) : '';

        const enabled = el('presetEnabled');
        if (enabled) enabled.checked = editingPreset.enabled !== false;

        const sub = el('presetSub');
        if (sub) sub.textContent = presetIsNew ? 'новый пресет' : (editingPreset.name || '');

        const container = el('presetEditor');
        if (container) {
            if (!presetEditor) presetEditor = createEditor(container);
            presetEditor.setLoadout({ name: editingPreset.name, items: editingPreset.items });
        }

        renderPresetPlayers();
        setStatus('presetStatus', '');
    }

    /** Мультивыбор игроков (база + онлайн). */
    function renderPresetPlayers() {
        const box = el('presetPlayers');
        if (!box) return;
        box.replaceChildren();

        const chosen = new Set(editingPreset?.players ?? []);

        // Объединяем онлайн и базу.
        const map = new Map();
        for (const p of Players.database?.() ?? []) map.set(p.id, p);
        for (const p of Players.livePlayers?.() ?? []) map.set(p.id, { ...(map.get(p.id) ?? {}), ...p });

        const players = [...map.values()];

        const search = document.createElement('input');
        search.type = 'text';
        search.placeholder = 'Поиск игрока';
        search.style.marginBottom = '8px';

        const list = document.createElement('div');
        list.className = 'preset-players-list';

        function draw(term) {
            list.replaceChildren();
            const t = (term || '').trim().toLowerCase();
            const rows = players.filter((p) =>
                !t || (p.name || '').toLowerCase().includes(t) || (p.plainId || p.id || '').toLowerCase().includes(t));

            if (rows.length === 0) {
                const hint = document.createElement('div');
                hint.className = 'muted';
                hint.style.fontSize = '12px';
                hint.textContent = players.length ? 'Никого не найдено.' : 'Нет игроков в базе.';
                list.appendChild(hint);
                return;
            }

            for (const p of rows) {
                const label = document.createElement('label');
                label.className = 'check preset-player';

                const cb = document.createElement('input');
                cb.type = 'checkbox';
                cb.checked = chosen.has(p.id);
                cb.addEventListener('change', () => {
                    if (cb.checked) chosen.add(p.id);
                    else chosen.delete(p.id);
                    editingPreset.players = [...chosen];
                });

                const span = document.createElement('span');
                span.textContent = `${p.name || p.id} (${p.plainId || p.id})`;
                if (p.online) {
                    const tag = document.createElement('span');
                    tag.className = 'tag on';
                    tag.style.marginLeft = '6px';
                    tag.textContent = 'в сети';
                    span.appendChild(tag);
                }

                label.append(cb, span);
                list.appendChild(label);
            }
        }

        search.addEventListener('input', () => draw(search.value));
        draw('');
        box.append(search, list);
    }

    async function savePreset() {
        if (!editingPreset) return;

        const name = el('presetName')?.value.trim() ?? '';
        if (!name) {
            App.toast('Нужно название', 'Задайте название пресета.', 'warn');
            return;
        }

        const expires = el('presetExpires')?.value ?? '';
        const items = presetEditor?.getLoadout().items ?? [];

        const body = {
            id: editingPreset.id || '',
            name,
            items,
            players: editingPreset.players ?? [],
            expiresAt: expires ? new Date(expires).toISOString() : null,
            enabled: el('presetEnabled')?.checked !== false
        };

        try {
            await App.api(apiPath('/loadouts/presets'), { method: 'POST', body });
            App.toast('Пресет сохранён', '', 'info');
            await load();
            showPresetList();
        } catch (error) {
            App.toast('Не удалось сохранить пресет', error.message, 'error', 8000);
        }
    }

    function deletePreset(preset) {
        const target = preset ?? editingPreset;
        if (!target?.id) { showPresetList(); return; }

        UI.confirm(`Удалить пресет «${target.name || 'без названия'}»?`, async () => {
            try {
                await App.api(apiPath(`/loadouts/presets/${encodeURIComponent(target.id)}`), { method: 'DELETE' });
                App.toast('Пресет удалён', '', 'info');
                await load();
                showPresetList();
            } catch (error) {
                App.toast('Не удалось удалить', error.message, 'error');
            }
        }, 'Удалить');
    }

    /* ------------------------------------------------------------ помощники */

    function fmtTime(value) {
        try {
            const d = new Date(value);
            const pad = (n) => String(n).padStart(2, '0');
            return `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
        } catch {
            return String(value);
        }
    }

    /** ISO → значение для input[type=datetime-local]. */
    function toLocalInput(value) {
        const d = new Date(value);
        const pad = (n) => String(n).padStart(2, '0');
        return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
    }

    function setStatus(id, text) {
        const node = el(id);
        if (node) node.textContent = text || '';
    }

    /* ------------------------------------------------------------ привязка */

    function bind() {
        el('spawnKitEdit')?.addEventListener('click', openSpawnKit);
        el('presetEdit')?.addEventListener('click', openPresets);

        // Стартовый набор.
        el('spawnKitX')?.addEventListener('click', () => el('spawnKitModal')?.classList.remove('open'));
        el('spawnKitCancel')?.addEventListener('click', () => el('spawnKitModal')?.classList.remove('open'));
        el('spawnKitSave')?.addEventListener('click', saveSpawnKit);
        el('spawnKitClear')?.addEventListener('click', clearKit);
        el('spawnKitModal')?.addEventListener('click', (e) => {
            if (e.target.id === 'spawnKitModal') el('spawnKitModal').classList.remove('open');
        });
        for (const tab of document.querySelectorAll('#spawnKitTabs .subtab')) {
            tab.addEventListener('click', () => setKitTab(tab.dataset.kit));
        }

        // Пресеты.
        el('presetX')?.addEventListener('click', () => el('presetModal')?.classList.remove('open'));
        el('presetClose')?.addEventListener('click', () => el('presetModal')?.classList.remove('open'));
        el('presetModal')?.addEventListener('click', (e) => {
            if (e.target.id === 'presetModal') el('presetModal')?.classList.remove('open');
        });
        el('presetNew')?.addEventListener('click', newPreset);
        el('presetBack')?.addEventListener('click', showPresetList);
        el('presetCancel')?.addEventListener('click', showPresetList);
        el('presetSave')?.addEventListener('click', savePreset);
        el('presetDelete')?.addEventListener('click', () => deletePreset());
    }

    return { bind, load, updateCards, openSpawnKit, openPresets };
})();
