import { HanaForm } from "../components/forms/HanaForm";
import { A1Form } from "../components/forms/A1Form";
import { Card, CardHeader, CardContent } from "../components/ui/card";

const STEPS = [
  { key: "hana", label: "Conexão SAP HANA" },
  { key: "a1", label: "Empresa e certificado" },
];

export function SetupPage({ step, onHanaSaved, onCompanyRegistered }) {
  return (
    <div className="min-h-screen bg-[#f5f7fb] flex flex-col">
      <header className="border-b border-slate-200 bg-white px-6 py-5">
        <div className="mx-auto max-w-4xl">
          <div className="flex items-center gap-3">
            <div className="rounded-xl bg-blue-50 p-2 text-blue-600 text-lg font-bold">NF</div>
            <div>
              <div className="font-bold text-slate-900">NexFiscal</div>
              <div className="text-xs text-slate-500">Configuração inicial</div>
            </div>
          </div>
        </div>
      </header>
      <main className="mx-auto w-full max-w-4xl px-4 py-10 sm:px-6 flex-1">
        <ol className="mb-8 flex gap-4">
          {STEPS.map((s, i) => {
            const active = i === step, done = i < step;
            return (
              <li key={s.key} className="flex-1">
                <div className={`h-1 rounded-full ${done ? "bg-emerald-500" : active ? "bg-blue-600" : "bg-slate-200"}`} />
                <div className={`mt-2 text-xs font-semibold ${active ? "text-blue-700" : done ? "text-emerald-700" : "text-slate-400"}`}>
                  {i + 1}. {s.label}
                </div>
              </li>
            );
          })}
        </ol>
        {step === 0 && <HanaForm onSaved={onHanaSaved} />}
        {step === 1 && <A1Form onRegistered={onCompanyRegistered} />}
        <Card className="mt-6 bg-white/60">
          <CardHeader><h3 className="text-sm font-bold">Sobre este ambiente</h3></CardHeader>
          <CardContent>
            <p className="text-xs text-slate-600 leading-5">
              O NexFiscal é executado on-premises. Credenciais e certificados são
              processados pelo backend, nunca persistidos no navegador. Após concluir, o
              sistema abre a Central de operação e a captura permanece pausada até uma
              inicialização fiscal segura.
            </p>
          </CardContent>
        </Card>
      </main>
    </div>
  );
}
