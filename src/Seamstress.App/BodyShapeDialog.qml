import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Body shape of the MakeHuman avatar: the same sliders as MakeHuman's Main modelling tab. The avatar is rebuilt
// when a slider is released, which takes a second or two.
AppDialog {
    id: root
    title: tr("MakeHuman Body Shape")
    modal: false
    x: 300
    y: 90
    width: 420
    standardButtons: Dialog.Close

    property var shape: ({ gender: 0.5, age: 0.5, muscle: 0.5, weight: 0.5, height: 0.5, proportions: 0.5, heightCm: 0, shaped: true, loading: false })
    function refresh() { shape = JSON.parse(App.avatarShape()) }
    // "165.9 cm", or feet and inches with imperial units.
    function heightText(cm) {
        if (!App.imperial)
            return cm.toFixed(1) + " cm"
        const tenths = Math.round(cm / 2.54 * 10)
        const feet = Math.floor(tenths / 120)
        return feet + " ft " + ((tenths - feet * 120) / 10).toFixed(1) + " in"
    }
    onAboutToShow: refresh()
    Connections {
        target: App
        enabled: root.visible
        function onViewportRevisionChanged() { root.refresh() }
        function onSelectionRevisionChanged() { root.refresh() }
    }

    component ShapeRow: RowLayout {
        id: row
        property string key: ""
        property string label: ""
        property string low: ""
        property string high: ""
        property real value: root.shape[key]
        Layout.fillWidth: true
        spacing: 8
        onValueChanged: if (!slider.pressed) slider.value = value
        Label { text: row.label; Layout.preferredWidth: 84 }
        Label { text: row.low; opacity: 0.55; font.pixelSize: 11; Layout.preferredWidth: 46; horizontalAlignment: Text.AlignRight }
        Slider {
            id: slider
            Layout.fillWidth: true
            from: 0; to: 1; stepSize: 0.01
            value: row.value
            // Rebuilding the avatar is too slow to do on every movement, so apply when the handle is let go.
            onPressedChanged: if (!pressed && Math.abs(value - row.value) > 1e-4) App.setAvatarShape(row.key, value)
        }
        Label { text: row.high; opacity: 0.55; font.pixelSize: 11; Layout.preferredWidth: 46 }
        Label { text: Math.round(slider.value * 100) + "%"; Layout.preferredWidth: 38; horizontalAlignment: Text.AlignRight; opacity: 0.8 }
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 8
        ShapeRow { key: "gender"; label: tr("Gender"); low: tr("female"); high: tr("male") }
        ShapeRow { key: "age"; label: tr("Age"); low: tr("baby"); high: tr("old") }
        ShapeRow { key: "muscle"; label: tr("Muscle"); low: tr("less"); high: tr("more") }
        ShapeRow { key: "weight"; label: tr("Weight"); low: tr("light"); high: tr("heavy") }
        ShapeRow { key: "height"; label: tr("Height"); low: tr("short"); high: tr("tall") }
        ShapeRow { key: "proportions"; label: tr("Proportions"); low: tr("uncommon"); high: tr("ideal") }
        RowLayout {
            Layout.fillWidth: true
            Label {
                Layout.fillWidth: true
                opacity: 0.75
                text: root.shape.loading ? tr("Rebuilding the avatar…")
                    : root.shape.heightCm > 0 ? tr("Height: %1").arg(root.heightText(root.shape.heightCm)) : ""
            }
            Button { text: tr("Reset to Neutral"); onClicked: App.resetAvatarShape() }
        }
        Label {
            visible: !root.shape.shaped
            Layout.fillWidth: true
            wrapMode: Text.WordWrap
            color: "#ffb469"
            text: tr("MakeHuman's targets folder was not found next to the base mesh, so the body cannot be reshaped. It should be at data/targets beside data/3dobjs.")
        }
        Label {
            Layout.fillWidth: true
            wrapMode: Text.WordWrap
            opacity: 0.6
            font.pixelSize: 11
            text: tr("These match the sliders on MakeHuman's Main modelling tab; 50% everywhere is MakeHuman's default character. Garments already draped keep their shape — simulate again to fit them to the new body.")
        }
    }
}
