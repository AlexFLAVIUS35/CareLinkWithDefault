$ErrorActionPreference = 'Stop'

$servicePath = 'src/CareLink/Helpers/LoginHelpers/CareLinkService.vb'
$service = Get-Content -Raw -Encoding UTF8 $servicePath

$service = $service.Replace(
    'Private Shared ReadOnly s_http As New HttpClient With {.Timeout = TimeSpan.FromSeconds(120)}',
    'Private Shared ReadOnly s_http As New HttpClient With {.Timeout = TimeSpan.FromSeconds(15)}')
$service = $service.Replace(
    'Private Shared ReadOnly s_http As New HttpClient With {.Timeout = TimeSpan.FromSeconds(30)}',
    'Private Shared ReadOnly s_http As New HttpClient With {.Timeout = TimeSpan.FromSeconds(15)}')
$service = $service.Replace(
    'If String.IsNullOrWhiteSpace(value:=magIdentifier) Then',
    'If Not String.IsNullOrWhiteSpace(value:=magIdentifier) Then')

# VB requires explicit continuation for this chained JsonElement access in the current project/toolchain.
$service = $service.Replace(
    'providersDoc.RootElement.GetProperty(propertyName:="providers")(index:=0).`r`n                                                 GetProperty(propertyName:="provider").`r`n                                                 GetProperty(propertyName:="auth_url").GetString()',
    'providersDoc.RootElement.GetProperty(propertyName:="providers")(index:=0). _`r`n                                                 GetProperty(propertyName:="provider"). _`r`n                                                 GetProperty(propertyName:="auth_url").GetString()')
$service = $service.Replace(
    'providersDoc.RootElement.GetProperty(propertyName:="providers")(index:=0).' + [Environment]::NewLine + '                                                 GetProperty(propertyName:="provider").' + [Environment]::NewLine + '                                                 GetProperty(propertyName:="auth_url").GetString()',
    'providersDoc.RootElement.GetProperty(propertyName:="providers")(index:=0). _' + [Environment]::NewLine + '                                                 GetProperty(propertyName:="provider"). _' + [Environment]::NewLine + '                                                 GetProperty(propertyName:="auth_url").GetString()')

$stopPattern = "Catch ex As Exception\r?\n\s*Stop"
$stopReplacement = 'Catch ex As Exception' + [Environment]::NewLine + '                            Throw New ApplicationException(message:=$"Failed to parse CareLink endpoint configuration: {ex.Message}", innerException:=ex)'
$service = [regex]::Replace($service, $stopPattern, $stopReplacement, 1)

$authPattern = '(?s)\s*'' Ensure the UI dialog and WebView2 initialization run on the UI thread\.\s*redirectResult = Await InvokeOnUiThreadAsync\(.*?End Function\)'
$authReplacement = @'
        ' Use the Windows default browser directly.
        Do
            Try
                redirectResult = Await DefaultBrowserOAuth.CaptureRedirectAsync(
                    startUrl:=fullUrl,
                    redirectUri:=redirectUri,
                    cancellationToken:=Threading.CancellationToken.None)
                Exit Do
            Catch ex As Exception
                Dim retryResult As DialogResult = MessageBox.Show(
                    ex.Message & Environment.NewLine & Environment.NewLine &
                    "Retry the CareLink login?",
                    "CareLink Login",
                    MessageBoxButtons.RetryCancel,
                    MessageBoxIcon.Error)
                If retryResult <> DialogResult.Retry Then
                    Throw New Exception("Login was cancelled.", ex)
                End If
            End Try
        Loop
'@
if ($service -notmatch $authPattern) { throw 'Auth0 browser block could not be located.' }
$service = [regex]::Replace($service, $authPattern, $authReplacement, 1)

$nonAuthPattern = '(?s)\s*Dim redirectResult As RedirectResult\s*Do\s*Using frm As New OAuthBrowserForm\(startUrl:=captchaUrl,.*?End Using\s*Loop'
$nonAuthReplacement = @'
                    Dim redirectResult As RedirectResult
                    Do
                        Try
                            redirectResult = Await DefaultBrowserOAuth.CaptureRedirectAsync(
                                startUrl:=captchaUrl,
                                redirectUri:=redirectUri,
                                cancellationToken:=Threading.CancellationToken.None)
                            Exit Do
                        Catch ex As Exception
                            Dim retryResult As DialogResult = MessageBox.Show(
                                ex.Message & Environment.NewLine & Environment.NewLine &
                                "Retry the CareLink login?",
                                "CareLink Login",
                                MessageBoxButtons.RetryCancel,
                                MessageBoxIcon.Error)
                            If retryResult <> DialogResult.Retry Then
                                Throw New Exception("Login was cancelled.", ex)
                            End If
                        End Try
                    Loop
'@
if ($service -notmatch $nonAuthPattern) { throw 'Non-Auth0 browser block could not be located.' }
$service = [regex]::Replace($service, $nonAuthPattern, $nonAuthReplacement, 1)

if ($service -match 'OAuthBrowserForm') { throw 'CareLinkService still references OAuthBrowserForm.' }
if ($service -notmatch 'DefaultBrowserOAuth\.CaptureRedirectAsync') { throw 'Direct default-browser OAuth was not applied.' }
if ($service -match 'providersDoc\.RootElement\.GetProperty\(propertyName:="providers"\)\(index:=0\)\.\r?\n') { throw 'Provider JSON chain still lacks explicit line continuation.' }
Set-Content -Path $servicePath -Value $service -Encoding UTF8

$loginPath = 'src/CareLink/Dialogs/LoginDialog.vb'
$login = Get-Content -Raw -Encoding UTF8 $loginPath
if ($login -notmatch 'Imports System.Threading.Tasks') {
    $login = $login.Replace(
        'Imports System.Net.Http' + [Environment]::NewLine,
        'Imports System.Net.Http' + [Environment]::NewLine + 'Imports System.Threading.Tasks' + [Environment]::NewLine)
}
$login = $login.Replace(
    'Dim discoveryTask As Task(Of DiscoveryRecord) = GetDiscoveryDataAsync()',
    'Dim discoveryTask As Task(Of DiscoveryRecord) = Task.Run(Function() GetDiscoveryDataAsync())')
if ($login -notmatch 'Task\.Run\(Function\(\) GetDiscoveryDataAsync\(\)\)') {
    $oldDiscovery = 'Dim discoveryResult As DiscoveryRecord = Await GetDiscoveryDataAsync()'
    $newDiscovery = @'
            Me.LoginStatus.Text = "Loading CareLink discovery configuration..."
            Me.Refresh()
            Dim discoveryTask As Task(Of DiscoveryRecord) = Task.Run(Function() GetDiscoveryDataAsync())
            Dim discoveryCompleted As Task = Await Task.WhenAny(discoveryTask, Task.Delay(TimeSpan.FromSeconds(20)))
            If Not Object.ReferenceEquals(discoveryCompleted, discoveryTask) Then
                Throw New TimeoutException("CareLink discovery timed out after 20 seconds. Check your internet connection and try again.")
            End If
            Dim discoveryResult As DiscoveryRecord = Await discoveryTask
'@
    if ($login -notmatch [regex]::Escape($oldDiscovery)) { throw 'Could not locate GetDiscoveryDataAsync call.' }
    $login = $login.Replace($oldDiscovery, $newDiscovery.TrimEnd("`r", "`n"))
}
if ($login -notmatch 'Task\.WhenAny\(discoveryTask') { throw 'Login discovery timeout fix could not be verified.' }
$login = $login.Replace(
    'Me.Ok_Button.Enabled = False' + [Environment]::NewLine + '                Application.DoEvents()',
    'Me.LoginStatus.Text = "Opening CareLink login..."' + [Environment]::NewLine + '                Me.Ok_Button.Enabled = False' + [Environment]::NewLine + '                Application.DoEvents()')
$login = $login.Replace('Me.LoginStatus.Text = "Checking token file..."', 'Me.LoginStatus.Text = "Checking CareLink connection..."')

if ($login -notmatch 'ReportLoginStatus\(Me\.LoginStatus, hasErrors:=True') {
    $catchPattern = '(?s)Catch ex As Exception\r?\n\s*Stop\r?\n\s*Finally\r?\n\s*Me\.Ok_Button\.Enabled = True\r?\n\s*Me\.Cancel_Button\.Enabled = True'
    $catchReplacement = @'
Catch ex As Exception
            Dim errorMessage As String = If(String.IsNullOrWhiteSpace(ex.Message), "The login operation failed.", ex.Message)
            ReportLoginStatus(Me.LoginStatus, hasErrors:=True, lastErrorMsg:=errorMessage, lastHttpStatusCode:=CInt(HttpStatusCode.InternalServerError))
            MessageBox.Show(Me, errorMessage, "CareLink Login", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            Me.Ok_Button.Enabled = True
            Me.Cancel_Button.Enabled = True
'@
    $patched = [regex]::Replace($login, $catchPattern, $catchReplacement.TrimEnd("`r", "`n"), 1)
    if ($patched -eq $login) { throw 'LoginDialog exception handler could not be located.' }
    $login = $patched
}
Set-Content -Path $loginPath -Value $login -Encoding UTF8

Write-Host 'Login source hardening applied and verified.'
