import type { ForgeConfig } from '@electron-forge/shared-types';
import { MakerSquirrel } from '@electron-forge/maker-squirrel';
import { MakerZIP } from '@electron-forge/maker-zip';
import { VitePlugin } from '@electron-forge/plugin-vite';
import { FusesPlugin } from '@electron-forge/plugin-fuses';
import { FuseV1Options, FuseVersion } from '@electron/fuses';
import path from 'node:path';
import fs from 'node:fs/promises';

// External Module die Vite NICHT inline bundlen darf (siehe vite.main.config.ts)
// UND die als Files in das gepackte node_modules kopiert werden müssen.
// electron-forge plugin-vite kopiert externals nicht selber (bekannter Bug
// electron/forge#3738 / #3917), daher manueller afterCopy-Hook unten.
const EXTERNAL_RUNTIME_DEPS = [
  'koffi',                          // FFI für DwmSetWindowAttribute (Cloak)
  '@koromix/koffi-win32-x64',       // koffi's native .node für Win-x64
  'electron-squirrel-startup',      // Squirrel-Installer-Hook
  'debug',                          // transitiver Dep von electron-squirrel-startup
  'ms',                             // transitiver Dep von debug
];

const config: ForgeConfig = {
  packagerConfig: {
    // asar.unpack='**/*.node': stellt sicher dass jede native Node-Lib
    // aus der ASAR ausgepackt wird. Plugin-auto-unpack-natives macht das
    // theoretisch automatisch — koffi 3 nutzt aber scoped optional deps
    // (@koromix/koffi-win32-x64), und das Plugin findet die Files nicht
    // immer zuverlässig. Explizites Pattern als Belt-und-Hosenträger.
    asar: { unpack: '**/*.node' },
    // External-Module ins gepackte node_modules kopieren (workaround für
    // Forge-Vite-Plugin Bug #3738/#3917 — externals werden vom Plugin nicht
    // automatisch mit-installiert).
    afterCopy: [
      async (buildPath: string, _electronVersion: string, _platform: string,
             _arch: string, callback: (err?: Error) => void) => {
        try {
          const srcRoot = path.resolve(__dirname);
          for (const dep of EXTERNAL_RUNTIME_DEPS) {
            const src = path.join(srcRoot, 'node_modules', dep);
            const dst = path.join(buildPath, 'node_modules', dep);
            await fs.mkdir(path.dirname(dst), { recursive: true });
            await fs.cp(src, dst, { recursive: true });
          }
          callback();
        } catch (e) {
          callback(e as Error);
        }
      },
    ],
    name: 'BeeHive_VR_Atlas',
    executableName: 'BeeHive_VR_Atlas',
    appBundleId: 'com.beehivevr.atlas',
    win32metadata: {
      CompanyName: 'BeeHive_VR',
      ProductName: 'BeeHive_VR Atlas',
      FileDescription: 'BeeHive_VR Atlas-Renderer (Electron)',
    },
  },
  rebuildConfig: {},
  makers: [
    new MakerSquirrel({
      name: 'BeeHive_VR_Atlas',
      authors: 'BeeHive_VR',
      description: 'BeeHive_VR Atlas-Renderer — feeds iRacing dashies into VR',
      // Setup-Filename: Squirrel default ist "<name>-<version> Setup.exe".
      setupExe: 'BeeHive_VR_Atlas-Setup.exe',
    }),
    // BeeHive_VR ist Windows-only (OpenXR-Layer + WPF). Linux/macOS-Maker raus.
    new MakerZIP({}, ['win32']),
  ],
  plugins: [
    // Auto-unpack native modules (z.B. koffi) aus dem ASAR-Archiv.
    // Sonst kann der Electron-Loader die .node-Files zur Laufzeit nicht
    // laden ("Cannot find the native Koffi module"). Plugin scannt
    // node_modules, ergänzt asar.unpack-Patterns für alle native deps.
    {
      name: '@electron-forge/plugin-auto-unpack-natives',
      config: {},
    },
    new VitePlugin({
      // `build` can specify multiple entry builds, which can be Main process, Preload scripts, Worker process, etc.
      // If you are familiar with Vite configuration, it will look really familiar.
      build: [
        {
          // `entry` is just an alias for `build.lib.entry` in the corresponding file of `config`.
          entry: 'src/main.ts',
          config: 'vite.main.config.ts',
          target: 'main',
        },
        {
          entry: 'src/preload.ts',
          config: 'vite.preload.config.ts',
          target: 'preload',
        },
      ],
      renderer: [
        {
          name: 'main_window',
          config: 'vite.renderer.config.ts',
        },
      ],
    }),
    // Fuses are used to enable/disable various Electron functionality
    // at package time, before code signing the application
    new FusesPlugin({
      version: FuseVersion.V1,
      [FuseV1Options.RunAsNode]: false,
      [FuseV1Options.EnableCookieEncryption]: true,
      [FuseV1Options.EnableNodeOptionsEnvironmentVariable]: false,
      [FuseV1Options.EnableNodeCliInspectArguments]: false,
      [FuseV1Options.EnableEmbeddedAsarIntegrityValidation]: true,
      [FuseV1Options.OnlyLoadAppFromAsar]: true,
    }),
  ],
};

export default config;
