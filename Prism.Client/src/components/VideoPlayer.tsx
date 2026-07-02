import { useEffect, useRef } from "react";
import Hls from "hls.js";
import type { StreamType } from "../api";

export interface PlayerSubtitle {
  index: number;
  url: string;
  label: string;
  lang: string;
}

// Воспроизведение потока: HLS через hls.js (Chrome/Firefox/Edge) или нативно
// (Safari), для direct — обычный src. Субтитры — как <track> (независимо от видео,
// переключаются без пересборки потока). Смена аудиодорожки меняет src (новая HLS-
// сессия), при этом позиция воспроизведения сохраняется.
export function VideoPlayer({
  src,
  type,
  subtitles = [],
  selectedSub = -1,
}: {
  src: string;
  type: StreamType;
  subtitles?: PlayerSubtitle[];
  selectedSub?: number;
}) {
  const ref = useRef<HTMLVideoElement>(null);
  const lastTime = useRef(-1); // позиция для восстановления при смене аудиодорожки

  useEffect(() => {
    const video = ref.current;
    if (!video) return;

    const startAt = lastTime.current;
    const restoreNative = () => {
      if (startAt > 0) video.currentTime = startAt;
    };

    if (type === "hls" && Hls.isSupported()) {
      const hls = new Hls({ maxBufferLength: 30, startPosition: startAt });
      hls.loadSource(src);
      hls.attachMedia(video);
      return () => {
        lastTime.current = video.currentTime || -1;
        hls.destroy();
      };
    }

    // Нативный HLS (Safari) или прямой поток.
    video.src = src;
    video.addEventListener("loadedmetadata", restoreNative, { once: true });
    return () => {
      lastTime.current = video.currentTime || -1;
      video.removeEventListener("loadedmetadata", restoreNative);
    };
  }, [src, type]);

  // Показ выбранной дорожки субтитров. Зависит и от src, чтобы переприменить режим
  // после пересборки потока при смене аудио.
  useEffect(() => {
    const video = ref.current;
    if (!video) return;
    const tracks = video.textTracks;
    for (let i = 0; i < tracks.length; i++) {
      const tt = tracks[i];
      tt.mode = Number(tt.id) === selectedSub ? "showing" : "disabled";
    }
  }, [selectedSub, src, subtitles]);

  return (
    <video ref={ref} controls autoPlay playsInline crossOrigin="anonymous" className="player">
      {subtitles.map((s) => (
        <track key={s.index} id={String(s.index)} kind="subtitles" src={s.url} srcLang={s.lang} label={s.label} />
      ))}
    </video>
  );
}
