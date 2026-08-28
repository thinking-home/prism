import { useEffect, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router";
import {
  Alert,
  Anchor,
  Badge,
  Card,
  Code,
  Group,
  Paper,
  Progress,
  SimpleGrid,
  Stack,
  Text,
  Title,
} from "@mantine/core";
import { api } from "../api";
import type { DebugInfo, SessionInfo } from "../api";

interface Prev {
  cpu: number;
  t: number;
}

// Живая дебаг-панель сессий транскодирования: количество, %CPU (по дельте
// процессорного времени), память, прогресс каждой сессии. Опрос раз в секунду.
// Сессии принадлежат конкретному хосту, а не библиотеке, поэтому его база
// приходит параметром ссылки со страницы фильма — там хост известен.
export function DebugPanel() {
  const [params] = useSearchParams();
  const host = params.get("host") ?? "";
  const [info, setInfo] = useState<DebugInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [cpuByPid, setCpuByPid] = useState<Record<number, number>>({});
  const prev = useRef<Map<number, Prev>>(new Map());

  useEffect(() => {
    if (!host) return;
    let stop = false;
    const tick = async () => {
      try {
        const d = await api.debug(host);
        if (stop) return;
        const now = performance.now();
        const pct: Record<number, number> = {};
        for (const s of d.sessions) {
          const p = prev.current.get(s.pid);
          if (p && now > p.t) {
            pct[s.pid] = Math.max(0, ((s.cpuSeconds - p.cpu) / ((now - p.t) / 1000)) * 100);
          }
          prev.current.set(s.pid, { cpu: s.cpuSeconds, t: now });
        }
        // Убираем исчезнувшие процессы.
        const alive = new Set(d.sessions.map((s) => s.pid));
        for (const pid of [...prev.current.keys()]) if (!alive.has(pid)) prev.current.delete(pid);

        setInfo(d);
        setCpuByPid(pct);
        setError(null);
      } catch (e: any) {
        if (!stop) setError(String(e?.message ?? e));
      }
    };
    tick();
    const id = setInterval(tick, 1000);
    return () => {
      stop = true;
      clearInterval(id);
    };
  }, [host]);

  const back = (
    <Anchor component={Link} to="/" size="sm">
      ← Библиотека
    </Anchor>
  );

  if (!host)
    return (
      <Stack gap="sm">
        {back}
        <Title order={2}>Дебаг сессий</Title>
        <Text c="dimmed">Сессии принадлежат хосту — откройте панель ссылкой со страницы фильма.</Text>
      </Stack>
    );

  const sessions = info?.sessions ?? [];
  const cores = info?.cpuCount ?? 1;
  const totalCpu = Object.values(cpuByPid).reduce((a, b) => a + b, 0);
  const totalMem = sessions.reduce((a, s) => a + s.memoryBytes, 0);

  return (
    <Stack gap="sm">
      {back}
      <Title order={2}>Дебаг сессий</Title>
      <Text size="sm" c="dimmed">
        хост: <Code>{host}</Code>
      </Text>
      {error && (
        <Alert color="red" title="Ошибка">
          {error}
        </Alert>
      )}

      <SimpleGrid cols={{ base: 1, sm: 3 }}>
        <Stat label="Сессий" value={String(sessions.length)} />
        <Stat
          label="CPU (ffmpeg)"
          value={`${totalCpu.toFixed(0)}%`}
          hint={`из ${cores * 100}% · ${(totalCpu / 100).toFixed(1)} из ${cores} ядер`}
        />
        <Stat label="Память" value={fmtBytes(totalMem)} />
      </SimpleGrid>

      {sessions.length === 0 ? (
        <Text c="dimmed">Активных сессий нет.</Text>
      ) : (
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          {sessions.map((s) => (
            <SessionCard key={`${s.mediaId}-${s.startIndex}-${s.stream}`} s={s} cpu={cpuByPid[s.pid]} />
          ))}
        </SimpleGrid>
      )}
    </Stack>
  );
}

function SessionCard({ s, cpu }: { s: SessionInfo; cpu?: number }) {
  const progress = s.total > 0 ? Math.min(100, (s.produced / s.total) * 100) : 0;
  return (
    <Card withBorder padding="md">
      <Group justify="space-between" mb="xs">
        <Text size="sm" fw={500}>
          сегменты [{s.startIndex}…{s.endIndex}) · {s.stream === "v" ? "видео" : `аудио ${s.stream.slice(1)}`}
        </Text>
        <Badge color={s.alive ? "blue" : "red"} variant="light">
          {s.alive ? "работает" : "завершена"}
        </Badge>
      </Group>

      <Meter label="произведено" value={progress} text={`${s.produced}/${s.total}`} color="green" />
      <Meter
        label="CPU"
        value={Math.min(100, cpu ?? 0)}
        text={cpu != null ? `${cpu.toFixed(0)}%` : "…"}
        color="blue"
      />

      <Group gap="md" mt="xs">
        <Text size="xs" c="dimmed">
          память {fmtBytes(s.memoryBytes)}
        </Text>
        <Text size="xs" c="dimmed">
          pid {s.pid}
        </Text>
        <Text size="xs" c="dimmed">
          простой {s.idleSeconds}s
        </Text>
      </Group>
    </Card>
  );
}

function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <Paper withBorder p="md">
      <Text size="xl" fw={600}>
        {value}
      </Text>
      <Text size="sm" c="dimmed">
        {label}
      </Text>
      {hint && (
        <Text size="xs" c="dimmed">
          {hint}
        </Text>
      )}
    </Paper>
  );
}

function Meter({ label, value, text, color }: { label: string; value: number; text: string; color: string }) {
  return (
    <Stack gap={2} mt="xs">
      <Group justify="space-between">
        <Text size="xs" c="dimmed">
          {label}
        </Text>
        <Text size="xs" c="dimmed">
          {text}
        </Text>
      </Group>
      <Progress value={value} color={color} />
    </Stack>
  );
}

function fmtBytes(b: number): string {
  if (b <= 0) return "0";
  const mb = b / (1024 * 1024);
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} ГБ` : `${mb.toFixed(0)} МБ`;
}
