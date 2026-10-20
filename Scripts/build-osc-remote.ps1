$env:CEF_ROOT = 'C:\My Project\cef-sdk\cef73'
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$proj = 'C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA OSC\NVIDIA OSC.vcxproj'
& $msbuild $proj /p:Configuration=Release /p:Platform=x64 /m /v:m /nologo
exit $LASTEXITCODE
