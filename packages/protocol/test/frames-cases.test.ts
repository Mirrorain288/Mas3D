import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { parseServerFrame } from '../src';

// 语料里的 error 是解析器抛出的原文，直接当正则用会把 [0]、.、|、() 当成元字符。
const escapeRegExp = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

const corpus = JSON.parse(readFileSync(fileURLToPath(new URL('./fixtures/frames-cases.json', import.meta.url)), 'utf8')) as {
  cases: { name: string; ok: boolean; frame?: unknown; error?: string }[];
};

describe('frames corpus', () => {
  for (const c of corpus.cases) {
    it(c.name, () => {
      const run = () => parseServerFrame(c.frame);
      if (c.ok) {
        expect(c.error).toBeUndefined();
        expect(run).not.toThrow();
        return;
      }
      // ok:false 的用例必须把错误原文写进语料：C# 镜像要逐字比同一句话（FrameProtocolTests.cs）。
      expect(typeof c.error).toBe('string');
      expect(run).toThrow(new RegExp(escapeRegExp(c.error as string)));
    });
  }
});
