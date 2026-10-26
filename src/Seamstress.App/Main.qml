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

    // The colour scheme shared by every control, from the theme chosen in Preferences ▸ Themes.
    palette {
        window: App.themeWindow; windowText: App.themeWindowText
        base: App.themeBase; alternateBase: App.themeAlternateBase; text: App.themeText
        button: App.themeButton; buttonText: App.themeButtonText
        highlight: App.themeHighlight; highlightedText: App.themeHighlightedText
        mid: App.themeMid; dark: App.themeDark; light: App.themeLight; midlight: App.themeMidlight; shadow: App.themeShadow
        placeholderText: App.themePlaceholderText; toolTipBase: App.themeToolTipBase; toolTipText: App.themeToolTipText
        brightText: App.themeBrightText; link: App.themeLink
    }

    // Chinese and Japanese draw some of the same characters (and punctuation) differently, so use the first regional
    // CJK font that is installed; with none of them, or in other languages, the usual font and its fallbacks apply.
    readonly property var cjkFonts: ({
        zh: ["Noto Sans CJK SC", "Source Han Sans SC", "Microsoft YaHei UI", "PingFang SC"],
        ja: ["Noto Sans CJK JP", "Source Han Sans JP", "Yu Gothic UI", "Meiryo UI", "Hiragino Sans"],
    })
    readonly property string cjkFont: {
        const installed = Qt.fontFamilies()
        return (cjkFonts[App.settingLanguage] || []).find(f => installed.indexOf(f) >= 0) || ""
    }
    font.family: cjkFont !== "" ? cjkFont : Qt.application.font.family

    property bool viewportConfirmed: false
    property var afterDiscard: null      // what to do once unsaved changes were dealt with
    property bool closing: false
    property string pendingExport: ""
    // Where Help sends readers for the manual; the guide lives in the repository's docs folder.
    readonly property string userGuideUrl: "https://github.com/MangoDragonHub/Seamstress/blob/main/docs/USER_GUIDE.md"
    // A text box has the keyboard: Enter and Escape belong to it rather than to the tool shortcuts.
    readonly property bool typing: activeFocusItem !== null && activeFocusItem.cursorPosition !== undefined
    // Interface texts in the language chosen in Preferences; reading the revision redoes the binding when it changes.
    function tr(text) { App.languageRevision; return App.tr(text) }

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
            const target = dialog ? dialog.contentItem.parent : view.indexOf("contextmenu") >= 0 ? contextMenu.contentItem.parent : rootLayout
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
            // "--view prefskeyboard" (or prefsviewport, …) opens Preferences on that page.
            if (view.indexOf("prefs") >= 0) preferences.openPage(preferences.pages.find(p => view.indexOf("prefs" + p.toLowerCase()) >= 0) || "General")
            if (view.indexOf("contextmenu") >= 0) contextMenu.popup(patternView, patternView.width * 0.45, patternView.height * 0.35)
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
    // The keys come from App.keyBindingsJson() and can be changed in Preferences ▸ Keyboard; this table says what
    // each command does.
    readonly property var commands: ({
        "file.new": () => newAction.trigger(),
        "file.open": () => openAction.trigger(),
        "file.save": () => saveAction.trigger(),
        "file.saveAs": () => saveAsAction.trigger(),
        "file.preferences": () => preferences.open(),
        "file.quit": () => window.close(),
        "edit.undo": () => App.undo(),
        "edit.redo": () => App.redo(),
        "edit.selectAll": () => App.selectAll(),
        "edit.duplicate": () => App.duplicate(),
        "edit.delete": () => App.deleteSelection(),
        "edit.mirror": () => App.mirror(),
        "edit.rotateLeft": () => App.rotate(90),
        "edit.rotateRight": () => App.rotate(-90),
        "edit.straighten": () => App.straightenEdges(),
        "edit.group": () => App.groupSelection(),
        "edit.reverseSeam": () => App.reverseSeam(),
        "tool.select": () => App.tool = "Select",
        "tool.editPoints": () => App.tool = "EditPoints",
        "tool.transformPoints": () => App.tool = "TransformPoints",
        "tool.sew": () => App.tool = "Sew",
        "tool.polygon": () => App.tool = "Polygon",
        "tool.rectangle": () => App.tool = "Rectangle",
        "tool.ellipse": () => App.tool = "Ellipse",
        "tool.ruler": () => App.tool = "Ruler",
        "tool.confirm": () => App.confirmTool(),
        "tool.cancel": () => { viewport.clearRuler(); if (App.pinBrush) App.pinBrush = false; else App.cancelTool() },
        "handles.full": () => App.gizmoMode = "Full",
        "handles.move": () => App.gizmoMode = "Move",
        "handles.rotate": () => App.gizmoMode = "Rotate",
        "handles.scale": () => App.gizmoMode = "Scale",
        "view.uv": () => App.uvMode = !App.uvMode,
        "view.frame": () => patternView.frameAll(),
        "view.avatar2d": () => App.showBodyOutline = !App.showBodyOutline,
        "view.ortho": () => viewport.orthographic = !viewport.orthographic,
        "view.front": () => viewport.setView(0, -5),
        "view.side": () => viewport.setView(90, -5),
        "view.back": () => viewport.setView(180, -5),
        "view.checker": () => viewport.displayMode = viewport.displayMode === "checker" ? "shaded" : "checker",
        "view.wireframe": () => viewport.wireframe = !viewport.wireframe,
        "view.normals": () => viewport.showNormals = !viewport.showNormals,
        "edit.invertNormals": () => App.invertNormals(),
        "edit.cutHole": () => App.cutHole(),
        "view.pinch": () => viewport.pinchMode = !viewport.pinchMode,
        "view.pinBrush": () => App.pinBrush = !App.pinBrush,
        "view.handles": () => viewport.showGizmo = !viewport.showGizmo,
        "view.avatar": () => viewport.showAvatar = !viewport.showAvatar,
        "sim.toggle": () => App.toggleSimulation(),
        "sim.reset": () => App.resetArrangement(),
    })
    function run(id) { if (commands[id]) commands[id]() }
    readonly property var bindings: { App.keysRevision; return JSON.parse(App.keyBindingsJson()) }
    // The first key of a command, for tooltips: " (V)", or nothing when it has no key.
    function keyHint(id) { App.keysRevision; const k = App.keyText(id); return k === "" ? "" : " (" + k + ")" }
    // Set while Preferences listens for a new key, so pressing it does not also run its command.
    readonly property bool recordingKey: preferences.recording

    Item {
        Repeater {
            model: window.bindings
            delegate: Item {
                id: holder
                required property var modelData
                Shortcut {
                    // While typing, keys without Ctrl, Alt or Meta belong to the text box.
                    sequences: window.typing ? holder.modelData.keys.filter(k => /(^|\+)(Ctrl|Alt|Meta)\+/.test(k)) : holder.modelData.keys
                    enabled: !window.recordingKey && sequences.length > 0
                    onActivated: window.run(holder.modelData.id)
                }
            }
        }
    }

    Action { id: newAction; text: tr("&New"); onTriggered: window.guard(() => App.newDocument()) }
    Action { id: openAction; text: tr("&Open…"); onTriggered: window.guard(() => openDialog.open()) }
    Action { id: saveAction; text: tr("&Save"); onTriggered: window.save(null) }
    Action {
        id: saveAsAction
        text: tr("Save &As…")
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
                title: tr("&File")
                KeyMenuItem { action: newAction; command: "file.new" }
                KeyMenuItem { action: openAction; command: "file.open" }
                Menu {
                    id: recentMenu
                    title: tr("Open &Recent")
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
                KeyMenuItem { action: saveAction; command: "file.save" }
                KeyMenuItem { action: saveAsAction; command: "file.saveAs" }
                MenuSeparator {}
                Menu {
                    title: tr("&Export")
                    MenuItem { text: tr("Wavefront &OBJ…"); onTriggered: window.startExport("obj") }
                    MenuItem { text: tr("&glTF 2.0 (.glb / .gltf)…"); onTriggered: window.startExport("gltf") }
                    MenuItem { text: tr("&FBX…"); onTriggered: window.startExport("fbx") }
                    MenuSeparator {}
                    MenuItem { text: tr("Send to &MakeHuman"); onTriggered: makeHumanDialog.open() }
                }
                MenuSeparator {}
                KeyMenuItem { text: tr("&Preferences…"); command: "file.preferences"; onTriggered: preferences.open() }
                MenuSeparator {}
                KeyMenuItem { text: tr("&Quit"); command: "file.quit"; onTriggered: window.close() }
            }
            EditMenu {}
            Menu {
                id: garmentMenu
                title: tr("&Garment")
                MenuItem { text: tr("New Empty Garment"); onTriggered: App.addGarment() }
                MenuItem { text: tr("New Group"); onTriggered: App.addGroup() }
                MenuSeparator {}
                Instantiator {
                    model: App.templateNames().split("|")
                    delegate: MenuItem {
                        required property string modelData
                        text: tr("Add %1").arg(tr(modelData))
                        onTriggered: App.addTemplate(modelData)
                    }
                    onObjectAdded: (index, object) => garmentMenu.insertItem(index + 3, object)
                    onObjectRemoved: (index, object) => garmentMenu.removeItem(object)
                }
            }
            Menu {
                title: tr("&Simulation")
                KeyMenuItem { text: App.simulating ? tr("Stop Simulation") : tr("Simulate"); command: "sim.toggle"; onTriggered: App.toggleSimulation() }
                MenuItem {
                    text: tr("Simulate All Garments")
                    checkable: true
                    checked: App.simulateAll
                    onTriggered: App.simulateAll = checked
                }
                MenuSeparator {}
                KeyMenuItem { text: tr("Reset Arrangement"); command: "sim.reset"; onTriggered: App.resetArrangement() }
                MenuSeparator {}
                MenuItem { text: tr("Unpin All on Selected Garments"); onTriggered: App.unpinAll() }
            }
            Menu {
                title: tr("&Avatar")
                MenuItem { text: tr("Mannequin"); checkable: true; checked: App.avatarType === "Mannequin"; onTriggered: App.setAvatar("Mannequin", "", 1) }
                MenuItem { text: tr("MakeHuman Base Mesh"); checkable: true; checked: App.avatarType === "MakeHuman"; onTriggered: App.setAvatar("MakeHuman", "", 1) }
                MenuItem { text: tr("MakeHuman Body Shape…"); enabled: App.avatarType === "MakeHuman"; onTriggered: bodyShape.open() }
                MenuItem { text: tr("Load OBJ…"); checkable: true; checked: App.avatarType === "CustomObj"; onTriggered: avatarDialog.open() }
                MenuItem { text: tr("None"); checkable: true; checked: App.avatarType === "None"; onTriggered: App.setAvatar("None", "", 1) }
            }
            Menu {
                title: tr("&View")
                MenuItem { text: tr("2D: Pattern"); checkable: true; checked: !App.uvMode; onTriggered: App.uvMode = false }
                KeyMenuItem { text: tr("2D: UV Layout"); command: "view.uv"; checkable: true; checked: App.uvMode; onTriggered: App.uvMode = true }
                KeyMenuItem { text: tr("2D: Show Avatar"); command: "view.avatar2d"; checkable: true; checked: App.showBodyOutline; onTriggered: App.showBodyOutline = checked }
                KeyMenuItem { text: tr("Frame All in 2D"); command: "view.frame"; onTriggered: patternView.frameAll() }
                MenuSeparator {}
                KeyMenuItem { text: tr("3D: Orthographic"); command: "view.ortho"; checkable: true; checked: viewport.orthographic; onTriggered: viewport.orthographic = checked }
                KeyMenuItem { text: tr("3D: Front"); command: "view.front"; onTriggered: viewport.setView(0, -5) }
                KeyMenuItem { text: tr("3D: Side"); command: "view.side"; onTriggered: viewport.setView(90, -5) }
                KeyMenuItem { text: tr("3D: Back"); command: "view.back"; onTriggered: viewport.setView(180, -5) }
                MenuSeparator {}
                KeyMenuItem { text: tr("3D: Checker (Texel Density)"); command: "view.checker"; checkable: true; checked: viewport.displayMode === "checker"; onTriggered: viewport.displayMode = checked ? "checker" : "shaded" }
                KeyMenuItem { text: tr("3D: Wireframe (Topology)"); command: "view.wireframe"; checkable: true; checked: viewport.wireframe; onTriggered: viewport.wireframe = checked }
                KeyMenuItem { text: tr("3D: Normal Direction"); command: "view.normals"; checkable: true; checked: viewport.showNormals; onTriggered: viewport.showNormals = checked }
                KeyMenuItem { text: tr("3D: Pinch Cloth"); command: "view.pinch"; checkable: true; checked: viewport.pinchMode; onTriggered: viewport.pinchMode = checked }
                KeyMenuItem { text: tr("3D: Pin Vertices"); command: "view.pinBrush"; checkable: true; checked: App.pinBrush; onTriggered: App.pinBrush = checked }
                KeyMenuItem { text: tr("3D: Show Handles"); command: "view.handles"; checkable: true; checked: viewport.showGizmo; onTriggered: viewport.showGizmo = checked }
                Menu {
                    title: tr("Handles")
                    KeyMenuItem { text: tr("Move, Rotate and Scale"); command: "handles.full"; checkable: true; checked: App.gizmoMode === "Full"; onTriggered: App.gizmoMode = "Full" }
                    KeyMenuItem { text: tr("Move"); command: "handles.move"; checkable: true; checked: App.gizmoMode === "Move"; onTriggered: App.gizmoMode = "Move" }
                    KeyMenuItem { text: tr("Rotate"); command: "handles.rotate"; checkable: true; checked: App.gizmoMode === "Rotate"; onTriggered: App.gizmoMode = "Rotate" }
                    KeyMenuItem { text: tr("Scale"); command: "handles.scale"; checkable: true; checked: App.gizmoMode === "Scale"; onTriggered: App.gizmoMode = "Scale" }
                }
                KeyMenuItem { text: tr("3D: Show Avatar"); command: "view.avatar"; checkable: true; checked: viewport.showAvatar; onTriggered: viewport.showAvatar = checked }
                MenuItem { text: tr("3D: Show Sewing Threads"); checkable: true; checked: viewport.showThreads; onTriggered: viewport.showThreads = checked }
            }
            Menu {
                title: tr("&Help")
                MenuItem { text: tr("Documentation"); onTriggered: Qt.openUrlExternally(window.userGuideUrl) }
                MenuSeparator {}
                MenuItem { text: tr("About Seamstress"); onTriggered: aboutDialog.open() }
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
                    property string command: ""
                    checkable: true
                    checked: App.tool === tool && !App.uvMode
                    enabled: !App.uvMode || tool === "Select"
                    onClicked: App.tool = tool
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tip + window.keyHint(command)
                }
                // A short, faint line between the buttons of one group, and a taller, brighter one with more room
                // around it between groups.
                component ButtonDivider: Rectangle {
                    implicitWidth: 1
                    implicitHeight: 16
                    Layout.leftMargin: 2
                    Layout.rightMargin: 2
                    color: palette.mid
                }
                component GroupDivider: Rectangle {
                    implicitWidth: 1
                    implicitHeight: 26
                    Layout.leftMargin: 10
                    Layout.rightMargin: 10
                    color: palette.light
                }

                ToolChoice { text: tr("Select"); tool: "Select"; command: "tool.select"; tip: tr("Select and move pieces") }
                ButtonDivider {}
                ToolChoice { text: tr("Edit Points"); tool: "EditPoints"; command: "tool.editPoints"; tip: tr("Move points, curve edges, add points") }
                ButtonDivider {}
                ToolChoice { text: tr("Transform Points"); tool: "TransformPoints"; command: "tool.transformPoints"; tip: tr("Select points and move, rotate or scale them with handles") }
                ButtonDivider {}
                ToolChoice { text: tr("Sew"); tool: "Sew"; command: "tool.sew"; tip: tr("Click two edges to sew them together") }
                GroupDivider {}
                ToolChoice { text: tr("Polygon"); tool: "Polygon"; command: "tool.polygon"; tip: tr("Draw a piece corner by corner") }
                ButtonDivider {}
                ToolChoice { text: tr("Rectangle"); tool: "Rectangle"; command: "tool.rectangle"; tip: tr("Drag out a rectangular piece") }
                ButtonDivider {}
                ToolChoice { text: tr("Ellipse"); tool: "Ellipse"; command: "tool.ellipse"; tip: tr("Drag out an elliptical piece") }
                GroupDivider {}
                ToolChoice { text: tr("Ruler"); tool: "Ruler"; command: "tool.ruler"; tip: tr("Measure distances in the 2D view, or between two points on the cloth or avatar in the 3D view") }
                GroupDivider {}
                ToolButton { text: "↶"; font.pixelSize: 18; enabled: App.canUndo; onClicked: App.undo(); ToolTip.visible: hovered; ToolTip.text: App.undoText }
                ToolButton { text: "↷"; font.pixelSize: 18; enabled: App.canRedo; onClicked: App.redo(); ToolTip.visible: hovered; ToolTip.text: App.redoText }
                GroupDivider {}
                ToolButton {
                    text: App.uvMode ? tr("UV Layout") : tr("Pattern")
                    checkable: true
                    checked: App.uvMode
                    onClicked: App.uvMode = checked
                    ToolTip.visible: hovered
                    ToolTip.text: tr("Switch the 2D view between the pattern and its UV layout") + window.keyHint("view.uv")
                }

                Item { Layout.fillWidth: true }

                CheckBox {
                    text: tr("All garments")
                    checked: App.simulateAll
                    onToggled: App.simulateAll = checked
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Off: only the active garment (and garments linked with ⇄) is simulated.\nOn: every visible garment is simulated.")
                }
                Button {
                    id: simulateButton
                    text: App.simulating ? "■  " + tr("Stop") : "▶  " + tr("Simulate")
                    implicitWidth: 120
                    highlighted: true
                    palette.highlight: App.simulating ? "#d9534f" : "#3fa35a"
                    palette.button: App.simulating ? "#d9534f" : "#3fa35a"
                    onClicked: App.toggleSimulation()
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Sew and drape the active garment") + window.keyHint("sim.toggle")
                }
                Button {
                    text: tr("Reset")
                    onClicked: App.resetArrangement()
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Return the pieces to their flat arrangement")
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
                    onContextMenuRequested: contextMenu.popup()
                    onPinMenuRequested: pinMenu.popup()
                    SplitView.preferredWidth: 520
                    SplitView.minimumWidth: 200
                }
                PatternView {
                    id: patternView
                    onContextMenuRequested: contextMenu.popup()
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
                Label { text: App.simInfo; visible: text !== ""; font.pixelSize: 12; color: App.themeInfoText }
                Label {
                    font.pixelSize: 12
                    opacity: 0.7
                    text: propertiesPanel.sel.totalFaces !== undefined ? tr("%1 faces total").arg(propertiesPanel.sel.totalFaces.toLocaleString(Qt.locale(), "f", 0)) : ""
                }
            }
        }
    }

    // ------------------------------------------------------------------ dialogs
    FileDialog {
        id: openDialog
        title: tr("Open Garment")
        nameFilters: [tr("Seamstress garments") + " (*.garment.json *.json)", tr("All files") + " (*)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        onAccepted: App.open(selectedFile)
    }
    FileDialog {
        id: saveDialog
        title: tr("Save Garment")
        fileMode: FileDialog.SaveFile
        nameFilters: [tr("Seamstress garments") + " (*.garment.json)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        property var then: null
        onAccepted: { if (App.saveAs(selectedFile) && then) then() }
    }
    FileDialog {
        id: exportDialog
        title: tr("Export")
        fileMode: FileDialog.SaveFile
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        onAccepted: App.exportTo(window.pendingExport, selectedFile, weldBox.checked, scopeBox.currentIndex === 1)
    }
    FileDialog {
        id: textureDialog
        title: tr("Choose Texture")
        nameFilters: [tr("Images") + " (*.png *.jpg *.jpeg *.webp *.bmp)", tr("All files") + " (*)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.PicturesLocation)
        onAccepted: App.setMaterialTexture(selectedFile)
    }
    FileDialog {
        id: avatarDialog
        title: tr("Load Avatar (OBJ, in metres)")
        nameFilters: ["Wavefront OBJ (*.obj)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        onAccepted: App.setAvatar("CustomObj", selectedFile, 1)
    }
    ColorDialog {
        id: colorDialog
        title: tr("Fabric Colour")
        onSelectedColorChanged: if (visible) App.setMaterialColor(selectedColor.toString())
        onAccepted: App.setMaterialColor(selectedColor.toString())
    }

    AppDialog {
        id: exportOptions
        title: tr("Export %1").arg(window.pendingExport === "obj" ? "OBJ" : window.pendingExport === "fbx" ? "FBX" : "glTF")
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok | Dialog.Cancel
        ColumnLayout {
            spacing: 10
            RowLayout {
                Label { text: tr("Export") }
                ComboBox { id: scopeBox; implicitWidth: 240; model: [tr("All visible garments"), tr("Active garment only")] }
            }
            CheckBox {
                id: weldBox
                checked: true
                text: tr("Weld sewn seams into one connected mesh")
            }
            Label {
                Layout.preferredWidth: 380
                wrapMode: Text.WordWrap
                opacity: 0.65
                font.pixelSize: 11
                text: tr("Exports the garment as it currently looks in the 3D view, in metres with Y up. Set the face count with Particle distance in the Topology panel.")
            }
        }
        onAccepted: {
            const ext = window.pendingExport === "obj" ? ".obj" : window.pendingExport === "fbx" ? ".fbx" : ".glb"
            exportDialog.nameFilters = window.pendingExport === "obj" ? ["Wavefront OBJ (*.obj)"]
                                     : window.pendingExport === "fbx" ? ["Autodesk FBX (*.fbx)"]
                                     : [tr("glTF binary") + " (*.glb)", tr("glTF separate") + " (*.gltf)"]
            exportDialog.currentFile = exportDialog.currentFolder + "/" + App.suggestedFileName(ext)
            exportDialog.open()
        }
    }

    AppDialog {
        id: makeHumanDialog
        title: tr("Send to MakeHuman")
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok | Dialog.Cancel
        // Where the asset goes and whether that replaces one already there, for the name being typed.
        property var target: ({ folder: "", exists: false })
        function check() { target = JSON.parse(App.makeHumanAssetInfo(assetName.text)) }
        onAboutToShow: {
            assetName.text = App.activeGarmentName()
            check()
            if (App.makeHumanLinked)
                assetName.forceActiveFocus()
        }
        okText: tr("Send")
        ColumnLayout {
            spacing: 10
            Label {
                visible: !App.makeHumanLinked
                Layout.preferredWidth: 460
                wrapMode: Text.WordWrap
                text: tr("MakeHuman is not linked yet. Choose its user data folder and base mesh in Preferences first.")
            }
            GridLayout {
                visible: App.makeHumanLinked
                columns: 2
                columnSpacing: 10
                rowSpacing: 8
                Label { text: tr("Asset name") }
                TextField {
                    id: assetName
                    Layout.preferredWidth: 340
                    selectByMouse: true
                    placeholderText: tr("the name MakeHuman lists it under")
                    onTextChanged: makeHumanDialog.check()
                    onAccepted: makeHumanDialog.accept()
                }
                Label { text: tr("Saved in"); opacity: 0.75 }
                Label {
                    Layout.preferredWidth: 340
                    text: makeHumanDialog.target.folder
                    elide: Text.ElideMiddle
                    opacity: 0.75
                    ToolTip.visible: savedIn.hovered
                    ToolTip.text: text
                    HoverHandler { id: savedIn }
                }
                Label { text: tr("Author"); opacity: 0.75 }
                Label { text: App.settingAuthor !== "" ? App.settingAuthor : App.defaultAuthor; opacity: 0.75 }
                Label { text: tr("License"); opacity: 0.75 }
                Label { text: App.settingMakeHumanLicense; opacity: 0.75 }
            }
            Label {
                visible: App.makeHumanLinked && makeHumanDialog.target.exists
                Layout.preferredWidth: 460
                wrapMode: Text.WordWrap
                color: App.themeWarningText
                text: tr("An asset with this name is already there. Sending replaces it; choose another name to keep both.")
            }
            Label {
                visible: App.makeHumanLinked
                Layout.preferredWidth: 460
                wrapMode: Text.WordWrap
                opacity: 0.65
                font.pixelSize: 11
                text: tr("The garment is fitted to the MakeHuman base mesh, so it follows the character when you change body shape. In MakeHuman it appears under Geometries ▸ Clothes. Author and license are set in Preferences ▸ MakeHuman.")
            }
            Label {
                visible: App.makeHumanLinked && App.avatarType !== "MakeHuman"
                Layout.preferredWidth: 460
                wrapMode: Text.WordWrap
                color: App.themeWarningText
                text: tr("The avatar is not the MakeHuman base mesh. Switch to Avatar ▸ MakeHuman Base Mesh and simulate the garment on it before sending.")
            }
            RowLayout {
                Button { visible: !App.makeHumanLinked; text: tr("Open Preferences…"); onClicked: { makeHumanDialog.close(); preferences.openPage("MakeHuman") } }
                Button { visible: App.makeHumanLinked; text: tr("Change Author or License…"); flat: true; onClicked: { makeHumanDialog.close(); preferences.openPage("MakeHuman") } }
            }
        }
        onAccepted: if (App.makeHumanLinked) App.sendToMakeHuman(assetName.text)
    }

    AppDialog {
        id: errorDialog
        property alias text: errorText.text
        title: "Seamstress"
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok
        ColumnLayout {
            Label { id: errorText; wrapMode: Text.WordWrap; Layout.maximumWidth: 520 }
        }
    }
    AppDialog {
        id: unsavedDialog
        title: tr("Unsaved Changes")
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Save | Dialog.Discard | Dialog.Cancel
        ColumnLayout {
            spacing: 6
            Label { text: tr("This garment has unsaved changes."); font.bold: true }
            Label { text: tr("Do you want to save them first?") }
        }
        function finish(saving) {
            const action = window.afterDiscard
            window.afterDiscard = null
            if (saving)
                window.save(action)
            else if (action)
                action()
        }
        onAccepted: finish(true)
        onDiscarded: { close(); finish(false) }
        onRejected: window.afterDiscard = null
    }
    AppDialog {
        id: aboutDialog
        title: tr("About Seamstress")
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok
        ColumnLayout {
            spacing: 8
            Label { text: "Seamstress " + App.version; font.bold: true; font.pixelSize: 15 }
            Label {
                Layout.maximumWidth: 460
                wrapMode: Text.WordWrap
                text: tr("A 3D garment creation tool: draw pattern pieces, sew them, and drape them on an avatar.") + "\n\n"
                      + tr("Built with C# and Qt Quick (Qt Bridge for C#).") + "\n" + tr("3D viewport: %1").arg(App.renderer)
            }
            Label {
                Layout.maximumWidth: 460
                wrapMode: Text.WordWrap
                font.pixelSize: 11
                opacity: 0.7
                text: tr("Source code: MIT licence. This build includes Qt Quick 3D under the GPL-3.0, so the package as a whole is GPL-3.0; the licence texts and notices are in LICENSES/ and NOTICE.")
            }
        }
    }

    PreferencesDialog { id: preferences }
    EditMenu { id: contextMenu }
    PinMenu { id: pinMenu }
    BodyShapeDialog { id: bodyShape }
}
