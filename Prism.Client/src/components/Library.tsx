import { useEffect, useState } from "react";
import { Link } from "react-router";
import { api } from "../api";
import type { MediaItem } from "../api";
import { useServerUrl } from "../serverUrl";

export function Library() {
  const { serverUrl } = useServerUrl();
  const [items, setItems] = useState<MediaItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const ac = new AbortController();
    setItems(null);
    setError(null);
    api
      .media(serverUrl, ac.signal)
      .then(setItems)
      .catch((e) => {
        if (!ac.signal.aborted) setError(String(e.message ?? e));
      });
    return () => ac.abort();
  }, [serverUrl]);

  if (error) return <p className="muted">Не удалось загрузить библиотеку: {error}</p>;
  if (!items) return <p className="muted">Загрузка…</p>;
  if (items.length === 0)
    return <p className="muted">Медиафайлы не найдены. Положите видео в папку сервера и обновите.</p>;

  return (
    <div className="grid">
      {items.map((it) => (
        <Card key={it.id} item={it} />
      ))}
    </div>
  );
}

function Card({ item }: { item: MediaItem }) {
  const badge =
    item.streamType === "direct"
      ? <span className="badge direct">прямой</span>
      : item.streamType === "hls"
        ? <span className="badge transcode">транскод</span>
        : <span className="badge unsupported">недоступно</span>;

  const inner = (
    <div className="card">
      <div className="card-main">
        <div className="name">{item.title}</div>
        <div className="meta">{item.fileName}</div>
      </div>
      {badge}
    </div>
  );

  return item.playable ? (
    <Link to={`/watch/${item.id}`} className="card-link">
      {inner}
    </Link>
  ) : (
    <div className="card-link disabled" title={item.statusMessage ?? undefined}>
      {inner}
    </div>
  );
}
