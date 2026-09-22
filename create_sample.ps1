Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 500, 250
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
$fontTitle = New-Object System.Drawing.Font('Arial', 18, [System.Drawing.FontStyle]::Bold)
$fontBody = New-Object System.Drawing.Font('Arial', 14)
$brush = [System.Drawing.Brushes]::Black

$g.DrawString("BOLETA DE VENTA ELECTRONICA", $fontTitle, $brush, 20.0, 20.0)
$g.DrawString("RUC: 20123456789", $fontBody, $brush, 20.0, 60.0)
$g.DrawString("TRANSACCION: TRX-985412", $fontBody, $brush, 20.0, 95.0)
$g.DrawString("FECHA: 19/09/2026", $fontBody, $brush, 20.0, 130.0)
$g.DrawString("TOTAL PAGADO: 150.00 USD", $fontTitle, $brush, 20.0, 175.0)

$g.Flush()
$bmp.Save("D:\wamp\www\PITBoletaTransacciones\boletas\in\boleta_test.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Host "Imagen boleta_test.png creada correctamente"
