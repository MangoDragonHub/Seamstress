import QtQuick
import QtQuick.Controls
import QtQuick.Dialogs
import QtQuick.Layouts

// Preferences: where MakeHuman lives, how the 3D viewport renders, and the texture size used for density readouts.
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
    }

    onAboutToShow: load()
    onAccepted: App.savePreferences(userFolder.text, baseMesh.text, command.text,
                                    ["auto", "gpu", "cpu"][renderer.currentIndex], resolution.model[resolution.currentIndex], author.text)

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
        onAccepted: userFolder.text = decodeURIComponent(selectedFolder.toString().replace(/^file:\/\//, ""))
    }
    FileDialog {
        id: meshDialog
        title: "MakeHuman Base Mesh (base.obj)"
        nameFilters: ["Wavefront OBJ (*.obj)"]
        onAccepted: baseMesh.text = decodeURIComponent(selectedFile.toString().replace(/^file:\/\//, ""))
    }
}
