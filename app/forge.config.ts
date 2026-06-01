import type { ForgeConfig } from '@electron-forge/shared-types';
import { MakerSquirrel } from '@electron-forge/maker-squirrel';
import { MakerZIP } from '@electron-forge/maker-zip';
import { VitePlugin } from '@electron-forge/plugin-vite';
import { FusesPlugin } from '@electron-forge/plugin-fuses';
import { FuseV1Options, FuseVersion } from '@electron/fuses';

const config: ForgeConfig = {
  packagerConfig: {
    asar: true,
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
