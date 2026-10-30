#!/usr/bin/env node
// 资产管线：把 素材/ 下的原始素材按 tools/assets.manifest.json 派生到前端发布目录。
//   node tools/sync-assets.mjs          生成/刷新派生资产，并把 bytes/sha256 回写清单
//   node tools/sync-assets.mjs --check   只校验，不写文件；任一不符退出码 1
import { createHash } from "node:crypto";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const MANIFEST = path.join(ROOT, "tools", "assets.manifest.json");

const abs = (rel) => path.join(ROOT, rel.split("/").join(path.sep));

async function digest(rel) {
  const buf = await readFile(abs(rel));
  return { bytes: buf.byteLength, sha256: createHash("sha256").update(buf).digest("hex") };
}

async function loadManifest() {
  const raw = await readFile(MANIFEST, "utf8");
  const parsed = JSON.parse(raw);
  if (!parsed || !Array.isArray(parsed.entries)) {
    throw new Error("assets.manifest.json 缺少 entries 数组");
  }
  return parsed;
}

async function saveManifest(manifest) {
  await writeFile(MANIFEST, JSON.stringify(manifest, null, 2) + "\n", "utf8");
}

function transformOf(entry) {
  const [kind, arg] = String(entry.transform || "").split(":");
  if (kind === "copy") return { kind };
  if (kind === "resize" || kind === "webp") {
    const width = Number(arg);
    if (!Number.isInteger(width) || width <= 0) {
      throw new Error(`transform 宽度非法: ${entry.target}`);
    }
    return { kind, width };
  }
  throw new Error(`未知 transform "${entry.transform}": ${entry.target}`);
}

async function check() {
  const manifest = await loadManifest();
  const problems = [];
  for (const entry of manifest.entries) {
    if (!existsSync(abs(entry.target))) {
      problems.push(`missing: ${entry.target}`);
      continue;
    }
    const got = await digest(entry.target);
    if (entry.bytes !== got.bytes) {
      problems.push(`size: ${entry.target} expected ${entry.bytes ?? "?"} got ${got.bytes}`);
    } else if (entry.sha256 !== got.sha256) {
      problems.push(`sha256: ${entry.target} expected ${entry.sha256 ?? "?"} got ${got.sha256}`);
    }
  }
  if (problems.length > 0) {
    for (const line of problems) console.error(line);
    console.error(`assets:check failed (${problems.length} of ${manifest.entries.length})`);
    process.exitCode = 1;
    return;
  }
  console.log(`assets:check ok (${manifest.entries.length} entries)`);
}

async function build() {
  const manifest = await loadManifest();
  const sharp = (await import("sharp")).default;
  for (const entry of manifest.entries) {
    const source = abs(entry.source);
    if (!existsSync(source)) throw new Error(`source 缺失: ${entry.source}`);
    await mkdir(path.dirname(abs(entry.target)), { recursive: true });
    const t = transformOf(entry);
    if (t.kind === "copy") {
      await copyFile(source, abs(entry.target));
    } else if (t.kind === "resize") {
      await sharp(source)
        .resize({ width: t.width })
        .jpeg({ quality: 85, mozjpeg: true })
        .toFile(abs(entry.target));
    } else {
      await sharp(source)
        .resize({ width: t.width })
        .webp({ quality: 82 })
        .toFile(abs(entry.target));
    }
    const got = await digest(entry.target);
    entry.bytes = got.bytes;
    entry.sha256 = got.sha256;
    console.log(`ok ${entry.target} ${got.bytes}`);
  }
  await saveManifest(manifest);
  console.log(`assets built (${manifest.entries.length} entries)`);
}

const mode = process.argv.includes("--check") ? "check" : "build";
await (mode === "check" ? check() : build());
