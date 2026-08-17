import AppKit
import Carbon

/// Chord actions. Raw values match the Windows action names so log lines
/// ("hotkey pressed -> region") stay greppable across platforms.
enum HotkeyAction: String, CaseIterable, Sendable {
    case region, screen, last, timer, archive
}

/// "Ctrl+Shift+S" → Carbon (keyCode, modifiers). Modifier words are accepted
/// case-insensitively; "win" maps to the Command key so a settings file
/// copied over from a Windows machine still parses to something sane.
struct Chord: Sendable, Equatable {
    let keyCode: UInt32
    let carbonModifiers: UInt32
    /// The original string, for menus and logs — never re-derived from the
    /// key code, so what the user typed is what the log says.
    let display: String

    static func parse(_ chord: String) -> Chord? {
        let parts = chord.split(separator: "+")
            .map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
            .filter { !$0.isEmpty }
        guard let keyToken = parts.last else { return nil }

        var mods: UInt32 = 0
        for word in parts.dropLast() {
            switch word {
            case "ctrl", "control": mods |= UInt32(controlKey)
            case "shift": mods |= UInt32(shiftKey)
            case "alt", "option", "opt": mods |= UInt32(optionKey)
            case "cmd", "command", "win": mods |= UInt32(cmdKey)
            default: return nil
            }
        }

        guard let code = keyCodes[keyToken] else { return nil }
        return Chord(keyCode: code, carbonModifiers: mods, display: chord)
    }

    /// ANSI-layout virtual key codes (kVK_ANSI_*). Carbon registers by
    /// position, not character — good enough for a–z, 0–9, f1–f12, which is
    /// the whole grammar the settings file promises.
    private static let keyCodes: [String: UInt32] = [
        "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7,
        "c": 8, "v": 9, "b": 11, "q": 12, "w": 13, "e": 14, "r": 15,
        "y": 16, "t": 17, "o": 31, "u": 32, "i": 34, "p": 35, "l": 37,
        "j": 38, "k": 40, "n": 45, "m": 46,
        "1": 18, "2": 19, "3": 20, "4": 21, "6": 22, "5": 23, "9": 25,
        "7": 26, "8": 28, "0": 29,
        "f1": 122, "f2": 120, "f3": 99, "f4": 118, "f5": 96, "f6": 97,
        "f7": 98, "f8": 100, "f9": 101, "f10": 109, "f11": 103, "f12": 111,
    ]
}

/// Carbon dispatches hot key events on the main run loop; assumeIsolated
/// documents that fact instead of hopping (a hop would let a second press
/// interleave with the first one's capture flow).
nonisolated(unsafe) private let hotkeyEventHandler: EventHandlerUPP = { _, event, userData in
    guard let event, let userData else { return OSStatus(eventNotHandledErr) }
    var hkID = EventHotKeyID()
    let status = GetEventParameter(event,
                                   EventParamName(kEventParamDirectObject),
                                   EventParamType(typeEventHotKeyID),
                                   nil, MemoryLayout<EventHotKeyID>.size, nil, &hkID)
    guard status == noErr else { return status }
    let manager = Unmanaged<HotkeyManager>.fromOpaque(userData).takeUnretainedValue()
    MainActor.assumeIsolated {
        manager.fire(id: hkID.id)
    }
    return noErr
}

/// Carbon RegisterEventHotKey — still the correct API: system-wide and needs
/// NO Accessibility permission (an NSEvent global monitor would add a second
/// TCC prompt for no benefit; docs/MAC.md "Hotkeys"). A failed registration
/// never aborts startup — the menu bar remains the fallback, same as the
/// Windows tray.
@MainActor
final class HotkeyManager {
    private static let baseId: UInt32 = 0xE5E0
    private static let signature: OSType = 0x6573_6765 // "esge"

    /// Successfully registered (action, chord) pairs, in order.
    private(set) var bound: [(action: HotkeyAction, chord: String)] = []

    private let onPress: @MainActor (HotkeyAction) -> Void
    private var actions: [UInt32: HotkeyAction] = [:]
    private var refs: [EventHotKeyRef] = []
    private var handlerRef: EventHandlerRef?

    init(bindings: [(chord: String, action: HotkeyAction)],
         onPress: @escaping @MainActor (HotkeyAction) -> Void) {
        self.onPress = onPress

        // One handler for every hot key, installed before any registration so
        // a press can never race the table it looks itself up in.
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard),
                                 eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetEventDispatcherTarget(), hotkeyEventHandler, 1, &spec,
                            Unmanaged.passUnretained(self).toOpaque(), &handlerRef)

        var seen = Set<String>()
        var nextId = Self.baseId
        for binding in bindings {
            let raw = binding.chord.trimmingCharacters(in: .whitespaces)
            // Duplicate chords keep the first binding, same as Windows.
            if raw.isEmpty || !seen.insert(raw.lowercased()).inserted { continue }

            guard let chord = Chord.parse(raw) else {
                Log.warn("hotkey '\(raw)' does not parse; skipping")
                continue
            }

            var ref: EventHotKeyRef?
            let hkID = EventHotKeyID(signature: Self.signature, id: nextId)
            let status = RegisterEventHotKey(chord.keyCode, chord.carbonModifiers, hkID,
                                             GetEventDispatcherTarget(), 0, &ref)
            if status == noErr, let ref {
                actions[nextId] = binding.action
                refs.append(ref)
                bound.append((action: binding.action, chord: raw))
                Log.info("hotkey registered: \(raw) -> \(binding.action.rawValue)")
                nextId += 1
            } else {
                Log.warn("hotkey \(raw) unavailable (likely claimed by another app)")
            }
        }

        if bound.isEmpty {
            Log.error("no capture hotkey could be registered; capture only reachable from tray")
        }
    }

    func unregisterAll() {
        for ref in refs { UnregisterEventHotKey(ref) }
        refs.removeAll()
        actions.removeAll()
        bound.removeAll()
        if let handlerRef {
            RemoveEventHandler(handlerRef)
            self.handlerRef = nil
        }
    }

    fileprivate func fire(id: UInt32) {
        guard let action = actions[id] else { return }
        Log.info("hotkey pressed -> \(action.rawValue)")
        onPress(action)
    }
}
