# Face Studio roadmap: toward a full face maker

Status 2026-11-08. Nothing below has been checked in the game yet. Steps are ordered by how much likeness they add per unit of work.

## Done

- Sculpt (move, grab, proportions, smooth) across head, eyeballs, teeth and hair together; undo/redo; projects.
- UV texture wrap (same-size replacement), front/side reference images.
- **Save as .big**: one face package in the stock layout, saved where the user chooses (byte-identical writer, 348/348 stock packages). Export is file-only by design: no FaceID, no game-folder changes.
- Authored normals preserved on export; correct texture mapping in the preview.
- Phase 1 face texture tools: full-atlas import with resize, 5-landmark photo projection with skin-tone matching, skin tone, eye colour. Checked by 3D renders on stock heads 10942 and 1188 and on gill.big, and by a .big export decoded back (DXT1 error 2.9/255).

- **Likeness fit** (head shape from a front and a side photo): 24 landmarks in atlas space, photo alignment by scale+offset, biharmonic RBF deformation with neck and skull-back held, symmetric option. On stock heads, fitting 1188 to photos of 10942 cut the landmark shape difference from 0.205 to 0.067 units (simulated perfect marker placement).

- **Hair for real players**: 8 stock heads with hair meshes as templates (6 full hair, 2 fringe; 2786's `hair` part is glasses), Volume brush (normal inflate, seam-welded, outward-oriented), hair recolour (relative brightness, contrast softened when lightening, alpha untouched) and hair texture replacement/resize in the texture window's hair mode. Saved in the face .big by the in-place writer. **Add hair from a template** for heads without a hair mesh: UV-based shape transfer onto a hair template (landmarks match to 0.002 units on stock heads), hair and eyeballs follow via RBF, neck blended to the template.

- **Pin warp** on the 3D head (Core/Textures/PinWarp.cs, Wendland C2 RBF, exact at pins, zero beyond reach, reach widened to avoid folds): the answer to texture features not sitting on the mesh features; replaced liquify (removed 2026-11-09 with Darken and Lighten; Paint now keeps texture detail, I = eyedropper).
- **Texture painting and alignment**: soft round brush (paint, darken, lighten, blur, clone, pick) on the face and hair textures, in the texture window (strokes stored normalised and replayed over the other edits) and directly on the 3D head (UV from the hit triangle's barycentric weights, live preview, seam-aware strokes). Move/scale/rotate plus a five-marker thin-plate warp to line an imported atlas up with the head (a 10/6 px misplaced face came back to 0.0 mean error in the feature area). Sculpt and paint share one undo history.

## Facts that shape the plan

- A face package is `model.o` + `textures.fsh`. Stock heads have no hair mesh: hair is painted on the face texture. Custom faces such as gill.big carry a hair layer (`grid`) inside `model.o`.
- All face atlases share one layout: unwrapped face in the centre, eyeball pictures bottom-left, mouth/teeth bottom-right. Landmarks sit at the same normalised positions at 256 and 512.
- Every head draws its eyeballs from the bottom-left strip: a separate eyeball part on 10942/21449/gill, triangles inside the main part on 1188/10407. The painted iris can sit a few pixels from the eyeball's front vertex (1188), so the eye tool snaps to the painting.
- Community faces are often finished atlases (512 or 1254 px), not photos. They lose detail on a 256 stock head until phase 2.
- Stock face textures are 256×256; gill.big uses 512×512 plus a 256×256 hair texture.
- Heads are found by FaceID (`playermanager.xml` location → `players\heads\<id>[z].big`). `z` is the sun-cream variant used in day matches, not an LOD; `lhead.big`/`mhead.big` are the shared LODs.
- `hhr*.o` hair templates belong to the created-player gameface system, not to FaceID heads.

## Next phases

1. **Face texture window: removed (2026-11-09).** Photo projection, skin tone, eye colour and feature warp are gone (code and tests deleted); Pin warp and Texture paint on the 3D head cover face texture work.
2. **Texture resolution change: done in the tool, not checked in game.** Texture size: a texture loaded with Wrap UV texture keeps its own size (e.g. 512); the face and hair texture windows (and their size choice) were removed 2026-11-09; export rebuilds `textures.fsh` at that size with `FshTextureEditor.Rebuild` (same DXT1 format and mip count, EA's 16 padding bytes, 0x69/0x70 attachments verbatim, offsets shifted). A 512 package on stock 1188 reopens at 512 with 2.3/255 DXT error. Open risk: whether the game accepts 512 (gill.big suggests yes) and 1024 (no evidence).
3. **Automatic landmarks: done for the front photo.** MediaPipe Face Mesh V2 (ONNX, Apache 2.0, `vendor/facemesh`, SHA-256 checked on load) via ONNX Runtime 1.20.1; full-frame crop search plus two eye-levelled refinements; mesh points per landmark calibrated on rendered faces. Marker error 2.4–4.5% of eye distance (jaw angles and cheekbones worst, ~6–9%); a fit from auto front markers reached 0.205→0.131 vs 0.067 with perfect markers, so users should still check markers. Side (profile) markers stay manual: the model does not handle true profiles. Not yet tried on real photographs.
4. **Named sliders.** Jaw width, nose length, eye spacing, etc. as brush presets anchored by UV position (the face atlas layout is shared), so they work on every template without per-mesh tagging.
5. **Hair inside the face .big.** Merging a hair mesh into `model.o` changes the ELF layout, which the current patch-in-place writer cannot do. Probe the OTools import route on a head (gill.big's `grid` layer shows community faces are built that way) and check whether skin weights survive.
