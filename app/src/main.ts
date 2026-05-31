// BeeHive_VR — Atlas-Renderer (Electron main).
//
// Renders offscreen widgets into a shared D3D11 texture and publishes the
// current handle to %LOCALAPPDATA%-via-shared-memory so the OpenXR layer
// running inside iRacing can compose it into the VR frame.
//
// Step 2 scope: live publish — every paint event becomes a new generation in
// shared memory. Two empirically-confirmed constraints (31.5.2026):
//   1. Retaining every tex forever stalls Chromium at ~11 textures
//      (pool exhausted, paint events stop firing).
//   2. Releasing every tex immediately races the layer: by the time the
//      layer reads the slot and calls DuplicateHandle, the handle is already
//      invalid (ERROR_INVALID_HANDLE = 6).
// Sweet spot: small ring buffer of last N retained — long enough for the
// layer to read+open before invalidation, short enough for Chromium to keep
// rotating its pool.

import { app, BrowserWindow } from 'electron';
import path from 'node:path';
import started from 'electron-squirrel-startup';
import { sharedFrame, tryAcquireSingleInstance, FramePublish, QuadDesc } from './ipc/shared-frame';
import { wpfLink, AtlasQuadFromWpf } from './ipc/wpf-link';

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

// Map Electron's pixelFormat string to DXGI_FORMAT.
function pixelFormatToDxgi(pf: string | undefined): number {
  if (pf === 'bgra') return 87; // DXGI_FORMAT_B8G8R8A8_UNORM
  if (pf === 'rgba') return 28; // DXGI_FORMAT_R8G8B8A8_UNORM
  return 0;
}

interface PaintEventTextureInfo {
  pixelFormat: string;
  codedSize: { width: number; height: number };
  handle?: { ntHandle?: Buffer };
}
interface PaintEventDetails {
  texture?: {
    textureInfo: PaintEventTextureInfo;
    release(): void;
  };
}

let frameCount = 0;
let publishCount = 0;
const handlesSeen = new Set<bigint>();

// Retain the last N tex objects to keep their NT handles valid for the
// consumer (layer) long enough to read+duplicate them.
const RETAIN_RING_SIZE = 8;
type Tex = NonNullable<PaintEventDetails['texture']>;
const retainRing: Tex[] = [];

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

// Mutable working copy. onPaint reads this, WPF messages overwrite it.
const currentLayout: QuadDesc[] = DEFAULT_LAYOUT.map(q => ({ ...q }));

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
}

function onPaint(details: PaintEventDetails) {
  frameCount++;

  const tex = details.texture;
  if (!tex) {
    if (frameCount === 1) {
      console.error('[main] no GPU texture in paint event — useSharedTexture not honored');
    }
    return;
  }

  const info = tex.textureInfo;
  const buf  = info.handle?.ntHandle;
  if (!buf || buf.length !== 8) {
    if (frameCount === 1) console.error('[main] cannot extract NT handle bytes from paint event');
    try { tex.release(); } catch { /* ignore */ }
    return;
  }

  const handle = buf.readBigUInt64LE(0);
  const isNew  = !handlesSeen.has(handle);
  if (isNew) handlesSeen.add(handle);

  const payload: FramePublish = {
    ntHandle: handle,
    width:    info.codedSize.width,
    height:   info.codedSize.height,
    format:   pixelFormatToDxgi(info.pixelFormat),
  };
  sharedFrame.publishAtlas(payload, currentLayout);
  publishCount++;

  if (publishCount === 1) {
    console.log(
      `[main] first publish: handle=0x${handle.toString(16)} ` +
      `size=${payload.width}x${payload.height} fmt=${payload.format}`);
    console.log('[main] live mode — every paint publishes. Ctrl+C to quit.');
  }
  if (isNew && handlesSeen.size <= 16) {
    console.log(`[main] new handle 0x${handle.toString(16)} (unique seen: ${handlesSeen.size})`);
  }
  if (publishCount % 300 === 0) {
    console.log(`[main] publish #${publishCount} handle=0x${handle.toString(16)} unique=${handlesSeen.size}`);
  }

  // Retain in the ring; release the one that falls off the back end so
  // Chromium's pool can keep rotating.
  retainRing.push(tex);
  if (retainRing.length > RETAIN_RING_SIZE) {
    const stale = retainRing.shift();
    if (stale) {
      try { stale.release(); } catch { /* ignore */ }
    }
  }
}

function createOffscreenWindow() {
  const win = new BrowserWindow({
    width: ATLAS_WIDTH,
    height: ATLAS_HEIGHT,
    show: false,
    frame: false,
    transparent: false,
    webPreferences: {
      offscreen: { useSharedTexture: true },
      backgroundThrottling: false,
    },
  });
  // Match iRacing's typical 90 Hz HMD rate to avoid the 60↔90 beat pattern
  // that shows up as visible stutter on the quad.
  win.webContents.setFrameRate(90);
  win.webContents.on('paint', onPaint as never);
  win.webContents.on('render-process-gone', (_e, info) => {
    console.error('[main] renderer gone:', info);
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

  createOffscreenWindow();
});

app.on('before-quit', () => {
  for (const tex of retainRing) { try { tex.release(); } catch { /* ignore */ } }
  retainRing.length = 0;
  try { wpfLink.stop(); } catch { /* ignore */ }
  try { sharedFrame.close(); } catch { /* ignore */ }
});

app.on('window-all-closed', () => app.quit());
