import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api } from "../api";
import type { MediaItem } from "../api";
import { useServerUrl } from "../serverUrl";
import { VideoPlayer } from "./VideoPlayer";

export function Watch() {
  const { id = "" } = useParams();
  const { serverUrl } = useServerUrl();
  const [item, setItem] = useState<MediaItem | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const ac = new AbortController();
    setItem(null);
    setError(null);
    api
      .mediaItem(serverUrl, id, ac.signal)
      .then(setItem)
      .catch((e) => {
        if (!ac.signal.aborted) setError(String(e.message ?? e));
      });
    return () => ac.abort();
  }, [serverUrl, id]);

  const back = (
    <Link to="/" className="back">
      ← Библиотека
    </Link>
  );

  if (error) return <>{back}<p className="muted">Ошибка: {error}</p></>;
  if (!item) return <>{back}<p className="muted">Загрузка…</p></>;

  const url = api.streamUrl(serverUrl, item);

  if (!item.playable || !url) {
    return (
      <>
        {back}
        <p><span className="badge unsupported">воспроизведение невозможно</span></p>
        <p className="muted">{item.statusMessage ?? "Этот файл нельзя воспроизвести."}</p>
      </>
    );
  }

  return (
    <>
      {back}
      <h2 className="title">{item.title}</h2>
      <div className="submeta">
        {item.width > 0 && <span>{item.width}×{item.height}</span>}
        <span>источник: {item.videoCodec ?? "?"}{item.audioCodec ? ` / ${item.audioCodec}` : ""}</span>
        <span>{item.streamType === "hls" ? "транскодирование HLS" : "прямой поток"}</span>
      </div>
      <VideoPlayer src={url} type={item.streamType} />
      <div className="muted small">URL потока: <code>{url}</code></div>
    </>
  );
}
