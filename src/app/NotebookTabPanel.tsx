// The Dockview panel of a notebook tab (Phase C3). The notebook's React code loads with the first notebook shown.
import { lazy, Suspense, useEffect, useState } from "react";
import type { IDockviewPanelProps } from "dockview";

const NotebookTab = lazy(() => import("../notebook/NotebookTab").then((m) => ({ default: m.NotebookTab })));

type Params = { tabId: string };

export function NotebookTabPanel(props: IDockviewPanelProps<Params>) {
  const [visible, setVisible] = useState(props.api.isVisible);
  const [groupId, setGroupId] = useState(props.api.group.id);
  useEffect(() => {
    const a = props.api.onDidVisibilityChange((e) => setVisible(e.isVisible));
    const b = props.api.onDidGroupChange(() => setGroupId(props.api.group.id));
    return () => {
      a.dispose();
      b.dispose();
    };
  }, [props.api]);
  return (
    <Suspense fallback={<div className="h-full flex items-center justify-center text-muted text-xs bg-bg">Loading the notebook…</div>}>
      <NotebookTab tabId={props.params.tabId} groupId={groupId} visible={visible} />
    </Suspense>
  );
}
