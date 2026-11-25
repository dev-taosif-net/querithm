// A time limit on one wait outside the webview (CLAUDE §4: no unbounded waits): the engine or the shell may never answer.

/** Rejects with `message` once `ms` pass before `p` settles; the timer is cleared either way. `p` keeps running. */
export function withTimeLimit<T>(p: Promise<T>, ms: number, message: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const limit = new Promise<never>((_, reject) => (timer = setTimeout(() => reject(new Error(message)), ms)));
  return Promise.race([p, limit]).finally(() => clearTimeout(timer));
}
