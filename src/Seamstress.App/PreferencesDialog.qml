import QtQuick
import QtQuick.Controls
import QtQuick.Dialogs
import QtQuick.Layouts

// Preferences, one page per topic: units and author, the 3D viewport, MakeHuman, textures, and keyboard shortcuts.
Dialog {
    id: root
    title: "Preferences"
    modal: true
    anchors.centerIn: parent
    width: 820
    height: 560
    standardButtons: Dialog.Save | Dialog.Cancel
    // Escape would close the dialog; while listening for a key it is the key being chosen, or cancels the choice.
    closePolicy: recording ? Popup.NoAutoClose : Popup.CloseOnEscape

    // Set while the Keyboard page waits for a key, so the key does not also run its command.
    readonly property bool recording: keyboard.recordingRow >= 0
    readonly property var pages: ["General", "Viewport", "MakeHuman", "Textures", "Keyboard"]
    property int page: 0
    function openPage(name) { page = Math.max(0, pages.indexOf(name)); open() }

    function load() {
        userFolder.text = App.settingMakeHumanUserFolder
        baseMesh.text = App.settingMakeHumanBaseMesh
        command.text = App.settingMakeHumanCommand
        author.text = App.settingAuthor
        license.editText = App.settingMakeHumanLicense
        license.currentIndex = license.find(App.settingMakeHumanLicense)
        renderer.currentIndex = Math.max(0, ["auto", "gpu", "cpu"].indexOf(App.settingRenderer))
        resolution.currentIndex = Math.max(0, resolution.model.indexOf(App.settingTextureResolution))
        units.currentIndex = App.settingUnits === "imperial" ? 1 : 0
        shownImperial = units.currentIndex === 1
        gridSize.text = shownLength(App.gridSizeCm)
        snapStep.text = shownLength(App.snapStepCm)
        keyboard.load()
    }

    // The grid size and snap step are typed in the units chosen in this dialog and stored in centimetres.
    property bool shownImperial: false
    readonly property real unitCm: shownImperial ? 2.54 : 1
    function shownLength(cm) { return String(Number((cm / unitCm).toFixed(shownImperial ? 3 : 2))) }
    function typedCm(field) { const v = Number(field.text); return isNaN(v) ? 0 : v * unitCm }
    function unitsChosen(imperial) {
        // Keep the lengths the same when the units change, except that the default becomes the new default.
        const convert = (field) => {
            const cm = typedCm(field), wasDefault = Math.abs(cm - (shownImperial ? 10.16 : 10)) < 0.01
            return wasDefault ? (imperial ? 10.16 : 10) : cm
        }
        const grid = convert(gridSize), snap = convert(snapStep)
        shownImperial = imperial
        gridSize.text = shownLength(grid)
        snapStep.text = shownLength(snap)
    }

    onAboutToShow: load()
    onClosed: keyboard.recordingRow = -1
    onAccepted: {
        App.savePreferences(userFolder.text, baseMesh.text, command.text,
                            ["auto", "gpu", "cpu"][renderer.currentIndex], resolution.model[resolution.currentIndex], author.text,
                            license.editText, units.currentIndex === 1 ? "imperial" : "metric", typedCm(gridSize), typedCm(snapStep))
        App.saveKeyBindings(JSON.stringify(keyboard.chosen()))
    }

    component Note: Label {
        Layout.fillWidth: true
        wrapMode: Text.WordWrap
        opacity: 0.6
        font.pixelSize: 11
    }

    RowLayout {
        anchors.fill: parent
        spacing: 12

        // Topics down the side.
        ListView {
            id: topics
            Layout.preferredWidth: 150
            Layout.fillHeight: true
            model: root.pages
            currentIndex: root.page
            interactive: false
            delegate: ItemDelegate {
                required property string modelData
                required property int index
                width: ListView.view.width
                text: modelData
                highlighted: ListView.isCurrentItem
                onClicked: root.page = index
            }
        }
        Rectangle { Layout.fillHeight: true; implicitWidth: 1; color: palette.mid }

        StackLayout {
            Layout.fillWidth: true
            Layout.fillHeight: true
            currentIndex: root.page

            // ---------------------------------------------------------- General
            ColumnLayout {
                spacing: 14
                GroupBox {
                    title: "Units"
                    Layout.fillWidth: true
                    GridLayout {
                        anchors.fill: parent
                        columns: 2
                        columnSpacing: 8
                        rowSpacing: 8
                        Label { text: "Measurements" }
                        ComboBox {
                            id: units
                            Layout.fillWidth: true
                            model: ["Metric (cm, m, g/m²)", "Imperial (in, oz/yd²)"]
                            onActivated: root.unitsChosen(currentIndex === 1)
                        }
                        Note {
                            Layout.columnSpan: 2
                            text: "Changes the units shown and typed in the interface. Garment files and exports are not affected."
                        }
                    }
                }
                Item { Layout.fillHeight: true }
            }

            // ---------------------------------------------------------- Viewport
            ColumnLayout {
                spacing: 14
                GroupBox {
                    title: "3D Viewport"
                    Layout.fillWidth: true
                    GridLayout {
                        anchors.fill: parent
                        columns: 2
                        columnSpacing: 8
                        rowSpacing: 8
                        Label { text: "Renderer" }
                        ComboBox {
                            id: renderer
                            Layout.fillWidth: true
                            model: ["Automatic (GPU, falls back to CPU)", "GPU", "CPU (software rendering)"]
                        }
                        Label { text: "Currently using" }
                        Label { text: App.renderer; opacity: 0.75 }
                        Note {
                            Layout.columnSpan: 2
                            text: "A renderer change takes effect the next time Seamstress starts."
                        }
                    }
                }
                GroupBox {
                    title: "Grid and Snapping"
                    Layout.fillWidth: true
                    GridLayout {
                        anchors.fill: parent
                        columns: 2
                        columnSpacing: 8
                        rowSpacing: 8
                        Label { text: "Grid size" }
                        RowLayout {
                            TextField {
                                id: gridSize
                                implicitWidth: 90
                                selectByMouse: true
                                horizontalAlignment: Text.AlignRight
                                validator: DoubleValidator { bottom: 0; notation: DoubleValidator.StandardNotation; locale: "C" }
                            }
                            Label { text: root.shownImperial ? "in" : "cm"; opacity: 0.7 }
                            Label { text: "One square of the 3D ground grid."; opacity: 0.6; font.pixelSize: 11; Layout.leftMargin: 8 }
                        }
                        Label { text: "Snap step" }
                        RowLayout {
                            TextField {
                                id: snapStep
                                implicitWidth: 90
                                selectByMouse: true
                                horizontalAlignment: Text.AlignRight
                                validator: DoubleValidator { bottom: 0; notation: DoubleValidator.StandardNotation; locale: "C" }
                            }
                            Label { text: root.shownImperial ? "in" : "cm"; opacity: 0.7 }
                            Label { text: "Hold Shift while dragging a 3D move arrow to snap to this."; opacity: 0.6; font.pixelSize: 11; Layout.leftMargin: 8 }
                        }
                        Note {
                            Layout.columnSpan: 2
                            text: "The 2D grid sizes itself to the zoom; Shift in Edit Points snaps to its smallest squares."
                        }
                    }
                }
                Item { Layout.fillHeight: true }
            }

            // ---------------------------------------------------------- MakeHuman
            ColumnLayout {
                spacing: 14
                GroupBox {
                    title: "MakeHuman"
                    Layout.fillWidth: true
                    GridLayout {
                        anchors.fill: parent
                        columns: 3
                        columnSpacing: 8
                        rowSpacing: 8

                        Label { text: "User data folder" }
                        TextField { id: userFolder; Layout.fillWidth: true; selectByMouse: true; placeholderText: "e.g. ~/makehuman/v1py3" }
                        Button { text: "Browse…"; onClicked: folderDialog.open() }

                        Label { text: "Base mesh" }
                        TextField { id: baseMesh; Layout.fillWidth: true; selectByMouse: true; placeholderText: "…/data/3dobjs/base.obj" }
                        Button { text: "Browse…"; onClicked: meshDialog.open() }

                        Label { text: "Launch command" }
                        TextField { id: command; Layout.fillWidth: true; selectByMouse: true; placeholderText: "optional, e.g. python3 /opt/makehuman/makehuman.py" }
                        Item {}

                        Label { text: "Author name" }
                        TextField { id: author; Layout.fillWidth: true; selectByMouse: true; placeholderText: App.defaultAuthor + " (your login name) when empty" }
                        Item {}

                        Label { text: "License" }
                        // Pick a common licence or type any other.
                        ComboBox {
                            id: license
                            Layout.fillWidth: true
                            editable: true
                            model: App.makeHumanLicenses().split("|")
                        }
                        Item {}

                        Item {}
                        RowLayout {
                            Layout.columnSpan: 2
                            Button {
                                text: "Detect Automatically"
                                onClicked: {
                                    status.text = App.detectMakeHuman() ? "Found a MakeHuman installation." : "No MakeHuman installation found in the usual places."
                                    userFolder.text = App.settingMakeHumanUserFolder
                                    baseMesh.text = App.settingMakeHumanBaseMesh
                                }
                            }
                            Label { id: status; opacity: 0.75; Layout.fillWidth: true; elide: Text.ElideRight }
                        }

                        Note {
                            Layout.columnSpan: 3
                            text: "“Send to MakeHuman” writes garments into <user data folder>/data/clothes and fits them to the base mesh. "
                                + "If a launch command is set, MakeHuman is started after sending when it is not already running. "
                                + "The author and license are written into every asset sent, and MakeHuman shows them in its clothes library. "
                                + "CC0 lets anyone use the asset freely; the CC-BY licenses ask for credit, and SA, NC and ND add share-alike, non-commercial and no-derivatives conditions."
                        }
                    }
                }
                Item { Layout.fillHeight: true }
            }

            // ---------------------------------------------------------- Textures
            ColumnLayout {
                spacing: 14
                GroupBox {
                    title: "Textures"
                    Layout.fillWidth: true
                    GridLayout {
                        anchors.fill: parent
                        columns: 3
                        columnSpacing: 8
                        rowSpacing: 8
                        Label { text: "Texture size for texel density" }
                        ComboBox { id: resolution; model: [512, 1024, 2048, 4096, 8192]; implicitWidth: 120 }
                        Label { text: "px"; opacity: 0.7; Layout.fillWidth: true }
                        Note {
                            Layout.columnSpan: 3
                            text: "The UV layout and the Properties panel report texel density (pixels per cm or inch) for a texture this size."
                        }
                    }
                }
                Item { Layout.fillHeight: true }
            }

            // ---------------------------------------------------------- Keyboard
            ColumnLayout {
                id: keyboard
                spacing: 8

                // Row being given a new key, or -1.
                property int recordingRow: -1
                property var conflicts: []

                function load() {
                    keyModel.clear()
                    for (const b of JSON.parse(App.keyBindingsJson()))
                        keyModel.append({ cid: b.id, group: b.group, name: b.name, keys: b.keys.join("\n"), defaults: b.defaults.join("\n") })
                    recordingRow = -1
                    search.text = ""
                    check()
                }
                function chosen() {
                    const map = {}
                    for (let i = 0; i < keyModel.count; i++) {
                        const row = keyModel.get(i)
                        map[row.cid] = row.keys === "" ? [] : row.keys.split("\n")
                    }
                    return map
                }
                function check() { conflicts = JSON.parse(App.keyConflicts(JSON.stringify(chosen()))) }
                function clashes(keys) {
                    if (keys === "")
                        return false
                    const list = keys.split("\n")
                    return conflicts.some(c => list.indexOf(c.key) >= 0)
                }
                function setKeys(row, keys) {
                    keyModel.setProperty(row, "keys", keys)
                    check()
                }

                // A key press written the way Qt reads shortcuts, with the modifiers in a fixed order; "" for a
                // modifier on its own.
                function keyName(e) {
                    const named = {
                        [Qt.Key_Escape]: "Escape", [Qt.Key_Tab]: "Tab", [Qt.Key_Backtab]: "Tab", [Qt.Key_Backspace]: "Backspace",
                        [Qt.Key_Return]: "Return", [Qt.Key_Enter]: "Enter", [Qt.Key_Insert]: "Insert", [Qt.Key_Delete]: "Delete",
                        [Qt.Key_Home]: "Home", [Qt.Key_End]: "End", [Qt.Key_PageUp]: "PgUp", [Qt.Key_PageDown]: "PgDown",
                        [Qt.Key_Left]: "Left", [Qt.Key_Up]: "Up", [Qt.Key_Right]: "Right", [Qt.Key_Down]: "Down",
                        [Qt.Key_Space]: "Space",
                    }
                    let key
                    if (named[e.key] !== undefined)
                        key = named[e.key]
                    else if (e.key >= Qt.Key_F1 && e.key <= Qt.Key_F35)
                        key = "F" + (e.key - Qt.Key_F1 + 1)
                    else if (e.key > 0x20 && e.key < 0x7f)
                        key = String.fromCharCode(e.key).toUpperCase()
                    else
                        return ""
                    const parts = []
                    if (e.modifiers & Qt.ControlModifier) parts.push("Ctrl")
                    if (e.modifiers & Qt.AltModifier) parts.push("Alt")
                    if ((e.modifiers & Qt.ShiftModifier) || e.key === Qt.Key_Backtab) parts.push("Shift")
                    if (e.modifiers & Qt.MetaModifier) parts.push("Meta")
                    if ((e.modifiers & Qt.KeypadModifier) && e.key >= Qt.Key_0 && e.key <= Qt.Key_9) parts.push("Num")
                    parts.push(key)
                    return parts.join("+")
                }

                RowLayout {
                    Layout.fillWidth: true
                    TextField {
                        id: search
                        Layout.fillWidth: true
                        selectByMouse: true
                        placeholderText: "Search commands or keys"
                    }
                    Button {
                        text: "Reset All"
                        onClicked: {
                            for (let i = 0; i < keyModel.count; i++)
                                keyModel.setProperty(i, "keys", keyModel.get(i).defaults)
                            keyboard.recordingRow = -1
                            keyboard.check()
                        }
                    }
                }

                ListModel { id: keyModel }

                ListView {
                    id: keyList
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    clip: true
                    model: keyModel
                    boundsBehavior: Flickable.StopAtBounds
                    ScrollBar.vertical: ScrollBar {}
                    section.property: "group"
                    section.delegate: Label {
                        required property string section
                        visible: search.text === ""
                        height: visible ? implicitHeight : 0
                        text: section
                        font.bold: true
                        topPadding: 8
                        bottomPadding: 2
                    }

                    delegate: Item {
                        id: keyRow
                        required property int index
                        required property string name
                        required property string keys
                        required property string defaults
                        readonly property string query: search.text.trim().toLowerCase()
                        readonly property bool matches: query === "" || name.toLowerCase().indexOf(query) >= 0
                                                        || keys.toLowerCase().split("\n").some(k => k === query || k.indexOf(query) >= 0)
                        readonly property bool listening: keyboard.recordingRow === index
                        width: ListView.view.width - 12
                        height: matches ? 32 : 0
                        visible: matches

                        RowLayout {
                            anchors.fill: parent
                            anchors.leftMargin: 8
                            spacing: 6
                            Label {
                                text: keyRow.name
                                Layout.fillWidth: true
                                elide: Text.ElideRight
                            }
                            // Click, then press the new key. Escape stops listening without a change.
                            Button {
                                id: keyButton
                                Layout.preferredWidth: 210
                                text: keyRow.listening ? "Press a key…" : keyRow.keys === "" ? "—" : keyRow.keys.split("\n").join(",  ")
                                highlighted: keyRow.listening
                                palette.buttonText: !keyRow.listening && keyboard.clashes(keyRow.keys) ? "#ff7b72" : root.palette.buttonText
                                onClicked: {
                                    keyboard.recordingRow = keyRow.listening ? -1 : keyRow.index
                                    if (keyboard.recordingRow >= 0)
                                        keyButton.forceActiveFocus()
                                }
                                Keys.onPressed: (e) => {
                                    if (!keyRow.listening)
                                        return
                                    e.accepted = true
                                    if (e.key === Qt.Key_Escape && e.modifiers === Qt.NoModifier) {
                                        keyboard.recordingRow = -1
                                        return
                                    }
                                    const name = keyboard.keyName(e)
                                    if (name === "")
                                        return
                                    keyboard.setKeys(keyRow.index, name)
                                    keyboard.recordingRow = -1
                                }
                                onActiveFocusChanged: if (!activeFocus && keyRow.listening) keyboard.recordingRow = -1
                                ToolTip.visible: hovered && !keyRow.listening
                                ToolTip.delay: 500
                                ToolTip.text: "Click, then press the new key (Escape cancels)"
                            }
                            ToolButton {
                                text: "✕"
                                enabled: keyRow.keys !== ""
                                onClicked: { keyboard.recordingRow = -1; keyboard.setKeys(keyRow.index, "") }
                                ToolTip.visible: hovered
                                ToolTip.delay: 500
                                ToolTip.text: "No key"
                            }
                            ToolButton {
                                text: "↺"
                                enabled: keyRow.keys !== keyRow.defaults
                                onClicked: { keyboard.recordingRow = -1; keyboard.setKeys(keyRow.index, keyRow.defaults) }
                                ToolTip.visible: hovered
                                ToolTip.delay: 500
                                ToolTip.text: keyRow.defaults === "" ? "Default: no key" : "Default: " + keyRow.defaults.split("\n").join(", ")
                            }
                        }
                    }
                }

                Label {
                    Layout.fillWidth: true
                    visible: keyboard.conflicts.length > 0
                    wrapMode: Text.WordWrap
                    color: "#ff7b72"
                    font.pixelSize: 11
                    text: keyboard.conflicts.map(c => c.key + " is used by " + c.names.join(" and ")).join(".  ")
                          + ". A key given to two commands does nothing until one of them is changed."
                }
                Note { text: "Changes take effect when you press Save." }
            }
        }
    }

    FolderDialog {
        id: folderDialog
        title: "MakeHuman User Data Folder"
        onAccepted: userFolder.text = App.pathFromUrl(selectedFolder.toString())
    }
    FileDialog {
        id: meshDialog
        title: "MakeHuman Base Mesh (base.obj)"
        nameFilters: ["Wavefront OBJ (*.obj)"]
        onAccepted: baseMesh.text = App.pathFromUrl(selectedFile.toString())
    }
}
