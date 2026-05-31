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

    s.on('error', (err) => {
      // Most errors here are "no pipe yet" or "pipe closed" — log once per cycle.
      if (this.stopped) return;
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const code = (err as any).code;
      if (code !== 'ENOENT') {
        console.warn('[wpf-link] socket error:', code || err.message);
      }
    });

    s.on('close', () => {
      this.socket = null;
      this.buf = Buffer.alloc(0);
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

  private dispatch(msg: { type?: string; quads?: AtlasQuadFromWpf[] }): void {
    if (!msg.type) return;
    switch (msg.type) {
      case 'setAtlasLayout':
        if (Array.isArray(msg.quads)) this.emit('atlasLayout', msg.quads);
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
