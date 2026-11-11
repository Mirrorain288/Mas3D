"""Convert a Kimodo motion .npz into the flat JSON the Unity spike reads.

Usage:
    python tools/kimodo-to-unity-motion.py <in.npz> <out.json> [--fps 30] [--skeleton somaskel77]

The spike only needs joint positions: the Unity side aims every mapped bone at its
child's target position, so rotations are re-solved on the real VRM rig instead of
being transferred across skeletons.
"""

import argparse
import json

import numpy as np


def load(path):
    with np.load(path, allow_pickle=True) as data:
        return {key: data[key] for key in data.files}


def skeleton_names(blob, skeleton, joint_count):
    for key in ("joint_names", "joints", "skeleton_joint_names", "names"):
        if key in blob:
            value = blob[key]
            return [str(v) for v in np.asarray(value).reshape(-1)]

    # Kimodo's own npz carries no names: rebuild them from the skeleton definition it exported.
    try:
        import kimodo.skeleton.definitions as definitions
    except Exception as error:  # pragma: no cover - only when run outside the venv
        raise SystemExit(
            "no joint names in the npz and kimodo is not importable (%s); pass --names <json>"
            % (error,)
        )

    for name in ("SOMASkeleton77", "SOMASkeleton30"):
        cls = getattr(definitions, name)
        instance = cls()
        if len(instance.bone_order_names) == joint_count:
            return list(instance.bone_order_names)
    raise SystemExit("no skeleton with %d joints; pass --names <json>" % (joint_count,))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("target")
    parser.add_argument("--fps", type=float, default=30.0)
    parser.add_argument("--skeleton", default="somaskel77")
    parser.add_argument("--names", default="")
    args = parser.parse_args()

    blob = load(args.source)
    posed = np.asarray(blob["posed_joints"], dtype=float)
    if posed.ndim != 3 or posed.shape[-1] != 3:
        raise SystemExit("posed_joints is not [T, J, 3]: %r" % (posed.shape,))

    if args.names:
        with open(args.names, "r", encoding="utf-8") as handle:
            names = [str(n) for n in json.load(handle)]
    else:
        names = skeleton_names(blob, args.skeleton, posed.shape[1])
    if len(names) != posed.shape[1]:
        raise SystemExit("joint name count %d does not match %d joints" % (len(names), posed.shape[1]))

    flat = [round(float(v), 5) for v in posed.reshape(-1)]
    out = {
        "fps": args.fps,
        "joints": names,
        "frameCount": int(posed.shape[0]),
        "jointCount": int(posed.shape[1]),
        "positions": flat,
        "source": args.source,
        "skeleton": args.skeleton,
    }
    with open(args.target, "w", encoding="utf-8") as handle:
        json.dump(out, handle)
    print("wrote %s frames=%d joints=%d" % (args.target, posed.shape[0], posed.shape[1]))


if __name__ == "__main__":
    main()
