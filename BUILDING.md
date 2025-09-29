# Building GTFO_VR

## Requirements
- .NET 6 SDK (6.x) available on PATH so `dotnet --version` reports 6.*
- GTFO installed locally with BepInEx IL2CPP already extracted into the game folder

## Build Steps
1. Identify your GTFO install directory, for example `C:\Program Files (x86)\Steam\steamapps\common\GTFO`.
2. From `GTFO_VR_Plugin`, run the build script:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build.ps1 -GTFOPath "<path-to-GTFO>" -Configuration Release
   ```

The script sets `GTFO_PATH` for MSBuild so the project can resolve the BepInEx and Il2Cpp assemblies and then runs `dotnet build` on `GTFO_VR\GTFO_VR.csproj`.

## Options
- Omit `-Configuration` to use the default `Release` (Debug is also valid).
- Set the `GTFO_PATH` environment variable ahead of time and skip `-GTFOPath`.
- Pass `-SkipCopy` if you only want the built DLL under `GTFO_VR\bin` and do not want it copied into `GTFO_PATH\BepInEx\plugins`.
- Pass `-NoRestore` when you have already restored dependencies and want to skip the restore step.

## Output
- `GTFO_VR\bin\<Configuration>\GTFO_VR.dll` (standard build output)
- Unless `-SkipCopy` is used, the DLL is also copied into `GTFO_PATH\BepInEx\plugins` for immediate use.
