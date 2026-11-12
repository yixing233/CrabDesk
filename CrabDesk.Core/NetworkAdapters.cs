namespace CrabDesk.Core;

public sealed record ControlNetworkAdapter(Guid Id, string Name, string Description, bool? Enabled, bool Connected, bool Wireless);
