import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { api } from "../api";
import type { DebugInfo, SessionInfo } from "../api";
import { useServerUrl } from "../serverUrl";

interface Prev {
  cpu: number;
  t: number;
}

// Живая дебаг-панель сессий транскодирования: количество, %CPU (по дельте
// процессорного времени), память, прогресс каждой сессии. Опрос раз в секунду.
export function DebugPanel() {
  const { serverUrl } = useServerUrl();
  const [info, setInfo] = useState<DebugInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [cpuByPid, setCpuByPid] = useState<Record<number, number>>({});
  const [history, setHistory] = useState<number[]>([]);
  const prev = useRef<Map<number, Prev>>(new Map());

  useEffect(() => {
    let stop = false;
    const tick = async () => {
      try {
        const d = await api.debug(serverUrl);
        if (stop) return;
        const now = performance.now();
        const pct: Record<number, number> = {};
        let totalPct = 0;
        for (const s of d.sessions) {
          const p = prev.current.get(s.pid);
          if (p && now > p.t) {
            const v = Math.max(0, ((s.cpuSeconds - p.cpu) / ((now - p.t) / 1000)) * 100);
            pct[s.pid] = v;
            totalPct += v;
          }
          prev.current.set(s.pid, { cpu: s.cpuSeconds, t: now });
        }
        // Убираем исчезнувшие процессы.
        const alive = new Set(d.sessions.map((s) => s.pid));
        for (const pid of [...prev.current.keys()]) if (!alive.has(pid)) prev.current.delete(pid);

        setInfo(d);
        setCpuByPid(pct);
        setHistory((h) => [...h.slice(-59), totalPct]);
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
  }, [serverUrl]);

  const sessions = info?.sessions ?? [];
  const cores = info?.cpuCount ?? 1;
  const totalCpu = Object.values(cpuByPid).reduce((a, b) => a + b, 0);
  const totalMem = sessions.reduce((a, s) => a + s.memoryBytes, 0);

  return (
    <>
      <Link to="/" className="back">← Библиотека</Link>
      <h2 className="title">Дебаг сессий</h2>
      {error && <p className="muted">Ошибка: {error}</p>}

      <div className="stat-row">
        <Stat label="Сессий" value={String(sessions.length)} />
        <Stat label="CPU (ffmpeg)" value={`${totalCpu.toFixed(0)}%`} hint={`из ${cores * 100}% · ${(totalCpu / 100).toFixed(1)} из ${cores} ядер`} />
        <Stat label="Память" value={fmtBytes(totalMem)} />
      </div>

      <Sparkline data={history} max={cores * 100} />

      <div className="grid" style={{ marginTop: 16 }}>
        {sessions.length === 0 && <p className="muted">Активных сессий нет.</p>}
        {sessions.map((s) => (
          <SessionCard key={`${s.mediaId}-${s.startIndex}-${s.stream}`} s={s} cpu={cpuByPid[s.pid]} />
        ))}
      </div>
    </>
  );
}

function SessionCard({ s, cpu }: { s: SessionInfo; cpu?: number }) {
  const progress = s.total > 0 ? Math.min(100, (s.produced / s.total) * 100) : 0;
  return (
    <div className="card dbg">
      <div className="dbg-head">
        <span className="name">
          сегменты [{s.startIndex}…{s.endIndex}) · {s.stream === "v" ? "видео" : `аудио ${s.stream.slice(1)}`}
        </span>
        <span className={`badge ${s.alive ? "transcode" : "unsupported"}`}>
          {s.alive ? "работает" : "завершена"}
        </span>
      </div>
      <Meter label="произведено" value={progress} text={`${s.produced}/${s.total}`} color="#4ade80" />
      <Meter label="CPU" value={cpu ?? 0} text={cpu != null ? `${cpu.toFixed(0)}%` : "…"} color="#6ea8fe" cap={100} />
      <div className="dbg-meta">
        <span>память {fmtBytes(s.memoryBytes)}</span>
        <span>pid {s.pid}</span>
        <span>простой {s.idleSeconds}s</span>
      </div>
    </div>
  );
}

function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="stat">
      <div className="stat-value">{value}</div>
      <div className="stat-label">{label}</div>
      {hint && <div className="stat-hint">{hint}</div>}
    </div>
  );
}

function Meter({ label, value, text, color, cap }: { label: string; value: number; text: string; color: string; cap?: number }) {
  const pct = Math.min(100, cap ? (value / cap) * 100 : value);
  return (
    <div className="meter">
      <div className="meter-top">
        <span>{label}</span>
        <span>{text}</span>
      </div>
      <div className="meter-track">
        <div className="meter-fill" style={{ width: `${pct}%`, background: color }} />
      </div>
    </div>
  );
}

function Sparkline({ data, max }: { data: number[]; max: number }) {
  const w = 600, h = 48;
  if (data.length < 2) return <div className="spark" style={{ height: h }} />;
  const scale = max > 0 ? max : Math.max(...data, 1);
  const pts = data
    .map((v, i) => `${(i / (data.length - 1)) * w},${h - Math.min(1, v / scale) * h}`)
    .join(" ");
  return (
    <div className="spark">
      <svg viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" width="100%" height={h}>
        <polyline points={pts} fill="none" stroke="#6ea8fe" strokeWidth="1.5" />
      </svg>
      <span className="spark-label">CPU ffmpeg, посл. {data.length}с</span>
    </div>
  );
}

function fmtBytes(b: number): string {
  if (b <= 0) return "0";
  const mb = b / (1024 * 1024);
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} ГБ` : `${mb.toFixed(0)} МБ`;
}
