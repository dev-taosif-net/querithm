// ER diagram export: a pure SVG drawn from the layout (cards as rect/text), never HTML serialised
// through <foreignObject>, which WebKitGTK cannot reliably draw onto a canvas. PNG = that SVG rasterised. Offline.
import { HEADER_H, ROW_H, type ErdEdge, type ErdGraph, type Positioned } from "./erdLayout";

export interface ErdColors {
  bg: string;
  card: string;
  header: string;
  line: string;
  fg: string;
  muted: string;
  pk: string;
  fk: string;
  edge: string;
}

const esc = (s: string) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

/** Y of a column row's centre inside a card, or the header's when the column is not shown. */
function anchorY(columns: { name: string }[], column: string, top: number): number {
  const i = columns.findIndex((c) => c.name.toLowerCase() === column.toLowerCase());
  return i < 0 ? top + HEADER_H / 2 : top + HEADER_H + i * ROW_H + ROW_H / 2;
}

/** Points of an FK edge: from the FK column of the referencing card to the key column of the referenced card. */
export function edgePath(e: ErdEdge, graph: ErdGraph, pos: Map<number, Positioned>): string {
  const a = graph.nodes.find((n) => n.id === e.from)!;
  const b = graph.nodes.find((n) => n.id === e.to)!;
  const pa = pos.get(a.id)!;
  const pb = pos.get(b.id)!;
  const ya = anchorY(a.columns, e.pairs[0]?.parent ?? "", pa.y);
  const yb = anchorY(b.columns, e.pairs[0]?.referenced ?? "", pb.y);
  if (e.self) {
    const x = pa.x + a.width;
    return `M ${x} ${ya} C ${x + 40} ${ya}, ${x + 40} ${yb}, ${x} ${yb}`;
  }
  const leftToRight = pa.x + a.width / 2 <= pb.x + b.width / 2;
  const xa = leftToRight ? pa.x + a.width : pa.x;
  const xb = leftToRight ? pb.x : pb.x + b.width;
  const dx = Math.max(30, Math.abs(xb - xa) / 2) * (leftToRight ? 1 : -1);
  return `M ${xa} ${ya} C ${xa + dx} ${ya}, ${xb - dx} ${yb}, ${xb} ${yb}`;
}

export function buildSvg(graph: ErdGraph, positions: Positioned[], colors: ErdColors): string {
  const pos = new Map(positions.map((p) => [p.id, p]));
  let maxX = 0;
  let maxY = 0;
  for (const n of graph.nodes) {
    const p = pos.get(n.id);
    if (!p) continue;
    maxX = Math.max(maxX, p.x + n.width + 60);
    maxY = Math.max(maxY, p.y + n.height + 20);
  }
  const out: string[] = [];
  out.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${Math.ceil(maxX)}" height="${Math.ceil(maxY)}" viewBox="0 0 ${Math.ceil(maxX)} ${Math.ceil(maxY)}" font-family="JetBrains Mono, Menlo, monospace" font-size="11">`);
  out.push(`<rect width="100%" height="100%" fill="${colors.bg}"/>`);
  for (const e of graph.edges) {
    out.push(`<path d="${edgePath(e, graph, pos)}" fill="none" stroke="${colors.edge}" stroke-width="1.4"${e.optional ? ' stroke-dasharray="5 3"' : ""}><title>${esc(e.name)}</title></path>`);
  }
  for (const n of graph.nodes) {
    const p = pos.get(n.id);
    if (!p) continue;
    out.push(`<g transform="translate(${p.x.toFixed(1)} ${p.y.toFixed(1)})">`);
    out.push(`<rect width="${n.width}" height="${n.height}" rx="6" fill="${colors.card}" stroke="${colors.line}"/>`);
    out.push(`<path d="M0 6 a6 6 0 0 1 6 -6 h${n.width - 12} a6 6 0 0 1 6 6 v${HEADER_H - 6} h-${n.width} z" fill="${colors.header}"/>`);
    out.push(`<text x="10" y="${HEADER_H / 2 + 4}" fill="${colors.fg}" font-weight="700">${esc(`${n.schema}.${n.name}`)}</text>`);
    n.columns.forEach((c, i) => {
      const y = HEADER_H + i * ROW_H + ROW_H / 2 + 4;
      const mark = c.pk ? "PK" : c.fk ? "FK" : "";
      if (mark) out.push(`<text x="10" y="${y}" fill="${c.pk ? colors.pk : colors.fk}" font-size="9" font-weight="700">${mark}</text>`);
      out.push(`<text x="32" y="${y}" fill="${colors.fg}"${c.notNull ? ' font-weight="600"' : ""}>${esc(c.name)}</text>`);
      out.push(`<text x="${n.width - 10}" y="${y}" fill="${colors.muted}" text-anchor="end">${esc(c.type)}</text>`);
    });
    out.push("</g>");
  }
  out.push("</svg>");
  return out.join("\n");
}

/** Rasterises the SVG through an <img> + canvas (the SVG holds no foreign content, so the canvas stays untainted). */
export async function svgToPng(svg: string, scale = 2): Promise<Uint8Array> {
  const url = URL.createObjectURL(new Blob([svg], { type: "image/svg+xml" }));
  try {
    const img = new Image();
    await new Promise<void>((resolve, reject) => {
      img.onload = () => resolve();
      img.onerror = () => reject(new Error("The diagram could not be rendered as an image."));
      img.src = url;
    });
    const canvas = document.createElement("canvas");
    canvas.width = Math.ceil(img.width * scale);
    canvas.height = Math.ceil(img.height * scale);
    const ctx = canvas.getContext("2d")!;
    ctx.scale(scale, scale);
    ctx.drawImage(img, 0, 0);
    const blob = await new Promise<Blob | null>((r) => canvas.toBlob(r, "image/png"));
    if (!blob) throw new Error("The diagram is too large to export as PNG; export it as SVG.");
    return new Uint8Array(await blob.arrayBuffer());
  } finally {
    URL.revokeObjectURL(url);
  }
}
