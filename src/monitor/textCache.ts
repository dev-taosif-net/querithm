// Query texts of one activity monitor (plan "Payload"): snapshots carry each text once; the webview keeps an LRU of
// 5,000 texts or 8 MB and asks monitor.texts for any it lacks. Ids the sidecar reports missing are never asked again.

export const MAX_TEXTS = 5_000;
export const MAX_TEXT_BYTES = 8 * 1024 * 1024;
export const MAX_IDS_PER_CALL = 500;

const utf8Bytes = (s: string) => {
  let n = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    n += c < 0x80 ? 1 : c < 0x800 ? 2 : c >= 0xd800 && c <= 0xdbff ? (i++, 4) : 3;
  }
  return n;
};

export type FetchTexts = (ids: string[]) => Promise<{ texts: Record<string, string>; missing: string[] }>;

export class TextCache {
  private map = new Map<string, { text: string; bytes: number }>();
  private missingIds = new Set<string>();
  private inFlight = new Set<string>();
  private bytes = 0;
  /** Calls made to fetch texts (tests assert "no repeated request"). */
  fetches = 0;

  constructor(
    private readonly fetch: FetchTexts,
    private readonly onChange: () => void = () => {},
    readonly maxTexts = MAX_TEXTS,
    readonly maxBytes = MAX_TEXT_BYTES,
  ) {}

  get size() {
    return this.map.size;
  }

  get byteSize() {
    return this.bytes;
  }

  get(id: string | null | undefined): string | undefined {
    if (!id) return undefined;
    const e = this.map.get(id);
    if (!e) return undefined;
    // LRU: touch on read.
    this.map.delete(id);
    this.map.set(id, e);
    return e.text;
  }

  has(id: string) {
    return this.map.has(id);
  }

  /** The sidecar no longer has this text (ended session evicted from its store). */
  isMissing(id: string | null | undefined) {
    return !!id && this.missingIds.has(id);
  }

  put(id: string, text: string) {
    const old = this.map.get(id);
    if (old) {
      this.bytes -= old.bytes;
      this.map.delete(id);
    }
    const bytes = utf8Bytes(text);
    this.map.set(id, { text, bytes });
    this.bytes += bytes;
    this.missingIds.delete(id);
    while ((this.map.size > this.maxTexts || this.bytes > this.maxBytes) && this.map.size > 1) {
      const oldest = this.map.keys().next().value as string;
      this.bytes -= this.map.get(oldest)!.bytes;
      this.map.delete(oldest);
    }
  }

  putAll(texts: Record<string, string> | null | undefined) {
    if (!texts) return;
    for (const [id, t] of Object.entries(texts)) this.put(id, t);
  }

  /** Fetches the ids that are neither cached, in flight nor known missing, in batches of ≤ 500. */
  async ensure(ids: Iterable<string | null | undefined>): Promise<void> {
    const want: string[] = [];
    for (const id of ids) if (id && !this.map.has(id) && !this.missingIds.has(id) && !this.inFlight.has(id) && !want.includes(id)) want.push(id);
    if (want.length === 0) return;
    for (const id of want) this.inFlight.add(id);
    try {
      for (let i = 0; i < want.length; i += MAX_IDS_PER_CALL) {
        const batch = want.slice(i, i + MAX_IDS_PER_CALL);
        this.fetches++;
        const r = await this.fetch(batch);
        this.putAll(r.texts);
        for (const id of r.missing) this.missingIds.add(id);
      }
    } catch {
      // the monitor may have stopped; a later snapshot asks again
    } finally {
      for (const id of want) this.inFlight.delete(id);
    }
    this.onChange();
  }

  clear() {
    this.map.clear();
    this.missingIds.clear();
    this.bytes = 0;
  }
}
