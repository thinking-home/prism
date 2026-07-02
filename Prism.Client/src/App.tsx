import { useEffect, useState } from "react";
import { Link, Route, Routes } from "react-router-dom";
import { api } from "./api";
import type { ServerInfo } from "./api";
import { useServerUrl } from "./serverUrl";
import { Library } from "./components/Library";
import { Watch } from "./components/Watch";
import { DebugPanel } from "./components/DebugPanel";

export function App() {
  const { serverUrl } = useServerUrl();
  const [info, setInfo] = useState<ServerInfo | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Перезапрашиваем данные сервера при смене его URL.
  useEffect(() => {
    const ac = new AbortController();
    setInfo(null);
    setError(null);
    api
      .info(serverUrl, ac.signal)
      .then(setInfo)
      .catch((e) => {
        if (!ac.signal.aborted) setError(String(e.message ?? e));
      });
    return () => ac.abort();
  }, [serverUrl]);

  return (
    <>
      <Header info={info} error={error} />
      <main>
        <Routes>
          <Route path="/" element={<Library />} />
          <Route path="/watch/:id" element={<Watch />} />
          <Route path="/debug" element={<DebugPanel />} />
        </Routes>
      </main>
    </>
  );
}

function Header({ info, error }: { info: ServerInfo | null; error: string | null }) {
  const { serverUrl, setServerUrl } = useServerUrl();
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState(serverUrl);

  useEffect(() => setDraft(serverUrl), [serverUrl]);

  const status = error
    ? <span className="status err">сервер недоступен</span>
    : info
      ? <span className="status ok">
          {info.name} · ffmpeg: {info.ffmpegAvailable ? "доступен" : "нет"} · кодек: {info.outputCodec}
        </span>
      : <span className="status">подключение…</span>;

  return (
    <header>
      <div className="bar">
        <h1>Prism</h1>
        <div className="spacer" />
        {status}
        <Link to="/debug" className="gear" title="Дебаг сессий">debug</Link>
        <button className="gear" onClick={() => setOpen((v) => !v)} title="Настройки сервера">
          ⚙
        </button>
      </div>
      {open && (
        <form
          className="settings"
          onSubmit={(e) => {
            e.preventDefault();
            setServerUrl(draft);
            setOpen(false);
          }}
        >
          <label>URL сервера</label>
          <input
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            placeholder="http://localhost:8080"
            spellCheck={false}
          />
          <button type="submit">Сохранить</button>
        </form>
      )}
    </header>
  );
}
