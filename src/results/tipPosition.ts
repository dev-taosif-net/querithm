/** Left edge for a tooltip anchored at `x`, shifted left so all `width` px stay `margin` px inside the viewport. */
export function clampLeft(x: number, width: number, viewport: number, margin = 4): number {
  return Math.max(margin, Math.min(x, viewport - width - margin));
}
