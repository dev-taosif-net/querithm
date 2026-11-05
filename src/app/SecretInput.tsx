import { useLayoutEffect, useRef, type InputHTMLAttributes } from "react";

/**
 * A text box for a secret (a password, a token, a passphrase, a connection string): uncontrolled, so the value never
 * becomes the input's `value` attribute in the page's HTML (React writes a controlled input's value there, where a page
 * serializer or DevTools would show it); the value prop is mirrored into the box's property only.
 */
export function SecretInput({ value, onValue, ...rest }: Omit<InputHTMLAttributes<HTMLInputElement>, "value" | "defaultValue" | "onChange"> & {
  value: string;
  onValue: (v: string) => void;
  "data-no-enter-save"?: boolean;
}) {
  const ref = useRef<HTMLInputElement>(null);
  useLayoutEffect(() => {
    if (ref.current && ref.current.value !== value) ref.current.value = value;
  }, [value]);
  return <input ref={ref} {...rest} onChange={(e) => onValue(e.target.value)} />;
}
