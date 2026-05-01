/*
 * Home — Face Blood app entry point
 * Bio-Lab Noir design: full-screen camera canvas with HUD overlays.
 * No padding, no wrapper — PulseVisualizer fills the entire viewport.
 */

import PulseVisualizer from "@/components/PulseVisualizer";

export default function Home() {
  return (
    <div
      className="fixed inset-0 bg-black overflow-hidden"
      style={{ touchAction: "none" }}
    >
      <PulseVisualizer amplification={4} />
    </div>
  );
}
