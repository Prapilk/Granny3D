from scratch_test19 import tracks_by_name, torso_bones
print("Torso Pelvis rest pos:", [b['pos'] for b in torso_bones if b['name'] == 'Bip01 Pelvis'][0])
print("Walk Pelvis anim p0:  ", tracks_by_name.get('Bip01 Pelvis', {}).get('p0'))

print("\nTorso Spine rest pos:", [b['pos'] for b in torso_bones if b['name'] == 'Bip01 Spine'][0])
print("Walk Spine anim p0:  ", tracks_by_name.get('Bip01 Spine', {}).get('p0'))
