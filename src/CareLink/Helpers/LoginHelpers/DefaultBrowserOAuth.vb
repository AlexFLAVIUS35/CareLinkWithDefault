Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading

Friend Module DefaultBrowserOAuth

    Private Const CallbackTimeout As Integer = 300

    Friend Async Function CaptureRedirectAsync(startUrl As String,
                                               redirectUri As String,
                                               cancellationToken As CancellationToken) As Task(Of RedirectResult)
        Dim redirect As New Uri(redirectUri, UriKind.Absolute)

        If Not redirect.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) OrElse
           Not redirect.IsLoopback Then
            Throw New NotSupportedException(
                $"The CareLink OAuth redirect URI is not a loopback HTTP URI: {redirectUri}")
        End If

        If redirect.Port <= 0 OrElse redirect.Port > 65535 Then
            Throw New ArgumentException($"The OAuth redirect URI has an invalid port: {redirectUri}")
        End If

        Dim expectedPath As String = If(String.IsNullOrEmpty(redirect.AbsolutePath), "/", redirect.AbsolutePath)
        Dim expectedState As String = Guid.NewGuid().ToString("N")
        Dim browserUrl As String = AddQueryParameter(startUrl, "state", expectedState)

        ' Use TcpListener rather than HttpListener. HttpListener relies on HTTP.sys URL ACLs
        ' and can fail or hang on otherwise-valid user installations.
        Using listener As New TcpListener(IPAddress.Loopback, redirect.Port)
            Try
                listener.Start()
            Catch ex As SocketException
                Throw New InvalidOperationException(
                    $"CareLink could not open its local login callback on port {redirect.Port}. " &
                    "Another program may already be using that port.", ex)
            End Try

            Try
                Dim browserInfo As New ProcessStartInfo With {
                    .FileName = browserUrl,
                    .UseShellExecute = True}

                Try
                    Dim browserProcess As Process = Process.Start(browserInfo)
                    If browserProcess Is Nothing Then
                        Throw New InvalidOperationException("Windows could not start the default browser.")
                    End If
                Catch ex As Exception
                    Throw New InvalidOperationException(
                        "CareLink could not open the Windows default browser for login.", ex)
                End Try

                Dim clientTask As Task(Of TcpClient) = listener.AcceptTcpClientAsync()
                Dim timeoutTask As Task = Task.Delay(TimeSpan.FromSeconds(CallbackTimeout), cancellationToken)
                Dim completedTask As Task = Await Task.WhenAny(clientTask, timeoutTask)

                If completedTask IsNot clientTask Then
                    If cancellationToken.IsCancellationRequested Then
                        Throw New OperationCanceledException(cancellationToken)
                    End If
                    Throw New TimeoutException(
                        $"CareLink did not receive the login callback within {CallbackTimeout \ 60} minutes. " &
                        "Finish signing in in your default browser and try again.")
                End If

                Using tcpClient As TcpClient = Await clientTask
                    Using stream As NetworkStream = tcpClient.GetStream()
                        Dim requestLine As String = Await ReadRequestLineAsync(stream, cancellationToken)
                        Dim callbackUri As Uri = ParseRequestUri(requestLine, redirect)

                        If Not callbackUri.IsLoopback OrElse
                           Not callbackUri.AbsolutePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) Then
                            Await WriteResponseAsync(stream,
                                "Invalid CareLink OAuth callback. You can close this tab.",
                                HttpStatusCode.BadRequest,
                                cancellationToken)
                            Throw New InvalidDataException("Received an unexpected OAuth callback URI.")
                        End If

                        Dim query As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                        Dim queryText As String = callbackUri.Query.TrimStart("?"c)
                        If queryText.Length > 0 Then
                            For Each part As String In queryText.Split("&"c, StringSplitOptions.RemoveEmptyEntries)
                                Dim pieces As String() = part.Split("="c, 2)
                                Dim key As String = Uri.UnescapeDataString(pieces(0).Replace("+", " "))
                                Dim value As String = If(pieces.Length > 1,
                                                         Uri.UnescapeDataString(pieces(1).Replace("+", " ")),
                                                         String.Empty)
                                query(key) = value
                            Next
                        End If

                        Dim returnedState As String = Nothing
                        query.TryGetValue("state", returnedState)
                        If Not String.Equals(returnedState, expectedState, StringComparison.Ordinal) Then
                            Await WriteResponseAsync(stream,
                                "The CareLink login callback could not be verified. You can close this tab.",
                                HttpStatusCode.BadRequest,
                                cancellationToken)
                            Throw New InvalidDataException("The OAuth state returned by the browser did not match the login request.")
                        End If

                        Dim code As String = Nothing
                        query.TryGetValue("code", code)

                        Dim oauthError As String = Nothing
                        query.TryGetValue("error", oauthError)

                        If String.IsNullOrWhiteSpace(code) Then
                            Dim errorText As String =
                                If(String.IsNullOrWhiteSpace(oauthError),
                                   "No authorization code was returned.",
                                   $"Authorization failed: {oauthError}")
                            Await WriteResponseAsync(stream,
                                WebUtility.HtmlEncode(errorText) & "<br>You can close this tab.",
                                HttpStatusCode.BadRequest,
                                cancellationToken)
                            Throw New Exception(errorText)
                        End If

                        Await WriteResponseAsync(stream,
                            "CareLink login complete. You can return to CareLink and close this tab.",
                            HttpStatusCode.OK,
                            cancellationToken)

                        Return New RedirectResult With {
                            .Code = code,
                            .State = returnedState}
                    End Using
                End Using
            Finally
                listener.Stop()
            End Try
        End Using
    End Function

    Private Shared Function AddQueryParameter(url As String, name As String, value As String) As String
        Dim separator As String = If(url.Contains("?"c), "&", "?")
        Return url & separator & Uri.EscapeDataString(name) & "=" & Uri.EscapeDataString(value)
    End Function

    Private Shared Async Function ReadRequestLineAsync(stream As NetworkStream,
                                                       cancellationToken As CancellationToken) As Task(Of String)
        Dim buffer As New List(Of Byte)()
        Dim singleByte(0) As Byte

        Do
            Dim readTask As Task(Of Integer) = stream.ReadAsync(singleByte, 0, 1, cancellationToken)
            Dim read As Integer = Await readTask
            If read = 0 Then
                Exit Do
            End If

            buffer.Add(singleByte(0))
            If buffer.Count >= 2 AndAlso
               buffer(buffer.Count - 2) = 13 AndAlso
               buffer(buffer.Count - 1) = 10 Then
                Exit Do
            End If

            If buffer.Count > 8192 Then
                Throw New InvalidDataException("The OAuth callback request was too large.")
            End If
        Loop

        If buffer.Count = 0 Then
            Throw New InvalidDataException("The OAuth callback connection was closed before a request was received.")
        End If

        Return Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd(ControlChars.Cr, ControlChars.Lf)
    End Function

    Private Shared Function ParseRequestUri(requestLine As String, redirect As Uri) As Uri
        Dim parts As String() = requestLine.Split(" "c)
        If parts.Length < 2 OrElse Not parts(0).Equals("GET", StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("The OAuth callback was not a valid HTTP GET request.")
        End If

        Dim requestTarget As String = parts(1)
        If Not requestTarget.StartsWith("/", StringComparison.Ordinal) Then
            Throw New InvalidDataException("The OAuth callback request target was invalid.")
        End If

        Return New UriBuilder("http", redirect.Host, redirect.Port, requestTarget).Uri
    End Function

    Private Shared Async Function WriteResponseAsync(stream As NetworkStream,
                                                     body As String,
                                                     statusCode As HttpStatusCode,
                                                     cancellationToken As CancellationToken) As Task
        Dim bytes As Byte() = Encoding.UTF8.GetBytes(
            $"<html><head><meta charset='utf-8'><title>CareLink</title></head><body>{body}</body></html>")
        Dim header As String =
            $"HTTP/1.1 {CInt(statusCode)} {statusCode.ToString()}{vbCrLf}" &
            "Content-Type: text/html; charset=utf-8" & vbCrLf &
            $"Content-Length: {bytes.Length}" & vbCrLf &
            "Connection: close" & vbCrLf & vbCrLf
        Dim headerBytes As Byte() = Encoding.ASCII.GetBytes(header)
        Await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken)
        Await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken)
    End Function

End Module
