import QtQuick
import QtQuick.Controls

// Right-click menu of the 3D pin brush, and the same actions under the Simulation menu.
Menu {
    function tr(text) { App.languageRevision; return App.tr(text) }
    MenuItem {
        text: tr("Pin Selected Vertices")
        enabled: App.pinSelectionCount > 0
        onTriggered: App.pinSelectedVertices()
    }
    MenuItem {
        text: tr("Unpin Selected Vertices")
        enabled: App.pinSelectionCount > 0
        onTriggered: App.unpinSelectedVertices()
    }
    MenuItem {
        text: tr("Clear Vertex Selection")
        enabled: App.pinSelectionCount > 0
        onTriggered: App.clearPinSelection()
    }
    MenuSeparator {}
    MenuItem {
        text: tr("Unpin All on Selected Garments")
        onTriggered: App.unpinAll()
    }
    MenuSeparator {}
    MenuItem {
        text: tr("Turn Off Pin Brush")
        onTriggered: App.pinBrush = false
    }
}
