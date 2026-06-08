// Named-Pipe client connecting Electron to the WPF UI on \\.\pipe\BeeHiveVR.
//
// WPF is the server (long-lived, listening). Electron is the client (auto-
// reconnects on disconnect). Wire format mirrors WPF's EngineLink:
//   [uint32 LE length][UTF-8 JSON body]
//
// Messages we care about right now (Step 4b):
//   setAtlasLayout: { type, quads: [{ id, posX, posY, posZ, quatX..W, sizeW, sizeH, visible }] }
//
// Future messages (recenter, placeMode, ...) get added here as we wire them up.

import { EventEmitter } from 'node:events';
import * as net from 'node:net';

const PIPE_PATH = '\\\\.\\pipe\\BeeHiveVR';
const RECONNECT_MS = 1000;
const MAX_PAYLOAD = 16 * 1024 * 1024;

export interface AtlasQuadFromWpf {
  id:      string;
  posX:    number;
  posY:    number;
  posZ:    number;
  quatX:   number;
  quatY:   number;
  quatZ:   number;
  quatW:   number;
  sizeW:   number;
  sizeH:   number;
  visible: boolean;
  // Voll qualifizierte URL des Widgets (z.B. http://localhost:8723/dashie.html?widget=relative).
  // main.ts setzt damit per executeJavaScript den Iframe-src der zugewiesenen
  // Atlas-Region — sonst zeigt die Region ihren statischen Default-Inhalt.
  target?:  string;
  // 0..1 Multiplier auf RGB+Alpha, getrieben vom LayoutPage-Opacity-Slider.
  // Default 1.0 (voll deckend). Compute-Shader im Layer wendet das pro Quad an.
  opacity?: number;
  // 0..1 BG Opacity (CSS-Background im Dashie-Widget). Aus irdashies-config.json
  // per-Widget global. WPF stopft den aktuellen Wert in den AtlasQuad damit der
  // Layer beim CTRL+ALT-Grab-Start m_dragBgOpacity korrekt initialisiert.
  // Default 0.0 = transparent.
  bgOpacity?: number;
  // C3b (4.6.2026): Wunsch-Pixel-Größe des Widgets im Atlas. Electron-Packer
  // sucht damit einen Slot, ist authoritativ für die Ist-Position (rectX/Y) im
  // QuadSlot. 0/undefined → Default 512×384.
  rectW?:   number;
  rectH?:   number;
  // Phase 3 (5.6.2026): User-vergebener Anzeigename (z.B. „Relative Dashie").
  // Atlas zeigt ihn als Sticker am Quad bei Hover/Grab.
  name?:    string;
  // C6 (5.6.2026): Source-Subtyp. "browser" → target ist iframe.src direkt.
  // "window" → target ist Fenstertitel; main.ts resolved per
  // desktopCapturer.getSources den match und baut eine Wrapper-URL
  // window-capture.html?sourceId=<dc-id>&title=<urlenc>.
  // Undefined wird als "browser" behandelt (Rückwärts-Kompat).
  type?:    string;
}

class WpfLink extends EventEmitter {
  private socket:  net.Socket | null = null;
  private buf:     Buffer = Buffer.alloc(0);
  private stopped = false;

  start(): void {
    this.stopped = false;
    this.connect();
  }

  stop(): void {
    this.stopped = true;
    if (this.socket) { try { this.socket.destroy(); } catch { /* ignore */ } this.socket = null; }
  }

  private connect(): void {
    if (this.stopped) return;
    const s = net.createConnection(PIPE_PATH);
    this.socket = s;

    s.on('connect', () => {
      console.log('[wpf-link] connected to', PIPE_PATH);
      this.emit('connect');
    });

    s.on('data', (chunk: Buffer) => {
      this.buf = this.buf.length === 0 ? chunk : Buffer.concat([this.buf, chunk]);
      this.drain();
    });

    s.on('error', (err: NodeJS.ErrnoException) => {
      // Most errors here are "no pipe yet" (ENOENT) or "pipe closed" — log
      // anything else once per reconnect cycle, swallow the noise.
      if (this.stopped) return;
      if (err.code !== 'ENOENT') {
        console.warn('[wpf-link] socket error:', err.code || err.message);
      }
    });

    s.on('close', () => {
      this.socket = null;
      this.buf = Buffer.alloc(0);
      // F5: WPF-Lebenszeichen weg. main.ts hört darauf, leert das Layout
      // und republished mit quadCount=0 → Layer rendert keine Quads mehr
      // (statt sie auf dem letzten Stand einzufrieren). Auch wenn WPF noch
      // nicht da WAR: zweite Disconnect-Emission ohne Effekt im Handler ok.
      this.emit('disconnect');
      if (this.stopped) return;
      setTimeout(() => this.connect(), RECONNECT_MS);
    });
  }

  private drain(): void {
    while (this.buf.length >= 4) {
      const len = this.buf.readUInt32LE(0);
      if (len === 0 || len > MAX_PAYLOAD) {
        console.warn('[wpf-link] bogus length', len, '— dropping connection');
        try { this.socket?.destroy(); } catch { /* ignore */ }
        return;
      }
      if (this.buf.length < 4 + len) return; // need more bytes

      const json = this.buf.subarray(4, 4 + len).toString('utf8');
      this.buf = this.buf.subarray(4 + len);

      try {
        const msg = JSON.parse(json);
        this.dispatch(msg);
      } catch (e) {
        console.warn('[wpf-link] JSON parse failed:', (e as Error).message);
      }
    }
  }

  private dispatch(msg: { type?: string; quads?: AtlasQuadFromWpf[]; on?: boolean; id?: string; value?: boolean }): void {
    if (!msg.type) return;
    switch (msg.type) {
      case 'setAtlasLayout':
        if (Array.isArray(msg.quads)) this.emit('atlasLayout', msg.quads);
        break;
      case 'placeMode':
        // Phase 1 (5.6.2026): nur on durchreichen; optional id für später
        // (Place-Button auf einer Source-Karte fixiert direkt auf die ID,
        // wird in Phase 2 mit Halo + Aim verarbeitet).
        this.emit('placeMode', { on: !!msg.on, id: msg.id });
        break;
      case 'recenter':
        // B7 (5.6.2026): WPF-Keybind hat Recenter ausgelöst. main.ts erhöht
        // FrameSlot.recenterEpoch + republished; Layer reagiert beim
        // nächsten xrEndFrame mit Reference-Space-Neuaufbau.
        this.emit('recenter');
        break;
      case 'setMasterVisible':
        // 6.6.2026: globaler Master-Visible-Switch aus WPF (Menubar-Button
        // oder Keybind ToggleOverlays). main.ts schaltet damit den Publish
        // auf leere Quads — Layout-State bleibt erhalten, kommt bei Re-On
        // sofort zurück. Default true bei Reconnect.
        this.emit('masterVisible', !!msg.value);
        break;
      default:
        // Unknown message types are fine — just ignored. Old WPF still sends
        // setLayout/recenter/etc., which we'll wire up incrementally.
        break;
    }
  }

  // ---- outgoing ---------------------------------------------------------
  send(msg: object): void {
    if (!this.socket || this.socket.destroyed) return;
    const body = Buffer.from(JSON.stringify(msg), 'utf8');
    const head = Buffer.alloc(4);
    head.writeUInt32LE(body.length, 0);
    this.socket.write(Buffer.concat([head, body]));
  }

  sayHello(): void {
    this.send({ type: 'hello', app: 'BeeHive_VR Atlas-Renderer', engineVersion: '0.0.1-step4b' });
  }
}

export const wpfLink = new WpfLink();
