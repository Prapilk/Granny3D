import json
import os

new_translations = {
    # Markers
    "marker_head": {
        "fr": "Tête (Pivot nuque / base crâne)",
        "en": "Head (Neck pivot / skull base)"
    },
    "marker_neck": {
        "fr": "Cou (Pivot base cou / haut sternum)",
        "en": "Neck (Base of neck / upper sternum)"
    },
    "marker_chest": {
        "fr": "Poitrine / Cage Thoracique",
        "en": "Chest / Ribcage"
    },
    "marker_spine": {
        "fr": "Abdomen (Taille / nombril)",
        "en": "Abdomen (Waist / navel)"
    },
    "marker_pelvis": {
        "fr": "Bassin / Hanches (Pelvis)",
        "en": "Pelvis / Hips"
    },
    "marker_r_clavicle": {
        "fr": "Clavicule D (Écran Gauche)",
        "en": "R Clavicle (Screen Left)"
    },
    "marker_r_upperarm": {
        "fr": "Épaule D (Écran Gauche)",
        "en": "R Shoulder (Screen Left)"
    },
    "marker_r_forearm": {
        "fr": "Coude D (Écran Gauche)",
        "en": "R Elbow (Screen Left)"
    },
    "marker_r_hand": {
        "fr": "Poignet D (Écran Gauche)",
        "en": "R Wrist (Screen Left)"
    },
    "marker_r_thumb": {
        "fr": "Pouce D (Écran Gauche)",
        "en": "R Thumb (Screen Left)"
    },
    "marker_r_fingers": {
        "fr": "Doigts D (Écran Gauche)",
        "en": "R Fingers (Screen Left)"
    },
    "marker_l_clavicle": {
        "fr": "Clavicule G (Écran Droite)",
        "en": "L Clavicle (Screen Right)"
    },
    "marker_l_upperarm": {
        "fr": "Épaule G (Écran Droite)",
        "en": "L Shoulder (Screen Right)"
    },
    "marker_l_forearm": {
        "fr": "Coude G (Écran Droite)",
        "en": "L Elbow (Screen Right)"
    },
    "marker_l_hand": {
        "fr": "Poignet G (Écran Droite)",
        "en": "L Wrist (Screen Right)"
    },
    "marker_l_thumb": {
        "fr": "Pouce G (Écran Droite)",
        "en": "L Thumb (Screen Right)"
    },
    "marker_l_fingers": {
        "fr": "Doigts G (Écran Droite)",
        "en": "L Fingers (Screen Right)"
    },
    "marker_r_thigh": {
        "fr": "Hanche D (Écran Gauche)",
        "en": "R Thigh (Screen Left)"
    },
    "marker_r_calf": {
        "fr": "Genou D (Écran Gauche)",
        "en": "R Knee (Screen Left)"
    },
    "marker_r_foot": {
        "fr": "Cheville D (Écran Gauche)",
        "en": "R Ankle (Screen Left)"
    },
    "marker_l_thigh": {
        "fr": "Hanche G (Écran Droite)",
        "en": "L Thigh (Screen Right)"
    },
    "marker_l_calf": {
        "fr": "Genou G (Écran Droite)",
        "en": "L Knee (Screen Right)"
    },
    "marker_l_foot": {
        "fr": "Cheville G (Écran Droite)",
        "en": "L Ankle (Screen Right)"
    },

    # Cameras and Viewports
    "cam_front": {
        "fr": "👁 Vue Face (Z/X)",
        "en": "👁 Front View (Z/X)"
    },
    "cam_profile": {
        "fr": "👁 Vue Profil (Z/Y)",
        "en": "👁 Profile View (Z/Y)"
    },
    "cam_back": {
        "fr": "👁 Vue Dos",
        "en": "👁 Back View"
    },
    "cam_3d": {
        "fr": "🎥 3D Persp",
        "en": "🎥 3D Persp"
    },
    "cam_center": {
        "fr": "🎯 Centrer Caméra",
        "en": "🎯 Center Camera"
    },
    "chk_wireframe": {
        "fr": "Fil de fer",
        "en": "Wireframe"
    },
    "chk_skeleton": {
        "fr": "Squelette",
        "en": "Skeleton"
    },
    "chk_mesh": {
        "fr": "Modèle 3D",
        "en": "3D Model"
    },

    # Rig Studio
    "rig_title": {
        "fr": "⚡ Convert/create rig to UO (Rig Studio)",
        "en": "⚡ Convert/create rig to UO (Rig Studio)"
    },
    "rig_grp_source": {
        "fr": "1. Modèle 3D Source (Actif / Importé)",
        "en": "1. Source 3D Model (Active / Imported)"
    },
    "rig_btn_browse": {
        "fr": "📂 Ouvrir un autre modèle 3D…",
        "en": "📂 Open another 3D model…"
    },
    "rig_btn_reload_preview": {
        "fr": "🔄 Recharger la preview active",
        "en": "🔄 Reload active preview"
    },
    "rig_lbl_loading_model": {
        "fr": "Chargement du modèle…",
        "en": "Loading model…"
    },
    "rig_grp_transform": {
        "fr": "2. Orientation, Échelle & Alignement du Modèle",
        "en": "2. Model Orientation, Scale & Alignment"
    },
    "rig_btn_stand_up": {
        "fr": "🧍 Redresser Debout (Z-up)",
        "en": "🧍 Stand Up (Z-up)"
    },
    "rig_btn_ground": {
        "fr": "⚡ Pieds au sol (Z=0) & Centrer",
        "en": "⚡ Feet on Ground (Z=0) & Center"
    },
    "rig_btn_fit_skel": {
        "fr": "⚡ Ajuster taille au squelette (1.75m)",
        "en": "⚡ Fit Size to Skeleton (1.75m)"
    },
    "rig_btn_mirror_x": {
        "fr": "⇋ Miroir X",
        "en": "⇋ Mirror X"
    },
    "rig_lbl_nudge": {
        "fr": "Ajuster position :",
        "en": "Adjust position:"
    },
    "rig_lbl_scale": {
        "fr": "Échelle globale :",
        "en": "Global scale:"
    },
    "rig_lbl_calc_bounds": {
        "fr": "Dimensions : calcul en cours…",
        "en": "Dimensions: calculating…"
    },
    "rig_grp_skeleton": {
        "fr": "3. Squelette UO (Humains, Monstres, Animaux) & Options",
        "en": "3. UO Skeleton (Humans, Monsters, Animals) & Options"
    },
    "rig_rb_male": {
        "fr": "Homme (H_Male)",
        "en": "Male (H_Male)"
    },
    "rig_rb_female": {
        "fr": "Femme (H_Female)",
        "en": "Female (H_Female)"
    },
    "rig_rb_creature": {
        "fr": "Autre créature UO",
        "en": "Other UO creature"
    },
    "rig_btn_choose_skel": {
        "fr": "🔍 Choisir squelette...",
        "en": "🔍 Choose skeleton..."
    },
    "rig_chk_symmetry": {
        "fr": "Symétrie X automatique (déplacer G met à jour D)",
        "en": "Auto X symmetry (moving L updates R)"
    },
    "rig_chk_force_skin": {
        "fr": "Forcer le skinning volumétrique UO (recommandé si modèle statique)",
        "en": "Force UO volumetric skinning (recommended if static model)"
    },
    "rig_grp_markers": {
        "fr": "4. Positionnement des Articulations (Croix 3D)",
        "en": "4. Joint Positioning (3D Crosses)"
    },
    "rig_lbl_joint_tip": {
        "fr": "💡 Les croix 3D marquent les PIVOTS d'articulation (rotation), pas le milieu des volumes.",
        "en": "💡 3D crosses mark rotation PIVOTS, not the center of volumes."
    },
    "rig_btn_auto_detect": {
        "fr": "🎯 Auto-Placement Mixamo (5 repères)",
        "en": "🎯 Mixamo Auto-Placement (5 landmarks)"
    },
    "rig_btn_orig_skel": {
        "fr": "🦴 Squelette d'Origine",
        "en": "🦴 Original Skeleton"
    },
    "rig_msg_no_orig_skel": {
        "fr": "Aucun squelette d'origine en mémoire pour ce modèle.",
        "en": "No original skeleton stored in memory for this model."
    },
    "rig_btn_recalc_arms": {
        "fr": "💪 Recalculer Skinning Bras",
        "en": "💪 Recalculate Arm Skinning"
    },
    "rig_btn_recalc_all": {
        "fr": "🔥 Recalculer Tout le Skinning",
        "en": "🔥 Recalculate Full Skinning"
    },
    "rig_lbl_sel_cross": {
        "fr": "Croix sélectionnée :",
        "en": "Selected cross:"
    },
    "rig_lbl_twist": {
        "fr": "Torsion (Roll) :",
        "en": "Twist (Roll):"
    },
    "rig_palm_down": {
        "fr": "Paume Bas (90°)",
        "en": "Palm Down (90°)"
    },
    "rig_palm_back": {
        "fr": "Paume Arr. (180°)",
        "en": "Palm Back (180°)"
    },
    "rig_palm_default": {
        "fr": "0° (Défaut)",
        "en": "0° (Default)"
    },
    "rig_lbl_rot_tip": {
        "fr": "💡 Astuce : Cliquez directement sur les croix (Pouce, Doigts, etc.) et déplacez-les à la souris !",
        "en": "💡 Tip: Click directly on the crosses (Thumb, Fingers, etc.) and move them with the mouse!"
    },
    "rig_btn_reset_pose": {
        "fr": "🔄 Réinitialiser les repères Biped UO par défaut",
        "en": "🔄 Reset UO Biped landmarks to default"
    },
    "rig_grp_anim": {
        "fr": "5. Prévisualisation en Direct (Test Animation UO)",
        "en": "5. Live Preview (UO Animation Test)"
    },
    "rig_btn_play_anim": {
        "fr": "▶ Lancer le Test",
        "en": "▶ Start Test"
    },
    "rig_btn_stop_anim": {
        "fr": "⏹ T-Pose",
        "en": "⏹ T-Pose"
    },
    "rig_btn_victory": {
        "fr": "✨ Test Doigts (Victoire)",
        "en": "✨ Finger Test (Victory)"
    },
    "rig_btn_import_action": {
        "fr": "📂 Importer Action...",
        "en": "📂 Import Action..."
    },
    "rig_lbl_finger_flex": {
        "fr": "✋ Test Flexion Doigts / Poing : 0%",
        "en": "✋ Finger Flex / Fist Test: 0%"
    },
    "rig_btn_flex_open": {
        "fr": "Ouvrir",
        "en": "Open"
    },
    "rig_btn_flex_fist": {
        "fr": "Poing",
        "en": "Fist"
    },
    "rig_lbl_finger_flex_pct": {
        "fr": "✋ Test Flexion Doigts / Poing : {0}%",
        "en": "✋ Finger Flex / Fist Test: {0}%"
    },
    "rig_grp_action": {
        "fr": "6. Enregistrement",
        "en": "6. Save"
    },
    "rig_btn_validate": {
        "fr": "⚡ Valider & Générer le Character UO (.char)",
        "en": "⚡ Validate & Generate UO Character (.char)"
    },

    # Main UI Controls & Bone Linking
    "lbl_attach_bone": {
        "fr": "Attacher à l'os :",
        "en": "Attach to bone:"
    },
    "btn_attach_toggle": {
        "fr": "Attacher / Détacher",
        "en": "Attach / Detach"
    },
    "lbl_models": {
        "fr": "Modèles (LOD2)",
        "en": "Models (LOD2)"
    },
    "btn_force_base": {
        "fr": "Afficher la géométrie brute (base, sans peau)",
        "en": "Show raw geometry (base, unskinned)"
    },
    "btn_preview": {
        "fr": "Aperçu",
        "en": "Preview"
    },
    "btn_cancel": {
        "fr": "Annuler",
        "en": "Cancel"
    },
    "chk_wrap_live": {
        "fr": "Aperçu temps réel",
        "en": "Live preview"
    },
    "btn_pose_reset_bone": {
        "fr": "↺  Réinitialiser l'os",
        "en": "↺  Reset bone"
    },
    "btn_pose_reset_all": {
        "fr": "⟲  Tout réinitialiser",
        "en": "⟲  Reset all"
    },
    "btn_pose_save": {
        "fr": "💾  Sauvegarder la pose sous...  (fichier .pose)",
        "en": "💾  Save pose as... (.pose file)"
    },
    "chk_pose_body_only": {
        "fr": "Corps seulement (l'équipement libre reste en place)",
        "en": "Body only (free equipment stays in place)"
    },
    "chk_pose_mirror": {
        "fr": "Miroir X automatique (D ↔ G)",
        "en": "Auto X mirror (R ↔ L)"
    },
    "cb_bone_none": {
        "fr": "(non attaché)",
        "en": "(not attached)"
    },
    "lbl_selected_meshes_fmt": {
        "fr": "{0} meshes sélectionnés",
        "en": "{0} meshes selected"
    },
    "menu_strip_rig": {
        "fr": "✂️ Supprimer le squelette / Convertir en accessoire rigide (Selle, Arme, Prop)...",
        "en": "✂️ Strip skeleton / Convert to rigid accessory (Saddle, Weapon, Prop)..."
    },
    "msg_strip_rig_confirm": {
        "fr": "Voulez-vous supprimer le squelette et le skinning de ce(s) mesh(es) ?\n\nIl(s) deviendront des accessoires 100% rigides (idéal pour les selles, armes, casques, boucliers) qui s'attachent à n'importe quel os (ex: dos du cheval, main, tête) sans aucune déformation.",
        "en": "Do you want to strip the skeleton and skinning from this mesh(es)?\n\nThey will become 100% rigid accessories (ideal for saddles, weapons, helmets, shields) that attach to any bone (e.g. horse back, hand, head) without deformation."
    },
    "title_strip_rig": {
        "fr": "Convertir en accessoire rigide",
        "en": "Convert to rigid accessory"
    },
    "msg_strip_rig_done": {
        "fr": "Squelette et skinning supprimés avec succès !\nVous pouvez maintenant sélectionner l'os cible dans « Attacher à l'os » (ex: Bip01 Spine pour le dos du cheval) et cliquer sur « Attacher / Détacher ».",
        "en": "Skeleton and skinning successfully removed!\nYou can now select the target bone in 'Attach to bone' (e.g. Bip01 Spine for the horse's back) and click 'Attach / Detach'."
    },
    "msg_import_as_accessory": {
        "fr": "Ce fichier 3D ne contient aucun squelette d'animation.\n\nSouhaitez-vous l'importer comme accessoire rigide ?\n(Idéal pour les selles, armes, boucliers, casques à attacher sur un os sans déformation)\n\n• OUI : Accessoire rigide direct (aucun os ni skinning ajouté)\n• NON : Rigger sur le squelette humanoïde H_Male avec skinning automatique",
        "en": "This 3D file does not contain any animation skeleton.\n\nDo you want to import it as a rigid accessory?\n(Ideal for saddles, weapons, shields, helmets to attach to a bone without deformation)\n\n• YES: Direct rigid accessory (no bones or skinning added)\n• NO: Rig onto H_Male humanoid skeleton with automatic skinning"
    },
    "title_import_mesh_type": {
        "fr": "Type d'équipement",
        "en": "Equipment Type"
    },
    "msg_import_mesh_fail": {
        "fr": "Import du mesh échoué : ",
        "en": "Mesh import failed: "
    },

    # Menus
    "menu_import_mixamo": {
        "fr": "Importer un Character Mixamo (FBX)...",
        "en": "Import Mixamo Character (FBX)..."
    },
    "menu_import_mesh": {
        "fr": "Importer un mesh (FBX / OBJ)...",
        "en": "Import mesh (FBX / OBJ)..."
    },
    "menu_import_anim": {
        "fr": "Importer une animation FBX Mixamo...",
        "en": "Import Mixamo FBX animation..."
    },
    "menu_refresh_models": {
        "fr": "Rafraîchir la liste des modèles",
        "en": "Refresh models list"
    },
    "menu_collapse": {
        "fr": "Réduire",
        "en": "Collapse"
    },
    "menu_expand": {
        "fr": "Développer",
        "en": "Expand"
    },
    "menu_auto_rig": {
        "fr": "⚡ Convert/create rig to UO (Rig Studio)...",
        "en": "⚡ Convert/create rig to UO (Rig Studio)..."
    },
    "menu_play_anim": {
        "fr": "Lire l'animation",
        "en": "Play animation"
    },
    "menu_load_creature_model": {
        "fr": "Charger le modèle de la créature",
        "en": "Load creature model"
    },
    "menu_load_replace_scene": {
        "fr": "Charger (remplacer la scène)",
        "en": "Load (replace scene)"
    },
    "menu_add_to_scene": {
        "fr": "Ajouter à la scène (équipement)",
        "en": "Add to scene (equipment)"
    },
    "menu_manage_textures": {
        "fr": "Assigner / Modifier les textures...",
        "en": "Assign / Modify textures..."
    },
    "menu_rename": {
        "fr": "Renommer...",
        "en": "Rename..."
    },
    "menu_move_to_custom": {
        "fr": "Déplacer vers 'Meshs custom'",
        "en": "Move to 'Custom meshes'"
    },
    "menu_move_to_others": {
        "fr": "Déplacer vers 'Others'",
        "en": "Move to 'Others'"
    },
    "menu_extract_parts": {
        "fr": "Extraire des pièces vers 'Meshs custom'...",
        "en": "Extract parts to 'Custom meshes'..."
    },
    "menu_del_mesh": {
        "fr": "Supprimer ce mesh",
        "en": "Delete this mesh"
    },
    "menu_del_char": {
        "fr": "Supprimer ce character",
        "en": "Delete this character"
    },
    "menu_remove_last_mesh": {
        "fr": "Retirer le dernier mesh",
        "en": "Remove last mesh"
    },
    "menu_clear_scene": {
        "fr": "Vider la scène",
        "en": "Clear scene"
    },
    "menu_assign_tex": {
        "fr": "Assigner une texture...",
        "en": "Assign texture..."
    },
    "menu_remove_tex": {
        "fr": "Retirer la texture",
        "en": "Remove texture"
    },
    "menu_manage_all_tex": {
        "fr": "Gérer toutes les textures du modèle...",
        "en": "Manage all model textures..."
    },
    "menu_remove_meshes_fmt": {
        "fr": "Retirer ces {0} meshes de la scène",
        "en": "Remove these {0} meshes from scene"
    },
    "menu_remove_mesh": {
        "fr": "Retirer ce mesh de la scène",
        "en": "Remove this mesh from scene"
    },

    # Textures Dialog
    "tex_dlg_title_fmt": {
        "fr": "Textures du Character : {0}",
        "en": "Character Textures: {0}"
    },
    "tex_dlg_model_info_fmt": {
        "fr": "Modèle : {0}  ({1} mesh{2})",
        "en": "Model: {0}  ({1} mesh{2})"
    },
    "tex_dlg_subtitle": {
        "fr": "Assignez une texture BaseColor / Diffuse (PNG, TGA, JPG, BMP) à chaque mesh du modèle, ou extrayez-les d'un FBX.",
        "en": "Assign a BaseColor / Diffuse texture (PNG, TGA, JPG, BMP) to each mesh, or extract them from an FBX."
    },
    "tex_dlg_preview_header": {
        "fr": "Aperçu de la texture :",
        "en": "Texture preview:"
    },
    "tex_col_mesh": {
        "fr": "Mesh",
        "en": "Mesh"
    },
    "tex_col_material": {
        "fr": "Matériau FBX",
        "en": "FBX Material"
    },
    "tex_col_texture_file": {
        "fr": "Fichier Texture",
        "en": "Texture File"
    },
    "tex_col_action": {
        "fr": "Action",
        "en": "Action"
    },
    "tex_col_browse": {
        "fr": "Parcourir...",
        "en": "Browse..."
    },
    "tex_col_clear": {
        "fr": "Retirer",
        "en": "Remove"
    },
    "btn_apply_save": {
        "fr": "Appliquer et Enregistrer",
        "en": "Apply and Save"
    },
    "btn_extract_fbx_tex": {
        "fr": "Extraire textures d'un FBX...",
        "en": "Extract textures from FBX..."
    },
    "btn_open_model_folder": {
        "fr": "Ouvrir dossier modèle...",
        "en": "Open model folder..."
    }
}

target_dirs = [
    r'c:\Games\Sphere\IrisGrannyViewer\UoFiddler.Plugin.Granny3D\Languages',
    r'c:\Games\Sphere\IrisGrannyViewer\UOFiddler-master\UoFiddler.Plugin.Granny3D\Languages'
]

for d in target_dirs:
    if not os.path.exists(d):
        continue
    fr_file = os.path.join(d, 'fr.json')
    en_file = os.path.join(d, 'en.json')

    with open(fr_file, 'r', encoding='utf-8') as f:
        fr = json.load(f)
    with open(en_file, 'r', encoding='utf-8') as f:
        en = json.load(f)

    for k, val in new_translations.items():
        fr[k] = val['fr']
        en[k] = val['en']

    with open(fr_file, 'w', encoding='utf-8') as f:
        json.dump(fr, f, ensure_ascii=False, indent=2)
    with open(en_file, 'w', encoding='utf-8') as f:
        json.dump(en, f, ensure_ascii=False, indent=2)

    print(f'Updated {d}: FR={len(fr)} keys, EN={len(en)} keys')
