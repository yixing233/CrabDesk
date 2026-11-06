namespace CrabDesk.Core;

public enum ControlDisplayMode { Internal, Clone, Extend, External }

public sealed record ControlDisplaySnapshot(ControlDisplayMode? Mode, int DisplayCount);
