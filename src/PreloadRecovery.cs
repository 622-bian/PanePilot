using System;

// Retry only after uncertain windows are gone; never close an unverified window to recover.
internal sealed class PreloadRecovery
{
    internal int Failures { get; private set; }
    internal DateTime NextRetry { get; private set; }
    internal bool Exhausted { get { return Failures>=3; } }
    internal void Fail(DateTime now)
    {
        Failures++;
        NextRetry=now.AddSeconds(Failures==1 ? 5 : 15);
    }
    internal bool CanAttempt(DateTime now,bool manuallyPaused,bool unresolved)
    {
        return !manuallyPaused && !unresolved && !Exhausted && (Failures==0 || now>=NextRetry);
    }
    internal void Reset() { Failures=0; NextRetry=DateTime.MinValue; }
}
