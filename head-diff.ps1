Set-Location "T:\source\repos\CheapAvaloniaBlazor"
$lines = & git show "HEAD:Windows/BlazorHostWindow.cs" 2>&1
Write-Host "Total lines: $($lines.Count)"
$lines | Select-Object -First 120 | ForEach-Object { $_ }
