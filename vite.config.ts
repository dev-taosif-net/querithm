import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Tauri expects a fixed dev port; the webview loads only bundled assets (§13 rule 5: no CDNs).
export default defineConfig({
  plugins: [react(), tailwindcss()],
  clearScreen: false,
  server: { port: 1420, strictPort: true, host: "127.0.0.1", watch: { ignored: ["**/src-tauri/**", "**/sidecar/**"] } },
  // Only these prefixes reach import.meta.env. A bare "TAURI_" would also expose TAURI_SIGNING_PRIVATE_KEY(_PASSWORD) to the bundle.
  envPrefix: ["VITE_", "TAURI_ENV_"],
  build: {
    target: "es2022",
    sourcemap: false,
    chunkSizeWarningLimit: 4096,
    rollupOptions: {
      output: {
        manualChunks: {
          monaco: ["monaco-editor/esm/vs/editor/editor.api"],
          arrow: ["apache-arrow"],
        },
      },
    },
  },
  worker: { format: "es" },
  test: {
    include: ["src/**/__tests__/**/*.test.{ts,tsx}", "scripts/**/__tests__/**/*.test.ts"],
    environment: "node",
    // UI component tests opt in per file with `// @vitest-environment jsdom`.
    // Slow runners (QUERITHM_TEST_TIME_SCALE, ci.yml): test and async-wait limits scale with it.
    setupFiles: ["src/testing/timeScale.setup.ts"],
    testTimeout: 5_000 * Math.max(1, Number(process.env.QUERITHM_TEST_TIME_SCALE) || 1),
    coverage: {
      provider: "v8",
      include: ["src/**/*.{ts,tsx}", "scripts/**/*.mjs"],
      exclude: ["**/__tests__/**", "**/*.d.ts", "src/ipc/contract.ts", "scripts/bench/**", "src-tauri/**"],
      reporter: ["text-summary", "json-summary"],
    },
  },
});
