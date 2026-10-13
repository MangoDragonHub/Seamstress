import QtQuick
import QtQuick.Controls

// 2D viewport: draws the pattern pieces (or their UV layout) and forwards pointer input to the editor.
// World coordinates are centimetres with Y up; in UV mode they are texture units.
Item {
    id: root
    clip: true

    property var scene: ({ mode: "pattern", tool: "Select", pieces: [], seams: [], bounds: [-20, -20, 120, 100] })
    property real zoom: 5
    property real panX: 40
    property real panY: 40
    property string framedMode: ""
    property bool framedOnce: false
    property bool userAdjusted: false   // once the user pans or zooms, resizing no longer re-frames the view
    readonly property bool uv: scene.mode === "uv"
    readonly property color accent: palette.highlight

    function sx(x) { return x * zoom + panX }
    function sy(y) { return height - (y * zoom + panY) }
    function wx(px) { return (px - panX) / zoom }
    function wy(py) { return (height - py - panY) / zoom }
    function tolerance() { return 9 / zoom }

    function refresh() {
        scene = JSON.parse(App.patternScene())
        // Re-frame when switching between pattern and UV space, and the first time there is something to show.
        if (scene.mode !== framedMode || (!framedOnce && scene.pieces.length > 0)) {
            framedMode = scene.mode
            framedOnce = scene.pieces.length > 0
            userAdjusted = false
            frameAll()
        }
        canvas.requestPaint()
    }

    function frameAll() {
        if (width <= 0 || height <= 0)
            return
        userAdjusted = false
        const b = scene.bounds
        const w = Math.max(b[2] - b[0], 1e-6), h = Math.max(b[3] - b[1], 1e-6)
        const margin = 48
        zoom = Math.min((width - 2 * margin) / w, (height - 2 * margin) / h)
        panX = (width - w * zoom) / 2 - b[0] * zoom
        panY = (height - h * zoom) / 2 - b[1] * zoom
        canvas.requestPaint()
    }

    Component.onCompleted: refresh()
    onWidthChanged: if (!userAdjusted) frameAll(); else canvas.requestPaint()
    onHeightChanged: if (!userAdjusted) frameAll(); else canvas.requestPaint()

    Connections {
        target: App
        function onSceneRevisionChanged() { Qt.callLater(root.refresh) }
    }

    Canvas {
        id: canvas
        anchors.fill: parent
        renderStrategy: Canvas.Cooperative

        function trace(ctx, pts, close) {
            ctx.beginPath()
            for (let i = 0; i < pts.length; i += 2) {
                if (i === 0) ctx.moveTo(root.sx(pts[i]), root.sy(pts[i + 1]))
                else ctx.lineTo(root.sx(pts[i]), root.sy(pts[i + 1]))
            }
            if (close) ctx.closePath()
        }

        // Point halfway along a polyline, measured by length.
        function middle(pts) {
            let total = 0
            for (let i = 2; i < pts.length; i += 2)
                total += Math.hypot(pts[i] - pts[i - 2], pts[i + 1] - pts[i - 1])
            let walked = 0
            for (let i = 2; i < pts.length; i += 2) {
                const seg = Math.hypot(pts[i] - pts[i - 2], pts[i + 1] - pts[i - 1])
                if (walked + seg >= total / 2 && seg > 0) {
                    const t = (total / 2 - walked) / seg
                    return [pts[i - 2] + (pts[i] - pts[i - 2]) * t, pts[i - 1] + (pts[i + 1] - pts[i - 1]) * t]
                }
                walked += seg
            }
            return [pts[0], pts[1]]
        }

        function withAlpha(hex, alpha) {
            const c = Qt.color(hex)
            return Qt.rgba(c.r, c.g, c.b, alpha)
        }

        onPaint: {
            const ctx = getContext("2d")
            const s = root.scene
            ctx.reset()
            ctx.fillStyle = Qt.darker(palette.window, 1.25)
            ctx.fillRect(0, 0, width, height)

            if (root.uv) {
                // Texture space: the unit square filled with a checker, like the fabric texture would be.
                const cells = 16
                const size = root.zoom / cells
                for (let j = 0; j < cells; j++) {
                    for (let i = 0; i < cells; i++) {
                        ctx.fillStyle = (i + j) % 2 === 0 ? "#3b3e44" : "#55595f"
                        ctx.fillRect(root.sx(i / cells), root.sy((j + 1) / cells), size + 0.5, size + 0.5)
                    }
                }
                ctx.strokeStyle = "#9aa0a8"
                ctx.lineWidth = 1
                ctx.strokeRect(root.sx(0), root.sy(1), root.zoom, root.zoom)
            } else {
                // Grid every 10 cm, with a stronger line every metre and at the origin.
                const step = root.zoom * 10 >= 14 ? 10 : 100
                const x0 = Math.floor(root.wx(0) / step) * step, x1 = root.wx(width)
                const y0 = Math.floor(root.wy(height) / step) * step, y1 = root.wy(0)
                ctx.lineWidth = 1
                for (let x = x0; x <= x1; x += step) {
                    ctx.strokeStyle = x === 0 ? "#6a6f78" : (x % 100 === 0 ? "#484c53" : "#383b41")
                    ctx.beginPath(); ctx.moveTo(Math.round(root.sx(x)) + 0.5, 0); ctx.lineTo(Math.round(root.sx(x)) + 0.5, height); ctx.stroke()
                }
                for (let y = y0; y <= y1; y += step) {
                    ctx.strokeStyle = y === 0 ? "#6a6f78" : (y % 100 === 0 ? "#484c53" : "#383b41")
                    ctx.beginPath(); ctx.moveTo(0, Math.round(root.sy(y)) + 0.5); ctx.lineTo(width, Math.round(root.sy(y)) + 0.5); ctx.stroke()
                }
            }

            const showPoints = s.tool === "EditPoints" && !root.uv
            for (const p of s.pieces) {
                trace(ctx, p.outline, true)
                ctx.fillStyle = withAlpha(p.color, p.active ? (root.uv ? 0.5 : 0.62) : 0.28)
                ctx.fill()
                ctx.lineWidth = p.selected ? 2.5 : 1.3
                ctx.strokeStyle = p.selected ? root.accent : (p.active ? "#e8eaee" : "#8a8f98")
                ctx.stroke()
            }

            for (const seam of s.seams) {
                ctx.strokeStyle = seam.color
                ctx.lineWidth = seam.selected ? 5 : 3
                ctx.setLineDash([])
                trace(ctx, seam.a, false); ctx.stroke()
                trace(ctx, seam.b, false); ctx.stroke()
                const ma = middle(seam.a), mb = middle(seam.b)
                ctx.lineWidth = seam.selected ? 2.5 : 1.2
                ctx.setLineDash([6, 4])
                ctx.beginPath(); ctx.moveTo(root.sx(ma[0]), root.sy(ma[1])); ctx.lineTo(root.sx(mb[0]), root.sy(mb[1])); ctx.stroke()
                ctx.setLineDash([])
                // Dots mark the two ends that get sewn to each other.
                const n = seam.b.length
                const bx = seam.reversed ? seam.b[0] : seam.b[n - 2], by = seam.reversed ? seam.b[1] : seam.b[n - 1]
                ctx.fillStyle = seam.color
                for (const end of [[seam.a[0], seam.a[1]], [bx, by]]) {
                    ctx.beginPath(); ctx.arc(root.sx(end[0]), root.sy(end[1]), seam.selected ? 6 : 4.5, 0, Math.PI * 2); ctx.fill()
                }
            }

            for (const p of s.pieces) {
                if (showPoints || p.selected) {
                    for (let i = 0; i < p.points.length; i += 2) {
                        const chosen = p.selectedPoint === i / 2
                        const r = chosen ? 5 : 3.5
                        ctx.fillStyle = chosen ? root.accent : "#f2f3f5"
                        ctx.strokeStyle = "#1b1c1f"
                        ctx.lineWidth = 1
                        ctx.fillRect(root.sx(p.points[i]) - r, root.sy(p.points[i + 1]) - r, 2 * r, 2 * r)
                        ctx.strokeRect(root.sx(p.points[i]) - r, root.sy(p.points[i + 1]) - r, 2 * r, 2 * r)
                    }
                }
                if (p.controls) {
                    for (const c of p.controls) {
                        ctx.strokeStyle = "#c9a227"
                        ctx.lineWidth = 1
                        ctx.setLineDash([3, 3])
                        trace(ctx, c, false); ctx.stroke()
                        ctx.setLineDash([])
                        ctx.fillStyle = "#ffd447"
                        ctx.beginPath(); ctx.arc(root.sx(c[2]), root.sy(c[3]), 4.5, 0, Math.PI * 2); ctx.fill()
                    }
                }
                ctx.fillStyle = p.active ? "#f4f5f7" : "#a9adb5"
                ctx.font = (p.selected ? "bold " : "") + "12px sans-serif"
                ctx.textAlign = "center"
                const label = root.uv ? p.name + "  ·  " + p.density.toFixed(1) + " px/cm" : p.name
                ctx.fillText(label, root.sx(p.label[0]), root.sy(p.label[1]) + 4)
            }

            if (s.hover) {
                ctx.strokeStyle = "#ffd447"; ctx.lineWidth = 4
                trace(ctx, s.hover, false); ctx.stroke()
            }
            if (s.pending) {
                ctx.strokeStyle = root.accent; ctx.lineWidth = 6
                trace(ctx, s.pending, false); ctx.stroke()
            }
            if (s.draft) {
                ctx.strokeStyle = root.accent; ctx.lineWidth = 2
                ctx.setLineDash([5, 4])
                trace(ctx, s.draft, s.draftClosed); ctx.stroke()
                ctx.setLineDash([])
                ctx.fillStyle = root.accent
                for (let i = 0; i < s.draft.length; i += 2)
                    ctx.fillRect(root.sx(s.draft[i]) - 3, root.sy(s.draft[i + 1]) - 3, 6, 6)
            }
        }
    }

    MouseArea {
        id: mouse
        anchors.fill: parent
        acceptedButtons: Qt.LeftButton | Qt.MiddleButton | Qt.RightButton
        hoverEnabled: true
        property bool panning: false
        property real lastX: 0
        property real lastY: 0
        cursorShape: panning ? Qt.ClosedHandCursor : (root.scene.tool === "Select" || root.uv ? Qt.ArrowCursor : Qt.CrossCursor)

        onPressed: (e) => {
            root.forceActiveFocus()
            if (e.button === Qt.LeftButton && !(e.modifiers & Qt.AltModifier)) {
                App.pointerDown(root.wx(e.x), root.wy(e.y), root.tolerance(), (e.modifiers & Qt.ControlModifier) !== 0 || (e.modifiers & Qt.ShiftModifier) !== 0)
            } else {
                panning = true
                lastX = e.x; lastY = e.y
            }
        }
        onPositionChanged: (e) => {
            if (panning) {
                root.userAdjusted = true
                root.panX += e.x - lastX
                root.panY -= e.y - lastY
                lastX = e.x; lastY = e.y
                canvas.requestPaint()
            } else {
                App.pointerMove(root.wx(e.x), root.wy(e.y), root.tolerance())
            }
            coords.text = root.uv
                ? "U " + root.wx(e.x).toFixed(3) + "   V " + root.wy(e.y).toFixed(3)
                : root.wx(e.x).toFixed(1) + ", " + root.wy(e.y).toFixed(1) + " cm"
        }
        onReleased: (e) => {
            if (panning) panning = false
            else if (e.button === Qt.LeftButton) App.pointerUp(root.wx(e.x), root.wy(e.y))
        }
        onDoubleClicked: (e) => {
            if (e.button === Qt.LeftButton) App.doubleClick(root.wx(e.x), root.wy(e.y), root.tolerance())
        }
        onWheel: (e) => {
            // Zoom about the cursor so the point under it stays put.
            root.userAdjusted = true
            const factor = Math.pow(1.0015, e.angleDelta.y)
            const cx = root.wx(e.x), cy = root.wy(e.y)
            root.zoom = Math.max(root.uv ? 20 : 0.2, Math.min(root.uv ? 20000 : 400, root.zoom * factor))
            root.panX = e.x - cx * root.zoom
            root.panY = root.height - e.y - cy * root.zoom
            canvas.requestPaint()
        }
    }

    Label {
        anchors { left: parent.left; top: parent.top; margins: 8 }
        text: root.uv ? "UV Layout" : "2D Pattern"
        font.bold: true
        opacity: 0.75
    }
    Label {
        id: coords
        anchors { right: parent.right; bottom: parent.bottom; margins: 8 }
        opacity: 0.6
        font.pixelSize: 11
    }
    Label {
        anchors.centerIn: parent
        visible: root.scene.pieces.length === 0 && !root.scene.draft
        horizontalAlignment: Text.AlignHCenter
        opacity: 0.55
        text: "No pattern pieces yet.\nDraw one with the Polygon, Rectangle or Ellipse tool,\nor add a ready-made garment from the Garment menu."
    }
}
