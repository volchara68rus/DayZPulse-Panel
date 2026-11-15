/* ============================================================
   DayZPulse — интерактивная тайловая карта (без внешних библиотек).
   Тайлы отдаёт панель (проксирует xam.nu), поэтому строгий CSP
   и офлайн-работа панели не нарушаются.
   ============================================================ */
'use strict';

/**
 * Небольшая карта на тайлах: перетаскивание, зум колесом и кнопками.
 * Система координат — игровая (метры). Ось X растёт на восток,
 * ось Z — на север; на экране север сверху.
 *
 * Маркеры рисуются в отдельном SVG-слое в экранных пикселях; слой
 * перерисовывается при любом смещении или зуме.
 */
class TileMap {
    /**
     * @param {HTMLElement} wrap контейнер карты (position: relative)
     * @param {HTMLElement} tiles слой тайлов
     * @param {() => void} onRender вызывается после каждого изменения вида
     */
    constructor(wrap, tiles, onRender) {
        this.wrap = wrap;
        this.tiles = tiles;
        this.onRender = onRender;

        this.size = 15360;       // размер мира в метрах
        this.minZoom = 0;
        this.maxZoom = 8;
        this.zoom = 3;
        this.layer = 'topographic';
        this.tileUrl = null;     // (layer, z, x, y) => string

        // Центр вида в игровых метрах.
        this.centerX = this.size / 2;
        this.centerZ = this.size / 2;

        this.world = document.createElement('div');
        this.world.className = 'map-world';
        this.tiles.appendChild(this.world);

        // Загруженные тайлы: ключ «z/x/y» → элемент. Позволяет не пересоздавать
        // картинки при перетаскивании и не дёргать сеть повторно.
        this._tileNodes = new Map();

        this._bindPointer();
        this._bindWheel();
        window.addEventListener('resize', () => this.render());
    }

    /** Настраивает карту: размер мира, максимальный зум, источник тайлов. */
    configure({ size, maxZoom, layer, tileUrl }) {
        if (size) this.size = size;
        if (maxZoom) this.maxZoom = maxZoom;
        if (layer) this.layer = layer;

        // Новый источник (другая карта) — сбрасываем загруженные тайлы,
        // иначе старые картинки останутся на месте.
        if (tileUrl && tileUrl !== this.tileUrl) {
            this.tileUrl = tileUrl;
            this._resetTiles();
        }

        this.minZoom = Math.max(0, Math.min(this.minZoom, this.maxZoom));
        this.zoom = Math.max(this.minZoom, Math.min(this.zoom, this.maxZoom));
        this.render();
    }

    _resetTiles() {
        this._tileNodes.clear();
        this.world.replaceChildren();
        this._renderedZoom = null;
    }

    get containerWidth() { return this.wrap.clientWidth || 1; }
    get containerHeight() { return this.wrap.clientHeight || 1; }

    /** Мировой размер в пикселях на текущем зуме. */
    get worldPx() { return 256 * Math.pow(2, this.zoom); }

    /** Игровые координаты → пиксели в мировом слое. */
    project(gameX, gameZ) {
        const s = this.worldPx / this.size;
        return { x: gameX * s, y: (this.size - gameZ) * s };
    }

    /** Пиксели мирового слоя → экранные (относительно контейнера). */
    toScreen(worldX, worldY) {
        return {
            x: worldX + this.containerWidth / 2 - this.centerPx.x,
            y: worldY + this.containerHeight / 2 - this.centerPx.y
        };
    }

    /** Центр вида в пикселях мирового слоя. */
    get centerPx() {
        const s = this.worldPx / this.size;
        return { x: this.centerX * s, y: (this.size - this.centerZ) * s };
    }

    /** Ставит центр по игровым координатам и приближает к ним. */
    setView(gameX, gameZ, zoom) {
        if (typeof zoom === 'number') {
            this.zoom = Math.max(this.minZoom, Math.min(this.maxZoom, Math.round(zoom)));
        }
        this.centerX = gameX;
        this.centerZ = gameZ;
        this._clamp();
        this.render();
    }

    /** Центрирует и подбирает зум так, чтобы карта помещалась целиком. */
    fit() {
        const need = Math.max(this.containerWidth, this.containerHeight);
        const ideal = Math.log2(need / 256);
        this.zoom = Math.max(this.minZoom, Math.min(this.maxZoom, Math.floor(ideal)));
        this.centerX = this.size / 2;
        this.centerZ = this.size / 2;
        this._clamp();
        this.render();
    }

    setLayer(layer) {
        if (layer === this.layer) return;
        this.layer = layer;

        // Ключи тайлов не включают слой — сбрасываем загруженные картинки.
        this._resetTiles();
        this.render();
    }

    zoomBy(delta) {
        const next = Math.max(this.minZoom, Math.min(this.maxZoom, this.zoom + delta));
        if (next !== this.zoom) {
            this.zoom = next;
            this._clamp();
            this.render();
        }
    }

    invalidateSize() {
        this.render();
    }

    /** Полный сброс при переключении сервера: чистит тайлы и вид. */
    reset() {
        this.tileUrl = null;
        this._resetTiles();
        this.zoom = 3;
        this.centerX = this.size / 2;
        this.centerZ = this.size / 2;
    }

    /* ------------------------------------------------------------ отрисовка */

    render() {
        if (!this.tileUrl) return;

        const w = this.containerWidth;
        const h = this.containerHeight;
        const worldPx = this.worldPx;
        const center = this.centerPx;
        const offsetX = w / 2 - center.x;
        const offsetY = h / 2 - center.y;

        this.world.style.width = worldPx + 'px';
        this.world.style.height = worldPx + 'px';
        this.world.style.transform = `translate(${offsetX}px, ${offsetY}px)`;

        if (this._renderedZoom !== this.zoom) {
            // Смена зума: старая сетка тайлов больше не подходит.
            this._resetTiles();
            this._renderedZoom = this.zoom;
        }

        // Видимый диапазон тайлов (с запасом в один тайл по краям).
        const max = Math.pow(2, this.zoom) - 1;
        const firstX = Math.max(0, Math.floor((-offsetX) / 256) - 1);
        const firstY = Math.max(0, Math.floor((-offsetY) / 256) - 1);
        const lastX = Math.min(max, Math.floor((-offsetX + w) / 256) + 1);
        const lastY = Math.min(max, Math.floor((-offsetY + h) / 256) + 1);

        const wanted = new Set();

        for (let tx = firstX; tx <= lastX; tx++) {
            for (let ty = firstY; ty <= lastY; ty++) {
                const key = `${this.zoom}/${tx}/${ty}`;
                wanted.add(key);

                if (!this._tileNodes.has(key)) {
                    const img = this._tile(key, this.zoom, tx, ty);
                    this._tileNodes.set(key, img);
                    this.world.appendChild(img);
                }
            }
        }

        // Убираем тайлы, вышедшие за пределы вида, чтобы не копить мусор.
        for (const [key, node] of this._tileNodes) {
            if (!wanted.has(key)) {
                node.remove();
                this._tileNodes.delete(key);
            }
        }

        if (typeof this.onRender === 'function') {
            this.onRender();
        }
    }

    _tile(key, z, x, y) {
        const img = document.createElement('img');
        img.className = 'map-tile';
        img.decoding = 'async';
        img.loading = 'lazy';
        img.alt = '';
        img.style.left = (x * 256) + 'px';
        img.style.top = (y * 256) + 'px';
        img.src = this.tileUrl(this.layer, z, x, y);
        img.addEventListener('error', () => { img.style.visibility = 'hidden'; });
        return img;
    }

    /* ---------------------------------------------------------- управление */

    _bindPointer() {
        const DRAG_THRESHOLD = 4; // px — до этого считаем, что это клик

        let pending = false;
        let dragging = false;
        let pointerId = null;
        let startX = 0;
        let startY = 0;
        let originX = 0;
        let originZ = 0;

        const down = (event) => {
            if (event.button !== 0) return;
            // Не начинаем перетаскивание с элементов управления картой.
            if (event.target.closest?.('.map-zoom, .map-filters, .map-note')) return;

            pending = true;
            dragging = false;
            pointerId = event.pointerId;
            startX = event.clientX;
            startY = event.clientY;
            originX = this.centerX;
            originZ = this.centerZ;
        };

        const move = (event) => {
            if (!pending) return;

            // Захват указателя включаем только после заметного сдвига, иначе
            // перехватывали бы клики по кнопкам зума и маркерам игроков.
            if (!dragging) {
                if (Math.hypot(event.clientX - startX, event.clientY - startY) < DRAG_THRESHOLD) return;
                dragging = true;
                this.wrap.classList.add('dragging');
                try { this.wrap.setPointerCapture?.(pointerId); } catch { /* не критично */ }
            }

            const scale = this.size / this.worldPx;

            // Экранный сдвиг переводим в игровые метры.
            this.centerX = originX - (event.clientX - startX) * scale;
            this.centerZ = originZ + (event.clientY - startY) * scale;
            this._clamp();
            this.render();
        };

        const up = () => {
            if (dragging) {
                this.wrap.classList.remove('dragging');
                if (pointerId !== null) {
                    try { this.wrap.releasePointerCapture?.(pointerId); } catch { /* не критично */ }
                }
            }
            pending = false;
            dragging = false;
            pointerId = null;
        };

        this.wrap.addEventListener('pointerdown', down);
        this.wrap.addEventListener('pointermove', move);
        this.wrap.addEventListener('pointerup', up);
        this.wrap.addEventListener('pointercancel', up);
        this.wrap.addEventListener('pointerleave', up);
    }

    _bindWheel() {
        this.wrap.addEventListener('wheel', (event) => {
            // Зум к курсору: сохраняем точку под мышью на месте.
            event.preventDefault();

            const rect = this.wrap.getBoundingClientRect();
            const mouseX = event.clientX - rect.left;
            const mouseY = event.clientY - rect.top;

            const before = this._screenToGame(mouseX, mouseY);
            this.zoomBy(event.deltaY < 0 ? 1 : -1);
            const after = this._screenToGame(mouseX, mouseY);

            this.centerX += before.x - after.x;
            this.centerZ += before.z - after.z;
            this._clamp();
            this.render();
        }, { passive: false });
    }

    /** Экранная точка → игровые координаты на текущем зуме. */
    _screenToGame(screenX, screenY) {
        const scale = this.size / this.worldPx;
        const center = this.centerPx;
        const worldX = screenX - this.containerWidth / 2 + center.x;
        const worldY = screenY - this.containerHeight / 2 + center.y;
        return { x: worldX * scale, z: this.size - worldY * scale };
    }

    /** Не даёт увести карту за пределы мира. */
    _clamp() {
        this.centerX = Math.max(0, Math.min(this.size, this.centerX));
        this.centerZ = Math.max(0, Math.min(this.size, this.centerZ));
    }
}

window.TileMap = TileMap;
