import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { validatePlan } from '../src/plan';

interface Case {
  name: string;
  ok: boolean;
  plan: unknown;
}

// 与 apps/bridge/tests/test_plan_contract.py 共用同一份用例：跨语言契约不许分叉
const cases = JSON.parse(
  readFileSync(new URL('./fixtures/plan-cases.json', import.meta.url), 'utf8'),
) as { cases: Case[] };

describe('validatePlan contract shared with the bridge', () => {
  it('has cases', () => {
    expect(cases.cases.length).toBeGreaterThan(0);
  });

  for (const item of cases.cases) {
    it(item.name, () => {
      if (item.ok) {
        const plan = validatePlan(item.plan);
        expect(plan.operation.length).toBeGreaterThan(0);
        if (item.plan && typeof item.plan === 'object' && (item.plan as { reply?: unknown }).reply === null) {
          expect(plan.reply).toBeUndefined();
        }
      } else {
        expect(() => validatePlan(item.plan)).toThrow();
      }
    });
  }
});
