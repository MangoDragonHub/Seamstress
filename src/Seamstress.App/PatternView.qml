import QtQuick
import QtQuick.Controls

// 2D viewport: draws the pattern pieces (or their UV layout) and forwards pointer input to the editor.
// World coordinates are centimetres with Y up; in UV mode they are texture units. With imperial units only the
// grid spacing and the read-outs change.
Item {
    id: root
    clip: true

    function tr(text) { App.languageRevision; return App.tr(text) }
    property var scene: ({ mode: "pattern", tool: "Select", pieces: [], seams: [], bounds: [-20, -20, 120, 100] })
    property real zoom: 5
    property real panX: 40
    property real panY: 40
    property string framedMode: ""
    property bool framedOnce: false
    property bool userAdjusted: false   // once the user pans or zooms, resizing no longer re-frames the view
    readonly property bool uv: scene.mode === "uv"
    readonly property color accent: palette.highlight
    // Right-click without dragging: the Edit menu. Dragging with the right button still pans.
    signal contextMenuRequested()

    function sx(x) { return x * zoom + panX }
    function sy(y) { return height - (y * zoom + panY) }
    function wx(px) { return (px - panX) / zoom }
    function wy(py) { return (height - py - panY) / zoom }
    function tolerance() { return 9 / zoom }

    // The grid gets finer as you zoom in. Each level is [spacing in cm, its name, lines per stronger line]; the
    // finest one whose squares are still at least 14 pixels wide is drawn.
    readonly property var gridLevels: App.imperial
        ? [[2.54 / 8, "1/8 in", 8], [2.54, "1 in", 12], [4 * 2.54, "4 in", 3], [12 * 2.54, "1 ft", 3], [36 * 2.54, "1 yd", 10]]
        : [[0.1, "1 mm", 10], [1, "1 cm", 10], [10, "10 cm", 10], [100, "1 m", 10], [1000, "10 m", 10]]
    function gridLevel() {
        for (const level of gridLevels) {
            if (level[0] * zoom >= 14)
                return level
        }
        return gridLevels[gridLevels.length - 1]
    }
    // Shift in Edit Points snaps to the smallest squares on screen.
    Binding { target: App; property: "patternGridStep"; value: root.gridLevel()[0] }
    function lengthText(cm) { return App.imperial ? (cm / 2.54).toFixed(2) + " in" : cm.toFixed(1) + " cm" }

    // Shift keeps proportions in the drawing tools; follow the key itself so the shape reacts without a mouse move.
    Keys.onPressed: (e) => { if (e.key === Qt.Key_Shift) App.constrain = true }
    Keys.onReleased: (e) => { if (e.key === Qt.Key_Shift) App.constrain = false }

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
        function onImperialChanged() { canvas.requestPaint() }
        function onLanguageRevisionChanged() { canvas.requestPaint() }
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

        // A length read-out: text on a dark pill at a world position, moved off it in the direction (nx, ny).
        function measure(ctx, x, y, text, nx, ny) {
            ctx.font = "bold 11px sans-serif"
            ctx.textAlign = "center"
            const w = ctx.measureText(text).width + 12
            const px = root.sx(x) + nx * (w / 2 + 5), py = root.sy(y) - ny * 15
            ctx.fillStyle = Qt.rgba(0.07, 0.07, 0.09, 0.88)
            ctx.strokeStyle = "#ffd447"
            ctx.lineWidth = 1
            ctx.beginPath(); ctx.roundedRect(Math.round(px - w / 2) + 0.5, Math.round(py - 9) + 0.5, w, 18, 4, 4); ctx.fill(); ctx.stroke()
            ctx.fillStyle = "#ffe58a"
            ctx.fillText(text, px, py + 4)
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
                // Grid with a stronger line every few squares and at the origin.
                const level = root.gridLevel()
                const step = level[0], perMajor = level[2]
                ctx.lineWidth = 1
                for (let i = Math.floor(root.wx(0) / step); i * step <= root.wx(width); i++) {
                    const x = i * step
                    ctx.strokeStyle = i === 0 ? "#6a6f78" : (i % perMajor === 0 ? "#484c53" : "#383b41")
                    ctx.beginPath(); ctx.moveTo(Math.round(root.sx(x)) + 0.5, 0); ctx.lineTo(Math.round(root.sx(x)) + 0.5, height); ctx.stroke()
                }
                for (let i = Math.floor(root.wy(height) / step); i * step <= root.wy(0); i++) {
                    const y = i * step
                    ctx.strokeStyle = i === 0 ? "#6a6f78" : (i % perMajor === 0 ? "#484c53" : "#383b41")
                    ctx.beginPath(); ctx.moveTo(0, Math.round(root.sy(y)) + 0.5); ctx.lineTo(width, Math.round(root.sy(y)) + 0.5); ctx.stroke()
                }
            }

            const showPoints = (s.tool === "EditPoints" || s.tool === "TransformPoints") && !root.uv
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
                        const chosen = p.selectedPoint === i / 2 || (p.selectedPoints !== undefined && p.selectedPoints.indexOf(i / 2) >= 0)
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
                const label = root.uv ? p.name + "  ·  " + (App.imperial ? (p.density * 2.54).toFixed(1) + " px/in" : p.density.toFixed(1) + " px/cm") : p.name
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
            if (s.marquee) {
                const x = root.sx(s.marquee[0]), y = root.sy(s.marquee[1])
                ctx.fillStyle = withAlpha(root.accent, 0.15)
                ctx.strokeStyle = root.accent; ctx.lineWidth = 1
                ctx.setLineDash([4, 3])
                ctx.fillRect(x, y, root.sx(s.marquee[2]) - x, root.sy(s.marquee[3]) - y)
                ctx.strokeRect(x + 0.5, y + 0.5, root.sx(s.marquee[2]) - x, root.sy(s.marquee[3]) - y)
                ctx.setLineDash([])
            }
            if (s.pointGizmo) {
                // Move arrows (red along X, green along Y) with a square to move freely, a ring to rotate, and
                // boxes to scale: one on each axis and a yellow one that scales evenly.
                const cx = root.sx(s.pointGizmo[0]), cy = root.sy(s.pointGizmo[1]), arrow = s.pointGizmo[2], ring = s.pointGizmo[3]
                const boxes = s.pointGizmo[4], even = s.pointGizmo[5]
                if (ring > 0) {
                    ctx.strokeStyle = "#3e7bdc"; ctx.lineWidth = 2.5
                    ctx.beginPath(); ctx.arc(cx, cy, ring, 0, Math.PI * 2); ctx.stroke()
                }
                for (const a of [["#e5484d", 1, 0], ["#46a758", 0, -1]]) {
                    if (boxes > 0) {
                        const bx = cx + a[1] * boxes, by = cy + a[2] * boxes
                        ctx.strokeStyle = a[0]; ctx.fillStyle = a[0]; ctx.lineWidth = 3
                        if (arrow === 0) { ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(bx, by); ctx.stroke() }
                        ctx.fillRect(bx - 6, by - 6, 12, 12)
                        ctx.strokeStyle = "#1b1c1f"; ctx.lineWidth = 1
                        ctx.strokeRect(bx - 5.5, by - 5.5, 12, 12)
                    }
                    if (arrow === 0)
                        continue
                    const ex = cx + a[1] * arrow, ey = cy + a[2] * arrow
                    ctx.strokeStyle = a[0]; ctx.fillStyle = a[0]; ctx.lineWidth = 3
                    ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(ex, ey); ctx.stroke()
                    ctx.beginPath()
                    ctx.moveTo(ex + a[1] * 10, ey + a[2] * 10)
                    ctx.lineTo(ex - a[2] * 6, ey + a[1] * 6)
                    ctx.lineTo(ex + a[2] * 6, ey - a[1] * 6)
                    ctx.closePath(); ctx.fill()
                }
                ctx.strokeStyle = "#1b1c1f"; ctx.lineWidth = 1
                if (arrow > 0) {
                    ctx.fillStyle = "#f2f3f5"
                    ctx.fillRect(cx - 6, cy - 6, 12, 12); ctx.strokeRect(cx - 5.5, cy - 5.5, 12, 12)
                }
                if (boxes > 0) {
                    ctx.fillStyle = "#ffd447"
                    ctx.fillRect(cx + even - 6, cy - even - 6, 12, 12); ctx.strokeRect(cx + even - 5.5, cy - even - 5.5, 12, 12)
                }
            }
            for (let i = 0; s.measures && i < s.measures.length; i += 5)
                measure(ctx, s.measures[i], s.measures[i + 1], root.lengthText(s.measures[i + 2]), s.measures[i + 3], s.measures[i + 4])
            for (let i = 0; s.draftMeasures && i < s.draftMeasures.length; i += 3)
                measure(ctx, s.draftMeasures[i], s.draftMeasures[i + 1], root.lengthText(s.draftMeasures[i + 2]), 0, 0)
            if (s.draftSize)
                measure(ctx, s.draftSize[0], s.draftSize[1], root.lengthText(s.draftSize[2]) + "  ×  " + root.lengthText(s.draftSize[3]), 0, 0)
            if (s.ruler) {
                // Ruler: a line between the two ends with end stops, a tick every grid square and the distance.
                const ax = root.sx(s.ruler[0]), ay = root.sy(s.ruler[1]), bx = root.sx(s.ruler[2]), by = root.sy(s.ruler[3])
                const len = Math.hypot(bx - ax, by - ay)
                const ux = len > 0 ? (bx - ax) / len : 1, uy = len > 0 ? (by - ay) / len : 0
                ctx.strokeStyle = "#ffd447"; ctx.lineWidth = 2
                ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by)
                for (const e of [[ax, ay], [bx, by]]) {
                    ctx.moveTo(e[0] - uy * 8, e[1] + ux * 8); ctx.lineTo(e[0] + uy * 8, e[1] - ux * 8)
                }
                ctx.stroke()
                const step = root.gridLevel()[0] * root.zoom
                ctx.lineWidth = 1
                ctx.beginPath()
                for (let d = step; d < len - 1 && len / step < 400; d += step) {
                    const tx = ax + ux * d, ty = ay + uy * d
                    ctx.moveTo(tx - uy * 4, ty + ux * 4); ctx.lineTo(tx + uy * 4, ty - ux * 4)
                }
                ctx.stroke()
                if (s.ruler[4] > 0) {
                    const dx = s.ruler[2] - s.ruler[0], dy = s.ruler[3] - s.ruler[1]
                    const angle = Math.atan2(dy, dx) * 180 / Math.PI
                    measure(ctx, (s.ruler[0] + s.ruler[2]) / 2, (s.ruler[1] + s.ruler[3]) / 2,
                            root.lengthText(s.ruler[4]) + "   " + angle.toFixed(0) + "°", -uy, -ux)
                }
            }

            if (!root.uv) {
                // Scale bar: one grid square, with its size.
                const level = root.gridLevel()
                const len = level[0] * root.zoom, x = 10.5, y = height - 12.5
                ctx.strokeStyle = "#c9ccd2"; ctx.lineWidth = 1
                ctx.beginPath()
                ctx.moveTo(x, y - 4); ctx.lineTo(x, y + 4)
                ctx.moveTo(x, y); ctx.lineTo(x + len, y)
                ctx.moveTo(x + len, y - 4); ctx.lineTo(x + len, y + 4)
                ctx.stroke()
                ctx.fillStyle = "#c9ccd2"
                ctx.font = "11px sans-serif"
                ctx.textAlign = "left"
                ctx.fillText(root.tr("1 square = %1").arg(level[1]), x + len + 8, y + 4)
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
        property real travelled: 0
        cursorShape: panning ? Qt.ClosedHandCursor : (root.scene.tool === "Select" || root.uv ? Qt.ArrowCursor : Qt.CrossCursor)

        onPressed: (e) => {
            root.forceActiveFocus()
            App.constrain = (e.modifiers & Qt.ShiftModifier) !== 0
            if (e.button === Qt.LeftButton && !(e.modifiers & Qt.AltModifier)) {
                App.pointerDown(root.wx(e.x), root.wy(e.y), root.tolerance(), (e.modifiers & Qt.ControlModifier) !== 0 || (e.modifiers & Qt.ShiftModifier) !== 0)
            } else {
                panning = true
                lastX = e.x; lastY = e.y
                travelled = 0
            }
        }
        onPositionChanged: (e) => {
            if (panning) {
                root.userAdjusted = true
                travelled += Math.abs(e.x - lastX) + Math.abs(e.y - lastY)
                root.panX += e.x - lastX
                root.panY -= e.y - lastY
                lastX = e.x; lastY = e.y
                canvas.requestPaint()
            } else {
                App.constrain = (e.modifiers & Qt.ShiftModifier) !== 0
                App.pointerMove(root.wx(e.x), root.wy(e.y), root.tolerance())
            }
            coords.text = root.uv
                ? "U " + root.wx(e.x).toFixed(3) + "   V " + root.wy(e.y).toFixed(3)
                : App.imperial ? (root.wx(e.x) / 2.54).toFixed(2) + ", " + (root.wy(e.y) / 2.54).toFixed(2) + " in"
                : root.wx(e.x).toFixed(1) + ", " + root.wy(e.y).toFixed(1) + " cm"
        }
        onReleased: (e) => {
            if (panning) {
                panning = false
                if (e.button === Qt.RightButton && travelled < 4) {
                    if (!root.uv)
                        App.contextSelect(root.wx(e.x), root.wy(e.y))
                    root.contextMenuRequested()
                }
            } else if (e.button === Qt.LeftButton) {
                App.pointerUp(root.wx(e.x), root.wy(e.y))
            }
        }
        onDoubleClicked: (e) => {
            if (e.button === Qt.LeftButton)
                App.doubleClick(root.wx(e.x), root.wy(e.y), root.tolerance(), (e.modifiers & Qt.ControlModifier) !== 0 || (e.modifiers & Qt.ShiftModifier) !== 0)
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
        text: root.uv ? tr("UV Layout") : tr("2D Pattern")
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
        text: tr("No pattern pieces yet.\nDraw one with the Polygon, Rectangle or Ellipse tool,\nor add a ready-made garment from the Garment menu.")
    }
}
