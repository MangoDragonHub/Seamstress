import QtQuick
import QtQuick.Controls
import QtQuick.Dialogs
import QtQuick.Layouts

// Preferences: where MakeHuman lives, how the 3D viewport renders, the units shown, and the texture size used for
// density readouts.
Dialog {
    id: root
    title: "Preferences"
    modal: true
    anchors.centerIn: parent
    width: 640
    standardButtons: Dialog.Save | Dialog.Cancel

    function load() {
        userFolder.text = App.settingMakeHumanUserFolder
        baseMesh.text = App.settingMakeHumanBaseMesh
        command.text = App.settingMakeHumanCommand
        author.text = App.settingAuthor
        renderer.currentIndex = Math.max(0, ["auto", "gpu", "cpu"].indexOf(App.settingRenderer))
        resolution.currentIndex = Math.max(0, resolution.model.indexOf(App.settingTextureResolution))
        units.currentIndex = App.settingUnits === "imperial" ? 1 : 0
        shownImperial = units.currentIndex === 1
        gridSize.text = shownLength(App.gridSizeCm)
        snapStep.text = shownLength(App.snapStepCm)
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
    onAccepted: App.savePreferences(userFolder.text, baseMesh.text, command.text,
                                    ["auto", "gpu", "cpu"][renderer.currentIndex], resolution.model[resolution.currentIndex], author.text,
                                    units.currentIndex === 1 ? "imperial" : "metric", typedCm(gridSize), typedCm(snapStep))

    ColumnLayout {
        anchors.fill: parent
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
                TextField { id: author; Layout.fillWidth: true; selectByMouse: true; placeholderText: "written into exported assets" }
                Item {}

                Item {}
                RowLayout {
                    Layout.columnSpan: 2
                    Button {
                        text: "Detect Automatically"
                        onClicked: {
                            status.text = App.detectMakeHuman() ? "Found a MakeHuman installation." : "No MakeHuman installation found in the usual places."
                            const keepCommand = command.text, keepAuthor = author.text
                            root.load()
                            command.text = keepCommand
                            author.text = keepAuthor
                        }
                    }
                    Label { id: status; opacity: 0.75; Layout.fillWidth: true; elide: Text.ElideRight }
                }

                Label {
                    Layout.columnSpan: 3
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.6
                    font.pixelSize: 11
                    text: "“Send to MakeHuman” writes garments into <user data folder>/data/clothes and fits them to the base mesh. "
                        + "If a launch command is set, MakeHuman is started after sending when it is not already running."
                }
            }
        }

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
                    Label { text: "One square of the ground grid."; opacity: 0.6; font.pixelSize: 11; Layout.leftMargin: 8 }
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
                    Label { text: "Hold Shift while dragging a move arrow to snap to this."; opacity: 0.6; font.pixelSize: 11; Layout.leftMargin: 8 }
                }
                Label {
                    Layout.columnSpan: 2
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.6
                    font.pixelSize: 11
                    text: "A renderer change takes effect the next time Seamstress starts."
                }
            }
        }

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
                Label {
                    Layout.columnSpan: 2
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.6
                    font.pixelSize: 11
                    text: "Changes the units shown and typed in the interface. Garment files and exports are not affected."
                }
            }
        }

        GroupBox {
            title: "Textures"
            Layout.fillWidth: true
            RowLayout {
                anchors.fill: parent
                Label { text: "Texture size for texel density" }
                ComboBox { id: resolution; model: [512, 1024, 2048, 4096, 8192]; implicitWidth: 120 }
                Label { text: "px"; opacity: 0.7 }
                Item { Layout.fillWidth: true }
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
