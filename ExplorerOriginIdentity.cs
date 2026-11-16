namespace QuickPreview;

internal enum OriginLifetimeState { Unknown, Alive, Closed }

internal sealed record ExplorerOriginIdentity(long BindingId, IntPtr FrameWindow, IntPtr TabWindow,
    uint ProcessId, uint FrameThreadId, uint TabThreadId, uint CapturedAt, string FrameClass, string TabClass);

internal sealed record ExplorerSpaceGesture(bool PreviewWasVisible, IntPtr FrameWindow, IntPtr TabWindow);
