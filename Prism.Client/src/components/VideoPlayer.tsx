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
// (Safari), для direct — обычный src. Для HLS src — master-плейлист: аудиодорожки
// переключаются внутри него самим плеером, без смены src и потери позиции.
// Субтитры — как <track> (независимо от видео), но в DOM живёт только выбранная
// дорожка: браузер грузит <track> сразу, как тот появился, а извлечение каждой
// дорожки стоит хосту отдельного прохода ffmpeg по всему файлу.
export function VideoPlayer({
  src,
  type,
  subtitles = [],
  selectedSub = -1,
  audioTrack = 0,
}: {
  src: string;
  type: StreamType;
  subtitles?: PlayerSubtitle[];
  selectedSub?: number;
  audioTrack?: number;
}) {
  const ref = useRef<HTMLVideoElement>(null);
  const hlsRef = useRef<Hls | null>(null);

  useEffect(() => {
    const video = ref.current;
    if (!video) return;

    if (type === "hls" && Hls.isSupported()) {
      // Субтитры в браузере показываем через <track>, поэтому свои текстовые
      // дорожки из рендиций master-плейлиста hls.js не создаёт: иначе один и тот
      // же текст рисуют оба. Рендиции в плейлисте нужны плеерам вроде ExoPlayer.
      const hls = new Hls({ maxBufferLength: 30, renderTextTracksNatively: false });
      hlsRef.current = hls;
      hls.loadSource(src);
      hls.attachMedia(video);
      return () => {
        hlsRef.current = null;
        hls.destroy();
      };
    }

    // Нативный HLS (Safari) или прямой поток.
    video.src = src;
  }, [src, type]);

  // Переключение аудиодорожки: hls.js — рендиции AUDIO из master-плейлиста;
  // нативный HLS (Safari) — стандартный audioTracks на <video>.
  useEffect(() => {
    const hls = hlsRef.current;
    if (hls) {
      if (audioTrack >= 0 && audioTrack < hls.audioTracks.length) hls.audioTrack = audioTrack;
      return;
    }
    const tracks = (ref.current as any)?.audioTracks;
    if (tracks) {
      for (let i = 0; i < tracks.length; i++) tracks[i].enabled = i === audioTrack;
    }
  }, [audioTrack, src]);

  // Показ выбранной дорожки. Своя дорожка в DOM одна, но перебираем именно
  // свои <track>, а не весь video.textTracks: у чужих дорожек (рендиции плеера)
  // id пустой, а Number("") === 0 — они включились бы заодно с дорожкой 0.
  useEffect(() => {
    const video = ref.current;
    if (!video) return;
    video.querySelectorAll("track").forEach((el) => {
      el.track.mode = Number(el.id) === selectedSub ? "showing" : "disabled";
    });
  }, [selectedSub, src, subtitles]);

  // Только выбранная дорожка: при «Выкл» не грузится ни одна, при смене —
  // ровно одна новая (ключ по индексу пересоздаёт элемент).
  const active = subtitles.find((s) => s.index === selectedSub);

  return (
    <video ref={ref} controls autoPlay playsInline crossOrigin="anonymous" className="player">
      {active && (
        <track
          key={active.index}
          id={String(active.index)}
          kind="subtitles"
          src={active.url}
          srcLang={active.lang}
          label={active.label}
        />
      )}
    </video>
  );
}
