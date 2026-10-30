import { describe, expect, it } from 'vitest';
import { deriveCue } from '../src/index';

describe('deriveCue', () => {
  it('ignores emotion words inside quoted speech', () => {
    expect(deriveCue('她说"我很生气"，然后笑了').emotion).not.toBe('angry');
  });

  it('is deterministic', () => {
    expect(deriveCue('太好了！！')).toEqual(deriveCue('太好了！！'));
  });

  it('clamps intensity to 1 with repeated exclamation', () => {
    expect(deriveCue('真的！！！').intensity).toBeLessThanOrEqual(1);
  });

  it('maps 点头 to nod', () => {
    expect(deriveCue('嗯，点头').gesture).toBe('nod');
  });

  it('returns neutral for empty-ish text', () => {
    expect(deriveCue('……').emotion).toBe('neutral');
  });
});
