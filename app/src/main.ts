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
  const ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as bigint;
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

// Identity quat = {0, 0, 0, 1}. Helper to keep LAYOUT readable.
// These are DEFAULTS — WPF can override pose/size/visibility per id via the
// setAtlasLayout pipe message. Atlas rects stay Electron-owned (the iframe
// packing is our concern, not WPF's).
const DEFAULT_LAYOUT: QuadDesc[] = [
  // p1 — top-left, 1m forward-left, 0.4m wide
  { id: 'p1', rectX:   0, rectY:   0, rectW:  512, rectH: 384,
    posX: -0.6, posY:  0.0, posZ: -1.0, sizeW: 0.40, sizeH: 0.30 },
  // p2 — top-right, 1m forward-right
  { id: 'p2', rectX: 512, rectY:   0, rectW:  512, rectH: 384,
    posX:  0.6, posY:  0.0, posZ: -1.0, sizeW: 0.40, sizeH: 0.30 },
  // p3 — bottom wide, slightly below eye height
  { id: 'p3', rectX:   0, rectY: 384, rectW: 1024, rectH: 384,
    posX:  0.0, posY: -0.35, posZ: -1.0, sizeW: 0.80, sizeH: 0.30 },
];

// Mutable working copy. WPF messages overwrite it; republish() pushes the
// current state to the shared mapping.
const currentLayout: QuadDesc[] = DEFAULT_LAYOUT.map(q => ({ ...q }));

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
  for (const q of quads) {
    const slot = currentLayout.find(s => s.id === q.id);
    if (!slot) {
      console.warn(`[main] WPF sent unknown atlas id "${q.id}" — ignoring`);
      continue;
    }
    slot.posX  = q.posX;  slot.posY  = q.posY;  slot.posZ  = q.posZ;
    slot.quatX = q.quatX; slot.quatY = q.quatY; slot.quatZ = q.quatZ; slot.quatW = q.quatW;
    slot.sizeW = q.sizeW; slot.sizeH = q.sizeH;
    slot.visible = q.visible;
  }
  console.log(`[main] WPF layout applied: ${quads.length} quad(s)`);
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

  createCapturedWindow();
});

app.on('before-quit', () => {
  try { wpfLink.stop(); } catch { /* ignore */ }
  try { sharedFrame.close(); } catch { /* ignore */ }
});

app.on('window-all-closed', () => app.quit());
