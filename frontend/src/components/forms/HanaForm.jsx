import { useState } from "react";
import { Database, Eye, EyeOff, PlugZap, ShieldCheck, ArrowRight } from "lucide-react";
import { Card, CardHeader, CardContent } from "../ui/card";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { Field } from "../common/Field";
import { api } from "../../lib/api";
import { useNotices } from "../common/NoticesContext";

export function HanaForm({ onSaved }) {
  const { notify } = useNotices();
  const [hana, setHana] = useState({ server: "", port: "30015", username: "", password: "" });
  const [showPass, setShowPass] = useState(false);
  const [checked, setChecked] = useState(false);
  const [tablesFound, setTablesFound] = useState(null);
  const [busy, setBusy] = useState(false);

  function setField(field, value) {
    setHana((s) => ({ ...s, [field]: value }));
    setChecked(false); setTablesFound(null);
    notify.error(""); notify.success("");
  }

  async function runTest(save) {
    setBusy(true); notify.error("");
    try {
      const result = await api.post(
        save ? "/api/setup/hana/save" : "/api/setup/hana/test",
        { server: hana.server, port: Number(hana.port), username: hana.username, password: hana.password }
      );
      setChecked(result.schemaReady);
      setTablesFound(result.tablesFound);
      if (!result.schemaReady) {
        throw new Error(`Faltam tabelas no HANA: ${result.tablesFound} de 5 tabelas BRAI_NEX. Use "Preparar estrutura".`);
      }
      if (save) { notify.success("Conexão HANA validada e salva de forma protegida no servidor."); onSaved?.(); }
      else { notify.success("Conexão estabelecida. Schema BRAI_NEX e cinco tabelas verificados."); }
    } catch (e) { notify.error(e.message); }
    finally { setBusy(false); }
  }

  async function provision() {
    setBusy(true); notify.error("");
    try {
      const result = await api.post("/api/setup/hana/provision", {
        server: hana.server, port: Number(hana.port), username: hana.username, password: hana.password,
      });
      setTablesFound(result.tablesFound);
      setChecked(result.schemaReady);
      if (!result.schemaReady) throw new Error("Instalação incompleta. Consulte o administrador do HANA.");
      notify.success("Estrutura BRAI_NEX preparada. Clique em Salvar e continuar.");
    } catch (e) { notify.error(e.message); }
    finally { setBusy(false); }
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-3">
          <div className="p-3 bg-blue-50 rounded-xl text-blue-600"><Database size={22} /></div>
          <div>
            <h2 className="text-lg font-bold">Conexão SAP HANA</h2>
            <p className="text-sm text-slate-500 mt-0.5">Banco intermediário BRAI_NEX · configuração única</p>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <div className="grid gap-5 sm:grid-cols-[1fr_145px]">
          <Field label="Servidor / host" htmlFor="hana-server">
            <Input id="hana-server" placeholder="hana.empresa.local" autoComplete="off"
              value={hana.server} onChange={(e) => setField("server", e.target.value)} />
          </Field>
          <Field label="Porta SQL" htmlFor="hana-port">
            <Input id="hana-port" inputMode="numeric" placeholder="30015"
              value={hana.port} onChange={(e) => setField("port", e.target.value)} />
          </Field>
        </div>
        <div className="mt-5 grid gap-5 sm:grid-cols-2">
          <Field label="Usuário do banco" htmlFor="hana-user">
            <Input id="hana-user" placeholder="NEXFISCAL_APP" autoComplete="off"
              value={hana.username} onChange={(e) => setField("username", e.target.value)} />
          </Field>
          <Field label="Senha do usuário" htmlFor="hana-pass">
            <div className="relative">
              <Input id="hana-pass" type={showPass ? "text" : "password"} autoComplete="new-password"
                className="pr-12" value={hana.password}
                onChange={(e) => setField("password", e.target.value)} />
              <button type="button" onClick={() => setShowPass((v) => !v)}
                aria-label={showPass ? "Ocultar senha" : "Mostrar senha"}
                className="absolute right-3 top-2.5 text-slate-400">
                {showPass ? <EyeOff size={18} /> : <Eye size={18} />}
              </button>
            </div>
          </Field>
        </div>
        <div className="mt-6 flex flex-wrap gap-3 border-t border-slate-100 pt-5">
          <Button variant="outline" disabled={busy || !hana.server || !hana.username || !hana.password} onClick={() => runTest(false)}>
            <PlugZap size={16} />{busy ? "Conectando..." : "Testar conexão"}
          </Button>
          {tablesFound !== null && !checked && (
            <Button variant="outline" disabled={busy} onClick={provision}>
              <Database size={16} />Preparar estrutura
            </Button>
          )}
          <Button disabled={busy || !checked} onClick={() => runTest(true)}>
            <ShieldCheck size={16} />Salvar e continuar<ArrowRight size={16} />
          </Button>
        </div>
        {checked && (
          <p className="mt-4 text-xs text-emerald-700">
            Estrutura validada — {tablesFound} de 5 tabelas encontradas.
          </p>
        )}
      </CardContent>
    </Card>
  );
}
