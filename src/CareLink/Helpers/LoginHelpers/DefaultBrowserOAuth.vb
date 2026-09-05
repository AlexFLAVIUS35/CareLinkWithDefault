Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Text
Imports System.Threading
Imports Microsoft.Win32

Friend Module DefaultBrowserOAuth

    Private Const CallbackTimeoutSeconds As Integer = 300
    Private Const ProtocolScheme As String = "com.medtronic.carepartner"
    Private Const ProtocolHandlerName As String = "CareLink Windows OAuth Callback"
    Private ReadOnly s_callbackLock As New Object()
    Private s_callbackSource As TaskCompletionSource(Of String)
    Private s_expectedState As String

    Friend Async Function CaptureRedirectAsync(startUrl As String,
                                               redirectUri As String,
                                               cancellationToken As CancellationToken) As Task(Of RedirectResult)
        If String.IsNullOrWhiteSpace(startUrl) Then
            Throw New ArgumentException("The CareLink login URL is empty.", NameOf(startUrl))
        End If

        Dim redirect As New Uri(redirectUri, UriKind.Absolute)

        If redirect.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) AndAlso redirect.IsLoopback Then
            Return Await CaptureLoopbackRedirectAsync(startUrl, redirect, cancellationToken)
        End If

        If redirect.Scheme.Equals(ProtocolScheme, StringComparison.OrdinalIgnoreCase) AndAlso
           redirect.AbsolutePath.Equals("/sso", StringComparison.OrdinalIgnoreCase) Then
            Return Await CaptureCustomSchemeRedirectAsync(startUrl, redirect, cancellationToken)
        End If

        Throw New NotSupportedException(
            $"The CareLink OAuth redirect URI is not supported on Windows: {redirectUri}")
    End Function

    Private Async Function CaptureCustomSchemeRedirectAsync(startUrl As String,
                                                            redirect As Uri,
                                                            cancellationToken As CancellationToken) As Task(Of RedirectResult)
        Dim expectedState As String = GetQueryParameter(startUrl, "state")
        If String.IsNullOrWhiteSpace(expectedState) Then
            expectedState = Guid.NewGuid().ToString("N")
            startUrl = AddQueryParameter(startUrl, "state", expectedState)
        End If

        RegisterWindowsProtocolHandler()

        Dim callbackTask As Task(Of String)
        SyncLock s_callbackLock
            s_expectedState = expectedState
            s_callbackSource = New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)
            callbackTask = s_callbackSource.Task
        End SyncLock

        Try
            OpenDefaultBrowser(startUrl)

            Dim timeoutTask As Task = Task.Delay(TimeSpan.FromSeconds(CallbackTimeoutSeconds), cancellationToken)
            Dim completedTask As Task = Await Task.WhenAny(callbackTask, timeoutTask)
            If completedTask IsNot callbackTask Then
                If cancellationToken.IsCancellationRequested Then
                    Throw New OperationCanceledException(cancellationToken)
                End If
                Throw New TimeoutException(
                    "CareLink did not return the login callback within 5 minutes. Finish signing in in your default browser and try again.")
            End If

            Dim callbackUrl As String = Await callbackTask
            Dim callbackUri As New Uri(callbackUrl, UriKind.Absolute)

            If Not callbackUri.Scheme.Equals(ProtocolScheme, StringComparison.OrdinalIgnoreCase) OrElse
               Not callbackUri.AbsolutePath.Equals(redirect.AbsolutePath, StringComparison.OrdinalIgnoreCase) Then
                Throw New InvalidDataException("The CareLink OAuth callback URI was not the expected callback.")
            End If

            Dim returnedState As String = GetQueryParameter(callbackUri, "state")
            If Not String.Equals(returnedState, expectedState, StringComparison.Ordinal) Then
                Throw New InvalidDataException("The OAuth state returned by CareLink did not match the login request.")
            End If

            Dim code As String = GetQueryParameter(callbackUri, "code")
            Dim oauthError As String = GetQueryParameter(callbackUri, "error")
            Dim oauthErrorDescription As String = GetQueryParameter(callbackUri, "error_description")

            If String.IsNullOrWhiteSpace(code) Then
                Throw New Exception(
                    If(String.IsNullOrWhiteSpace(oauthError),
                       "CareLink did not return an authorization code.",
                       If(String.IsNullOrWhiteSpace(oauthErrorDescription),
                          $"CareLink authorization failed: {oauthError}",
                          $"CareLink authorization failed: {oauthError} - {oauthErrorDescription}")))
            End If

            Return New RedirectResult With {
                .Code = code,
                .State = returnedState}
        Finally
            SyncLock s_callbackLock
                s_callbackSource = Nothing
                s_expectedState = Nothing
            End SyncLock
        End Try
    End Function

    Friend Sub HandleProtocolCallback(callbackUrl As String)
        If String.IsNullOrWhiteSpace(callbackUrl) Then Return

        Dim source As TaskCompletionSource(Of String) = Nothing
        Dim expectedState As String = Nothing
        SyncLock s_callbackLock
            source = s_callbackSource
            expectedState = s_expectedState
        End SyncLock

        If source Is Nothing OrElse source.Task.IsCompleted Then Return

        Try
            Dim callbackUri As New Uri(callbackUrl, UriKind.Absolute)
            If Not callbackUri.Scheme.Equals(ProtocolScheme, StringComparison.OrdinalIgnoreCase) OrElse
               Not callbackUri.AbsolutePath.Equals("/sso", StringComparison.OrdinalIgnoreCase) Then Return

            Dim returnedState As String = GetQueryParameter(callbackUri, "state")
            If Not String.Equals(returnedState, expectedState, StringComparison.Ordinal) Then Return

            source.TrySetResult(callbackUrl)
        Catch
            ' Ignore unrelated protocol activations; the active login will time out with a useful error.
        End Try
    End Sub

    Private Sub RegisterWindowsProtocolHandler()
        Dim executablePath As String = Environment.ProcessPath
        If String.IsNullOrWhiteSpace(executablePath) Then
            Throw New InvalidOperationException("CareLink could not determine its executable path for OAuth callback registration.")
        End If

        Using schemeKey As RegistryKey = Registry.CurrentUser.CreateSubKey($"Software\Classes\{ProtocolScheme}")
            schemeKey.SetValue("", "URL:CareLink OAuth Callback", RegistryValueKind.String)
            schemeKey.SetValue("URL Protocol", "", RegistryValueKind.String)

            Using commandKey As RegistryKey = schemeKey.CreateSubKey("shell\open\command")
                commandKey.SetValue("", String.Format("""{0}" "%1"", executablePath), RegistryValueKind.String)
            End Using
        End Using
    End Sub

    Private Async Function CaptureLoopbackRedirectAsync(startUrl As String,
                                                        redirect As Uri,
                                                        cancellationToken As CancellationToken) As Task(Of RedirectResult)
        Dim expectedPath As String = If(String.IsNullOrEmpty(redirect.AbsolutePath), "/", redirect.AbsolutePath)
        Dim expectedState As String = GetQueryParameter(startUrl, "state")
        If String.IsNullOrWhiteSpace(expectedState) Then
            expectedState = Guid.NewGuid().ToString("N")
            startUrl = AddQueryParameter(startUrl, "state", expectedState)
        End If

        Using listener As New Net.Sockets.TcpListener(Net.IPAddress.Loopback, redirect.Port)
            Try
                listener.Start()
            Catch ex As Net.Sockets.SocketException
                Throw New InvalidOperationException(
                    $"CareLink could not open its local login callback on port {redirect.Port}. Another program may already be using that port.", ex)
            End Try

            Try
                OpenDefaultBrowser(startUrl)
                Dim clientTask As Task(Of Net.Sockets.TcpClient) = listener.AcceptTcpClientAsync()
                Dim timeoutTask As Task = Task.Delay(TimeSpan.FromSeconds(CallbackTimeoutSeconds), cancellationToken)
                Dim completedTask As Task = Await Task.WhenAny(clientTask, timeoutTask)

                If completedTask IsNot clientTask Then
                    If cancellationToken.IsCancellationRequested Then Throw New OperationCanceledException(cancellationToken)
                    Throw New TimeoutException("CareLink did not receive the login callback within 5 minutes.")
                End If

                Using tcpClient As Net.Sockets.TcpClient = Await clientTask
                    Using stream As Net.Sockets.NetworkStream = tcpClient.GetStream()
                        Dim requestLine As String = Await ReadRequestLineAsync(stream, cancellationToken)
                        Dim callbackUri As Uri = ParseRequestUri(requestLine, redirect)
                        If Not callbackUri.IsLoopback OrElse Not callbackUri.AbsolutePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) Then
                            Throw New InvalidDataException("Received an unexpected OAuth callback URI.")
                        End If
                        Dim returnedState As String = GetQueryParameter(callbackUri, "state")
                        If Not String.Equals(returnedState, expectedState, StringComparison.Ordinal) Then
                            Throw New InvalidDataException("The OAuth state returned by the browser did not match the login request.")
                        End If
                        Dim code As String = GetQueryParameter(callbackUri, "code")
                        If String.IsNullOrWhiteSpace(code) Then Throw New Exception("No authorization code was returned.")
                        Await WriteResponseAsync(stream, "CareLink login complete. You can return to CareLink and close this tab.", HttpStatusCode.OK, cancellationToken)
                        Return New RedirectResult With {.Code = code, .State = returnedState}
                    End Using
                End Using
            Finally
                listener.Stop()
            End Try
        End Using
    End Function

    Private Sub OpenDefaultBrowser(url As String)
        Try
            Dim browserInfo As New ProcessStartInfo With {.FileName = url, .UseShellExecute = True}
            Dim browserProcess As Process = Process.Start(browserInfo)
            If browserProcess Is Nothing Then Throw New InvalidOperationException("Windows did not return a browser process.")
        Catch ex As Exception
            Throw New InvalidOperationException("CareLink could not open the Windows default browser for login. Check that Windows has a default browser configured.", ex)
        End Try
    End Sub

    Private Function AddQueryParameter(url As String, name As String, value As String) As String
        Dim separator As String = If(url.Contains("?"c), "&", "?")
        Return url & separator & Uri.EscapeDataString(name) & "=" & Uri.EscapeDataString(value)
    End Function

    Private Function GetQueryParameter(url As String, name As String) As String
        Return GetQueryParameter(New Uri(url, UriKind.Absolute), name)
    End Function

    Private Function GetQueryParameter(uri As Uri, name As String) As String
        Dim query As String = uri.Query.TrimStart("?"c)
        If query.Length = 0 Then Return Nothing
        For Each part As String In query.Split("&"c, StringSplitOptions.RemoveEmptyEntries)
            Dim pieces As String() = part.Split("="c, 2)
            Dim key As String = Uri.UnescapeDataString(pieces(0).Replace("+", " "))
            If key.Equals(name, StringComparison.OrdinalIgnoreCase) Then
                Return If(pieces.Length > 1, Uri.UnescapeDataString(pieces(1).Replace("+", " ")), String.Empty)
            End If
        Next
        Return Nothing
    End Function

    Private Async Function ReadRequestLineAsync(stream As Net.Sockets.NetworkStream, cancellationToken As CancellationToken) As Task(Of String)
        Dim buffer As New List(Of Byte)()
        Dim singleByte(0) As Byte
        Do
            Dim read As Integer = Await stream.ReadAsync(singleByte, 0, 1, cancellationToken)
            If read = 0 Then Exit Do
            buffer.Add(singleByte(0))
            If buffer.Count >= 2 AndAlso buffer(buffer.Count - 2) = 13 AndAlso buffer(buffer.Count - 1) = 10 Then Exit Do
            If buffer.Count > 8192 Then Throw New InvalidDataException("The OAuth callback request was too large.")
        Loop
        If buffer.Count = 0 Then Throw New InvalidDataException("The OAuth callback connection was closed before a request was received.")
        Return Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd(ControlChars.Cr, ControlChars.Lf)
    End Function

    Private Function ParseRequestUri(requestLine As String, redirect As Uri) As Uri
        Dim parts As String() = requestLine.Split(" "c)
        If parts.Length < 2 OrElse Not parts(0).Equals("GET", StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("The OAuth callback was not a valid HTTP GET request.")
        If Not parts(1).StartsWith("/", StringComparison.Ordinal) Then Throw New InvalidDataException("The OAuth callback request target was invalid.")
        Return New UriBuilder("http", redirect.Host, redirect.Port, parts(1)).Uri
    End Function

    Private Async Function WriteResponseAsync(stream As Net.Sockets.NetworkStream, body As String, statusCode As HttpStatusCode, cancellationToken As CancellationToken) As Task
        Dim bytes As Byte() = Encoding.UTF8.GetBytes($"<html><head><meta charset='utf-8'><title>CareLink</title></head><body>{WebUtility.HtmlEncode(body)}</body></html>")
        Dim header As String = $"HTTP/1.1 {CInt(statusCode)} {statusCode.ToString()}{vbCrLf}" & "Content-Type: text/html; charset=utf-8" & vbCrLf & $"Content-Length: {bytes.Length}" & vbCrLf & "Connection: close" & vbCrLf & vbCrLf
        Dim headerBytes As Byte() = Encoding.ASCII.GetBytes(header)
        Await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken)
        Await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken)
    End Function

End Module
