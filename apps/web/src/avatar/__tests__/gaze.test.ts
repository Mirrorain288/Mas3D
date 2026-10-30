import { describe, expect, it } from 'vitest';
import { GAZE_LIMIT, gazeTarget } from '../gaze';

const head = { x: 0, y: 1.35, z: 0 };
const camFront = { x: 0, y: 1.45, z: 2.4 };
const camFarLeft = { x: -40, y: 1.45, z: 0.1 };

describe('gazeTarget', () => {
  it('looks straight at a camera sitting in front', () => {
    const g = gazeTarget(camFront, head, 'player');
    expect(Math.abs(g.yaw)).toBeLessThan(0.05);
    expect(g.pitch).toBeLessThan(0);
  });

  it('tilts the head down for the down mode', () => {
    expect(gazeTarget(camFront, head, 'down').pitch).toBeGreaterThan(0.2);
  });

  it('looks up for the up mode', () => {
    expect(gazeTarget(camFront, head, 'up').pitch).toBeLessThan(-0.2);
  });

  it('turns at least 25° away on the away mode', () => {
    const yawDeg = (Math.abs(gazeTarget(camFront, head, 'away').yaw) * 180) / Math.PI;
    expect(yawDeg).toBeGreaterThanOrEqual(25);
  });

  it('clamps extreme angles to the 35° eyeball limit', () => {
    const g = gazeTarget(camFarLeft, head, 'player');
    expect(Math.abs(g.yaw)).toBeLessThanOrEqual(GAZE_LIMIT + 1e-9);
    expect(Math.abs(g.pitch)).toBeLessThanOrEqual(GAZE_LIMIT + 1e-9);
  });

  it('points left and right without falling back to the camera', () => {
    expect(gazeTarget(camFront, head, 'left').yaw).toBeLessThan(0);
    expect(gazeTarget(camFront, head, 'right').yaw).toBeGreaterThan(0);
  });
});
