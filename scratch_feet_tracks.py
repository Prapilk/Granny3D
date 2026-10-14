import scratch_feet_sim as S

names = ['Bip01 Pelvis', 'Bip01 L Thigh', 'Bip01 L Calf', 'Bip01 L Foot', 'Bip01 L Toe0',
         'Bip01 R Thigh', 'Bip01 R Calf', 'Bip01 R Foot', 'Bip01 R Toe0']
for b in S.bones:
    if b['name'] not in names:
        continue
    tr = S.tracks.get(b['name'])
    if tr is None:
        print(f"{b['name']:16s} bind=({b['pos'][0]:6.3f},{b['pos'][1]:6.3f},{b['pos'][2]:6.3f})  NO TRACK")
        continue
    pos = tr['pos']
    rot = tr['rot']
    nP = len(tr['timesP']) if tr['timesP'] else 0
    nR = len(tr['timesR']) if tr['timesR'] else 0
    print(f"{b['name']:16s} bind=({b['pos'][0]:6.3f},{b['pos'][1]:6.3f},{b['pos'][2]:6.3f})  nP={nP} nR={nR}")
    for k, p in enumerate(pos):
        print(f"    pos[{k}] = ({p[0]:6.3f},{p[1]:6.3f},{p[2]:6.3f})")
