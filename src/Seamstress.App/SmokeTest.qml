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
        App.doubleClick(15, 80, 1)
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
        App.gizmoMove(0.1, 0, 0); App.gizmoMove(0.25, 0, 0)
        App.gizmoEnd()
        check("piece moved in 3D", Math.abs(selection().piece.posX - (startX + 0.25)) < 1e-4)
        App.gizmoBegin(); App.gizmoRotate("y", 90); App.gizmoEnd()
        check("piece rotated in 3D", Math.abs(selection().piece.rotY - 90) < 0.1)
        App.undo(); App.undo()
        check("3D moves undo", Math.abs(selection().piece.posX - startX) < 1e-4 && Math.abs(selection().piece.rotY) < 0.1)

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
        check("reopen", App.open(base + ".garment.json") && scene().pieces.length === 5)
        check("viewport lists the pieces", JSON.parse(App.viewportModels()).models.length === 5)
        App.setAvatarShape("gender", 0.9)
        check("avatar body setting stored", Math.abs(JSON.parse(App.avatarShape()).gender - 0.9) < 1e-6)
        App.resetAvatarShape()
        check("avatar body reset", JSON.parse(App.avatarShape()).gender === 0.5)
        App.log(failures === 0 ? "smoke test passed" : "smoke test FAILED: " + failures)
    }
}
