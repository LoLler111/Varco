# Blocca lo spegnimento di Windows finché questa finestra è aperta. Serve a provare che la schermata
# "Chiusura app in corso" si comanda da un altro PC con Varco (il Blocco note di Windows 11 salva da solo e non la fa comparire).
#
#   powershell -ExecutionPolicy Bypass -File prova-arresto.ps1
#
# Poi spegni o riavvia questo PC: compare la schermata con "Arresta comunque" e "Annulla". Premili dall'altro PC.
# Con "Annulla" la finestra resta aperta e si può riprovare; per chiuderla basta la X.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace VarcoProva -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);
'@

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Varco - prova dello spegnimento'
$form.ClientSize = New-Object System.Drawing.Size(440, 140)
$form.StartPosition = 'CenterScreen'
$label = New-Object System.Windows.Forms.Label
$label.Text = "Finché questa finestra è aperta, Windows non si spegne da solo.`n`n" +
              "Spegni il PC e premi 'Arresta comunque' o 'Annulla' dall'altro PC."
$label.Dock = 'Fill'
$label.TextAlign = 'MiddleCenter'
$form.Controls.Add($label)

# Il motivo compare nella schermata "Chiusura app in corso" sotto il nome della finestra.
$form.Add_Shown({ [void][VarcoProva.Native]::ShutdownBlockReasonCreate($form.Handle, "Prova di Varco: premi 'Arresta comunque' dall'altro PC") })
# Allo spegnimento Windows chiede alla finestra se può chiuderla: rispondere di no è ciò che fa comparire la schermata.
$form.Add_FormClosing({
    param($sender, $e)
    if ($e.CloseReason -eq [System.Windows.Forms.CloseReason]::WindowsShutDown) { $e.Cancel = $true }
})
[System.Windows.Forms.Application]::Run($form)
