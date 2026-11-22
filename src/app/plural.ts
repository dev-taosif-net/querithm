/** "1 connection", "3 connections": a count with its noun, singular for exactly one. */
export const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;
