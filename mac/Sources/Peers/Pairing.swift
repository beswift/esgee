import Foundation
import CryptoKit

enum PairAttemptResult: Sendable { case accepted, wrongPin, notActive }

/// One Bluetooth-style pairing offer: a cryptographically random 6-digit PIN
/// that lives for two minutes, is single-use (the first success consumes it),
/// and locks out after 5 wrong guesses. The PIN authorizes exactly one thing —
/// handing the real PeerToken to the machine that typed it — and neither the
/// PIN nor the token is ever written to the log.
///
/// Lifetime is tied to the pairing window: the window registers the session
/// with PeerServer.beginPairing on show and close()s it when it goes away, so
/// /pair exists only while a human is looking at the PIN.
///
/// Callbacks fire on server worker threads — subscribers hop to the main
/// actor themselves.
final class PairingSession: @unchecked Sendable {
    static let maxAttempts = 5
    static let lifetime: TimeInterval = 120

    private let gate = NSLock()
    /// The PIN is compared hash-to-hash (same trick as the token): the digest
    /// comparison leaks nothing about the digits however long it takes,
    /// because timing can only reveal the digest — and the digest reveals
    /// nothing a guesser can use.
    private let pinDigest: SHA256.Digest
    private var failures = 0
    private var consumed = false
    private var closed = false

    /// The 6 digits. Shown on screen, compared in constant time, never logged.
    let pin: String

    let expiresAt: Date

    var failuresSoFar: Int {
        gate.lock()
        defer { gate.unlock() }
        return failures
    }

    /// Fires when a peer redeems the PIN; carries the redeeming machine's name.
    var onSucceeded: (@Sendable (String) -> Void)?

    /// Fires on every wrong guess, with the running failure count.
    var onWrongGuess: (@Sendable (Int) -> Void)?

    /// Fires after the 5th wrong guess.
    var onLockedOut: (@Sendable () -> Void)?

    init() {
        // Int.random over the exact range is uniform — no modulo bias — and
        // SystemRandomNumberGenerator draws from the OS CSPRNG.
        var rng = SystemRandomNumberGenerator()
        pin = String(format: "%06d", Int.random(in: 0..<1_000_000, using: &rng))
        pinDigest = SHA256.hash(data: Data(pin.utf8))
        expiresAt = Date().addingTimeInterval(Self.lifetime)
    }

    var active: Bool {
        gate.lock()
        defer { gate.unlock() }
        return activeLocked
    }

    private var activeLocked: Bool {
        !closed && !consumed && failures < Self.maxAttempts && Date() < expiresAt
    }

    /// The pairing window is gone — /pair goes dark immediately.
    func close() {
        gate.lock()
        closed = true
        gate.unlock()
    }

    func tryRedeem(pin: String, peerMachine: String) -> PairAttemptResult {
        let result: PairAttemptResult
        var failuresNow = 0
        var lockedOutNow = false

        gate.lock()
        if !activeLocked {
            gate.unlock()
            return .notActive
        }

        if SHA256.hash(data: Data(pin.utf8)) == pinDigest {
            consumed = true // single-use: success invalidates the PIN forever
            result = .accepted
        } else {
            failures += 1
            failuresNow = failures
            lockedOutNow = failures >= Self.maxAttempts
            result = .wrongPin
        }
        let succeededCb = onSucceeded
        let wrongCb = onWrongGuess
        let lockedCb = onLockedOut
        gate.unlock()

        // Events fire outside the lock — a subscriber that hops to the main
        // actor and back must not be able to deadlock a server worker.
        switch result {
        case .accepted:
            succeededCb?(peerMachine)
        case .wrongPin:
            wrongCb?(failuresNow)
            if lockedOutNow { lockedCb?() }
        case .notActive:
            break
        }
        return result
    }
}
