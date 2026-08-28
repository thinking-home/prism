// Слой доступа к API. Клиент работает с библиотекой — её базовый URL и есть
// единственная настройка. Но часть данных живёт на хостах: поток, субтитры и
// сессии транскодирования отдаёт хост-владелец файла. Его адрес приходит в
// каждой записи каталога полем hostUrl, поэтому к хосту клиент ходит только
// там, где известна конкретная запись (страница фильма).

export type StreamType = "hls" | "direct" | "pending" | "unsupported";

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
  relativePath: string;
  // Хост-владелец: имя из конфига библиотеки и его база — по ней доступны
  // собственные ручки хоста (сведения о сервере, субтитры, сессии).
  host: string;
  hostUrl: string;
  // pending — хост ещё не разобрал файл; метаданные и режим появятся сами.
  streamType: StreamType;
  playable: boolean;
  // Абсолютный: библиотека подставляет базу хоста-владельца, браузер стримит
  // с хоста напрямую.
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

// Дерево библиотеки. Группы плоским списком — вложенность через parentId;
// членство отдельным списком, файл может быть в нескольких группах.
// present:false — запись о файле, которого сейчас нет ни на одном доступном хосте.
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
  // ---- Библиотека: каталог всех хостов, дерево групп и мета ----------------
  media: (base: string, signal?: AbortSignal) => getJson<MediaItem[]>(base, "/api/media", signal),
  mediaItem: (base: string, id: string, signal?: AbortSignal) =>
    getJson<MediaItem>(base, `/api/media/${id}`, signal),
  tree: (base: string, signal?: AbortSignal) => getJson<LibraryTree>(base, "/api/library/tree", signal),

  // ---- Хост-владелец записи: база берётся из её поля hostUrl ---------------
  info: (hostUrl: string, signal?: AbortSignal) => getJson<ServerInfo>(hostUrl, "/api/info", signal),
  debug: (hostUrl: string, signal?: AbortSignal) =>
    getJson<DebugInfo>(hostUrl, "/api/debug/sessions", signal),

  // Абсолютный URL дорожки субтитров в WebVTT — их извлекает хост.
  subtitleUrl: (hostUrl: string, id: string, index: number): string =>
    join(hostUrl, `/api/media/${id}/subtitle/${index}.vtt`),
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
