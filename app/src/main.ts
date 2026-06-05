// BeeHive_VR — Atlas-Renderer (Electron main).
//
// WGC-Pivot (1.6.2026): renders to a normal *visible* BrowserWindow on the
// desktop. The OpenXR layer running inside iRacing captures that window via
// Windows.Graphics.Capture and composites it into the VR frame.
//
// Why visible instead of offscreen+useSharedTexture: the shared-NT-handle
// path produced uncorrectable content-update jitter on animated elements
// (see HANDOVER_beehive_wgc_pivot_260601.md). WGC has its own sync model
// (frame pool, no race against Chromium's allocator) and matches the
// pattern Edge Overlays uses via its native VR companion.
//
// IPC contract for step 1: the FrameSlot field that used to carry the
// shared-texture NT handle now carries the BrowserWindow HWND. Bytes/layout
// unchanged so we can roll back without recompiling the layer. Renamed in
// step 3 after the visual proof.

import { app, BrowserWindow } from 'electron';
import path from 'node:path';
import fs from 'node:fs';
import started from 'electron-squirrel-startup';
import koffi from 'koffi';

// ⚠ Atlas-File-Logger (3.6.2026, Diagnose):
// Atlas läuft detached → kein sichtbarer stdout. Wir schreiben deshalb
// parallel zu console.log in eine eigene Log-File neben der WPF-Log.
const ATLAS_LOG_PATH = path.join(
  process.env.LOCALAPPDATA || process.env.APPDATA || '',
  'BeeHive_VR', 'logs', 'atlas.log');
try { fs.mkdirSync(path.dirname(ATLAS_LOG_PATH), { recursive: true }); } catch { /* ignore */ }
function atlasLog(msg: string): void {
  const line = `${new Date().toISOString()} ${msg}\n`;
  try { fs.appendFileSync(ATLAS_LOG_PATH, line); } catch { /* ignore */ }
  console.log(msg);
}
import { sharedFrame, tryAcquireSingleInstance, FramePublish, QuadDesc } from './ipc/shared-frame';
import { wpfLink, AtlasQuadFromWpf } from './ipc/wpf-link';
import { placeOut, PlaceUpdate } from './ipc/place-out';

// Win32 bindings for cloaking the atlas window. Without this Edge Overlays
// (and other "list visible top-level windows" tools) pick up our atlas and
// composite it a second time over the VR scene — the user sees two copies of
// the dashies. Honey's browser-host had the same fix; see HANDOVER 11c.
//
// - DWMWA_CLOAK keeps DWM compositing the window (so WGC still captures it)
//   but removes it from the desktop visually.
// - WS_EX_TOOLWINDOW hides it from the taskbar / Alt-Tab / overlay-picker
//   enumerations (Edge Overlays among them).
const user32 = koffi.load('user32.dll');
const dwmapi = koffi.load('dwmapi.dll');
const DwmSetWindowAttribute = dwmapi.func(
  'long __stdcall DwmSetWindowAttribute(void* hwnd, uint32_t dwAttribute, ' +
  'void* pvAttribute, uint32_t cbAttribute)');
const GetWindowLongPtrW = user32.func(
  'intptr_t __stdcall GetWindowLongPtrW(void* hWnd, int nIndex)');
const SetWindowLongPtrW = user32.func(
  'intptr_t __stdcall SetWindowLongPtrW(void* hWnd, int nIndex, intptr_t dwNewLong)');
const DWMWA_CLOAK = 13;
const GWL_EXSTYLE = -20;
const WS_EX_TOOLWINDOW = 0x00000080;

function applyCloakingAndToolWindow(hwnd: bigint): void {
  // koffi 3 round-trips void* as BigInt — pass the HWND straight in.
  // Tool-window first (changes extended style); cloaking is independent.
  // GetWindowLongPtrW returns intptr_t which koffi may surface as Number
  // (small values fit a JS double); normalise to BigInt so OR doesn't throw.
  const exRaw = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as number | bigint;
  const ex = typeof exRaw === 'bigint' ? exRaw : BigInt(exRaw);
  SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex | BigInt(WS_EX_TOOLWINDOW));
  // BOOL TRUE = 4-byte 1.
  const cloak = Buffer.alloc(4);
  cloak.writeUInt32LE(1, 0);
  const hr = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, cloak, 4) as number;
  console.log(`[main] cloak applied hr=0x${(hr >>> 0).toString(16)} ` +
              `exStyleBefore=0x${ex.toString(16)}`);
}

if (started) app.quit();

// Two layers of single-instance enforcement:
// 1. Electron's app-level lock (handles the "user double-clicked the shortcut" case)
// 2. Win32 named mutex shared with future native companion (cross-component)
if (!app.requestSingleInstanceLock()) {
  console.log('[main] another Electron instance owns the app-lock — quitting');
  app.quit();
} else if (!tryAcquireSingleInstance()) {
  console.log('[main] BeeHive_VR named mutex already taken (another component?) — quitting');
  app.quit();
}

// WGC produces R8G8B8A8_UNORM regardless of how the source window is rendered.
// The layer's swapchain format is locked to whatever we publish here.
const ATLAS_FORMAT_DXGI = 28; // DXGI_FORMAT_R8G8B8A8_UNORM

// C3b (4.6.2026): Atlas-Größe ist jetzt das Output eines Packers, nicht mehr
// statisch. Initial-Werte halten Chromium happy bis die erste setAtlasLayout-
// Message ankommt. Layer pollt EnsureSetup bis Electron einen non-zero
// FrameSlot publiziert hat — kein Schaden wenn wir hier mit 256×256 starten.
let atlasWidth  = 256;
let atlasHeight = 256;

// Packer-Konfig: Shelf-Packing wickelt nach PACKER_MAX_WIDTH um. 2048 px ist
// genug für 3-4 typische Widgets (~600 px) nebeneinander; bei mehr Sources
// wachsen weitere Zeilen drunter. Limit existiert nur damit ein einzelner
// 8000-px-Source nicht in 1 Zeile alles aufzieht — der Atlas ist kompakter
// wenn er hochkant wachsen darf.
const PACKER_MAX_WIDTH = 2048;
const DEFAULT_RECT_W   = 512;
const DEFAULT_RECT_H   = 384;
// BrowserWindow-Mindest-Größe (Chromium mag keine 0×0).
const MIN_ATLAS_DIM    = 16;

// WPF authoritatively owns which quads exist — nothing in VR until
// setAtlasLayout arrives with at least one entry.
const currentLayout: QuadDesc[] = [];

// id → target-URL pro Quad. Wird in jedem applyWpfLayout aktualisiert und
// von syncIframes() zur DOM-Konstruktion gelesen. Iframes selbst werden über
// die Source-Id adressiert (sanitisiert für die DOM-id) — separate iframeId-
// Map wie vor C3b ist obsolet, weil der Packer pro Re-Pack neue Slots zuweist
// und es keinen stabilen "p1/p2/p3"-Pool mehr gibt.
const slotTargetById = new Map<string, string>();

// Throttle-Snapshot: was wurde zuletzt ans DOM gepusht? Verhindert dass jeder
// Place-in-VR-Frame (60 Hz) den executeJavaScript-Roundtrip macht.
let lastSyncedDomKey = '';

// C3b: Wunsch-Pixel-Größe pro Quad-Id. Wir packen nur dann neu wenn sich
// (Id-Set ∪ Wunsch-Größen) ändert; reine Pose-Updates (=Place-in-VR-Drag)
// dürfen die rectX/Y/W/H NICHT anfassen, sonst stretcht der Atlas pro Frame.
const currentRectWishById = new Map<string, { w: number; h: number }>();

// Phase 1 (5.6.2026): Place-in-VR-Wächter. Default false → Layer ignoriert
// Controller-Trigger; WPF-Toggle setzt auf true. Wird in jeden FrameSlot
// gespiegelt; Layer liest FrameSlot.placeModeOn (uint32, war reserved).
let currentPlaceModeOn = false;

let currentHwnd: bigint = 0n;
let atlasWindow: BrowserWindow | null = null;

// Race-Schutz: erst syncIframes feuern wenn die Atlas-Page komplett
// geladen ist (DOM mit <iframe id="p1/p2/p3"> existiert). Sonst läuft
// das executeJavaScript ins Leere und der Throttle-Key blockiert
// nachfolgende identische Updates → Iframes bleiben für immer about:blank.
let atlasPageReady = false;

function republish(): void {
  if (currentHwnd === 0n) return;
  const payload: FramePublish = {
    hwnd:        currentHwnd,
    width:       atlasWidth,
    height:      atlasHeight,
    format:      ATLAS_FORMAT_DXGI,
    placeModeOn: currentPlaceModeOn,
  };
  sharedFrame.publishAtlas(payload, currentLayout);
}

// Shelf-Packer (FFDH, naïv aber gut genug für ≤8 Quads): Inputs nach Höhe
// absteigend sortieren, Zeile für Zeile von links nach rechts füllen bis
// PACKER_MAX_WIDTH überschritten. Output ist die Region pro Id plus die
// Atlas-Gesamtgröße. Stabile Reihenfolge nicht garantiert — irrelevant weil
// wir per Id matchen.
interface PackInput  { id: string; rectW: number; rectH: number; }
interface PackOutput { id: string; rectX: number; rectY: number; rectW: number; rectH: number; }
function packShelf(inputs: PackInput[]):
    { rects: PackOutput[]; atlasW: number; atlasH: number } {
  const sorted = inputs.slice().sort((a, b) => b.rectH - a.rectH);
  const rects: PackOutput[] = [];
  let rowY = 0, rowH = 0, cursorX = 0, maxX = 0;
  for (const it of sorted) {
    const w = Math.max(1, Math.floor(it.rectW));
    const h = Math.max(1, Math.floor(it.rectH));
    if (cursorX > 0 && cursorX + w > PACKER_MAX_WIDTH) {
      rowY += rowH; rowH = 0; cursorX = 0;
    }
    rects.push({ id: it.id, rectX: cursorX, rectY: rowY, rectW: w, rectH: h });
    cursorX += w;
    if (h > rowH) rowH = h;
    if (cursorX > maxX) maxX = cursorX;
  }
  const atlasW = Math.max(MIN_ATLAS_DIM, maxX);
  const atlasH = Math.max(MIN_ATLAS_DIM, rowY + rowH);
  return { rects, atlasW, atlasH };
}

// True wenn sich entweder das Id-Set oder eine der Wunsch-Größen ggü.
// currentRectWishById geändert hat — nur dann packen wir neu (und resizen
// das BrowserWindow). Reine Pose-Updates pro Place-in-VR-Frame nicht.
function topologyChanged(quads: AtlasQuadFromWpf[]): boolean {
  if (quads.length !== currentRectWishById.size) return true;
  for (const q of quads) {
    const want = currentRectWishById.get(q.id);
    if (!want) return true;
    const wantW = q.rectW && q.rectW > 0 ? q.rectW : DEFAULT_RECT_W;
    const wantH = q.rectH && q.rectH > 0 ? q.rectH : DEFAULT_RECT_H;
    if (want.w !== wantW || want.h !== wantH) return true;
  }
  return false;
}

function applyWpfLayout(quads: AtlasQuadFromWpf[]): void {
  // ⚠ Diagnose-Log (3.6.2026): zeigt was Atlas-Main empfängt + State des
  // Atlas-Windows. Schreibt ins File damit's auch ohne DevTools sichtbar ist.
  const debug = quads.map(q => `${q.id}:${q.target ?? '<undef>'}`).join(' ');
  const winState = !atlasWindow ? 'NULL'
                  : atlasWindow.isDestroyed() ? 'DESTROYED'
                  : atlasWindow.webContents.isLoading() ? 'LOADING'
                  : 'READY';
  atlasLog(`[applyWpfLayout] win=${winState} quads=${quads.length} ${debug}`);

  const repack = topologyChanged(quads);
  if (repack) {
    // (1) Wunsch-Größen-Snapshot updaten.
    currentRectWishById.clear();
    for (const q of quads) {
      currentRectWishById.set(q.id, {
        w: q.rectW && q.rectW > 0 ? q.rectW : DEFAULT_RECT_W,
        h: q.rectH && q.rectH > 0 ? q.rectH : DEFAULT_RECT_H,
      });
    }
    // (2) Packer aufrufen.
    const packInputs: PackInput[] = quads.map(q => ({
      id: q.id,
      rectW: q.rectW && q.rectW > 0 ? q.rectW : DEFAULT_RECT_W,
      rectH: q.rectH && q.rectH > 0 ? q.rectH : DEFAULT_RECT_H,
    }));
    const { rects, atlasW, atlasH } = packShelf(packInputs);

    // (3) currentLayout neu aufbauen — Pose-Felder werden gleich im Phase-2-
    // Loop unten gesetzt. Hier nur Rect + Identität.
    currentLayout.length = 0;
    for (const r of rects) {
      currentLayout.push({
        id: r.id, rectX: r.rectX, rectY: r.rectY, rectW: r.rectW, rectH: r.rectH,
        posX: 0, posY: 0, posZ: -1, sizeW: 0.4, sizeH: 0.3,
      });
    }

    // (4) BrowserWindow + atlasWidth/Height auf neue Größe ziehen.
    resizeAtlasWindow(atlasW, atlasH);
    atlasLog(`[applyWpfLayout] repack: atlas=${atlasW}x${atlasH} regions=${rects.length}`);

    // (5) URL-Map + Name-Map auf aktuelle Ids reduzieren.
    for (const id of Array.from(slotTargetById.keys())) {
      if (!quads.some(q => q.id === id)) slotTargetById.delete(id);
    }
  } else {
    // Pose-Only-Update: currentLayout in der Größe stabil, nur Pose/Vis-Felder
    // gleich unten geupdated. Atlas-Window-Größe unverändert → kein republish-
    // mit-neuer-Größe nötig (republish() schickt eh den aktuellen atlasWidth).
  }

  // Phase 2: in jedem Fall Pose / Quat / Size / Visibility / Opacity / Target
  // pro Quad aus den Eingangs-DTOs in den Slot kopieren.
  for (const q of quads) {
    const slot = currentLayout.find(s => s.id === q.id);
    if (!slot) continue;
    slot.posX  = q.posX;  slot.posY  = q.posY;  slot.posZ  = q.posZ;
    slot.quatX = q.quatX; slot.quatY = q.quatY; slot.quatZ = q.quatZ; slot.quatW = q.quatW;
    slot.sizeW = q.sizeW; slot.sizeH = q.sizeH;
    slot.visible = q.visible;
    slot.opacity = q.opacity ?? 1.0;
    if (q.target) slotTargetById.set(q.id, q.target);
  }

  console.log(`[main] WPF layout applied: ${quads.length} quad(s), live=${currentLayout.length}, repack=${repack}`);
  republish();
  syncIframes();
}

// Sanitiziert eine Source-Id zum DOM-id-tauglichen String (Buchstaben/Zahlen/
// Bindestrich/Unterstrich). Source-Ids sind heute GUIDs, brauchen aber den
// `q-`-Prefix damit sie nicht mit Ziffer anfangen.
function sourceIdToDomId(srcId: string): string {
  return 'q-' + srcId.replace(/[^a-zA-Z0-9_-]/g, '_');
}

// C3b: baut/aktualisiert die Iframe-DOM-Struktur. Pro currentLayout-Slot ein
// .panel-Container mit absoluter Pixel-Positionierung; darin ein <iframe> mit
// src=slotTargetById. Container die nicht mehr in currentLayout sind werden
// entfernt. Throttled: identische DOM-Beschreibung → kein executeJavaScript.
function syncIframes(): void {
  if (!atlasWindow || atlasWindow.isDestroyed()) return;
  if (!atlasPageReady) {
    atlasLog('[syncIframes] skipped — page not ready yet');
    return;
  }

  interface IframeSpec { domId: string; rectX: number; rectY: number; rectW: number; rectH: number; url: string; }
  const specs: IframeSpec[] = [];
  for (const slot of currentLayout) {
    const url = slotTargetById.get(slot.id) ?? 'about:blank';
    specs.push({
      domId: sourceIdToDomId(slot.id),
      rectX: slot.rectX, rectY: slot.rectY, rectW: slot.rectW, rectH: slot.rectH,
      url,
    });
  }
  // DOM-Key: ändert sich bei Add/Remove/Resize/URL-Wechsel; bleibt stabil
  // wenn nur Pose im Layer mutiert.
  const key = specs
    .map(s => `${s.domId}@${s.rectX},${s.rectY},${s.rectW},${s.rectH}=${s.url}`)
    .sort()
    .join('|');
  if (key === lastSyncedDomKey) return;

  // Reconciler-JS: bekommt die Spec-Liste, löscht .panel die nicht mehr drin
  // sind, legt fehlende an, updated style + src der bestehenden.
  const specsJson = JSON.stringify(specs);
  const js = `(function(specs){
    var root = document.getElementById('atlas-root');
    if (!root) return;
    var wanted = {};
    for (var i = 0; i < specs.length; i++) wanted[specs[i].domId] = specs[i];
    var existing = root.querySelectorAll('.panel');
    for (var j = 0; j < existing.length; j++) {
      var el = existing[j];
      if (!wanted[el.id]) el.parentNode.removeChild(el);
    }
    for (var k = 0; k < specs.length; k++) {
      var s = specs[k];
      var panel = document.getElementById(s.domId);
      if (!panel) {
        panel = document.createElement('div');
        panel.className = 'panel';
        panel.id = s.domId;
        var f = document.createElement('iframe');
        panel.appendChild(f);
        root.appendChild(panel);
      }
      panel.style.left   = s.rectX + 'px';
      panel.style.top    = s.rectY + 'px';
      panel.style.width  = s.rectW + 'px';
      panel.style.height = s.rectH + 'px';
      var frame = panel.querySelector('iframe');
      if (frame && frame.src !== s.url) frame.src = s.url;
    }
  })(${specsJson});`;

  atlasLog(`[syncIframes] dispatch n=${specs.length} key-bytes=${key.length}`);
  atlasWindow.webContents.executeJavaScript(js)
    .then(() => {
      lastSyncedDomKey = key;
      atlasLog('[syncIframes] ok');
    })
    .catch((e: Error) => {
      atlasLog(`[syncIframes] FAIL: ${e.message}`);
      // key NICHT setzen — beim nächsten applyWpfLayout nochmal versuchen
    });
}

// Atlas-BrowserWindow auf die vom Packer geforderte Größe ziehen + Felder
// updaten. Wird nur bei Topology-Change gerufen (siehe applyWpfLayout). Layer
// erkennt die neue WGC-Source-Größe im nächsten xrEndFrame und re-allokiert
// Swapchain + Compute-Intermediate (Layer-C3b-Task).
function resizeAtlasWindow(newW: number, newH: number): void {
  const w = Math.max(MIN_ATLAS_DIM, newW);
  const h = Math.max(MIN_ATLAS_DIM, newH);
  if (w === atlasWidth && h === atlasHeight) return;
  atlasWidth  = w;
  atlasHeight = h;
  if (atlasWindow && !atlasWindow.isDestroyed()) {
    atlasWindow.setContentSize(w, h);
    atlasLog(`[resizeAtlasWindow] setContentSize ${w}x${h}`);
  }
}

function createCapturedWindow() {
  const win = new BrowserWindow({
    width: atlasWidth,
    height: atlasHeight,
    // C4 Diagnose-Schritt (4.6.2026): erstmal ON-SCREEN sichtbar testen.
    // Off-screen (-9999) lieferte alpha=0 ins HMD — Verdacht: Chromium
    // suppressed Paint bei off-screen Fenstern. On-screen schließt das aus
    // und beweist ob transparent:true selbst funktioniert. Hide-Strategie
    // kommt im nächsten Schritt (Cloak alleine, WS_EX_LAYERED, o.ä.).
    x: 100,
    y: 100,
    show: true,
    // Frameless so WGC's captured client area exactly matches atlasWidth x
    // atlasHeight. With frame: true the Win32 chrome eats ~14 px off each
    // axis, WGC reports the smaller client size to the layer, and quad rects
    // configured against the atlas size run past the swapchain edge →
    // XR_ERROR_SWAPCHAIN_RECT_INVALID stalls iRacing's loader.
    frame: false,
    useContentSize: true,
    title: 'BeeHive_VR Atlas (WGC source)',
    // C4 Alpha-Pfad: transparent BrowserWindow + transparent body bg.
    // Compute-Shader im Layer schleift Alpha 1:1 durch (kein Chroma-Key mehr).
    transparent: true,
    backgroundColor: '#00000000',
    webPreferences: {
      backgroundThrottling: false,
    },
  });
  atlasWindow = win;
  // ⚠ Temporärer Debug-Hebel (3.6.2026): DevTools öffnen wenn env-var gesetzt.
  // Atlas-Window selbst ist off-screen (Alpha-Pfad), DevTools-Fenster ist sichtbar →
  // erlaubt Console + Network + DOM-Inspection.
  // Aktivieren mit: $env:BEEHIVE_ATLAS_DEVTOOLS = "1" vor App-Start.
  if (process.env.BEEHIVE_ATLAS_DEVTOOLS) {
    win.webContents.openDevTools({ mode: 'detach' });
  }
  // Match iRacing's typical 90 Hz HMD rate. WGC samples this window on the
  // compositor's clock — keeping the renderer at HMD rate avoids the visible
  // 60↔90 beat we saw in the offscreen path.
  win.webContents.setFrameRate(90);
  win.webContents.on('render-process-gone', (_e, info) => {
    console.error('[main] renderer gone:', info);
  });
  win.webContents.once('did-finish-load', () => {
    atlasPageReady = true;
    atlasLog('[did-finish-load] DOM ready');
    // Falls in der Zwischenzeit schon Layout-Pushes kamen, jetzt nachholen.
    syncIframes();
    const buf = win.getNativeWindowHandle();
    if (buf.length < 8) {
      console.error('[main] getNativeWindowHandle: expected at least 8 bytes, got', buf.length);
      return;
    }
    currentHwnd = buf.readBigUInt64LE(0);
    console.log(`[main] HWND=0x${currentHwnd.toString(16)} — publishing for layer`);
    // C4 Alpha-Pfad Schritt 2: Cloak + WS_EX_TOOLWINDOW wieder rein. Der
    // 2.6.-Black-Screen-Bug mit Cloak+transparent ist mit aktuellem Setup
    // neu zu prüfen — viele Variablen seither geändert (Atlas-Auto-Start,
    // syncIframes, URL-Mapping, Compute-Shader-Setup). Wenn's wieder
    // schwarz wird, weichen wir auf WS_EX_TOOLWINDOW alleine aus.
    try {
      applyCloakingAndToolWindow(currentHwnd);
    } catch (e) {
      console.error('[main] cloak/tool-window failed:', e);
    }
    republish();
  });

  // MAIN_WINDOW_VITE_* are injected by @electron-forge/plugin-vite (see forge.env.d.ts).
  if (MAIN_WINDOW_VITE_DEV_SERVER_URL) {
    win.loadURL(MAIN_WINDOW_VITE_DEV_SERVER_URL);
  } else {
    win.loadFile(path.join(__dirname, `../renderer/${MAIN_WINDOW_VITE_NAME}/index.html`));
  }
}

app.whenReady().then(() => {
  console.log('[main] electron', process.versions.electron, 'chrome', process.versions.chrome);
  try {
    sharedFrame.open();
    console.log('[main] shared frame channel opened (Local\\BeeHiveVR_Frame)');
  } catch (e) {
    console.error('[main] failed to open shared frame channel:', e);
    app.quit();
    return;
  }

  // WPF pipe — non-blocking. If WPF is not running yet, the client will
  // retry every second until it shows up.
  wpfLink.on('connect', () => { wpfLink.sayHello(); });
  wpfLink.on('atlasLayout', (quads: AtlasQuadFromWpf[]) => applyWpfLayout(quads));
  // Phase 1: Place-in-VR-Toggle aus WPF. Edge-Log + sofortiger republish
  // damit der Layer im nächsten xrEndFrame den neuen Flag sieht.
  wpfLink.on('placeMode', (m: { on: boolean; id?: string }) => {
    if (m.on !== currentPlaceModeOn) {
      currentPlaceModeOn = m.on;
      atlasLog(`[placeMode] ${m.on ? 'ON' : 'OFF'}${m.id ? ` id=${m.id}` : ''}`);
      republish();
    }
  });
  wpfLink.start();

  // Place-in-VR: layer publishes pose updates while a controller-grab is
  // active; we forward each generation to WPF over the existing pipe. The
  // mapping only exists once iRacing is running and the layer is past its
  // setup-holdoff, so the reader just polls quietly until then.
  placeOut.on('placeUpdate', (u: PlaceUpdate) => {
    // JSON keys match WPF's EngineLink.PlaceUpdate parser (legacy field names
    // — x/y/z/yaw/pitch/scale/opacity). `scale` carries sizeW; sizeH is
    // implicitly proportional via WPF's aspect handling.
    wpfLink.send({
      type:    'placeUpdate',
      id:      u.id,
      x:       u.posX,
      y:       u.posY,
      z:       u.posZ,
      yaw:     u.yawDeg,
      pitch:   u.pitchDeg,
      scale:   u.sizeW,
      // B10: Layer carried opacity jetzt in PlaceOut (ALT-Drag schreibt
      // m_dragOpacity). WPF EngineLink-Parser FOpt erkennt das Feld und
      // setzt src.Opacity → Slider folgt live.
      opacity: u.opacity,
    });
  });
  placeOut.start();

  createCapturedWindow();
});

app.on('before-quit', () => {
  try { placeOut.stop(); } catch { /* ignore */ }
  try { wpfLink.stop(); } catch { /* ignore */ }
  try { sharedFrame.close(); } catch { /* ignore */ }
});

app.on('window-all-closed', () => app.quit());
