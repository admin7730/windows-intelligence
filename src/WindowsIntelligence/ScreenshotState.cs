namespace WindowsIntelligence;

internal enum ScreenshotPhase
{
    Idle, Triggered, Capturing, Captured, Preview, WaitingForSpeech, Listening, Transcribing, ReadyToSend,
    Uploading, AiProcessing, Completed, Failed, Cancelled
}

internal sealed class ScreenshotState
{
    public Guid RequestId { get; } = Guid.NewGuid();
    public ScreenshotPhase Phase { get; private set; }
    private bool attempted;

    public bool Transition(ScreenshotPhase next)
    {
        if (Phase is ScreenshotPhase.Completed or ScreenshotPhase.Cancelled || Phase == next) return false;
        if (next is ScreenshotPhase.Failed or ScreenshotPhase.Cancelled) { Phase = next; return true; }
        bool allowed = (Phase, next) switch
        {
            (ScreenshotPhase.Idle, ScreenshotPhase.Triggered) => true,
            (ScreenshotPhase.Triggered, ScreenshotPhase.Capturing) => true,
            (ScreenshotPhase.Capturing, ScreenshotPhase.Captured) => true,
            (ScreenshotPhase.Captured, ScreenshotPhase.Preview or ScreenshotPhase.WaitingForSpeech) => true,
            (ScreenshotPhase.Preview, ScreenshotPhase.WaitingForSpeech or ScreenshotPhase.ReadyToSend) => true,
            (ScreenshotPhase.WaitingForSpeech, ScreenshotPhase.Listening or ScreenshotPhase.ReadyToSend) => true,
            (ScreenshotPhase.Listening, ScreenshotPhase.Transcribing or ScreenshotPhase.ReadyToSend) => true,
            (ScreenshotPhase.Transcribing, ScreenshotPhase.ReadyToSend) => true,
            (ScreenshotPhase.ReadyToSend, ScreenshotPhase.Uploading) when !attempted => true,
            (ScreenshotPhase.Uploading, ScreenshotPhase.AiProcessing or ScreenshotPhase.Completed) => true,
            (ScreenshotPhase.AiProcessing, ScreenshotPhase.Completed) => true,
            _ => false
        };
        if (!allowed) return false;
        if (next == ScreenshotPhase.Uploading) attempted = true;
        Phase = next;
        return true;
    }

    public bool Retry()
    {
        if (Phase != ScreenshotPhase.Failed) return false;
        attempted = false;
        Phase = ScreenshotPhase.ReadyToSend;
        return true;
    }
}
