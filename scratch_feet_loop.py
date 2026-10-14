import scratch_feet_sim as S, math

names = ['Bip01 Pelvis', 'Bip01 L Thigh', 'Bip01 L Calf', 'Bip01 L Foot', 'Bip01 L Toe0',
         'Bip01 R Thigh', 'Bip01 R Calf', 'Bip01 R Foot', 'Bip01 R Toe0']

def qdiff(a, b):
    return sum((a[k]-b[k])**2 for k in range(4))

print(f"{'bone':16s} {'nR':>3} {'rot[0]':>22s} {'rot[-1]':>22s} {'qdiff(first,last)':>18s}  {'timesR first/last':>20s}")
for b in S.bones:
    if b['name'] not in names: continue
    tr = S.tracks.get(b['name'])
    if not tr or not tr['rot']: continue
    r0 = tr['rot'][0]; r1 = tr['rot'][-1]
    t0 = tr['timesR'][0]; t1 = tr['timesR'][-1]
    print(f"{b['name']:16s} {len(tr['rot']):3d} ({r0[0]:+.3f},{r0[1]:+.3f},{r0[2]:+.3f},{r0[3]:+.3f}) ({r1[0]:+.3f},{r1[1]:+.3f},{r1[2]:+.3f},{r1[3]:+.3f}) {qdiff(r0,r1):>18.5f}  {t0:7.3f}..{t1:7.3f}")
