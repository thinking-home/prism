import { useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api, trackLabel } from "../api";
import type { MediaItem } from "../api";
import { useServerUrl } from "../serverUrl";
import { VideoPlayer } from "./VideoPlayer";
import type { PlayerSubtitle } from "./VideoPlayer";

export function Watch() {
  const { id = "" } = useParams();
  const { serverUrl } = useServerUrl();
  const [item, setItem] = useState<MediaItem | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [audio, setAudio] = useState(0);
  const [sub, setSub] = useState(-1); // -1 = субтитры выключены

  useEffect(() => {
    const ac = new AbortController();
    setItem(null);
    setError(null);
    setAudio(0);
    setSub(-1);
    api
      .mediaItem(serverUrl, id, ac.signal)
      .then(setItem)
      .catch((e) => {
        if (!ac.signal.aborted) setError(String(e.message ?? e));
      });
    return () => ac.abort();
  }, [serverUrl, id]);

  // Только текстовые субтитры можно отдать как WebVTT.
  const textSubs = useMemo(
    () => (item?.subtitleTracks ?? []).filter((s) => s.textBased),
    [item],
  );
  const playerSubs: PlayerSubtitle[] = useMemo(
    () =>
      textSubs.map((s) => ({
        index: s.index,
        url: api.subtitleUrl(serverUrl, id, s.index),
        label: trackLabel(s, `Дорожка ${s.index + 1}`),
        lang: s.language ?? "und",
      })),
    [textSubs, serverUrl, id],
  );

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

  const showAudioPicker = item.audioTracks.length > 1;
  const showSubPicker = textSubs.length > 0;

  return (
    <>
      {back}
      <h2 className="title">{item.title}</h2>
      <div className="submeta">
        {item.width > 0 && <span>{item.width}×{item.height}</span>}
        <span>источник: {item.videoCodec ?? "?"}{item.audioCodec ? ` / ${item.audioCodec}` : ""}</span>
        <span>{item.streamType === "hls" ? "транскодирование HLS" : "прямой поток"}</span>
      </div>

      <VideoPlayer src={url} type={item.streamType} subtitles={playerSubs} selectedSub={sub} audioTrack={audio} />

      {(showAudioPicker || showSubPicker) && (
        <div className="tracks">
          {showAudioPicker && (
            <label>
              Аудио
              <select value={audio} onChange={(e) => setAudio(Number(e.target.value))}>
                {item.audioTracks.map((t) => (
                  <option key={t.index} value={t.index}>
                    {trackLabel(t, `Дорожка ${t.index + 1}`)}
                  </option>
                ))}
              </select>
            </label>
          )}
          {showSubPicker && (
            <label>
              Субтитры
              <select value={sub} onChange={(e) => setSub(Number(e.target.value))}>
                <option value={-1}>Выкл</option>
                {textSubs.map((s) => (
                  <option key={s.index} value={s.index}>
                    {trackLabel(s, `Дорожка ${s.index + 1}`)}
                  </option>
                ))}
              </select>
            </label>
          )}
        </div>
      )}

      <div className="muted small">URL потока: <code>{url}</code></div>
    </>
  );
}
