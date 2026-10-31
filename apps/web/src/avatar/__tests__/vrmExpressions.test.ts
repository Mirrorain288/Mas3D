import { describe, expect, it } from 'vitest';
import { detachExpressions } from '../vrmExpressions';

function fakeManager(names: string[]) {
  const registered = names.map((name) => ({ name }));
  const unregistered: string[] = [];
  const manager = {
    get expressions() {
      return registered.filter((item) => !unregistered.includes(item.name));
    },
    unregisterExpression(expression: unknown) {
      unregistered.push((expression as { name: string }).name);
    },
  };
  return { manager, unregistered };
}

describe('detachExpressions', () => {
  it('注销管理器里的每一个表达式', () => {
    const { manager, unregistered } = fakeManager(['joy', 'aa', 'blink']);
    expect(detachExpressions(manager)).toBe(3);
    expect(unregistered).toEqual(['joy', 'aa', 'blink']);
    expect(manager.expressions).toHaveLength(0);
  });

  it('没有表达式时是空操作', () => {
    const { manager } = fakeManager([]);
    expect(detachExpressions(manager)).toBe(0);
  });

  it('管理器缺失（VRM 1.0 之外的模型）时返回 0', () => {
    expect(detachExpressions(null)).toBe(0);
    expect(detachExpressions(undefined)).toBe(0);
  });

  it('注销期间的列表变化不会漏掉后面的表达式', () => {
    const { manager, unregistered } = fakeManager(['a', 'b', 'c', 'd']);
    detachExpressions(manager);
    expect(unregistered).toEqual(['a', 'b', 'c', 'd']);
  });
});
