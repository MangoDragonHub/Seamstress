import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Properties of the current selection: topology, arrangement, fabric physics, material, UVs, seams
// and the simulation settings.
Pane {
    id: root
    padding: 0

    property var sel: ({ kind: "none", count: 0, presets: [], materials: [], simulation: {} })
    signal pickColor(string current)
    signal pickTexture()

    function refresh() { sel = JSON.parse(App.selection()) }
    Component.onCompleted: refresh()
    Connections {
        target: App
        function onSelectionRevisionChanged() { Qt.callLater(root.refresh) }
    }

    // "--view propsbottom" (used when capturing documentation screenshots) starts scrolled to the end.
    Timer {
        interval: 2000
        running: App.startupView.indexOf("propsbottom") >= 0
        onTriggered: scroll.ScrollBar.vertical.position = 1 - scroll.ScrollBar.vertical.size
    }

    component Section: ColumnLayout {
        id: section
        property string title: ""
        property bool open: true
        default property alias content: body.data
        Layout.fillWidth: true
        spacing: 0
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: 26
            color: Qt.alpha(palette.windowText, 0.07)
            Label {
                anchors { left: parent.left; leftMargin: 8; verticalCenter: parent.verticalCenter }
                text: (section.open ? "▾  " : "▸  ") + section.title
                font.bold: true
            }
            MouseArea { anchors.fill: parent; onClicked: section.open = !section.open }
        }
        ColumnLayout {
            id: body
            visible: section.open
            Layout.fillWidth: true
            Layout.margins: 8
            spacing: 5
        }
    }

    // A labelled number box. Commits on Enter or when it loses focus; arrow keys step the value.
    component NumberRow: RowLayout {
        id: numberRow
        property string label: ""
        property real value: 0
        property int decimals: 2
        property real step: 1
        property string suffix: ""
        signal edited(real value)
        Layout.fillWidth: true
        spacing: 6
        onValueChanged: if (!field.activeFocus) field.text = value.toFixed(decimals)
        Label { text: numberRow.label; Layout.preferredWidth: 118; elide: Text.ElideRight; opacity: 0.85 }
        TextField {
            id: field
            Layout.fillWidth: true
            implicitHeight: 24
            padding: 3
            selectByMouse: true
            horizontalAlignment: Text.AlignRight
            text: numberRow.value.toFixed(numberRow.decimals)
            validator: DoubleValidator { notation: DoubleValidator.StandardNotation; locale: "C" }
            function commit() {
                const v = Number(text)
                if (!isNaN(v) && Math.abs(v - numberRow.value) > 1e-9)
                    numberRow.edited(v)
                else
                    text = numberRow.value.toFixed(numberRow.decimals)
            }
            onEditingFinished: commit()
            Keys.onUpPressed: { numberRow.edited(Number(text) + numberRow.step) }
            Keys.onDownPressed: { numberRow.edited(Number(text) - numberRow.step) }
        }
        Label { visible: numberRow.suffix !== ""; text: numberRow.suffix; opacity: 0.6; Layout.preferredWidth: 26 }
    }

    component SliderRow: RowLayout {
        id: sliderRow
        property string label: ""
        property real value: 0
        property real from: 0
        property real to: 100
        property real stepSize: 1
        property int decimals: 0
        property string suffix: ""
        signal edited(real value)
        Layout.fillWidth: true
        spacing: 6
        onValueChanged: if (!slider.pressed) slider.value = value
        Label { text: sliderRow.label; Layout.preferredWidth: 118; elide: Text.ElideRight; opacity: 0.85 }
        Slider {
            id: slider
            Layout.fillWidth: true
            implicitHeight: 22
            from: sliderRow.from; to: sliderRow.to; stepSize: sliderRow.stepSize
            value: sliderRow.value
            onMoved: sliderRow.edited(value)
        }
        Label {
            text: slider.value.toFixed(sliderRow.decimals) + sliderRow.suffix
            Layout.preferredWidth: 54
            horizontalAlignment: Text.AlignRight
            opacity: 0.8
        }
    }

    component InfoRow: RowLayout {
        property string label: ""
        property string value: ""
        Layout.fillWidth: true
        Label { text: parent.label; Layout.preferredWidth: 118; opacity: 0.85 }
        Label { text: parent.value; Layout.fillWidth: true; elide: Text.ElideRight; horizontalAlignment: Text.AlignRight }
    }

    ScrollView {
        id: scroll
        anchors.fill: parent
        contentWidth: availableWidth
        clip: true

        ColumnLayout {
            width: scroll.availableWidth
            spacing: 0

            Label {
                Layout.fillWidth: true
                padding: 8
                font.bold: true
                elide: Text.ElideRight
                text: root.sel.kind === "seam" ? "Seam"
                    : root.sel.kind === "pieces" ? root.sel.piece.name
                    : root.sel.kind === "garment" ? root.sel.garment.name
                    : "Properties"
            }

            Label {
                visible: root.sel.kind === "none"
                Layout.fillWidth: true
                padding: 8
                wrapMode: Text.WordWrap
                opacity: 0.6
                text: "Nothing selected. Add a garment from the Garment menu or draw a piece in the 2D view."
            }

            // ---- Topology ------------------------------------------------------------
            Section {
                title: "Topology"
                visible: !!root.sel.garment
                SliderRow {
                    label: "Particle distance"
                    from: 0.5; to: 6; stepSize: 0.1; decimals: 1; suffix: " cm"
                    value: root.sel.garment ? root.sel.garment.particleDistance : 2
                    onEdited: (v) => App.setParticleDistance(v)
                }
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: "Faces"; Layout.preferredWidth: 118; opacity: 0.85 }
                    ComboBox {
                        Layout.fillWidth: true
                        implicitHeight: 26
                        model: ["Triangles", "Quads"]
                        currentIndex: root.sel.garment && root.sel.garment.topology === "Quads" ? 1 : 0
                        onActivated: App.setTopology(currentText)
                    }
                }
                InfoRow {
                    label: "Garment mesh"
                    value: root.sel.garment ? root.sel.garment.faces.toLocaleString(Qt.locale(), "f", 0) + " faces  ·  "
                         + root.sel.garment.vertices.toLocaleString(Qt.locale(), "f", 0) + " verts" : ""
                }
                InfoRow {
                    visible: root.sel.garment ? root.sel.garment.topology === "Quads" : false
                    label: "Of which"
                    value: root.sel.garment ? root.sel.garment.quads.toLocaleString(Qt.locale(), "f", 0) + " quads  ·  "
                         + (root.sel.garment.faces - root.sel.garment.quads).toLocaleString(Qt.locale(), "f", 0) + " triangles" : ""
                }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: "Smaller distance = more faces: finer wrinkles, slower simulation. Quads export to OBJ and FBX; glTF and MakeHuman always receive triangles."
                }
                NumberRow {
                    visible: root.sel.kind === "pieces"
                    label: "Piece override"
                    decimals: 1; step: 0.1; suffix: "cm"
                    value: root.sel.piece ? root.sel.piece.particleDistance : 0
                    onEdited: (v) => App.setPieceValue("particleDistance", v)
                }
                InfoRow {
                    visible: root.sel.kind === "pieces"
                    label: "Selected"
                    value: root.sel.piece ? root.sel.piece.faces.toLocaleString(Qt.locale(), "f", 0) + " faces  ·  "
                         + root.sel.piece.area.toFixed(0) + " cm²" : ""
                }
                Label {
                    visible: root.sel.kind === "pieces"
                    Layout.fillWidth: true
                    opacity: 0.55
                    font.pixelSize: 11
                    text: "Override 0 uses the garment's particle distance."
                }
            }

            // ---- Seam ----------------------------------------------------------------
            Section {
                title: "Seam"
                visible: root.sel.kind === "seam"
                InfoRow { label: "From"; value: root.sel.seam ? root.sel.seam.a : "" }
                InfoRow { label: "To"; value: root.sel.seam ? root.sel.seam.b : "" }
                InfoRow {
                    label: "Lengths"
                    value: root.sel.seam ? root.sel.seam.lengthA.toFixed(1) + " cm  ↔  " + root.sel.seam.lengthB.toFixed(1) + " cm" : ""
                }
                RowLayout {
                    Layout.fillWidth: true
                    Button {
                        Layout.fillWidth: true
                        text: "Reverse Direction"
                        onClicked: App.reverseSeam()
                        ToolTip.visible: hovered
                        ToolTip.text: "Swap which ends of the two edges are sewn together (fixes a twisted seam)"
                    }
                    Button { Layout.fillWidth: true; text: "Delete Seam"; onClicked: App.deleteSelection() }
                }
            }

            // ---- Arrangement ---------------------------------------------------------
            Section {
                title: "3D Arrangement"
                visible: root.sel.kind === "pieces"
                NumberRow { label: "Position X"; suffix: "m"; step: 0.01; value: root.sel.piece ? root.sel.piece.posX : 0; onEdited: (v) => App.setPieceValue("posX", v) }
                NumberRow { label: "Position Y"; suffix: "m"; step: 0.01; value: root.sel.piece ? root.sel.piece.posY : 0; onEdited: (v) => App.setPieceValue("posY", v) }
                NumberRow { label: "Position Z"; suffix: "m"; step: 0.01; value: root.sel.piece ? root.sel.piece.posZ : 0; onEdited: (v) => App.setPieceValue("posZ", v) }
                NumberRow { label: "Rotation X"; suffix: "°"; decimals: 1; step: 5; value: root.sel.piece ? root.sel.piece.rotX : 0; onEdited: (v) => App.setPieceValue("rotX", v) }
                NumberRow { label: "Rotation Y"; suffix: "°"; decimals: 1; step: 5; value: root.sel.piece ? root.sel.piece.rotY : 0; onEdited: (v) => App.setPieceValue("rotY", v) }
                NumberRow { label: "Rotation Z"; suffix: "°"; decimals: 1; step: 5; value: root.sel.piece ? root.sel.piece.rotZ : 0; onEdited: (v) => App.setPieceValue("rotZ", v) }
                SliderRow {
                    label: "Wrap"
                    from: -300; to: 300; stepSize: 5; suffix: "°"
                    value: root.sel.piece ? root.sel.piece.curvature : 0
                    onEdited: (v) => App.setPieceValue("curvature", v)
                }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: "Where the piece sits in 3D; you can also drag the arrows and rings in the 3D view. Wrap curls the flat piece around an arm or leg."
                }
            }

            // ---- Fabric --------------------------------------------------------------
            Section {
                title: "Fabric Physics"
                visible: root.sel.kind === "pieces"
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: "Preset"; Layout.preferredWidth: 118; opacity: 0.85 }
                    ComboBox {
                        id: presetBox
                        Layout.fillWidth: true
                        implicitHeight: 26
                        model: root.sel.physics && root.sel.presets.indexOf(root.sel.physics.preset) < 0
                             ? root.sel.presets.concat([root.sel.physics.preset]) : root.sel.presets
                        currentIndex: root.sel.physics ? model.indexOf(root.sel.physics.preset) : -1
                        onActivated: if (root.sel.presets.indexOf(currentText) >= 0) App.setPreset(currentText)
                    }
                }
                SliderRow { label: "Weight"; from: 20; to: 1000; stepSize: 5; suffix: " g/m²"; value: root.sel.physics ? root.sel.physics.density : 150; onEdited: (v) => App.setPieceValue("density", v) }
                SliderRow { label: "Stretch"; value: root.sel.physics ? root.sel.physics.stretch : 0; onEdited: (v) => App.setPieceValue("stretch", v) }
                SliderRow { label: "Shear"; value: root.sel.physics ? root.sel.physics.shear : 0; onEdited: (v) => App.setPieceValue("shear", v) }
                SliderRow { label: "Bend"; value: root.sel.physics ? root.sel.physics.bend : 0; onEdited: (v) => App.setPieceValue("bend", v) }
                SliderRow { label: "Damping"; value: root.sel.physics ? root.sel.physics.damping : 0; onEdited: (v) => App.setPieceValue("damping", v) }
                SliderRow { label: "Friction"; value: root.sel.physics ? root.sel.physics.friction : 0; onEdited: (v) => App.setPieceValue("friction", v) }
                SliderRow { label: "Thickness"; from: 0.5; to: 15; stepSize: 0.5; decimals: 1; suffix: " mm"; value: root.sel.physics ? root.sel.physics.thickness : 3; onEdited: (v) => App.setPieceValue("thickness", v) }
            }

            // ---- Material ------------------------------------------------------------
            Section {
                title: "Material"
                visible: root.sel.kind === "pieces" && !!root.sel.material
                RowLayout {
                    Layout.fillWidth: true
                    ComboBox {
                        Layout.fillWidth: true
                        implicitHeight: 26
                        model: root.sel.materials
                        textRole: "name"
                        valueRole: "id"
                        currentIndex: root.sel.material ? root.sel.materials.findIndex(m => m.id === root.sel.material.id) : -1
                        onActivated: App.assignMaterial(currentValue)
                    }
                    Button {
                        text: "New"
                        implicitHeight: 26
                        onClicked: App.newMaterial()
                        ToolTip.visible: hovered
                        ToolTip.text: "Give the selected pieces their own copy of this material"
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: "Name"; Layout.preferredWidth: 118; opacity: 0.85 }
                    TextField {
                        Layout.fillWidth: true
                        implicitHeight: 24
                        padding: 3
                        selectByMouse: true
                        text: root.sel.material ? root.sel.material.name : ""
                        onEditingFinished: if (root.sel.material && text.trim() !== "" && text !== root.sel.material.name) App.renameMaterial(text)
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: "Colour"; Layout.preferredWidth: 118; opacity: 0.85 }
                    Rectangle {
                        Layout.fillWidth: true
                        implicitHeight: 24
                        radius: 3
                        color: root.sel.material ? root.sel.material.color : "gray"
                        border.color: Qt.alpha(palette.windowText, 0.4)
                        MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: root.pickColor(root.sel.material.color) }
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: "Texture"; Layout.preferredWidth: 118; opacity: 0.85 }
                    Label {
                        Layout.fillWidth: true
                        elide: Text.ElideMiddle
                        text: root.sel.material && root.sel.material.textureName !== ""
                            ? (root.sel.material.textureMissing ? "⚠ missing: " : "") + root.sel.material.textureName : "None"
                        opacity: root.sel.material && root.sel.material.textureName !== "" ? 1 : 0.5
                        color: root.sel.material && root.sel.material.textureMissing ? "#ff8c69" : palette.windowText
                    }
                    Button { text: "…"; implicitWidth: 30; implicitHeight: 24; onClicked: root.pickTexture() }
                    Button {
                        text: "✕"; implicitWidth: 26; implicitHeight: 24
                        enabled: root.sel.material ? root.sel.material.textureName !== "" : false
                        onClicked: App.setMaterialTexture("")
                    }
                }
                SliderRow { label: "Texture repeat"; from: 0.25; to: 20; stepSize: 0.25; decimals: 2; suffix: "×"; value: root.sel.material ? root.sel.material.repeat : 1; onEdited: (v) => App.setMaterialValue("repeat", v) }
                SliderRow { label: "Roughness"; from: 0; to: 1; stepSize: 0.01; decimals: 2; value: root.sel.material ? root.sel.material.roughness : 0.8; onEdited: (v) => App.setMaterialValue("roughness", v) }
                SliderRow { label: "Metallic"; from: 0; to: 1; stepSize: 0.01; decimals: 2; value: root.sel.material ? root.sel.material.metallic : 0; onEdited: (v) => App.setMaterialValue("metallic", v) }
                SliderRow { label: "Opacity"; from: 0; to: 1; stepSize: 0.01; decimals: 2; value: root.sel.material ? root.sel.material.opacity : 1; onEdited: (v) => App.setMaterialValue("opacity", v) }
                Label {
                    visible: root.sel.material ? root.sel.material.users > root.sel.count : false
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: root.sel.material ? "Shared by " + root.sel.material.users + " pieces — changes affect all of them." : ""
                }
            }

            // ---- UV ------------------------------------------------------------------
            Section {
                title: "UV & Texel Density"
                visible: root.sel.kind === "pieces"
                InfoRow {
                    label: "Texel density"
                    value: root.sel.piece ? root.sel.piece.texelDensity.toFixed(2) + " px/cm  @ " + root.sel.piece.textureResolution + " px" : ""
                }
                SliderRow { label: "UV scale"; from: 0.1; to: 4; stepSize: 0.05; decimals: 2; suffix: "×"; value: root.sel.piece ? root.sel.piece.uvScale : 1; onEdited: (v) => App.setPieceValue("uvScale", v) }
                SliderRow { label: "UV rotation"; from: -180; to: 180; stepSize: 5; suffix: "°"; value: root.sel.piece ? root.sel.piece.uvRotation : 0; onEdited: (v) => App.setPieceValue("uvRotation", v) }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: "UVs follow the 2D pattern layout. Switch the 2D view to UV to move pieces in texture space; turn on Checker in the 3D view to preview density."
                }
            }

            // ---- Simulation ----------------------------------------------------------
            Section {
                title: "Simulation"
                open: root.sel.kind !== "pieces"
                CheckBox {
                    visible: !!root.sel.garment
                    Layout.fillWidth: true
                    text: "Also simulate with other garments"
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: "When on, this garment is simulated too while another garment is the active one."
                    checked: root.sel.garment ? root.sel.garment.simulateWithOthers : false
                    onToggled: App.setSimulateWithOthers(root.sel.garment.id, checked)
                }
                SliderRow { label: "Gravity"; from: -20; to: 0; stepSize: 0.1; decimals: 1; suffix: " m/s²"; value: root.sel.simulation.gravity !== undefined ? root.sel.simulation.gravity : -9.8; onEdited: (v) => App.setSimulationValue("gravity", v) }
                SliderRow { label: "Substeps"; from: 4; to: 40; stepSize: 1; value: root.sel.simulation.substeps !== undefined ? root.sel.simulation.substeps : 12; onEdited: (v) => App.setSimulationValue("substeps", v) }
                SliderRow { label: "Speed"; from: 0.1; to: 2; stepSize: 0.1; decimals: 1; suffix: "×"; value: root.sel.simulation.timeScale !== undefined ? root.sel.simulation.timeScale : 1; onEdited: (v) => App.setSimulationValue("timeScale", v) }
                CheckBox {
                    Layout.fillWidth: true
                    text: "Cloth-to-cloth collision"
                    checked: !!root.sel.simulation.selfCollision
                    onToggled: App.setSimulationValue("selfCollision", checked ? 1 : 0)
                }
                CheckBox {
                    text: "Ground plane"
                    checked: !!root.sel.simulation.groundPlane
                    onToggled: App.setSimulationValue("groundPlane", checked ? 1 : 0)
                }
            }

            Item { Layout.fillHeight: true; implicitHeight: 12 }
        }
    }
}
