import QtQuick
import QtQuick.Controls

// A menu entry that shows its keyboard shortcut on the right. "command" is an id from App.keyBindingsJson(), so the
// key shown follows whatever was chosen in Preferences.
MenuItem {
    id: item
    property string command: ""
    readonly property string keys: { App.keysRevision; return command === "" ? "" : App.keyText(command) }

    implicitWidth: Math.max(implicitBackgroundWidth + leftInset + rightInset,
                            implicitContentWidth + leftPadding + rightPadding + (keys !== "" ? keyLabel.implicitWidth + 32 : 0))

    Label {
        id: keyLabel
        visible: item.keys !== ""
        text: item.keys
        anchors { right: parent.right; rightMargin: item.rightPadding + 4; verticalCenter: parent.verticalCenter }
        font.pixelSize: item.font.pixelSize - 1
        color: item.highlighted ? item.palette.highlightedText : item.palette.windowText
        opacity: item.enabled ? 0.6 : 0.3
    }
}
