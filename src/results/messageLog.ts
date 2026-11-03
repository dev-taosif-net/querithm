// The Messages log as data (§12 bottom panel): its lines, their pixel offsets for the windowed view, and the run's
// errors, kept per message list and extended as a batch arrives. The webview appends a run's messages in place
// (actions.ts appendMessages), so a batch of 500 costs O(500) here instead of a pass over the whole log (up to 20,100
// lines) on every query.messages notification (§14 #36frames).
import type { ServerMessage } from "../ipc/contract";
import type { ExecutionView } from "../store/executions";
import type { ExecutionData } from "./model";

/** One line of the log: a message, or the place a result set began. */
export type Entry = { kind: "msg"; m: ServerMessage; i: number; key: string } | { kind: "set"; index: number; key: string };

/** Where a result set began: how many messages had arrived then, and its result index. */
export type SetMark = readonly [at: number, index: number];

export const LINE_PX = 19;

export function header(m: ServerMessage): string | null {
  if (m.severity !== "error" || m.number == null) return null;
  return `Msg ${m.number}${m.class != null ? `, Level ${m.class}` : ""}${m.state != null ? `, State ${m.state}` : ""}${m.procedure ? `, Procedure ${m.procedure}` : ""}${m.line ? `, Line ${m.line}` : ""}${m.editorLine ? ` (editor line ${m.editorLine})` : ""}`;
}

/** The pixel height of a line of the log (the windowed view places lines by these). */
export const heightOf = (e: Entry) => (e.kind === "set" ? 22 : (e.m.message.split("\n").length + (header(e.m) ? 1 : 0)) * LINE_PX + 4);

/** The run's result sets that have a recorded start, in result-set order (the order the log lists sets that began together). */
export function setMarks(view: Pick<ExecutionView, "setAfter">, data: Pick<ExecutionData, "resultSets"> | undefined): SetMark[] {
  const out: SetMark[] = [];
  for (const rs of data?.resultSets ?? []) {
    const at = view.setAfter?.[rs.index];
    if (at != null) out.push([at, rs.index]);
  }
  return out;
}

/** The marks placed before the first `count` messages, as a comparable string. */
function marksBefore(marks: readonly SetMark[], count: number): string {
  let s = "";
  for (const [at, index] of marks) if (at < count) s += `${at}:${index},`;
  return s;
}

/**
 * The log of one message list, built once and then extended per batch. `sync` gives the same lines (and keys) as
 * listing every message from the start, with each result set's marker before the message that arrived after it began:
 * message keys `m<i>`, error keys `e<n>` counted over all messages (so a key stays the same when Errors filters the
 * rest out), set keys `s<index>`. A different list (another run, Clear), a different filter, a list that shrank or was
 * rewritten, or a moved marker among the lines already built start it over.
 */
export class MessageLogIndex {
  /** The lines in order. Extended in place: read it after `sync`, do not keep it across renders. */
  readonly entries: Entry[] = [];
  /** offsets[k] is the top of entries[k] in px; the last element is the log's height. */
  readonly offsets: number[] = [0];
  private source: readonly ServerMessage[] | undefined;
  private errorsOnly = false;
  private consumed = 0;
  private last: ServerMessage | undefined;
  private errors = 0;
  private body = 0;
  private bodyMarks = "";
  private readonly sets = new Map<number, Entry>();

  sync(messages: readonly ServerMessage[], marks: readonly SetMark[], errorsOnly: boolean): this {
    const placed = errorsOnly ? [] : marks;
    const n = messages.length;
    const same =
      messages === this.source &&
      errorsOnly === this.errorsOnly &&
      n >= this.consumed &&
      (this.consumed === 0 || messages[this.consumed - 1] === this.last) &&
      marksBefore(placed, this.consumed) === this.bodyMarks;
    if (!same) {
      this.source = messages;
      this.errorsOnly = errorsOnly;
      this.consumed = 0;
      this.last = undefined;
      this.errors = 0;
      this.body = 0;
    }
    // The markers after the last message are placed again after the new ones.
    this.entries.length = this.body;
    this.offsets.length = this.body + 1;
    const starts = new Map<number, number[]>();
    for (const [at, index] of placed) if (at >= this.consumed && at <= n) starts.set(at, [...(starts.get(at) ?? []), index]);
    for (let i = this.consumed; i < n; i++) {
      this.placeSets(starts.get(i));
      const m = messages[i];
      const key = m.severity === "error" ? `e${this.errors++}` : `m${i}`;
      if (!errorsOnly || m.severity === "error") this.push({ kind: "msg", m, i, key });
    }
    this.consumed = n;
    this.last = messages[n - 1];
    this.body = this.entries.length;
    this.bodyMarks = marksBefore(placed, n);
    this.placeSets(starts.get(n));
    return this;
  }

  private placeSets(indexes: number[] | undefined) {
    for (const index of indexes ?? []) {
      let e = this.sets.get(index);
      if (!e) this.sets.set(index, (e = { kind: "set", index, key: `s${index}` }));
      this.push(e);
    }
  }

  private push(e: Entry) {
    this.entries.push(e);
    this.offsets.push(this.offsets[this.offsets.length - 1] + heightOf(e));
  }
}

type ErrorScan = { n: number; last: ServerMessage | undefined; list: ServerMessage[]; at: number[] };
const scans = new WeakMap<readonly ServerMessage[], ErrorScan>();

/**
 * The errors in a message list and their positions, scanned once per list and extended as it grows. Shared: the
 * results are read-only and grow with the list.
 */
export function errorsIn(messages: readonly ServerMessage[]): { readonly list: readonly ServerMessage[]; readonly at: readonly number[] } {
  let s = scans.get(messages);
  if (!s || s.n > messages.length || (s.n > 0 && messages[s.n - 1] !== s.last)) scans.set(messages, (s = { n: 0, last: undefined, list: [], at: [] }));
  for (let i = s.n; i < messages.length; i++) {
    if (messages[i].severity !== "error") continue;
    s.list.push(messages[i]);
    s.at.push(i);
  }
  s.n = messages.length;
  s.last = messages[s.n - 1];
  return s;
}

let clockFormat: Intl.DateTimeFormat | undefined;
let clockOffset: number | undefined;

/**
 * `new Date(ts).toLocaleTimeString(undefined, { hour12: false })` with one formatter: that call builds a new ICU
 * formatter each time (about 40 µs, and its native memory is freed in GC callbacks). The options are the ones
 * toLocaleTimeString adds when none of hour / minute / second is given (ECMA-402 ToDateTimeOptions "time", "time"),
 * so the text is the same; the formatter is built again when the machine's UTC offset changes. A time outside the
 * Date range takes the old call ("Invalid Date"; the formatter would throw).
 */
export function timeOfDay(ts: number): string {
  if (!(Math.abs(ts) <= 8.64e15)) return new Date(ts).toLocaleTimeString(undefined, { hour12: false });
  const offset = new Date().getTimezoneOffset();
  if (!clockFormat || offset !== clockOffset) {
    clockFormat = new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "numeric", second: "numeric", hour12: false });
    clockOffset = offset;
  }
  return clockFormat.format(ts);
}
