Face Studio for EA Cricket 07
=============================

Make and edit player faces for EA SPORTS Cricket 07 (PC) and save them as a face .big file.

Requirements
  Windows 10 or 11, 64-bit. Nothing to install: unzip anywhere and run Cricket07.FaceStudio.exe.
  (Keep the folder together; the program needs the "tools" and "templates" folders next to it.)

Getting started
  1. Pick a stock head from the list and choose "Start with selected head", or "Open face (.big / .o)"
     to edit an existing face package (for example a player's face .big from your game files).
  2. Sculpt the head (see below). Load front/side reference photos to compare.
  3. "Save as .big..." writes your face as one .big file (model.o + textures.fsh, the game's own layout)
     wherever you choose. Face Studio never changes your game folder: putting the face into the game is up
     to you. Back up the original file you replace.

Sculpting
  - Left drag on the head: grab and move the area under the cursor (Brush radius / strength sliders).
  - X/Y/Z buttons, Smooth region, Feature proportions: precise edits around the clicked spot.
  - Single vertex (V): left drag moves just the point under the cursor (a yellow dot shows which).
    Points along texture seams move together; with Mirror on, the other side moves too.
  - Show wireframe (W): see the mesh edges.
  - Likeness from photos: fits the head shape to a front and a side photo (front markers are placed
    automatically; check them).
  - Hair: Add hair from a template (for heads with painted hair), Volume +/-, grab and smooth the hair.

Texture work (on the 3D head)
  - Paint (keeps the skin/hair detail), Blur, Clone (Ctrl+click sets the source).
    Alt or I picks the colour under the cursor. [ and ] change the brush size. Click the colour square for
    any colour.
  - Pin warp: drag a painted feature (moustache, beard edge, lip line, brow) to where it belongs on the
    mesh; pins can be moved again, Mirror pins does both sides.
  - Wrap UV texture...: put a prepared texture image on the selected part (a finished face texture, any
    square size; 256 is the game's own size, 512 looks sharper).

Navigation
  Right drag (or Alt + left drag on a touchpad): orbit. Add Shift to pan. Wheel or pinch: zoom at the cursor.
  Arrow keys orbit, F fits the head, Home = front view. Ctrl+Z / Ctrl+Y: undo / redo.
  View buttons are in the top-right corner of the 3D view.

Projects
  "Save project..." keeps your work (shape, textures, references) to continue later: open the same head
  again, then "Load project...".

Good to know
  - The game keeps a copy of the squads in saved matches and tournaments: check a new face in a NEW
    exhibition match.
  - Face textures larger than 256 x 256 work in the tool; check them in your game before sharing a face.
  - The tool checks every saved model by reading it back, but always test a face in the game.

Licence: MIT (LICENSE.txt). Credits and third-party licences: see THIRD-PARTY-NOTICES.txt. Face Studio is a fan-made tool, not affiliated with
Electronic Arts or HB Studios.
