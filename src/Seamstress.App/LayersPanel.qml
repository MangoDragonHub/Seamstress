import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Layers: garments, the groups inside them, and their pieces, as an indented list.
Pane {
    id: root
    padding: 0

    property var rows: []
    property string editingId: ""
    property string menuId: ""
    property string menuKind: ""
    property var groupChoices: []

    function refresh() { rows = JSON.parse(App.layers()) }
    Component.onCompleted: refresh()
    Connections {
        target: App
        function onLayersRevisionChanged() { Qt.callLater(root.refresh) }
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 0

        Label {
            text: "Layers"
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
                        onVisibleChanged: if (visible) { text = row.modelData.name; forceActiveFocus(); selectAll() }
                        onAccepted: {
                            const id = row.modelData.id, value = text
                            root.editingId = ""
                            if (value.trim() !== "" && value !== row.modelData.name)
                                App.rename(id, value)
                        }
                        onActiveFocusChanged: if (!activeFocus && visible) root.editingId = ""
                        Keys.onEscapePressed: root.editingId = ""
                    }

                    Label {
                        visible: row.modelData.kind === "garment" && root.editingId !== row.modelData.id
                        text: row.modelData.kind === "garment" ? row.modelData.faces.toLocaleString(Qt.locale(), "f", 0) + " faces" : ""
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
                        ToolTip.text: checked ? "Simulated together with the active garment" : "Only simulated when it is the active garment"
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
                        ToolTip.text: row.modelData.visible ? "Hide" : "Show"
                    }
                }
            }

            Label {
                anchors.centerIn: parent
                visible: root.rows.length === 0
                opacity: 0.5
                horizontalAlignment: Text.AlignHCenter
                text: "No garments yet"
            }
        }

        RowLayout {
            Layout.fillWidth: true
            Layout.margins: 4
            spacing: 4
            Button { text: "+ Garment"; Layout.fillWidth: true; onClicked: App.addGarment() }
            Button { text: "+ Group"; Layout.fillWidth: true; onClicked: App.addGroup(); enabled: root.rows.length > 0 }
            Button { text: "Delete"; Layout.fillWidth: true; onClicked: App.deleteSelection(); enabled: root.rows.some(r => r.selected) }
        }
    }

    Menu {
        id: contextMenu
        MenuItem { text: "Rename"; onTriggered: root.editingId = root.menuId }
        MenuItem { text: "Duplicate"; enabled: root.menuKind === "piece"; onTriggered: App.duplicate() }
        MenuSeparator {}
        MenuItem { text: "Group Selected Pieces"; enabled: root.menuKind === "piece"; onTriggered: App.groupSelection() }
        MenuItem { text: "Ungroup"; enabled: root.menuKind === "group"; onTriggered: App.ungroup(root.menuId) }
        Menu {
            id: moveMenu
            title: "Move to Group"
            enabled: root.menuKind !== "garment"
            MenuItem { text: "(Top level)"; onTriggered: App.moveToGroup(root.menuId, "") }
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
        MenuItem { text: "Delete"; onTriggered: App.deleteLayer(root.menuId) }
    }
}
