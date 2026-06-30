// Слой доступа к API сервера Prism.Host. Базовый URL приходит снаружи —
// это единственная настройка клиента.

export type StreamType = "hls" | "direct" | "unsupported";

export interface ServerInfo {
  name: string;
  ffmpegAvailable: boolean;
  outputCodec: string;
  segmentSeconds: number;
  audioBitrateKbps: number;
  audioSampleRate: number;
  mediaDirectory: string;
  mediaCount: number;
}

export interface MediaItem {
  id: string;
  title: string;
  fileName: string;
  streamType: StreamType;
  playable: boolean;
  streamUrl: string | null;
  durationSeconds: number;
  width: number;
  height: number;
  videoCodec: string | null;
  audioCodec: string | null;
  audioChannels: number;
  statusMessage: string | null;
}

function join(base: string, path: string): string {
  return base.replace(/\/+$/, "") + path;
}

async function getJson<T>(base: string, path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(join(base, path), { signal });
  if (!res.ok) throw new Error(`HTTP ${res.status} для ${path}`);
  return (await res.json()) as T;
}

export const api = {
  info: (base: string, signal?: AbortSignal) => getJson<ServerInfo>(base, "/api/info", signal),
  media: (base: string, signal?: AbortSignal) => getJson<MediaItem[]>(base, "/api/media", signal),
  mediaItem: (base: string, id: string, signal?: AbortSignal) =>
    getJson<MediaItem>(base, `/api/media/${id}`, signal),
  // Абсолютный URL потока для тега <video> / hls.js.
  streamUrl: (base: string, item: MediaItem): string | null =>
    item.streamUrl ? join(base, item.streamUrl) : null,
};
