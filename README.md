# Cricket 07 Face Studio

Standalone Windows desktop authoring tool. It has its own executable, projects, workspace and template library; Player Asset Manager does not host it.

## Start

Expand **Match proportions to photo** directly below **Show wireframe** in the main editor. Load a front or side reference and use **Move reference only**, Scale and Opacity to align it with the head. Enable **Sculpt with shape pins**, click the head to place a pin and drag it onto the matching photo feature. Other pins hold their spots. Mirror, neck lock and part locks apply. Each drag is one undo step. These controls edit geometry in the main viewport; reference photos guide the sculpt and are saved in the project. **Save as .big…** exports the resulting shape.

Double-click `Start-FaceStudio.cmd`, or run `dist/Cricket07.FaceStudio.exe`. Keep the entire dist folder together, in a writable location. The launcher uses the workspace's bundled .NET SDK when available; otherwise install the .NET 8 Windows Desktop runtime. OTools 0.179 is included with its license.

1. Select a head template (heads marked "with hair" carry a 3D hair mesh, "with hair fringe" a small front fringe; the others have hair painted on the face texture). Choose **Start with selected head**, or **Open face (.big / .o)** for a face package or uncompressed Cricket 07 ELF model. BIGF/BIG4 packages with root `model.o` and `textures.fsh` (or `textures`) members open directly; compressed members are rejected. Named layers are matched to decoded meshes, with conservative bounds that include the edited geometry.
2. Choose a mesh part. Click a feature such as the chin, nose, eyelid or jaw. Adjust radius and strength, then move on X/Y/Z or smooth the selected region. Undo/redo, optional symmetry and a bottom-Y neck lock are available. **Single vertex** (V) moves just the vertex under the cursor (a yellow dot shows which); its copies along texture seams move with it so the surface never tears, and with Mirror on its partner on the other side moves mirrored (a centre-line point stays on the centre line). **Show wireframe** (W) draws the mesh edges on the head.
3. Use **Match proportions to photo** to compare and sculpt the head against front and side references. Adjust photo alignment first, then drag shape pins to match the nose, eye spacing, jaw and chin. Right-click a shape pin to remove its handle. The sculpted shape is preserved in projects and face packages.
   **Texture paint** tools (in the editor): *Paint* (sets the colour but keeps the texture's own fine detail, taken from the texture as it was when the stroke began), *Blur* and *Clone* (Ctrl+click sets the source). **Alt** (with Paint) or **I** takes the colour under the cursor and switches to Paint; a ring pops out around the cursor showing the new colour (top) and the previous one (bottom), then fades (in the 3D view Alt + left drag still orbits and puts the old colour back). **Brush size** is the brush diameter in screen pixels, shown by a circle cursor of exactly that size (each dab is converted to texture pixels where it lands, so the painted area matches the circle); **[** and **]** change it. Click the big colour square for the Windows colour picker (custom colours kept for the session).
   **Pin warp** (in Texture paint, the tool for lining a painted moustache, beard edge, lip line or brow up with the mesh): drag the painted feature to where it belongs on the head and a yellow pin stays there, with a dashed line back to where it came from. Click without dragging to pin a spot still (e.g. the eyes). *Mirror pins* (on by default, face texture only) also applies each pin to the other side of the face (faded rings); your own pins on the other side win. *Pin reach* sets how far around each pin the texture follows; the cursor outline and the selected pin's dashed outline show exactly that area on the surface (a long drag widens only that pin's reach). Over a pin the cursor becomes a hand: pressing grabs it. Pins move only their surroundings, can be dragged again any time, and are removed with Delete, *Remove pin* or *Clear pins*. The texture is always re-warped once from how it was before the first pin (smooth local RBF, bicubic sampling), so editing pins never smears or softens it. Undo also brings the previous pin layout back.
   **Texture paint** in the editor paints straight onto the 3D head: pick a tool under *Left drag on the head* and drag over the face (face texture) or a hair mesh (hair texture). The head shows the stroke as it is painted; the circle at the cursor is the brush (its diameter is the Brush size in screen pixels); with Pin warp the outline shows the pin reach on the surface. A stroke stops at texture seams; start again on the other side. Undo/redo steps back through paint and sculpt in the order they happened, . (The face and hair texture windows were removed 2026-11-09: Pin warp and Texture paint on the head replace them; Wrap UV texture… still loads a prepared texture image.)
   Load front/side references for visual comparison. **Wrap UV texture** applies a prepared BMP/TGA image to the existing UV layout. A matching `textures.fsh` must be beside the source .o when it is opened; replacement dimensions must match. Hair templates are geometry-only with neutral preview materials.
4. Save a `.c07face.json` project. To resume, open the same original head/hair, then load the project. Projects embed texture changes and references and identify sources by SHA-256.
5. **Save as .big…** saves the face you made as one `.big` file (`model.o` + `textures.fsh`, the stock face package layout) wherever you choose. Before writing, the model is decoded again and checked; those checks run in a scratch folder that is removed afterwards. The package writer reproduces all 348 stock face packages byte-for-byte. Original assets remain untouched, and nothing is written to the game folder. A separately chosen hair template is not included: a face .big holds one model.

Sculpt brushes (move, grab, proportions) deform every loaded part with the same falloff, so eyeballs, teeth and hair follow the eyelids, lips and scalp. Smoothing applies to the selected part and welds UV-seam duplicates so seams stay closed.

The head templates come from local stock archives 1188, 10942, 21449 and 10407, plus the stock heads with a hair mesh: 14360, 1536, 23525, 28116, 6059, 8881 (full hair) and 15484, 9324 (front fringe). Each was checked by rendering its extra part alone: 2786's `hair` part is glasses, so it is not offered. The created-player hair meshes hhraa_a through hhran_a stay in the catalog for tests but are not offered on the start screen, because a face .big holds one model. The catalog records original paths and hashes. These locally extracted EA assets are not a redistributable asset license; distribute the application source and extraction script separately from game assets.

## Texture workspace

Scan the game folder, search a filename, archive path, image tag or hash, and optionally assign descriptive aliases such as `india_kit_2007`. Names cannot be inferred reliably from hashed filenames. Select an image, import a same-size BMP/TGA, inspect the encoded preview and export the containing file outside the game folder.

The JSON catalog indexes loose SHPI files and supported BIGF/BIG4 archive members. Writes support BGRA8888, BGR888, DXT1/3/5 and supported mip chains. Export preserves the container layout and unrelated bytes, checks the source hash, and writes a manifest. The DXT encoder is basic; review compression quality. Compressed/unknown payloads are searchable read-only records. RefPack/QFS rebuilding is not implemented, and the scan does not claim to decode textures hidden inside unsupported compressed archives.

## .o compatibility approach

OTools decodes a private source copy and produces a structural dump. `NativeModelPatch` validates ELF32 little-endian section bounds and uniquely matches vertex buffers against decoded positions/normals. It modifies only mapped positions, normals and validated model/layer bounding fields. Vertex order, indices, UVs, rig data, skin weights, materials, relocations and all other original bytes remain intact. Unedited export is byte-identical. Authored normals are kept; a normal is rotated only where the surface around it changed, with seam duplicates welded by position so both sides of a UV seam stay equal (a sample sculpt on head 1188 changes 165 bytes instead of 6,466).

The preview maps texture coordinates absolutely (0..1 over the whole atlas). WPF's default maps them relative to each part's own UV range, which showed skin on the eyeball part because it uses only the eye strip at the bottom of the face atlas. Mesh interiors are drawn dark.

This deliberately avoids rebuilding a skinned head through generic glTF import: local probes showed that path loses skin data. Export independently reopens the native result and checks geometry, UVs, indices, texture bindings and auxiliary attributes. Unsupported morph data, ambiguous buffer mappings, transformed nodes and unsupported multi-model layouts are rejected. Named layers are matched to decoded meshes, with conservative bounds that include the edited geometry.

Head and hair remain separate models, not an automatically combined player asset. The tool only saves files you choose; it does not edit `.ros` or `playermanager.xml`, assign a face to a player, or install anything. It also does not provide photo projection, landmark fitting, named anatomical sliders, rig editing, topology changes or all-LOD generation. Reference pictures guide manual sculpting; UV wrapping expects a prepared texture. In-game appearance, animation, seams, clipping and LOD behavior still need verification.

## Validation

- Release build: zero warnings/errors.
- 59 automated tests pass (unit), including real installation texture samples, paint brushes and texture alignment, archive preservation, source-change rejection, TGA decoding, sculpting and native patch bounds.
- All 18 templates pass byte-identical no-edit export and decoder reopening.
- WPF smoke checks construct the windows and exercise sculpt movement, undo/redo, project save/load and edited head export.
- Local installation scan: 3,101 records; 1,441 replacement-eligible, 1,653 opaque/compressed, 4 truncated, 3 unsupported.
- No game or roster files were modified. These are file-level and UI checks, not game-level validation.

## Build and architecture

`src/FaceStudio.App` contains the independent WPF interface and OTools orchestration. `src/FaceStudio.Core` contains model parsing, native patching, sculpt brushes, texture codecs, scanning and export. `tests/FaceStudio.Tests` tests binary safety; `tools/FaceStudio.UiSmoke` exercises the actual local templates and UI. `tools/Import-LocalTemplates.ps1` builds the template catalog from local assets.

From the repository root:

```powershell
& ./tools/dotnet-sdk/dotnet.exe build ./FaceStudio/FaceStudio.sln -c Release
$env:C07_FACE_STUDIO_CATALOG = "$PWD/FaceStudio/data/target-catalog.json"
& ./tools/dotnet-sdk/dotnet.exe test ./FaceStudio/FaceStudio.sln -c Release --no-restore
& ./tools/dotnet-sdk/dotnet.exe run --project ./FaceStudio/tools/FaceStudio.UiSmoke -c Release --no-restore -- "$PWD/FaceStudio"
& ./tools/dotnet-sdk/dotnet.exe publish ./FaceStudio/src/FaceStudio.App -c Release -r win-x64 --self-contained false -o ./FaceStudio/dist
```

OTools upstream: https://github.com/FIFAM-Mods/otools/releases/tag/0.179 . Its MIT license is retained under `dist/tools/otools/otools-release-0179`. Release archive SHA-256: `7E9C820239FF80063B8F6A7BD7C6E56F3D32F953D6783AEAE845569D1F890E5D`.


## Hair

Hair belongs to the head model, so it is saved inside the face `.big`.
- **Shape**: in the editor's Hair section, **Select hair** picks the hair mesh. **Volume +** / **Volume −** make it fuller or flatter around the clicked spot (brush radius and strength), or all of it with **Whole hair**. Grab (left drag) on the hair changes its outline and length, and **Smooth region** evens it out. Likeness fitting moves the hair with the head.
- **Colour and texture**: paint the hair mesh directly with Texture paint (Paint keeps the strands), or load a prepared hair texture image with **Wrap UV texture…** with the hair part selected.
- **Add hair to a head that has none** (hair painted on the face texture, e.g. Virat): **Add hair from a template…** in the Hair section. Choose one of the 8 hair templates; that template head takes this head's shape (each template vertex moves to the point drawn at the same face-texture position, which matches facial landmarks exactly) and its face texture at full size, and keeps its hair mesh, which follows the new skull. The neck blends back to the template's so it still joins the body. The result opens in a new editor (suggesting the original file name for Save as .big); the original head is not changed. The saved head uses the template's mesh reshaped to the player, not the player's original mesh. On some heads part of the hairline is also painted on the face texture and keeps its colour unless you edit the face texture too.
- Not checked in game: how edited hair looks there (transparency, sorting, caps and helmets).

## Navigation

- **3D editor**: left drag sculpts. Right drag (or Alt + left drag on a touchpad) orbits; add Shift to pan. Wheel or touchpad pinch zooms at the cursor. Arrow keys orbit in 15° steps (Shift + arrows pan), `F` fits the head, `Home` shows the front, `+`/`-` zoom, Ctrl+Z / Ctrl+Y undo and redo. The panel in the top-right corner of the 3D view has the same controls plus Front, ¾, Back, Left, Right and Focus (frames the clicked feature).
- **Photo, atlas and head panels** (likeness window and crop window): wheel or pinch zooms at the cursor, drag empty space (or right/middle drag) pans, double-click or `0` fits, `+`/`-` zoom; each panel has +, − and Fit buttons and shows its zoom. Markers stay the same size on screen at any zoom, so zoom in to place them precisely.

## Proportions and viewport references

- Middle-button drag orbits around the camera target with Y kept upright. Shift + middle-button drag pans. Wheel zooms. Front, Side and Fit restore orthographic views.
- Left-click selects the visible mesh part and brush center; left-drag grabs that region in the view plane. A drag is one undo step. Radius, symmetry and neck lock apply to the stroke.
- Feature proportions changes width (X), height (Y) and depth (Z) around the selected center with brush falloff. Set the three factors, then Apply. Values return to 1 after application. These are local geometry controls, not automatic anatomical landmarks.
- Import Front/Side reference pictures, then use Viewport reference to show or hide the corresponding image plane. Tick **Move reference only** and drag the photo in the 3D view to position it (the head is not selected or sculpted while ticked); set its size with Scale and its see-through amount with Opacity. Draw in front of model gives an overlay for alignment. Each picture has separate settings, saved with the project. Images keep their aspect ratio and are not baked into the exported game texture.
- This is manual reference matching, not full Blender parity: no landmark detection, camera calibration, automatic likeness fitting, or remeshing.

Interaction verification: 27 tests pass. The custom-face harness additionally checks orbit target preservation, upright axis, pan movement, proportion undo, reference plane creation and alignment save/load, followed by native package export. Actual mouse feel still benefits from user feedback.
