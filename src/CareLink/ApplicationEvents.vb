' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports Microsoft.VisualBasic.ApplicationServices

Namespace My

    Partial Friend Class MyApplication

        Private Sub Me_ApplyApplicationDefaults(sender As Object, e As ApplyApplicationDefaultsEventArgs) _
            Handles Me.ApplyApplicationDefaults

            e.HighDpiMode = HighDpiMode.PerMonitorV2
            e.ColorMode = SystemColorMode.Dark
            e.FormRevealMode = FormRevealMode.Deferred
            e.VisualStylesMode = VisualStylesMode.Net11
        End Sub

        Private Sub Me_StartupNextInstance(sender As Object, e As StartupNextInstanceEventArgs) _
            Handles Me.StartupNextInstance

            For Each argument As String In e.CommandLine
                If argument.StartsWith("com.medtronic.carepartner:", StringComparison.OrdinalIgnoreCase) Then
                    DefaultBrowserOAuth.HandleProtocolCallback(argument)
                    Exit For
                End If
            Next
        End Sub

        Private Sub Me_UnhandledException(sender As Object, e As UnhandledExceptionEventArgs) _
            Handles Me.UnhandledException

            ExceptionHandlerDialog.UnhandledException = e
            ExceptionHandlerDialog.ShowDialog()
        End Sub

    End Class
End Namespace
