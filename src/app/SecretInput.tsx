import { useLayoutEffect, useRef, useState, type InputHTMLAttributes } from "react";
import { Icon } from "./icons";

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

/**
 * A SecretInput with an eye button that shows what was typed and hides it again. Hidden at first, and the value still
 * never reaches the page's HTML when shown (the box only changes its type). The button keeps one name ("Show password")
 * with a pressed state, is reachable by Tab after the box, never submits the dialog, and a click leaves the caret in the
 * box.
 */
export function PasswordInput({ what = "password", className = "", ...rest }: Omit<Parameters<typeof SecretInput>[0], "type"> & { what?: string }) {
  const [shown, setShown] = useState(false);
  return (
    <span className="relative block">
      <SecretInput {...rest} className={className} style={{ ...rest.style, paddingRight: 34 }} type={shown ? "text" : "password"} spellCheck={false} autoCapitalize="off" autoCorrect="off" />
      <button
        type="button"
        className="q-icon-btn absolute right-1 top-1/2 -translate-y-1/2 w-7 h-6"
        title={shown ? "Hide" : "Show"}
        aria-label={`Show ${what}`}
        aria-pressed={shown}
        data-layout-adornment
        // Keeps the caret in the box: the click toggles, focus stays where the user is typing.
        onMouseDown={(e) => e.preventDefault()}
        onClick={(e) => {
          e.preventDefault(); // inside a <label>: no click forwarded to the box
          setShown((v) => !v);
        }}
      >
        <Icon name={shown ? "eyeOff" : "eye"} size={15} strokeWidth={1.9} />
      </button>
    </span>
  );
}
