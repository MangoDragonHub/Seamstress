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
                color: modelData.selected ? Qt.alpha(palette.highlight, 0.45)
                     : (modelData.kind === "garment" && modelData.active ? Qt.alpha(palette.highlight, 0.14) : "transparent")

                MouseArea {
                    anchors.fill: parent
                    acceptedButtons: Qt.LeftButton | Qt.RightButton
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
                }

                RowLayout {
                    anchors.fill: parent
                    anchors.leftMargin: 6 + row.modelData.depth * 14
                    anchors.rightMargin: 6
                    spacing: 4

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
            MenuItem { text: tr("(Top level)"); onTriggered: App.moveToGroup(root.menuId, "") }
            Instantiator {
                model: root.groupChoices
                delegate: MenuItem {
                    required property var modelData
                    text: modelData.name
                    enabled: modelData.id !== root.menuId
                    onTriggered: App.moveToGroup(root.menuId, modelData.id)
                }
                onObjectAdded: (index, object) => moveMenu.insertItem(index + 1, object)
                onObjectRemoved: (index, object) => moveMenu.removeItem(object)
            }
        }
        MenuSeparator {}
        MenuItem { text: tr("Delete"); onTriggered: App.deleteLayer(root.menuId) }
    }
}
