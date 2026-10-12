# Ongi 배포 패키지를 만듭니다: 빌드 → 셀프테스트 → dist\Ongi-<버전>.zip(+ .sha256)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $root 'build.ps1')
$exe = Join-Path $root 'bin\Ongi.exe'

# 셀프테스트가 하나라도 실패하면 패키지를 만들지 않습니다(종료 코드 = 실패 수).
$test = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru
if ($test.ExitCode -ne 0) { throw "셀프테스트 실패 $($test.ExitCode)건 — %LOCALAPPDATA%\Ongi\ongi.log를 확인하세요." }

$version = (Get-Item $exe).VersionInfo.ProductVersion
$name = "Ongi-$version"
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $exe, (Join-Path $root 'README.md') $stage

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding Ascii

Write-Host "패키지 완료: $zip"
Write-Host "SHA-256: $hash"
Write-Host "릴리스(태그 v$version)에는 $name.zip과 $name.zip.sha256 두 파일을 모두 첨부하세요. 앱 안 업데이트는 해시 파일이 있어야 받습니다."
