// Win32-Shared-Memory publisher for BeeHive_VR.
//
// Talks to the OpenXR layer (engine/xr-api-beehive) via a named file mapping.
// The layer polls the FrameSlot from xrEndFrame — WGC has its own sync model
// (free-threaded frame pool), so no event signalling is needed.
//
// koffi 3 idiom: HANDLE = pointer-to-opaque, returned/passed as BigInt. We
// write into the mapped memory via koffi.encode() rather than koffi.view()
// because Electron forbids external buffers and koffi.view() throws there.

import koffi from 'koffi';

const kernel32 = koffi.load('kernel32.dll');

// HANDLE as an opaque pointer; in koffi 3 this round-trips as BigInt.
const HANDLE = koffi.pointer('HANDLE', koffi.opaque());

// FrameSlot — byte-for-byte match with the C++ layer (40 bytes).
// Describes the atlas source window. The QuadSlot array sits behind it.
// Phase 1 (5.6.2026): das frühere `reserved` uint32 trägt jetzt placeModeOn
// (0/1). Byte-Layout unverändert, Layer-static_assert(sizeof(FrameSlot)==40)
// gilt weiter. WPF toggled über EngineLink.PushPlaceMode → wpf-link emit →
// main.ts schreibt hier rein → Layer gated DrivePlaceMode darauf.
const FrameSlot = koffi.struct('FrameSlot', {
  generation:  'uint64_t',
  producerPid: 'uint32_t',
  placeModeOn: 'uint32_t',   // 0 = Trigger gesperrt, 1 = Trigger arbeitet (war: reserved)
  hwnd:        'uint64_t',   // BrowserWindow HWND that WGC captures
  width:       'uint32_t',
  height:      'uint32_t',
  format:      'uint32_t',
  quadCount:   'uint32_t',   // number of valid entries in the QuadSlot array
});
const FRAME_SLOT_SIZE: number = koffi.sizeof(FrameSlot);

// QuadSlot — one entry per sub-region of the atlas. Byte-for-byte match
// with the C++ side (76 bytes). The trailing 4 bytes carry per-quad opacity
// (was `reserved` before C4 Transparenz) — Layer-Compute-Shader nimmt das
// als Multiplier auf RGB+Alpha.
const QuadSlot = koffi.struct('QuadSlot', {
  id:       koffi.array('char', 16),  // ASCII, NUL-terminated, for logging
  rectX:    'uint32_t',
  rectY:    'uint32_t',
  rectW:    'uint32_t',
  rectH:    'uint32_t',
  posX:     'float32',
  posY:     'float32',
  posZ:     'float32',
  quatX:    'float32',
  quatY:    'float32',
  quatZ:    'float32',
  quatW:    'float32',
  sizeW:    'float32',
  sizeH:    'float32',
  visible:  'uint32_t',
  opacity:  'float32',  // 0..1, default 1.0 (war reserved bis C4)
});
const QUAD_SLOT_SIZE: number = koffi.sizeof(QuadSlot);

const MAX_QUADS = 8;
const MAPPING_SIZE = FRAME_SLOT_SIZE + MAX_QUADS * QUAD_SLOT_SIZE;

const CreateFileMappingW = kernel32.func(
  'HANDLE __stdcall CreateFileMappingW(HANDLE hFile, void* lpAttrs, uint32_t flProtect, ' +
  'uint32_t dwMaximumSizeHigh, uint32_t dwMaximumSizeLow, str16 lpName)');
const OpenFileMappingW = kernel32.func(
  'HANDLE __stdcall OpenFileMappingW(uint32_t dwDesiredAccess, bool bInheritHandle, str16 lpName)');
const MapViewOfFile = kernel32.func(
  'void* __stdcall MapViewOfFile(HANDLE hFileMappingObject, uint32_t dwDesiredAccess, ' +
  'uint32_t dwFileOffsetHigh, uint32_t dwFileOffsetLow, size_t dwNumberOfBytesToMap)');
const UnmapViewOfFile = kernel32.func('bool __stdcall UnmapViewOfFile(void* lpBaseAddress)');
const CloseHandle = kernel32.func('bool __stdcall CloseHandle(HANDLE hObject)');
const CreateMutexW = kernel32.func(
  'HANDLE __stdcall CreateMutexW(void* lpMutexAttributes, bool bInitialOwner, str16 lpName)');
const GetLastError = kernel32.func('uint32_t __stdcall GetLastError()');

// Windows constants.
const INVALID_HANDLE_VALUE = 0xFFFFFFFFFFFFFFFFn; // (HANDLE)-1
const PAGE_READWRITE       = 0x4;
const FILE_MAP_WRITE       = 0x2;
const ERROR_ALREADY_EXISTS = 183;

// koffi 3 returns BigInts for pointer types but the function signatures it
// exposes are untyped (just `Function`). Centralize the cast so the calls
// downstream read naturally.
function ptr(v: unknown): bigint {
  return typeof v === 'bigint' ? v : v == null ? 0n : BigInt(v as number);
}

// Names (keep in sync with the layer + the BeeHive_VR naming convention).
const NAME_MAPPING = 'Local\\BeeHiveVR_Frame';
const NAME_MUTEX   = 'Global\\BeeHiveVR_Instance';

export interface FramePublish {
  hwnd:        bigint;   // BrowserWindow HWND that the layer will WGC-capture
  width:       number;   // atlas width in pixels
  height:      number;   // atlas height in pixels
  format:      number;   // DXGI_FORMAT advertised to the layer (informational)
  placeModeOn: boolean;  // Phase 1: Trigger gated. Layer skipt DrivePlaceMode wenn false.
}

// One sub-region of the atlas. id is for human debugging only; layer doesn't
// match on it. Quat defaults to identity ({0,0,0,1}) if you omit. Opacity
// defaults to 1.0 (voll deckend) — Layer-Shader multipliziert auf RGB+Alpha.
export interface QuadDesc {
  id:     string;
  rectX:  number;
  rectY:  number;
  rectW:  number;
  rectH:  number;
  posX:   number;
  posY:   number;
  posZ:   number;
  quatX?: number;
  quatY?: number;
  quatZ?: number;
  quatW?: number;
  sizeW:  number;     // meters
  sizeH:  number;     // meters
  visible?: boolean;
  opacity?: number;   // 0..1, default 1.0
}

class SharedFrameChannel {
  // koffi 3 pointers are BigInt; 0n means NULL / "not opened".
  private mapping: bigint = 0n;
  private mapView: bigint = 0n;
  private generation = 0n;

  open(): void {
    const m = ptr(CreateFileMappingW(
      INVALID_HANDLE_VALUE, null, PAGE_READWRITE, 0, MAPPING_SIZE, NAME_MAPPING));
    if (!m) throw new Error(`CreateFileMappingW failed err=${GetLastError()}`);
    this.mapping = m;

    const v = ptr(MapViewOfFile(m, FILE_MAP_WRITE, 0, 0, MAPPING_SIZE));
    if (!v) throw new Error(`MapViewOfFile failed err=${GetLastError()}`);
    this.mapView = v;

    // Zero the whole mapping so a stale slot from a previous process does not
    // confuse the layer.
    this.zeroAll();
  }

  private zeroAll(): void {
    if (!this.mapView) return;
    koffi.encode(this.mapView, FrameSlot, {
      generation: 0n, producerPid: 0, placeModeOn: 0,
      hwnd: 0n, width: 0, height: 0, format: 0, quadCount: 0,
    });
    const empty = {
      id: '',
      rectX: 0, rectY: 0, rectW: 0, rectH: 0,
      posX: 0, posY: 0, posZ: 0,
      quatX: 0, quatY: 0, quatZ: 0, quatW: 1,
      sizeW: 0, sizeH: 0, visible: 0, opacity: 1.0,
    };
    for (let i = 0; i < MAX_QUADS; i++) {
      const offset = FRAME_SLOT_SIZE + i * QUAD_SLOT_SIZE;
      koffi.encode(this.mapView, offset, QuadSlot, empty);
    }
  }

  publishAtlas(f: FramePublish, quads: QuadDesc[]): void {
    if (!this.mapView) throw new Error('SharedFrameChannel not opened');
    if (quads.length > MAX_QUADS) {
      throw new Error(`publishAtlas: ${quads.length} quads exceeds MAX_QUADS=${MAX_QUADS}`);
    }
    this.generation++;

    koffi.encode(this.mapView, FrameSlot, {
      generation:  this.generation,
      producerPid: process.pid,
      placeModeOn: f.placeModeOn ? 1 : 0,
      hwnd:        f.hwnd,
      width:       f.width,
      height:      f.height,
      format:      f.format,
      quadCount:   quads.length,
    });

    for (let i = 0; i < quads.length; i++) {
      const q = quads[i];
      const offset = FRAME_SLOT_SIZE + i * QUAD_SLOT_SIZE;
      koffi.encode(this.mapView, offset, QuadSlot, {
        id:       q.id.slice(0, 15),   // koffi truncates + NUL-terminates char[16]
        rectX:    q.rectX,
        rectY:    q.rectY,
        rectW:    q.rectW,
        rectH:    q.rectH,
        posX:     q.posX,
        posY:     q.posY,
        posZ:     q.posZ,
        quatX:    q.quatX ?? 0,
        quatY:    q.quatY ?? 0,
        quatZ:    q.quatZ ?? 0,
        quatW:    q.quatW ?? 1,
        sizeW:    q.sizeW,
        sizeH:    q.sizeH,
        visible:  (q.visible ?? true) ? 1 : 0,
        opacity:  q.opacity ?? 1.0,
      });
    }
  }

  close(): void {
    if (this.mapView) {
      try { this.zeroAll(); } catch { /* ignore */ }
      UnmapViewOfFile(this.mapView);
      this.mapView = 0n;
    }
    if (this.mapping) { CloseHandle(this.mapping); this.mapping = 0n; }
  }
}

/**
 * Try to acquire the BeeHive_VR cross-component single-instance lock. Returns
 * true if we own it, false if another process already does. The mutex is held
 * for the lifetime of the process — we do not release explicitly.
 */
export function tryAcquireSingleInstance(): boolean {
  const mutex = ptr(CreateMutexW(null, false, NAME_MUTEX));
  if (!mutex) {
    console.warn(`tryAcquireSingleInstance: CreateMutexW failed err=${GetLastError()}`);
    return true;  // don't block app startup on a mutex glitch
  }
  if (GetLastError() === ERROR_ALREADY_EXISTS) {
    CloseHandle(mutex);
    return false;
  }
  // Leak the handle intentionally: process termination releases it.
  return true;
}

export const sharedFrame = new SharedFrameChannel();
