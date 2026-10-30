import * as THREE from "three";

export interface SceneLights {
  root: THREE.Group;
  key: THREE.DirectionalLight;
  setShadows(enabled: boolean): void;
  dispose(): void;
}

/**
 * 太空教室的打光刻意「窄」：一盏方向光负责脸上的明暗，
 * 一盏半球光把阴影提亮到能看清，再用一盏冷色点光勾轮廓。
 * 没有 HDR 环境光的强反射 —— 太空舱不该像摄影棚。
 */
export function createLights(): SceneLights {
  const root = new THREE.Group();
  root.name = "lights";

  const key = new THREE.DirectionalLight(0xfff1e0, 2.1);
  key.name = "key";
  key.position.set(2.1, 3.2, 2.4);
  key.target.position.set(0, 0.9, 0);
  key.castShadow = true;
  key.shadow.mapSize.set(1024, 1024);
  key.shadow.bias = -0.0006;
  key.shadow.normalBias = 0.015;
  const shadowCamera = key.shadow.camera;
  shadowCamera.near = 0.5;
  shadowCamera.far = 12;
  shadowCamera.left = -3.2;
  shadowCamera.right = 3.2;
  shadowCamera.top = 3.2;
  shadowCamera.bottom = -3.2;

  const fill = new THREE.HemisphereLight(0x9fb4ff, 0x0b0d14, 0.55);
  fill.name = "fill";

  const rim = new THREE.PointLight(0x6ea8ff, 14, 9, 2);
  rim.name = "rim";
  rim.position.set(-2.4, 2.3, -1.8);

  root.add(key, key.target, fill, rim);

  return {
    root,
    key,
    setShadows(enabled: boolean): void {
      key.castShadow = enabled;
    },
    dispose(): void {
      key.dispose();
      fill.dispose();
      rim.dispose();
      root.clear();
    },
  };
}
