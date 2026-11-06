using CrabDesk.Core;

namespace CrabDesk.Native;

public interface IDisplayControl
{
    ControlDisplaySnapshot Read();
    ControlOperationResult SetMode(ControlDisplayMode mode);
    ControlOperationResult OpenCast();
    ControlOperationResult OpenSettings();
}
