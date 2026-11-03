// Open modals, bottom to top. Every Modal listens on window, so only the topmost one may act on Escape/Enter
// (a confirm stacked over the Connect dialog must not close both).
const stack: object[] = [];

/** Registers a modal as the new top; the returned function removes it wherever it sits. */
export function pushModal(token: object): () => void {
  stack.push(token);
  return () => {
    const i = stack.lastIndexOf(token);
    if (i >= 0) stack.splice(i, 1);
  };
}

export function isTopModal(token: object): boolean {
  return stack[stack.length - 1] === token;
}
