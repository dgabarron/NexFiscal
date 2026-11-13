import { createContext, useCallback, useContext, useMemo, useState } from "react";

const NoticesContext = createContext(null);

export function NoticesProvider({ children }) {
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const clear = useCallback(() => { setError(""); setMessage(""); }, []);
  const notify = useMemo(() => ({
    error: (text) => { setError(text); setMessage(""); },
    success: (text) => { setMessage(text); setError(""); },
  }), []);
  const value = useMemo(() => ({ error, message, clear, notify }), [error, message, clear, notify]);
  return <NoticesContext.Provider value={value}>{children}</NoticesContext.Provider>;
}

export function useNotices() {
  const ctx = useContext(NoticesContext);
  if (!ctx) throw new Error("useNotices precisa estar dentro de <NoticesProvider>.");
  return ctx;
}
