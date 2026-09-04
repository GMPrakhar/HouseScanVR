#!/usr/bin/env python3
"""Generate a synthetic 3DGS .ply resembling a scanned house interior.

Produces standard INRIA 3D Gaussian Splatting fields so it exercises exactly the
same import path as a real Polycam / Scaniverse export. Coordinates are authored
Y-up in metres (Unity convention) so no post-import rotation is required.

Ground-truth colours per surface let us assert what the renderer must produce.
"""
import argparse
import math
import numpy as np

SH_C0 = 0.2820948

# Ground-truth palette (linear 0..1 RGB). Verification asserts these appear.
PALETTE = {
    "floor":    (0.42, 0.28, 0.16),   # brown wood
    "ceiling":  (0.92, 0.92, 0.90),   # off-white
    "wall":     (0.78, 0.76, 0.70),   # beige
    "wall_e":   (0.24, 0.44, 0.62),   # blue feature wall
    "couch":    (0.65, 0.18, 0.18),   # red couch
    "table":    (0.30, 0.24, 0.20),
    "plant":    (0.16, 0.48, 0.20),   # green
}


def logit(p):
    p = min(max(p, 1e-6), 1.0 - 1e-6)
    return math.log(p / (1.0 - p))


def quat_from_normal(n):
    """Quaternion (w,x,y,z) rotating +Z onto n."""
    n = np.asarray(n, dtype=np.float64)
    n = n / np.linalg.norm(n)
    z = np.array([0.0, 0.0, 1.0])
    d = float(np.dot(z, n))
    if d > 0.999999:
        return (1.0, 0.0, 0.0, 0.0)
    if d < -0.999999:
        return (0.0, 1.0, 0.0, 0.0)
    axis = np.cross(z, n)
    s = math.sqrt((1.0 + d) * 2.0)
    return (s * 0.5, axis[0] / s, axis[1] / s, axis[2] / s)


class SplatBuilder:
    def __init__(self, rng):
        self.rng = rng
        self.pos = []
        self.col = []
        self.scale = []
        self.rot = []
        self.op = []

    def add_plane(self, origin, u, v, normal, count, colour, roughness=0.012,
                  jitter=0.008, holes=None):
        """Scatters splats over a parallelogram.

        ``holes`` is a list of (u0, u1, v0, v1) rectangles in normalised plane
        coordinates; samples landing inside one are dropped. This is how door
        openings are cut, so the rooms form one connected walkable space rather
        than a set of sealed boxes.
        """
        origin = np.asarray(origin, dtype=np.float64)
        u = np.asarray(u, dtype=np.float64)
        v = np.asarray(v, dtype=np.float64)
        normal = np.asarray(normal, dtype=np.float64)
        count = max(int(count), 1)

        area = np.linalg.norm(u) * np.linalg.norm(v)
        # Overlap slightly so the surface reads as continuous, like a real scan.
        splat_radius = math.sqrt(area / count) * 0.9

        a = self.rng.random(count)
        b = self.rng.random(count)

        if holes:
            keep = np.ones(count, dtype=bool)
            for u0, u1, v0, v1 in holes:
                keep &= ~((a >= u0) & (a <= u1) & (b >= v0) & (b <= v1))
            a, b = a[keep], b[keep]
            count = int(keep.sum())
            if count == 0:
                return

        pts = origin + a[:, None] * u + b[:, None] * v
        pts = pts + normal[None, :] * self.rng.normal(0.0, jitter, size=(count, 1))

        q = quat_from_normal(normal)
        base = np.array(colour, dtype=np.float64)
        shade = self.rng.normal(1.0, 0.06, size=(count, 1)).clip(0.75, 1.25)
        cols = (base[None, :] * shade).clip(0.0, 1.0)
        radii = splat_radius * self.rng.normal(1.0, 0.15, size=count).clip(0.5, 1.6)

        for i in range(count):
            self.pos.append(pts[i])
            self.col.append(cols[i])
            self.scale.append((radii[i], radii[i], roughness))
            self.rot.append(q)
            self.op.append(0.97)

    def add_box(self, centre, size, colour, density=9000):
        cx, cy, cz = centre
        sx, sy, sz = size
        faces = [
            ((cx - sx / 2, cy - sy / 2, cz - sz / 2), (sx, 0, 0), (0, sy, 0), (0, 0, -1)),
            ((cx - sx / 2, cy - sy / 2, cz + sz / 2), (sx, 0, 0), (0, sy, 0), (0, 0, 1)),
            ((cx - sx / 2, cy - sy / 2, cz - sz / 2), (0, 0, sz), (0, sy, 0), (-1, 0, 0)),
            ((cx + sx / 2, cy - sy / 2, cz - sz / 2), (0, 0, sz), (0, sy, 0), (1, 0, 0)),
            ((cx - sx / 2, cy + sy / 2, cz - sz / 2), (sx, 0, 0), (0, 0, sz), (0, 1, 0)),
            ((cx - sx / 2, cy - sy / 2, cz - sz / 2), (sx, 0, 0), (0, 0, sz), (0, -1, 0)),
        ]
        for origin, u, v, n in faces:
            area = np.linalg.norm(u) * np.linalg.norm(v)
            self.add_plane(origin, u, v, n, max(int(area * density), 200), colour,
                           roughness=0.010, jitter=0.005)

    def count(self):
        return len(self.pos)


def build_house(rng, density, ceilings=True, doors=True):
    """Two rooms plus a hallway, connected by doorways - whole-house scale.

    Doorways matter: without them each room is a sealed box, the walkable
    analysis finds three disconnected islands, and any walkthrough camera has
    to pass straight through a wall.
    """
    b = SplatBuilder(rng)
    H = 2.6
    # Doorways run from the floor to 80% of wall height.
    DOOR_V = (0.0, 0.80)

    rooms = [
        ("living", -4.0, -3.0, 7.0, 6.0),
        ("bedroom", 3.0, -3.0, 4.0, 4.0),
        ("hall", -1.0, 3.0, 3.0, 3.5),
    ]

    # Door openings, in normalised coordinates of the wall they are cut into.
    # living<->bedroom at x=3 spanning z=-1.5..-0.5; living<->hall at z=3
    # spanning x=0..1.
    holes = {} if not doors else {
        ("living", "east"):  [(0.250, 0.4167) + DOOR_V],
        ("bedroom", "west"): [(0.375, 0.625) + DOOR_V],
        ("living", "north"): [(0.5714, 0.7143) + DOOR_V],
        ("hall", "south"):   [(0.3333, 0.6667) + DOOR_V],
    }

    for name, x0, z0, w, d in rooms:
        b.add_plane((x0, 0.0, z0), (w, 0, 0), (0, 0, d), (0, 1, 0),
                    w * d * density, PALETTE["floor"])
        if ceilings:
            b.add_plane((x0, H, z0), (w, 0, 0), (0, 0, d), (0, -1, 0),
                        w * d * density * 0.6, PALETTE["ceiling"])
        b.add_plane((x0, 0, z0), (w, 0, 0), (0, H, 0), (0, 0, -1),
                    w * H * density, PALETTE["wall"],
                    holes=holes.get((name, "south")))
        b.add_plane((x0, 0, z0 + d), (w, 0, 0), (0, H, 0), (0, 0, 1),
                    w * H * density, PALETTE["wall"],
                    holes=holes.get((name, "north")))
        b.add_plane((x0, 0, z0), (0, 0, d), (0, H, 0), (-1, 0, 0),
                    d * H * density, PALETTE["wall"],
                    holes=holes.get((name, "west")))
        east_colour = PALETTE["wall_e"] if name == "living" else PALETTE["wall"]
        b.add_plane((x0 + w, 0, z0), (0, 0, d), (0, H, 0), (1, 0, 0),
                    d * H * density, east_colour,
                    holes=holes.get((name, "east")))

    b.add_box((-2.0, 0.42, 0.0), (2.0, 0.85, 0.9), PALETTE["couch"], density * 1.4)
    b.add_box((0.6, 0.36, 0.2), (1.1, 0.72, 0.7), PALETTE["table"], density * 1.4)
    b.add_box((-3.4, 0.55, 2.0), (0.5, 1.1, 0.5), PALETTE["plant"], density * 1.4)
    # Kept clear of the x=3 doorway: at x 3.4 this bed sat directly against the
    # opening and eroded the door to a single navigable cell, which made the
    # bedroom unreachable.
    b.add_box((5.8, 0.30, -1.6), (1.6, 0.6, 1.6), PALETTE["couch"], density * 1.4)
    return b


def write_ply(path, b):
    n = b.count()
    pos = np.asarray(b.pos, dtype=np.float32)
    col = np.asarray(b.col, dtype=np.float64)
    scale = np.asarray(b.scale, dtype=np.float64)
    rot = np.asarray(b.rot, dtype=np.float32)
    op = np.asarray(b.op, dtype=np.float64)

    f_dc = ((col - 0.5) / SH_C0).astype(np.float32)
    log_scale = np.log(np.clip(scale, 1e-8, None)).astype(np.float32)
    op_logit = np.array([logit(o) for o in op], dtype=np.float32)

    data = np.zeros((n, 62), dtype=np.float32)
    data[:, 0:3] = pos
    data[:, 6:9] = f_dc
    data[:, 54] = op_logit
    data[:, 55:58] = log_scale
    data[:, 58:62] = rot

    props = ["x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2"]
    props += [f"f_rest_{i}" for i in range(45)]
    props += ["opacity", "scale_0", "scale_1", "scale_2",
              "rot_0", "rot_1", "rot_2", "rot_3"]

    header = "ply\nformat binary_little_endian 1.0\n"
    header += f"element vertex {n}\n"
    header += "".join(f"property float {p}\n" for p in props)
    header += "end_header\n"

    with open(path, "wb") as fh:
        fh.write(header.encode("ascii"))
        fh.write(data.tobytes())
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--density", type=float, default=2500.0,
                    help="splats per square metre of surface")
    ap.add_argument("--seed", type=int, default=1234)
    ap.add_argument("--no-ceiling", action="store_true",
                    help="omit ceilings, for a cutaway view of the floor plan")
    ap.add_argument("--no-doors", action="store_true",
                    help="seal every room, as a negative control for pathfinding")
    args = ap.parse_args()

    rng = np.random.default_rng(args.seed)
    b = build_house(rng, args.density, ceilings=not args.no_ceiling,
                    doors=not args.no_doors)
    n = write_ply(args.out, b)
    p = np.asarray(b.pos)
    print(f"wrote {args.out}: {n} splats")
    print(f"bounds min={np.min(p, axis=0).round(2).tolist()} "
          f"max={np.max(p, axis=0).round(2).tolist()}")


if __name__ == "__main__":
    main()
