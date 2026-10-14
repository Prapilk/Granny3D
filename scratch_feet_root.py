import scratch_feet_sim as S

names = ['__Root', 'master_h_male', 'Bip01', 'Bip01 Pelvis']
for b in S.bones:
    if b['name'] not in names:
        continue
    tr = S.tracks.get(b['name'])
    print(f"--- bone {b['name']} (id={b['id']}, p={b['p']}) bind pos=({b['pos'][0]:.3f},{b['pos'][1]:.3f},{b['pos'][2]:.3f}) ---")
    if tr is None:
        print("    NO TRACK")
        continue
    print(f"    nP={len(tr['timesP']) if tr['timesP'] else 0} nR={len(tr['timesR']) if tr['timesR'] else 0}")
    if tr['timesP']:
        print("    pos keys:", [f"({p[0]:.3f},{p[1]:.3f},{p[2]:.3f})" for p in tr['pos']])
    if tr['timesR']:
        print("    rot keys (first 3):", [f"({r[0]:.3f},{r[1]:.3f},{r[2]:.3f},{r[3]:.3f})" for r in tr['rot'][:3]])
        print("    rot keys (last):", [f"({r[0]:.3f},{r[1]:.3f},{r[2]:.3f},{r[3]:.3f})" for r in tr['rot'][-1:]])

# world position of pelvis + root over time
idx = {b['name']: b['id'] for b in S.bones}
def wpos(i, w): return (w[i][3][0], w[i][3][1], w[i][3][2])
print("\n t   RootXYZ            Bip01XYZ           PelvisXYZ")
steps = 8
for s in range(steps+1):
    t = 0.983 * s / steps
    w = S.pose(t)
    r = wpos(idx['__Root'], w)
    b = wpos(idx['Bip01'], w)
    p = wpos(idx['Bip01 Pelvis'], w)
    print(f"{t:4.2f} ({r[0]:6.3f},{r[1]:6.3f},{r[2]:6.3f})  ({b[0]:6.3f},{b[1]:6.3f},{b[2]:6.3f})  ({p[0]:6.3f},{p[1]:6.3f},{p[2]:6.3f})")
