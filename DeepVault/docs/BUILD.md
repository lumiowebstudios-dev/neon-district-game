# Build and packaging

## Requirements
* .NET 10 SDK (10.0.100 or later). Nothing else; NuGet restores the packages listed in `THIRD_PARTY.md`.
* Windows 10/11 x64 to **run** the application. The whole solution also **builds** on Linux/macOS
  (`EnableWindowsTargeting`), and Core/CLI/tests run there.

## Build and test
```
dotnet build DeepVault.sln -c Release
dotnet test tests/DeepVault.Tests -c Release
```

## Package (self-contained, no .NET installation needed on the target PC)
Windows: `powershell -ExecutionPolicy Bypass -File build.ps1`
Linux/macOS: `./build.sh`

Both produce `artifacts/DeepVault-1.0.0-win-x64/` and `artifacts/DeepVault-1.0.0-win-x64.zip`
containing `DeepVault.exe`, `deepvault-cli.exe`, the runtime, docs, licences and the test images.
Manual equivalent:
```
dotnet publish src/DeepVault.App -c Release -r win-x64 --self-contained true -o out
dotnet publish src/DeepVault.Cli -c Release -r win-x64 --self-contained true -o out
```

## Continuous integration
`.github/workflows/deepvault-windows.yml` (repository root) runs on `windows-latest`:
1. all automated tests;
2. self-contained publish;
3. `tools/ci/windows-e2e.ps1`: creates a VHD, formats it NTFS, writes and deletes files with
   Windows, recovers them with `deepvault-cli` from `\\.\PhysicalDriveN`, compares SHA-256;
4. the GUI self-test (`DeepVault.exe --selftest <image> <outdir>`), which drives every page and
   fails on errors; screenshots are uploaded as artifacts.

## Regenerating the test disk images (Linux, root, FUSE)
```
sudo apt install ntfs-3g dosfstools mtools exfatprogs exfat-fuse gdisk attr ffmpeg p7zip-full python3-pil
python3 tools/fixtures/make_samples.py /tmp/dv-samples
sudo python3 tools/fixtures/make_images.py /tmp/dv-samples tests/fixtures/images
```
The script only creates files in the output folder (loop devices are used for exFAT images only).

## Code signing (not done)
The build is unsigned; SmartScreen will warn. For distribution, sign `DeepVault.exe` and
`deepvault-cli.exe` with an Authenticode certificate (`signtool sign /fd SHA256 /tr <timestamp-url> ...`).
