import { useEffect, useState } from "react";
import { Route, Routes } from "react-router";
import { useServerUrl } from "./serverUrl";
import { Library } from "./components/Library";
import { Watch } from "./components/Watch";
import { DebugPanel } from "./components/DebugPanel";

export function App() {
  return (
    <>
      <Header />
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

// Статуса сервера в шапке нет намеренно: клиент говорит с библиотекой, а
// ffmpeg и кодек — свойства конкретного хоста, которых у неё несколько. Эти
// сведения и дебаг сессий показываются на странице фильма, где хост известен.
function Header() {
  const { serverUrl, setServerUrl } = useServerUrl();
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState(serverUrl);

  useEffect(() => setDraft(serverUrl), [serverUrl]);

  return (
    <header>
      <div className="bar">
        <h1>Prism</h1>
        <div className="spacer" />
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
          <label>URL библиотеки</label>
          <input
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            placeholder="http://localhost:8081"
            spellCheck={false}
          />
          <button type="submit">Сохранить</button>
        </form>
      )}
    </header>
  );
}
