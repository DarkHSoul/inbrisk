Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

$form = New-Object System.Windows.Forms.Form
$form.Text = "Inbrisk Ghost Canvas - Cizim ve Karalama"
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(450, 180)
$form.Size = New-Object System.Drawing.Size(1000, 700)
$form.BackColor = [System.Drawing.Color]::FromArgb(30, 30, 35)

# Current drawing state
$script:drawing = $false
$script:lastPoint = [System.Drawing.Point]::Empty
$script:currentColor = [System.Drawing.Color]::Crimson
$script:penWidth = 5

# Canvas PictureBox
$canvas = New-Object System.Windows.Forms.PictureBox
$canvas.Location = New-Object System.Drawing.Point(20, 60)
$canvas.Size = New-Object System.Drawing.Size(945, 580)
$canvas.BackColor = [System.Drawing.Color]::White

$bmp = New-Object System.Drawing.Bitmap(945, 580)
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$gfx.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$gfx.Clear([System.Drawing.Color]::White)
$canvas.Image = $bmp

$canvas.Add_MouseDown({
    param($s, $e)
    if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
        $script:drawing = $true
        $script:lastPoint = $e.Location
    }
})

$canvas.Add_MouseMove({
    param($s, $e)
    if ($script:drawing -and ($script:lastPoint -ne [System.Drawing.Point]::Empty)) {
        $pen = New-Object System.Drawing.Pen($script:currentColor, $script:penWidth)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $gfx.DrawLine($pen, $script:lastPoint, $e.Location)
        $script:lastPoint = $e.Location
        $canvas.Invalidate()
        $pen.Dispose()
    }
})

$canvas.Add_MouseUp({
    param($s, $e)
    $script:drawing = $false
    $script:lastPoint = [System.Drawing.Point]::Empty
})

# Toolbar Panel
$toolbar = New-Object System.Windows.Forms.Panel
$toolbar.Location = New-Object System.Drawing.Point(20, 10)
$toolbar.Size = New-Object System.Drawing.Size(945, 40)
$toolbar.BackColor = [System.Drawing.Color]::FromArgb(40, 40, 48)

$colors = @(
    @{ Name = "Kirmizi"; Color = [System.Drawing.Color]::Crimson },
    @{ Name = "Mavi"; Color = [System.Drawing.Color]::DodgerBlue },
    @{ Name = "Yesil"; Color = [System.Drawing.Color]::MediumSeaGreen },
    @{ Name = "Sari"; Color = [System.Drawing.Color]::Gold },
    @{ Name = "Mor"; Color = [System.Drawing.Color]::MediumOrchid },
    @{ Name = "Siyah"; Color = [System.Drawing.Color]::Black }
)

[int]$xOffset = 10
foreach ($c in $colors) {
    $btn = New-Object System.Windows.Forms.Button
    $btn.Text = $c.Name
    $btn.Location = New-Object System.Drawing.Point($xOffset, 6)
    $btn.Size = New-Object System.Drawing.Size(75, 28)
    $btn.BackColor = $c.Color
    $btn.ForeColor = [System.Drawing.Color]::White
    $btn.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $btn.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
    $col = $c.Color
    $btn.Add_Click({ $script:currentColor = $col }.GetNewClosure())
    [void]$toolbar.Controls.Add($btn)
    $xOffset = $xOffset + 85
}

$btnClear = New-Object System.Windows.Forms.Button
$btnClear.Text = "Temizle"
$btnClear.Location = New-Object System.Drawing.Point(($xOffset + 20), 6)
$btnClear.Size = New-Object System.Drawing.Size(85, 28)
$btnClear.BackColor = [System.Drawing.Color]::DimGray
$btnClear.ForeColor = [System.Drawing.Color]::White
$btnClear.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$btnClear.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
$btnClear.Add_Click({
    $gfx.Clear([System.Drawing.Color]::White)
    $canvas.Invalidate()
})
[void]$toolbar.Controls.Add($btnClear)

[void]$form.Controls.Add($toolbar)
[void]$form.Controls.Add($canvas)

[System.Windows.Forms.Application]::Run($form)
