using System.Security.Cryptography;
using System.Text;

namespace Esgee.Peers;

public enum PairAttemptResult { Accepted, WrongPin, NotActive }

/// <summary>
/// One Bluetooth-style pairing offer: a cryptographically random 6-digit PIN
/// that lives for two minutes, is single-use (the first success consumes it),
/// and locks out after 5 wrong guesses. The PIN authorizes exactly one thing —
/// handing the real PeerToken to the machine that typed it — and neither the
/// PIN nor the token is ever written to the log.
///
/// Lifetime is tied to the pairing window: the window registers the session
/// with PeerServer.BeginPairing on open and Close()s it when it goes away, so
/// /pair exists only while a human is looking at the PIN.
/// </summary>
public sealed class PairingSession
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly byte[] _pinBytes;
    private int _failures;
    private bool _consumed;
    private bool _closed;

    /// <summary>The 6 digits. Shown on screen, compared in constant time,
    /// never logged.</summary>
    public string Pin { get; }

    public DateTimeOffset ExpiresAt { get; }

    public int FailuresSoFar { get { lock (_gate) return _failures; } }

    /// <summary>Raised on a worker thread when a peer redeems the PIN; carries
    /// the redeeming machine's name.</summary>
    public event Action<string>? Succeeded;

    /// <summary>Raised on a worker thread on every wrong guess, with the
    /// running failure count.</summary>
    public event Action<int>? WrongGuess;

    /// <summary>Raised on a worker thread after the 5th wrong guess.</summary>
    public event Action? LockedOut;

    public PairingSession()
    {
        // GetInt32 is uniform over the range — no modulo bias.
        Pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _pinBytes = Encoding.ASCII.GetBytes(Pin);
        ExpiresAt = DateTimeOffset.UtcNow + Lifetime;
    }

    public bool Active { get { lock (_gate) return ActiveLocked; } }

    private bool ActiveLocked =>
        !_closed && !_consumed && _failures < MaxAttempts &&
        DateTimeOffset.UtcNow < ExpiresAt;

    /// <summary>The pairing window is gone — /pair goes dark immediately.</summary>
    public void Close() { lock (_gate) _closed = true; }

    public PairAttemptResult TryRedeem(string pin, string peerMachine)
    {
        PairAttemptResult result;
        var failures = 0;
        var lockedOutNow = false;

        lock (_gate)
        {
            if (!ActiveLocked) return PairAttemptResult.NotActive;

            var supplied = Encoding.ASCII.GetBytes(pin);
            var match = supplied.Length == _pinBytes.Length &&
                        CryptographicOperations.FixedTimeEquals(supplied, _pinBytes);
            if (match)
            {
                _consumed = true; // single-use: success invalidates the PIN forever
                result = PairAttemptResult.Accepted;
            }
            else
            {
                failures = ++_failures;
                lockedOutNow = _failures >= MaxAttempts;
                result = PairAttemptResult.WrongPin;
            }
        }

        // Events fire outside the lock — subscribers marshal to the UI thread.
        if (result == PairAttemptResult.Accepted)
        {
            Succeeded?.Invoke(peerMachine);
        }
        else
        {
            WrongGuess?.Invoke(failures);
            if (lockedOutNow) LockedOut?.Invoke();
        }
        return result;
    }
}
