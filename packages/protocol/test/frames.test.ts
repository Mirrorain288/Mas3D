import { describe, expect, it } from 'vitest';
import { parseServerFrame, validatePlan, ProtocolError } from '../src/index';

describe('parseServerFrame', () => {
  it('parses a valid reply frame', () => {
    const f = parseServerFrame({ type: 'reply', turnId: 't1', text: '好呀', cue: { emotion: 'joy', intensity: 0.8, gaze: 'player', gesture: 'nod' } });
    expect(f.type).toBe('reply');
  });

  it('accepts a reply frame without a cue', () => {
    const f = parseServerFrame({ type: 'reply', turnId: 't2', text: '我在' });
    expect(f.type).toBe('reply');
    if (f.type === 'reply') expect(f.cue).toBeUndefined();
  });

  it('still rejects a malformed cue', () => {
    expect(() => parseServerFrame({ type: 'reply', turnId: 't3', text: '好', cue: { emotion: 'joy' } })).toThrow(ProtocolError);
  });

  it('rejects an unknown frame type', () => {
    expect(() => parseServerFrame({ type: 'nope' })).toThrow(ProtocolError);
  });
});

describe('validatePlan', () => {
  it('rejects a plan with five intents', () => {
    expect(() => validatePlan({ base: { change: false, name: 'x', motion: 0, rest: true }, operation: 'command', timing: '同时', intents: Array(5).fill({ key: 'arm', speed: 1, strength: 1, hold: 1, delay: 0 }) })).toThrow(ProtocolError);
  });

  it('rejects a chat plan carrying intents', () => {
    expect(() => validatePlan({ base: { change: false, name: 'x', motion: 0, rest: true }, operation: 'chat', timing: '同时', intents: [{ key: 'arm', speed: 1, strength: 1, hold: 1, delay: 0 }] })).toThrow(ProtocolError);
  });
});
