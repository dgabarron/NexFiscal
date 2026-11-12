import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: { outDir: "../src/NexFiscal.Web/wwwroot", emptyOutDir: true },
  server: { proxy: { "/api": "http://127.0.0.1:5187" } }
});
