# Granny3D — Correctif du mapping des os de skin (SKINMAP-FIX-1)

Date : 2026-10-13
Statut : **résolu, validé, déployé**

## Symptôme

En lecture d'une animation (`DIRECT`), le mesh de l'Orc se **déforme** :
les mains/doigts, la tête, les pieds sont tirés vers des os anatomiquement
distants. Des sommets attachés au pied suivent le doigt, la tête suit la
cuisse, etc. `maxEdgeStretch` montait jusqu'à **12–23×** avec **~250–283
arêtes > 1,5×** sur toute la foulée.

Le **mesh statique** (au repos) paraissait correct.

## Cause racine

Les poids de skin ne référencent **pas** directement l'ordre du squelette.
Chaque mesh possède sa propre liste « bone tie » (chunk **`0xCA5E0C0A`**) dont
le premier champ `int` de chaque enregistrement est l'**index d'os squelette**.
L'index local d'influence d'un sommet pointe dans **cette liste de ties**.

L'ancien code passait cet index de tie par une indirection erronée :

```csharp
// ANCIEN (faux)
int idx = b00.IndexOf(val);        // scrambles
if (idx >= 0 && idx < Bones.Count) boneMap[i] = idx;
```

`b00` (liste d'index d'objets) réordonnait les os → **poids attribués aux
mauvais os**. Le mesh statique semblait bon car au bind `skin ≈ I` quel que
soit l'os : le bug n'apparaissait qu'en animation.

## Correctif

Utiliser **directement** l'index d'os du tie :

```csharp
// NOUVEAU (correct)
var skinBoneMap = new List<int>();
for (int i = 0; i < c0aChunks.Count; i++)
{
    reader.BaseStream.Seek(c0aChunks[i].DataOffset, SeekOrigin.Begin);
    skinBoneMap.Add(reader.ReadInt32());
}
...
int masterB = (localB >= 0 && localB < skinBoneMap.Count) ? skinBoneMap[localB] : localB;
```

### Détail de `GrannyFormatParser.ExtractData`

- Étape 1 (~l.388) : `skinChunks` = `0xCA5E0702`, `c0aChunks` = `0xCA5E0C0A`.
- Étape 2 : construire `skinBoneMap` = premier `int` de chaque chunk `c0a`.
- Étape 3 (lecture skin) : `masterB = skinBoneMap[localB]`.

Valeurs observées sur `Orcs_Orc_Lod2.grn` :
```
c0aVals = [4,10,9,8,11,6,7,5,3,12,13,14,16,15,18,17,20,19,21,22,23,25,24,27,26,28,29,30,31,32,33]
```
Soit (index sémantique correct vérifié par géométrie) : 0→`Spine`,
1→`L UpperArm`, 2→`L Clavicle`, 4→`Head`, 5→`Spine2`, 6→`Spine3`, 9→`L Thigh`…

## Preuve (harnais)

Avant / après, sur Orc LOD2 + Walk (`DIRECT edge-stretch check`) :

| | avant | après |
|---|---|---|
| arêtes > 1,5× | 250–283 | **4–10** |
| étirement max | 12–23× | **2,5–8×** |
| poids pieds/doigts | incohérents | **anatomiquement corrects** |

Influences après correctif (sommets « coupables ») :
- v52 (tête/main) → `L Hand:0.5 + L Finger1:0.5`
- v127 (pied) → `R Calf:0.5 + R Foot:0.5`
- v161 (jambe) → `L Calf:1.0`
- v169, v172 (pied gauche) → `L Toe0:1.0`

Il ne reste qu'**1 arête** mineure (hanche v18↔v20, 2,4→6,5 cm) = artefact LBS négligeable.

## Validation multi-modèles (généralisation)

| Modèle | max stretch | arêtes > 1,5× |
|---|---|---|
| Orc (bipède) | 2,5–8× | 4–10 |
| Cow | 1,4–3,5× | 0–10 |
| Bull | 1,4–2,8× | 0–14 |
| Eagle | 1,4–1,6× | 0–2 |

Aucun étirement catastrophique ; le résidu est du LBS normal aux articulations.

## Déploiement

- Build : csproj **live**
  `…\UOFiddler-master\UoFiddler.Plugin.Granny3D\UoFiddler.Plugin.Granny3D.csproj`
  (`AssemblyName=Granny3DPlugin`, `OutputPath=..\UoFiddler\bin\Debug\plugins\`).
- Sortie : `…\UOFiddler-master\UoFiddler\bin\Debug\plugins\Granny3DPlugin.dll`
  (74 240 o, 0 erreur).
- Marqueur de build dans `Granny3DControl` : **`SKINMAP-FIX-1`**.

## Fichiers modifiés

- `UoFiddler.Plugin.Granny3D\GrannyFormatParser.cs` — mapping skin (correctif).
- `UoFiddler.Plugin.Granny3D\Granny3DControl.cs` — marqueur de build.

Copies : source de vérité `C:\Games\Sphere\IrisGrannyViewer\…` ↔ live
`C:\Games\Sphere\Programmes\TestGemini\Axis2_WPF\UOFiddler-master\…`
(synchronisées par `Copy-Item -Force`).

## À retenir

> Le skin d'un mesh Granny UO indexe sa **propre liste de bone-ties
> (`0xCA5E0C0A`)**, pas l'ordre du squelette global. Toujours résoudre
> `masterBone = c0aTie[localIndex]` **sans** repasser par une permutation
> d'objets (`b00.IndexOf`).

---

# Ajout : stabilisation de l'arbre + superposition d'équipements (TREE-EQUIP-1)

Date : 2026-10-13 — Fichier : `UoFiddler.Plugin.Granny3D\Granny3DControl.cs`

## 1. Stabilisation expand/collapse

- Flag `Populated` dans `TreeNodeInfo` → chaque nœud n'est peuplé **qu'une
  seule fois** (expand/collapse idempotent, plus de double-population).
- Suppression du **saut de sélection** : cliquer une catégorie ne sélectionne
  plus automatiquement le premier enfant (c'était la cause principale de
  ré-entrance / instabilité).
- Garde de ré-entrance `_populatingTree` + `BeginUpdate`/`EndUpdate` autour des
  populations (plus de scintillement).
- Sélection d'une créature via `EnsureMeshLoaded` (pas de rechargement si déjà
  courant).
- Suppression de `NodesArePlaceholders` (devenu inutile).

## 2. Personnage + plusieurs équipements

Menu contextuel (clic droit) dans l'arbre :
- **Créature** → « Charger (remplacer la scène) » / « **Ajouter à la scène
  (équipement)** ».
- **Animation** → « Lire l'animation » / « Charger le modèle de la créature ».
- Toujours : « Retirer le dernier mesh » / « Vider la scène ».

Détection équipement = **catégorie** (« Others » = Equipment) et non le préfixe
`E_` : couvre donc `E_*`, `Weapon_*`, `Shield_*`, `Staff_*`, `Wand_*`,
`Light_*`, `Spell_*`, et les peaux d'armure `H_*` du dossier. Les vrais PNJ
`Character_*` remplacent toujours la scène.

### Transparence (alpha) — build TREE-EQUIP-2
Le fragment shader échantillonne maintenant l'**alpha** de la texture et
`discard` les texels quasi transparents (`uAlphaCut = 0.15`), avec blending
activé (`SrcAlpha / OneMinusSrcAlpha`). Corrige les ailes de dragon, cheveux,
feuillages, capes, etc.

`AddToScene(path)` empile tous les meshes d'un `.grn` **sans** toucher à la
caméra ni aux bounds de base (donc aligné sur le corps) et **rebinde par nom
d'os** à l'animation courante.

Preuve d'alignement : `H_Male_Lod2.grn` et `E_Male_Plate01_Chest_V2_LOD2.grn`
partagent **32 noms d'os** (`Bip01 Spine`, `Bip01 L Hand`, `Bip01 Head`…), donc
l'équipement suit l'animation du corps.

### Utilisation
1. Charger `Humans\H_Male_Lod2.grn`, choisir une anim (lecture auto).
2. **Simple clic** sur un équipement (`E_*` du dossier Equipment) : il est
   **ajouté** au personnage (au lieu de remplacer la scène) tant qu'un modèle
   est déjà chargé. Ou clic droit → « Ajouter à la scène (équipement) ».
3. Répéter pour plusieurs pièces (garde anti-doublon). Double-clic dans
   « Meshes affiches » = retirer (et permet de re-ajouter).

## Reste à faire

- (rien de bloquant)

## TREE-EQUIP-2 — arbre epure, panneau Animations, attache a un os

Date : 2026-10-13 (build TREE-EQUIP-2, DLL 82944 o).

- **Arbre epure** : les animations ne sont plus injectees comme enfants de
  l'arbre. L'arbre = Categories -> Modeles LOD2 uniquement.
- **Panneau Animations dedie** : en-tete (creature courante + nombre d'anim),
  champ de filtre, liste qui se met a jour pour le LOD selectionne et permet de
  choisir n'importe quelle animation de cette creature.
- **Attache a un os (rigid attach)** : dans "Meshes affiches", selectionner une
  arme/armure, choisir un os (ex. Bip01 R Hand) puis "Attacher / Detacher".
  Le mesh est desormais pilote par la matrice de skin de cet os (squelette
  anime du corps) : deform = TransformPosition(v, host.SkinMatrices[bone]).
  Il suit donc l'animation au lieu de rester en pose de bind. Re-cliquer le
  meme os = detacher. L'attache est marquee " -> os" dans la liste.

### TREE-EQUIP-3 — conserver le personnage + controle de placement (DLL 91136 o)

Date : 2026-10-13.

- **Animer sans perdre le personnage** : choisir une animation ne recharge plus
  le mesh de base quand la scene contient deja quelque chose
  (`if (_loadedMeshes.Count == 0) LoadModel(baseMesh)`). On garde donc le PNJ
  (`Character_*`) et ses equipements attaques ; l'animation s'applique dessus.
- **Controleur d'equipement** (a droite de "Playback Controls") : miroir X/Y/Z,
  rotation en degres X/Y/Z (boutons rapides +90 / -90 + pas fin) et decalage
  X/Y/Z, plus "Reinitialiser le placement".
  - Tweak applique **dans l'espace de l'objet** avant la matrice de l'os :
    `AttachMatrix = (Scale(mirror) * Rotation(euler) * Translation(offset)) *
    WorldMatrices[bone]` (meshes rigides) ; les meshes skinnes gardent
    `SkinMatrices[bone]` (espace corps).
  - Exemple : la crossbow apparait inversee -> cocher **Miroir** (bon axe) puis
    ajuster la rotation ; fine-tune via les NumericUpDown.
  - Cull-face deja desactive -> un miroir (scale negatif) ne fait pas
    disparaitre la geometrie.

### TREE-EQUIP-4 — os en menu deroulant (DLL 91136 o)

Date : 2026-10-13.

- Le champ "Attacher a l'os" (`cbBones`) passe de `DropDown` (zone de saisie
  editable) a **`DropDownList`** : c'est un vrai menu deroulant listant tous les
  os du squelette, plus de saisie libre.
- `DropDownWidth = 320`, `MaxDropDownItems = 20` (les noms d'os longs tiennent).
- `PopulateBoneCombo()` choisit desormais le mesh au **plus grand nombre d'os**
  (le vrai squelette humain, pas l'equipement a 2 os) avant de remplir la liste.
- Nouveau helper `SelectBoneByName(name)` (comparaison insensible a la casse) :
  utilisee pour (a) restaurer l'os precedent apres repopulation, (b) definir le
  defaut (`Bip01 R Hand` > `Bip01 L Hand` > `Bip01 Head` > `Bip01 Spine`),
  (c) resynchroniser la combo quand on clique un mesh attache dans
  "Meshes affiches" (`LstLoaded_SelectedIndexChanged`).
- `AttachSelectedMeshToBone()` lit toujours `cbBones.Text` (= texte de l'item
  selectionne) : inchange.

---

# Fiche technique — systeme d'attache / placement d'equipement

Reference complete pour reprendre le travail plus tard.

## 1. Structures de donnees

### `LoadedMesh` (classe, `Granny3DControl.cs`)
Un mesh affiche (corps, armure, arme...). Champs d'attache :
- `AttachBoneName : string` — nom de l'os cible (`null` = non attache).
- `AttachMatrix : Matrix4` — matrice finale calculee chaque `ApplyAnimationPose`.
- `HasAttachMatrix : bool` — true quand `AttachMatrix` est valide a la frame.
- `MirrorX / MirrorY / MirrorZ : bool` — miroirs par axe (scale ±1).
- `AttachRotation : Vector3` — rotation en **degres** (X, Y, Z).
- `AttachOffset : Vector3` — translation en unites monde.
- Autres : `Bones`, `Skin`, `BaseVertices`, `Indices`, `CornerVertex`,
  `Interleaved`, `Vbo`, `Vao`, `TextureId`, `SourcePath`, `SourceMesh`.

### Matrices du host (modele porteur, ex. H_Male / Character_*)
- `WorldMatrices[bone]` : matrice **monde de l'os** -> espace **local** (rigide).
- `SkinMatrices[bone]` = `InvBind * World` -> espace **bind du corps** (skinne).
Ces tableaux sont recalcules a chaque `ApplyAnimationPose(_currentTime)`.

## 2. Regle d'espace (essentielle)

| Type de mesh | Exemples | `Skin` | Espace des sommets | Matrice d'attache |
|---|---|---|---|---|
| **Rigide / local** | `Weapon_*`, `Shield_*`, `Staff_*`, `Wand_*` (2 os, racine 0,0,0) | vide | local (autour de l'origine) | `WorldMatrices[bone]` (+ tweak) |
| **Skinne / corps** | armures `E_*`, cheveux `H_*` (34 os) | non vide | bind du corps (torse z~0.97-1.50) | `SkinMatrices[bone]` |

Test : `bool inBodySpace = lm.Skin != null && lm.Skin.Length > 0;`

## 3. Calcul de la matrice d'attache

Passe d'attache dans `ApplyAnimationPose` (pour chaque `LoadedMesh` attache) :

```
bool inBodySpace = lm.Skin != null && lm.Skin.Length > 0;
if (inBodySpace && host.SkinMatrices != null && bi < host.SkinMatrices.Length)
    lm.AttachMatrix = host.SkinMatrices[bi];
else if (host.WorldMatrices != null && bi < host.WorldMatrices.Length)
    lm.AttachMatrix = BuildAttachTweak(lm) * host.WorldMatrices[bi];
else
    continue;
lm.HasAttachMatrix = true;
```

`BuildAttachTweak(lm)` (convention **vecteur-ligne**, `v * M`) :

```
Matrix4 scale = Matrix4.CreateScale(mirrorX?-1:1, mirrorY?-1:1, mirrorZ?-1:1);
Matrix4 rot   = Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                    Radians(AttachRotation.X), Radians(AttachRotation.Y), Radians(AttachRotation.Z)));
Matrix4 tr    = Matrix4.CreateTranslation(AttachOffset);
return scale * rot * tr;                 // ordre : miroir -> rotation -> translation
```

Donc `AttachMatrix = Tweak * BoneMatrix` : le tweak agit **dans l'espace de
l'objet**, avant la pose de l'os. Dans `RefreshVertexBuffers`, la branche
`rigid` fait `pos = TransformPosition(v, AttachMatrix)`.

> Le **miroir** utilise un scale negatif : surete grace a
> `GL.Disable(EnableCap.CullFace)` dans `RenderScene` (pas de disparition).

## 4. UI (panneau droit, `InitializeComponent`)

Structure : `pnlRight` = `glControl` (Fill) + `pnlRightBottom` (Bottom, 350 px).
`pnlRightBottom` = `grpInfo` "Model Info" (Fill) + `pnlPlayRow` (Top, 240 px).
`pnlPlayRow` = `grpPlayback` "Playback Controls" (Left, 250 px) + `grpEquip` (Fill).

### `grpEquip` "Equipement (attache) — placement"
- `lblEquipTarget` : "Cible : <mesh>  -> <os>" (ou "(aucun)" / "(non attache)").
- `cbMirrorX / cbMirrorY / cbMirrorZ` : cases miroir par axe.
- `nudRotX / nudRotY / nudRotZ` : NumericUpDown -360..360, pas 5, 1 decimale.
  Boutons rapides `+90` / `-90` par axe (`MakeRotBtn`, bornes clampées).
- `nudOffX / nudOffY / nudOffZ` : NumericUpDown -1..1, pas 0.01, 3 decimales.
- `btnEquipReset` : "Reinitialiser le placement".

### Panneau "Meshes affiches" (`grpLoaded`)
- `lstLoaded` : liste des meshes ; suffixe "  ->  <os>" si attache.
- `pnlAttach` : `lblBone` + `cbBones` (**DropDownList**) + `btnAttach`.

## 5. Methodes cles (par nom)

| Methode | Role |
|---|---|
| `PopulateBoneCombo()` | Remplit `cbBones` depuis le mesh au plus grand nombre d'os |
| `SelectBoneByName(name)` | Selectionne une entree de `cbBones` (insensible a la casse) |
| `AttachSelectedMeshToBone()` | Attache/detache le mesh selectionne a `cbBones.Text` |
| `LstLoaded_SelectedIndexChanged` | Sync combo + `LoadEquipControlsFromMesh()` |
| `ApplyEquipControls()` | Pousse les widgets sur le mesh + `ApplyAnimationPose` + Invalidate |
| `ResetEquipControls()` | Remet miroir/rotation/decalage a zero |
| `LoadEquipControlsFromMesh()` | Reflete le tweak stocke du mesh dans les widgets |
| `SetNud(nud, v)` | Assigne une valeur clampee a un NumericUpDown |
| `BuildAttachTweak(lm)` | Miroir * Rotation * Translation (espace objet) |
| `ApplyAnimationPose(t)` | Recalcule os + matrices d'attache de tous les meshes |
| `PlayAnimationFile(path)` | Charge une anim ; **ne recharge le mesh de base que si scene vide** |
| `AddToScene(path)` | Ajoute un `.grn` (equipement) sans toucher camera/bounds |
| `RefreshLoadedList()` | Reconstruit `lstLoaded` (+ marque " -> os") |
| Garde `_suppressEquip` | Empeche les handlers de reentrer pendant le remplissage |

## 6. Comportements voulus (regressions a eviter)

1. **Choisir une anim ne doit PAS vider la scene** : on garde PNJ + equipements,
   l'anim s'applique dessus. (Le mesh de base n'est charge que si scene vide.)
2. **Simple clic** sur un equipement (`Others`) = **ajout** au personnage, sauf
   `Character_*` = remplace la scene.
3. **Double-clic** dans "Meshes affiches" = retirer le mesh.
4. Re-cliquer le meme os sur un mesh = **detacher**.
5. Un miroir ne doit pas faire disparaitre la geometrie (cull desactive).

## 7. Conventions d'assets (`Models\`)

- Dossiers = categories : `Humans, Monsters, Animals, SeaCreatures, Others` ;
  `Others` = **Equipment** a l'affichage.
- Meshes = fichiers contenant **"lod"** (LOD2). Les `.grn` **sans** "lod" =
  animations (exceptions rares dans `Others`).
- `Others` : `E_*` (armures), `Weapon_*`, `Shield_*`, `Staff_*`, `Wand_*`,
  `Light_*`, `Spell_*`, `H_*` (peaux), `Character_*` (PNJ complets).
- Animations Humans : `H_Male_*` / `H_Female_*` (Walk/Run/Idle/Attack/Fidget...).

## 8. Build & deploiement

- **Cible live** : `...\UOFiddler-master\UoFiddler.Plugin.Granny3D\UoFiddler.Plugin.Granny3D.csproj`
  (`AssemblyName=Granny3DPlugin`, sortie `..\UoFiddler\bin\Debug\plugins\`).
- `msbuild <csproj> -t:Compile` compile dans `obj` (OK meme si UOFiddler ouvert).
- `msbuild <csproj> -t:Build` copie le DLL ; **exige UOFiddler ferme** sinon
  `MSB3027`/`MSB3021` (fichier verrouille). Redemarrer UOFiddler apres.
- **Source de verite** : `C:\Games\Sphere\IrisGrannyViewer\UoFiddler.Plugin.Granny3D\UoFiddler.Plugin.Granny3D\`.
  Resync apres chaque modif : `Copy-Item -Force` live -> truth.
- Log de session : `%TEMP%\Granny3D_plugin.log`.

## 9. Etat des DLL deployee

| Build | Taille | Note |
|---|---|---|
| TREE-EQUIP-2 alpha | ~78336 o | alpha/transparence (uAlphaCut 0.15) |
| TREE-EQUIP-2 | 82944 o | arbre epure + panneau Animations + attache os |
| TREE-EQUIP-3 | 91136 o | conserver personnage + controleur placement |
| TREE-EQUIP-4 | 91136 o | os en DropDownList |
| DAWN-AUTOATTACH | 92672 o | auto-attache des accessoires non-skinés (épée Dawn_Girl) |
| CHAR-MULTIPART-1 | 99840 o | personnage multipartie H_Male/H_Female (corps auto + menu Visage/Coiffure/Barbe/Torse) |
| HOST-SKELETON | 100352 o | squelette placeholder gardé comme host (CP_Grasp_*) ; attache robuste ; refus d'attacher un mesh skinné ; auto-attache préfère les sockets nommées |

## 10. Pistes suivantes

- Corrections d'animations **Animals / Monsters**.
- Si besoin : presets de placement par type d'arme (crossbow inversee, etc.).
- CHAR-MULTIPART : ajouter d'autres slots au menu (chapeaux, bijoux, bottes).

---

# Ajout : personnage multipartie H_Male / H_Female (CHAR-MULTIPART-1)

## Probleme

`H_Male_Lod2.grn` et `H_Female_Lod2.grn` ne contiennent **pas** le corps : seulement
le **squelette 43 os** + **un mesh-boite placeholder de 19 sommets** (21 indices,
texture `Hue_Chip`). Le vrai corps est un **assemblage de pieces** stockees dans
`Models\Others`, chacune etant un petit mesh **skinne** en espace bind commun.

## Decouverte (grannydump)

Chaque piece est un fichier Granny **autonome** (squelette partiel + mesh skinne) :
- `H_Male_Torso_V2_LOD2.grn` : 25 os, 117 verts (skin=117), tex `UT256_H_Male_V2G`.
- `H_Male_Torso_Upper_V2_LOD2.grn` : 13 os, 46 verts, tex `UT256_H_Male_V2h`.
- `H_Male_Head_V2_LOD2.grn` : 19 os, 49 verts, tex `UT256_H_Male_V2G`.
- `H_Male_Face_01_V2_LOD2.grn` : 10 os, 78 verts, tex `UT256_H_Male_Faces_V2`.
- `H_Male_Hands_V2_LOD2.grn` : 27 os, 96 verts.
- `E_Male_Hair01_V2_LOD2.grn` : 34 os, 51 verts, tex `UT256_Elven_Hair01_V2`.

Les **noms d'os** (`Bip01 Head`, `Bip01 R Hand`, ...) sont communs a toutes les
pieces -> l'animation (tracks nommees) se rejoue sur chaque piece independamment
via `BindMeshToAnimation` (appariement par nom). L'index d'os **differe** d'un
fichier a l'autre (ex. `Bip01 Head` = bone[12] dans Torso, bone[8] dans Face) :
tout passe donc par le **nom**, jamais par l'index.

## Assemblage automatique du corps

Quand une animation `H_Male_*` / `H_Female_*` est jouee (ou le modele clique),
`NaturalBaseMesh` renvoie `H_Male_Lod2` / `H_Female_Lod2`. Dans `LoadModelCore` :
- detection via `IsHumanPlaceholder(path, out female)` ;
- les meshes du fichier placeholder sont **ignores** (le mesh-boite n'est pas dessine) ;
- `AssembleHumanBody(female)` charge une piece par slot :

| slot | fichier (`H_Male_` / `H_Female_`) |
|---|---|
| torso | `Torso_V2_LOD2` |
| torsoupper | `Torso_Upper_V2_LOD2` |
| neck | `Neck_V2_LOD2` |
| head | `Head_V2_LOD2` |
| ears | `Ears_V2_LOD2` |
| uarms | `UArms_V2_LOD2` |
| farms | `FArms_V2_LOD2` |
| hands | `Hands_V2_LOD2` |
| ulegs | `ULegs_V2_LOD2` |
| llegs | `LLegs_V2_LOD2` |
| feet | `Feet_V2_LOD2` |
| pelvis | `Pelvis_V2_LOD2` |
| face | `Face_01_V2_LOD2` |

## Menu "Personnage (multipartie)"

`grpChar` (Dock Right, largeur 250) place **a droite de** `grpEquip` dans
`pnlPlayRow` (`grpEquip` Fill, puis `grpChar` Right, puis `grpPlayback` Left).
4 menus deroulants `DropDownList` remplis par `PopulateCharCombos()` depuis
`Others` selon le sexe (`_charGender` : 0=aucun, 1=homme, 2=femme) :

| Controle | slot | filtre |
|---|---|---|
| `cbCharFace` | `face` | `H_*_Face_*` (exclut `facial`) |
| `cbCharHair` | `hair` | `E_*_Hair*` ou `H_*_Hair_*` (exclut `facial`) |
| `cbCharBeard` | `beard` | `H_Male_Hair_Facial_*` (homme seulement) |
| `cbCharTorso` | `torso` | `H_*_Torso_*` (exclut `_Torso_Upper`) |

Changer un combo appelle `SetCharacterPart(slot, path)` : detruit tous les meshes
du slot, charge la nouvelle piece (`LoadPartMeshes`), puis `AfterCharacterChange()`
(recalcule bornees, rebinde l'anim, re-pose). `_suppressChar` protege les
re-affectations. `PreselectSlot` aligne chaque combo sur la piece deja chargee.

## Points techniques

- Nouveau champ `LoadedMesh.CharSlot` (le slot de la piece).
- `LoadPartMeshes` = variante de `AddToScene` **sans** refresh et **sans**
  `_sceneExtras` (les pieces sont gerees par slot, pas par chemin) ; tag `CharSlot`.
- `ResetCharMenu()` appele dans `LoadModelCore` (debut) et `ClearScene()`.
- Les pieces etant **skinnees**, `TryAutoAttachUnskinned` les ignore (bon).
- Rendu : chaque piece a sa propre texture (chargement par fichier), alpha conserve.

---

## Fiche technique (CHAR-MULTIPART-1)

### 1. Inventaire des pieces (releve grannydump)

Toutes les pieces = **1 mesh skinne** (`skin == verts`), espace bind commun, dans
`Models\Others`. Champs : os du fichier / verts / indices / texture.

| Piece | os | verts | indices | texture |
|---|---|---|---|---|
| `H_Male_Torso` | 25 | 117 | 630 | `UT256_H_Male_V2G` |
| `H_Male_Torso_Upper` | 13 | 46 | 192 | `UT256_H_Male_V2h` |
| `H_Male_Neck` | 14 | 28 | 120 | `UT256_H_Male_V2G` |
| `H_Male_Head` | 19 | 49 | 222 | `UT256_H_Male_V2G` |
| `H_Male_Ears` | 19 | 24 | 72 | `UT256_H_Male_V2G` |
| `H_Male_UArms` (2 bras) | 15 | 70 | 354 | `UT256_H_Male_V2G` |
| `H_Male_FArms` (2 avant-bras) | 19 | 62 | 294 | `UT256_H_Male_V2G` |
| `H_Male_Hands` | 27 | 96 | 534 | `UT256_H_Male_V2G` |
| `H_Male_ULegs` (2 cuisses) | 12 | 61 | 300 | `UT256_H_Male_V2G` |
| `H_Male_LLegs` (2 mollets) | 12 | 68 | 342 | `UT256_H_Male_V2G` |
| `H_Male_Feet` | 12 | 74 | 402 | `UT256_H_Male_V2G` |
| `H_Male_Pelvis` | 10 | 87 | 480 | `UT256_H_Male_V2G` |
| `H_Male_Face_01` | 10 | 78 | 408 | `UT256_H_Male_Faces_V2` |
| `H_Male_Hair_Curly` | 10 | 79 | 318 | `UT256_Clothing_12_V2` |
| `H_Male_Hair_Facial_Short_Beard` | 10 | 41 | 189 | `UT256_Clothing_11_V2` |
| `E_Male_Hair01` | 34 | 51 | 276 | `UT256_Elven_Hair01_V2` |
| `H_Female_Torso` | 22 | 140 | 750 | `UT256_H_Female_V2c` |
| `H_Female_Torso_PushUp` | 15 | 98 | 510 | `UT256_H_Female_V2c` |
| `E_Female_Hair01` | 34 | 51 | 276 | `UT256_Elven_Hair01_V2` |

Observations :
- Le nombre d'os **varie selon la piece** (10 a 34) : chaque fichier ne contient
  que le sous-ensemble d'os utile a la piece (`Bip01 Head` = bone[12] dans Torso
  mais bone[8] dans Face). **Ne jamais mapper par index**, toujours par **nom**.
- Les `Face_01..32` = 32 visages (~78 verts chacun) ; `H_Male_Face_V2_lod2` = base.
- Les boundings de bind sont coherents entre pieces -> assemblage aligne.
- Les cheveux `E_*` (Elven/HD) ont 34 os et 10 399 UV (atlas) ; les `H_*_Hair_*`
  sont plus simples (10 os).

### 2. Structures de donnees

```
// LoadedMesh (ajout)
public string CharSlot;   // "face","hair","beard","torso","head","pelvis",...

// classe imbriquee Granny3DControl
private class PartItem { public string Display; public string Path;
                         public override string ToString() => Display; }
```

Champs UI ajoutes : `grpChar`, `lblCharInfo`, `cbCharFace`, `cbCharHair`,
`cbCharBeard`, `cbCharTorso`, `_suppressChar`, `_charGender` (0=aucun,1=homme,2=femme).

### 3. UI (dans `InitializeComponent`)

- `grpChar` = `GroupBox` "Personnage (multipartie)", `Dock = Right`, `Width = 250`.
- 4 combos `DropDownList` (`MakeSlotCombo`) : `cbCharFace` (y=68), `cbCharHair`
  (114), `cbCharBeard` (160), `cbCharTorso` (206), largeur 232, `DropDownWidth=260`.
  Evenement commun `CharCombo_Changed`.
- `lblCharInfo` (y=20) affiche le sexe + nb de parties.
- **Ordre d'ajout** dans `pnlPlayRow` (regle de docking WinForms : le premier ajoute
  est docke **en dernier**, donc `Fill` en premier) :
  `Add(grpEquip); Add(grpChar); Add(grpPlayback);` -> `grpPlayback` Left (250),
  `grpChar` Right (250), `grpEquip` Fill (au milieu, a droite de grpPlayback).

> Note : si la fenetre est etroite, `grpEquip` (Fill) peut etre compresse ; ses
> controles sont ancrees a gauche et peuvent etre rognes a droite. Elargir la
> fenetre UOFiddler si besoin.

### 4. Methodes cles (par nom)

| Methode | Role |
|---|---|
| `IsHumanPlaceholder(path, out female)` | vrai si nom = `H_Male_Lod2` / `H_Female_Lod2` |
| `ResetCharMenu()` | `_charGender=0`, vide/desactive les 4 combos (appele par `LoadModelCore` et `ClearScene`) |
| `AssembleHumanBody(female)` | fixe `_charGender`, charge les 13 pieces de base via `LoadPartMeshes` |
| `LoadPartMeshes(path, slot)` | charge les meshes d'un fichier, tag `CharSlot=slot`, **sans** refresh/`_sceneExtras` ; renvoie le nb ajoute |
| `SetCharacterPart(slot, path)` | detruit tous les meshes du `slot`, recharge la piece, appelle `AfterCharacterChange` |
| `AfterCharacterChange()` | `RefreshLoadedList` + `PopulateBoneCombo` + `ComputeReferenceBounds` + rebinde anim + `ApplyAnimationPose`/`RefreshVertexBuffers` |
| `PopulateCharCombos()` | remplit les 4 combos depuis `Others` selon `_charGender`, puis `PreselectSlot` |
| `MakePart(fn)` / `BeautifyPart(fn)` | construit un `PartItem` ; nettoie le nom (retire prefixe genre + `_V2_LOD2`) |
| `PreselectSlot(cb, slot)` | selectionne dans le combo la piece deja chargee pour ce slot |
| `CharCombo_Changed` | si `!_suppressChar` -> `SetCharacterPart(slot, item.Path)` |
| `UpdateCharInfo()` | met a jour `lblCharInfo` ("Homme/Femme - N parties") |

### 5. Branchement dans `LoadModelCore`

1. `ResetCharMenu()` juste apres `_currentModelPath = path;`.
2. `bool humanPlaceholder = IsHumanPlaceholder(path, out bool humanFemale);`
3. Dans la boucle des meshes : le mesh de la boite placeholder est charge mais marque
   `Hidden=true` (voir FICHE "HOST SKELETON") : sa boite n'est **pas** dessinee
   (`RenderScene` saute les meshes caches) mais son **squelette complet 43 os** reste
   present comme **host** pour la combobox des os et pour l'attache.
4. Apres la boucle : si placeholder -> `AssembleHumanBody(humanFemale); PopulateCharCombos();` puis le reste (refresh/bounds/pose) inchange.

### 6. Regles filtres des combos (`PopulateCharCombos`)

Prefixe selon le sexe : `H_Male_`/`E_Male_` (homme) ou `H_Female_`/`E_Female_` (femme).
Seuls les fichiers contenant **"lod"** sont consideres.

| Combo | Condition sur le nom (minuscule) |
|---|---|
| Face | prefixe `H_*` ET contient `_face` ET PAS `facial` |
| Coiffure | (`H_*` OU `E_*`) ET contient `hair` ET PAS `facial` |
| Barbe | homme ET `H_*` ET contient `hair_facial` |
| Torse | `H_*` ET contient `_torso` ET PAS `_torso_upper` |

### 7. Comportements voulus (regressions a eviter)

1. Charger `H_Male_Lod2`/`H_Female_Lod2` -> **corps assemble**, **pas** de boite.
2. Changer Visage/Coiffure/Barbe/Torse -> l'ancien mesh du slot **disparait**, le nouveau suit l'animation.
3. La barbe n'est proposee que pour l'**homme** (`cbCharBeard.Enabled=false` en femme).
4. Changer un slot ne doit **pas** casser l'attache d'equipement en cours (re-pose complete par `ApplyAnimationPose`).
5. `ClearScene` / charger un autre modele -> menu Personnage **reinitialise**.
6. Les pieces skinnees ne doivent **pas** etre auto-attachees (`TryAutoAttachUnskinned` les ignore deja).

### 8. Conventions d'assets pour les pieces (`Others`)

- Corps de base : `H_{Male,Female}_{Torso,Torso_Upper,Neck,Head,Ears,UArms,FArms,Hands,ULegs,LLegs,Feet,Pelvis}_V2_LOD2.grn`.
- Visages : `H_{Male,Female}_Face_NN_V2_LOD2.grn` (+ base `H_Female_Face_V2_lod2`).
- Coiffures : `E_{Male,Female}_HairNN_V2_LOD2.grn` (HD/Elven) et `H_{Male,Female}_Hair_<style>_V2_LOD2.grn`.
- Barbe/moustache (homme) : `H_Male_Hair_Facial_<style>_V2_LOD2.grn`.
- Chapeaux : `H_{Male,Female}_Hat_*` et `H_*_HatHair_*` (cheveux sous chapeau).
- Torse femelle variante : `H_Female_Torso_PushUp_V2_LOD2.grn`.



## HOST-SKELETON-1 : attache CP_Grasp, mesh skinné, Dawn_Girl

### 1. Constat (relevé `grannydump`)

- `CP_Grasp_Rhand` / `CP_Grasp_Lhand` **n'existent que dans le squelette complet
  43 os** (`H_Male_Lod2` / `H_Female_Lod2`, os 37/40) et dans les pieces
  `Hands`/`FArms` (os correctement place a `bindW=(-0,681,0,041,1,305)` etc.).
  La piece `Torso` en expose une **copie dégénérée** a l'origine
  (`bindW=(0,057,0,022,0,003)`) car elle ne contient pas la chaine du bras.
- `H_Male_Armor_Leather_Sleeves_V2_LOD2` : mesh **skinné** (`verts=86 skin=86`,
  17 os) -> couvre les deux bras, **suit deja le squelette**, ne doit **pas** etre attache.
- `Dawn_Girl_Lod2` : 34 os, racine `__Root`/`master_h_female`, os standard
  `Bip01 ...`, **pas** de `CP_Grasp_Rhand` ; possede un os dedie `Sword01`
  (bone[12], enfant de `Bip01 R Hand`). Deux meshes : corps 815v skinné +
  epee 36v `skin=0`.
- Les animations `H_Female_*` ont des tracks nommes `__Root, master_h_female,
  Bip01 ...` (43 tracks) -> compatibles par **nom** avec Dawn_Girl (33/34 os).

### 2. Cause racine

Le squelette complet du placeholder etait **jete** (`continue`), donc :
1. la combobox des os se remplissait depuis la piece au plus d'os (Torso 25 ou
   Hands 27) ;
2. l'attache tombait, selon le mesh host trouve en premier, sur le `CP_Grasp`
   **dégénéré** du Torse -> arme collee a l'origine ;
3. recharger H_male puis H_female cassait l'attache (host/ordre different).

### 3. Correctif (DLL 100352 o)

- `LoadedMesh.Hidden` : la boite placeholder est chargee mais `Hidden=true`.
  `RenderScene` et `RefreshVertexBuffers` sautent les meshes cachés.
  `ApplyAnimationPose` calcule quand meme ses matrices (garde = `Bones.Count>0`,
  plus de test sur `Skin`).
- Attache : on choisit le **host au plus grand nombre d'os** contenant l'os
  demande (le placeholder 43 os bat le CP_Grasp dégénéré du Torse).
- `AttachSelectedMeshToBone` : **refuse** d'attacher un mesh skinné
  (message "Mesh skinné : suit déjà le squelette").
- `TryAutoAttachUnskinned` : parmi les os proches, prefere une **socket nommee**
  (`grasp, cp_, sword, weapon, shield, bow, staff, wand, axe, mace`) si elle est
  a moins de `+0,10` (distance²) du plus proche -> `Dawn_Girl` s'attache a
  `Sword01` au lieu d'un bout de doigt.
- Defaut de la combobox des os : `CP_Grasp_Rhand`, `CP_Grasp_Lhand` d'abord.

### 4. Comportements voulus

1. H_male ET H_female exposent `CP_Grasp_Rhand`/`CP_Grasp_Lhand` dans le combo.
2. Une arme (mesh rigide non-skinne) attachee a `CP_Grasp_Rhand` suit la main.
3. Armures/cheveux skinnes : attache refusee (ils suivent deja le corps).
4. Recharger H_male -> H_female conserve une attache correcte.
5. `Dawn_Girl` : l'epee s'attache a `Sword01`.





---

# SKINMAP-FIX-2 — Bone-ties par mesh (fichiers multi-mesh)

Date : 2026-10-14
Statut : **corrige, valide (harness), deploye**

## Symptome

`Dawn_Girl_Lod2` (2 meshes : epee 36v + corps 815v) : en lecture d'animation
H_Female, le **corps se deformait massivement** — `DIRECT maxEdgeStretch=49,64`
avec **738 aretes > 1,5x des t=0,0** (Orc = 7,98 pour comparaison). Le mesh au
repos etait correct (bind = identite).

## Cause racine

SKINMAP-FIX-1 mappait l'index local d'influence via une **liste unique et plate**
de tous les chunks `0xCA5E0C0A` (bone-ties) du fichier :

```csharp
skinBoneMap[localB] = c0aChunks[localB].firstInt   // valable 1 seule mesh
```

Or les bone-ties sont **par mesh** : chaque mesh possede sa **propre** liste de
ties contigue dans le fichier, et son `localB` indexe **sa** sous-liste. Pour un
fichier multi-mesh, la concatenation globale decale tous les indices.

Preuve (Dawn) : avec un decalage **+1** (les ties du corps commencent a
`c0a[1]` car `c0a[0]=Sword01` appartient a la mesh-epee), la table
`localB -> os` colle exactement a la geometrie (groupe de sommets par `localB`,
centroide -> os bind-world le plus proche) sur les 29 entrees.

Le nombre de ties d'une mesh est donne par l'en-tete du chunk skin
(`0xCA5E0702`) : `h1 = bindingCount - 1`.

- Dawn : mesh0(epee) `h1=0 -> 1 tie`, mesh1(corps) `h1=27 -> 28 ties` ; somme
  `1+28 = 29 = c0a count`.
- Orc : mesh0 `h1=30 -> 31 ties` ; somme `31 = c0a count` (base 0, inchange).

## Correctif

Calculer une **base de ties par mesh** (somme cumulee des counts en ordre de
mesh) et indexer `bindingBase[j] + localB`. Garde-fou : si la somme des counts
ne tombe pas exactement sur le nombre de chunks c0a, on retombe sur l'ancien
comportement global (base 0) — les fichiers mono-mesh sont donc inchanges.

```csharp
int[] bindingBase = new int[meshCount + 1];
for (int j = 0; j < meshCount; j++) {
    int cnt = 0;
    if (j < skinChunks.Count && skinChunks[j].DataSize != 0xFFFFFFFF) {
        seek(skinChunks[j].DataOffset); ReadInt32(); // numVerts
        int h1 = ReadInt32();
        if (h1 >= 0 && h1 < 65536) cnt = h1 + 1;
    }
    bindingBase[j + 1] = bindingBase[j] + cnt;
}
bool usePerMeshTies = bindingBase[meshCount] == c0aChunks.Count;
if (!usePerMeshTies) bindingBase = new int[meshCount + 1];

// mapping influence :
int mapIdx = bindingBase[j] + localB;
int masterB = (mapIdx >= 0 && mapIdx < skinBoneMap.Count) ? skinBoneMap[mapIdx] : localB;
```

## Validation (harness grannyrepro, Dawn + H_Female_Athletic_Idle)

`DIRECT edge-stretch` : **49,64 -> 4,63** (738 -> 118 aretes >1,5x), soit
**mieux qu'Orc** (7,98). Les `domVerts` par os sont desormais coherents
(Head=174, L Hand=34, R Clavicle=37, ...). Le stretch residuel = limitation
intrinseque de `DIRECT` (rest-pos d'anim != bind modele), pas le skin map.

## Fichiers modifies

- `GrannyFormatParser.cs` : `bindingBase[]` (juste apres `meshCount`) + mapping
  d'influence (~ligne 510). Copie truth -> live, build live -> DLL deploye
  (`Granny3DPlugin.dll`, ~100864 o, 2026-10-14 08:27).

---

# EQUIP-FIT-1 — Anti-penetration des accessoires (armures/pantalons/brassards)

Date : 2026-10-14
Statut : **implemente, deploye**

## Probleme

Sur H_Male / H_Female, les accessoires ajoutes (ex.
`H_Male_Armor_Dragon_Breastpiece`, pantalons, brassards) "rentrent" dans le
corps. Cause dominante : ces pieces exportent **tous leurs os avec le nom = le
chemin `.max`** (chaines identiques) => la liaison d'animation par nom echoue
=> la piece reste figee en pose bind pendant que le corps s'anime => le corps
la traverse. Cause secondaire : ajustement geometrique (piece legerement plus
petite).

Le collider et le depth-offset ont ete ecartes (pas de physique ici ; le
depth-offset ne corrige que le z-fighting, pas les vraies intersections).

## Correctif A — liaison positionnelle des os (racine)

`EnsureAnimationBindings(anim)` (appele depuis `ApplyAnimationPose` et depuis
les chargements) :
1. lie par **nom** chaque mesh (retourne le nombre d'os matches) ;
2. choisit comme **reference** le mesh qui a matche le plus de tracks (le
   placeholder 43 os ou le torse principal) ;
3. pour chaque mesh dont < moitie des os sont lies par nom, lie chaque os
   restant a l'os de la reference dont la **position bind-world** est la plus
   proche (via `ComputeBindWorld`).

Resultat : les pieces path-named suivent desormais le corps -> plus de traversee
pendant l'animation.

Note : ne PAS utiliser le squelette de l'ANIMATION comme reference (son rest-pose
differe du bind, ex. epaules baissees -> la clavicule matcherait l'UpperArm). La
reference doit etre un mesh correctement lie par NOM (bind = bind humain).

## Correctif B — reglages manuels par piece (offset + echelle)

`grpEquip` gagne un champ **Echelle** (0,5..1,5, defaut 1,0). Les champs
Miroir / Rotation / Decalage existants s'appliquent maintenant **aussi aux
meshes skinnes** (pas seulement aux armes) :

- `LoadedMesh.PartScale` (defaut 1) + `BindCentroid` (pivot calcule dans
  `BuildCornerData`).
- `RefreshVertexBuffers` (branche skinnee) : apres skinning,
  `p = pivot + (p - pivot) * PartScale + AttachOffset`.
- `BuildAttachTweak` (armes rigides) : la mise a l'echelle uniforme s'ajoute au
  miroir.
- `ApplyEquipControls` re-pose desormais **tout** mesh selectionne (skinne ou
  rigide), plus seulement les meshes attaches a un os.
- `ResetEquipControls` remet l'echelle a 1.

Usage : selectionner la piece dans "Meshes affiches", puis augmenter l'echelle
(pour l'envelopper / gonfler) ou la decaler (X/Y/Z) jusqu'a supprimer la
penetration.

## Fichiers modifies

- `Granny3DControl.cs` : `LoadedMesh.PartScale/BindCentroid`, `BuildCornerData`
  (centroide), `BindMeshToAnimation` (retourne le nb de matches), nouvelle
  `EnsureAnimationBindings`, `ApplyAnimationPose` (appel + branche skinnee
  tweakee), `BuildAttachTweak`, `RefreshVertexBuffers`, UI `nudScale` +
  `ApplyEquipControls`/`ResetEquipControls`/`LoadEquipControlsFromMesh`.
- DLL deploye : `Granny3DPlugin.dll`, ~102912 o, 2026-10-14 08:43.

## EQUIP-FIT-2 : scale par axe + skin wrap (2026-10-14)

Retour utilisateur sur EQUIP-FIT-1 : "ca aide un peu mais c'est loin d'etre
parfait" ; la T-pose a deja des erreurs et en mouvement "c'est catastrophique"
(certains meshes depassent aleatoirement ; un scale global n'arrange pas).
Deux ajouts :

### A. Scale par axe X/Y/Z

- `LoadedMesh.PartScaleVec` (Vector3, defaut 1,1,1), multiplie par le scale
  global `PartScale`.
- UI `grpEquip` : 3 NUD `nudScaleX/Y/Z` (ligne "Echelle X/Y/Z") + label
  "Echelle (globale)". `pnlPlayRow` passe a 322 px, `pnlRightBottom` a 440 px
  pour tout loger.
- `RefreshVertexBuffers` (branche skinnee) : `pscale = PartScale * PartScaleVec`
  puis, autour du centroide bind :
  `r = (skinnedPos - pivot) ; r *= pscale ; skinnedPos = pivot + r + AttachOffset`
  (scale non uniforme par axe).
- `ApplyEquipControls` / `ResetEquipControls` / `LoadEquipControlsFromMesh`
  gerent `nudScaleX/Y/Z`.

### B. Skin wrap (bouton) - copier la peau du corps

Objectif : rendre l'accessoire SOLIDAIRE de la surface du corps, quel que soit
son propre squelette (souvent PATH-named mal relie). Principe retenu : pour
chaque sommet de l'accessoire, copier les POIDS de skinning du sommet de corps
le plus proche (en espace bind), en referencant les os du mesh corps SOURCE.

- `LoadedMesh.WrapSource` (LoadedMesh) + `WrapSkin` (VertexInfluence[][]).
- `SkinWrapSelected()` (bouton "Skin wrap (copier la peau du corps)") :
  1. sources = autres meshes visibles, non Hidden, `Skin!=null && Length>0`,
     `Bones.Count>0`, `WrapSource==null` (pas de chainage).
  2. passe 1 : plus proche global (toutes sources) -> compte les "hits" par
     source ; passe 2 : plus proche DANS la source gagnante (max hits) ;
     `WrapSkin[v] = source.Skin[idx]`.
  3. bouton = toggle : si `WrapSource != null`, detache et restaure le rendu
     d'origine.
- `RefreshVertexBuffers` : si `hasWrap`, utilise `WrapSkin` + les matrices du
  SOURCE (`WrapSource.SkinMatrices`, `WrapSource.Bones.Count`) au lieu des
  matrices propres. Normales idem (branche wrap).
- Suppression d'un mesh : les meshes dont `WrapSource == dead` perdent leur
  wrap (nullifies) dans `RemoveMeshAt` ET dans `SetCharacterPart` (changement
  de slot multipartie) pour eviter une reference pendante.

### Fichiers modifies (EQUIP-FIT-2)

- `Granny3DControl.cs` : champs `PartScaleVec`, `WrapSource`, `WrapSkin`,
  nouvelle `SkinWrapSelected`, branche wrap + scale par axe dans
  `RefreshVertexBuffers`, UI `nudScaleX/Y/Z` + `btnSkinWrap`, tailles de panels,
  nullification du wrap dans `RemoveMeshAt`/`SetCharacterPart`.
- DLL deploye : `Granny3DPlugin.dll`, 106496 o, 2026-10-14 08:55.

## EQUIP-FIT-3 : poids lissés + épaisseur normale (2026-10-14)

Retour utilisateur (apres EQUIP-FIT-2) : meme avec le skin wrap et un placement
parfait en T-pose, en mouvement (bras tendus en avant, etc.) le dos/torse du
personnage ressort a travers l'accessoire ; idem une cape en walk (les pieds du
H_male traversent la cape). Ce n'est PAS un probleme de placement : c'est un
artefact de skinning aux articulations. Le skin wrap copiait les poids du SEUL
sommet le plus proche, alors que la surface du corps est une INTERPOLATION de
plusieurs sommets : a l'epaule/coude/genou les deux se deforment differemment et
le corps ressort.

### A. Poids lissés (K plus proches, pondération par la distance)

`SkinWrapSelected` : apres avoir choisi le mesh source (le plus de "hits"),
pour chaque sommet de l'accessoire on prend les **K=4** sommets du corps les
plus proches, et on accumule leurs influences ponderees par `1/(d^2+eps)`. On
garde les 4 os les plus forts, normalises. L'accessoire suit ainsi la SURFACE
du corps (et non un seul sommet) -> divergence quasi nulle aux articulations.

### B. Épaisseur (gonflement le long de la normale)

- `LoadedMesh.BaseVertexNormals` (Vector3[]) : normales de repos par sommet
  (moyenne ponderee par aire des faces adjacentes), calculees dans `SetupMeshVBO`.
- `LoadedMesh.Thickness` (float, defaut 0) + NUD `nudThickness` ("Épaisseur
  (normale)", 0..0.5, pas 0.002).
- `RefreshVertexBuffers` (branche skindee) : apres skinning + scale + offset, on
  pousse le sommet le long de la normale skinnee :
  `dnorm = Σ w * TransformNormal(baseNormal, mats[bId])`, normalisee, puis
  `skinnedPos += dnorm * Thickness`. Pousse tout le vetement vers l'exterieur
  -> aucun micro-ecart corps/armure ne ressort. (Pour la cape : la normale
  pointe vers l'exterieur, les pieds restent dedans.)
- `ApplyEquipControls` / `ResetEquipControls` / `LoadEquipControlsFromMesh`
  gerent `nudThickness`.

Méthode : selectionner la piece, cliquer "Skin wrap", puis augmenter
progressivement "Épaisseur (normale)" jusqu'à disparition totale de la
traversée sur toutes les poses.

### Note fuite d'encodage (corrigee)

Un `Set-Content -Encoding UTF8` (redimensionnement des panels) a relu le fichier
en CP1252 et l'a double-encode (36 sequences : é->"Ã©", É->"Ã‰", ->"â†'",
etc.). Repare par la transformation inverse (UTF-8 decode -> CP1252 encode ->
UTF-8 decode) ; back-up dans `%TEMP%\opencode\Granny3DControl.corrupt.bak.cs`.
**Ne pas utiliser Set-Content/Get-Content sur ce fichier** : passer par l'outil
d'edition (UTF-8) ou [System.IO.File]::WriteAllText + UTF8Encoding($true).

### Fichiers modifies (EQUIP-FIT-3)

- `Granny3DControl.cs` : `LoadedMesh.BaseVertexNormals/Thickness`, normales par
  sommet dans `SetupMeshVBO`, K-nearest lissé dans `SkinWrapSelected`, épaisseur
  dans `RefreshVertexBuffers`, UI `nudThickness`, panneaux `pnlPlayRow`=352 /
  `pnlRightBottom`=470.
- DLL deploye : `Granny3DPlugin.dll`, 108544 o, 2026-10-14 09:20.

## CLOTH-SIM-1 : simulation de tissu temps reel (capes / robes / jupes)

Objectif : au lieu de laisser les jambes traverser une cape, simuler un vrai
tissu (verlet) qui pend, tombe et est repousse par le corps.

- `LoadedMesh` : `IsCloth`, `ClothPos`, `ClothPrev`, `ClothPin` (bool[]),
  `ClothEdgeA/B` (long[]), `ClothRest` (float[]), `ClothSimIterations=10`,
  `ClothGravity=9.8f`.
- `BuildClothFromMesh` : particules = sommets (depart = `BaseVertices`), liens =
  aretes des triangles (`CornerVertex`, dedupliquees via `HashSet<long>`),
  epingles = bande haute (z >= maxZ - 0.18*(maxZ-minZ)) = epaules/cou.
- `ClothPinTarget` : position epinglee via le skin PROPRE du mesh (pour une cape
  = le bon binding spine, pas les jambes).
- `BuildBodyCapsules` : segments os->parent de TOUS les autres meshes avec
  squelette (y compris les Hidden : le placeholder H_Male porte les os des
  jambes), rayon R=0.06. `PushOutOfCapsules` repousse chaque particule libre
  hors des capsules.
- `StepClothForMesh` : verlet (damp=0.99, gravite -Z), puis
  `ClothSimIterations` passes de contrainte de distance + collision capsules.
- Integration : `_clothDt` (dt du RenderTimer) passe a `StepClothForMesh` dans
  `ApplyAnimationPose` avant `RefreshVertexBuffers` ; `ResetAllCloth` au
  chargement d'une animation. `RefreshVertexBuffers` copie `ClothPos` dans le
  cache deforme et recalcule les normales par triangle.
- UI `grpEquip` : `chkCloth` « Tissu (simulation) » -> `SetClothSelected(bool)`
  (active = annule le skin wrap + `BuildClothFromMesh`), `nudClothGrav`
  (0..30, defaut 9.8). Panneaux `pnlPlayRow`=392 / `pnlRightBottom`=510.
- **Ne pas cumuler avec skin wrap** (le wrap enverrait la cape sur les jambes).
- Test : H_Male + cape, SANS skin wrap, cocher Tissu, jouer Walk/Run -> la cape
  doit tomber et etre poussee par les jambes.
- DLL deploye : `Granny3DPlugin.dll`, 114176 o, 2026-10-14 09:40.

## CLOTH-SIM-2 : weld (fusions de sommets coincidents) + subdivision

Deux problemes observes en test :
1. L'epaisseur ne "epaissit" pas, elle separe le mesh en deux feuilles non liees.
2. Quand on active le tissu, certains points se decollent (weld/connect manquant).

Cause commune : le mesh contient des **sommets coincidents** (geometrie
double-face et/ou coutures UV) : plusieurs indices a la meme position. L'epaisseur
les poussait chacun selon SA normale (directions opposees -> deux feuilles), et
le cloth les simulait comme des particules independantes (une couche tombait,
l'autre restait -> decollement).

Correctif : **soudure par position** au niveau des particules.
- `BuildWeld(lm)` : quantifie les positions a ~0.1 mm, regroupe les sommets
  coincidents -> `Weld[v]` = id de particule, `WeldCount` = nb de positions uniques.
  Appele en fin de `BuildCornerData`.
- **Epaisseur** : les `BaseVertexNormals` sont moyennes par particule (toutes les
  copies coincidentes partagent la meme normale) -> l'epaisseur deplace les copies
  ensemble, plus de separation en deux feuilles.
- **Cloth** : les particules sont les positions soudees (`ClothPos/Prev/Pin` indexes
  par particule, taille `WeldCount`), `ClothRep[p]` = sommet original representatif
  (pour skinner les epingles), `ClothPos0` = positions repos par particule.
  Les aretes sont remappees via `Weld`. Au rendu, chaque sommet lit
  `ClothPos[Weld[v]]` -> plus de dechirure/decollement.
- `ResetAllCloth` copie depuis `ClothPos0` (plus `BaseVertices`, indexation differente).

Subdivision (bouton "Subdiviser (1 -> 4)") : `SubdivideSelectedMesh` fait une
subdivision par milieux (1 triangle -> 4) : positions interpolees, poids de peau
moyennes (`AverageSkin`, union des os, top 4, normalises), UV par coin interpolees
(`BuildCornerData(lm, explicitCornerUVs)`), puis re-VBO et re-cloth. Donne plus de
particules au cloth pour un drapé plus fin. Les meshes qui "wrappent" ce mesh
perdent leur wrap (indices devenus obsoletes).

- UI `grpEquip` : `btnSubdiv` "Subdiviser (1 → 4)" ; panneaux `pnlPlayRow`=422 /
  `pnlRightBottom`=540.
- DLL a deployer : `Granny3DPlugin.dll`, 2026-10-14 (apres fermeture UOFiddler).

## CLOTH-SIM-3 : collision contre la surface reelle du corps

**Symptome** : les jambes traversaient encore la cape malgre les capsules d'os.

**Diagnostic** (`ClothDbg` dans le log) : `caps=221` mais `insideNow=4` seulement.
Les particules du tissu tombaient a `partY=[0.16, 0.49]` alors que les capsules
d'os etaient centrees a `capY=[-0.50, 0.35]`. Or les **maillages de jambe** (log
`Pose ... defBounds`) montent jusqu'a `y≈0.387` : la SURFACE de la jambe atteint
la cape, mais la capsule d'os (sur l'axe de l'os, Y≈0, rayon 0.13) n'y arrive pas.
=> collision quasi jamais declenchee.

**Correctif** : collisionner contre la **surface deformee reelle** du corps, pas
seulement l'axe des os.
- `BuildBodyPoints(lm)` : collecte, pour chaque autre mesh visible non-cloth et
  non-Hidden, tous les `DeformedCache[i]` (sommets) + chaque centroide de triangle
  (`CornerVertex[t..t+2]`). = nuage de points sur la vraie surface.
- `PushOutOfPoints(lm, pts, R)` : pour chaque particule libre, si un point du corps
  est a moins de `R` (5 cm), la pousse a `R` de ce point (direction
  `particle - point`).
- Dans `StepClothForMesh` : `PR = 0.05`. Apres les contraintes de distance de chaque
  iteration, on pousse out of capsules PUIS out of body points.
- **Note timing** : `StepClothForMesh` tourne AVANT `RefreshVertexBuffers`, donc le
  `DeformedCache` du corps utilise est celui de la frame precedente (decalage 1
  frame, negligeable).
- Diagnostic mis a jour : `ClothDbg ... pts=N ptY=[..] ptZ=[..] partY=[..] partZ=[..] insidePts=K`
  (au lieu de `caps/insideNow`). `insidePts > 0` = la collision surface s'active.

- DLL a deployer : `Granny3DPlugin.dll` 121344 o, 2026-10-14 10:25 (apres fermeture
  UOFiddler).
