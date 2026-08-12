import { useEffect, useState } from "react";
import { Link } from "react-router";
import { api } from "../api";
import type { LibraryTree, MediaItem } from "../api";
import { useServerUrl } from "../serverUrl";

// Узел дерева для отрисовки: группы, файлы внутри группы и записи о файлах,
// которых сейчас нет на диске (в /api/media их нет — только id из членства).
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

  if (error) return <p className="muted">Не удалось загрузить библиотеку: {error}</p>;
  if (!data) return <p className="muted">Загрузка…</p>;

  const { roots, ungrouped, missingCount, groupedCount } = build(data.tree, data.items);

  if (data.items.length === 0 && missingCount === 0)
    return <p className="muted">Медиафайлы не найдены. Положите видео в папку сервера и обновите.</p>;

  return (
    <>
      <div className="tree-summary muted">
        файлов: {data.items.length} · в группах: {groupedCount} · вне групп: {ungrouped.length}
        {missingCount > 0 && ` · нет на диске: ${missingCount}`}
      </div>

      <div className="tree">
        {roots.map((n) => (
          <NodeView key={n.id} node={n} />
        ))}

        {ungrouped.length > 0 &&
          (roots.length === 0 ? (
            ungrouped.map((f) => <FileRow key={f.id} item={f} />)
          ) : (
            <NodeView node={{ id: "~", name: "Вне групп", children: [], files: ungrouped, missing: [] }} />
          ))}
      </div>
    </>
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
// <details> хранит состояние сам — своего кода на раскрытие не нужно.
function NodeView({ node }: { node: TreeNode }) {
  return (
    <details className="tree-node">
      <summary>
        <span className="tree-name">{node.name}</span>
        <span className="tree-count">{countFiles(node)}</span>
      </summary>
      <div className="tree-children">
        {node.children.map((c) => (
          <NodeView key={c.id} node={c} />
        ))}
        {node.files.map((f) => (
          <FileRow key={f.id} item={f} />
        ))}
        {node.missing.map((id) => (
          <div key={id} className="tree-file missing" title={`Файла нет на диске (id ${id})`}>
            <span className="tree-name">нет на диске</span>
            <span className="badge unsupported">отсутствует</span>
          </div>
        ))}
      </div>
    </details>
  );
}

function FileRow({ item }: { item: MediaItem }) {
  const badge =
    item.streamType === "direct" ? (
      <span className="badge direct">прямой</span>
    ) : item.streamType === "hls" ? (
      <span className="badge transcode">транскод</span>
    ) : (
      <span className="badge unsupported">недоступно</span>
    );

  const inner = (
    <>
      <span className="tree-name">{item.title}</span>
      <span className="tree-file-name">{item.fileName}</span>
      {badge}
    </>
  );

  return item.playable ? (
    <Link to={`/watch/${item.id}`} className="tree-file">
      {inner}
    </Link>
  ) : (
    <div className="tree-file disabled" title={item.statusMessage ?? undefined}>
      {inner}
    </div>
  );
}
