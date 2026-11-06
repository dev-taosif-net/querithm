import { Component, lazy, Suspense, useState, type ComponentType, type ReactNode } from "react";
import { LoadingSkeleton } from "./LoadingStatus";
import { Modal } from "./Modal";

class LoadBoundary extends Component<{ children: ReactNode; fallback: (error: Error) => ReactNode }, { error: Error | null }> {
  state = { error: null as Error | null };
  static getDerivedStateFromError(error: Error) { return { error }; }
  render() { return this.state.error ? this.props.fallback(this.state.error) : this.props.children; }
}

/** Recreates React.lazy on retry: a rejected lazy promise otherwise stays rejected. */
export function lazyDialog<P extends object>(load: () => Promise<{ default: ComponentType<P> }>) {
  let cached = lazy(load);
  return function LazyDialog(props: P & { loadingTitle: string; onLoadClose: () => void }) {
    const { loadingTitle, onLoadClose, ...contentProps } = props;
    const [attempt, setAttempt] = useState(0);
    const [Content, setContent] = useState(() => cached);
    const retry = () => {
      cached = lazy(load);
      setContent(() => cached);
      setAttempt((n) => n + 1);
    };
    return <LoadBoundary key={attempt} fallback={(error) => (
      <Modal title={loadingTitle} onClose={onLoadClose} footer={<><button className="q-btn" onClick={onLoadClose}>Close</button><button className="q-btn q-btn-primary" onClick={retry}>Retry</button></>}>
        <div role="alert" className="text-err">Could not load this dialog. {error.message}</div>
      </Modal>
    )}>
      <Suspense fallback={<Modal title={loadingTitle} onClose={onLoadClose} footer={<button className="q-btn" onClick={onLoadClose}>Cancel</button>}><LoadingSkeleton label={`Loading ${loadingTitle.toLowerCase()}…`} /></Modal>}>
        <Content {...contentProps as P} />
      </Suspense>
    </LoadBoundary>;
  };
}
