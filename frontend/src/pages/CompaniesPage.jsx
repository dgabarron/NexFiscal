import { useState } from "react";
import { Building2, Plus, CheckCircle2, AlertCircle } from "lucide-react";
import { Card, CardContent } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { A1Form } from "../components/forms/A1Form";
import { formatCnpj } from "../lib/fiscal-format";

export function CompaniesPage({ companies, onCompanyAdded }) {
  const [adding, setAdding] = useState(false);
  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-bold text-slate-900">Empresas e certificados</h1>
          <p className="text-sm text-slate-500 mt-1">
            Cada empresa é identificada pelo certificado A1. Não é possível alterar o CNPJ manualmente.
          </p>
        </div>
        <Button onClick={() => setAdding((v) => !v)}>
          <Plus size={16} />{adding ? "Cancelar" : "Adicionar empresa"}
        </Button>
      </div>
      {adding && <A1Form onRegistered={(result) => { setAdding(false); onCompanyAdded?.(result); }} />}
      {companies.length === 0 && !adding && (
        <p className="text-sm text-slate-500">Nenhuma empresa cadastrada.</p>
      )}
      <div className="grid gap-4 sm:grid-cols-2">
        {companies.map((company) => (
          <Card key={company.companyId}>
            <CardContent className="pt-6">
              <div className="flex items-start gap-3">
                <div className="p-2 bg-blue-50 rounded-lg text-blue-600"><Building2 size={20} /></div>
                <div className="flex-1">
                  <h3 className="font-bold">{company.legalName}</h3>
                  <p className="text-sm text-slate-500 mt-0.5">CNPJ {formatCnpj(company.cnpj)}</p>
                  <div className="mt-2 flex items-center gap-1 text-xs">
                    {company.certificateStored
                      ? <><CheckCircle2 size={14} className="text-emerald-500" />Certificado A1 disponível</>
                      : <><AlertCircle size={14} className="text-amber-500" />Certificado ausente</>}
                  </div>
                </div>
              </div>
            </CardContent>
          </Card>
        ))}
      </div>
    </div>
  );
}
