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
    property bool pinchMode: false
    property bool showAvatar: true
    property bool showThreads: true
    property string avatarSource: ""
    property string threadSource: ""

    property real yaw: 25
    property real pitch: -8
    property real distance: 3.2
    property vector3d target: Qt.vector3d(0, 0.95, 0)

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
        refresh()
    }
    Connections {
        target: App
        function onViewportRevisionChanged() { Qt.callLater(root.refresh) }
    }

    View3D {
        id: view
        anchors.fill: parent
        camera: camera

        environment: SceneEnvironment {
            clearColor: "#24262b"
            backgroundMode: SceneEnvironment.Color
            antialiasingMode: SceneEnvironment.MSAA
            antialiasingQuality: SceneEnvironment.High
            // Ground: a 10 cm grid (4 in with imperial units) that fades with distance.
            InfiniteGrid {
                gridInterval: App.imperial ? 0.1016 : 0.1
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
                fieldOfView: 35
            }
        }

        DirectionalLight { eulerRotation: Qt.vector3d(-35, 30, 0); brightness: 1.1; ambientColor: Qt.rgba(0.28, 0.28, 0.30, 1) }
        DirectionalLight { eulerRotation: Qt.vector3d(-20, -140, 0); brightness: 0.55 }
        DirectionalLight { eulerRotation: Qt.vector3d(60, 180, 0); brightness: 0.25 }

        Model {
            visible: root.showAvatar && root.avatarSource !== ""
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
        camera: gizmoCamera
        visible: root.hasGizmo && root.showGizmo && !App.simulating && !root.pinchMode
        environment: SceneEnvironment { backgroundMode: SceneEnvironment.Transparent; antialiasingMode: SceneEnvironment.MSAA }

        Node {
            position: root.target
            eulerRotation: Qt.vector3d(root.pitch, root.yaw, 0)
            PerspectiveCamera { id: gizmoCamera; z: root.distance; clipNear: 0.02; clipFar: 80; fieldOfView: 35 }
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
            Handle { part: "tx"; tint: "#e5484d"; source: App.gizmoArrow; eulerRotation.z: -90 }
            Handle { part: "ty"; tint: "#46a758"; source: App.gizmoArrow }
            Handle { part: "tz"; tint: "#3e7bdc"; source: App.gizmoArrow; eulerRotation.x: 90 }
            Handle { part: "rx"; tint: "#e5484d"; source: App.gizmoRing; eulerRotation.z: -90 }
            Handle { part: "ry"; tint: "#46a758"; source: App.gizmoRing }
            Handle { part: "rz"; tint: "#3e7bdc"; source: App.gizmoRing; eulerRotation.x: 90 }
        }
    }

    MouseArea {
        id: mouse
        anchors.fill: parent
        acceptedButtons: Qt.LeftButton | Qt.MiddleButton | Qt.RightButton
        property real lastX: 0
        property real lastY: 0
        property real travelled: 0
        property int mode: 0   // 1 orbit, 2 pan, 3 dragging a handle, 4 pinching cloth
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

        function dragHandle(x, y) {
            const axis = axisOf(activePart)
            const origin = gizmoView.mapFrom3DScene(dragOrigin)
            if (activePart.charAt(0) === "t") {
                // Slide along the axis by as much as the cursor moved along the axis as it appears on screen.
                const tip = gizmoView.mapFrom3DScene(dragOrigin.plus(axis))
                const sx = tip.x - origin.x, sy = tip.y - origin.y
                const length2 = sx * sx + sy * sy
                if (length2 < 1e-6)
                    return
                const amount = ((x - pressX) * sx + (y - pressY) * sy) / length2
                App.gizmoMove(axis.x * amount, axis.y * amount, axis.z * amount)
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
                if (hit.objectHit && hit.objectHit.part && App.gizmoBegin()) {
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
                    pinchScale = 2 * depth * Math.tan(camera.fieldOfView * Math.PI / 360) / view.height
                    mode = 4
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
                dragHandle(e.x, e.y)
            } else if (mode === 4) {
                const mx = (e.x - pressX) * pinchScale, my = (pressY - e.y) * pinchScale
                const r = camera.right, u = camera.up
                App.pinchMove(pinchStart.x + r.x * mx + u.x * my, pinchStart.y + r.y * mx + u.y * my, pinchStart.z + r.z * mx + u.z * my)
            } else if (mode === 1) {
                root.yaw -= dx * 0.4
                root.pitch = Math.max(-89, Math.min(89, root.pitch - dy * 0.4))
            } else if (mode === 2) {
                // Slide the orbit centre in the camera plane; scale by distance so the scene follows the cursor.
                const k = root.distance * 0.0016
                const r = camera.right, u = camera.up
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
            ToolTip.text: "Drag the cloth itself to tug it into place, during or after simulation (G)"
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
        anchors { left: parent.left; bottom: parent.bottom; margins: 8 }
        opacity: 0.55
        font.pixelSize: 11
        text: root.pinchMode ? "Pinch: drag the cloth to pull it  ·  Drag elsewhere: orbit  ·  Shift-drag: pan  ·  Wheel: zoom"
                            : "Drag: orbit  ·  Shift-drag: pan  ·  Wheel: zoom  ·  Click: select  ·  Handles: move / rotate"
    }
    Label {
        anchors { right: parent.right; bottom: parent.bottom; margins: 8 }
        opacity: 0.55
        font.pixelSize: 11
        text: App.renderer
    }
}
