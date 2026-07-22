// Window-Capture-Entkopplung (19.7.2026) — Publisher für Local\BeeHiveVR_WinSrc.
//
// Die WPF löst Fenstertitel → HWND auf (EnumWindows sieht ALLE Fenster, auch die
// Overlay-/Tool-Fenster, die Chromiums desktopCapturer verschweigt) und schickt die
// HWND je Window-Quad im Layout-Push mit. Hier publizieren wir quadId→hwnd für den
// capture-host, der die Fenster nativ per WGC captured (CaptureWindowWinRT auf HWND).
//
// Byte-Layout MUSS zu engine/shared/beehive_shm.h::WinSrcSlot passen (320 B).
// Atlas = einziger Writer; Seqlock-Konvention wie shared-frame.ts (Reader doppel-
// liest und vergleicht generation).

import koffi from 'koffi';

const kernel32 = koffi.load('kernel32.dll');
// HANDLEs als built-in void* (BigInt-Roundtrip) — kein named Typ, keine Kollision
// mit shared-frame.ts' globaler koffi-Registry (gleiche Lehre wie atlas-texin.ts).

const WinSrcEntry = koffi.struct('WinSrcEntry', {
  id:   koffi.array('char', 16), // @0  NUL-terminiert (koffi truncated)
  hwnd: 'uint64_t',              // @16
});
const ENTRY_SIZE: number = koffi.sizeof(WinSrcEntry); // 24

const WinSrcHead = koffi.struct('WinSrcHead', {
  generation: 'uint64_t', // @0
  count:      'uint32_t', // @8
  pad0:       'uint32_t', // @12
});
const HEAD_SIZE: number = koffi.sizeof(WinSrcHead); // 16
const MAX_ENTRIES = 12;  // == kMaxQuads
const MAPPING_SIZE = 320; // == sizeof(WinSrcSlot)

const CreateFileMappingW = kernel32.func(
  'void* __stdcall CreateFileMappingW(void* hFile, void* lpAttrs, uint32_t flProtect, ' +
  'uint32_t dwMaximumSizeHigh, uint32_t dwMaximumSizeLow, str16 lpName)');
const MapViewOfFile = kernel32.func(
  'void* __stdcall MapViewOfFile(void* hFileMappingObject, uint32_t dwDesiredAccess, ' +
  'uint32_t dwFileOffsetHigh, uint32_t dwFileOffsetLow, size_t dwNumberOfBytesToMap)');
const UnmapViewOfFile = kernel32.func('bool __stdcall UnmapViewOfFile(void* lpBaseAddress)');
const CloseHandle = kernel32.func('bool __stdcall CloseHandle(void* hObject)');
const GetLastError = kernel32.func('uint32_t __stdcall GetLastError()');

const INVALID_HANDLE_VALUE = 0xFFFFFFFFFFFFFFFFn;
const PAGE_READWRITE = 0x4;
const FILE_MAP_WRITE = 0x2;
const NAME = 'Local\\BeeHiveVR_WinSrc';

function ptr(v: unknown): bigint {
  return typeof v === 'bigint' ? v : v == null ? 0n : BigInt(v as number);
}

export interface WinSrcPublish {
  id: string;
  hwnd: bigint; // 0n = (noch) nicht aufgelöst
}

class WinSrcChannel {
  private mapping = 0n;
  private mapView = 0n;
  private generation = 0n;

  open(): void {
    const m = ptr(CreateFileMappingW(
      INVALID_HANDLE_VALUE, null, PAGE_READWRITE, 0, MAPPING_SIZE, NAME));
    if (!m) throw new Error(`CreateFileMappingW(WinSrc) failed err=${GetLastError()}`);
    this.mapping = m;
    const v = ptr(MapViewOfFile(m, FILE_MAP_WRITE, 0, 0, MAPPING_SIZE));
    if (!v) throw new Error(`MapViewOfFile(WinSrc) failed err=${GetLastError()}`);
    this.mapView = v;
    this.publish([]); // sauberer Startzustand (count=0, gen=1)
  }

  publish(entries: WinSrcPublish[]): void {
    if (!this.mapView) return; // vor open() (oder open fehlgeschlagen) → no-op
    const n = Math.min(entries.length, MAX_ENTRIES);
    this.generation++;
    // Seqlock-Konvention (wie C++ PublishTexOut): PAYLOAD zuerst (Einträge, count),
    // die generation als LETZTER, separater 8-Byte-Store. Würde gen zusammen mit der
    // Payload geschrieben, könnte ein Doppelread zweimal die alte gen bei halb
    // geschriebenen Einträgen sehen → Tearing unentdeckt.
    for (let i = 0; i < n; i++) {
      koffi.encode(this.mapView, HEAD_SIZE + i * ENTRY_SIZE, WinSrcEntry, {
        id:   entries[i].id.slice(0, 15),
        hwnd: entries[i].hwnd,
      });
    }
    for (let i = n; i < MAX_ENTRIES; i++) {
      koffi.encode(this.mapView, HEAD_SIZE + i * ENTRY_SIZE, WinSrcEntry, { id: '', hwnd: 0n });
    }
    koffi.encode(this.mapView, 8, 'uint32_t', n);               // count @8
    koffi.encode(this.mapView, 'uint64_t', this.generation);     // generation @0, zuletzt
  }

  close(): void {
    if (this.mapView) {
      try { this.publish([]); } catch { /* ignore */ }
      UnmapViewOfFile(this.mapView);
      this.mapView = 0n;
    }
    if (this.mapping) { CloseHandle(this.mapping); this.mapping = 0n; }
  }
}

export const winSrc = new WinSrcChannel();
