import QtQuick
import QtQuick.Controls

// A dialog whose standard buttons follow the interface language; Qt would label them in English. "okText" renames
// the OK button (e.g. to "Send").
Dialog {
    id: dialog
    property string okText: tr("OK")

    function tr(text) { App.languageRevision; return App.tr(text) }

    Component.onCompleted: {
        const labels = [[Dialog.Ok, () => dialog.okText], [Dialog.Cancel, () => tr("Cancel")], [Dialog.Save, () => tr("Save")],
                        [Dialog.Close, () => tr("Close")], [Dialog.Discard, () => tr("Don't Save")]]
        for (const [kind, label] of labels) {
            const button = standardButton(kind)
            if (button)
                button.text = Qt.binding(label)
        }
    }
}
