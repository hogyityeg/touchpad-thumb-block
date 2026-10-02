# Builds TouchpadZone.exe with the C# compiler bundled with Windows (.NET Framework 4.x).
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /out:"$PSScriptRoot\TouchpadZone.exe" "$PSScriptRoot\TouchpadZone.cs"
