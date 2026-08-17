import AppKit

// No storyboard, no nib: the delegate builds everything once AppKit is
// pumping. LSUIElement in Info.plist keeps the app out of the Dock — the
// status item is the whole surface. Top-level lets keep both objects alive
// for the life of the process.
let delegate = AppDelegate()
NSApplication.shared.delegate = delegate
NSApplication.shared.run()
