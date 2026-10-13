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
            debugSettings: DebugSettings { wireframeEnabled: root.wireframe }
            // Ground: a 10 cm grid that fades with distance.
            InfiniteGrid {
                gridInterval: 0.1
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

        // Threads between stitched edges that have not been pulled together yet.
        Model {
            visible: root.showThreads && root.threadSource !== ""
            source: root.threadSource
            materials: PrincipledMaterial { baseColor: "#ff5a5f"; lighting: PrincipledMaterial.NoLighting }
        }
    }

    MouseArea {
        anchors.fill: parent
        acceptedButtons: Qt.LeftButton | Qt.MiddleButton | Qt.RightButton
        property real lastX: 0
        property real lastY: 0
        property real travelled: 0
        property int mode: 0   // 1 orbit, 2 pan

        onPressed: (e) => {
            root.forceActiveFocus()
            lastX = e.x; lastY = e.y; travelled = 0
            mode = (e.button === Qt.LeftButton && !(e.modifiers & Qt.ShiftModifier)) ? 1 : 2
        }
        onPositionChanged: (e) => {
            const dx = e.x - lastX, dy = e.y - lastY
            lastX = e.x; lastY = e.y
            travelled += Math.abs(dx) + Math.abs(dy)
            if (mode === 1) {
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
            // A click without dragging selects the piece under the cursor.
            if (travelled < 4 && e.button === Qt.LeftButton) {
                const hit = view.pick(e.x, e.y)
                const additive = (e.modifiers & Qt.ControlModifier) !== 0 || (e.modifiers & Qt.ShiftModifier) !== 0
                App.selectIn3D(hit.objectHit && hit.objectHit.pid ? hit.objectHit.pid : "", additive)
            }
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
        text: "Drag: orbit   ·   Shift/Middle drag: pan   ·   Wheel: zoom   ·   Click: select"
    }
    Label {
        anchors { right: parent.right; bottom: parent.bottom; margins: 8 }
        opacity: 0.55
        font.pixelSize: 11
        text: App.renderer
    }
}
