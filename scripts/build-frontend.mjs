import { existsSync, writeFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const frontend = resolve(root, "frontend");
const vite = resolve(frontend, "node_modules", "vite", "bin", "vite.js");
const logPath = resolve(frontend, ".nexfiscal-build.log");
let output = [
  "NexFiscal — compilação do frontend",
  `Node: ${process.version} (${process.execPath})`,
  `Diretório: ${frontend}`,
].join("\n") + "\n";

function finish(message, status = 1) {
  output += message + "\n";
  try { writeFileSync(logPath, output, "utf8"); }
  catch (e) { console.error("Não foi possível gravar o diagnóstico:", e.message); }
  if (status === 0) {
    console.log("NexFiscal frontend compilado.");
  } else {
    console.error("NEXFISCAL-FRONTEND: compilação falhou.");
    console.error(message);
    console.error(`Diagnóstico completo: ${logPath}`);
    console.error("Confira o arquivo acima; o MSB3073 é apenas consequência.");
  }
  process.exit(status);
}

if (!existsSync(vite)) {
  finish("Vite ausente em node_modules. A instalação de pacotes parece incompleta. Feche processos Node/Vite e restaure as dependências uma vez; não execute npm install repetidamente pelo F5.");
}

const result = spawnSync(process.execPath, [vite, "build"], {
  cwd: frontend, encoding: "utf8", windowsHide: true, maxBuffer: 20 * 1024 * 1024,
});
output += (result.stdout ?? "") + "\n" + (result.stderr ?? "") + "\n";
if (result.error) output += `Erro de processo: ${result.error.message}\n`;
if (result.status !== 0 || result.error) {
  const tail = output.trim().split(/\r?\n/).slice(-35).join("\n");
  finish(tail);
}
finish("Vite build concluído com sucesso.", 0);
