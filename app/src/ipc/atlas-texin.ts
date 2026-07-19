// D2 (14.7.2026) — Publisher für Local\BeeHiveVR_AtlasTexIn.
//
// Der Atlas rendert offscreen (webPreferences.offscreen.useSharedTexture) und
// bekommt pro paint eine geteilte D3D11-Textur. Hier publizieren wir den
// prozess-LOKALEN NT-Handle-Wert dieser Textur (electron textureInfo.handle.ntHandle,
// 8 B → u64) + Metadaten für den capture-host, der ihn per DuplicateHandle zu sich
// zieht + OpenSharedResource1 öffnet + in seinen Ring kopiert.
//
// Byte-Layout MUSS zu engine/shared/beehive_shm.h::AtlasTexIn passen (128 B).
// Wir schreiben nur die ersten 56 B (AtlasTexInHead) — das Feld
// consumedFrameCounter@56 gehört dem capture-host (Release-Rückmeldung) und darf
// vom Atlas NICHT überschrieben werden. Seqlock: generation zuerst, ein Encode
// (identisches Muster wie shared-frame.ts::publishAtlas; der C++-Reader liest
// doppelt und vergleicht generation).

import koffi from 'koffi';

const kernel32 = koffi.load('kernel32.dll');
// HANDLEs als built-in void* (BigInt-Roundtrip) — kein named 'HANDLE'-Typ, damit
// wir nicht mit shared-frame.ts's globaler koffi-Registrierung kollidieren.

// Erste 56 Bytes von AtlasTexIn (ohne consumedFrameCounter@56 + reserved).
const AtlasTexInHead = koffi.struct('AtlasTexInHead', {
  generation:   'uint64_t', // @0
  atlasPid:     'uint32_t', // @8
  pixelFormat:  'uint32_t', // @12  0=bgra 1=rgba 2=rgbaf16
  ntHandle:     'uint64_t', // @16  NT-Handle-Wert im Atlas-Prozess
  frameCounter: 'uint64_t', // @24
  codedWidth:   'uint32_t', // @32
  codedHeight:  'uint32_t', // @36
  visRectX:     'uint32_t', // @40
  visRectY:     'uint32_t', // @44
  visRectW:     'uint32_t', // @48
  visRectH:     'uint32_t', // @52
});
const HEAD_SIZE: number = koffi.sizeof(AtlasTexInHead); // 56
const MAPPING_SIZE = 128;        // == sizeof(AtlasTexIn)
const CONSUMED_OFFSET = 56;      // consumedFrameCounter (capture-host = Writer)

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
const FILE_MAP_WRITE = 0x2; // gewährt Lesen UND Schreiben
const NAME = 'Local\\BeeHiveVR_AtlasTexIn';

function ptr(v: unknown): bigint {
  return typeof v === 'bigint' ? v : v == null ? 0n : BigInt(v as number);
}

export interface AtlasTexPublish {
  ntHandle: bigint;
  atlasPid: number;
  pixelFormat: number;
  frameCounter: bigint;
  codedWidth: number;
  codedHeight: number;
  visRectX: number;
  visRectY: number;
  visRectW: number;
  visRectH: number;
}

class AtlasTexInChannel {
  private mapping = 0n;
  private mapView = 0n;
  private generation = 0n;

  open(): void {
    const m = ptr(CreateFileMappingW(
      INVALID_HANDLE_VALUE, null, PAGE_READWRITE, 0, MAPPING_SIZE, NAME));
    if (!m) throw new Error(`CreateFileMappingW(AtlasTexIn) failed err=${GetLastError()}`);
    this.mapping = m;
    const v = ptr(MapViewOfFile(m, FILE_MAP_WRITE, 0, 0, MAPPING_SIZE));
    if (!v) throw new Error(`MapViewOfFile(AtlasTexIn) failed err=${GetLastError()}`);
    this.mapView = v;
    // Ganzen Block nullen (inkl. consumedFrameCounter) — Startzustand: nichts konsumiert.
    koffi.encode(this.mapView, AtlasTexInHead, {
      generation: 0n, atlasPid: 0, pixelFormat: 0, ntHandle: 0n, frameCounter: 0n,
      codedWidth: 0, codedHeight: 0, visRectX: 0, visRectY: 0, visRectW: 0, visRectH: 0,
    });
    // consumedFrameCounter + reserved (Rest bis 128) nullen.
    for (let off = HEAD_SIZE; off + 8 <= MAPPING_SIZE; off += 8) {
      koffi.encode(this.mapView, off, 'uint64_t', 0n);
    }
  }

  publish(p: AtlasTexPublish): void {
    if (!this.mapView) throw new Error('AtlasTexInChannel not opened');
    this.generation++;
    koffi.encode(this.mapView, AtlasTexInHead, {
      generation:   this.generation,
      atlasPid:     p.atlasPid,
      pixelFormat:  p.pixelFormat,
      ntHandle:     p.ntHandle,
      frameCounter: p.frameCounter,
      codedWidth:   p.codedWidth,
      codedHeight:  p.codedHeight,
      visRectX:     p.visRectX,
      visRectY:     p.visRectY,
      visRectW:     p.visRectW,
      visRectH:     p.visRectH,
    });
  }

  /** capture-host schreibt hierher, welchen frameCounter es zuletzt kopiert hat. */
  readConsumed(): bigint {
    if (!this.mapView) return 0n;
    try { return BigInt(koffi.decode(this.mapView, CONSUMED_OFFSET, 'uint64_t') as number | bigint); }
    catch { return 0n; }
  }

  close(): void {
    if (this.mapView) { UnmapViewOfFile(this.mapView); this.mapView = 0n; }
    if (this.mapping) { CloseHandle(this.mapping); this.mapping = 0n; }
  }
}

export const atlasTexIn = new AtlasTexInChannel();
