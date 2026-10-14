import math
import scratch_feet_sim as S

# compare mesh bind rotation vs anim first-frame rotation for leg bones
names = ['Bip01 L Thigh','Bip01 L Calf','Bip01 L Foot','Bip01 L Toe0',
         'Bip01 R Thigh','Bip01 R Calf','Bip01 R Foot','Bip01 R Toe0',
         'Bip01','Bip01 Pelvis','Bip01 Spine']

def qang(q):
    w = max(-1.0, min(1.0, q[3]))
    return 2*math.acos(abs(w))*180/math.pi

print(f"{'bone':16s} | mesh bind rot (angle) | anim rot[0] (angle) | angle diff")
for b in S.bones:
    if b['name'] not in names: continue
    tr = S.tracks.get(b['name'])
    if tr is None:
        print(f"{b['name']:16s} | bind {qang(b['rot']):6.1f} deg | NO TRACK")
        continue
    ar = tr['rot'][0] if tr['rot'] else (0,0,0,1)
    ba = qang(b['rot']); aa = qang(ar)
    print(f"{b['name']:16s} | bind {ba:6.1f} deg | anim0 {aa:6.1f} deg | {aa-ba:6.1f} deg")

# Also: is the anim first frame close to the mesh bind for POSITION too?
print("\nPosition check (bind vs anim pos[0]):")
for b in S.bones:
    if b['name'] not in names: continue
    tr = S.tracks.get(b['name'])
    if tr is None: continue
    p0 = tr['pos'][0] if tr['pos'] else (0,0,0)
    d = math.sqrt(sum((p0[k]-b['pos'][k])**2 for k in range(3)))
    print(f"{b['name']:16s} | bind=({b['pos'][0]:+.3f},{b['pos'][1]:+.3f},{b['pos'][2]:+.3f}) anim0=({p0[0]:+.3f},{p0[1]:+.3f},{p0[2]:+.3f}) dist={d:.4f}")
