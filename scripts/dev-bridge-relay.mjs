// The dev bridge's copy of the shell's data-plane relay (src-tauri/src/ipc.rs, §5): the same constants and the same
// rules, so the browser dev mode and the UI smoke see what the app sees — frames whose length is outside
// [FRAME_HEADER_LEN, MAX_FRAME_LEN] end the connection (a stream that lost its framing), at most 8 batches wait per run
// (backpressure on the socket), a full queue whose consumer has not polled for 30 s is dropped until its end (H3),
// results_next answers a dropped or ended run with the shell-made frame 0xFE ("gone"), and a discard after the end
// leaves no entry behind. R1-3: any entry nobody polled for 30 s goes (swept on push and poll), and `clear` starts a
// new generation whose frames alone are kept. R1-4: a kind byte outside 0x01–0x04 ends the connection like a bad length.

export const MAX_BUFFERED_BATCHES = 8;
export const KIND_SCHEMA = 0x01;
export const KIND_BATCH = 0x02;
export const KIND_END_OF_EXECUTION = 0x04;
/** Made by the shell, never sent by the sidecar: the run's frames were dropped (or it already ended); stop polling. */
export const KIND_GONE = 0xfe;
/** What the length field counts besides the payload: kind, execution id, result index. */
export const FRAME_HEADER_LEN = 1 + 16 + 2;
/** `FrameLimits.MaxLength` in the sidecar, `MAX_FRAME_LEN` in ipc.rs. */
export const MAX_FRAME_LEN = 256 * 1024 * 1024;
export const STALE_CONSUMER_MS = 30_000;
export const REMEMBERED_ENDS = 1024;
export const POLL_WAIT_MS = 20_000;
export const GONE = Buffer.from([KIND_GONE, 0, 0]);

const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/** ULID bytes → 26-char Crockford base32, matching the sidecar's executionId strings. */
export function ulid(bytes) {
  let v = 0n;
  for (const b of bytes) v = (v << 8n) | BigInt(b);
  let s = "";
  for (let i = 0; i < 26; i++) {
    s = ALPHABET[Number(v & 31n)] + s;
    v >>= 5n;
  }
  return s;
}

/** Null for a length the shell accepts, else why the stream lost its framing (the connection must close). */
export function framingError(len) {
  return len >= FRAME_HEADER_LEN && len <= MAX_FRAME_LEN
    ? null
    : `a frame of ${len} bytes is outside ${FRAME_HEADER_LEN}..=${MAX_FRAME_LEN}: the results stream lost its framing`;
}

/** Null for a kind the sidecar sends, else why the stream lost its framing (checked before the payload is waited for). */
export function kindError(kind) {
  return kind >= KIND_SCHEMA && kind <= KIND_END_OF_EXECUTION
    ? null
    : `a frame of kind 0x${kind.toString(16).toUpperCase().padStart(2, "0")} is not one the engine sends: the results stream lost its framing`;
}

/**
 * Per-execution queues. `push` returns false while the run's queue is full and its consumer is reading: the caller
 * keeps the frame, pauses the socket and offers it again when `onSpace` fires (a frame taken, a discard, or the consumer
 * found silent — then the queue is dropped and the frame accepted into nothing).
 */
export function createRelay({ staleAfterMs = STALE_CONSUMER_MS, now = Date.now, log = (m) => console.warn(m) } = {}) {
  const byId = new Map();
  const ended = [];
  const endedSet = new Set();
  const arrivals = new Set();
  const spaceListeners = new Set();
  let staleTimer = null;
  let generation = 0;
  let nextSweep = -Infinity;

  const notifyArrivals = () => {
    for (const w of [...arrivals]) w();
  };
  const notifySpace = () => {
    if (staleTimer) clearTimeout(staleTimer);
    staleTimer = null;
    for (const l of [...spaceListeners]) l();
  };
  const make = (id) => {
    let q = byId.get(id);
    if (!q) byId.set(id, (q = { frames: [], batches: 0, discarded: false, ended: false, lastPoll: now() }));
    return q;
  };
  const dropFrames = (q) => {
    q.discarded = true;
    q.frames = [];
    q.batches = 0;
  };
  const rememberEnd = (id) => {
    if (endedSet.has(id)) return;
    endedSet.add(id);
    ended.push(id);
    if (ended.length > REMEMBERED_ENDS) endedSet.delete(ended.shift());
  };
  /** Entries nobody polled for the bound go, remembered as ended (at most every quarter of the bound). */
  const sweep = () => {
    const t = now();
    if (t < nextSweep) return;
    nextSweep = t + staleAfterMs / 4;
    for (const [id, q] of [...byId]) {
      if (t - q.lastPoll < staleAfterMs) continue;
      byId.delete(id);
      if (!q.discarded) log(`[bridge] results of ${id} dropped: nothing asked for them for ${Math.floor((t - q.lastPoll) / 1000)} s`);
      rememberEnd(id);
    }
  };

  return {
    onSpace(listener) {
      spaceListeners.add(listener);
      return () => spaceListeners.delete(listener);
    },

    /** The current engine's generation: a socket reader passes the one it connected under. */
    generation: () => generation,

    push(id, kind, frame, gen = generation) {
      if (gen !== generation) return true; // a reader of an engine that went drains into nothing
      sweep();
      if (endedSet.has(id)) return true; // a run that already ended sends nothing more; never make it a new entry
      const q = make(id);
      if (q.discarded) {
        if (kind === KIND_END_OF_EXECUTION) {
          byId.delete(id);
          rememberEnd(id);
        }
        return true;
      }
      if (kind !== KIND_BATCH || q.batches < MAX_BUFFERED_BATCHES) {
        if (kind === KIND_BATCH) q.batches++;
        if (kind === KIND_END_OF_EXECUTION) q.ended = true;
        q.frames.push(frame);
        notifyArrivals();
        return true;
      }
      const silent = now() - q.lastPoll;
      if (silent >= staleAfterMs) {
        log(`[bridge] results of ${id} dropped: nothing asked for them for ${Math.floor(silent / 1000)} s`);
        dropFrames(q);
        notifyArrivals();
        return true;
      }
      // Check the consumer again once it could be stale (a poll or a discard comes first and wakes the reader).
      if (staleTimer) clearTimeout(staleTimer);
      staleTimer = setTimeout(notifySpace, staleAfterMs - silent);
      return false;
    },

    /** The next frame, waiting up to `waitMs`: empty = nothing yet; GONE = dropped or already ended. */
    next(id, waitMs = POLL_WAIT_MS) {
      return new Promise((resolve) => {
        let timer = null;
        const done = (frame) => {
          if (timer) clearTimeout(timer);
          arrivals.delete(attempt);
          resolve(frame);
        };
        const attempt = () => {
          sweep();
          const q = byId.get(id);
          if (q ? q.discarded : endedSet.has(id)) {
            done(GONE);
            return true;
          }
          if (!q) return false;
          q.lastPoll = now();
          const f = q.frames.shift();
          if (!f) return false;
          if (f[0] === KIND_BATCH) q.batches--;
          if (f[0] === KIND_END_OF_EXECUTION) {
            byId.delete(id);
            rememberEnd(id);
          }
          done(f);
          notifySpace();
          return true;
        };
        if (attempt()) return;
        arrivals.add(attempt);
        timer = setTimeout(() => done(Buffer.alloc(0)), waitMs);
      });
    },

    /** Drops buffered and later frames; a run whose end already arrived (buffered or consumed) leaves no entry. */
    discard(id) {
      if (!endedSet.has(id)) {
        const q = make(id);
        if (q.ended) {
          byId.delete(id);
          rememberEnd(id);
        } else dropFrames(q);
      }
      notifySpace();
      notifyArrivals();
    },

    clear() {
      generation++;
      nextSweep = -Infinity;
      byId.clear();
      ended.length = 0;
      endedSet.clear();
      notifyArrivals();
      notifySpace();
    },

    /** Tests: entries held (ended runs are only remembered by id). */
    entries: () => byId.size,
  };
}
