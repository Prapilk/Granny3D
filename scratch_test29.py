from scratch_test28 import anim
for t in anim['tracks']:
    if 'pelvis' in t['name'].lower() or 'root' in t['name'].lower() or t['id'] in (0, 1, 2):
        print(f"Track {t['id']:2d} '{t['name']}': p0={t['p0']}, r0={t['r0']}")
