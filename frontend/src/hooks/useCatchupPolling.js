import { useEffect, useRef } from "react";
import { api } from "../lib/api";

export function useCatchupPolling(runs, setRuns, companies = [], documentType = "NFE") {
  const route = documentType === "CTE" ? "/api/sefaz/cte/catchup" : "/api/sefaz/nfe/catchup";
  const companyKey = companies.map((c) => c.companyId).join(",");
  const runsRef = useRef(runs);
  runsRef.current = runs;

  // Reidrata o estado do job ao entrar na página ou após atualizar o navegador.
  // Consulta apenas o backend local; não gera chamadas fiscais à SEFAZ.
  useEffect(() => {
    let cancelled = false;
    const ids = companyKey ? companyKey.split(",") : [];
    Promise.all(ids.map((id) => api.get(`${route}/${id}`).catch(() => null)))
      .then((items) => {
        if (cancelled) return;
        setRuns((prev) => {
          const next = { ...prev };
          ids.forEach((id, i) => { if (items[i]) next[id] = items[i]; });
          return next;
        });
      });
    return () => { cancelled = true; };
  }, [companyKey, setRuns, route]);

  useEffect(() => {
    const timer = setInterval(async () => {
      const running = Object.entries(runsRef.current).filter(([, job]) => job?.status === "running" || job?.status === "scheduled");
      if (running.length === 0) return;
      const results = await Promise.all(
        running.map(([companyId]) =>
          api.get(`${route}/${companyId}`).catch(() => null)
        )
      );
      setRuns((prev) => {
        const next = { ...prev };
        running.forEach(([companyId], i) => { if (results[i]) next[companyId] = results[i]; });
        return next;
      });
    }, 3000);
    return () => clearInterval(timer);
  }, [setRuns, route]);
}
