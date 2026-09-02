import { useEffect, useState } from "react";
import { Link } from "react-router";
import { Alert, Badge, Group, Loader, NavLink, Stack, Text } from "@mantine/core";
import { FilePlay, FileX, Folder } from "lucide-react";
import { api } from "../api";
import type { LibraryTree, MediaItem, StreamType } from "../api";
import { useServerUrl } from "../serverUrl";

// Узел дерева для отрисовки: группы, файлы внутри группы и записи о файлах,
// которых сейчас нет ни на одном доступном хосте (в /api/media их нет — только
// id из членства).
interface TreeNode {
  id: string;
  name: string;
  children: TreeNode[];
  files: MediaItem[];
  missing: string[];
}

export function Library() {
  const { serverUrl } = useServerUrl();
  const [data, setData] = useState<{ items: MediaItem[]; tree: LibraryTree } | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const ac = new AbortController();
    setData(null);
    setError(null);
    Promise.all([api.media(serverUrl, ac.signal), api.tree(serverUrl, ac.signal)])
      .then(([items, tree]) => setData({ items, tree }))
      .catch((e) => {
        if (!ac.signal.aborted) setError(String(e.message ?? e));
      });
    return () => ac.abort();
  }, [serverUrl]);

  if (error)
    return (
      <Alert color="red" title="Не удалось загрузить библиотеку">
        {error}
      </Alert>
    );
  if (!data)
    return (
      <Group justify="center" py="xl">
        <Loader />
      </Group>
    );

  const { roots, ungrouped, missingCount, groupedCount } = build(data.tree, data.items);

  if (data.items.length === 0 && missingCount === 0)
    return <Text c="dimmed">Медиафайлы не найдены. Положите видео в папку хоста и обновите.</Text>;

  return (
    <Stack gap="xs">
      <Text size="sm" c="dimmed">
        файлов: {data.items.length} · в группах: {groupedCount} · вне групп: {ungrouped.length}
        {missingCount > 0 && ` · нет на диске: ${missingCount}`}
      </Text>

      {roots.map((n) => (
        <NodeView key={n.id} node={n} />
      ))}

      {ungrouped.length > 0 &&
        (roots.length === 0 ? (
          ungrouped.map((f) => <FileRow key={f.id} item={f} />)
        ) : (
          <NodeView node={{ id: "~", name: "Вне групп", children: [], files: ungrouped, missing: [] }} />
        ))}
    </Stack>
  );
}

// Собирает дерево из плоских списков сервера. Файлы, не попавшие ни в одну
// группу, идут отдельным списком — по ним и видно, что правила не разложили.
function build(tree: LibraryTree, items: MediaItem[]) {
  const byId = new Map(items.map((i) => [i.id, i]));
  const nodes = new Map<string, TreeNode>(
    tree.nodes.map((n) => [n.id, { id: n.id, name: n.name, children: [], files: [], missing: [] }]),
  );

  const roots: TreeNode[] = [];
  for (const n of tree.nodes) {
    const node = nodes.get(n.id)!;
    const parent = n.parentId ? nodes.get(n.parentId) : undefined;
    if (parent) parent.children.push(node);
    else roots.push(node);
  }

  const grouped = new Set<string>();
  let missingCount = 0;
  for (const it of tree.items) {
    const node = nodes.get(it.nodeId);
    if (!node) continue;
    const media = byId.get(it.mediaId);
    if (media) {
      node.files.push(media);
      grouped.add(media.id);
    } else {
      node.missing.push(it.mediaId);
      missingCount++;
    }
  }

  const byName = (a: { name: string }, b: { name: string }) => a.name.localeCompare(b.name, "ru");
  const byTitle = (a: MediaItem, b: MediaItem) => a.title.localeCompare(b.title, "ru");
  for (const node of nodes.values()) {
    node.children.sort(byName);
    node.files.sort(byTitle);
  }
  roots.sort(byName);

  const ungrouped = items.filter((i) => !grouped.has(i.id)).sort(byTitle);
  return { roots, ungrouped, missingCount, groupedCount: grouped.size };
}

// Число файлов во всём поддереве — чтобы свёрнутая группа сразу показывала объём.
function countFiles(node: TreeNode): number {
  return (
    node.files.length + node.missing.length + node.children.reduce((sum, c) => sum + countFiles(c), 0)
  );
}

// Группы свёрнуты: у домашней библиотеки их десятки, и обзор важнее содержимого.
// NavLink хранит состояние раскрытия сам — своего кода на это не нужно.
// Счётчик файлов идёт меткой сразу за названием, а правая секция остаётся за
// шевроном NavLink: он там по умолчанию и разворачивается при раскрытии.
function NodeView({ node }: { node: TreeNode }) {
  return (
    <NavLink
      leftSection={<Folder size={16} />}
      label={
        <Group gap="xs">
          {node.name}
          <Badge variant="default" size="sm">
            {countFiles(node)}
          </Badge>
        </Group>
      }
      childrenOffset={28}
    >
      {node.children.map((c) => (
        <NodeView key={c.id} node={c} />
      ))}
      {node.files.map((f) => (
        <FileRow key={f.id} item={f} />
      ))}
      {node.missing.map((id) => (
        <NavLink
          key={id}
          disabled
          leftSection={<FileX size={16} />}
          label="нет на диске"
          description={`id ${id}`}
          rightSection={<Badge color="red" variant="light">отсутствует</Badge>}
        />
      ))}
    </NavLink>
  );
}

// Бейдж режима воспроизведения: pending — файл найден сканом, но хост ещё не
// прочитал метаданные; это штатное промежуточное состояние, а не ошибка.
const BADGES: Record<StreamType, { color: string; text: string }> = {
  direct: { color: "green", text: "прямой" },
  hls: { color: "blue", text: "транскод" },
  pending: { color: "yellow", text: "разбирается" },
  unsupported: { color: "red", text: "недоступно" },
};

function FileRow({ item }: { item: MediaItem }) {
  const badge = BADGES[item.streamType];
  const hint =
    item.statusMessage ??
    (item.streamType === "pending" ? "Хост читает метаданные файла" : undefined);

  // Тип component у NavLink полиморфный, поэтому играбельная и неиграбельная
  // строки — две разные ветки, а не один элемент с вычисляемым component.
  const props = {
    leftSection: <FilePlay size={16} />,
    label: item.title,
    description: hint ? `${item.fileName} — ${hint}` : item.fileName,
    rightSection: (
      <Badge color={badge.color} variant="light">
        {badge.text}
      </Badge>
    ),
  };

  return item.playable ? (
    <NavLink component={Link} to={`/watch/${item.id}`} {...props} />
  ) : (
    <NavLink disabled {...props} />
  );
}
