import { useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import {
  Alert,
  Anchor,
  Badge,
  Code,
  Group,
  Loader,
  Select,
  Stack,
  Text,
  Title,
} from "@mantine/core";
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
    <Anchor component={Link} to="/" size="sm">
      ← Библиотека
    </Anchor>
  );

  if (error)
    return (
      <Stack gap="sm">
        {back}
        <Alert color="red" title="Ошибка">
          {error}
        </Alert>
      </Stack>
    );

  if (!item)
    return (
      <Stack gap="sm">
        {back}
        <Group justify="center" py="xl">
          <Loader />
        </Group>
      </Stack>
    );

  // Хост в строке сведений и ссылка на его сессии — единственное место, где
  // клиент вообще знает про конкретный хост.
  const hostLine = (
    <Group gap="md">
      <Text size="sm" c="dimmed" title={item.hostUrl}>
        хост: {item.host}
      </Text>
      {hostInfo && (
        <Text size="sm" c="dimmed">
          ffmpeg: {hostInfo.ffmpegAvailable ? "есть" : "нет"} · кодек: {hostInfo.outputCodec}
        </Text>
      )}
      <Anchor component={Link} to={`/debug?host=${encodeURIComponent(item.hostUrl)}`} size="sm">
        сессии ffmpeg
      </Anchor>
    </Group>
  );

  // Состояния "pending" здесь не бывает: карточка /api/media/{id} на хосте
  // всегда ждёт полный resolve, в отличие от списка.
  const url = item.streamUrl; // абсолютный, на хост-владельца

  if (!item.playable || !url)
    return (
      <Stack gap="sm">
        {back}
        <Alert color="yellow" title="Воспроизведение невозможно">
          {item.statusMessage ?? "Этот файл нельзя воспроизвести."}
        </Alert>
        {hostLine}
      </Stack>
    );

  return (
    <Stack gap="sm">
      {back}
      <Title order={2}>{item.title}</Title>

      <Group gap="md">
        {item.width > 0 && (
          <Text size="sm" c="dimmed">
            {item.width}×{item.height}
          </Text>
        )}
        <Text size="sm" c="dimmed">
          источник: {item.videoCodec ?? "?"}
          {item.audioCodec ? ` / ${item.audioCodec}` : ""}
        </Text>
        <Badge variant="light" color={item.streamType === "hls" ? "blue" : "green"}>
          {item.streamType === "hls" ? "транскодирование HLS" : "прямой поток"}
        </Badge>
      </Group>
      {hostLine}

      <VideoPlayer
        src={url}
        type={item.streamType}
        subtitles={playerSubs}
        selectedSub={sub}
        audioTrack={audio}
      />

      <Group gap="md" align="flex-end">
        {item.audioTracks.length > 1 && (
          <Select
            label="Аудио"
            w={260}
            allowDeselect={false}
            value={String(audio)}
            onChange={(v) => setAudio(Number(v))}
            data={item.audioTracks.map((t) => ({
              value: String(t.index),
              label: trackLabel(t, `Дорожка ${t.index + 1}`),
            }))}
          />
        )}
        {textSubs.length > 0 && (
          <Select
            label="Субтитры"
            w={260}
            allowDeselect={false}
            value={String(sub)}
            onChange={(v) => setSub(Number(v))}
            data={[
              { value: "-1", label: "Выкл" },
              ...textSubs.map((s) => ({
                value: String(s.index),
                label: trackLabel(s, `Дорожка ${s.index + 1}`),
              })),
            ]}
          />
        )}
      </Group>

      <Text size="xs" c="dimmed">
        URL потока: <Code>{url}</Code>
      </Text>
    </Stack>
  );
}
