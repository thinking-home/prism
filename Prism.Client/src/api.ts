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
  mediaDirectories: string[];
  mediaCount: number;
}

export interface AudioTrack {
  index: number;
  codec: string | null;
  language: string | null;
  title: string | null;
  channels: number;
}

export interface SubtitleTrack {
  index: number;
  codec: string | null;
  language: string | null;
  title: string | null;
  textBased: boolean;
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
  audioTracks: AudioTrack[];
  subtitleTracks: SubtitleTrack[];
  statusMessage: string | null;
}

// Дерево библиотеки (плагин Prism.Plugins.Library). Группы плоским списком —
// вложенность через parentId; членство отдельным списком, файл может быть в
// нескольких группах. present:false — запись о файле, которого сейчас нет на диске.
export interface LibraryNode {
  id: string;
  parentId: string | null;
  name: string;
  meta: Record<string, string>;
}

export interface LibraryMembership {
  nodeId: string;
  mediaId: string;
  present: boolean;
}

export interface LibraryTree {
  nodes: LibraryNode[];
  items: LibraryMembership[];
}

export interface SessionInfo {
  mediaId: string;
  startIndex: number;
  endIndex: number;
  stream: string; // "v" — видео, "aN" — аудиодорожка N

  produced: number;
  total: number;
  alive: boolean;
  pid: number;
  memoryBytes: number;
  cpuSeconds: number;
  idleSeconds: number;
}

export interface DebugInfo {
  serverCpuSeconds: number;
  cpuCount: number;
  sessions: SessionInfo[];
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
  debug: (base: string, signal?: AbortSignal) => getJson<DebugInfo>(base, "/api/debug/sessions", signal),

  // Дерево библиотеки. Плагин метаданных опционален, поэтому отсутствие ручки —
  // не ошибка: пустое дерево означает «все файлы вне групп».
  tree: (base: string, signal?: AbortSignal) =>
    getJson<LibraryTree>(base, "/api/library/tree", signal).catch(
      (e): LibraryTree => {
        if (signal?.aborted) throw e;
        return { nodes: [], items: [] };
      },
    ),

  // Абсолютный URL потока для <video>/hls.js. Для HLS это master-плейлист —
  // дорожки внутри него переключает сам плеер, URL от них не зависит.
  streamUrl: (base: string, item: MediaItem): string | null =>
    item.streamUrl ? join(base, item.streamUrl) : null,

  // Абсолютный URL дорожки субтитров в WebVTT.
  subtitleUrl: (base: string, id: string, index: number): string =>
    join(base, `/api/media/${id}/subtitle/${index}.vtt`),
};

// Человекочитаемая подпись дорожки.
export function trackLabel(
  t: { language: string | null; title: string | null; codec: string | null; channels?: number },
  fallback: string,
): string {
  const parts: string[] = [];
  if (t.title) parts.push(t.title);
  if (t.language) parts.push(t.language);
  if (t.channels && t.channels > 2) parts.push(`${t.channels}ch`);
  if (parts.length === 0 && t.codec) parts.push(t.codec);
  return parts.length ? parts.join(" · ") : fallback;
}
