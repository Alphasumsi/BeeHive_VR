import { defineConfig } from 'vite';

// https://vitejs.dev/config
export default defineConfig({
  build: {
    rollupOptions: {
      // Native modules dürfen NICHT inline gebundled werden — koffi lädt
      // zur Laufzeit `@koromix/koffi-<platform>` (scoped optional deps mit
      // .node-Files). Wenn Vite das in main.js inlinet, sucht koffi den
      // Pfad innerhalb von app.asar und findet nichts → throw "Cannot find
      // the native Koffi module".
      //
      // 'electron' + 'electron-squirrel-startup' aus demselben Grund:
      // Electron's main process erwartet diese als externe requires.
      external: [
        'electron',
        'electron-squirrel-startup',
        'koffi',
        /^@koromix\//,
      ],
    },
  },
});
