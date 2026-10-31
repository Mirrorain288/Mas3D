#!/usr/bin/env node
/**
 * 换模体检：只加载模型，不连桥接、不烧模型。
 * 输出坐姿几何（骨骼世界坐标 + 包围盒）、面部形变通道、截图路径。
 *
 *   node e2e/vrm-probe.mjs [--vrm URL] [--shot out.png] [--port 5186]
 */
import { fileURLToPath } from "node:url";
import path from "node:path";
import { writeFileSync, mkdirSync } from "node:fs";
import { chromium } from "playwright";

const here = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(here, "..");
const repoRoot = path.resolve(webRoot, "..", "..");

const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const index = args.indexOf(name);
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback;
};

const VRM = argOf("--vrm", "/assets/monika/Monkia.vrm");
const PORT = Number(argOf("--port", "5186"));
const SHOT = path.resolve(argOf("--shot", path.join(repoRoot, ".superpowers", "shots", "vrm-probe.png")));
const REQUIRED = ["Fcl_MTH_A", "Fcl_MTH_I", "Fcl_MTH_U", "Fcl_MTH_E", "Fcl_MTH_O", "Fcl_EYE_Close", "Fcl_ALL_Joy"];

const vite = await import("vite");
const server = await vite.createServer({
  root: webRoot,
  configFile: path.join(webRoot, "vite.config.ts"),
  server: { port: PORT, host: "localhost", strictPort: true },
  define: {
    "import.meta.env.VITE_VRM_URL": JSON.stringify(VRM),
    // 探针不测对话：把一个不会响应的地址钉进去，避免误打 8790 的生产桥接
    "import.meta.env.VITE_MAS3D_WS": JSON.stringify("ws://127.0.0.1:1/mas3d/ws"),
  },
  logLevel: "silent",
});
await server.listen();

const browser = await chromium.launch({
  channel: "msedge",
  args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist"],
});

let exitCode = 1;
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));
  await page.goto("http://localhost:" + PORT + "/", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => Boolean(window.__mas3dAvatar && window.__mas3dAvatar.api), null, { timeout: 90000 });

  const report = await page.evaluate(() => {
    const hook = window.__mas3dAvatar;
    const morphNames = new Set();
    hook.vrm.scene.traverse((object) => {
      if (object.isMesh && object.morphTargetDictionary) {
        for (const name of Object.keys(object.morphTargetDictionary)) morphNames.add(name);
      }
    });
    // 真正生效的是 FaceDriver 里的映射表（取自 glTF JSON 的 primitive extras），
    // morphTargetDictionary 只是 three 自己的命名，两者不一定一致
    const mapped = hook.driver && hook.driver.map ? Object.keys(hook.driver.map.targets) : [];
    const stats = hook.stats ? hook.stats() : null;
    return { pose: hook.pose(), stats, mappedTargets: mapped, morphNames: [...morphNames] };
  });

  // 表情通道自检：直接让驱动器写一次 joy 与元音 A，读回真实 influences
  const faceProbe = await page.evaluate(async () => {
    const hook = window.__mas3dAvatar;
    const meshes = [];
    hook.vrm.scene.traverse((object) => {
      if (object.isMesh && object.morphTargetInfluences) meshes.push(object);
    });
    // 走产品真实入口（ChatPanel 调的就是这个 api），而不是直接找 driver
    hook.api.setEmotion("joy", 1);
    hook.driver.setVisemes({ A: 1 });
    const read = () => meshes.map((mesh) => ({
      name: mesh.name,
      count: mesh.morphTargetInfluences.length,
      joy: Number((mesh.morphTargetInfluences[3] ?? 0).toFixed(3)),
      mthA: Number((mesh.morphTargetInfluences[39] ?? 0).toFixed(3)),
    }));
    const immediate = read();
    const syncDriverEmotion = hook.driver.emotion;
    const samples = [];
    for (let i = 0; i < 8; i += 1) {
      await new Promise((resolve) => setTimeout(resolve, 150));
      samples.push({
        t: (i + 1) * 150,
        emotion: hook.driver.emotion,
        intensity: hook.driver.intensity,
        joy: Number((meshes[0].morphTargetInfluences[3] ?? 0).toFixed(3)),
      });
    }
    const later = read();
    return {
      meshCount: meshes.length,
      picked: hook.driver.mesh ? { name: hook.driver.mesh.name, count: hook.driver.mesh.morphTargetInfluences?.length ?? 0 } : null,
      degraded: hook.driver.degradedCount,
      syncDriverEmotion,
      immediate,
      samples,
      later,
    };
  });
  writeFileSync(SHOT.replace(/\.png$/, ".face.json"), JSON.stringify(faceProbe, null, 2) + "\n", "utf8");
  process.stdout.write("face probe: " + JSON.stringify(faceProbe) + "\n");

  await page.waitForTimeout(600);
  mkdirSync(path.dirname(SHOT), { recursive: true });
  await page.screenshot({ path: SHOT });
  writeFileSync(SHOT.replace(/\.png$/, ".json"), JSON.stringify(report, null, 2) + "\n", "utf8");

  const known = report.mappedTargets.length > 0 ? report.mappedTargets : report.morphNames;
  const missing = REQUIRED.filter((name) => !known.includes(name));
  const out = {
    vrm: VRM,
    mappedTargets: report.mappedTargets.length,
    morphTargets: report.morphNames.length,
    missingMorphs: missing,
    stats: report.stats,
    pose: report.pose,
    pageErrors,
    shot: SHOT,
  };
  process.stdout.write(JSON.stringify(out, null, 2) + "\n");
  exitCode = missing.length === 0 && pageErrors.length === 0 ? 0 : 1;
} catch (error) {
  process.stdout.write("probe failed: " + (error instanceof Error ? error.message : String(error)) + "\n");
} finally {
  await browser.close().catch(() => {});
  await server.close().catch(() => {});
}

process.exit(exitCode);
