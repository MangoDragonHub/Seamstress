import QtQuick
import QtQuick.Controls

// The Edit menu. The same menu opens from the menu bar and when right-clicking in the 2D and 3D views.
Menu {
    title: tr("&Edit")
    function tr(text) { App.languageRevision; return App.tr(text) }
    KeyMenuItem { text: App.undoText; command: "edit.undo"; enabled: App.canUndo; onTriggered: App.undo() }
    KeyMenuItem { text: App.redoText; command: "edit.redo"; enabled: App.canRedo; onTriggered: App.redo() }
    MenuSeparator {}
    KeyMenuItem { text: tr("Select All in Garment"); command: "edit.selectAll"; onTriggered: App.selectAll() }
    KeyMenuItem { text: tr("Duplicate"); command: "edit.duplicate"; onTriggered: App.duplicate() }
    KeyMenuItem { text: tr("Delete"); command: "edit.delete"; onTriggered: App.deleteSelection() }
    MenuSeparator {}
    KeyMenuItem { text: tr("Mirror Piece"); command: "edit.mirror"; onTriggered: App.mirror() }
    KeyMenuItem { text: tr("Rotate 90° Left"); command: "edit.rotateLeft"; onTriggered: App.rotate(90) }
    KeyMenuItem { text: tr("Rotate 90° Right"); command: "edit.rotateRight"; onTriggered: App.rotate(-90) }
    KeyMenuItem { text: tr("Straighten Curved Edges"); command: "edit.straighten"; onTriggered: App.straightenEdges() }
    KeyMenuItem { text: tr("Invert Normals"); command: "edit.invertNormals"; onTriggered: App.invertNormals() }
    KeyMenuItem { text: tr("Cut Hole"); command: "edit.cutHole"; onTriggered: App.cutHole() }
    MenuSeparator {}
    KeyMenuItem { text: tr("Group Selected Pieces"); command: "edit.group"; onTriggered: App.groupSelection() }
    KeyMenuItem { text: tr("Reverse Seam Direction"); command: "edit.reverseSeam"; onTriggered: App.reverseSeam() }
}
