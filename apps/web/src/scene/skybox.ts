import * as THREE from "three";

export const SKY_2K_URL = "/assets/hdri/night-sky-2k.jpg";
export const SKY_1K_URL = "/assets/hdri/night-sky-1k.jpg";

export interface SkyboxHandle {
  /** 亮起的等距柱状贴图；加载失败时为 null */
  texture: THREE.Texture | null;
  url: string;
}

/**
 * 反向天空盒：等距柱状 JPG 直接当 scene.background（three 自动做立方体映射），
 * 同时挂到 scene.environment 给金属/皮肤一点点环境反射 —— 强度压到 0.35，
 * 否则 VRM 的皮肤会被夜空照成蓝紫色。
 * 加载失败不抛错：保留深色背景，让教室照常可玩。
 */
export async function applySkybox(scene: THREE.Scene, url: string = SKY_2K_URL): Promise<SkyboxHandle> {
  const texture = await new THREE.TextureLoader().loadAsync(url);
  texture.mapping = THREE.EquirectangularReflectionMapping;
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.minFilter = THREE.LinearFilter;
  texture.generateMipmaps = false;

  scene.background = texture;
  scene.environment = texture;
  scene.environmentIntensity = 0.35;

  return { texture, url };
}

export function disposeSkybox(scene: THREE.Scene, handle: SkyboxHandle | null): void {
  if (scene.background === handle?.texture) scene.background = new THREE.Color(0x05070d);
  if (scene.environment === handle?.texture) scene.environment = null;
  handle?.texture?.dispose();
}

/** 天空盒加载失败时的兜底：深色渐变 + 星点由 CSS 背景提供，这里只留纯色。 */
export function fallbackSky(scene: THREE.Scene): void {
  scene.background = new THREE.Color(0x05070d);
  scene.environment = null;
}
