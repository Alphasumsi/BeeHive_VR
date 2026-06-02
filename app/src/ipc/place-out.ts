// Reads the layer's PlaceOut mapping and emits placeUpdate events. The layer
// publishes a new pose snapshot every frame while a grab is active; we poll on
// a short interval and forward the latest generation. Polling instead of
// event-signalling keeps the IPC simple — placeUpdate is only meaningful for
// the duration of a grab (a few seconds at most), so a 16 ms tick is plenty.

import koffi from 'koffi';
import { EventEmitter } from 'node:events';

// HANDLE type is registered globally by shared-frame.ts; re-declaring here
// triggers koffi's "Duplicate type name 'HANDLE'" crash. The string-form func
// signatures below just reference the existing registration.
const kernel32 = koffi.load('kernel32.dll');

const OpenFileMappingW = kernel32.func(
  'HANDLE __stdcall OpenFileMappingW(uint32_t dwDesiredAccess, bool bInheritHandle, str16 lpName)');
const MapViewOfFile = kernel32.func(
  'void* __stdcall MapViewOfFile(HANDLE hFileMappingObject, uint32_t dwDesiredAccess, ' +
  'uint32_t dwFileOffsetHigh, uint32_t dwFileOffsetLow, size_t dwNumberOfBytesToMap)');
const UnmapViewOfFile = kernel32.func('bool __stdcall UnmapViewOfFile(void* lpBaseAddress)');
const CloseHandle = kernel32.func('bool __stdcall CloseHandle(HANDLE hObject)');
const GetLastError = kernel32.func('uint32_t __stdcall GetLastError()');

// Byte-for-byte match with the layer's PublishPlaceOut layout (96 bytes).
const PlaceOutStruct = koffi.struct('PlaceOut', {
  generation: 'uint64_t',
  id:         koffi.array('char', 16),
  posX:       'float32',
  posY:       'float32',
  posZ:       'float32',
  yawDeg:     'float32',
  pitchDeg:   'float32',
  sizeW:      'float32',
  sizeH:      'float32',
  padding:    koffi.array('uint8', 44),
});

const FILE_MAP_READ = 0x4;
const NAME = 'Local\\BeeHiveVR_PlaceOut';
const POLL_MS = 16;
const NUL = String.fromCharCode(0);

function ptr(v: unknown): bigint {
  return typeof v === 'bigint' ? v : v == null ? 0n : BigInt(v as number);
}

interface PlaceOutRaw {
  generation: bigint;
  id:         string;
  posX:       number;
  posY:       number;
  posZ:       number;
  yawDeg:     number;
  pitchDeg:   number;
  sizeW:      number;
  sizeH:      number;
}

export interface PlaceUpdate {
  id:       string;
  posX:     number;
  posY:     number;
  posZ:     number;
  yawDeg:   number;
  pitchDeg: number;
  sizeW:    number;
  sizeH:    number;
}

class PlaceOutReader extends EventEmitter {
  private mapping: bigint = 0n;
  private view:    bigint = 0n;
  private lastGen: bigint = 0n;
  private timer:   NodeJS.Timeout | null = null;

  start(): void {
    this.timer = setInterval(() => this.tick(), POLL_MS);
  }

  stop(): void {
    if (this.timer) { clearInterval(this.timer); this.timer = null; }
    this.close();
  }

  private tryOpen(): boolean {
    if (this.view) return true;
    const m = ptr(OpenFileMappingW(FILE_MAP_READ, false, NAME));
    if (!m) return false;
    const v = ptr(MapViewOfFile(m, FILE_MAP_READ, 0, 0, koffi.sizeof(PlaceOutStruct)));
    if (!v) {
      console.warn(`[place-out] MapViewOfFile err=${GetLastError()}`);
      CloseHandle(m);
      return false;
    }
    this.mapping = m;
    this.view = v;
    console.log('[place-out] opened', NAME);
    return true;
  }

  private close(): void {
    if (this.view)    { UnmapViewOfFile(this.view); this.view = 0n; }
    if (this.mapping) { CloseHandle(this.mapping);  this.mapping = 0n; }
    this.lastGen = 0n;
  }

  private tick(): void {
    if (!this.tryOpen()) return;
    const raw = koffi.decode(this.view, PlaceOutStruct) as PlaceOutRaw;
    if (raw.generation === 0n || raw.generation === this.lastGen) return;
    this.lastGen = raw.generation;

    // koffi returns the full 16-char window; NUL-truncate to the producer id.
    const id = raw.id.split(NUL)[0];
    const u: PlaceUpdate = {
      id,
      posX:     raw.posX,
      posY:     raw.posY,
      posZ:     raw.posZ,
      yawDeg:   raw.yawDeg,
      pitchDeg: raw.pitchDeg,
      sizeW:    raw.sizeW,
      sizeH:    raw.sizeH,
    };
    this.emit('placeUpdate', u);
  }
}

export const placeOut = new PlaceOutReader();
