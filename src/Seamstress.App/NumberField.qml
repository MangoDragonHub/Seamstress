import QtQuick
import QtQuick.Controls

// A box for a number that can be typed or dragged. Press and drag left or right to change the value (hold Shift for
// fine steps, Ctrl for coarse ones); a click without dragging selects the number for typing. The arrow keys step it.
// "edited" reports each new value: continuously while dragging, on Enter or when the box loses focus after typing,
// and as you type when "live" is set. The box never changes "value" itself, so it can stay bound to the model.
TextField {
    id: field
    property real value: 0
    property int decimals: 2
    property real step: 1
    property real from: -1e9
    property real to: 1e9
    property bool live: false
    readonly property bool dragging: scrub.dragging
    signal edited(real value)

    selectByMouse: true
    horizontalAlignment: Text.AlignRight
    validator: DoubleValidator { notation: DoubleValidator.StandardNotation; locale: "C" }

    function shown(v) { return Number(v).toFixed(decimals) }
    function clamp(v) { return Math.min(to, Math.max(from, v)) }
    function typed() { const v = Number(text); return text.trim() === "" || isNaN(v) ? NaN : v }
    function report(v) {
        v = clamp(Number(Number(v).toFixed(decimals)))
        text = shown(v)
        edited(v)
    }

    Component.onCompleted: text = shown(value)
    onValueChanged: if (!activeFocus) text = shown(value)
    onTextEdited: if (live && !isNaN(typed())) edited(clamp(typed()))
    onEditingFinished: {
        const v = typed()
        if (!isNaN(v) && Math.abs(clamp(v) - value) > 1e-9)
            report(v)
        else
            text = shown(value)
    }
    Keys.onUpPressed: report((isNaN(typed()) ? value : typed()) + step)
    Keys.onDownPressed: report((isNaN(typed()) ? value : typed()) - step)

    // Dragging, only while the box is not being typed in, so that the mouse selects text as usual then.
    MouseArea {
        id: scrub
        anchors.fill: parent
        enabled: field.enabled && !field.readOnly && !field.activeFocus
        cursorShape: Qt.SizeHorCursor
        preventStealing: true
        property bool dragging: false
        property real lastX: 0
        property real start: 0
        property real moved: 0      // in steps since the press
        property real current: 0
        onPressed: (mouse) => {
            lastX = mouse.x
            moved = 0
            dragging = false
            start = isNaN(field.typed()) ? field.value : field.typed()
            current = start
        }
        onPositionChanged: (mouse) => {
            const dx = mouse.x - lastX
            if (!dragging && Math.abs(dx) < 3)
                return
            dragging = true
            lastX = mouse.x
            // Eight pixels a step; Shift a tenth of that, Ctrl ten times as much.
            const factor = (mouse.modifiers & Qt.ShiftModifier) ? 0.1 : (mouse.modifiers & Qt.ControlModifier) ? 10 : 1
            moved += dx / 8 * factor
            const v = field.clamp(Number((start + moved * field.step).toFixed(field.decimals)))
            // Past either end, dragging back starts changing the value again straight away.
            if (field.step > 0 && (v <= field.from || v >= field.to))
                moved = (v - start) / field.step
            if (Math.abs(v - current) > 1e-9) {
                current = v
                field.report(v)
            }
        }
        onReleased: {
            if (!dragging) {
                field.forceActiveFocus()
                field.selectAll()
            }
            dragging = false
        }
        onCanceled: dragging = false
    }
}
