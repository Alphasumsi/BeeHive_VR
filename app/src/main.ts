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
import started from 'electron-squirrel-startup';
import koffi from 'koffi';
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

// Atlas dimensions and the static layout of sub-regions. This is the POC
// layout — eventually the WPF UI or the user's saved layout will drive it.
const ATLAS_WIDTH  = 1024;
const ATLAS_HEIGHT = 768;

// Atlas packing — three fixed regions matching the iframe panels in
// index.html. Electron stays authoritative for the rect side (iframe DOM is
// Electron-owned); WPF supplies pose / size / visibility and we assign a
// region to each incoming id on a first-come basis. Existing assignments
// stick (so dragging a quad in WPF doesn't re-shuffle which iframe shows).
const ATLAS_REGIONS: { rectX: number; rectY: number; rectW: number; rectH: number }[] = [
  { rectX:   0, rectY:   0, rectW:  512, rectH: 384 },   // p1 iframe
  { rectX: 512, rectY:   0, rectW:  512, rectH: 384 },   // p2 iframe
  { rectX:   0, rectY: 384, rectW: 1024, rectH: 384 },   // p3 iframe
];

// WPF authoritatively owns which quads exist — nothing in VR until
// setAtlasLayout arrives with at least one entry.
const currentLayout: QuadDesc[] = [];

let currentHwnd: bigint = 0n;

function republish(): void {
  if (currentHwnd === 0n) return;
  const payload: FramePublish = {
    hwnd:   currentHwnd,
    width:  ATLAS_WIDTH,
    height: ATLAS_HEIGHT,
    format: ATLAS_FORMAT_DXGI,
  };
  sharedFrame.publishAtlas(payload, currentLayout);
}

function applyWpfLayout(quads: AtlasQuadFromWpf[]): void {
  // Phase 1: keep existing entries that WPF still wants, drop ones it doesn't.
  // Phase 2: append new ones, assigning the next free atlas region.
  const incomingIds = new Set(quads.map(q => q.id));
  for (let i = currentLayout.length - 1; i >= 0; i--) {
    if (!incomingIds.has(currentLayout[i].id)) currentLayout.splice(i, 1);
  }
  const usedRegions = new Set(
    currentLayout.map(s => `${s.rectX},${s.rectY},${s.rectW},${s.rectH}`));
  for (const q of quads) {
    let slot = currentLayout.find(s => s.id === q.id);
    if (!slot) {
      const region = ATLAS_REGIONS.find(
        r => !usedRegions.has(`${r.rectX},${r.rectY},${r.rectW},${r.rectH}`));
      if (!region) {
        console.warn(`[main] no free atlas region for new id "${q.id}" — dropping`);
        continue;
      }
      slot = {
        id: q.id, rectX: region.rectX, rectY: region.rectY,
        rectW: region.rectW, rectH: region.rectH,
        posX: 0, posY: 0, posZ: -1, sizeW: 0.4, sizeH: 0.3,
      };
      currentLayout.push(slot);
      usedRegions.add(`${region.rectX},${region.rectY},${region.rectW},${region.rectH}`);
    }
    slot.posX  = q.posX;  slot.posY  = q.posY;  slot.posZ  = q.posZ;
    slot.quatX = q.quatX; slot.quatY = q.quatY; slot.quatZ = q.quatZ; slot.quatW = q.quatW;
    slot.sizeW = q.sizeW; slot.sizeH = q.sizeH;
    slot.visible = q.visible;
  }
  console.log(`[main] WPF layout applied: ${quads.length} quad(s), live=${currentLayout.length}`);
  republish();
}

function createCapturedWindow() {
  const win = new BrowserWindow({
    width: ATLAS_WIDTH,
    height: ATLAS_HEIGHT,
    x: 100,
    y: 100,
    show: true,
    // Frameless so WGC's captured client area exactly matches ATLAS_WIDTH x
    // ATLAS_HEIGHT. With frame: true the Win32 chrome eats ~14 px off each
    // axis, WGC reports the smaller client size to the layer, and quad rects
    // configured against the atlas size run past the swapchain edge →
    // XR_ERROR_SWAPCHAIN_RECT_INVALID stalls iRacing's loader.
    frame: false,
    useContentSize: true,
    title: 'BeeHive_VR Atlas (WGC source)',
    transparent: false,
    webPreferences: {
      backgroundThrottling: false,
    },
  });
  // Match iRacing's typical 90 Hz HMD rate. WGC samples this window on the
  // compositor's clock — keeping the renderer at HMD rate avoids the visible
  // 60↔90 beat we saw in the offscreen path.
  win.webContents.setFrameRate(90);
  win.webContents.on('render-process-gone', (_e, info) => {
    console.error('[main] renderer gone:', info);
  });
  win.webContents.once('did-finish-load', () => {
    const buf = win.getNativeWindowHandle();
    if (buf.length < 8) {
      console.error('[main] getNativeWindowHandle: expected at least 8 bytes, got', buf.length);
      return;
    }
    currentHwnd = buf.readBigUInt64LE(0);
    console.log(`[main] HWND=0x${currentHwnd.toString(16)} — publishing for layer`);
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
  wpfLink.start();

  // Place-in-VR: layer publishes pose updates while a controller-grab is
  // active; we forward each generation to WPF over the existing pipe. The
  // mapping only exists once iRacing is running and the layer is past its
  // setup-holdoff, so the reader just polls quietly until then.
  placeOut.on('placeUpdate', (u: PlaceUpdate) => {
    // JSON keys match WPF's EngineLink.PlaceUpdate parser (legacy field names
    // — x/y/z/yaw/pitch/scale/opacity). `scale` carries sizeW; sizeH is
    // implicitly proportional via WPF's aspect handling. Opacity is not in
    // QuadSlot yet, send 0 as placeholder so the parser doesn't choke.
    wpfLink.send({
      type:    'placeUpdate',
      id:      u.id,
      x:       u.posX,
      y:       u.posY,
      z:       u.posZ,
      yaw:     u.yawDeg,
      pitch:   u.pitchDeg,
      scale:   u.sizeW,
      opacity: 0,
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
