$projectDir = $PSScriptRoot
$outputDir = Join-Path $projectDir "bin\Debug\net10.0-windows"

Add-Type -Path (Join-Path $outputDir "Sdcb.SimdPaddleOCR.Models.ChineseV6Medium.dll")
Add-Type -Path (Join-Path $outputDir "Sdcb.SimdPaddleOCR.ModelProvider.dll")

$dictStream = [Sdcb.SimdPaddleOCR.Models.ChineseV6Medium.ChineseV6MediumModel]::Dictionary.OpenRead()
$reader = New-Object System.IO.StreamReader($dictStream, [System.Text.Encoding]::UTF8)
$content = $reader.ReadToEnd()
$reader.Dispose()
$dictStream.Dispose()

$chars = @(
    [char]0x00F1, [char]0x00D1, [char]0x00E1, [char]0x00E9, [char]0x00ED,
    [char]0x00F3, [char]0x00FA, [char]0x00C1, [char]0x00C9, [char]0x00CD,
    [char]0x00D3, [char]0x00DA, [char]0x00BF, [char]0x00A1, [char]0x00FC,
    [char]0x00DC, '$', '%', '/', '#', '@'
)
foreach ($c in $chars) {
    $found = $content.Contains($c)
    Write-Host "Character '$c' : $found"
}
Write-Host "Total characters in dictionary: $($content.Length)"
