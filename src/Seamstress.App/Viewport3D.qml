import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import QtQuick3D
import QtQuick3D.Helpers

// 3D viewport: shows the avatar and every visible piece. Geometry comes from mesh files the C# side keeps
// up to date; this item only swaps the files it displays and handles the camera.
Item {
    id: root
    clip: true

    property string displayMode: "shaded"   // "shaded" or "checker"
    property bool wireframe: false
    onWireframeChanged: App.showWire = wireframe
    property string wireSource: ""
    property bool hasGizmo: false
    property vector3d gizmoOrigin: Qt.vector3d(0, 0, 0)
    property bool showGizmo: true
    // Distance of the scale boxes from the handle centre, in handle units (an arrow is 1 long).
    readonly property real scaleReach: App.gizmoMode === "Full" ? 1.2 : 0.85
    property bool pinchMode: false
    property bool showAvatar: true
    property bool showThreads: true
    property string avatarSource: ""
    property string threadSource: ""

    property real yaw: 25
    property real pitch: -8
    property real distance: 3.2
    property vector3d target: Qt.vector3d(0, 0.95, 0)

    // Orthographic: no perspective, so sizes can be compared across the garment. It frames the same area the
    // perspective camera sees at the orbit centre, so switching keeps the garment where it was.
    property bool orthographic: false
    readonly property real fieldOfView: 35
    readonly property real magnification: Math.max(1, view.height) / Math.max(1e-6, 2 * distance * Math.tan(fieldOfView * Math.PI / 360))
    readonly property var activeCamera: orthographic ? orthoCamera : camera

    // Right-click without dragging: the Edit menu.
    signal contextMenuRequested()

    // Ruler (the Ruler tool): two points on the cloth or the avatar, in metres. With only the first end placed,
    // the second follows the pointer until the next click.
    readonly property bool measuring: App.tool === "Ruler" && !App.uvMode
    property bool rulerPlaced: false
    property bool rulerOpen: false
    property vector3d rulerA: Qt.vector3d(0, 0, 0)
    property vector3d rulerB: Qt.vector3d(0, 0, 0)
    function clearRuler() { rulerPlaced = false; rulerOpen = false; rulerCanvas.requestPaint() }
    onMeasuringChanged: if (!measuring) clearRuler()
    function rulerText() {
        const cm = rulerA.minus(rulerB).length() * 100
        return App.imperial ? (cm / 2.54).toFixed(2) + " in" : cm.toFixed(1) + " cm"
    }

    ListModel { id: pieceModel }

    function refresh() {
        const data = JSON.parse(App.viewportModels())
        avatarSource = data.avatar
        threadSource = data.seams
        wireSource = data.wire
        hasGizmo = data.gizmo !== undefined
        if (hasGizmo)
            gizmoOrigin = Qt.vector3d(data.gizmo[0], data.gizmo[1], data.gizmo[2])
        const wanted = data.models
        // Update rows in place so unchanged pieces keep their GPU buffers.
        for (let i = pieceModel.count - 1; i >= 0; i--) {
            if (!wanted.some(m => m.id === pieceModel.get(i).pid))
                pieceModel.remove(i)
        }
        for (const m of wanted) {
            const row = { pid: m.id, meshUrl: m.source, tint: m.color, tex: m.texture, rep: m.repeat,
                          rough: m.roughness, metal: m.metallic, alpha: m.opacity, sel: m.selected }
            let found = false
            for (let i = 0; i < pieceModel.count; i++) {
                if (pieceModel.get(i).pid === m.id) {
                    pieceModel.set(i, row)
                    found = true
                    break
                }
            }
            if (!found)
                pieceModel.append(row)
        }
    }

    function setView(newYaw, newPitch) {
        yaw = newYaw
        pitch = newPitch
        target = Qt.vector3d(0, 0.95, 0)
        distance = 3.2
    }

    Component.onCompleted: {
        if (App.startupView.indexOf("checker") >= 0) displayMode = "checker"
        if (App.startupView.indexOf("wire") >= 0) wireframe = true
        if (App.startupView.indexOf("pinch") >= 0) pinchMode = true
        if (App.startupView.indexOf("ortho") >= 0) orthographic = true
        if (App.startupView.indexOf("ruler") >= 0) {
            // For documentation screenshots: across the shoulders of the mannequin.
            rulerA = Qt.vector3d(-0.19, 1.42, 0.02)
            rulerB = Qt.vector3d(0.19, 1.42, 0.02)
            rulerPlaced = true
        }
        refresh()
    }
    Connections {
        target: App
        function onViewportRevisionChanged() { Qt.callLater(root.refresh) }
    }

    View3D {
        id: view
        anchors.fill: parent
        camera: root.activeCamera

        environment: SceneEnvironment {
            clearColor: "#24262b"
            backgroundMode: SceneEnvironment.Color
            antialiasingMode: SceneEnvironment.MSAA
            antialiasingQuality: SceneEnvironment.High
            // Ground: a grid that fades with distance; 10 cm (4 in with imperial units) unless changed in Preferences.
            InfiniteGrid {
                gridInterval: App.gridSizeCm / 100
                gridAxes: false
            }
        }

        Node {
            id: pivot
            position: root.target
            eulerRotation: Qt.vector3d(root.pitch, root.yaw, 0)
            PerspectiveCamera {
                id: camera
                z: root.distance
                clipNear: 0.02
                clipFar: 80
                fieldOfView: root.fieldOfView
            }
            OrthographicCamera {
                id: orthoCamera
                z: 20
                clipNear: 0.02
                clipFar: 80
                horizontalMagnification: root.magnification
                verticalMagnification: root.magnification
            }
        }

        DirectionalLight { eulerRotation: Qt.vector3d(-35, 30, 0); brightness: 1.1; ambientColor: Qt.rgba(0.28, 0.28, 0.30, 1) }
        DirectionalLight { eulerRotation: Qt.vector3d(-20, -140, 0); brightness: 0.55 }
        DirectionalLight { eulerRotation: Qt.vector3d(60, 180, 0); brightness: 0.25 }

        Model {
            visible: root.showAvatar && root.avatarSource !== ""
            pickable: true      // for the ruler
            source: root.avatarSource
            materials: PrincipledMaterial { baseColor: "#b8aaa0"; roughness: 0.75 }
        }

        Repeater3D {
            model: pieceModel
            delegate: Model {
                id: pieceItem
                required property string pid
                required property string meshUrl
                required property string tint
                required property string tex
                required property real rep
                required property real rough
                required property real metal
                required property real alpha
                required property bool sel
                readonly property bool checker: root.displayMode === "checker"
                readonly property bool textured: checker || tex !== ""

                source: meshUrl
                pickable: true
                materials: PrincipledMaterial {
                    baseColor: pieceItem.checker ? "#ffffff" : (pieceItem.tex !== "" ? "#ffffff" : pieceItem.tint)
                    baseColorMap: pieceItem.textured ? fabricTexture : null
                    roughness: pieceItem.rough
                    metalness: pieceItem.metal
                    opacity: pieceItem.alpha
                    alphaMode: pieceItem.alpha < 0.999 ? PrincipledMaterial.Blend : PrincipledMaterial.Opaque
                    cullMode: Material.NoCulling
                    emissiveFactor: pieceItem.sel ? Qt.vector3d(0.16, 0.12, 0.02) : Qt.vector3d(0, 0, 0)
                }
                Texture {
                    id: fabricTexture
                    source: pieceItem.checker ? App.checkerTexture : pieceItem.tex
                    scaleU: pieceItem.rep
                    scaleV: pieceItem.rep
                    tilingModeHorizontal: Texture.Repeat
                    tilingModeVertical: Texture.Repeat
                    generateMipmaps: true
                    mipFilter: Texture.Linear
                }
            }
        }

        // Topology overlay: the outline of every face.
        Model {
            visible: root.wireframe && root.wireSource !== ""
            source: root.wireSource
            materials: PrincipledMaterial { baseColor: "#14161a"; lighting: PrincipledMaterial.NoLighting }
        }

        // Threads between stitched edges that have not been pulled together yet.
        Model {
            visible: root.showThreads && root.threadSource !== ""
            source: root.threadSource
            materials: PrincipledMaterial { baseColor: "#ff5a5f"; lighting: PrincipledMaterial.NoLighting }
        }
    }

    // Move/rotate handles for the selected pieces, drawn in their own layer so the garment never hides them.
    View3D {
        id: gizmoView
        anchors.fill: parent
        camera: root.orthographic ? gizmoOrtho : gizmoCamera
        visible: root.hasGizmo && root.showGizmo && !App.simulating && !root.pinchMode && !root.measuring
        environment: SceneEnvironment { backgroundMode: SceneEnvironment.Transparent; antialiasingMode: SceneEnvironment.MSAA }

        Node {
            position: root.target
            eulerRotation: Qt.vector3d(root.pitch, root.yaw, 0)
            PerspectiveCamera { id: gizmoCamera; z: root.distance; clipNear: 0.02; clipFar: 80; fieldOfView: root.fieldOfView }
            OrthographicCamera { id: gizmoOrtho; z: 20; clipNear: 0.02; clipFar: 80; horizontalMagnification: root.magnification; verticalMagnification: root.magnification }
        }

        component Handle: Model {
            property string part: ""
            property color tint: "white"
            pickable: true
            materials: PrincipledMaterial {
                baseColor: mouse.activePart === part ? "#ffe066" : tint
                lighting: PrincipledMaterial.NoLighting
            }
        }

        Node {
            position: root.gizmoOrigin
            // Keeps the handles the same size on screen whatever the zoom.
            scale: Qt.vector3d(root.distance * 0.085, root.distance * 0.085, root.distance * 0.085)
            // Q shows every handle; W, E and R only those that move, rotate or scale.
            readonly property bool moves: App.gizmoMode === "Full" || App.gizmoMode === "Move"
            readonly property bool turns: App.gizmoMode === "Full" || App.gizmoMode === "Rotate"
            readonly property bool scales: App.gizmoMode === "Full" || App.gizmoMode === "Scale"
            Handle { part: "tx"; tint: "#e5484d"; source: App.gizmoArrow; eulerRotation.z: -90; visible: parent.moves }
            Handle { part: "ty"; tint: "#46a758"; source: App.gizmoArrow; visible: parent.moves }
            Handle { part: "tz"; tint: "#3e7bdc"; source: App.gizmoArrow; eulerRotation.x: 90; visible: parent.moves }
            Handle { part: "rx"; tint: "#e5484d"; source: App.gizmoRing; eulerRotation.z: -90; visible: parent.turns }
            Handle { part: "ry"; tint: "#46a758"; source: App.gizmoRing; visible: parent.turns }
            Handle { part: "rz"; tint: "#3e7bdc"; source: App.gizmoRing; eulerRotation.x: 90; visible: parent.turns }
            // Scale boxes: beyond the arrow tips when everything is shown, on short stalks on their own. The
            // white box in the middle scales evenly. "#Cube" is 100 units wide.
            Handle { part: "sx"; tint: "#e5484d"; source: "#Cube"; x: root.scaleReach; scale: Qt.vector3d(0.0011, 0.0011, 0.0011); visible: parent.scales }
            Handle { part: "sy"; tint: "#46a758"; source: "#Cube"; y: root.scaleReach; scale: Qt.vector3d(0.0011, 0.0011, 0.0011); visible: parent.scales }
            Handle { part: "sz"; tint: "#3e7bdc"; source: "#Cube"; z: root.scaleReach; scale: Qt.vector3d(0.0011, 0.0011, 0.0011); visible: parent.scales }
            Handle { part: "su"; tint: "#f2f3f5"; source: "#Cube"; scale: Qt.vector3d(0.0013, 0.0013, 0.0013); visible: parent.scales }
            Handle { part: "sx"; tint: "#e5484d"; source: "#Cube"; x: root.scaleReach / 2; scale: Qt.vector3d(root.scaleReach / 100, 0.00025, 0.00025); visible: App.gizmoMode === "Scale" }
            Handle { part: "sy"; tint: "#46a758"; source: "#Cube"; y: root.scaleReach / 2; scale: Qt.vector3d(0.00025, root.scaleReach / 100, 0.00025); visible: App.gizmoMode === "Scale" }
            Handle { part: "sz"; tint: "#3e7bdc"; source: "#Cube"; z: root.scaleReach / 2; scale: Qt.vector3d(0.00025, 0.00025, root.scaleReach / 100); visible: App.gizmoMode === "Scale" }
        }
    }

    // The ruler, drawn over the scene so the garment never hides it.
    Canvas {
        id: rulerCanvas
        anchors.fill: parent
        visible: root.rulerPlaced && root.measuring
        renderStrategy: Canvas.Cooperative
        readonly property var cameraState: [root.yaw, root.pitch, root.distance, root.target, root.orthographic, width, height]
        onCameraStateChanged: Qt.callLater(requestPaint)
        onVisibleChanged: if (visible) requestPaint()
        onPaint: {
            const ctx = getContext("2d")
            ctx.reset()
            if (!root.rulerPlaced)
                return
            const a = view.mapFrom3DScene(root.rulerA), b = view.mapFrom3DScene(root.rulerB)
            ctx.strokeStyle = "#ffd447"; ctx.lineWidth = 2
            ctx.setLineDash(root.rulerOpen ? [6, 4] : [])
            ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke()
            ctx.setLineDash([])
            ctx.fillStyle = "#ffd447"; ctx.strokeStyle = "#1b1c1f"; ctx.lineWidth = 1
            for (const p of [a, b]) {
                ctx.beginPath(); ctx.arc(p.x, p.y, 4.5, 0, Math.PI * 2); ctx.fill(); ctx.stroke()
            }
            const text = root.rulerText()
            ctx.font = "bold 11px sans-serif"
            ctx.textAlign = "center"
            const w = ctx.measureText(text).width + 12, mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2 - 16
            ctx.fillStyle = Qt.rgba(0.07, 0.07, 0.09, 0.88)
            ctx.strokeStyle = "#ffd447"
            ctx.beginPath(); ctx.roundedRect(Math.round(mx - w / 2) + 0.5, Math.round(my - 9) + 0.5, w, 18, 4, 4); ctx.fill(); ctx.stroke()
            ctx.fillStyle = "#ffe58a"
            ctx.fillText(text, mx, my + 4)
        }
    }

    MouseArea {
        id: mouse
        anchors.fill: parent
        acceptedButtons: Qt.LeftButton | Qt.MiddleButton | Qt.RightButton
        property real lastX: 0
        property real lastY: 0
        property real travelled: 0
        property int mode: 0   // 1 orbit, 2 pan, 3 dragging a handle, 4 pinching cloth, 5 placing a ruler end
        hoverEnabled: root.rulerOpen
        property vector3d pinchStart: Qt.vector3d(0, 0, 0)
        property real pinchScale: 0
        cursorShape: mode === 4 ? Qt.ClosedHandCursor : (root.pinchMode ? Qt.OpenHandCursor : Qt.ArrowCursor)
        property string activePart: ""
        property real pressX: 0
        property real pressY: 0
        property real startAngle: 0
        property real turned: 0
        property real lastAngle: 0

        function axisOf(part) {
            const a = part.charAt(1)
            return a === "x" ? Qt.vector3d(1, 0, 0) : a === "y" ? Qt.vector3d(0, 1, 0) : Qt.vector3d(0, 0, 1)
        }

        function dragHandle(x, y, snap) {
            if (activePart === "su") {
                // The middle box scales evenly: drag up or to the right to grow, down or to the left to shrink.
                App.gizmoScale("u", Math.max(0.05, 1 + ((x - pressX) - (y - pressY)) / 150), snap)
                return
            }
            const axis = axisOf(activePart)
            const origin = gizmoView.mapFrom3DScene(dragOrigin)
            if (activePart.charAt(0) === "s") {
                // A box on an axis scales by how far it is pulled along that axis from where it was.
                const tip = gizmoView.mapFrom3DScene(dragOrigin.plus(axis))
                const sx = tip.x - origin.x, sy = tip.y - origin.y
                const length2 = sx * sx + sy * sy
                if (length2 < 1e-6)
                    return
                const amount = ((x - pressX) * sx + (y - pressY) * sy) / length2
                App.gizmoScale(activePart.charAt(1), Math.max(0.05, 1 + amount / (root.distance * 0.085 * root.scaleReach)), snap)
            } else if (activePart.charAt(0) === "t") {
                // Slide along the axis by as much as the cursor moved along the axis as it appears on screen.
                const tip = gizmoView.mapFrom3DScene(dragOrigin.plus(axis))
                const sx = tip.x - origin.x, sy = tip.y - origin.y
                const length2 = sx * sx + sy * sy
                if (length2 < 1e-6)
                    return
                const amount = ((x - pressX) * sx + (y - pressY) * sy) / length2
                App.gizmoMove(axis.x * amount, axis.y * amount, axis.z * amount, snap)
            } else {
                // Turn by the angle the cursor sweeps around the handle centre, counting past half turns.
                const angle = Math.atan2(y - origin.y, x - origin.x)
                let step = angle - lastAngle
                if (step > Math.PI) step -= 2 * Math.PI
                if (step < -Math.PI) step += 2 * Math.PI
                turned += step
                lastAngle = angle
                const facing = axis.dotProduct(gizmoCamera.forward) > 0 ? 1 : -1
                App.gizmoRotate(activePart.charAt(1), turned * facing * 180 / Math.PI)
            }
        }
        property vector3d dragOrigin: Qt.vector3d(0, 0, 0)

        onPressed: (e) => {
            root.forceActiveFocus()
            lastX = e.x; lastY = e.y; travelled = 0
            pressX = e.x; pressY = e.y
            if (e.button === Qt.LeftButton && gizmoView.visible) {
                const hit = gizmoView.pick(e.x, e.y)
                if (hit.objectHit && hit.objectHit.part
                        && (hit.objectHit.part.charAt(0) === "s" ? App.gizmoBeginScale() : App.gizmoBegin())) {
                    activePart = hit.objectHit.part
                    dragOrigin = root.gizmoOrigin
                    const origin = gizmoView.mapFrom3DScene(dragOrigin)
                    lastAngle = Math.atan2(e.y - origin.y, e.x - origin.x)
                    turned = 0
                    mode = 3
                    return
                }
            }
            if (e.button === Qt.LeftButton && root.pinchMode && !(e.modifiers & Qt.ShiftModifier)) {
                // Pinch: take hold of the cloth under the cursor. Dragging on the avatar or empty space still orbits.
                const hit = view.pick(e.x, e.y)
                if (hit.objectHit && hit.objectHit.pid
                        && App.pinchBegin(hit.objectHit.pid, hit.scenePosition.x, hit.scenePosition.y, hit.scenePosition.z)) {
                    pinchStart = hit.scenePosition
                    // Metres per pixel at the depth of the grabbed point, so the cloth stays under the cursor.
                    const depth = hit.scenePosition.minus(camera.scenePosition).dotProduct(camera.forward)
                    pinchScale = root.orthographic ? 1 / root.magnification : 2 * depth * Math.tan(camera.fieldOfView * Math.PI / 360) / view.height
                    mode = 4
                    return
                }
            }
            if (e.button === Qt.LeftButton && root.measuring && !(e.modifiers & Qt.ShiftModifier)) {
                // Ruler: press on a surface to place an end. Dragging from empty space still orbits.
                const hit = view.pick(e.x, e.y)
                if (hit.objectHit) {
                    if (root.rulerOpen) {
                        root.rulerB = hit.scenePosition
                        root.rulerOpen = false
                    } else {
                        root.rulerA = root.rulerB = hit.scenePosition
                        root.rulerPlaced = true
                        root.rulerOpen = true
                    }
                    mode = 5
                    rulerCanvas.requestPaint()
                    return
                }
            }
            mode = (e.button === Qt.LeftButton && !(e.modifiers & Qt.ShiftModifier)) ? 1 : 2
        }
        onPositionChanged: (e) => {
            const dx = e.x - lastX, dy = e.y - lastY
            lastX = e.x; lastY = e.y
            travelled += Math.abs(dx) + Math.abs(dy)
            if (mode === 3) {
                dragHandle(e.x, e.y, (e.modifiers & Qt.ShiftModifier) !== 0)
            } else if (mode === 5 || (mode === 0 && root.rulerOpen)) {
                // The far end of the ruler follows the pointer across any surface.
                const hit = view.pick(e.x, e.y)
                if (hit.objectHit) {
                    root.rulerB = hit.scenePosition
                    rulerCanvas.requestPaint()
                }
            } else if (mode === 4) {
                const mx = (e.x - pressX) * pinchScale, my = (pressY - e.y) * pinchScale
                const r = root.activeCamera.right, u = root.activeCamera.up
                App.pinchMove(pinchStart.x + r.x * mx + u.x * my, pinchStart.y + r.y * mx + u.y * my, pinchStart.z + r.z * mx + u.z * my)
            } else if (mode === 1) {
                root.yaw -= dx * 0.4
                root.pitch = Math.max(-89, Math.min(89, root.pitch - dy * 0.4))
            } else if (mode === 2) {
                // Slide the orbit centre in the camera plane; scale by distance so the scene follows the cursor.
                const k = root.distance * 0.0016
                const r = root.activeCamera.right, u = root.activeCamera.up
                root.target = Qt.vector3d(root.target.x - (r.x * dx - u.x * dy) * k,
                                          root.target.y - (r.y * dx - u.y * dy) * k,
                                          root.target.z - (r.z * dx - u.z * dy) * k)
            }
        }
        onReleased: (e) => {
            if (mode === 3) {
                App.gizmoEnd()
                activePart = ""
            } else if (mode === 4) {
                App.pinchEnd()
            } else if (mode === 5) {
                // A drag places both ends at once; a click leaves the far end following the pointer.
                if (travelled >= 4)
                    root.rulerOpen = false
            } else if (travelled < 4 && e.button === Qt.RightButton) {
                const hit = view.pick(e.x, e.y)
                App.contextSelectIn3D(hit.objectHit && hit.objectHit.pid ? hit.objectHit.pid : "")
                root.contextMenuRequested()
            } else if (travelled < 4 && e.button === Qt.LeftButton && root.measuring) {
                root.clearRuler()      // a click on empty space
            } else if (travelled < 4 && e.button === Qt.LeftButton) {
                // A click without dragging selects the piece under the cursor.
                const hit = view.pick(e.x, e.y)
                const additive = (e.modifiers & Qt.ControlModifier) !== 0 || (e.modifiers & Qt.ShiftModifier) !== 0
                App.selectIn3D(hit.objectHit && hit.objectHit.pid ? hit.objectHit.pid : "", additive)
            }
            mode = 0
        }
        onCanceled: {
            if (mode === 3) App.gizmoEnd()
            if (mode === 4) App.pinchEnd()
            activePart = ""
            mode = 0
        }
        onWheel: (e) => { root.distance = Math.max(0.25, Math.min(25, root.distance * Math.pow(0.999, e.angleDelta.y))) }
    }

    Label {
        anchors { left: parent.left; top: parent.top; margins: 8 }
        text: "3D Garment"
        font.bold: true
        opacity: 0.75
    }

    RowLayout {
        anchors { right: parent.right; top: parent.top; margins: 6 }
        spacing: 4
        component ViewButton: ToolButton {
            font.pixelSize: 11
            implicitHeight: 24
            padding: 5
        }
        ViewButton { text: "Front"; onClicked: root.setView(0, -5) }
        ViewButton { text: "Side"; onClicked: root.setView(90, -5) }
        ViewButton { text: "Back"; onClicked: root.setView(180, -5) }
        ViewButton {
            text: "Ortho"
            checkable: true
            checked: root.orthographic
            onToggled: root.orthographic = checked
            ToolTip.visible: hovered
            readonly property string key: { App.keysRevision; return App.keyText("view.ortho") }
            ToolTip.text: "Orthographic view: no perspective, so parts of the garment can be compared in size" + (key !== "" ? " (" + key + ")" : "")
        }
        ToolSeparator { implicitHeight: 22 }
        ViewButton {
            text: "Checker"
            checkable: true
            checked: root.displayMode === "checker"
            onToggled: root.displayMode = checked ? "checker" : "shaded"
            ToolTip.visible: hovered
            ToolTip.text: "Show a checker texture to judge UVs and texel density"
        }
        ViewButton {
            text: "Wire"
            checkable: true
            checked: root.wireframe
            onToggled: root.wireframe = checked
            ToolTip.visible: hovered
            ToolTip.text: "Show the mesh topology"
        }
        ViewButton {
            text: "Pinch"
            checkable: true
            checked: root.pinchMode
            onToggled: root.pinchMode = checked
            ToolTip.visible: hovered
            ToolTip.text: "Drag the cloth itself to tug it into place, during or after simulation"
        }
        ViewButton {
            text: "Gizmo"
            checkable: true
            checked: root.showGizmo
            onToggled: root.showGizmo = checked
            ToolTip.visible: hovered
            ToolTip.text: "Show handles to move (arrows) and rotate (rings) the selected pieces"
        }
        ViewButton {
            text: "Avatar"
            checkable: true
            checked: root.showAvatar
            onToggled: root.showAvatar = checked
        }
    }

    Label {
        anchors { left: parent.left; right: rendererLabel.left; bottom: parent.bottom; margins: 8 }
        elide: Text.ElideRight
        opacity: 0.55
        font.pixelSize: 11
        text: root.measuring ? "Ruler: click two points on the cloth or avatar, or drag between them  ·  Click empty space: clear  ·  Drag elsewhere: orbit"
            : root.pinchMode ? "Pinch: drag the cloth to pull it  ·  Drag elsewhere: orbit  ·  Shift-drag: pan  ·  Wheel: zoom"
                            : "Drag: orbit  ·  Shift-drag: pan  ·  Wheel: zoom  ·  Click: select  ·  Right-click: Edit menu  ·  Handles: move / rotate / scale (Q W E R)  ·  Shift: snap"
    }
    Label {
        id: rendererLabel
        anchors { right: parent.right; bottom: parent.bottom; margins: 8 }
        opacity: 0.55
        font.pixelSize: 11
        text: App.renderer
    }
}
