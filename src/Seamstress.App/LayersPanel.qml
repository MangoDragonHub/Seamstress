import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Layers: garments, the groups inside them, and their pieces, as an indented list.
Pane {
    id: root
    padding: 0

    property var rows: []
    function tr(text) { App.languageRevision; return App.tr(text) }
    property string editingId: ""
    property string menuId: ""
    property string menuKind: ""
    property var groupChoices: []

    // Rebuilding the list would throw away a name being typed, so wait until the rename is done.
    property bool refreshPending: false
    function refresh() {
        if (editingId !== "") {
            refreshPending = true
            return
        }
        refreshPending = false
        rows = JSON.parse(App.layers())
    }
    onEditingIdChanged: if (editingId === "" && refreshPending) Qt.callLater(refresh)
    Component.onCompleted: refresh()
    Connections {
        target: App
        function onLayersRevisionChanged() { Qt.callLater(root.refresh) }
    }

    // Where a dragged layer lands when dropped at height y of a row: dropping on a garment puts it at the end of the
    // garment's top level, the middle of a group puts it inside that group, and the upper or lower part of a row puts
    // it just before or after that row among its siblings, in any garment. Null when it cannot go there.
    function dropTarget(dragSource, to, y, height) {
        if (!dragSource || dragSource.modelData === undefined)
            return null
        const from = dragSource.modelData
        if (from.kind === "garment" || from.id === to.id)
            return null
        let place
        if (to.kind === "garment") {
            place = { zone: "into", garment: to.id, group: "", before: "" }
        } else {
            const parent = (to.kind === "group" ? to.parentId : to.groupId) || ""
            const edge = to.kind === "group" ? 0.25 : 0.5
            if (y < height * edge) {
                place = { zone: "before", garment: to.garment, group: parent, before: to.id }
            } else if (y >= height * (1 - edge)) {
                // Just below an open group is the top of its contents.
                if (to.kind === "group" && to.expanded && to.hasChildren)
                    place = { zone: "after", garment: to.garment, group: to.id, before: firstChild(to) }
                else
                    place = { zone: "after", garment: to.garment, group: parent, before: nextSibling(to) }
            } else {
                place = { zone: "into", garment: to.garment, group: to.id, before: "" }
            }
        }
        if (from.kind === "group" && place.group !== "" && (place.group === from.id || isDescendant(place.group, from.id)))
            return null
        return place
    }
    function nextSibling(r) {
        for (let i = rows.findIndex(x => x.id === r.id) + 1; i > 0 && i < rows.length; i++) {
            if (rows[i].depth < r.depth)
                break
            if (rows[i].depth === r.depth)
                return rows[i].id
        }
        return ""
    }
    function firstChild(r) {
        const i = rows.findIndex(x => x.id === r.id)
        return i >= 0 && i + 1 < rows.length && rows[i + 1].depth === r.depth + 1 ? rows[i + 1].id : ""
    }
    function isDescendant(groupId, ancestorId) {
        let current = rows.find(r => r.id === groupId)
        for (let guard = 0; current && current.parentId && guard < 64; guard++) {
            if (current.parentId === ancestorId)
                return true
            current = rows.find(r => r.id === current.parentId)
        }
        return false
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 0

        Label {
            text: tr("Layers")
            font.bold: true
            padding: 8
            Layout.fillWidth: true
        }

        ListView {
            id: list
            Layout.fillWidth: true
            Layout.fillHeight: true
            clip: true
            model: root.rows
            boundsBehavior: Flickable.StopAtBounds
            ScrollBar.vertical: ScrollBar {}

            delegate: Rectangle {
                id: row
                required property var modelData
                width: list.width
                height: 26
                property string dropZone: ""
                property real grabX: 0
                property real grabY: 0
                property int dragCount: 1  // dragging one of several selected pieces takes them all
                function startDrag(x, y) {
                    grabX = x
                    grabY = y
                    dragCount = App.layersToMoveCount(modelData.id)
                    const p = mapToItem(Overlay.overlay, 0, 0)
                    dragProxy.x = p.x
                    dragProxy.y = p.y
                }
                color: row.dropZone === "into" ? Qt.alpha(palette.highlight, 0.7)
                     : (modelData.selected ? Qt.alpha(palette.highlight, 0.45)
                     : (modelData.kind === "garment" && modelData.active ? Qt.alpha(palette.highlight, 0.14) : "transparent"))

                // A copy of the row that follows the cursor during a drag; kept in the window overlay so the
                // list cannot clip it.
                Rectangle {
                    id: dragProxy
                    parent: Overlay.overlay
                    width: row.width
                    height: row.height
                    visible: rowMouse.drag.active || gripMouse.drag.active
                    z: 1000
                    radius: 4
                    color: palette.highlight
                    opacity: 0.9
                    border.color: palette.highlightedText
                    border.width: 1

                    Label {
                        anchors.fill: parent
                        anchors.leftMargin: 8 + row.modelData.depth * 14
                        anchors.rightMargin: 8
                        verticalAlignment: Text.AlignVCenter
                        elide: Text.ElideRight
                        text: row.dragCount > 1 ? tr("%1 and %2 more").arg(row.modelData.name).arg(row.dragCount - 1) : row.modelData.name
                        color: palette.highlightedText
                    }

                    Drag.active: rowMouse.drag.active || gripMouse.drag.active
                    Drag.source: row
                    Drag.keys: ["seamstress-layer"]
                    Drag.mimeData: ({ "application/x-seamstress-layer": row.modelData.id })
                    Drag.hotSpot.x: row.grabX
                    Drag.hotSpot.y: row.grabY
                }

                MouseArea {
                    id: rowMouse
                    anchors.fill: parent
                    acceptedButtons: Qt.LeftButton | Qt.RightButton
                    drag.target: row.modelData.kind === "garment" ? null : dragProxy
                    drag.threshold: 12
                    drag.smoothed: false
                    onPressed: (e) => {
                        if (e.button === Qt.LeftButton && row.modelData.kind !== "garment")
                            row.startDrag(e.x, e.y)
                    }
                    onClicked: (e) => {
                        if (e.button === Qt.RightButton) {
                            if (!row.modelData.selected)
                                App.selectLayer(row.modelData.id, false)
                            root.menuId = row.modelData.id
                            root.menuKind = row.modelData.kind
                            root.groupChoices = JSON.parse(App.groupChoices())
                            contextMenu.popup()
                        } else {
                            App.selectLayer(row.modelData.id, (e.modifiers & Qt.ControlModifier) !== 0 || (e.modifiers & Qt.ShiftModifier) !== 0)
                        }
                    }
                    onDoubleClicked: (e) => { if (e.button === Qt.LeftButton) root.editingId = row.modelData.id }
                    // Internal drags are cancelled when Drag.active becomes false on release, so
                    // deliver the drop explicitly while the target still contains the drag.
                    onReleased: if (dragProxy.Drag.active) dragProxy.Drag.drop()
                }

                DropArea {
                    anchors.fill: parent
                    keys: ["seamstress-layer"]
                    function track(drag) {
                        const place = root.dropTarget(drag.source, row.modelData, drag.y, row.height)
                        row.dropZone = place ? place.zone : ""
                    }
                    onEntered: (drag) => {
                        drag.accept()
                        track(drag)
                    }
                    onPositionChanged: (drag) => track(drag)
                    onExited: row.dropZone = ""
                    onDropped: (drop) => {
                        row.dropZone = ""
                        const place = root.dropTarget(drop.source, row.modelData, drop.y, row.height)
                        if (place)
                            App.moveLayer(drop.source.modelData.id, place.garment, place.group, place.before)
                    }
                }

                // Insertion line for dropping before or after this row; below an open group it is indented to show
                // the layer goes in at the top of the group.
                Rectangle {
                    visible: row.dropZone === "before" || row.dropZone === "after"
                    x: 6 + (row.modelData.depth + (row.dropZone === "after" && row.modelData.kind === "group"
                            && row.modelData.expanded && row.modelData.hasChildren ? 1 : 0)) * 14
                    width: row.width - x - 6
                    height: 2
                    y: row.dropZone === "before" ? 0 : row.height - height
                    z: 2
                    color: palette.highlight
                }

                RowLayout {
                    anchors.fill: parent
                    anchors.leftMargin: 6 + row.modelData.depth * 14
                    anchors.rightMargin: 6
                    spacing: 4

                    // Drag handle: pieces and groups can be dragged into a group or another garment, or reordered.
                    Label {
                        Layout.preferredWidth: 8
                        text: "⋮⋮"
                        font.pixelSize: 10
                        opacity: row.modelData.kind === "garment" ? 0 : 0.35
                        MouseArea {
                            id: gripMouse
                            anchors.fill: parent
                            anchors.margins: -4
                            enabled: row.modelData.kind !== "garment"
                            preventStealing: true
                            drag.target: dragProxy
                            drag.threshold: 4
                            drag.smoothed: false
                            cursorShape: Qt.OpenHandCursor
                            onPressed: (e) => {
                                const p = mapToItem(row, e.x, e.y)
                                row.startDrag(p.x, p.y)
                            }
                            onReleased: if (dragProxy.Drag.active) dragProxy.Drag.drop()
                        }
                    }

                    // Fold arrow for garments and groups.
                    Label {
                        Layout.preferredWidth: 12
                        text: row.modelData.hasChildren ? (row.modelData.expanded ? "▾" : "▸") : ""
                        opacity: 0.8
                        MouseArea {
                            anchors.fill: parent
                            anchors.margins: -4
                            enabled: row.modelData.hasChildren
                            onClicked: App.setExpanded(row.modelData.id, !row.modelData.expanded)
                        }
                    }

                    Rectangle {
                        visible: row.modelData.kind === "piece"
                        Layout.preferredWidth: 10; Layout.preferredHeight: 10
                        radius: 2
                        color: row.modelData.color || "transparent"
                        border.color: Qt.alpha(palette.windowText, 0.4)
                    }
                    Label {
                        visible: row.modelData.kind !== "piece"
                        text: row.modelData.kind === "garment" ? "◆" : "▣"
                        opacity: 0.7
                        font.pixelSize: 11
                    }

                    Label {
                        visible: root.editingId !== row.modelData.id
                        Layout.fillWidth: true
                        elide: Text.ElideRight
                        text: row.modelData.name
                        font.bold: row.modelData.kind === "garment"
                        opacity: row.modelData.visible ? 1 : 0.45
                    }
                    TextField {
                        id: nameEdit
                        visible: root.editingId === row.modelData.id
                        Layout.fillWidth: true
                        implicitHeight: 22
                        padding: 2
                        text: row.modelData.name
                        selectByMouse: true
                        function start() { text = row.modelData.name; forceActiveFocus(); selectAll() }
                        // Enter or clicking elsewhere keeps the new name; Escape leaves it as it was.
                        function finish(keep) {
                            if (root.editingId !== row.modelData.id)
                                return
                            const id = row.modelData.id, value = text.trim()
                            root.editingId = ""
                            if (keep && value !== "" && value !== row.modelData.name)
                                App.rename(id, value)
                        }
                        onVisibleChanged: if (visible) start()
                        onAccepted: finish(true)
                        onActiveFocusChanged: if (!activeFocus && visible) finish(true)
                        Keys.onEscapePressed: finish(false)
                    }

                    Label {
                        visible: row.modelData.kind === "garment" && root.editingId !== row.modelData.id
                        text: row.modelData.kind === "garment" ? tr("%1 faces").arg(row.modelData.faces.toLocaleString(Qt.locale(), "f", 0)) : ""
                        font.pixelSize: 10
                        opacity: 0.55
                    }

                    // Garments can opt in to being simulated together with whichever garment is active.
                    ToolButton {
                        visible: row.modelData.kind === "garment"
                        implicitWidth: 22; implicitHeight: 22
                        padding: 0
                        text: "⇄"
                        font.pixelSize: 12
                        checkable: true
                        checked: row.modelData.kind === "garment" && row.modelData.simulateWithOthers
                        opacity: checked ? 1 : 0.35
                        onClicked: App.setSimulateWithOthers(row.modelData.id, checked)
                        ToolTip.visible: hovered
                        ToolTip.delay: 400
                        ToolTip.text: checked ? tr("Simulated together with the active garment") : tr("Only simulated when it is the active garment")
                    }

                    ToolButton {
                        implicitWidth: 22; implicitHeight: 22
                        padding: 0
                        text: row.modelData.visible ? "◉" : "○"
                        font.pixelSize: 12
                        opacity: row.modelData.visible ? 0.9 : 0.4
                        onClicked: App.setVisible(row.modelData.id, !row.modelData.visible)
                        ToolTip.visible: hovered
                        ToolTip.delay: 400
                        ToolTip.text: row.modelData.visible ? tr("Hide") : tr("Show")
                    }
                }
            }

            Label {
                anchors.centerIn: parent
                visible: root.rows.length === 0
                opacity: 0.5
                horizontalAlignment: Text.AlignHCenter
                text: tr("No garments yet")
            }
        }

        RowLayout {
            Layout.fillWidth: true
            Layout.margins: 4
            spacing: 4
            Button { text: tr("+ Garment"); Layout.fillWidth: true; onClicked: App.addGarment() }
            Button { text: tr("+ Group"); Layout.fillWidth: true; onClicked: App.addGroup(); enabled: root.rows.length > 0 }
            Button { text: tr("Delete"); Layout.fillWidth: true; onClicked: App.deleteSelection(); enabled: root.rows.some(r => r.selected) }
        }
    }

    Menu {
        id: contextMenu
        MenuItem { text: tr("Rename"); onTriggered: root.editingId = root.menuId }
        MenuItem { text: tr("Duplicate"); enabled: root.menuKind === "piece"; onTriggered: App.duplicate() }
        MenuSeparator {}
        MenuItem { text: tr("Group Selected Pieces"); enabled: root.menuKind === "piece"; onTriggered: App.groupSelection() }
        MenuItem { text: tr("Ungroup"); enabled: root.menuKind === "group"; onTriggered: App.ungroup(root.menuId) }
        Menu {
            id: moveMenu
            title: tr("Move to Group")
            enabled: root.menuKind !== "garment"
            MenuItem { text: tr("(Top level)"); onTriggered: App.moveLayer(root.menuId, "", "", "") }
            Instantiator {
                model: root.groupChoices
                delegate: MenuItem {
                    required property var modelData
                    text: modelData.name
                    enabled: modelData.id !== root.menuId
                    onTriggered: App.moveLayer(root.menuId, modelData.garment, modelData.id, "")
                }
                onObjectAdded: (index, object) => moveMenu.insertItem(index + 1, object)
                onObjectRemoved: (index, object) => moveMenu.removeItem(object)
            }
        }
        MenuSeparator {}
        MenuItem { text: tr("Delete"); onTriggered: App.deleteLayer(root.menuId) }
    }
}
