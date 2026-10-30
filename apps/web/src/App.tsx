import { useCallback } from "react";
import { Scene, type SceneApi } from "./scene/Scene";

export function App() {
  const handleReady = useCallback((_api: SceneApi) => {
    // Task 7/8 会把对话面板的 cue 接进来；本任务只确认场景可用
    document.body.dataset.scene = "ready";
  }, []);

  return (
    <div className="app">
      <Scene onReady={handleReady} />
      <footer className="credit">
        Monika model by tyler1234506 · hub.vroid.com · 非商用
      </footer>
    </div>
  );
}
