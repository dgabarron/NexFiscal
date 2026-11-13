import { useCallback, useEffect, useState } from "react";
import { api } from "../lib/api";

export function useBootstrap() {
  const [state, setState] = useState({ loading: true, hanaReady: false, error: "" });

  const refresh = useCallback(async () => {
    // Só bloqueia a navegação na inicialização. Atualizações não desmontam as telas.
    setState((s) => ({ ...s, error: "" }));
    try {
      const health = await api.get("/api/health");
      setState({ loading: false, hanaReady: !!health.hanaConfigured, error: "" });
    } catch (e) {
      setState({ loading: false, hanaReady: false, error: e.message });
    }
  }, []);

  useEffect(() => { refresh(); }, [refresh]);
  return { ...state, refresh };
}
