#!/usr/bin/env python3
"""Generate HyperdriveHeavy.craft: a 100 % stock, ~310-part test vessel for AutoBench.

Two Rockomax Jumbo-64 tanks stacked on their real stack nodes, each wrapped in rings of
surface-attached Oscar-B tanks touching the hull. No engine: the vessel stands on the launch
pad, which gives a stable, repeatable CPU load (physics, joints, FlightIntegrator per part).

usage: make_heavy_craft.py <KSP root> [rings per tank] [tanks per ring]
writes <KSP root>/Ships/VAB/HyperdriveHeavy.craft
"""
import math
import os
import random
import sys

JUMBO = "Rockomax64.BW"      # Rockomax64_BW: radius 1.25 m, stack nodes at +/-3.73 m
OSCAR = "miniFuelTank"       # Oscar-B: node_attach at 0.3175 m, height 0.349 m
JUMBO_RADIUS = 1.25
JUMBO_HALF = 3.73
OSCAR_ATTACH = 0.3175


def part(name, pid, persistent, pos, att_pos0, rot, attm, links=(), att_nodes=(), srf=None):
    lines = [
        "PART", "{",
        f"\tpart = {name}_{pid}",
        "\tpartName = Part",
        f"\tpersistentId = {persistent}",
        "\tpos = {:.5f},{:.5f},{:.5f}".format(*pos),
        "\tattPos = 0,0,0",
        "\tattPos0 = {:.5f},{:.5f},{:.5f}".format(*att_pos0),
        "\trot = {:.6f},{:.6f},{:.6f},{:.6f}".format(*rot),
        "\tattRot = 0,0,0,1",
        "\tattRot0 = {:.6f},{:.6f},{:.6f},{:.6f}".format(*rot),
        "\tmir = 1,1,1",
        "\tsymMethod = Radial",
        "\tautostrutMode = Off",
        "\trigidAttachment = False",
        "\tistg = -1", "\tresPri = 0", "\tdstg = 0", "\tsidx = -1", "\tsqor = -1", "\tsepI = -1",
        f"\tattm = {attm}",
        "\tmodCost = 0", "\tmodMass = 0", "\tmodSize = 0,0,0",
    ]
    lines += [f"\tlink = {link}" for link in links]
    lines += [f"\tattN = {node}" for node in att_nodes]
    if srf:
        lines.append(f"\tsrfN = srfAttach,{srf}")
    lines += ["\tEVENTS", "\t{", "\t}", "\tACTIONS", "\t{", "\t}", "\tPARTDATA", "\t{", "\t}", "}"]
    return lines


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    root = sys.argv[1]
    rings = int(sys.argv[2]) if len(sys.argv) > 2 else 11
    per_ring = int(sys.argv[3]) if len(sys.argv) > 3 else 14

    rng = random.Random(1225)
    next_id = [4290000000]

    def new_id():
        next_id[0] += 1
        return next_id[0]

    def persistent():
        return rng.randrange(1, 2 ** 31)

    radius = JUMBO_RADIUS + OSCAR_ATTACH
    spacing = 2 * (JUMBO_HALF - 0.3) / max(1, rings - 1)
    base_y = 10.0

    lower, upper = new_id(), new_id()
    tanks = {lower: [], upper: []}
    body = []
    for jumbo, centre in ((lower, base_y), (upper, base_y + 2 * JUMBO_HALF)):
        for r in range(rings):
            dy = -(JUMBO_HALF - 0.3) + r * spacing
            for k in range(per_ring):
                theta = 2 * math.pi * (k + 0.5 * (r % 2)) / per_ring
                x, z = radius * math.cos(theta), radius * math.sin(theta)
                phi = math.pi - theta  # turn the attach node (+X) towards the hull
                rot = (0.0, math.sin(phi / 2), 0.0, math.cos(phi / 2))
                cid = new_id()
                tanks[jumbo].append(f"{OSCAR}_{cid}")
                body += part(OSCAR, cid, persistent(), (x, centre + dy, z), (x, dy, z), rot, 1, srf=f"{JUMBO}_{jumbo}")

    ident = (0.0, 0.0, 0.0, 1.0)
    head = part(JUMBO, lower, persistent(), (0, base_y, 0), (0, base_y, 0), ident, 0,
                links=[f"{JUMBO}_{upper}"] + tanks[lower],
                att_nodes=[f"top,{JUMBO}_{upper}_0|{JUMBO_HALF}|0"])
    head += part(JUMBO, upper, persistent(), (0, base_y + 2 * JUMBO_HALF, 0), (0, 2 * JUMBO_HALF, 0), ident, 0,
                 links=tanks[upper],
                 att_nodes=[f"bottom,{JUMBO}_{lower}_0|{-JUMBO_HALF}|0"])

    count = 2 + len(tanks[lower]) + len(tanks[upper])
    header = [
        "ship = HyperdriveHeavy",
        "version = 1.12.5",
        f"description = Hyperdrive benchmark vessel: {count} stock parts, no engine, stands on the launch pad.",
        "type = VAB",
        "size = 5,16,5",
        "steamPublishedFileId = 0",
        f"persistentId = {persistent()}",
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Ship",
    ]
    out = os.path.join(root, "Ships", "VAB", "HyperdriveHeavy.craft")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(header + head + body) + "\n")
    print(f"wrote {out}: {count} parts ({rings} rings x {per_ring} Oscar-B per Jumbo-64)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
