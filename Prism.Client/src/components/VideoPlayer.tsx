import { useEffect, useRef } from "react";
import Hls from "hls.js";
import type { StreamType } from "../api";

// Воспроизведение потока: HLS через hls.js (Chrome/Firefox/Edge) или нативно
// (Safari), для direct — обычный src тега <video>.
export function VideoPlayer({ src, type }: { src: string; type: StreamType }) {
  const ref = useRef<HTMLVideoElement>(null);

  useEffect(() => {
    const video = ref.current;
    if (!video) return;

    if (type === "hls") {
      if (Hls.isSupported()) {
        const hls = new Hls({ maxBufferLength: 30 });
        hls.loadSource(src);
        hls.attachMedia(video);
        return () => hls.destroy();
      }
      if (video.canPlayType("application/vnd.apple.mpegurl")) {
        video.src = src; // нативный HLS (Safari)
        return;
      }
      return;
    }

    video.src = src; // direct
  }, [src, type]);

  return <video ref={ref} controls autoPlay playsInline className="player" />;
}
