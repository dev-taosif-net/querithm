// Brand mark (docs/brand/querithm-logo.svg): the database is the Q, its middle layer runs out as a violet pulse.
// Transparent in every theme. `tile` keeps the app icon's padding (About, welcome); without it the mark is cropped
// tight so it stays legible at toolbar size.
import { useId } from "react";

const BODY =
  "M108 180 A148 88 0 0 1 404 180 A148 88 0 0 1 108 180 Z M152 180 A104 48 0 0 0 360 180 A104 48 0 0 0 152 180 Z " +
  "M108 214 A148 78 0 0 0 262 291.9 L262 327.9 A148 84 0 0 1 108 244 Z " +
  "M108 274 A148 78 0 0 0 316 345.3 L316 380.8 A148 84 0 0 1 108 304 Z";
const PULSE = "M300 310 L330 310 L352 278 L388 400 L414 310 L446 310";

export function Logo({ className, tile = false }: { className?: string; tile?: boolean }) {
  const id = `qlogo${useId().replace(/[^\w-]/g, "")}`; // url(#…) refs need a plain id
  const body = `${id}-body`;
  const pulse = `${id}-pulse`;
  return (
    <svg className={className} viewBox={tile ? "84 53 404 404" : "102 71 368 368"} aria-hidden>
      <defs>
        <linearGradient id={body} gradientUnits="userSpaceOnUse" x1="140" y1="92" x2="330" y2="390">
          <stop offset="0" stopColor="#38BDF8" />
          <stop offset="0.55" stopColor="#2F6FED" />
          <stop offset="1" stopColor="#4F46E5" />
        </linearGradient>
        <linearGradient id={pulse} gradientUnits="userSpaceOnUse" x1="296" y1="0" x2="450" y2="0">
          <stop offset="0" stopColor="#D8B4FE" />
          <stop offset="1" stopColor="#A855F7" />
        </linearGradient>
      </defs>
      <path fillRule="evenodd" fill={`url(#${body})`} d={BODY} />
      <path d={PULSE} fill="none" stroke={`url(#${pulse})`} strokeWidth="36" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
