import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Properties of the current selection: topology, arrangement, fabric physics, material, UVs, seams
// and the simulation settings.
Pane {
    id: root
    padding: 0

    function tr(text) { App.languageRevision; return App.tr(text) }
    property var sel: ({ kind: "none", count: 0, presets: [], materials: [], simulation: {} })
    signal pickColor(string current)
    signal pickTexture()

    // Values are stored metric; with imperial units they are converted for display and converted back when edited.
    readonly property bool imperial: App.imperial
    readonly property real cmUnit: imperial ? 2.54 : 1            // centimetres per displayed length unit
    readonly property real mUnit: imperial ? 0.0254 : 1           // metres per displayed position unit
    readonly property real mmUnit: imperial ? 25.4 : 1            // millimetres per displayed thickness unit
    readonly property real gsmUnit: imperial ? 33.9057 : 1        // g/m² per displayed fabric weight unit (oz/yd²)
    readonly property string lengthName: imperial ? "in" : "cm"

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
        property int valueWidth: 54
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
            Layout.preferredWidth: sliderRow.valueWidth
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
                text: root.sel.kind === "seam" ? tr("Seam")
                    : root.sel.kind === "pieces" ? root.sel.piece.name
                    : root.sel.kind === "garment" ? root.sel.garment.name
                    : tr("Properties")
            }

            Label {
                visible: root.sel.kind === "none"
                Layout.fillWidth: true
                padding: 8
                wrapMode: Text.WordWrap
                opacity: 0.6
                text: tr("Nothing selected. Add a garment from the Garment menu or draw a piece in the 2D view.")
            }

            // ---- Topology ------------------------------------------------------------
            Section {
                title: tr("Topology")
                visible: !!root.sel.garment
                SliderRow {
                    label: tr("Particle distance")
                    from: root.imperial ? 0.2 : 0.5; to: root.imperial ? 2.4 : 6
                    stepSize: root.imperial ? 0.05 : 0.1; decimals: root.imperial ? 2 : 1; suffix: " " + root.lengthName
                    value: (root.sel.garment ? root.sel.garment.particleDistance : 2) / root.cmUnit
                    onEdited: (v) => App.setParticleDistance(v * root.cmUnit)
                }
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: tr("Faces"); Layout.preferredWidth: 118; opacity: 0.85 }
                    ComboBox {
                        Layout.fillWidth: true
                        implicitHeight: 26
                        model: [tr("Triangles"), tr("Quads")]
                        currentIndex: root.sel.garment && root.sel.garment.topology === "Quads" ? 1 : 0
                        onActivated: App.setTopology(currentIndex === 1 ? "Quads" : "Triangles")
                    }
                }
                InfoRow {
                    label: tr("Garment mesh")
                    value: root.sel.garment ? tr("%1 faces  ·  %2 verts").arg(root.sel.garment.faces.toLocaleString(Qt.locale(), "f", 0))
                                                .arg(root.sel.garment.vertices.toLocaleString(Qt.locale(), "f", 0)) : ""
                }
                InfoRow {
                    visible: root.sel.garment ? root.sel.garment.topology === "Quads" : false
                    label: tr("Of which")
                    value: root.sel.garment ? tr("%1 quads  ·  %2 triangles").arg(root.sel.garment.quads.toLocaleString(Qt.locale(), "f", 0))
                                                .arg((root.sel.garment.faces - root.sel.garment.quads).toLocaleString(Qt.locale(), "f", 0)) : ""
                }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: tr("Smaller distance = more faces: finer wrinkles, slower simulation. Quads export to OBJ and FBX; glTF and MakeHuman always receive triangles.")
                }
                NumberRow {
                    visible: root.sel.kind === "pieces"
                    label: tr("Piece override")
                    decimals: root.imperial ? 2 : 1; step: root.imperial ? 0.05 : 0.1; suffix: root.lengthName
                    value: (root.sel.piece ? root.sel.piece.particleDistance : 0) / root.cmUnit
                    onEdited: (v) => App.setPieceValue("particleDistance", v * root.cmUnit)
                }
                InfoRow {
                    visible: root.sel.kind === "pieces"
                    label: tr("Selected")
                    value: root.sel.piece ? tr("%1 faces").arg(root.sel.piece.faces.toLocaleString(Qt.locale(), "f", 0)) + "  ·  "
                         + (root.sel.piece.area / (root.cmUnit * root.cmUnit)).toFixed(0) + " " + root.lengthName + "²" : ""
                }
                Label {
                    visible: root.sel.kind === "pieces"
                    Layout.fillWidth: true
                    opacity: 0.55
                    font.pixelSize: 11
                    text: tr("Override 0 uses the garment's particle distance.")
                }
            }

            // ---- Seam ----------------------------------------------------------------
            Section {
                title: tr("Seam")
                visible: root.sel.kind === "seam"
                InfoRow { label: tr("From"); value: root.sel.seam ? root.sel.seam.a : "" }
                InfoRow { label: tr("To"); value: root.sel.seam ? root.sel.seam.b : "" }
                InfoRow {
                    label: tr("Lengths")
                    value: root.sel.seam ? (root.sel.seam.lengthA / root.cmUnit).toFixed(root.imperial ? 2 : 1) + " " + root.lengthName + "  ↔  "
                         + (root.sel.seam.lengthB / root.cmUnit).toFixed(root.imperial ? 2 : 1) + " " + root.lengthName : ""
                }
                RowLayout {
                    Layout.fillWidth: true
                    Button {
                        Layout.fillWidth: true
                        text: tr("Reverse Direction")
                        onClicked: App.reverseSeam()
                        ToolTip.visible: hovered
                        ToolTip.text: tr("Swap which ends of the two edges are sewn together (fixes a twisted seam)")
                    }
                    Button { Layout.fillWidth: true; text: tr("Delete Seam"); onClicked: App.deleteSelection() }
                }
            }

            // ---- Arrangement ---------------------------------------------------------
            Section {
                title: tr("3D Arrangement")
                visible: root.sel.kind === "pieces"
                NumberRow { label: tr("Position X"); suffix: root.imperial ? "in" : "m"; decimals: root.imperial ? 1 : 2; step: root.imperial ? 0.5 : 0.01; value: (root.sel.piece ? root.sel.piece.posX : 0) / root.mUnit; onEdited: (v) => App.setPieceValue("posX", v * root.mUnit) }
                NumberRow { label: tr("Position Y"); suffix: root.imperial ? "in" : "m"; decimals: root.imperial ? 1 : 2; step: root.imperial ? 0.5 : 0.01; value: (root.sel.piece ? root.sel.piece.posY : 0) / root.mUnit; onEdited: (v) => App.setPieceValue("posY", v * root.mUnit) }
                NumberRow { label: tr("Position Z"); suffix: root.imperial ? "in" : "m"; decimals: root.imperial ? 1 : 2; step: root.imperial ? 0.5 : 0.01; value: (root.sel.piece ? root.sel.piece.posZ : 0) / root.mUnit; onEdited: (v) => App.setPieceValue("posZ", v * root.mUnit) }
                NumberRow { label: tr("Rotation X"); suffix: "°"; decimals: 1; step: 5; value: root.sel.piece ? root.sel.piece.rotX : 0; onEdited: (v) => App.setPieceValue("rotX", v) }
                NumberRow { label: tr("Rotation Y"); suffix: "°"; decimals: 1; step: 5; value: root.sel.piece ? root.sel.piece.rotY : 0; onEdited: (v) => App.setPieceValue("rotY", v) }
                NumberRow { label: tr("Rotation Z"); suffix: "°"; decimals: 1; step: 5; value: root.sel.piece ? root.sel.piece.rotZ : 0; onEdited: (v) => App.setPieceValue("rotZ", v) }
                SliderRow {
                    label: tr("Wrap")
                    from: -300; to: 300; stepSize: 5; suffix: "°"
                    value: root.sel.piece ? root.sel.piece.curvature : 0
                    onEdited: (v) => App.setPieceValue("curvature", v)
                }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: tr("Where the piece sits in 3D; you can also drag the arrows and rings in the 3D view. Wrap curls the flat piece around an arm or leg.")
                }
                InfoRow {
                    readonly property int flipped: root.sel.piece ? root.sel.piece.flippedNormals : 0
                    readonly property int count: root.sel.piece ? root.sel.piece.count : 0
                    label: tr("Normals")
                    value: flipped === 0 ? tr("As drawn") : flipped === count ? (count === 1 ? tr("Inverted") : tr("All inverted"))
                                                                         : tr("%1 of %2 inverted").arg(flipped).arg(count)
                }
                Button {
                    Layout.fillWidth: true
                    text: tr("Invert Normals")
                    onClicked: App.invertNormals()
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Turn the faces of the selected pieces to point the other way. Use View ▸ 3D: Normal Direction to see which way they face: blue is the front, red the back.")
                }
            }

            // ---- Fabric --------------------------------------------------------------
            Section {
                title: tr("Fabric Physics")
                visible: root.sel.kind === "pieces"
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: tr("Preset"); Layout.preferredWidth: 118; opacity: 0.85 }
                    ComboBox {
                        id: presetBox
                        Layout.fillWidth: true
                        implicitHeight: 26
                        // Preset names are stored in English and shown translated.
                        readonly property var names: root.sel.physics && root.sel.presets.indexOf(root.sel.physics.preset) < 0
                             ? root.sel.presets.concat([root.sel.physics.preset]) : root.sel.presets
                        model: names.map(n => root.tr(n))
                        currentIndex: root.sel.physics ? names.indexOf(root.sel.physics.preset) : -1
                        onActivated: (index) => { if (root.sel.presets.indexOf(names[index]) >= 0) App.setPreset(names[index]) }
                    }
                }
                SliderRow {
                    label: tr("Weight")
                    from: root.imperial ? 0.6 : 20; to: root.imperial ? 29.5 : 1000
                    stepSize: root.imperial ? 0.1 : 5; decimals: root.imperial ? 1 : 0
                    suffix: root.imperial ? " oz/yd²" : " g/m²"; valueWidth: root.imperial ? 70 : 54
                    value: (root.sel.physics ? root.sel.physics.density : 150) / root.gsmUnit
                    onEdited: (v) => App.setPieceValue("density", v * root.gsmUnit)
                }
                SliderRow { label: tr("Stretch"); value: root.sel.physics ? root.sel.physics.stretch : 0; onEdited: (v) => App.setPieceValue("stretch", v) }
                SliderRow { label: tr("Shear"); value: root.sel.physics ? root.sel.physics.shear : 0; onEdited: (v) => App.setPieceValue("shear", v) }
                SliderRow { label: tr("Bend"); value: root.sel.physics ? root.sel.physics.bend : 0; onEdited: (v) => App.setPieceValue("bend", v) }
                SliderRow { label: tr("Damping"); value: root.sel.physics ? root.sel.physics.damping : 0; onEdited: (v) => App.setPieceValue("damping", v) }
                SliderRow { label: tr("Friction"); value: root.sel.physics ? root.sel.physics.friction : 0; onEdited: (v) => App.setPieceValue("friction", v) }
                SliderRow {
                    label: tr("Thickness")
                    from: root.imperial ? 0.02 : 0.5; to: root.imperial ? 0.6 : 15
                    stepSize: root.imperial ? 0.02 : 0.5; decimals: root.imperial ? 2 : 1; suffix: root.imperial ? " in" : " mm"
                    value: (root.sel.physics ? root.sel.physics.thickness : 3) / root.mmUnit
                    onEdited: (v) => App.setPieceValue("thickness", v * root.mmUnit)
                }
            }

            // ---- Material ------------------------------------------------------------
            Section {
                title: tr("Material")
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
                        text: tr("New")
                        implicitHeight: 26
                        onClicked: App.newMaterial()
                        ToolTip.visible: hovered
                        ToolTip.text: tr("Give the selected pieces their own copy of this material")
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: tr("Name"); Layout.preferredWidth: 118; opacity: 0.85 }
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
                    Label { text: tr("Colour"); Layout.preferredWidth: 118; opacity: 0.85 }
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
                    Label { text: tr("Texture"); Layout.preferredWidth: 118; opacity: 0.85 }
                    Label {
                        Layout.fillWidth: true
                        elide: Text.ElideMiddle
                        text: root.sel.material && root.sel.material.textureName !== ""
                            ? (root.sel.material.textureMissing ? "⚠ " + tr("missing:") + " " : "") + root.sel.material.textureName : tr("None")
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
                SliderRow { label: tr("Texture repeat"); from: 0.25; to: 20; stepSize: 0.25; decimals: 2; suffix: "×"; value: root.sel.material ? root.sel.material.repeat : 1; onEdited: (v) => App.setMaterialValue("repeat", v) }
                SliderRow { label: tr("Roughness"); from: 0; to: 1; stepSize: 0.01; decimals: 2; value: root.sel.material ? root.sel.material.roughness : 0.8; onEdited: (v) => App.setMaterialValue("roughness", v) }
                SliderRow { label: tr("Metallic"); from: 0; to: 1; stepSize: 0.01; decimals: 2; value: root.sel.material ? root.sel.material.metallic : 0; onEdited: (v) => App.setMaterialValue("metallic", v) }
                SliderRow { label: tr("Opacity"); from: 0; to: 1; stepSize: 0.01; decimals: 2; value: root.sel.material ? root.sel.material.opacity : 1; onEdited: (v) => App.setMaterialValue("opacity", v) }
                Label {
                    visible: root.sel.material ? root.sel.material.users > root.sel.count : false
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: root.sel.material ? tr("Shared by %1 pieces — changes affect all of them.").arg(root.sel.material.users) : ""
                }
            }

            // ---- UV ------------------------------------------------------------------
            Section {
                title: tr("UV & Texel Density")
                visible: root.sel.kind === "pieces"
                InfoRow {
                    label: tr("Texel density")
                    value: root.sel.piece ? (root.sel.piece.texelDensity * root.cmUnit).toFixed(root.imperial ? 1 : 2) + " px/" + root.lengthName + "  @ " + root.sel.piece.textureResolution + " px" : ""
                }
                SliderRow { label: tr("UV scale"); from: 0.1; to: 4; stepSize: 0.05; decimals: 2; suffix: "×"; value: root.sel.piece ? root.sel.piece.uvScale : 1; onEdited: (v) => App.setPieceValue("uvScale", v) }
                SliderRow { label: tr("UV rotation"); from: -180; to: 180; stepSize: 5; suffix: "°"; value: root.sel.piece ? root.sel.piece.uvRotation : 0; onEdited: (v) => App.setPieceValue("uvRotation", v) }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: tr("UVs follow the 2D pattern layout. Switch the 2D view to UV to move pieces in texture space; turn on Checker in the 3D view to preview density.")
                }
            }

            // ---- Simulation ----------------------------------------------------------
            Section {
                title: tr("Simulation")
                open: root.sel.kind !== "pieces"
                CheckBox {
                    visible: !!root.sel.garment
                    Layout.fillWidth: true
                    text: tr("Also simulate with other garments")
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("When on, this garment is simulated too while another garment is the active one.")
                    checked: root.sel.garment ? root.sel.garment.simulateWithOthers : false
                    onToggled: App.setSimulateWithOthers(root.sel.garment.id, checked)
                }
                SliderRow { label: tr("Gravity"); from: -20; to: 0; stepSize: 0.1; decimals: 1; suffix: " m/s²"; value: root.sel.simulation.gravity !== undefined ? root.sel.simulation.gravity : -9.8; onEdited: (v) => App.setSimulationValue("gravity", v) }
                SliderRow { label: tr("Substeps"); from: 4; to: 40; stepSize: 1; value: root.sel.simulation.substeps !== undefined ? root.sel.simulation.substeps : 12; onEdited: (v) => App.setSimulationValue("substeps", v) }
                SliderRow { label: tr("Speed"); from: 0.1; to: 2; stepSize: 0.1; decimals: 1; suffix: "×"; value: root.sel.simulation.timeScale !== undefined ? root.sel.simulation.timeScale : 1; onEdited: (v) => App.setSimulationValue("timeScale", v) }
                CheckBox {
                    Layout.fillWidth: true
                    text: tr("Cloth-to-cloth collision")
                    checked: !!root.sel.simulation.selfCollision
                    onToggled: App.setSimulationValue("selfCollision", checked ? 1 : 0)
                }
                CheckBox {
                    text: tr("Ground plane")
                    checked: !!root.sel.simulation.groundPlane
                    onToggled: App.setSimulationValue("groundPlane", checked ? 1 : 0)
                }
            }

            Item { Layout.fillHeight: true; implicitHeight: 12 }
        }
    }
}
