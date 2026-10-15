import QtQuick
import QtQuick.Controls

// The Edit menu. The same menu opens from the menu bar and when right-clicking in the 2D and 3D views.
Menu {
    title: "&Edit"
    KeyMenuItem { text: App.undoText; command: "edit.undo"; enabled: App.canUndo; onTriggered: App.undo() }
    KeyMenuItem { text: App.redoText; command: "edit.redo"; enabled: App.canRedo; onTriggered: App.redo() }
    MenuSeparator {}
    KeyMenuItem { text: "Select All in Garment"; command: "edit.selectAll"; onTriggered: App.selectAll() }
    KeyMenuItem { text: "Duplicate"; command: "edit.duplicate"; onTriggered: App.duplicate() }
    KeyMenuItem { text: "Delete"; command: "edit.delete"; onTriggered: App.deleteSelection() }
    MenuSeparator {}
    KeyMenuItem { text: "Mirror Piece"; command: "edit.mirror"; onTriggered: App.mirror() }
    KeyMenuItem { text: "Rotate 90° Left"; command: "edit.rotateLeft"; onTriggered: App.rotate(90) }
    KeyMenuItem { text: "Rotate 90° Right"; command: "edit.rotateRight"; onTriggered: App.rotate(-90) }
    KeyMenuItem { text: "Straighten Curved Edges"; command: "edit.straighten"; onTriggered: App.straightenEdges() }
    KeyMenuItem { text: "Invert Normals"; command: "edit.invertNormals"; onTriggered: App.invertNormals() }
    MenuSeparator {}
    KeyMenuItem { text: "Group Selected Pieces"; command: "edit.group"; onTriggered: App.groupSelection() }
    KeyMenuItem { text: "Reverse Seam Direction"; command: "edit.reverseSeam"; onTriggered: App.reverseSeam() }
}
