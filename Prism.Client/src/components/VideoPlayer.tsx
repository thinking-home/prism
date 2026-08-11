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
// Субтитры — как <track> (независимо от видео).
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
      const hls = new Hls({ maxBufferLength: 30 });
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

  // Показ выбранной дорожки субтитров.
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
