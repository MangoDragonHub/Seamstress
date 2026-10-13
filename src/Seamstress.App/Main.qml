import QtQuick
import QtQuick.Controls
import QtQuick.Dialogs
import QtQuick.Layouts
import QtCore

ApplicationWindow {
    id: window
    visible: true
    width: App.startupWidth > 0 ? App.startupWidth : 1500
    height: App.startupHeight > 0 ? App.startupHeight : 900
    minimumWidth: 900
    minimumHeight: 560
    title: App.windowTitle

    // Dark colour scheme shared by every control.
    palette {
        window: "#2b2d31"; windowText: "#e4e6ea"
        base: "#1e1f22"; alternateBase: "#25272b"; text: "#e4e6ea"
        button: "#3a3d43"; buttonText: "#e4e6ea"
        highlight: "#4f8fd9"; highlightedText: "#ffffff"
        mid: "#4a4d54"; dark: "#191a1d"; light: "#5a5e66"; midlight: "#454850"; shadow: "#101012"
        placeholderText: "#8c9099"; toolTipBase: "#3a3d43"; toolTipText: "#e4e6ea"
        brightText: "#ffffff"; link: "#6aa7ee"
    }

    property bool viewportConfirmed: false
    property var afterDiscard: null      // what to do once unsaved changes were dealt with
    property bool closing: false
    property string pendingExport: ""

    onFrameSwapped: {
        if (!viewportConfirmed) {
            viewportConfirmed = true
            App.viewportReady()
        }
    }

    onClosing: (close) => {
        if (App.dirty && !closing) {
            close.accepted = false
            guard(() => { closing = true; window.close() })
        } else {
            App.shutdown()
        }
    }

    // Runs an action immediately, or after asking what to do with unsaved changes.
    function guard(action) {
        if (!App.dirty) {
            action()
            return
        }
        afterDiscard = action
        unsavedDialog.open()
    }

    function save(then) {
        if (App.hasFile) {
            if (App.save() && then) then()
        } else {
            saveDialog.then = then || null
            saveDialog.currentFile = saveDialog.currentFolder + "/" + App.suggestedFileName(".garment.json")
            saveDialog.open()
        }
    }

    function startExport(format) {
        pendingExport = format
        exportOptions.open()
    }

    // Applies the newest simulation frame about 30 times a second; cheap when nothing is running.
    Timer { interval: 33; running: true; repeat: true; onTriggered: App.tick() }

    // Used by automated runs: capture the window, then quit.
    Timer {
        interval: App.startupSmoke ? 6000 : 3500
        running: App.startupScreenshot !== ""
        onTriggered: {
            // "--view prefs|export|makehuman" captures that dialog instead of the main window.
            const view = App.startupView
            const dialog = view.indexOf("prefs") >= 0 ? preferences : view.indexOf("export") >= 0 ? exportOptions
                         : view.indexOf("makehuman") >= 0 ? makeHumanDialog
                         : view.indexOf("bodyshape") >= 0 ? bodyShape : null
            const target = dialog ? dialog.contentItem.parent : rootLayout
            target.grabToImage(function(result) {
                result.saveToFile(App.startupScreenshot)
                window.closing = true
                Qt.quit()
            })
        }
    }

    Timer {
        interval: 1500
        running: App.startupScreenshot !== ""
        onTriggered: {
            const view = App.startupView
            if (view.indexOf("prefs") >= 0) preferences.open()
            if (view.indexOf("export") >= 0) { window.pendingExport = "fbx"; exportOptions.open() }
            if (view.indexOf("makehuman") >= 0) makeHumanDialog.open()
            if (view.indexOf("bodyshape") >= 0) bodyShape.open()
        }
    }

    // Automated interface check ("--smoke <folder>"), loaded only when asked for.
    Loader {
        id: smokeLoader
        active: App.startupSmoke
        source: "SmokeTest.qml"
        onLoaded: {
            item.folder = App.startupFolderUrl
            item.run()
            smokeFinish.start()
        }
    }
    Timer { id: smokeFinish; interval: 4000; onTriggered: smokeLoader.item.finish() }

    Connections {
        target: App
        function onErrorRevisionChanged() {
            if (App.startupSmoke) {
                App.log("  ERROR " + App.lastError)
                return
            }
            errorDialog.text = App.lastError
            errorDialog.open()
        }
    }

    // ------------------------------------------------------------------ shortcuts
    Shortcut { sequences: [StandardKey.New]; onActivated: newAction.trigger() }
    Shortcut { sequences: [StandardKey.Open]; onActivated: openAction.trigger() }
    Shortcut { sequences: [StandardKey.Save]; onActivated: saveAction.trigger() }
    Shortcut { sequence: "Ctrl+Shift+S"; onActivated: saveAsAction.trigger() }
    Shortcut { sequences: [StandardKey.Undo]; onActivated: App.undo() }
    Shortcut { sequences: ["Ctrl+Shift+Z", "Ctrl+Y"]; onActivated: App.redo() }
    Shortcut { sequences: ["Delete", "Backspace"]; onActivated: App.deleteSelection() }
    Shortcut { sequence: "Ctrl+D"; onActivated: App.duplicate() }
    Shortcut { sequence: "Ctrl+G"; onActivated: App.groupSelection() }
    Shortcut { sequence: "Ctrl+A"; onActivated: App.selectAll() }
    Shortcut { sequence: "Space"; onActivated: App.toggleSimulation() }
    Shortcut { sequence: "Escape"; onActivated: App.cancelTool() }
    Shortcut { sequences: ["Return", "Enter"]; onActivated: App.confirmTool() }
    Shortcut { sequence: "V"; onActivated: App.tool = "Select" }
    Shortcut { sequence: "E"; onActivated: App.tool = "EditPoints" }
    Shortcut { sequence: "T"; onActivated: App.tool = "TransformPoints" }
    Shortcut { sequence: "S"; onActivated: App.tool = "Sew" }
    Shortcut { sequence: "P"; onActivated: App.tool = "Polygon" }
    Shortcut { sequence: "R"; onActivated: App.tool = "Rectangle" }
    Shortcut { sequence: "O"; onActivated: App.tool = "Ellipse" }
    Shortcut { sequence: "U"; onActivated: App.uvMode = !App.uvMode }
    Shortcut { sequence: "F"; onActivated: patternView.frameAll() }
    Shortcut { sequence: "G"; onActivated: viewport.pinchMode = !viewport.pinchMode }

    Action { id: newAction; text: "&New"; onTriggered: window.guard(() => App.newDocument()) }
    Action { id: openAction; text: "&Open…"; onTriggered: window.guard(() => openDialog.open()) }
    Action { id: saveAction; text: "&Save"; onTriggered: window.save(null) }
    Action {
        id: saveAsAction
        text: "Save &As…"
        onTriggered: {
            saveDialog.then = null
            saveDialog.currentFile = saveDialog.currentFolder + "/" + App.suggestedFileName(".garment.json")
            saveDialog.open()
        }
    }

    ColumnLayout {
        id: rootLayout
        anchors.fill: parent
        spacing: 0

        // ------------------------------------------------------------------ menus
        MenuBar {
            Layout.fillWidth: true
            Menu {
                title: "&File"
                MenuItem { action: newAction }
                MenuItem { action: openAction }
                Menu {
                    id: recentMenu
                    title: "Open &Recent"
                    property var files: []
                    enabled: files.length > 0
                    onAboutToShow: files = App.recentFiles().split("|").filter(f => f !== "")
                    Component.onCompleted: files = App.recentFiles().split("|").filter(f => f !== "")
                    Instantiator {
                        model: recentMenu.files
                        delegate: MenuItem {
                            required property string modelData
                            text: App.fileName(modelData)
                            onTriggered: window.guard(() => App.open(modelData))
                        }
                        onObjectAdded: (index, object) => recentMenu.insertItem(index, object)
                        onObjectRemoved: (index, object) => recentMenu.removeItem(object)
                    }
                }
                MenuSeparator {}
                MenuItem { action: saveAction }
                MenuItem { action: saveAsAction }
                MenuSeparator {}
                Menu {
                    title: "&Export"
                    MenuItem { text: "Wavefront &OBJ…"; onTriggered: window.startExport("obj") }
                    MenuItem { text: "&glTF 2.0 (.glb / .gltf)…"; onTriggered: window.startExport("gltf") }
                    MenuItem { text: "&FBX…"; onTriggered: window.startExport("fbx") }
                    MenuSeparator {}
                    MenuItem { text: "Send to &MakeHuman"; onTriggered: makeHumanDialog.open() }
                }
                MenuSeparator {}
                MenuItem { text: "&Preferences…"; onTriggered: preferences.open() }
                MenuSeparator {}
                MenuItem { text: "&Quit"; onTriggered: window.close() }
            }
            Menu {
                title: "&Edit"
                MenuItem { text: App.undoText; enabled: App.canUndo; onTriggered: App.undo() }
                MenuItem { text: App.redoText; enabled: App.canRedo; onTriggered: App.redo() }
                MenuSeparator {}
                MenuItem { text: "Select All in Garment"; onTriggered: App.selectAll() }
                MenuItem { text: "Duplicate"; onTriggered: App.duplicate() }
                MenuItem { text: "Delete"; onTriggered: App.deleteSelection() }
                MenuSeparator {}
                MenuItem { text: "Mirror Piece"; onTriggered: App.mirror() }
                MenuItem { text: "Rotate 90° Left"; onTriggered: App.rotate(90) }
                MenuItem { text: "Rotate 90° Right"; onTriggered: App.rotate(-90) }
                MenuItem { text: "Straighten Curved Edges"; onTriggered: App.straightenEdges() }
                MenuSeparator {}
                MenuItem { text: "Group Selected Pieces"; onTriggered: App.groupSelection() }
                MenuItem { text: "Reverse Seam Direction"; onTriggered: App.reverseSeam() }
            }
            Menu {
                id: garmentMenu
                title: "&Garment"
                MenuItem { text: "New Empty Garment"; onTriggered: App.addGarment() }
                MenuItem { text: "New Group"; onTriggered: App.addGroup() }
                MenuSeparator {}
                Instantiator {
                    model: App.templateNames().split("|")
                    delegate: MenuItem {
                        required property string modelData
                        text: "Add " + modelData
                        onTriggered: App.addTemplate(modelData)
                    }
                    onObjectAdded: (index, object) => garmentMenu.insertItem(index + 3, object)
                    onObjectRemoved: (index, object) => garmentMenu.removeItem(object)
                }
            }
            Menu {
                title: "&Simulation"
                MenuItem { text: App.simulating ? "Stop Simulation" : "Simulate"; onTriggered: App.toggleSimulation() }
                MenuItem {
                    text: "Simulate All Garments"
                    checkable: true
                    checked: App.simulateAll
                    onTriggered: App.simulateAll = checked
                }
                MenuSeparator {}
                MenuItem { text: "Reset Arrangement"; onTriggered: App.resetArrangement() }
            }
            Menu {
                title: "&Avatar"
                MenuItem { text: "Mannequin"; checkable: true; checked: App.avatarType === "Mannequin"; onTriggered: App.setAvatar("Mannequin", "", 1) }
                MenuItem { text: "MakeHuman Base Mesh"; checkable: true; checked: App.avatarType === "MakeHuman"; onTriggered: App.setAvatar("MakeHuman", "", 1) }
                MenuItem { text: "MakeHuman Body Shape…"; enabled: App.avatarType === "MakeHuman"; onTriggered: bodyShape.open() }
                MenuItem { text: "Load OBJ…"; checkable: true; checked: App.avatarType === "CustomObj"; onTriggered: avatarDialog.open() }
                MenuItem { text: "None"; checkable: true; checked: App.avatarType === "None"; onTriggered: App.setAvatar("None", "", 1) }
            }
            Menu {
                title: "&View"
                MenuItem { text: "2D: Pattern"; checkable: true; checked: !App.uvMode; onTriggered: App.uvMode = false }
                MenuItem { text: "2D: UV Layout"; checkable: true; checked: App.uvMode; onTriggered: App.uvMode = true }
                MenuItem { text: "Frame All in 2D"; onTriggered: patternView.frameAll() }
                MenuSeparator {}
                MenuItem { text: "3D: Checker (Texel Density)"; checkable: true; checked: viewport.displayMode === "checker"; onTriggered: viewport.displayMode = checked ? "checker" : "shaded" }
                MenuItem { text: "3D: Wireframe (Topology)"; checkable: true; checked: viewport.wireframe; onTriggered: viewport.wireframe = checked }
                MenuItem { text: "3D: Pinch Cloth"; checkable: true; checked: viewport.pinchMode; onTriggered: viewport.pinchMode = checked }
                MenuItem { text: "3D: Move / Rotate Handles"; checkable: true; checked: viewport.showGizmo; onTriggered: viewport.showGizmo = checked }
                MenuItem { text: "3D: Show Avatar"; checkable: true; checked: viewport.showAvatar; onTriggered: viewport.showAvatar = checked }
                MenuItem { text: "3D: Show Sewing Threads"; checkable: true; checked: viewport.showThreads; onTriggered: viewport.showThreads = checked }
            }
            Menu {
                title: "&Help"
                MenuItem { text: "About Seamstress"; onTriggered: aboutDialog.open() }
            }
        }

        // ------------------------------------------------------------------ toolbar
        ToolBar {
            Layout.fillWidth: true
            RowLayout {
                anchors.fill: parent
                anchors.leftMargin: 6
                anchors.rightMargin: 6
                spacing: 2

                component ToolChoice: ToolButton {
                    property string tool: ""
                    property string tip: ""
                    checkable: true
                    checked: App.tool === tool && !App.uvMode
                    enabled: !App.uvMode || tool === "Select"
                    onClicked: App.tool = tool
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tip
                }
                ToolChoice { text: "Select"; tool: "Select"; tip: "Select and move pieces (V)" }
                ToolChoice { text: "Edit Points"; tool: "EditPoints"; tip: "Move points, curve edges, add points (E)" }
                ToolChoice { text: "Transform Points"; tool: "TransformPoints"; tip: "Select points and move or rotate them with handles (T)" }
                ToolChoice { text: "Sew"; tool: "Sew"; tip: "Click two edges to sew them together (S)" }
                ToolSeparator {}
                ToolChoice { text: "Polygon"; tool: "Polygon"; tip: "Draw a piece corner by corner (P)" }
                ToolChoice { text: "Rectangle"; tool: "Rectangle"; tip: "Drag out a rectangular piece (R)" }
                ToolChoice { text: "Ellipse"; tool: "Ellipse"; tip: "Drag out an elliptical piece (O)" }
                ToolSeparator {}
                ToolButton { text: "↶"; font.pixelSize: 18; enabled: App.canUndo; onClicked: App.undo(); ToolTip.visible: hovered; ToolTip.text: App.undoText }
                ToolButton { text: "↷"; font.pixelSize: 18; enabled: App.canRedo; onClicked: App.redo(); ToolTip.visible: hovered; ToolTip.text: App.redoText }
                ToolSeparator {}
                ToolButton {
                    text: App.uvMode ? "UV Layout" : "Pattern"
                    checkable: true
                    checked: App.uvMode
                    onClicked: App.uvMode = checked
                    ToolTip.visible: hovered
                    ToolTip.text: "Switch the 2D view between the pattern and its UV layout (U)"
                }

                Item { Layout.fillWidth: true }

                CheckBox {
                    text: "All garments"
                    checked: App.simulateAll
                    onToggled: App.simulateAll = checked
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: "Off: only the active garment (and garments linked with ⇄) is simulated.\nOn: every visible garment is simulated."
                }
                Button {
                    id: simulateButton
                    text: App.simulating ? "■  Stop" : "▶  Simulate"
                    implicitWidth: 120
                    highlighted: true
                    palette.highlight: App.simulating ? "#d9534f" : "#3fa35a"
                    palette.button: App.simulating ? "#d9534f" : "#3fa35a"
                    onClicked: App.toggleSimulation()
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: "Sew and drape the active garment (Space)"
                }
                Button {
                    text: "Reset"
                    onClicked: App.resetArrangement()
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: "Return the pieces to their flat arrangement"
                }
            }
        }

        // ------------------------------------------------------------------ workspace
        SplitView {
            Layout.fillWidth: true
            Layout.fillHeight: true
            orientation: Qt.Horizontal

            LayersPanel {
                SplitView.preferredWidth: 280
                SplitView.minimumWidth: 200
            }

            SplitView {
                SplitView.fillWidth: true
                orientation: Qt.Horizontal
                Viewport3D {
                    id: viewport
                    SplitView.preferredWidth: 520
                    SplitView.minimumWidth: 200
                }
                PatternView {
                    id: patternView
                    SplitView.fillWidth: true
                    SplitView.minimumWidth: 200
                }
            }

            PropertiesPanel {
                id: propertiesPanel
                SplitView.preferredWidth: 330
                SplitView.minimumWidth: 260
                onPickColor: (current) => { colorDialog.selectedColor = current; colorDialog.open() }
                onPickTexture: textureDialog.open()
            }
        }

        // ------------------------------------------------------------------ status bar
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: 24
            color: palette.dark
            RowLayout {
                anchors.fill: parent
                anchors.leftMargin: 8
                anchors.rightMargin: 8
                spacing: 16
                Label { text: App.statusText; Layout.fillWidth: true; elide: Text.ElideRight; font.pixelSize: 12 }
                Label { text: App.simInfo; visible: text !== ""; font.pixelSize: 12; color: "#7ed491" }
                Label {
                    font.pixelSize: 12
                    opacity: 0.7
                    text: propertiesPanel.sel.totalFaces !== undefined ? propertiesPanel.sel.totalFaces.toLocaleString(Qt.locale(), "f", 0) + " faces total" : ""
                }
            }
        }
    }

    // ------------------------------------------------------------------ dialogs
    FileDialog {
        id: openDialog
        title: "Open Garment"
        nameFilters: ["Seamstress garments (*.garment.json *.json)", "All files (*)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        onAccepted: App.open(selectedFile)
    }
    FileDialog {
        id: saveDialog
        title: "Save Garment"
        fileMode: FileDialog.SaveFile
        nameFilters: ["Seamstress garments (*.garment.json)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        property var then: null
        onAccepted: { if (App.saveAs(selectedFile) && then) then() }
    }
    FileDialog {
        id: exportDialog
        title: "Export"
        fileMode: FileDialog.SaveFile
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        onAccepted: App.exportTo(window.pendingExport, selectedFile, weldBox.checked, scopeBox.currentIndex === 1)
    }
    FileDialog {
        id: textureDialog
        title: "Choose Texture"
        nameFilters: ["Images (*.png *.jpg *.jpeg *.webp *.bmp)", "All files (*)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.PicturesLocation)
        onAccepted: App.setMaterialTexture(selectedFile)
    }
    FileDialog {
        id: avatarDialog
        title: "Load Avatar (OBJ, in metres)"
        nameFilters: ["Wavefront OBJ (*.obj)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        onAccepted: App.setAvatar("CustomObj", selectedFile, 1)
    }
    ColorDialog {
        id: colorDialog
        title: "Fabric Colour"
        onSelectedColorChanged: if (visible) App.setMaterialColor(selectedColor.toString())
        onAccepted: App.setMaterialColor(selectedColor.toString())
    }

    Dialog {
        id: exportOptions
        title: window.pendingExport === "obj" ? "Export OBJ" : window.pendingExport === "fbx" ? "Export FBX" : "Export glTF"
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok | Dialog.Cancel
        ColumnLayout {
            spacing: 10
            RowLayout {
                Label { text: "Export" }
                ComboBox { id: scopeBox; implicitWidth: 240; model: ["All visible garments", "Active garment only"] }
            }
            CheckBox {
                id: weldBox
                checked: true
                text: "Weld sewn seams into one connected mesh"
            }
            Label {
                Layout.preferredWidth: 380
                wrapMode: Text.WordWrap
                opacity: 0.65
                font.pixelSize: 11
                text: "Exports the garment as it currently looks in the 3D view, in metres with Y up. Set the face count with Particle distance in the Topology panel."
            }
        }
        onAccepted: {
            const ext = window.pendingExport === "obj" ? ".obj" : window.pendingExport === "fbx" ? ".fbx" : ".glb"
            exportDialog.nameFilters = window.pendingExport === "obj" ? ["Wavefront OBJ (*.obj)"]
                                     : window.pendingExport === "fbx" ? ["Autodesk FBX (*.fbx)"]
                                     : ["glTF binary (*.glb)", "glTF separate (*.gltf)"]
            exportDialog.currentFile = exportDialog.currentFolder + "/" + App.suggestedFileName(ext)
            exportDialog.open()
        }
    }

    Dialog {
        id: makeHumanDialog
        title: "Send to MakeHuman"
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok | Dialog.Cancel
        ColumnLayout {
            spacing: 10
            Label {
                Layout.preferredWidth: 440
                wrapMode: Text.WordWrap
                text: App.makeHumanLinked
                    ? "The active garment will be written to MakeHuman's clothes library:\n" + App.settingMakeHumanUserFolder + "/data/clothes\n\nIt is fitted to the MakeHuman base mesh, so it follows the character when you change body shape. In MakeHuman it appears under Geometries ▸ Clothes."
                    : "MakeHuman is not linked yet. Choose its user data folder and base mesh in Preferences first."
            }
            Label {
                visible: App.makeHumanLinked && App.avatarType !== "MakeHuman"
                Layout.preferredWidth: 440
                wrapMode: Text.WordWrap
                color: "#ffb469"
                text: "The avatar is not the MakeHuman base mesh. Switch to Avatar ▸ MakeHuman Base Mesh and simulate the garment on it before sending."
            }
            Button { visible: !App.makeHumanLinked; text: "Open Preferences…"; onClicked: { makeHumanDialog.close(); preferences.open() } }
        }
        onAccepted: if (App.makeHumanLinked) App.sendToMakeHuman()
    }

    MessageDialog {
        id: errorDialog
        title: "Seamstress"
        buttons: MessageDialog.Ok
    }
    MessageDialog {
        id: unsavedDialog
        title: "Unsaved Changes"
        text: "This garment has unsaved changes."
        informativeText: "Do you want to save them first?"
        buttons: MessageDialog.Save | MessageDialog.Discard | MessageDialog.Cancel
        onButtonClicked: (button, role) => {
            const action = window.afterDiscard
            window.afterDiscard = null
            if (button === MessageDialog.Save)
                window.save(action)
            else if (button === MessageDialog.Discard && action)
                action()
        }
    }
    MessageDialog {
        id: aboutDialog
        title: "About Seamstress"
        text: "Seamstress " + App.version
        informativeText: "A 3D garment creation tool: draw pattern pieces, sew them, and drape them on an avatar.\n\nBuilt with C# and Qt Quick (Qt Bridge for C#).\n3D viewport: " + App.renderer
        buttons: MessageDialog.Ok
    }

    PreferencesDialog { id: preferences }
    BodyShapeDialog { id: bodyShape }
}
