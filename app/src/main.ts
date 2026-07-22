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

import { app, BrowserWindow, desktopCapturer, powerSaveBlocker, screen as electronScreen } from 'electron';
import path from 'node:path';
import fs from 'node:fs';
import os from 'node:os';
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
// 7.7.2026: Lokalzeit statt toISOString()/UTC, damit atlas.log 1:1 zu den
// C++-Logs (stalls.log/engine.log, localtime_s) passt — sonst 2h-Falle beim
// Freeze-Cross-Ref. Format YYYY-MM-DD HH:MM:SS.mmm (ms nur für atlas-interne
// Reihenfolge; beim Vergleich mit stalls.log einfach abschneiden).
function localTs(d: Date): string {
  const p = (n: number, w = 2): string => String(n).padStart(w, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ` +
         `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
}
function atlasLog(msg: string): void {
  const line = `${localTs(new Date())} ${msg}\n`;
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
import { atlasTexIn } from './ipc/atlas-texin';
import { winSrc } from './ipc/win-src';
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
// 18.7.2026: Billige Fenster-Zustandsabfrage statt desktopCapturer-Enumeration.
// Trace-Befund: `getSources()` alle 3 s erzeugte einen Kernel-GPU-Burst
// (System-Prozess ~4-5k Submissions/s, Atlas 15×) und kippte gelegentlich den
// ganzen Adapter → Freeze. IsIconic/IsWindow kosten praktisch nichts.
const IsIconic = user32.func('bool __stdcall IsIconic(void* hWnd)');
const IsWindow = user32.func('bool __stdcall IsWindow(void* hWnd)');

const DWMWA_CLOAK = 13;
const GWL_EXSTYLE = -20;
const WS_EX_TOOLWINDOW = 0x00000080;

// desktopCapturer-Window-Ids haben die Form "window:<HWND>:<Z>" — daraus die
// HWND ziehen, damit wir sie direkt per Win32 abfragen können.
function hwndFromSourceId(sourceId: string): bigint | null {
  const m = /^window:(\d+):/.exec(sourceId);
  if (!m) return null;
  try { return BigInt(m[1]); } catch { return null; }
}

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

// 16.6.2026: Chromium-CLI-Switches gegen Windows-Background-Throttling. Atlas
// ist DWM-cloaked, hat also keine sichtbare Window-Präsenz. Wenn WPF parallel
// minimiert/im-Tray ist (typischer Workflow), klassifiziert Windows den
// gesamten BeeHive_VR_Atlas-Prozess als Background → setInterval-Timer
// gestreckt, F5-Heartbeat zu selten → Watchdog trippt grundlos. Diese
// Switches halten den Main-Process-Timer und Renderer auf Vordergrund-Niveau.
// 13.7.2026 — SWITCHES-ROLLBACK-EXPERIMENT (Freeze-Diagnose): Die 3 Switches
// nahmen Chromium ALLE Bremsen — Hauptverdächtiger als Verstärker der gemessenen
// Atlas-Eruptionen (10-30x Submission-Stürme → DWM-Sturm → adapter-weiter
// WDDM-Stall; Freezes begannen zeitgleich mit den Switches, 16.6.; der A/B am
// 1.7. zeigte ohne Switches seltenere Freezes). Ersatz fürs Heartbeat-Problem
// (der Grund für die Switches): powerSaveBlocker unten in app.whenReady() —
// verhindert gezielt die App-Drosselung, ohne Chromiums Render-Bremsen global
// zu lösen. webPreferences.backgroundThrottling:false am BrowserWindow bleibt
// (Widget-Renderer laufen weiter). Falls F5-Watchdog wieder grundlos trippt
// (alle Overlays blinken gleichzeitig weg): Heartbeat-Härtung nachlegen,
// NICHT die Switches zurückholen.

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

// D2 (OSR-Pivot): OSR (offscreen+useSharedTexture) ist jetzt DEFAULT (15.7.2026).
// Der Atlas rendert ohne sichtbares Fenster; setFrameRate deckelt den Compositor
// (BEEHIVE_ATLAS_FPS, Default 30, wie C's Boden). Kill-Switch: BEEHIVE_ATLAS_OSR=0
// → zurück auf den alten WGC-Fenster-Pfad (bleibt für Window-Capture + Fallback).
// Nur der Wert "0" schaltet ab; unset oder alles andere = OSR.
const OSR_MODE = process.env.BEEHIVE_ATLAS_OSR !== '0';

// Window-Capture-Entkopplung (19.7.2026): Window-Quads werden vom capture-host
// nativ per WGC auf die HWND gecaptured (er sieht auch Overlay-/Tool-Fenster, die
// Chromiums desktopCapturer verschweigt) und direkt in den Ring komponiert. Der
// Atlas baut dafür dann KEIN <video>/getUserMedia mehr — das spart den teuren
// getSources-Auflösungsversuch (Ruckeln beim Start) und die 18-fps-Hover-Regression.
// Kill-Switch: BEEHIVE_WINCAP_NATIVE=0 → alter desktopCapturer-Pfad.
const WINCAP_NATIVE = process.env.BEEHIVE_WINCAP_NATIVE !== '0';
const ATLAS_FPS = (() => {
  const n = parseInt(process.env.BEEHIVE_ATLAS_FPS || '', 10);
  return Number.isFinite(n) && n > 0 ? n : 30;
})();

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
//
// ⚠ MUSS `let` bleiben (NICHT zurück auf const!): der wpf-link-`disconnect`-Handler
// weist hier komplett neu zu (`currentLayout = []`). Als `const` warf das zur
// Laufzeit `TypeError: Assignment to constant variable` — und zwar GENAU dann, wenn
// die WPF verschwindet. Der TypeError blockierte den Main-Thread (Electron-Fehler-
// Modal), sodass der `--parent-pid`-Watchdog nie zu seinem `app.exit(0)` kam →
// Symptom „Atlas bleibt nach WPF-Ende in der Taskleiste hängen" (18.7.2026).
// Lag lange latent, weil der Vite/esbuild-Build NICHT typecheckt (tsc sähe TS2588).
let currentLayout: QuadDesc[] = [];

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

// Window-Capture-Entkopplung (19.7.2026): id → HWND des Ziel-Fensters, von der
// WPF per EnumWindows aufgelöst und im Layout-Push mitgeliefert. 0n = unaufgelöst.
// Publiziert via WinSrc-SHM an den capture-host (native WGC-Capture).
const slotHwndById = new Map<string, bigint>();

// Läuft dieses Window-Quad über den nativen WGC-Pfad (capture-host) statt über
// Chromiums desktopCapturer? Ausnahme: die WPF-Self-Quelle im Display-Crop-Modus —
// die MUSS beim desktopCapturer bleiben, weil sie den ganzen Monitor captured und
// zuschneidet (WPF-Kontextmenüs/Dropdowns sind eigene Popup-HWNDs und fehlen in
// einer reinen Fenster-Capture). Sonst würde der capture-host das Rect mit der
// popup-losen Variante überschreiben.
function usesNativeWindowCapture(target: string | undefined): boolean {
  if (!WINCAP_NATIVE) return false;
  if (WPF_SELF_DISPLAY_MODE && target === WPF_SELF_TITLE) return false;
  return true;
}

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

// 7.6.2026: WPF-Self-Source Iconic-Sync — wenn WPF minimiert ist, wird die
// WPF-Self-Source (target=WPF_SELF_TITLE) als iconic markiert, sonst freigegeben.
// Wirkt parallel zum normalen desktopCapturer-Absenz-Mechanismus für andere
// Window-Sources.
function applyWpfSelfIconic(): void {
  let changed = false;
  const minimized = wpfBounds?.minimized ?? false;
  for (const slot of currentLayout) {
    if (slotTypeById.get(slot.id) !== 'window') continue;
    if (slotTargetById.get(slot.id) !== WPF_SELF_TITLE) continue;
    if (iconicById.get(slot.id) !== minimized) {
      iconicById.set(slot.id, minimized);
      changed = true;
    }
  }
  if (changed) { applyEffectiveVisibility(); republish(); }
}

// C6: Title → desktopCapturer-sourceId Cache. WPF schickt den Fenstertitel als
// `target`; Electron muss daraus die opake sourceId resolven die getUserMedia
// braucht. Periodischer Refresh (3 s) hält den Cache aktuell — Fenster die
// neu aufgehen oder ihren Titel ändern werden automatisch eingesammelt. Bei
// Cache-Miss rendert syncIframes ein schwarzes Placeholder-Video; sobald der
// Refresh die ID kennt, baut die nächste syncIframes-Runde den Stream.
const windowSourceIdByTitle = new Map<string, string>();
// 18.7.2026: kein 3-s-getSources-Poll mehr (Freeze-Ursache, s.o.). Stattdessen ein
// billiger IsIconic-Poll; getSources läuft nur noch ON DEMAND (unaufgelöster Titel
// oder HWND ungültig geworden) und dann frühestens alle SOURCES_REFRESH_MIN_MS.
let windowIconicTimer: NodeJS.Timeout | null = null;
let lastSourcesRefreshMs = 0;
const SOURCES_REFRESH_MIN_MS = 5000;
// 19.7.2026: Ein Titel, der sich NIE auflösen lässt (Fenster taucht in Chromiums
// Enumeration gar nicht auf), hielt `needResolve` dauerhaft true → getSources()
// feuerte endlos alle 5 s = exakt der teure Burst von vorher, nur langsamer
// getaktet (User-Symptom: Overlays stocken alle ~5 s). Deshalb Backoff:
// bei jedem Fehlversuch verdoppeln bis SOURCES_REFRESH_MAX_MS, bei Erfolg oder
// Layout-Wechsel zurücksetzen.
const SOURCES_REFRESH_MAX_MS = 60000;
let sourcesRefreshBackoffMs = SOURCES_REFRESH_MIN_MS;
let loggedMissingKey = '';

// 7.6.2026 — WPF-Self-Capture via Display-Mode + CSS-Crop. ContextMenus &
// ComboBox-Dropdowns rendern in WPF als separate Popup-HWNDs und fehlen im
// Window-Capture-Mode (Chromium's window-source erfasst nur das Target-HWND).
// Workaround: für die WPF-Self-Source (target = WPF_SELF_TITLE) schalten wir
// auf Screen-Capture des betroffenen Monitors um und cropen das Video per
// CSS-overflow:hidden + negative position auf den Window-Rect.
const WPF_SELF_TITLE = 'BeeHive VR';   // = AppEdition.ProductName
// 17.6.2026 — Perf: Display-Capture dupliziert den GANZEN Monitor pro Frame
// (Full-Monitor-DXGI-Duplication + CSS-Crop) → teurer Video-Decode, treibt die
// Electron-CPU-Last hoch und hungert iRacings Render-Thread aus (~12 FPS / +22
// CPU-Punkte in der PresentMon-Messung). Auf false fällt WPF-Self auf das
// strukturell billigere Window-Capture zurück. Trade-off: ContextMenus/Dropdowns
// (separate Popup-HWNDs) sind in VR wieder unsichtbar — bewusst akzeptiert.
const WPF_SELF_DISPLAY_MODE = false;
// Cache: Display-ID (electron.screen) → sourceId (desktopCapturer). Wird im
// selben 3-s-Tick mit refreshWindowSources mit-aktualisiert. Display-ID ist
// stabil über den App-Lebenszyklus, sourceId kann sich beim Monitor-
// Reconnect ändern.
const screenSourceIdByDisplayId = new Map<number, string>();
// State von WPF gepusht (left/top/width/height in physical px, minimized).
// null → Display-Crop deaktiviert (WPF noch nicht verbunden), syncIframes
// fällt zurück auf normalen Window-Capture-Pfad.
interface WpfBounds {
  left: number; top: number; width: number; height: number;
  minimized: boolean;
}
let wpfBounds: WpfBounds | null = null;

async function refreshWindowSources(): Promise<void> {
  lastSourcesRefreshMs = Date.now(); // Rate-Limit-Marke (auch bei Fehlschlag)
  try {
    // Beide Types in einem Aufruf — Chromium liefert window UND screen Sources
    // im selben Result-Array, jeder mit display_id (für screen) bzw. window-
    // HWND-Prefix (für window).
    const sources = await desktopCapturer.getSources({
      types: ['window', 'screen'],
      thumbnailSize: { width: 0, height: 0 }, // Thumbnails brauchen wir nicht
      fetchWindowIcons: false,
    });
    const prevKeys = new Set(windowSourceIdByTitle.keys());
    let changed = false;
    windowSourceIdByTitle.clear();
    screenSourceIdByDisplayId.clear();
    for (const s of sources) {
      // Window-Sources: id-Prefix "window:HWND:Z". Screen-Sources: id-Prefix
      // "screen:DISPLAY_ID:0". display_id ist auf screen-Sources non-leer.
      if (s.display_id) {
        const displayId = Number(s.display_id);
        if (Number.isFinite(displayId)) screenSourceIdByDisplayId.set(displayId, s.id);
      } else {
        windowSourceIdByTitle.set(s.name, s.id);
        if (!prevKeys.has(s.name)) changed = true;
        prevKeys.delete(s.name);
      }
    }
    if (prevKeys.size > 0) changed = true; // Fenster verschwunden

    // 18.7.2026: Die frühere Iconic-Erkennung „via ABSENZ aus dieser Liste" ist hier
    // RAUS — sie war der einzige Grund für den 3-s-getSources-Poll, und genau der
    // erzeugte den Kernel-GPU-Burst, der den Adapter kippte (Trace-Beweis 18.7.).
    // Minimiert-Status kommt jetzt aus pollWindowIconic() (IsIconic, praktisch
    // gratis) bzw. für die WPF-Self-Quelle aus applyWpfSelfIconic(). Diese Funktion
    // löst nur noch Titel→sourceId + Screen-Ids auf und läuft ON DEMAND.
    // Aufräumen: iconic-Einträge von Slots, die es nicht mehr gibt.
    let iconicChanged = false;
    const liveIds = new Set(currentLayout.map((s) => s.id));
    for (const id of Array.from(iconicById.keys())) {
      if (!liveIds.has(id)) { iconicById.delete(id); iconicChanged = true; }
    }

    // Auflösungs-Status prüfen → Backoff steuern (s. SOURCES_REFRESH_MAX_MS).
    const wantedTitles = new Set<string>();
    for (const slot of currentLayout) {
      if (slotTypeById.get(slot.id) !== 'window') continue;
      const t = slotTargetById.get(slot.id);
      if (t) wantedTitles.add(t);
    }
    const missing = Array.from(wantedTitles).filter((t) => !windowSourceIdByTitle.has(t));
    if (missing.length === 0) {
      sourcesRefreshBackoffMs = SOURCES_REFRESH_MIN_MS;
      loggedMissingKey = '';
    } else {
      sourcesRefreshBackoffMs = Math.min(sourcesRefreshBackoffMs * 2, SOURCES_REFRESH_MAX_MS);
      // Diagnose (einmal je Konstellation): zeigt sofort, ob es ein Namens-Mismatch
      // ist oder ob Chromiums Enumeration das Fenster gar nicht anbietet (sie filtert
      // u.a. minimierte/layered/tool/cloaked Fenster). OpenKneeboard sieht solche
      // Fenster, weil es per WGC direkt auf die HWND capturet statt zu enumerieren.
      const key = missing.join('|');
      if (key !== loggedMissingKey) {
        loggedMissingKey = key;
        atlasLog(
          `[refreshWindowSources] NICHT aufgelöst: ${missing.map((m) => `"${m}"`).join(', ')} ` +
          `— enumerierte Fenster (${windowSourceIdByTitle.size}): ` +
          (Array.from(windowSourceIdByTitle.keys()).map((n) => `"${n}"`).join(', ') || '<keine>') +
          ` — retry-Abstand jetzt ${sourcesRefreshBackoffMs} ms`);
      }
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

// 18.7.2026 — billiger Ersatz für den 3-s-getSources-Poll: fragt den Minimiert-
// Zustand direkt per IsIconic() an der HWND ab (aus der sourceId geparst). Kostet
// praktisch nichts und erzeugt KEINE GPU-/Kernel-Last. getSources() wird nur noch
// angestoßen, wenn ein Titel unaufgelöst ist oder eine HWND ungültig wurde
// (Fenster zu/neu) — und dann höchstens alle SOURCES_REFRESH_MIN_MS.
function pollWindowIconic(): void {
  let changed = false;
  let needResolve = false;
  for (const slot of currentLayout) {
    if (slotTypeById.get(slot.id) !== 'window') continue;
    const title = slotTargetById.get(slot.id);
    if (!title) continue;
    // WPF-Self-Quelle: iconic kommt aus wpfBounds (applyWpfSelfIconic) — nicht anfassen.
    if (title === WPF_SELF_TITLE) continue;

    // Nativer Pfad: die HWND kommt fertig von der WPF (WindowResolver) im Layout-
    // Push — kein desktopCapturer nötig. Das ist der Grund, warum getSources hier
    // NICHT mehr angestoßen wird (war die Quelle des Ruckelns beim Start).
    let hwnd: bigint | null;
    if (usesNativeWindowCapture(title)) {
      const pushed = slotHwndById.get(slot.id) ?? 0n;
      if (pushed === 0n) {
        // WPF findet das Fenster nicht (zu / Titel geändert) → Quad ausblenden.
        if (iconicById.get(slot.id) !== true) { iconicById.set(slot.id, true); changed = true; }
        continue;
      }
      hwnd = pushed;
    } else {
      const sourceId = windowSourceIdByTitle.get(title);
      hwnd = sourceId ? hwndFromSourceId(sourceId) : null;
      if (hwnd == null) { needResolve = true; continue; }
    }

    if (!IsWindow(hwnd)) {
      // Fenster geschlossen/neu. Im nativen Pfad löst die WPF neu auf (2-s-Timer),
      // im Legacy-Pfad brauchen wir eine frische sourceId.
      if (!usesNativeWindowCapture(title)) {
        windowSourceIdByTitle.delete(title);
        needResolve = true;
      }
      if (iconicById.get(slot.id) !== true) { iconicById.set(slot.id, true); changed = true; }
      continue;
    }
    const minimized = !!IsIconic(hwnd);
    if (iconicById.get(slot.id) !== minimized) {
      iconicById.set(slot.id, minimized);
      changed = true;
    }
  }
  if (changed) { applyEffectiveVisibility(); republish(); syncIframes(); }
  if (needResolve && Date.now() - lastSourcesRefreshMs >= sourcesRefreshBackoffMs) {
    void refreshWindowSources();
  }
}

function startWindowSourceRefresh(): void {
  if (windowIconicTimer) return;
  void refreshWindowSources();                              // einmalig auflösen
  windowIconicTimer = setInterval(pollWindowIconic, 1000);  // danach nur noch billig
}

// Gegenstück (15.7.2026): ohne Window-Capture-Quelle im Layout ist die 3-s-
// desktopCapturer.getSources()-Enumeration reine Verschwendung — und im OSR-Modus
// stallt sie den Paint sichtbar (~alle 3 s kurzes Overlay-Hängen). Der Timer wurde
// bisher gestartet (bei erster Window-Quelle) aber NIE gestoppt → lief endlos weiter.
function stopWindowSourceRefresh(): void {
  if (windowIconicTimer) { clearInterval(windowIconicTimer); windowIconicTimer = null; }
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

// 7.6.2026 — WPF-Self-Source Display-Lookup. Sucht aus electron.screen den
// Display dessen physische Bounds den WPF-Window-Rect enthalten. Liefert
// Screen-SourceId + Crop-Parameter zurück (Monitor-Größe in physical px =
// MediaStream-Auflösung; Crop-Offset = WPF-Position relativ zum Monitor).
// null → kein Display gefunden (z.B. WPF noch nicht-verbunden, oder
// Monitor-Disconnect).
interface WpfCrop {
  sourceId: string;
  monitorW: number;   // physical px = MediaStream width
  monitorH: number;   // physical px = MediaStream height
  cropX: number;      // physical px relative to monitor topleft
  cropY: number;
  cropW: number;      // physical px = panel-size = wpfBounds.width
  cropH: number;
}
function resolveWpfCrop(): WpfCrop | null {
  if (!wpfBounds || wpfBounds.minimized) return null;
  if (wpfBounds.width <= 0 || wpfBounds.height <= 0) return null;
  const displays = electronScreen.getAllDisplays();
  for (const d of displays) {
    // electron.screen liefert bounds + scaleFactor in DIPs. WPF schickt
    // physical px (GetWindowRect ist DPI-aware). Konvertieren wir hier.
    const sf = d.scaleFactor || 1;
    const physLeft = Math.round(d.bounds.x * sf);
    const physTop  = Math.round(d.bounds.y * sf);
    const physW    = Math.round(d.bounds.width  * sf);
    const physH    = Math.round(d.bounds.height * sf);
    // TopLeft des WPF-Fensters muss in diesem Monitor liegen — Multi-Monitor-
    // Spanning ist sehr selten; Dominanz-Monitor reicht.
    if (wpfBounds.left >= physLeft && wpfBounds.left < physLeft + physW
        && wpfBounds.top >= physTop && wpfBounds.top < physTop + physH) {
      const sourceId = screenSourceIdByDisplayId.get(d.id);
      if (!sourceId) return null;
      return {
        sourceId,
        monitorW: physW,
        monitorH: physH,
        cropX: wpfBounds.left - physLeft,
        cropY: wpfBounds.top - physTop,
        cropW: wpfBounds.width,
        cropH: wpfBounds.height,
      };
    }
  }
  return null;
}

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
  // WGC: erst publizieren wenn die HWND bekannt ist (vorher kann der Layer/Helper
  // eh nichts capturen). OSR: es GIBT keine HWND — trotzdem publizieren, denn
  // FrameSlot trägt die Quads/Posen (Bild kommt separat über AtlasTexIn). Ohne das
  // bekämen capture-host + Layer im OSR-Modus nie Quads → keine Overlays.
  if (!OSR_MODE && currentHwnd === 0n) return;
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
    slot.opacity   = q.opacity   ?? 1.0;
    slot.bgOpacity = q.bgOpacity ?? 0.0;
    wpfVisibleById.set(q.id, q.visible);
    if (q.target) slotTargetById.set(q.id, q.target);
    if (q.name)   slotNameById.set(q.id, q.name);
    if (q.type)   slotTypeById.set(q.id, q.type);
    // Window-Capture-Entkopplung: HWND (von der WPF resolved) je Quad merken.
    if (q.type === 'window') slotHwndById.set(q.id, BigInt(q.hwnd ?? 0));
  }
  for (const id of Array.from(slotHwndById.keys())) {
    if (!quads.some(q => q.id === id)) slotHwndById.delete(id);
  }
  // quadId→hwnd an den capture-host publizieren (WinSrc-SHM). hwnd=0 = noch
  // nicht aufgelöst → capture-host lässt das Quad leer, WPF-Resolver pusht nach.
  {
    const entries = quads
      .filter(q => q.type === 'window' && usesNativeWindowCapture(q.target))
      .map(q => ({ id: q.id, hwnd: BigInt(q.hwnd ?? 0) }));
    try { winSrc.publish(entries); } catch (e) { atlasLog('[winsrc] publish error: ' + String(e)); }
    for (const e of entries) {
      atlasLog(`[winsrc] ${e.id} → hwnd=0x${e.hwnd.toString(16)}${e.hwnd === 0n ? ' (unaufgelöst)' : ''}`);
    }
  }
  // slot.visible berechnen aus wpfVisible ∧ ¬iconic (siehe C6).
  applyEffectiveVisibility();

  // C6: Refresh-Loop für Window-Sources nur starten wenn mindestens eine
  // Window-Source aktiv ist. Spart die desktopCapturer-Abfrage solange nur
  // Browser-Sources im Layout sind.
  if (quads.some(q => q.type === 'window')) {
    // Layout-Wechsel: Backoff zurücksetzen, damit neu hinzugekommene Titel zügig
    // versucht werden. Bewusst KEIN sofortiges getSources() hier — applyWpfLayout
    // kommt beim Konfigurieren in Bursts (9 Pushes in 0,7 s beobachtet), das würde
    // genau die teuren Enumerationen auslösen, die wir loswerden wollen. Der 1-s-Poll
    // holt es innerhalb der Backoff-Zeit nach.
    sourcesRefreshBackoffMs = SOURCES_REFRESH_MIN_MS;
    loggedMissingKey = '';
    startWindowSourceRefresh();
  } else stopWindowSourceRefresh();

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
    // 7.6.2026 — WPF-Self-Source Display-Crop. Wenn gesetzt: <video> wird
    // mit Monitor-Auflösung gerendert und per overflow:hidden auf das WPF-
    // Window-Rect maskiert. Sonst (für normale Window-Sources) leeres Objekt.
    crop: { monW: number; monH: number; x: number; y: number } | null;
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
      // 7.6.2026: WPF-Self-Source erkennen → Display-Capture + Crop statt
      // Window-Capture. ContextMenus & ComboBox-Dropdowns sind separate
      // Popup-HWNDs in WPF und fehlen im Window-Capture-Mode.
      // Nativer Pfad: der capture-host captured dieses Fenster selbst per WGC und
      // komponiert es direkt über das Quad-Rect. Der Atlas baut hier deshalb KEINEN
      // Stream mehr — sourceId leer lassen heißt: <video> bleibt ohne getUserMedia
      // (der Reconciler startet nur bei nicht-leerer sourceId). Das DOM-Element
      // bleibt trotzdem bestehen, damit der Namens-Sticker bei Hover weiter geht.
      let sourceId = usesNativeWindowCapture(target)
        ? '' : (windowSourceIdByTitle.get(target) ?? '');
      let crop: IframeSpec['crop'] = null;
      if (WPF_SELF_DISPLAY_MODE && target === WPF_SELF_TITLE) {
        const c = resolveWpfCrop();
        if (c) {
          sourceId = c.sourceId;
          crop = { monW: c.monitorW, monH: c.monitorH, x: c.cropX, y: c.cropY };
        }
        // Falls resolveWpfCrop null liefert (WPF-Bounds noch nicht gepusht,
        // Display-Cache leer) → Fallback auf Window-Capture-Pfad (kein Crop).
      }
      specs.push({
        domId: sourceIdToDomId(slot.id),
        rectX: slot.rectX, rectY: slot.rectY, rectW: slot.rectW, rectH: slot.rectH,
        kind: 'window',
        url: '',
        sourceId,
        title: target,
        name,
        isActive: slot.id === currentHoveredId,
        crop,
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
        crop: null,
      });
    }
  }
  // DOM-Key: ändert sich bei Add/Remove/Resize/URL-Wechsel UND bei
  // Hover-Toggle (Sticker an/aus) UND bei Namenswechsel UND bei
  // Kind/sourceId-Wechsel (Window-Capture wurde gefunden o. verloren);
  // bleibt stabil wenn nur Pose im Layer mutiert.
  const key = specs
    .map(s => {
      const c = s.crop ? `${s.crop.monW}x${s.crop.monH}+${s.crop.x},${s.crop.y}` : '-';
      return `${s.domId}@${s.rectX},${s.rectY},${s.rectW},${s.rectH}=${s.kind}:${s.url}/${s.sourceId}|${s.name}|${s.isActive ? 'A' : '-'}|c=${c}`;
    })
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
        // 7.6.2026: bei s.crop != null (WPF-Self-Source) wechseln wir in
        // Display-Capture-Mode + CSS-Crop — <video> wird auf Monitor-Größe
        // gerendert und durch overflow:hidden am .panel auf den WPF-Rect
        // maskiert. Erfasst dadurch ContextMenus / ComboBox-Dropdowns.
        var video = currentChild;
        if (!video) {
          video = document.createElement('video');
          video.autoplay = true;
          video.muted = true;
          video.playsInline = true;
          video.setAttribute('disablepictureinpicture', '');
          panel.insertBefore(video, panel.firstChild);
        }
        // Crop-Mode: Panel cropt das übergroße Video. Sonst Standard-Fit.
        if (s.crop) {
          panel.style.overflow = 'hidden';
          video.style.position = 'absolute';
          video.style.width  = s.crop.monW + 'px';
          video.style.height = s.crop.monH + 'px';
          video.style.left   = (-s.crop.x) + 'px';
          video.style.top    = (-s.crop.y) + 'px';
          video.style.objectFit = 'none';
        } else {
          panel.style.overflow = '';
          video.style.position = '';
          video.style.width = video.style.height = '';
          video.style.left = video.style.top = '';
          video.style.objectFit = '';
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

// 17.6.2026 — Perf: Electron-Prozesse (Main + GPU + jeder Renderer) auf
// BELOW_NORMAL. iRacings Render-Thread ist an vollen Grids CPU-bound; ohne
// Priorität konkurrieren BeeHives Prozesse um dieselben Kerne (PresentMon:
// +14 CPU-Punkte gesamt bei nur +1 ms iRacing-CPU-Busy = Kontention) → längere
// FPS-Drops. BELOW_NORMAL lässt Windows iRacing an den Engstellen Vorrang geben,
// BeeHive weicht zurück statt zu konkurrieren. getAppMetrics() liefert alle
// Electron-PIDs; periodisch nachgezogen, weil Renderer-Prozesse erst beim Laden
// der Dashies/Sources entstehen. Auf false zum Vergleichsmessen.
const LOW_PRIORITY = true;
let lowPriorityTimer: ReturnType<typeof setInterval> | null = null;
function applyLowPriority(): void {
  if (!LOW_PRIORITY) return;
  const below = os.constants.priority.PRIORITY_BELOW_NORMAL;
  for (const m of app.getAppMetrics()) {
    try { os.setPriority(m.pid, below); } catch { /* Prozess weg / kein Zugriff */ }
  }
}
function startLowPriority(): void {
  if (lowPriorityTimer) return;
  applyLowPriority();
  lowPriorityTimer = setInterval(applyLowPriority, 5000);
}

// D2 Phase 1 — OSR-Capture: publiziert pro paint den Shared-Texture-Handle an den
// capture-host (Local\BeeHiveVR_AtlasTexIn) und verwaltet den Textur-Lifecycle.
// Chromium hat einen 10er-Frame-Pool; wir halten je Textur bis der capture-host sie
// per consumedFrameCounter quittiert, dann release(). Safety-Valve OSR_MAX_HOLD gegen
// Pool-Erschöpfung falls der capture-host lahmt/tot ist (dann Frames droppen — besser
// als Chromiums Painting zu stallen).
const OSR_MAX_HOLD = 3;
function setupOsrCapture(win: BrowserWindow): void {
  const held = new Map<bigint, { release: () => void }>();
  let frameCounter = 0n;
  let firstPaintLogged = false;
  let paintCount = 0;
  let lastRateLog = Date.now();
  atlasLog(`[osr] Capture aktiv — publiziere an Local\\BeeHiveVR_AtlasTexIn, Deckel ${ATLAS_FPS} fps`);
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  win.webContents.on('paint', (e: any) => {
    const tex = e && e.texture;
    if (!tex) return;
    const info = tex.textureInfo;
    const nt = info && info.handle ? info.handle.ntHandle : undefined;
    if (!Buffer.isBuffer(nt) || nt.length < 8) {
      // Kein brauchbarer Handle → sofort freigeben, nicht halten.
      try { tex.release(); } catch { /* ignore */ }
      return;
    }
    if (!firstPaintLogged) {
      firstPaintLogged = true;
      atlasLog('[osr] FIRST paint — pixelFormat=' + info.pixelFormat +
        ' codedSize=' + JSON.stringify(info.codedSize) +
        ' visibleRect=' + JSON.stringify(info.visibleRect) +
        ' ntHandle.len=' + nt.length);
    }
    frameCounter++;
    const fmt = info.pixelFormat === 'rgba' ? 1 : info.pixelFormat === 'rgbaf16' ? 2 : 0;
    const cs = info.codedSize || { width: 0, height: 0 };
    const vr = info.visibleRect || { x: 0, y: 0, width: cs.width, height: cs.height };
    try {
      atlasTexIn.publish({
        ntHandle:     nt.readBigUInt64LE(0),
        atlasPid:     process.pid,
        pixelFormat:  fmt,
        frameCounter,
        codedWidth:   cs.width,
        codedHeight:  cs.height,
        visRectX:     vr.x, visRectY: vr.y, visRectW: vr.width, visRectH: vr.height,
      });
    } catch (err) {
      atlasLog('[osr] publish error: ' + String(err));
      try { tex.release(); } catch { /* ignore */ }
      return;
    }
    held.set(frameCounter, tex);

    // Vom capture-host bereits kopierte Frames freigeben.
    const consumed = atlasTexIn.readConsumed();
    for (const [fc, t] of held) {
      if (fc <= consumed) { try { t.release(); } catch { /* ignore */ } held.delete(fc); }
    }
    // Safety-Valve: nie den 10er-Pool erschöpfen. capture-host lahm/tot → ältesten
    // droppen. Der capture-host konsumiert immer den NEUESTEN Handle (höchster
    // frameCounter) → der wird nie gedroppt; nur zurückgestaute Alte.
    while (held.size > OSR_MAX_HOLD) {
      const oldest = held.keys().next().value as bigint;
      const t = held.get(oldest);
      held.delete(oldest);
      try { t?.release(); } catch { /* ignore */ }
    }

    paintCount++;
    const now = Date.now();
    if (now - lastRateLog >= 5000) {
      atlasLog(`[osr] paints/s≈${Math.round(paintCount / 5)} held=${held.size} consumed=${consumed}`);
      paintCount = 0;
      lastRateLog = now;
    }
  });
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
    show: !OSR_MODE,
    // Nie in der Windows-Taskleiste auftauchen (auch nicht aktiv). Als
    // Konstruktor-Option greift es beim Erstellen, bevor Windows einen Taskbar-
    // Button anlegt — verhindert sowohl das aktive Icon als auch den Ghost nach
    // dem Beenden. WS_EX_TOOLWINDOW (applyCloakingAndToolWindow) kommt erst in
    // did-finish-load und damit zu spät, um den Button noch zu entfernen.
    skipTaskbar: true,
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
    webPreferences: OSR_MODE
      ? { backgroundThrottling: false, offscreen: { useSharedTexture: true } }
      : { backgroundThrottling: false },
  });
  atlasWindow = win;
  if (OSR_MODE) setupOsrCapture(win);
  // ⚠ Temporärer Debug-Hebel (3.6.2026): DevTools öffnen wenn env-var gesetzt.
  // Atlas-Window selbst ist off-screen (Alpha-Pfad), DevTools-Fenster ist sichtbar →
  // erlaubt Console + Network + DOM-Inspection.
  // Aktivieren mit: $env:BEEHIVE_ATLAS_DEVTOOLS = "1" vor App-Start.
  if (process.env.BEEHIVE_ATLAS_DEVTOOLS) {
    win.webContents.openDevTools({ mode: 'detach' });
  }
  // 17.6.2026 — setFrameRate ENTFERNT: wirkungslos bei diesem Fenstertyp.
  // Electrons setFrameRate greift NUR bei offscreen-Rendering; unser Atlas ist
  // ein sichtbares (cloaked) WGC-Fenster (siehe Kopf-Kommentar) → reiner No-Op.
  // Die Compositing-Rate ist hier nicht drosselbar (der offscreen-Pfad, wo es
  // ginge, wurde wegen Content-Update-Jitter verworfen). CPU-Entlastung läuft
  // stattdessen über Prozess-Priorität (applyLowPriority) + quellseitige Caps.
  win.webContents.on('render-process-gone', (_e, info) => {
    console.error('[main] renderer gone:', info);
  });
  win.webContents.once('did-finish-load', () => {
    atlasPageReady = true;
    atlasLog('[did-finish-load] DOM ready');
    // Falls in der Zwischenzeit schon Layout-Pushes kamen, jetzt nachholen.
    syncIframes();
    if (OSR_MODE) {
      // OSR (D2): kein HWND/Cloak — der Bildinhalt kommt per paint-Event als
      // Shared-Texture (setupOsrSmoke). Hier nur den setFrameRate-Deckel scharf
      // schalten (heute im Fensterpfad ein No-Op, in OSR funktional).
      try {
        win.webContents.setFrameRate(ATLAS_FPS);
        atlasLog(`[osr] setFrameRate(${ATLAS_FPS}) gesetzt`);
      } catch (e) { atlasLog('[osr] setFrameRate failed: ' + String(e)); }
      // Phase-0-Deckel-Beweis: erzwingt kontinuierliche Repaints (rAF), damit
      // paints/s messbar wird auch ohne animierte Widgets. Nur mit Env-Flag.
      if (process.env.BEEHIVE_OSR_FORCEPAINT) {
        win.webContents.executeJavaScript(
          "(function(){var n=0,d=document.createElement('div');" +
          "d.style.cssText='position:fixed;top:0;left:0;width:10px;height:10px;" +
          "background:red;z-index:2147483647';document.body.appendChild(d);" +
          "(function loop(){n=(n+1)%80;d.style.transform='translateX('+n+'px)';" +
          "requestAnimationFrame(loop);})();})();"
        ).catch(() => { /* ignore */ });
        atlasLog('[osr] FORCEPAINT aktiv (rAF-Animation injiziert)');
      }
      republish();
      return;
    }
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

// --parent-pid=N (von der WPF gesetzt): stirbt die WPF unsauber (Crash/Task-Manager/
// Shutdown → OnExit lief nicht → ElectronAtlasService.Stop nie), beenden wir uns
// selbst. Ergänzt den Startup-Orphan-Sweep (der räumt Alt-Leichen; das hier verhindert
// NEUE). Poll statt Blocking-Wait — Node ist single-threaded. Muster wie capture-host.
function watchParentPid(): void {
  const arg = process.argv.find((a) => a.startsWith('--parent-pid='));
  if (!arg) return;
  const pid = parseInt(arg.slice('--parent-pid='.length), 10);
  if (!Number.isFinite(pid) || pid <= 0) return;
  atlasLog(`[main] parent-watch aktiv (WPF pid=${pid})`);
  setInterval(() => {
    try {
      process.kill(pid, 0); // Signal 0 = nur Existenz prüfen, kein Signal senden
    } catch (e) {
      // ESRCH = Prozess weg → WPF tot → Self-Exit. EPERM = existiert, nur keine
      // Rechte → lebt weiter, kein Exit.
      if ((e as NodeJS.ErrnoException).code === 'ESRCH') {
        // app.exit() statt app.quit(): quit() kann von beschäftigten Renderern /
        // before-quit-Handlern verzögert oder blockiert werden — genau das Symptom
        // "Atlas schließt in laufender Session nicht" (18.7.). Der Watchdog muss
        // hart raus; SHM-Mappings räumt der Prozess-Exit ohnehin auf.
        atlasLog(`[main] Parent (WPF pid=${pid}) weg — Self-Exit (hart)`);
        app.exit(0);
      }
    }
  }, 2000);
}

app.whenReady().then(() => {
  console.log('[main] electron', process.versions.electron, 'chrome', process.versions.chrome);
  watchParentPid();

  // 13.7.2026: Ersatz für die entfernten Anti-Throttling-Switches (s.o.):
  // hält Timer/Heartbeat am Leben wenn WPF minimiert + Atlas cloaked ist,
  // ohne Chromiums Render-Drosselung global abzuschalten.
  const psbId = powerSaveBlocker.start('prevent-app-suspension');
  atlasLog(`[main] powerSaveBlocker aktiv (id=${psbId}, prevent-app-suspension)`);
  try {
    sharedFrame.open();
    console.log('[main] shared frame channel opened (Local\\BeeHiveVR_Frame)');
  } catch (e) {
    console.error('[main] failed to open shared frame channel:', e);
    app.quit();
    return;
  }
  if (OSR_MODE) {
    try {
      atlasTexIn.open();
      atlasLog('[osr] AtlasTexIn channel opened (Local\\BeeHiveVR_AtlasTexIn)');
    } catch (e) {
      atlasLog('[osr] failed to open AtlasTexIn channel: ' + String(e));
    }
  }
  try {
    winSrc.open();
    atlasLog('[winsrc] WinSrc channel opened (Local\\BeeHiveVR_WinSrc)');
  } catch (e) {
    atlasLog('[winsrc] failed to open WinSrc channel: ' + String(e));
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
  // 7.6.2026: WPF-Self-Capture Display-Crop. Bounds-Update triggert ein
  // syncIframes wenn aktuell eine WPF-Self-Source im Layout ist (Reconciler
  // mutiert die Inline-Styles auf .panel + <video>). Minimize-Toggle wird
  // dort auch behandelt (Source wird hidden via applyEffectiveVisibility).
  wpfLink.on('wpfWindowBounds', (b: WpfBounds & { monitorIndex: number }) => {
    const prev = wpfBounds;
    wpfBounds = { left: b.left, top: b.top, width: b.width, height: b.height,
                  minimized: b.minimized };
    const moved = !prev
      || prev.left !== b.left || prev.top !== b.top
      || prev.width !== b.width || prev.height !== b.height
      || prev.minimized !== b.minimized;
    if (!moved) return;
    // iconic-Mechanismus: minimiert → WPF-Source als visible=false markieren
    // (applyEffectiveVisibility + republish + syncIframes wie bei Window-
    // Capture-Iconic).
    applyWpfSelfIconic();
    syncIframes();
  });
  wpfLink.start();

  // F5 (6.6.2026): Heartbeat-Republish. republish() bumpt FrameSlot.generation
  // pro Aufruf — der Layer benutzt das als Liveness-Signal (siehe Watchdog
  // in layer.cpp). Ohne Heartbeat würde Atlas im Idle aussehen wie Atlas-tot,
  // weil regulärer republish() nur bei State-Change feuert. 100 ms (war 250)
  // ist robuster gegen Background-Throttling wenn WPF minimiert + Atlas
  // DWM-cloaked = Prozess als Background klassifiziert (Win Timer-Resolution
  // wird coarser, setInterval kann auf >1 s stretchen). Layer-Threshold
  // ist 120 Frames (≈1.3 s) — kleinerer Heartbeat-Tick reduziert Trip-Risiko
  // bei kurzen OS-Stalls deutlich (16.6.2026, User-Beobachtung).
  setInterval(() => {
    if (OSR_MODE || currentHwnd !== 0n) republish();
  }, 100);

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
      // 7.6.2026: CTRL+ALT-Drag schreibt m_dragBgOpacity. WPF setzt damit
      // src.DashieBgOpacity → patcht irdashies-config.json + broadcastet
      // dashboardUpdated → iframe rendert CSS live nach.
      bgOpacity: u.bgOpacity,
      // Phase 3: stabilisierte Hover/Grab-Id für WPF-Pille-Highlight.
      hoveredId: u.hoveredId,
    });
  });
  placeOut.start();

  createCapturedWindow();
  startLowPriority();
});

app.on('before-quit', () => {
  try { placeOut.stop(); } catch { /* ignore */ }
  try { wpfLink.stop(); } catch { /* ignore */ }
  try { sharedFrame.close(); } catch { /* ignore */ }
  try { atlasTexIn.close(); } catch { /* ignore */ }
  try { winSrc.close(); } catch { /* ignore */ }
});

app.on('window-all-closed', () => app.quit());
