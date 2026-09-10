# Acrylic smoke test

Requires Windows 11 and .NET 8. Runs the actual runtime host and box renderer with in-memory boxes and an isolated temporary data directory. It does not change saved user settings or take over the real Explorer icon view.

From the repository root:

```powershell
# Animated red/blue stripes behind two standalone box renderers (7 seconds).
dotnet run --project tools/AcrylicSmokeTest/AcrylicSmokeTest.csproj -c Release

# Exercise DesktopSurfaceManager creation, separated icon/backdrop/foreground
# parents, hide/show and refresh three times. Uses a synthetic Explorer parent
# and icon view owned by this process.
dotnet run --project tools/AcrylicSmokeTest/AcrylicSmokeTest.csproj -c Release -- --manager

# Real desktop background, full-monitor host and two Show Desktop transitions.
# Temporarily minimizes application windows and restores them afterwards.
dotnet run --project tools/AcrylicSmokeTest/AcrylicSmokeTest.csproj -c Release -- --wallpaper --full-monitor --desktop-cycle

# Validate physical pointer hover expansion and exit collapse (11 seconds).
# Restores the cursor and minimized application windows after completion.
dotnet run --project tools/AcrylicSmokeTest/AcrylicSmokeTest.csproj -c Release -- --wallpaper --full-monitor --interaction
```

The program prints its evidence directory under `%TEMP%/CrabDesk-AcrylicSmokeTest`. It uses the production standalone foreground path and defers its first bitmap until desktop attachment. Its Windows compatibility manifest enables layered child windows. Inspect screenshots for live background blur, sharp titles, rounded edges, and correct expanded/collapsed geometry. The pointer classification should be true when the test boxes are unobscured. `--interaction` fails if the physical pointer cannot expand box B, if leaving does not collapse it, or if its hover timer remains active after collapse.

The visual probe uses fixed screen coordinates near `(610,275)` and requires enough visible screen space. It does not provide automated image assertions or mixed-DPI hardware coverage. `--desktop-cycle` is intended for use with `--wallpaper`.
