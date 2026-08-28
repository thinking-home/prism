import { createContext, useCallback, useContext, useMemo, useState } from "react";
import type { ReactNode } from "react";

// Единственная настройка клиента — URL библиотеки. Хранится в localStorage.
const STORAGE_KEY = "prism.serverUrl";

function defaultServerUrl(): string {
  // Клиента раздаёт сама библиотека, поэтому по умолчанию её API — тот же
  // origin, и настраивать нечего. Настройка остаётся для разработки: Vite
  // поднимает клиент на своём порту, и адрес библиотеки вписывается руками.
  return window.location.origin;
}

function loadServerUrl(): string {
  return localStorage.getItem(STORAGE_KEY) ?? defaultServerUrl();
}

interface ServerUrlCtx {
  serverUrl: string;
  setServerUrl: (url: string) => void;
}

const Ctx = createContext<ServerUrlCtx | null>(null);

export function ServerUrlProvider({ children }: { children: ReactNode }) {
  const [serverUrl, setUrl] = useState<string>(loadServerUrl);

  const setServerUrl = useCallback((url: string) => {
    const cleaned = url.trim().replace(/\/+$/, "");
    localStorage.setItem(STORAGE_KEY, cleaned);
    setUrl(cleaned);
  }, []);

  const value = useMemo(() => ({ serverUrl, setServerUrl }), [serverUrl, setServerUrl]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useServerUrl(): ServerUrlCtx {
  const ctx = useContext(Ctx);
  if (!ctx) throw new Error("useServerUrl должен использоваться внутри ServerUrlProvider");
  return ctx;
}
