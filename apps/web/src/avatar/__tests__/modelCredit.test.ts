import { describe, expect, it } from 'vitest';
import { creditFromMeta } from '../modelCredit';

describe('creditFromMeta', () => {
  it('VRM 0.x 用 title 与 author', () => {
    expect(creditFromMeta({ metaVersion: '0', title: 'Monkia', author: 'LilowLeeLani' })).toBe(
      'Monkia model by LilowLeeLani · hub.vroid.com · 非商用',
    );
  });

  it('VRM 1.0 退到 name 与 authors 数组', () => {
    expect(creditFromMeta({ name: 'Alicia', authors: ['someone', ' ', 'other'] })).toBe(
      'Alicia model by someone, other · hub.vroid.com · 非商用',
    );
  });

  it('缺作者时说明未声明，不冒用别的名字', () => {
    expect(creditFromMeta({ title: 'NoName' })).toBe('NoName · 未声明作者 · hub.vroid.com · 非商用');
  });

  it('meta 为空或字段类型不对时给兜底文案', () => {
    expect(creditFromMeta(null)).toBe('该模型 · 未声明作者 · hub.vroid.com · 非商用');
    expect(creditFromMeta({ title: 42, author: {} })).toBe('该模型 · 未声明作者 · hub.vroid.com · 非商用');
  });

  it('两个模型各自的署名不同', () => {
    const monkia = creditFromMeta({ title: 'Monkia', author: 'LilowLeeLani' });
    const monika = creditFromMeta({ title: 'Monika', author: 'tyler1234506' });
    expect(monkia).not.toBe(monika);
    expect(monkia).toContain('LilowLeeLani');
    expect(monika).toContain('tyler1234506');
  });
});
