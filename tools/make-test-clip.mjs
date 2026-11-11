import fs from 'node:fs';

// A synthetic stand-in clip so the Unity retargeter can be exercised before Kimodo finishes.
const joints = ['Hips','Spine1','Spine2','Chest','Neck1','Neck2','Head','HeadEnd',
  'LeftShoulder','LeftArm','LeftForeArm','LeftHand','RightShoulder','RightArm','RightForeArm','RightHand',
  'LeftLeg','LeftShin','LeftFoot','LeftToeBase','RightLeg','RightShin','RightFoot','RightToeBase'];

const base = {
  Hips:[0,0.62,0], Spine1:[0,0.72,0], Spine2:[0,0.84,0], Chest:[0,0.95,0],
  Neck1:[0,1.02,0], Neck2:[0,1.06,0], Head:[0,1.14,0], HeadEnd:[0,1.30,0],
  LeftShoulder:[0.16,1.00,0], LeftArm:[0.22,0.86,0.06], LeftForeArm:[0.18,0.76,0.30], LeftHand:[0.16,0.73,0.46],
  RightShoulder:[-0.16,1.00,0], RightArm:[-0.22,0.86,0.06], RightForeArm:[-0.18,0.76,0.30], RightHand:[-0.16,0.73,0.46],
  LeftLeg:[0.10,0.62,0.02], LeftShin:[0.11,0.32,0.20], LeftFoot:[0.11,0.06,0.36], LeftToeBase:[0.11,0.04,0.50],
  RightLeg:[-0.10,0.62,0.02], RightShin:[-0.11,0.32,0.20], RightFoot:[-0.11,0.06,0.36], RightToeBase:[-0.11,0.04,0.50],
};

const frameCount = 61;
const positions = [];
for (let f = 0; f < frameCount; f += 1) {
  const t = f / (frameCount - 1);
  const lift = Math.sin(t * Math.PI) * 0.18;   // hands rise and settle
  const out = Math.sin(t * Math.PI) * 0.08;
  for (const name of joints) {
    const p = [...base[name]];
    if (name.endsWith('Hand') || name.endsWith('ForeArm')) {
      p[1] += lift;
      p[0] += (name.startsWith('Left') ? 1 : -1) * out;
    }
    positions.push(...p.map((v) => Math.round(v * 100000) / 100000));
  }
}

const out = {
  fps: 30,
  joints,
  frameCount,
  jointCount: joints.length,
  positions,
  source: 'synthetic-standin',
  skeleton: 'somaskel77-subset',
};
fs.mkdirSync('D:/Mas3D/apps/unity/Assets/_Local/motion', { recursive: true });
fs.writeFileSync('D:/Mas3D/apps/unity/Assets/_Local/motion/hands.json', JSON.stringify(out));
console.log('wrote', joints.length, 'joints x', frameCount, 'frames');
