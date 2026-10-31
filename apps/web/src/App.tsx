import { useCallback, useState } from "react";
import { Scene, type SceneApi } from "./scene/Scene";
import { ChatPanel } from "./chat/ChatPanel";

export function App() {
  const [api, setApi] = useState<SceneApi | null>(null);
  const [credit, setCredit] = useState<string | null>(null);

  const handleReady = useCallback((next: SceneApi) => {
    setApi(next);
    document.body.dataset.scene = "ready";
  }, []);

  return (
    <div className="app">
      <Scene onReady={handleReady} onCredit={setCredit} />
      <ChatPanel api={api} />
      {/* 署名跟着实际加载的模型走（credit=necessary）：文案由 VRM meta 派生，见 avatar/modelCredit.ts */}
      <footer className="credit">{credit ?? "角色模型加载中 · hub.vroid.com · 非商用"}</footer>
    </div>
  );
}
