import { useState } from "react";
import { Server, ShieldCheck, Database, Info } from "lucide-react";
import { Card, CardHeader, CardContent } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Notice } from "../components/common/Notice";
import { useNotices } from "../components/common/NoticesContext";
import { api } from "../lib/api";

export function SettingsPage({ hanaReady }) {
  const { notify } = useNotices();
  const [provisioning, setProvisioning] = useState(false);

  async function provision() {
    setProvisioning(true);
    try {
      const result = await api.post("/api/setup/hana/provision", {});
      if (result.schemaReady) notify.success("Estrutura BRAI_NEX validada.");
      else notify.error(`Ainda faltam tabelas: ${result.tablesFound} de 5.`);
    } catch (e) { notify.error(e.message); }
    finally { setProvisioning(false); }
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Configurações</h1>
        <p className="text-sm text-slate-500 mt-1">
          Área administrativa. Alterações de conexão HANA e credenciais exigem o wizard de
          instalação; aqui apenas verificamos o estado atual.
        </p>
      </div>
      <Card>
        <CardHeader>
          <div className="flex items-center gap-3">
            <div className="p-2.5 bg-blue-50 rounded-lg text-blue-600"><Server size={20} /></div>
            <div>
              <h3 className="text-base font-bold">Conexão SAP HANA</h3>
              <p className="text-sm text-slate-500 mt-0.5">Schema BRAI_NEX</p>
            </div>
          </div>
        </CardHeader>
        <CardContent>
          <div className="flex items-center gap-2 text-sm">
            {hanaReady
              ? <><ShieldCheck size={18} className="text-emerald-500" />Conexão configurada e protegida no servidor</>
              : <><Info size={18} className="text-amber-500" />Conexão ainda não configurada</>}
          </div>
          <div className="mt-4 flex flex-wrap gap-3">
            <Button variant="outline" disabled={!hanaReady || provisioning} onClick={provision}>
              <Database size={16} />{provisioning ? "Verificando..." : "Verificar estrutura"}
            </Button>
          </div>
          <div className="mt-4">
            <Notice type="info">
              Não é possível alterar host, porta, usuário e senha sem reabrir o wizard de
              instalação. Isso evita que credenciais salvas no servidor sejam sobrescritas
              acidentalmente.
            </Notice>
          </div>
        </CardContent>
      </Card>
      <Card>
        <CardHeader><h3 className="text-base font-bold">Sobre esta instalação</h3></CardHeader>
        <CardContent>
          <ul className="text-sm text-slate-600 space-y-1.5 list-disc pl-5">
            <li>Versão de frontend: <code>0.3.0</code></li>
            <li>Portal acessível apenas por <code>127.0.0.1</code> nesta versão.</li>
            <li>Autenticação multiusuário e exposição em rede exigem backend dedicado.</li>
            <li>Integração SAP Business One não faz parte desta entrega.</li>
          </ul>
        </CardContent>
      </Card>
    </div>
  );
}
