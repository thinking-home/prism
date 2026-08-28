import { useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { api, trackLabel } from "../api";
import type { MediaItem, ServerInfo } from "../api";
import { useServerUrl } from "../serverUrl";
import { VideoPlayer } from "./VideoPlayer";
import type { PlayerSubtitle } from "./VideoPlayer";

export function Watch() {
  const { id = "" } = useParams();
  const { serverUrl } = useServerUrl();
  const [item, setItem] = useState<MediaItem | null>(null);
  const [hostInfo, setHostInfo] = useState<ServerInfo | null>(null);
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

  // Хост-владелец записи: его база нужна и для субтитров, и для его же сведений.
  const hostUrl = item?.hostUrl ?? "";

  // Сведения о самом хосте (ffmpeg, кодек) — справочная строка, поэтому ошибку
  // не показываем: если хост не ответил, видно и так по невозможности играть.
  useEffect(() => {
    if (!hostUrl) return;
    const ac = new AbortController();
    setHostInfo(null);
    api.info(hostUrl, ac.signal).then(setHostInfo).catch(() => {});
    return () => ac.abort();
  }, [hostUrl]);

  // Только текстовые субтитры можно отдать как WebVTT.
  const textSubs = useMemo(
    () => (item?.subtitleTracks ?? []).filter((s) => s.textBased),
    [item],
  );
  const playerSubs: PlayerSubtitle[] = useMemo(
    () =>
      textSubs.map((s) => ({
        index: s.index,
        url: api.subtitleUrl(hostUrl, id, s.index),
        label: trackLabel(s, `Дорожка ${s.index + 1}`),
        lang: s.language ?? "und",
      })),
    [textSubs, hostUrl, id],
  );

  const back = (
    <Link to="/" className="back">
      ← Библиотека
    </Link>
  );

  if (error) return <>{back}<p className="muted">Ошибка: {error}</p></>;
  if (!item) return <>{back}<p className="muted">Загрузка…</p></>;

  // Хост в строке сведений и ссылка на его сессии — единственное место, где
  // клиент вообще знает про конкретный хост.
  const hostLine = (
    <>
      <span title={item.hostUrl}>хост: {item.host}</span>
      {hostInfo && (
        <span>ffmpeg: {hostInfo.ffmpegAvailable ? "есть" : "нет"} · кодек: {hostInfo.outputCodec}</span>
      )}
      <Link to={`/debug?host=${encodeURIComponent(item.hostUrl)}`}>сессии ffmpeg</Link>
    </>
  );

  // Состояния "pending" здесь не бывает: карточка /api/media/{id} на хосте
  // всегда ждёт полный resolve, в отличие от списка. Бейдж "разбирается" нужен
  // только в списке.
  const url = item.streamUrl; // абсолютный, на хост-владельца

  if (!item.playable || !url) {
    return (
      <>
        {back}
        <p><span className="badge unsupported">воспроизведение невозможно</span></p>
        <p className="muted">{item.statusMessage ?? "Этот файл нельзя воспроизвести."}</p>
        <div className="submeta">{hostLine}</div>
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
        {hostLine}
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
