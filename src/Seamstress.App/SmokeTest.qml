import QtQuick

// Automated check run with "--smoke": drives the application through the same calls the mouse and menus
// make, so a broken connection between the interface and the C# side shows up without anyone clicking.
QtObject {
    id: smoke
    property string folder: ""
    property int failures: 0

    function check(what, ok) {
        if (!ok) failures++
        App.log((ok ? "  ok    " : "  FAIL  ") + what)
    }

    function drag(x0, y0, x1, y1) {
        App.pointerDown(x0, y0, 1, false)
        for (let i = 1; i <= 8; i++)
            App.pointerMove(x0 + (x1 - x0) * i / 8, y0 + (y1 - y0) * i / 8, 1)
        App.pointerUp(x1, y1)
    }

    function scene() { return JSON.parse(App.patternScene()) }
    function selection() { return JSON.parse(App.selection()) }

    function run() {
        App.log("smoke test")
        App.newDocument()
        check("starts empty", scene().pieces.length === 0)

        // Draw two rectangles and sew them.
        App.tool = "Rectangle"
        drag(0, 0, 40, 60)
        drag(60, 0, 100, 60)
        check("two pieces drawn", scene().pieces.length === 2)
        App.tool = "Sew"
        App.pointerDown(40, 30, 1, false); App.pointerUp(40, 30)
        check("first edge pending", scene().pending !== undefined)
        App.pointerDown(60, 30, 1, false); App.pointerUp(60, 30)
        check("seam created", scene().seams.length === 1)
        // Shift in Edit Points snaps to the squares the 2D view is showing.
        check("2D grid step reaches the editor", Math.abs(App.patternGridStep - patternView.gridLevel()[0]) < 1e-6)
        check("seam selected", selection().kind === "seam")
        App.reverseSeam()
        check("seam reversed", scene().seams[0].reversed === true)

        // Move a piece, then undo and redo the move.
        App.tool = "Select"
        const before = scene().pieces[0].label[0]
        drag(20, 30, 10, 30)
        check("piece moved", Math.abs(scene().pieces[0].label[0] - (before - 10)) < 0.01)
        App.undo()
        check("undo restores position", Math.abs(scene().pieces[0].label[0] - before) < 0.01)
        App.redo()
        check("redo moves again", Math.abs(scene().pieces[0].label[0] - (before - 10)) < 0.01)

        // Polygon tool and point editing.
        App.tool = "Polygon"
        for (const p of [[0, 80], [30, 80], [30, 110], [0, 110]]) { App.pointerDown(p[0], p[1], 1, false); App.pointerUp(p[0], p[1]) }
        App.confirmTool()
        check("polygon piece added", scene().pieces.length === 3)
        App.tool = "EditPoints"
        App.doubleClick(15, 80, 1, false)
        check("point inserted on edge", scene().pieces[2].points.length === 10)
        drag(15, 80, 15, 70)
        check("point dragged", Math.abs(scene().pieces[2].points[3] - 70) < 0.01)
        drag(30, 95, 36, 95)
        check("edge curved", scene().pieces[2].controls.length === 1)

        // Properties.
        App.tool = "Select"
        App.pointerDown(-5, 30, 1, false); App.pointerUp(-5, 30)
        check("piece selected", selection().kind === "pieces")
        App.setPreset("Silk")
        check("preset applied", selection().physics.preset === "Silk")
        App.setPieceValue("bend", 77)
        check("physics edited", selection().physics.bend === 77 && selection().physics.preset === "Custom")
        App.setMaterialColor("#ff8800")
        check("colour changed", selection().material.color === "#ff8800")
        const faces = selection().garment.faces
        App.setParticleDistance(1)
        check("more faces at smaller particle distance", selection().garment.faces > faces * 2)
        App.setParticleDistance(2)

        // Topology, and moving a piece with the 3D handles.
        const triangles = selection().garment.faces
        App.setTopology("Quads")
        check("quad topology", selection().garment.topology === "Quads" && selection().garment.quads > 0 && selection().garment.faces < triangles)
        App.setTopology("Triangles")
        check("back to triangles", selection().garment.faces === triangles)
        App.showWire = true
        check("wire overlay", JSON.parse(App.viewportModels()).wire !== "")
        App.showWire = false
        const startX = selection().piece.posX
        check("handles appear for the selection", JSON.parse(App.viewportModels()).gizmo !== undefined)
        check("handle drag begins", App.gizmoBegin())
        App.gizmoMove(0.1, 0, 0, false); App.gizmoMove(0.25, 0, 0, false)
        App.gizmoEnd()
        check("piece moved in 3D", Math.abs(selection().piece.posX - (startX + 0.25)) < 1e-4)
        App.gizmoBegin(); App.gizmoRotate("y", 90); App.gizmoEnd()
        check("piece rotated in 3D", Math.abs(selection().piece.rotY - 90) < 0.1)
        App.undo(); App.undo()
        check("3D moves undo", Math.abs(selection().piece.posX - startX) < 1e-4 && Math.abs(selection().piece.rotY) < 0.1)
        // Scale handles: the pattern grows, as one undo step, and the handle modes switch.
        const areaBefore = selection().piece.area
        App.gizmoBeginScale(); App.gizmoScale("u", 1.4, false); App.gizmoScale("u", 2, false); App.gizmoEnd()
        check("piece scaled in 3D", Math.abs(selection().piece.area - areaBefore * 4) < areaBefore * 0.01)
        App.undo()
        check("scale undone", Math.abs(selection().piece.area - areaBefore) < areaBefore * 0.001)
        App.gizmoMode = "Scale"
        check("handle mode switches", App.gizmoMode === "Scale")
        App.gizmoMode = "Full"
        // Shift on a move arrow: the piece lands on a multiple of the snap step.
        App.gizmoBegin(); App.gizmoMove(0.133, 0, 0, true); App.gizmoEnd()
        const step = App.snapStepCm / 100, snappedX = selection().piece.posX
        check("3D move snaps to the grid", snappedX !== startX && Math.abs(snappedX / step - Math.round(snappedX / step)) < 1e-3)
        App.undo()

        // Layers.
        App.selectAll()
        App.groupSelection()
        let rows = JSON.parse(App.layers())
        check("group created", rows.filter(r => r.kind === "group").length === 1)
        App.rename(rows[0].id, "Test Garment")
        check("garment renamed", JSON.parse(App.layers())[0].name === "Test Garment")
        App.setVisible(rows[0].id, false)
        check("hidden garment leaves the 2D view", scene().pieces.length === 0)
        App.undo()
        check("undo shows it again", scene().pieces.length === 3)

        // UV view.
        App.uvMode = true
        check("uv mode", scene().mode === "uv" && scene().pieces.length === 3)
        App.uvMode = false

        // Transform Points: box-select two corners of the first rectangle and slide them along X with the arrow.
        App.tool = "TransformPoints"
        const corner = scene().pieces[0].points
        const right = Math.max(corner[0], corner[2], corner[4], corner[6]), low = Math.min(corner[1], corner[3], corner[5], corner[7])
        const high = Math.max(corner[1], corner[3], corner[5], corner[7])
        drag(right - 3, low - 3, right + 3, high + 3)
        const handles = scene().pointGizmo
        check("points box-selected", handles !== undefined && scene().pieces[0].selectedPoints.length === 2)
        // Tolerance 1 means a pixel is 1/9 cm here, so 3 cm from the centre is on the X arrow.
        drag(handles[0] + 3, handles[1], handles[0] + 8, handles[1] + 2)
        const after = scene().pieces[0].points
        check("points moved along X only", Math.abs(Math.max(after[0], after[2], after[4], after[6]) - (right + 5)) < 0.01
              && Math.abs(Math.min(after[1], after[3], after[5], after[7]) - low) < 0.01)
        App.undo()
        App.tool = "Select"

        // Shift draws a square, and the sides are measured while drawing.
        App.tool = "Rectangle"
        App.constrain = true
        App.pointerDown(200, 0, 1, false)
        App.pointerMove(230, 12, 1)
        const sides = scene().draftMeasures
        check("shift draws a square", sides && sides.length === 6 && Math.abs(sides[2] - 30) < 0.01 && Math.abs(sides[5] - 30) < 0.01)
        App.constrain = false
        check("releasing shift frees the shape", Math.abs(scene().draftMeasures[5] - 12) < 0.01)
        App.cancelTool()
        App.tool = "Select"
        check("version reported", /^\d+\.\d+\.\d+/.test(App.version))

        // Double-clicking a piece selects all of its points, in Transform Points.
        const shape = scene().pieces[1]
        App.doubleClick(shape.label[0] + 3, shape.label[1] + 3, 1, false)
        check("double-click selects every point of the piece", App.tool === "TransformPoints"
              && scene().pieces[1].selectedPoints !== undefined && scene().pieces[1].selectedPoints.length === shape.points.length / 2)
        App.tool = "Select"

        // Ruler in 2D: both ends catch on opposite corners of the second rectangle, 40 × 60.
        App.tool = "Ruler"
        const box = scene().pieces[1].points
        drag(box[0] + 0.4, box[1] - 0.3, box[4] - 0.3, box[5] + 0.4)
        const ruler = scene().ruler
        check("2D ruler measures between corners", ruler !== undefined && Math.abs(ruler[4] - Math.hypot(40, 60)) < 0.01)
        App.pointerDown(100, 100, 1, false); App.pointerUp(100, 100)
        check("a click clears the ruler", scene().ruler === undefined)
        App.tool = "Select"

        // Orthographic 3D view and the right-click menu.
        viewport.orthographic = true
        check("orthographic camera", viewport.activeCamera !== null && viewport.magnification > 0)
        viewport.orthographic = false
        App.selectIn3D("", false)
        App.contextSelect(shape.label[0], shape.label[1])
        check("right-click selects the piece under it", selection().kind === "pieces")
        contextMenu.popup()
        check("context menu opens", contextMenu.visible)
        contextMenu.close()

        // Normals: shown in the 3D view, inverted from Properties as one undo step.
        viewport.showNormals = true
        check("normal direction shown", viewport.showNormals)
        viewport.showNormals = false
        check("normals start as drawn", selection().piece.flippedNormals === 0)
        App.invertNormals()
        check("invert normals", selection().piece.flippedNormals === selection().piece.count && App.undoText === "Undo Invert normals")
        App.undo()
        check("invert normals undone", selection().piece.flippedNormals === 0)

        // Cut Hole: a small rectangle drawn on the second one, both selected, cut from the right-click menu.
        App.tool = "Rectangle"
        drag(70, 20, 90, 40)
        App.tool = "Select"
        App.pointerDown(80, 30, 1, false); App.pointerUp(80, 30)
        App.pointerDown(65, 10, 1, true); App.pointerUp(65, 10)
        check("pieces to cut selected", selection().kind === "pieces" && selection().count === 2)
        let cutItem = null
        for (let i = 0; i < contextMenu.count; i++) {
            if (contextMenu.itemAt(i) && contextMenu.itemAt(i).text === "Cut Hole")
                cutItem = contextMenu.itemAt(i)
        }
        check("context menu offers Cut Hole", cutItem !== null)
        cutItem.triggered()
        const holed = scene().pieces.filter(p => p.holes !== undefined)
        check("hole cut", holed.length === 1 && holed[0].holes[0].length === 8 && scene().pieces.length === 5
              && App.undoText === "Undo Cut hole" && selection().count === 1)
        check("hole points shown for editing", holed[0].points.length === 16)

        // Send to MakeHuman suggests the garment's name and says where the asset would go (nothing is written here).
        check("send dialog suggests the garment name", App.activeGarmentName() !== "")
        const info = JSON.parse(App.makeHumanAssetInfo("Summer Skirt/v2"))
        check("send dialog names the asset folder", info.folder === "" || /summer_skirt_v2$/.test(info.folder))
        check("licences offered", App.makeHumanLicenses().split("|").indexOf("CC-BY") >= 0 && App.settingMakeHumanLicense !== "")

        // Languages offered in Preferences, and the texts of the one in use.
        const languages = JSON.parse(App.languagesJson())
        check("languages offered", languages.length === 6 && languages.some(l => l.code === "ja") && App.settingLanguage === languages[0].code)
        check("interface texts", window.tr("Simulate") === "Simulate" && window.tr("view|Front") === "Front")
        // Switching (for this run only) re-reads the texts on both sides; the menus and panels follow through tr().
        App.showLanguage("fr")
        check("language switch", window.tr("Simulate") === "Simuler" && App.undoText.indexOf("Annuler") === 0
              && JSON.parse(App.keyBindingsJson())[0].group === "Fichier" && window.title.indexOf("Sans titre") >= 0 === !App.hasFile)
        App.showLanguage("en")
        check("language switched back", window.tr("Simulate") === "Simulate" && App.undoText.indexOf("Undo") === 0)

        // Keyboard shortcuts come from the registry, and a key on two commands is reported.
        const bindings = JSON.parse(App.keyBindingsJson())
        check("shortcut registry", bindings.length > 40 && App.keyText("tool.ruler") !== "" && bindings.every(b => window.commands[b.id] !== undefined))
        check("shortcut conflicts reported", JSON.parse(App.keyConflicts(JSON.stringify({ "tool.select": ["M"], "tool.ruler": ["m"] }))).length === 1)

        // Templates, a second garment and per-garment simulation.
        App.addTemplate("Skirt")
        rows = JSON.parse(App.layers())
        const garments = rows.filter(r => r.kind === "garment")
        check("second garment added and active", garments.length === 2 && garments[1].active)
        App.toggleSimulation()
    }

    // Runs after the simulation had a moment to work.
    function finish() {
        App.tick()
        check("simulation running", App.simulating)
        check("only the skirt is simulated", JSON.parse(App.layers()).filter(r => r.kind === "garment" && r.simulating).length === 1)
        App.toggleSimulation()
        check("simulation stopped", !App.simulating)
        App.selectAll()
        check("skirt draped", selection().piece.draped === true)

        // Pinching with no simulation running starts one for that garment.
        const skirtPiece = JSON.parse(App.layers()).filter(r => r.kind === "piece" && r.name === "Skirt Front")[0].id
        check("pinch takes hold", App.pinchBegin(skirtPiece, 0, 0.45, 0.16))
        check("pinch runs the simulation", App.simulating)
        App.pinchMove(0, 0.5, 0.3)
        App.pinchEnd()
        App.toggleSimulation()
        check("pinch is one undo step", App.undoText === "Undo Pinch cloth")
        App.selectLayer(JSON.parse(App.layers())[0].id, false)
        check("other garment untouched", selection().piece.draped === false)

        const base = folder + "/smoke"
        check("save", App.saveAs(base + ".garment.json") && !App.dirty)
        check("export obj", App.exportTo("obj", base + ".obj", true, false))
        check("export glb", App.exportTo("gltf", base + ".glb", true, false))
        check("export fbx", App.exportTo("fbx", base + ".fbx", true, true))
        App.newDocument()
        check("reopen", App.open(base + ".garment.json") && scene().pieces.length === 7)
        check("reopened with its hole", scene().pieces.filter(p => p.holes !== undefined).length === 1)
        check("viewport lists the pieces", JSON.parse(App.viewportModels()).models.length === 7)
        App.setAvatarShape("gender", 0.9)
        check("avatar body setting stored", Math.abs(JSON.parse(App.avatarShape()).gender - 0.9) < 1e-6)
        App.resetAvatarShape()
        check("avatar body reset", JSON.parse(App.avatarShape()).gender === 0.5)
        App.log(failures === 0 ? "smoke test passed" : "smoke test FAILED: " + failures)
    }
}
