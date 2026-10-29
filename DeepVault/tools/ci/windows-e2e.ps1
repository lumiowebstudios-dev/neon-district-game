<#
  DeepVault end-to-end test on real Windows (used by CI; can be run manually as administrator).

  Creates a disposable 128 MB VHD, formats it NTFS with Windows, writes sample files (one deliberately
  fragmented), deletes them with Windows, then uses deepvault-cli to scan the attached PHYSICAL disk
  read-only (\\.\PhysicalDriveN), restore the recoverable files to a separate folder and compare
  SHA-256 hashes with the originals. It never touches any disk other than the VHD it creates.

  Usage:  powershell -ExecutionPolicy Bypass -File windows-e2e.ps1 -Cli <path\deepvault-cli.exe> -Samples <dir> -Work <dir>
#>
param(
    [Parameter(Mandatory)] [string] $Cli,
    [Parameter(Mandatory)] [string] $Samples,
    [Parameter(Mandatory)] [string] $Work
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Work | Out-Null
$vhd = Join-Path $Work 'dv-e2e.vhd'
$letter = 'V'
$result = [ordered]@{ ok = $false; steps = @(); files = @() }
function Step($m) { Write-Host "== $m"; $result.steps += $m }

Step 'Create and attach disposable VHD'
@"
create vdisk file="$vhd" maximum=128 type=fixed
select vdisk file="$vhd"
attach vdisk
convert mbr
create partition primary
format fs=ntfs quick label=DVE2E
assign letter=$letter
"@ | Set-Content (Join-Path $Work 'dp-create.txt') -Encoding ascii
diskpart /s (Join-Path $Work 'dp-create.txt') | Out-Host
Start-Sleep 3

try {
    Step 'Write sample files with Windows NTFS'
    $targets = @(
        @{ dir = 'Photos\2020'; name = 'holiday_beach.jpg' }, @{ dir = 'Photos\2020'; name = 'diagram.png' },
        @{ dir = 'Documents'; name = 'invoice_2019.pdf' }, @{ dir = 'Documents'; name = 'thesis.docx' },
        @{ dir = 'Documents'; name = 'old_report.doc' }, @{ dir = 'Documents'; name = 'notes.txt' },
        @{ dir = 'Music'; name = 'song.mp3' }, @{ dir = 'Archive'; name = 'backup.zip' })
    foreach ($t in $targets) {
        New-Item -ItemType Directory -Force "${letter}:\$($t.dir)" | Out-Null
        Copy-Item (Join-Path $Samples $t.name) "${letter}:\$($t.dir)\$($t.name)"
    }
    # Force fragmentation of one file: fill free space, punch holes, then write the large JPEG.
    New-Item -ItemType Directory -Force "${letter}:\Filler" | Out-Null
    $i = 0
    try { while ($true) { [IO.File]::WriteAllBytes("${letter}:\Filler\f$i.bin", [byte[]](,[byte]($i % 251) * 65536)); $i++ } } catch { }
    for ($k = 0; $k -lt $i; $k += 2) { Remove-Item "${letter}:\Filler\f$k.bin" }
    Copy-Item (Join-Path $Samples 'family_large.jpg') "${letter}:\Photos\family_large.jpg"
    $targets += @{ dir = 'Photos'; name = 'family_large.jpg' }
    Get-ChildItem "${letter}:\Filler" | Remove-Item

    Step 'Delete the files with Windows'
    foreach ($t in $targets) { Remove-Item "${letter}:\$($t.dir)\$($t.name)" }
    Remove-Item "${letter}:\Photos\2020"
    Write-VolumeCache -DriveLetter $letter
    $disk = (Get-Partition -DriveLetter $letter).DiskNumber
    Step "VHD is physical disk $disk"

    Step 'deepvault-cli disks'
    & $Cli disks | Out-Host

    Step 'Deep scan of the physical disk (read-only)'
    $proj = Join-Path $Work 'e2e.dvproj'
    & $Cli scan "disk:$disk" --project $proj | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "scan failed ($LASTEXITCODE)" }
    & $Cli summary $proj | Out-Host
    & $Cli list $proj --view recoverable --search jpg | Out-Host

    Step 'Restore onto the source disk must be refused'
    & $Cli restore $proj --source "disk:$disk" --dest "${letter}:\restore-here" --view recoverable | Out-Host
    if ($LASTEXITCODE -ne 3) { throw "restore to the source disk was not blocked (exit $LASTEXITCODE)" }

    Step 'Restore recoverable files to a separate folder'
    $dest = Join-Path $Work 'restored'
    & $Cli restore $proj --source "disk:$disk" --dest $dest --view recoverable | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "restore reported failures ($LASTEXITCODE)" }

    Step 'Compare SHA-256 with the originals'
    $allOk = $true
    foreach ($t in $targets) {
        $orig = (Get-FileHash (Join-Path $Samples $t.name) -Algorithm SHA256).Hash
        $found = Get-ChildItem $dest -Recurse -File | Where-Object { $_.Name -eq $t.name }
        $match = $found | Where-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash -eq $orig } | Select-Object -First 1
        $ok = $null -ne $match
        $allOk = $allOk -and $ok
        $result.files += [ordered]@{ name = $t.name; originalFolder = "\$($t.dir)"; restored = if ($match) { $match.FullName } else { $null }; hashMatch = $ok }
        Write-Host ("{0,-22} {1}" -f $t.name, $(if ($ok) { 'HASH MATCH' } else { 'NOT RECOVERED EXACTLY' }))
    }
    $result.ok = $allOk
}
finally {
    @"
select vdisk file="$vhd"
detach vdisk
"@ | Set-Content (Join-Path $Work 'dp-detach.txt') -Encoding ascii
    diskpart /s (Join-Path $Work 'dp-detach.txt') | Out-Host
    $result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Work 'windows-e2e-result.json')
}
if (-not $result.ok) { throw 'Windows end-to-end recovery test failed' }
Write-Host 'Windows end-to-end recovery test PASSED'
