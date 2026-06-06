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

import { app, BrowserWindow, desktopCapturer } from 'electron';
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
// 3-MB-Cap mit einmaliger Rotation auf .old. Per-Write-Stat ist akzeptabel
// — Log-Volume ist im Hundertstel-Sekunden-Bereich, kein Hot-Path.
const ATLAS_LOG_MAX_BYTES = 3 * 1024 * 1024;
function atlasLog(msg: string): void {
  const line = `${new Date().toISOString()} ${msg}\n`;
  try {
    try {
      const stat = fs.statSync(ATLAS_LOG_PATH);
      if (stat.size + line.length > ATLAS_LOG_MAX_BYTES) {
        const oldPath = ATLAS_LOG_PATH + '.old';
        try { fs.unlinkSync(oldPath); } catch { /* nicht vorhanden, OK */ }
        try { fs.renameSync(ATLAS_LOG_PATH, oldPath); } catch { /* ignore */ }
      }
    } catch { /* statSync wirft wenn Datei noch nicht da → erster Write erzeugt sie */ }
    fs.appendFileSync(ATLAS_LOG_PATH, line);
  } catch { /* ignore */ }
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
// Phase 3 (5.6.2026): Sicherheits-Streifen zwischen Quads + zum Atlas-Rand.
// OpenXR-Compositor sampelt bilinear an der Quad-Grenze und kann Border-Pixel
// vom Nachbar-Quad einlesen → weißer Bleed auf der falschen Seite. Mit
// transparenten Gap-Pixeln zwischen den Rects bekommt der Sampler höchstens
// alpha=0 zu fressen, kein Farbüberlauf.
const ATLAS_QUAD_GAP_PX = 10;

// WPF authoritatively owns which quads exist — nothing in VR until
// setAtlasLayout arrives with at least one entry.
const currentLayout: QuadDesc[] = [];

// id → target-URL pro Quad. Wird in jedem applyWpfLayout aktualisiert und
// von syncIframes() zur DOM-Konstruktion gelesen. Iframes selbst werden über
// die Source-Id adressiert (sanitisiert für die DOM-id) — separate iframeId-
// Map wie vor C3b ist obsolet, weil der Packer pro Re-Pack neue Slots zuweist
// und es keinen stabilen "p1/p2/p3"-Pool mehr gibt.
const slotTargetById = new Map<string, string>();

// Phase 3 (5.6.2026): User-vergebener Source-Name pro Quad (z.B. „Relative
// Dashie"). syncIframes rendert ihn als Sticker am Quad; Sichtbarkeit toggle
// über currentHoveredId-Klasse.
const slotNameById = new Map<string, string>();

// C6 (5.6.2026): Subtyp pro Quad. "browser" → DOM-Element ist <iframe> mit
// src=target. "window" → DOM-Element ist <video> mit MediaStream aus
// desktopCapturer. Default (fehlt/null) = "browser" für Rückwärts-Kompat.
const slotTypeById = new Map<string, string>();

// C6 (5.6.2026): WPF-gegebener Visible-Flag pro Slot (vor iconic-Maskierung).
// Brauchen wir um beim de-Minimieren wieder auf den User-Wunsch zurückzukehren
// ohne dass der WPF-Push verloren geht.
const wpfVisibleById = new Map<string, boolean>();

// C6 (5.6.2026): Iconic-State pro Slot. true = Quell-Fenster ist minimiert,
// Slot wird in syncIframes übersprungen (Panel weg, Stream stoppt) und im
// FrameSlot.visible auf false gespiegelt (Layer rendert nichts). Aktualisiert
// im 3-s-Refresh-Takt: Title fehlt in desktopCapturer-Liste → iconic.
const iconicById = new Map<string, boolean>();

// Spiegelt wpfVisibleById ∧ ¬iconicById auf currentLayout[i].visible.
function applyEffectiveVisibility(): void {
  for (const slot of currentLayout) {
    const wpf = wpfVisibleById.get(slot.id) ?? true;
    const minimized = iconicById.get(slot.id) ?? false;
    slot.visible = wpf && !minimized;
  }
}

// C6: Title → desktopCapturer-sourceId Cache. WPF schickt den Fenstertitel als
// `target`; Electron muss daraus die opake sourceId resolven die getUserMedia
// braucht. Periodischer Refresh (3 s) hält den Cache aktuell — Fenster die
// neu aufgehen oder ihren Titel ändern werden automatisch eingesammelt. Bei
// Cache-Miss rendert syncIframes ein schwarzes Placeholder-Video; sobald der
// Refresh die ID kennt, baut die nächste syncIframes-Runde den Stream.
const windowSourceIdByTitle = new Map<string, string>();
let windowRefreshTimer: NodeJS.Timeout | null = null;

async function refreshWindowSources(): Promise<void> {
  try {
    const sources = await desktopCapturer.getSources({
      types: ['window'],
      thumbnailSize: { width: 0, height: 0 }, // Thumbnails brauchen wir nicht
      fetchWindowIcons: false,
    });
    const prevKeys = new Set(windowSourceIdByTitle.keys());
    let changed = false;
    windowSourceIdByTitle.clear();
    for (const s of sources) {
      windowSourceIdByTitle.set(s.name, s.id);
      if (!prevKeys.has(s.name)) changed = true;
      prevKeys.delete(s.name);
    }
    if (prevKeys.size > 0) changed = true; // Fenster verschwunden
    // C6 (5.6.2026): Iconic-Erkennung via ABSENZ aus desktopCapturer-Liste.
    // Hintergrund: Electron-Doku — "On Windows, getSources() excludes
    // minimized windows from the result." Damit ist die Title-Cache-Lookup
    // unten die ganze Wahrheit: Title noch in Cache → Fenster sichtbar;
    // Title nicht mehr in Cache → minimiert (oder geschlossen, was wir hier
    // gleich behandeln). Spart IsIconic-Win32-Aufruf.
    // Edge-Case: Title-Flicker (z.B. Notepad „* – …") triggert kurz
    // false-positive iconic → 3-s-Hide-Cycle. Akzeptiert.
    let iconicChanged = false;
    const seenIconic = new Set<string>();
    for (const slot of currentLayout) {
      if (slotTypeById.get(slot.id) !== 'window') continue;
      const title = slotTargetById.get(slot.id);
      if (!title) continue;
      seenIconic.add(slot.id);
      const minimized = !windowSourceIdByTitle.has(title);
      if (iconicById.get(slot.id) !== minimized) {
        iconicById.set(slot.id, minimized);
        iconicChanged = true;
      }
    }
    // Slots die nicht mehr da sind aus iconicById raus.
    for (const id of Array.from(iconicById.keys())) {
      if (!seenIconic.has(id)) { iconicById.delete(id); iconicChanged = true; }
    }

    if (changed || iconicChanged) {
      atlasLog(`[refreshWindowSources] n=${sources.length}`);
      applyEffectiveVisibility();
      republish();
      syncIframes();
    }
  } catch (e) {
    atlasLog(`[refreshWindowSources] FAIL: ${(e as Error).message}`);
  }
}

function startWindowSourceRefresh(): void {
  if (windowRefreshTimer) return;
  void refreshWindowSources();
  windowRefreshTimer = setInterval(() => { void refreshWindowSources(); }, 3000);
}

// Phase 3: aktuell gehoveretem/grabbed-Id aus dem Layer (kommt via PlaceOut).
// Triggert syncIframes-Update damit Sticker an/aus geht.
let currentHoveredId = '';

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

// B7 (5.6.2026): monoton steigender Counter. Atlas inkrementiert pro WPF-
// Recenter-Trigger, Layer reagiert beim Wechsel mit Reference-Space-
// Neuaufbau. uint32 → wraparound bei 4 Mrd Klicks ist irrelevant.
let currentRecenterEpoch = 0;

let currentHwnd = 0n;
let atlasWindow: BrowserWindow | null = null;

// Race-Schutz: erst syncIframes feuern wenn die Atlas-Page komplett
// geladen ist (DOM mit <iframe id="p1/p2/p3"> existiert). Sonst läuft
// das executeJavaScript ins Leere und der Throttle-Key blockiert
// nachfolgende identische Updates → Iframes bleiben für immer about:blank.
let atlasPageReady = false;

// 6.6.2026: Globaler Master-Visible-Switch aus WPF (Menubar-Button oder
// Keybind ToggleOverlays). false → republish liefert leere Quads → Layer
// composed nichts. Layout-State bleibt unverändert, kommt bei Re-On
// sofort zurück. F5-Heartbeat-Republish läuft weiter → Watchdog ruhig.
let currentMasterVisible = true;

function republish(): void {
  if (currentHwnd === 0n) return;
  const payload: FramePublish = {
    hwnd:          currentHwnd,
    width:         atlasWidth,
    height:        atlasHeight,
    format:        ATLAS_FORMAT_DXGI,
    placeModeOn:   currentPlaceModeOn,
    recenterEpoch: currentRecenterEpoch,
  };
  sharedFrame.publishAtlas(payload, currentMasterVisible ? currentLayout : []);
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
  const gap = ATLAS_QUAD_GAP_PX;
  // Erste Zeile startet bei gap (linker + oberer Rand-Streifen). Bei
  // 0 Quads kommt unten MIN_ATLAS_DIM zum Tragen.
  let rowY = gap, rowH = 0, cursorX = gap, maxX = 0;
  for (const it of sorted) {
    const w = Math.max(1, Math.floor(it.rectW));
    const h = Math.max(1, Math.floor(it.rectH));
    if (cursorX > gap && cursorX + w > PACKER_MAX_WIDTH - gap) {
      rowY += rowH + gap; rowH = 0; cursorX = gap;
    }
    rects.push({ id: it.id, rectX: cursorX, rectY: rowY, rectW: w, rectH: h });
    cursorX += w + gap;
    if (h > rowH) rowH = h;
    if (cursorX > maxX) maxX = cursorX;
  }
  // maxX/rowY+rowH zeigen schon hinter den letzten Quad inkl. trailing gap.
  // Nochmal +gap für den rechten/unteren Rand wäre doppelt → einfach so lassen.
  const atlasW = Math.max(MIN_ATLAS_DIM, maxX);
  const atlasH = Math.max(MIN_ATLAS_DIM, rowY + rowH + gap);
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
  const debug = quads.map(q => `${q.id}[${q.type ?? '<no-type>'}]:${q.target ?? '<undef>'}`).join(' ');
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

    // (5) URL-Map + Name-Map + Type-Map auf aktuelle Ids reduzieren.
    for (const id of Array.from(slotTargetById.keys())) {
      if (!quads.some(q => q.id === id)) slotTargetById.delete(id);
    }
    for (const id of Array.from(slotNameById.keys())) {
      if (!quads.some(q => q.id === id)) slotNameById.delete(id);
    }
    for (const id of Array.from(slotTypeById.keys())) {
      if (!quads.some(q => q.id === id)) slotTypeById.delete(id);
    }
    for (const id of Array.from(wpfVisibleById.keys())) {
      if (!quads.some(q => q.id === id)) wpfVisibleById.delete(id);
    }
    for (const id of Array.from(iconicById.keys())) {
      if (!quads.some(q => q.id === id)) iconicById.delete(id);
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
    slot.opacity = q.opacity ?? 1.0;
    wpfVisibleById.set(q.id, q.visible);
    if (q.target) slotTargetById.set(q.id, q.target);
    if (q.name)   slotNameById.set(q.id, q.name);
    if (q.type)   slotTypeById.set(q.id, q.type);
  }
  // slot.visible berechnen aus wpfVisible ∧ ¬iconic (siehe C6).
  applyEffectiveVisibility();

  // C6: Refresh-Loop für Window-Sources nur starten wenn mindestens eine
  // Window-Source aktiv ist. Spart die desktopCapturer-Abfrage solange nur
  // Browser-Sources im Layout sind.
  if (quads.some(q => q.type === 'window')) startWindowSourceRefresh();

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

  interface IframeSpec {
    domId: string; rectX: number; rectY: number; rectW: number; rectH: number;
    kind: 'browser' | 'window';
    url: string;          // nur relevant für kind=browser
    sourceId: string;     // nur relevant für kind=window (desktopCapturer-id)
    title: string;        // nur relevant für kind=window (Diagnose-Label)
    name: string;
    isActive: boolean;
  }
  const specs: IframeSpec[] = [];
  for (const slot of currentLayout) {
    // C6: invisible (User-Toggle oder iconic-Quell-Fenster) → Slot bleibt im
    // Atlas-Packer (Rect bleibt allokiert) aber Panel im DOM wird entfernt
    // und der MediaStream gestoppt. Layer rendert das Quad ohnehin nicht.
    if (!slot.visible) continue;
    const type = slotTypeById.get(slot.id) ?? 'browser';
    const target = slotTargetById.get(slot.id) ?? '';
    const name = slotNameById.get(slot.id) ?? '';
    if (type === 'window') {
      const sourceId = windowSourceIdByTitle.get(target) ?? '';
      specs.push({
        domId: sourceIdToDomId(slot.id),
        rectX: slot.rectX, rectY: slot.rectY, rectW: slot.rectW, rectH: slot.rectH,
        kind: 'window',
        url: '',
        sourceId,
        title: target,
        name,
        isActive: slot.id === currentHoveredId,
      });
    } else {
      specs.push({
        domId: sourceIdToDomId(slot.id),
        rectX: slot.rectX, rectY: slot.rectY, rectW: slot.rectW, rectH: slot.rectH,
        kind: 'browser',
        url: target || 'about:blank',
        sourceId: '',
        title: '',
        name,
        isActive: slot.id === currentHoveredId,
      });
    }
  }
  // DOM-Key: ändert sich bei Add/Remove/Resize/URL-Wechsel UND bei
  // Hover-Toggle (Sticker an/aus) UND bei Namenswechsel UND bei
  // Kind/sourceId-Wechsel (Window-Capture wurde gefunden o. verloren);
  // bleibt stabil wenn nur Pose im Layer mutiert.
  const key = specs
    .map(s => `${s.domId}@${s.rectX},${s.rectY},${s.rectW},${s.rectH}=${s.kind}:${s.url}/${s.sourceId}|${s.name}|${s.isActive ? 'A' : '-'}`)
    .sort()
    .join('||');
  if (key === lastSyncedDomKey) return;

  // Reconciler-JS: pro Spec wird ein .panel sichergestellt das (a) das richtige
  // Child-Element trägt (iframe für Browser, video für Window-Capture),
  // (b) auf die richtige Position/Größe gezogen, (c) src/MediaStream/Sticker
  // auf den aktuellen Stand gebracht. Container die nicht mehr in specs sind
  // werden inkl. MediaStream-Stop entfernt.
  const specsJson = JSON.stringify(specs);
  const js = `(function(specs){
    var root = document.getElementById('atlas-root');
    if (!root) return;
    var wanted = {};
    for (var i = 0; i < specs.length; i++) wanted[specs[i].domId] = specs[i];
    var existing = root.querySelectorAll('.panel');
    for (var j = 0; j < existing.length; j++) {
      var el = existing[j];
      if (!wanted[el.id]) {
        // MediaStream sauber stoppen damit der Capture-Pin freigegeben wird,
        // sonst hält Chromium das Quell-Fenster fest.
        var v = el.querySelector('video');
        if (v && v.srcObject) {
          try { v.srcObject.getTracks().forEach(function(t){ t.stop(); }); } catch(_) {}
          v.srcObject = null;
        }
        el.parentNode.removeChild(el);
      }
    }
    for (var k = 0; k < specs.length; k++) {
      var s = specs[k];
      var panel = document.getElementById(s.domId);
      if (!panel) {
        panel = document.createElement('div');
        panel.className = 'panel';
        panel.id = s.domId;
        var sticker = document.createElement('span');
        sticker.className = 'sticker';
        panel.appendChild(sticker);
        root.appendChild(panel);
      }
      panel.style.left   = s.rectX + 'px';
      panel.style.top    = s.rectY + 'px';
      panel.style.width  = s.rectW + 'px';
      panel.style.height = s.rectH + 'px';

      // Kind-Wechsel: altes Child entfernen wenn falscher Typ.
      var currentChild = panel.querySelector('iframe, video');
      if (currentChild) {
        var isVideo = currentChild.tagName === 'VIDEO';
        if ((s.kind === 'window') !== isVideo) {
          if (isVideo && currentChild.srcObject) {
            try { currentChild.srcObject.getTracks().forEach(function(t){ t.stop(); }); } catch(_) {}
          }
          currentChild.parentNode.removeChild(currentChild);
          currentChild = null;
        }
      }

      if (s.kind === 'browser') {
        var frame = currentChild;
        if (!frame) {
          frame = document.createElement('iframe');
          panel.insertBefore(frame, panel.firstChild);
        }
        if (frame.src !== s.url) frame.src = s.url;
      } else {
        // window-Capture: <video> mit MediaStream aus desktopCapturer.
        // Solange sourceId leer ist (Title noch nicht resolved) bleibt das
        // video schwarz; nächste Refresh-Runde triggert syncIframes erneut.
        var video = currentChild;
        if (!video) {
          video = document.createElement('video');
          video.autoplay = true;
          video.muted = true;
          video.playsInline = true;
          video.setAttribute('disablepictureinpicture', '');
          panel.insertBefore(video, panel.firstChild);
        }
        var wantSource = s.sourceId || '';
        // Stream nur stoppen/wechseln wenn ein NEUER non-empty sourceId vorliegt
        // der sich von der aktuellen dataset.sourceId unterscheidet. Leere
        // sourceId (Cache hat den Titel kurz nicht — z.B. desktopCapturer-
        // Refresh-Race, Title-Flicker) lässt den laufenden Stream in Ruhe.
        if (wantSource && video.dataset.sourceId !== wantSource) {
          // Stream wechseln: alten Track stoppen.
          if (video.srcObject) {
            try { video.srcObject.getTracks().forEach(function(t){ t.stop(); }); } catch(_) {}
            video.srcObject = null;
          }
          video.dataset.sourceId = wantSource;
          video.dataset.title = s.title || '';
          // IIFE: var-Loop-Vars (k, s, video, wantSource) sind sonst alle vom
          // Schleifen-Ende geteilt — bei ≥2 neuen Window-Sources im selben
          // syncIframes-Dispatch würden alle .then()-Callbacks im LETZTEN
          // Video-Element landen (Streams vertauscht).
          (function(vid, src, ttl){
            navigator.mediaDevices.getUserMedia({
              audio: false,
              video: { mandatory: {
                chromeMediaSource: 'desktop',
                chromeMediaSourceId: src,
              } }
            }).then(function(stream){
              if (vid.dataset.sourceId !== src) {
                stream.getTracks().forEach(function(t){ t.stop(); });
                return;
              }
              vid.srcObject = stream;
            }).catch(function(err){
              console.warn('[atlas] getUserMedia FAIL for', ttl, err.name, err.message);
            });
          })(video, wantSource, s.title);
        }
      }

      var sticker2 = panel.querySelector('.sticker');
      if (sticker2) {
        if (sticker2.textContent !== s.name) sticker2.textContent = s.name;
        sticker2.className = s.isActive ? 'sticker is-active' : 'sticker';
      }
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
  // F5 (6.6.2026): WPF-Crash/-Exit → Layout leeren und republishen.
  // FrameSlot.quadCount=0 + Heartbeat-Generation-Bump teilen dem Layer
  // mit „Publisher noch da, aber keine Quads" → Quads verschwinden sofort
  // statt eingefroren stehen zu bleiben. Bei WPF-Reconnect kommt der
  // erste setAtlasLayout sofort und füllt das Layout wieder.
  wpfLink.on('disconnect', () => {
    if (currentLayout.length === 0) return; // already empty, nothing to do
    atlasLog('[wpf-link] disconnect → clearing atlas layout');
    currentLayout = [];
    republish();
  });
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
  // B7 (5.6.2026): Recenter-Request aus WPF. Counter +1 → republish → Layer
  // sieht im nächsten xrEndFrame ein anderes recenterEpoch und baut den
  // Reference-Space neu auf.
  wpfLink.on('recenter', () => {
    currentRecenterEpoch = (currentRecenterEpoch + 1) >>> 0;
    atlasLog(`[recenter] epoch=${currentRecenterEpoch}`);
    republish();
  });
  wpfLink.on('masterVisible', (visible: boolean) => {
    if (visible === currentMasterVisible) return;
    currentMasterVisible = visible;
    atlasLog(`[masterVisible] ${visible ? 'ON' : 'OFF'}`);
    republish();
  });
  wpfLink.start();

  // F5 (6.6.2026): Heartbeat-Republish. republish() bumpt FrameSlot.generation
  // pro Aufruf — der Layer benutzt das als Liveness-Signal (siehe Watchdog
  // in layer.cpp). Ohne Heartbeat würde Atlas im Idle aussehen wie Atlas-tot,
  // weil regulärer republish() nur bei State-Change feuert. 250 ms ist
  // großzügig genug damit Layer-Threshold (≈60 Frames bei 90 Hz ≈ 0.7 s)
  // mit Sicherheits-Marge greift, klein genug damit Quads bei Atlas-Crash
  // innerhalb von ~1 s verschwinden.
  setInterval(() => {
    if (currentHwnd !== 0n) republish();
  }, 250);

  // Place-in-VR: layer publishes pose updates while a controller-grab is
  // active; we forward each generation to WPF over the existing pipe. The
  // mapping only exists once iRacing is running and the layer is past its
  // setup-holdoff, so the reader just polls quietly until then.
  placeOut.on('placeUpdate', (u: PlaceUpdate) => {
    // Phase 3 (5.6.2026): Hover/Grab-Id lokal mitlesen + an Atlas-Sticker
    // weiterreichen. Wechsel triggert syncIframes (nur DOM-Update wenn key
    // sich ändert — kein Spam).
    if (u.hoveredId !== currentHoveredId) {
      currentHoveredId = u.hoveredId;
      atlasLog(`[hoveredId] "${u.hoveredId}"`);
      syncIframes();
    }
    // JSON keys match WPF's EngineLink.PlaceUpdate parser (legacy field names
    // — x/y/z/yaw/pitch/scale/opacity). `scale` carries sizeW; sizeH is
    // implicitly proportional via WPF's aspect handling.
    wpfLink.send({
      type:      'placeUpdate',
      id:        u.id,
      x:         u.posX,
      y:         u.posY,
      z:         u.posZ,
      yaw:       u.yawDeg,
      pitch:     u.pitchDeg,
      scale:     u.sizeW,
      // B10: Layer carried opacity jetzt in PlaceOut (ALT-Drag schreibt
      // m_dragOpacity). WPF EngineLink-Parser FOpt erkennt das Feld und
      // setzt src.Opacity → Slider folgt live.
      opacity:   u.opacity,
      // Phase 3: stabilisierte Hover/Grab-Id für WPF-Pille-Highlight.
      hoveredId: u.hoveredId,
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
