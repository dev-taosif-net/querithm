// Staged grid edits. Nothing reaches the server on blur: edits, inserted rows and deletions are held
// here per result set until Apply. Old values are read from the Arrow data, never copied. Instances live outside React;
// components subscribe to `version`.
import type { EditColumn, Engine, GridApplyResult, GridPatchParams, GridValue, ResultEditInfo } from "../ipc/contract";
import { DEFAULT_ENGINE } from "../engines";
import { validateCellInput } from "./cellInput";

/** What the grid reads from the result set (ArrowResultSet implements it). */
export interface StagingSource {
  rowCount: number;
  columns: { ordinal: number; name: string; typeName: string }[];
  textPhysical(row: number, col: number): string | null;
  isTruncatedPhysical(row: number, col: number): boolean;
}

/** A staged value: text (the grid's invariant form), SQL NULL (value null), or the server default (inserts only). */
export interface Cell {
  value: string | null;
  isDefault?: boolean;
}

/** A grid row: an existing row (Arrow rows, then rows inserted by an earlier apply) or a pending inserted row. */
export type RowRef = { kind: "existing"; row: number } | { kind: "inserted"; index: number };

interface InsertedRow {
  id: number;
  values: Map<number, Cell>;
}

interface CommittedRow {
  values: Map<number, string | null>;
  /** Ordinals whose value the server generated and the grid does not know. */
  unknown: Set<number>;
}

interface StateSnapshot {
  edits: [number, [number, Cell][]][];
  inserted: { id: number; values: [number, Cell][] }[];
  deleted: number[];
}

/** What one patch carried (W2): the cells, rows and deletions as they were when it was built. */
interface Sent {
  updates: Map<number, Map<number, Cell>>;
  inserts: { id: number; values: Map<number, Cell> }[];
  deletes: number[];
}

export interface StagedCounts {
  updates: number;
  inserts: number;
  deletes: number;
}

const MAX_UNDO = 200;
/** Rows one Duplicate copies at most. */
export const MAX_DUPLICATE = 1000;
export const CHANGED_ON_SERVER = "This row changed on the server after the query ran (or it was deleted). Re-run the query to edit it.";

export class GridStaging {
  edits = new Map<number, Map<number, Cell>>();
  inserted: InsertedRow[] = [];
  deleted = new Set<number>();
  /** Row-version token per existing row ("" when the table has none). */
  tokens = new Map<number, string>();
  /** Rows the server says changed since the query; editing them is refused. */
  conflicted = new Map<number, string>();
  /** Values written by earlier applies, shown instead of the Arrow values. */
  applied = new Map<number, Map<number, string | null>>();
  appliedDeleted = new Set<number>();
  committed: CommittedRow[] = [];
  /** Per-cell validation errors (key "row:ordinal" / "i<id>:ordinal"). */
  errors = new Map<string, string>();
  /** Why Apply is refused: a commit of these changes was cut short, so they may be written already (re-run the query). */
  outcomeUnknown?: string;
  version = 0;
  private undoStack: StateSnapshot[] = [];
  private redoStack: StateSnapshot[] = [];
  private nextId = 1;
  private listeners = new Set<() => void>();
  private byOrdinal = new Map<number, EditColumn>();
  private colIndex = new Map<number, number>();
  /** Per patch built, what it carried: commit applies exactly that, whatever was staged while it was in flight (W2). */
  private sent = new WeakMap<GridPatchParams, Sent>();

  constructor(
    readonly edit: ResultEditInfo,
    readonly src: StagingSource,
    readonly engine: Engine = DEFAULT_ENGINE,
  ) {
    for (const c of edit.columns) this.byOrdinal.set(c.ordinal, c);
    src.columns.forEach((c, i) => this.colIndex.set(c.ordinal, i));
  }

  subscribe = (cb: () => void) => {
    this.listeners.add(cb);
    return () => this.listeners.delete(cb);
  };

  getVersion = () => this.version;

  /** An Apply is in flight (started by the button, Ctrl+S or a prompt): the edit row locks its buttons. */
  busy = false;

  setBusy(b: boolean) {
    if (this.busy === b) return;
    this.busy = b;
    this.changed();
  }

  private changed() {
    this.version++;
    this.listeners.forEach((l) => l());
  }

  /** Existing rows: the Arrow rows plus rows inserted by earlier applies. */
  get baseRows(): number {
    return this.src.rowCount + this.committed.length;
  }

  column(ordinal: number): EditColumn | undefined {
    return this.byOrdinal.get(ordinal);
  }

  get canInsert(): boolean {
    return this.edit.editable && !this.edit.insertBlockedReason;
  }

  // ------------------------------------------------------------------------------------------ reading

  /** The value the row has on the server as far as the grid knows (Arrow, or an earlier apply). */
  original(row: number, ordinal: number): string | null {
    const ov = this.applied.get(row);
    if (ov?.has(ordinal)) return ov.get(ordinal)!;
    if (row >= this.src.rowCount) return this.committed[row - this.src.rowCount]?.values.get(ordinal) ?? null;
    const ci = this.colIndex.get(ordinal);
    return ci == null ? null : this.src.textPhysical(row, ci);
  }

  /** The staged value if there is one, else the original. */
  cell(ref: RowRef, ordinal: number): Cell & { staged: boolean; unknown?: boolean } {
    if (ref.kind === "inserted") {
      const r = this.insertedRow(ref.index);
      const c = r?.values.get(ordinal);
      if (c) return { ...c, staged: true };
      const col = this.column(ordinal);
      return col?.hasDefault || col?.generated ? { value: null, isDefault: true, staged: true } : { value: null, staged: true };
    }
    const staged = this.edits.get(ref.row)?.get(ordinal);
    if (staged) return { ...staged, staged: true };
    const committed = ref.row >= this.src.rowCount ? this.committed[ref.row - this.src.rowCount] : undefined;
    return { value: this.original(ref.row, ordinal), staged: false, unknown: committed?.unknown.has(ordinal) };
  }

  private insertedRow(index: number): InsertedRow | undefined {
    return this.inserted[index];
  }

  isDeleted(row: number): boolean {
    return this.deleted.has(row);
  }

  /** The cell's validation error; a new row's required column left empty counts too, so the cell itself shows it. */
  errorFor(ref: RowRef, ordinal: number): string | undefined {
    const err = this.errors.get(errKey(ref, ordinal));
    if (err || ref.kind !== "inserted") return err;
    const col = this.column(ordinal);
    return col && this.missing(this.inserted[ref.index], col) ? `${col.column} needs a value` : undefined;
  }

  /** A required column of a new row with no value: NOT NULL, no default, not generated or locked. */
  private missing(r: InsertedRow | undefined, c: EditColumn): boolean {
    if (!r || !c.column || c.locked || c.generated || c.nullable || c.hasDefault) return false;
    const v = r.values.get(c.ordinal);
    return !v || !!v.isDefault || v.value == null;
  }

  /** Why this cell cannot be edited, or null. `fullValue`: the edit carries the whole value (cell viewer), so a shortened display is no obstacle. */
  lockReason(ref: RowRef, ordinal: number, fullValue = false): string | null {
    if (!this.edit.editable) return this.edit.reason ?? "Read-only";
    const col = this.column(ordinal);
    if (!col) return "Not part of the edited table";
    if (ref.kind === "inserted") {
      if (col.generated) return "Generated by the server on insert";
      if (col.locked) return col.lockReason ?? "Read-only column";
      return null;
    }
    if (this.appliedDeleted.has(ref.row)) return "This row was deleted";
    if (this.deleted.has(ref.row)) return "The row is marked for deletion";
    if (this.conflicted.has(ref.row)) return this.conflicted.get(ref.row)!;
    if (col.locked) return col.lockReason ?? "Read-only column";
    if (this.keyTruncated(ref.row)) return "The row's key is shortened in the grid, so the row cannot be matched";
    if (!fullValue && ref.row < this.src.rowCount && this.isTruncated(ref.row, ordinal) && !this.edits.get(ref.row)?.has(ordinal))
      return "The value is shortened in the grid; open it in the cell viewer to edit the full value";
    return null;
  }

  private isTruncated(row: number, ordinal: number): boolean {
    const ci = this.colIndex.get(ordinal);
    return ci != null && row < this.src.rowCount && this.src.isTruncatedPhysical(row, ci);
  }

  private keyTruncated(row: number): boolean {
    return this.edit.keyOrdinals.some((o) => this.isTruncated(row, o));
  }

  // ------------------------------------------------------------------------------------------ editing

  private snapshot(): StateSnapshot {
    return {
      edits: [...this.edits].map(([r, m]) => [r, [...m].map(([o, c]) => [o, { ...c }])]),
      inserted: this.inserted.map((r) => ({ id: r.id, values: [...r.values].map(([o, c]) => [o, { ...c }]) })),
      deleted: [...this.deleted],
    };
  }

  private restore(s: StateSnapshot) {
    this.edits = new Map(s.edits.map(([r, cells]) => [r, new Map(cells)]));
    this.inserted = s.inserted.map((r) => ({ id: r.id, values: new Map(r.values) }));
    this.deleted = new Set(s.deleted);
    this.revalidate();
  }

  /** Set while `batch` runs: the batch recorded its one undo step already. */
  private batching = false;

  /** Runs several changes (a paste that also adds rows) as one undo step with one notification. */
  batch<T>(f: () => T): T {
    if (this.batching) return f();
    const before = this.snapshot();
    const undoLen = this.undoStack.length;
    this.batching = true;
    let out: T;
    try {
      out = f();
    } finally {
      this.batching = false;
    }
    // Nothing changed: no undo step, and redo survives.
    if (JSON.stringify(before) !== JSON.stringify(this.snapshot())) {
      this.undoStack.length = undoLen;
      this.undoStack.push(before);
      if (this.undoStack.length > MAX_UNDO) this.undoStack.shift();
      this.redoStack = [];
      this.changed();
    }
    return out;
  }

  /** Records the state before a change, for undo. */
  private record() {
    if (this.batching) return;
    this.undoStack.push(this.snapshot());
    if (this.undoStack.length > MAX_UNDO) this.undoStack.shift();
    this.redoStack = [];
  }

  /** Stages one or more cell values as one undo step. Cells that cannot be edited are skipped; returns how many were staged. */
  setCells(changes: { ref: RowRef; ordinal: number; cell: Cell }[], fullValue = false): number {
    const ok = changes.filter((c) => this.lockReason(c.ref, c.ordinal, fullValue) == null);
    if (!ok.length) return 0;
    this.record();
    for (const { ref, ordinal, cell } of ok) this.put(ref, ordinal, cell);
    this.changed();
    return ok.length;
  }

  setCell(ref: RowRef, ordinal: number, cell: Cell, fullValue = false): boolean {
    return this.setCells([{ ref, ordinal, cell }], fullValue) === 1;
  }

  private put(ref: RowRef, ordinal: number, cell: Cell) {
    const col = this.column(ordinal)!;
    const next: Cell = cell.isDefault ? { value: null, isDefault: true } : { value: cell.value };
    if (ref.kind === "inserted") {
      this.insertedRow(ref.index)?.values.set(ordinal, next);
    } else {
      const same = !next.isDefault && next.value === this.original(ref.row, ordinal);
      let m = this.edits.get(ref.row);
      if (same) {
        m?.delete(ordinal);
        if (m && !m.size) this.edits.delete(ref.row);
      } else {
        if (!m) this.edits.set(ref.row, (m = new Map()));
        m.set(ordinal, next);
      }
    }
    this.validateCell(ref, ordinal, col, next);
  }

  private validateCell(ref: RowRef, ordinal: number, col: EditColumn, c: Cell | undefined) {
    const key = errKey(ref, ordinal);
    this.errors.delete(key);
    if (!c) return;
    if (c.isDefault) {
      if (ref.kind === "existing") this.errors.set(key, "DEFAULT is only allowed on new rows");
      return;
    }
    if (c.value == null) {
      if (!col.nullable) this.errors.set(key, `${col.column} does not allow NULL`);
      return;
    }
    const err = validateCellInput(col.type ?? "", c.value, this.engine);
    if (err) this.errors.set(key, err);
  }

  private revalidate() {
    this.errors.clear();
    // A row marked for deletion is never sent with its edits (buildPatch skips them), so they cannot block Apply.
    for (const [row, m] of this.edits) if (!this.deleted.has(row)) for (const [o, c] of m) this.validateCell({ kind: "existing", row }, o, this.column(o)!, c);
    this.inserted.forEach((r, index) => {
      for (const [o, c] of r.values) this.validateCell({ kind: "inserted", index }, o, this.column(o)!, c);
    });
  }

  /** Appends an empty new row; returns its index among inserted rows. */
  addRow(values?: Map<number, Cell>): number {
    if (!this.canInsert) return -1;
    this.record();
    const row: InsertedRow = { id: this.nextId++, values: new Map() };
    this.inserted.push(row);
    const index = this.inserted.length - 1;
    if (values) for (const [o, c] of values) if (this.lockReason({ kind: "inserted", index }, o) == null) this.put({ kind: "inserted", index }, o, c);
    this.changed();
    return index;
  }

  /** Marks existing rows for deletion (or unmarks them); removes pending inserted rows outright. */
  toggleDelete(refs: RowRef[]) {
    const existing = refs.filter((r): r is { kind: "existing"; row: number } => r.kind === "existing" && !this.appliedDeleted.has(r.row));
    const inserted = refs.filter((r): r is { kind: "inserted"; index: number } => r.kind === "inserted").map((r) => r.index);
    if (!existing.length && !inserted.length) return;
    this.record();
    const allDeleted = existing.length > 0 && existing.every((r) => this.deleted.has(r.row));
    for (const r of existing) {
      if (allDeleted) this.deleted.delete(r.row);
      else this.deleted.add(r.row);
    }
    if (inserted.length) {
      const drop = new Set(inserted);
      this.inserted = this.inserted.filter((_, i) => !drop.has(i));
    }
    this.revalidate();
    this.changed();
  }

  /**
   * Copies rows into new rows (one undo step) and returns their indexes among inserted rows. Server-generated and
   * locked columns are left to the server; a key column is never copied (it would collide): DEFAULT when it has one,
   * else empty so validation asks for a new value. A value the grid holds only shortened is not copied either.
   */
  duplicateRows(refs: RowRef[]): number[] {
    if (!this.canInsert) return [];
    const seen = new Set<string>();
    const src = refs.filter((r) => {
      const k = r.kind === "inserted" ? `i${this.inserted[r.index]?.id}` : `e${r.row}`;
      if (seen.has(k) || (r.kind === "existing" && this.appliedDeleted.has(r.row)) || (r.kind === "inserted" && !this.inserted[r.index])) return false;
      seen.add(k);
      return true;
    }).slice(0, MAX_DUPLICATE);
    if (!src.length) return [];
    // Read every source cell first: the copies are appended to `inserted`, which inserted-row refs index into.
    const copies = src.map((ref) => {
      const values = new Map<number, Cell>();
      for (const col of this.edit.columns) {
        const o = col.ordinal;
        if (!col.column || col.generated || col.locked) continue;
        if (col.key || this.edit.keyOrdinals.includes(o)) {
          values.set(o, col.hasDefault ? { value: null, isDefault: true } : { value: null });
          continue;
        }
        const c = this.cell(ref, o);
        const shortened = ref.kind === "existing" && !c.staged && this.isTruncated(ref.row, o);
        if (c.unknown || shortened) values.set(o, col.hasDefault ? { value: null, isDefault: true } : { value: null });
        else values.set(o, c.isDefault ? { value: null, isDefault: true } : { value: c.value });
      }
      return values;
    });
    return this.batch(() => copies.map((values) => this.addRow(values)));
  }

  /** Reverts staged values of the given cells (or whole rows when ordinals is omitted); a new row goes back to empty. */
  revert(rows: RowRef[], ordinals?: number[]) {
    const touches = rows.some((ref) =>
      ref.kind === "inserted"
        ? (this.inserted[ref.index]?.values.size ?? 0) > 0
        : this.edits.has(ref.row) || (!ordinals && this.deleted.has(ref.row)),
    );
    if (!touches) return; // nothing to revert: keep the redo history
    this.record();
    for (const ref of rows) {
      if (ref.kind === "inserted") {
        const r = this.inserted[ref.index];
        if (!r) continue;
        if (!ordinals) r.values.clear();
        else for (const o of ordinals) r.values.delete(o);
        continue;
      }
      if (!ordinals) {
        this.edits.delete(ref.row);
        this.deleted.delete(ref.row);
      } else {
        const m = this.edits.get(ref.row);
        for (const o of ordinals) m?.delete(o);
        if (m && !m.size) this.edits.delete(ref.row);
      }
    }
    this.revalidate();
    this.changed();
  }

  discardAll() {
    if (!this.hasChanges) return;
    this.record();
    this.edits.clear();
    this.inserted = [];
    this.deleted.clear();
    this.errors.clear();
    this.changed();
  }

  undo(): boolean {
    const s = this.undoStack.pop();
    if (!s) return false;
    this.redoStack.push(this.snapshot());
    this.restore(s);
    this.changed();
    return true;
  }

  redo(): boolean {
    const s = this.redoStack.pop();
    if (!s) return false;
    this.undoStack.push(this.snapshot());
    this.restore(s);
    this.changed();
    return true;
  }

  get canUndo() {
    return this.undoStack.length > 0;
  }

  get canRedo() {
    return this.redoStack.length > 0;
  }

  get hasChanges(): boolean {
    return this.edits.size > 0 || this.inserted.length > 0 || this.deleted.size > 0;
  }

  get counts(): StagedCounts {
    return { updates: [...this.edits.keys()].filter((r) => !this.deleted.has(r)).length, inserts: this.inserted.length, deletes: this.deleted.size };
  }

  /**
   * One entry per invalid cell (never merged: five bad numbers are five problems), and per required value a new row
   * lacks. Apply is refused while there are any.
   */
  problems(): string[] {
    const out: string[] = [];
    for (const [key, msg] of this.errors) {
      const [where] = key.split(":");
      out.push(where.startsWith("i") ? `New row ${Number(where.slice(1)) + 1}: ${msg}` : `Row ${Number(where) + 1}: ${msg}`);
    }
    this.inserted.forEach((r, i) => {
      for (const c of this.edit.columns) if (this.missing(r, c) && !this.errors.has(errKey({ kind: "inserted", index: i }, c.ordinal))) out.push(`New row ${i + 1}: ${c.column} needs a value`);
    });
    return out;
  }

  // ------------------------------------------------------------------------------------------ tokens

  /** Edited or deleted rows without a token yet, with the displayed values the server must still have. */
  rowsNeedingTokens(): { row: number; expect: Record<string, GridValue> }[] {
    const out: { row: number; expect: Record<string, GridValue> }[] = [];
    const rows = new Set([...this.edits.keys(), ...this.deleted]);
    for (const row of rows) {
      if (this.tokens.has(row) || this.conflicted.has(row)) continue;
      const ordinals = this.deleted.has(row) ? this.edit.columns.filter((c) => c.column && c.comparable).map((c) => c.ordinal) : [...(this.edits.get(row)?.keys() ?? [])];
      const expect: Record<string, GridValue> = {};
      for (const o of ordinals) {
        const c = this.column(o);
        if (c?.column && c.comparable) expect[c.column] = gridValue({ value: this.original(row, o) });
      }
      out.push({ row, expect });
    }
    return out;
  }

  keyOf(row: number): GridValue[] {
    return this.edit.keyOrdinals.map((o) => gridValue({ value: this.original(row, o) }));
  }

  /** Stores tokens read by data.rowTokens; a null token means the row changed on the server since the query. */
  setTokens(rows: number[], tokens: (string | null)[]) {
    rows.forEach((row, i) => {
      const t = tokens[i];
      if (t == null) this.conflicted.set(row, CHANGED_ON_SERVER);
      else this.tokens.set(row, t);
    });
    this.changed();
  }

  // ------------------------------------------------------------------------------------------ patch

  buildPatch(sessionId: string, executionId: string, resultIndex: number, force = false): GridPatchParams {
    const name = (o: number) => this.column(o)!.column!;
    const updates = [...this.edits]
      .filter(([row, m]) => !this.deleted.has(row) && m.size)
      .map(([row, m]) => ({
        rowIndex: row,
        key: this.keyOf(row),
        token: this.edit.tokenKind ? this.tokens.get(row) ?? null : null,
        changes: Object.fromEntries([...m].map(([o, c]) => [name(o), gridValue(c)])),
        original: Object.fromEntries([...m.keys()].map((o) => [name(o), gridValue({ value: this.original(row, o) })])),
      }));
    const inserts = this.inserted.map((r, i) => ({
      tempIndex: i,
      values: Object.fromEntries([...r.values].filter(([o]) => this.column(o)?.column && !this.column(o)!.generated).map(([o, c]) => [name(o), gridValue(c)])),
    }));
    const comparable = this.edit.columns.filter((c) => c.column && c.comparable);
    const deletes = [...this.deleted].map((row) => ({
      rowIndex: row,
      key: this.keyOf(row),
      token: this.edit.tokenKind ? this.tokens.get(row) ?? null : null,
      original: Object.fromEntries(comparable.map((c) => [c.column!, gridValue({ value: this.original(row, c.ordinal) })])),
    }));
    const patch: GridPatchParams = { sessionId, executionId, resultIndex, updates, inserts, deletes, confirmedProd: false, confirmedDestructive: false, forceOverwriteConflicts: force };
    this.sent.set(patch, this.carried());
    return patch;
  }

  /** What a patch built now carries (copies: later edits never change it). */
  private carried(): Sent {
    return {
      updates: new Map([...this.edits].filter(([row, m]) => !this.deleted.has(row) && m.size).map(([row, m]) => [row, new Map([...m].map(([o, c]) => [o, { ...c }]))])),
      inserts: this.inserted.map((r) => ({ id: r.id, values: new Map([...r.values].map(([o, c]) => [o, { ...c }])) })),
      deletes: [...this.deleted],
    };
  }

  /**
   * After a successful apply: what the patch carried becomes the rows' values — written cells the rows' values, deleted
   * rows stay deleted, new rows become existing rows. W2: only what it carried; what was staged while it was in flight
   * stays staged (a newer value of a written cell, now edited over the written one; a new row never sent; another
   * edit or deletion), and a sent new row edited meanwhile keeps those edits as edits of the row it became.
   */
  commit(patch: GridPatchParams, result: GridApplyResult) {
    const sent = this.sent.get(patch) ?? this.carried();
    this.sent.delete(patch);
    for (const [row, cells] of sent.updates) {
      let ov = this.applied.get(row);
      if (!ov) this.applied.set(row, (ov = new Map()));
      const staged = this.edits.get(row);
      for (const [o, c] of cells) {
        ov.set(o, c.value);
        const now = staged?.get(o);
        if (now && !now.isDefault && !c.isDefault && now.value === c.value) staged!.delete(o); // written: no longer staged
      }
      if (staged && !staged.size) this.edits.delete(row);
      this.tokens.delete(row); // the row has a new version now
    }
    for (const row of sent.deletes) {
      this.appliedDeleted.add(row);
      this.tokens.delete(row);
      this.deleted.delete(row);
      this.edits.delete(row); // the row is gone: an edit made meanwhile has nothing to change
    }
    sent.inserts.forEach((r, i) => {
      const values = new Map<number, string | null>();
      const unknown = new Set<number>();
      for (const c of this.edit.columns) {
        const v = r.values.get(c.ordinal);
        if (v && !v.isDefault) values.set(c.ordinal, v.value);
        else unknown.add(c.ordinal);
      }
      const keys = result.insertedKeys?.[String(i)];
      this.edit.keyOrdinals.forEach((o, k) => {
        const kv = keys?.[k];
        if (kv) {
          values.set(o, kv.isNull ? null : kv.text ?? null);
          unknown.delete(o);
        }
      });
      this.committed.push({ values, unknown });
      const at = this.inserted.findIndex((x) => x.id === r.id);
      if (at < 0) return; // removed from the grid meanwhile: it was written all the same, and shows as an existing row
      const [now] = this.inserted.splice(at, 1);
      const row = this.src.rowCount + this.committed.length - 1;
      for (const [o, c] of now.values) {
        const before = r.values.get(o);
        const same = !!before && !!before.isDefault === !!c.isDefault && before.value === c.value;
        if (!same && !c.isDefault && this.lockReason({ kind: "existing", row }, o, true) == null) this.put({ kind: "existing", row }, o, c);
      }
    });
    this.revalidate();
    this.undoStack = [];
    this.redoStack = [];
    this.changed();
  }

  /** After a conflict: rows the server changed are marked so they cannot be edited again until re-read. */
  markConflicts(rows: number[], message = CHANGED_ON_SERVER) {
    for (const r of rows) {
      this.conflicted.set(r, message);
      this.tokens.delete(r);
    }
    this.changed();
  }

  /** Drops the staged changes of rows (after the user discarded the conflicting ones). */
  dropRows(rows: number[]) {
    if (!rows.some((r) => this.edits.has(r) || this.deleted.has(r))) return;
    this.record();
    for (const r of rows) {
      this.edits.delete(r);
      this.deleted.delete(r);
    }
    this.revalidate();
    this.changed();
  }
}

export function gridValue(c: Cell): GridValue {
  if (c.isDefault) return { isNull: false, useDefault: true, text: null };
  return c.value == null ? { isNull: true, useDefault: false, text: null } : { isNull: false, useDefault: false, text: c.value };
}

function errKey(ref: RowRef, ordinal: number) {
  return ref.kind === "inserted" ? `i${ref.index}:${ordinal}` : `${ref.row}:${ordinal}`;
}

/** Staging per (execution, result set), created when the user first edits. */
export const stagings = new Map<string, GridStaging>();

export const stagingKey = (executionId: string, resultIndex: number) => `${executionId}:${resultIndex}`;

/** Staged changes anywhere in these executions (tab close / re-run prompt). */
export function pendingIn(executionIds: readonly string[]): GridStaging[] {
  const ids = new Set(executionIds);
  return [...stagings.entries()].filter(([k, s]) => ids.has(k.slice(0, k.lastIndexOf(":"))) && s.hasChanges).map(([, s]) => s);
}

export function dropStagings(executionId: string) {
  for (const k of [...stagings.keys()]) if (k.startsWith(`${executionId}:`)) stagings.delete(k);
}
