// A DOM stand-in for @xyflow/react (which needs real layout + ResizeObserver): renders each node through its
// registered node type and exposes the pane/node/edge handlers as clickable elements.
import { createElement as h } from "react";

export const flowMock = {
  ReactFlow: (p: any) =>
    h("div", { "data-testid": "flow" },
      p.nodes.map((n: any) => {
        const C = p.nodeTypes[n.type];
        return h("div", {
          key: n.id, "data-testid": `node-${n.id}`,
          onClick: (e: any) => p.onNodeClick(e, n),
        }, h(C, { data: n.data }));
      }),
      p.edges.map((e: any) =>
        h("button", {
          key: e.id, "data-testid": `edge-${e.id}`, "data-source-handle": e.sourceHandle, "data-target-handle": e.targetHandle, "data-type": e.type,
          onMouseEnter: () => p.onEdgeMouseEnter({}, e), onMouseLeave: () => p.onEdgeMouseLeave({}, e),
        }, e.label ?? "")),
      h("div", { "data-testid": "pane", onClick: () => p.onPaneClick() }),
      p.children),
  Background: () => h("i"),
  Controls: () => h("i"),
  MiniMap: () => h("i", { "data-testid": "minimap" }),
  Handle: (p: any) => h("i", { "data-handle": p.id }),
  Position: { Left: "left", Right: "right" },
};
