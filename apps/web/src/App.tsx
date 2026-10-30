import { useCallback, useState } from "react";
import { Scene, type SceneApi } from "./scene/Scene";
import { ChatPanel } from "./chat/ChatPanel";

export function App() {
  const [api, setApi] = useState<SceneApi | null>(null);

  const handleReady = useCallback((next: SceneApi) => {
    setApi(next);
    document.body.dataset.scene = "ready";
  }, []);

  return (
    <div className="app">
      <Scene onReady={handleReady} />
      <ChatPanel api={api} />
      <footer className="credit">
        Monika model by tyler1234506 · hub.vroid.com · 非商用
      </footer>
    </div>
  );
}
