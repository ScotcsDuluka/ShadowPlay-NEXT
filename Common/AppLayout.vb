' AppLayout.vb — ROOT-FIXED LAYOUT SUPPORT (custom app tree, 2026-08-28)
'
' Active staging tree (Build/Build-Config/dev-layout.json):
'
'   NVIDIA ShadowPlay\
'     Launcher.exe
'     NVIDIA Container\NvContainer.exe + nvsphelper.exe
'     NVIDIA Graphics\NvGraphics.exe
'     NVIDIA Adapter\NVIDIA OSC Adapter.exe + native hook DLL
'     Overlay On-Screen Control\NVIDIA OSC\NVIDIA OSC.exe + CEF runtime
'     Overlay On-Screen Control\NVIDIA NodeAPI\NVIDIA Web Helper.exe + NvNode
'     NVIDIA Launcher\Cef\Launcher.dll
'     NVIDIA Config\ config.json + service configuration
'     Config\ Logs\ Data\ Languages\ Resources\ Flags\
'
' Legacy v1/v2 product folders (NvOverlay\, NvCapture\, NvContainer\,
' NvBackend\, NvGraphics\, NvConfig\, and the older Application/Overlay tree)
' remain supported as fallbacks for existing staged installations.
'
' This module (one copy LINKED into every app project) provides:
'
'   1. Dir  — the layout ROOT. Derived from the EXE location so it works
'      no matter which process asks: Application\* and Overlay\* walk one
'      level up; anything else (root app, dev bin\) is its own root.
'      Override for exotic installs: env NVIDIA_SHADOWPLAY_APP_ROOT.
'
'   2. Assembly resolution — every owner EXE is paired with its owner DLLs
'      in the same directory. Shared-family folders (NvCapture/NvAudio/
'      NvGraphics/Runtime/NvBackend) are found by the Resolving handler below.
'      Zero config, no probing XML: the folder map IS the layout. This also makes the
'      app *.deps.json files relocatable (OWNER tree: they live in
'      .NET Deployment\, NOT next to the dlls) — hostpolicy falls back
'      to app-dir probing and this handler supplies every cross-folder
'      dependency. Only runtimeconfig.json is NOT relocatable: hostfxr
'      hard-requires it beside the app dll before any managed code runs.
'
'   3. Initialize() — call once at app startup (MyApplication.Startup):
'      installs the resolver and points the process CWD at the root so
'      any relative-path leftover code keeps working in dev layouts.
'
' Dev mode: running from a normal bin\ folder, none of the family
' folders exist, the resolver finds nothing (harmless — deps are local
' there) and Dir == exe dir == today's behaviour. This file is
' deliberately dependency-free and Option Strict On-clean so it compiles
' identically inside all five app projects.

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.Loader

Public Module AppLayout

    Private _initialized As Boolean = False
    Private _dir As String = Nothing

    ''' <summary>Raw directory of the running executable (the apphost).</summary>
    Public ReadOnly Property ExeDir As String
        Get
            Try
                Dim exe As String = Process.GetCurrentProcess().MainModule.FileName
                If Not String.IsNullOrEmpty(exe) Then
                    Return Path.GetDirectoryName(Path.GetFullPath(exe))
                End If
            Catch
            End Try
            ' Fallback: the managed app dll's directory.
            Return AppContext.BaseDirectory
        End Get
    End Property

    ''' <summary>The layout ROOT (see header). Computed once per process.</summary>
    Public ReadOnly Property Dir As String
        Get
            If _dir Is Nothing Then
                Dim rootEnv As String = Environment.GetEnvironmentVariable("NVIDIA_SHADOWPLAY_APP_ROOT")
                If Not String.IsNullOrEmpty(rootEnv) AndAlso Directory.Exists(rootEnv) Then
                    _dir = Path.GetFullPath(rootEnv)
                Else
                    ' NOTE: VB is case-insensitive — a local named `exeDir`
                    ' would SHADOW the ExeDir property here (BC42104), so
                    ' this local deliberately carries a distinct name.
                    Dim exeFolder As String = ExeDir
                    Dim leaf As String = New DirectoryInfo(exeFolder).Name
                    ' New owner slots (NVIDIA Launcher\Cef and the two Overlay
                    ' owners) and the nested CaptureEngine live two levels down.
                    ' Preserve the original NvOverlay role slots for old layouts.
                    Dim parentName As String = ""
                    Try
                        Dim parentDir As DirectoryInfo = New DirectoryInfo(exeFolder).Parent
                        If parentDir IsNot Nothing Then parentName = parentDir.Name
                    Catch
                    End Try
                    Dim isLegacyOverlaySlot As Boolean =
                        String.Equals(parentName, "NvOverlay", StringComparison.OrdinalIgnoreCase) AndAlso
                        (String.Equals(leaf, "WinForm", StringComparison.OrdinalIgnoreCase) OrElse
                         String.Equals(leaf, "CEF", StringComparison.OrdinalIgnoreCase))
                    Dim isLauncherCefSlot As Boolean =
                        String.Equals(parentName, "NVIDIA Launcher", StringComparison.OrdinalIgnoreCase) AndAlso
                        String.Equals(leaf, "Cef", StringComparison.OrdinalIgnoreCase)
                    Dim isOverlayOwnerSlot As Boolean =
                        String.Equals(parentName, "Overlay On-Screen Control", StringComparison.OrdinalIgnoreCase) AndAlso
                        (String.Equals(leaf, "NVIDIA OSC", StringComparison.OrdinalIgnoreCase) OrElse
                         String.Equals(leaf, "NVIDIA NodeAPI", StringComparison.OrdinalIgnoreCase))
                    Dim isCaptureEngineSlot As Boolean =
                        String.Equals(parentName, "NVIDIA Container", StringComparison.OrdinalIgnoreCase) AndAlso
                        String.Equals(leaf, "CaptureEngine", StringComparison.OrdinalIgnoreCase)
                    Dim isRoleSlot As Boolean =
                        isLegacyOverlaySlot OrElse isLauncherCefSlot OrElse
                        isOverlayOwnerSlot OrElse isCaptureEngineSlot
                    Dim isOneLevelHost As Boolean =
                        String.Equals(leaf, "NvCapture", StringComparison.OrdinalIgnoreCase) OrElse
                        String.Equals(leaf, "NvContainer", StringComparison.OrdinalIgnoreCase) OrElse
                        String.Equals(leaf, "NvBackend", StringComparison.OrdinalIgnoreCase) OrElse
                        String.Equals(leaf, "NVIDIA Capture", StringComparison.OrdinalIgnoreCase) OrElse
                        String.Equals(leaf, "NVIDIA Container", StringComparison.OrdinalIgnoreCase) OrElse
                        String.Equals(leaf, "NVIDIA Graphics", StringComparison.OrdinalIgnoreCase) OrElse
                        String.Equals(leaf, "NVIDIA Adapter", StringComparison.OrdinalIgnoreCase) OrElse
                        String.Equals(leaf, "Application", StringComparison.OrdinalIgnoreCase) OrElse
                        String.Equals(leaf, "Overlay", StringComparison.OrdinalIgnoreCase) OrElse
                        String.Equals(leaf, "ShadowPlay", StringComparison.OrdinalIgnoreCase)
                    If isRoleSlot Then
                        _dir = Path.GetFullPath(Path.Combine(exeFolder, "..", ".."))
                    ElseIf isOneLevelHost Then
                        _dir = Path.GetFullPath(Path.Combine(exeFolder, ".."))
                    Else
                        _dir = exeFolder
                    End If
                End If
            End If
            Return _dir
        End Get
    End Property

    ''' <summary>Path under the layout root: AppLayout.P("Config", "engine.json").</summary>
    Public Function P(ParamArray parts As String()) As String
        Dim acc As String = Dir
        Dim i As Integer
        For i = 0 To parts.Length - 1
            acc = Path.Combine(acc, parts(i))
        Next i
        Return acc
    End Function

    ''' <summary>
    ''' Full path of a family app executable, resolved against the owner
    ''' layout v2 (see header). First existing candidate wins; the final
    ''' fallback is always the layout root (dev bin\ where exes build flat).
    ''' Callers keep their own final File.Exists guards — this only picks
    ''' WHERE to look first.
    ''' </summary>
    Public Function ExePath(appExeName As String) As String
        Dim candidates As New List(Of String)(4)
        If String.Equals(appExeName, "nvsphelper64.exe", StringComparison.OrdinalIgnoreCase) Then
            candidates.Add(P("NVIDIA Container", appExeName))
            candidates.Add(P("NvCapture", appExeName))
            candidates.Add(P("ShadowPlay", appExeName))
        ElseIf String.Equals(appExeName, "NvCapture.exe", StringComparison.OrdinalIgnoreCase) Then
            candidates.Add(P("NVIDIA Container", "CaptureEngine", appExeName))
            candidates.Add(P("NVIDIA Capture", appExeName))
            candidates.Add(P("ShadowPlay", appExeName))
        ElseIf String.Equals(appExeName, "NVIDIA ShadowPlay Helper.exe", StringComparison.OrdinalIgnoreCase) Then
            candidates.Add(P("NVIDIA Container", appExeName))
            candidates.Add(P("ShadowPlay", appExeName))
            candidates.Add(P("NvCapture", appExeName))
        ElseIf String.Equals(appExeName, "NVIDIA OSC.exe", StringComparison.OrdinalIgnoreCase) Then
            candidates.Add(P("Overlay On-Screen Control", "NVIDIA OSC", appExeName))
            candidates.Add(P("NvOverlay", "CEF", appExeName))
            candidates.Add(P("Overlay", appExeName))
        ElseIf String.Equals(appExeName, "NVIDIA OSC Adapter.exe", StringComparison.OrdinalIgnoreCase) Then
            candidates.Add(P("NVIDIA Adapter", appExeName))
        ElseIf String.Equals(appExeName, "NvGraphics.exe", StringComparison.OrdinalIgnoreCase) Then
            candidates.Add(P("NVIDIA Graphics", appExeName))
            candidates.Add(P("NvGraphics", appExeName))
        ElseIf String.Equals(appExeName, "NVIDIA Share.exe", StringComparison.OrdinalIgnoreCase) Then
            candidates.Add(P("NvOverlay", "CEF", appExeName))
            candidates.Add(P("NvOverlay", "WinForm", appExeName))
            candidates.Add(P("Overlay", appExeName))
        ElseIf String.Equals(appExeName, "NVIDIA Notifier.exe", StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(appExeName, "NVIDIA ShadowPlay.exe", StringComparison.OrdinalIgnoreCase) Then
            candidates.Add(P("NvOverlay", "WinForm", appExeName))
            candidates.Add(P("Overlay", appExeName))
            candidates.Add(P("Application", appExeName))
        ElseIf String.Equals(appExeName, "NvContainer.exe", StringComparison.OrdinalIgnoreCase) Then
            candidates.Add(P("NVIDIA Container", appExeName))
            candidates.Add(P("NvContainer", appExeName))
        ElseIf String.Equals(appExeName, "NvBackend.exe", StringComparison.OrdinalIgnoreCase) Then
            candidates.Add(P("NvBackend", appExeName))
        ElseIf String.Equals(appExeName, "NVIDIA Backend.exe", StringComparison.OrdinalIgnoreCase) Then
            candidates.Add(P("NvBackend", appExeName))
        ElseIf String.Equals(appExeName, "NVIDIA Web Helper.exe", StringComparison.OrdinalIgnoreCase) Then
            ' Active owner slot first; old NvNode/NvBackend locations remain
            ' as fallbacks for pre-refactor staged trees.
            candidates.Add(P("Overlay On-Screen Control", "NVIDIA NodeAPI", appExeName))
            candidates.Add(P("NvNode", appExeName))
            candidates.Add(P("NvBackend", appExeName))
            candidates.Add(P("Application", appExeName))
        Else
            candidates.Add(P("Application", appExeName))
        End If
        candidates.Add(P(appExeName))
        For Each candidate As String In candidates
            If File.Exists(candidate) Then Return candidate
        Next candidate
        Return P(appExeName)
    End Function

    ''' <summary>
    ''' Creates the parent directory of <paramref name="filePath"/> on demand.
    ''' Flags\Config\Logs\Data are runtime-created — never staged — so every
    ''' writer of a root-fixed file calls this first. Never throws.
    ''' </summary>
    ''' <remarks>The parameter is deliberately named <c>filePath</c>, NOT
    ''' <c>path</c>: VB is case-insensitive and a <c>path</c> parameter would
    ''' shadow the System.IO.Path TYPE inside this body (BC30456).</remarks>
    Public Sub EnsureParentDir(filePath As String)
        Try
            If String.IsNullOrEmpty(filePath) Then Return
            Dim parent As String = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath))
            If Not String.IsNullOrEmpty(parent) AndAlso Not Directory.Exists(parent) Then
                Directory.CreateDirectory(parent)
            End If
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' Deletes a root-fixed file when both the file AND its folder exist.
    ''' File.Delete on a path whose DIRECTORY is missing throws
    ''' DirectoryNotFoundException (the Flags\ first-run crash) — this call
    ''' never throws for a missing file or folder.
    ''' </summary>
    ''' <remarks>Same <c>filePath</c> naming rule as EnsureParentDir — a
    ''' <c>path</c> parameter would shadow the System.IO.Path type.</remarks>
    Public Sub DeleteFileIfExists(filePath As String)
        Try
            If Not String.IsNullOrEmpty(filePath) AndAlso File.Exists(filePath) Then
                File.Delete(filePath)
            End If
        Catch
        End Try
    End Sub

    ''' <summary>Call once at startup: CWD = root + install the assembly
    ''' resolver. Idempotent and safe to call again.</summary>
    Public Sub Initialize()
        If _initialized Then Return
        _initialized = True
        Try
            Environment.CurrentDirectory = Dir
        Catch
        End Try
        AddHandler AssemblyLoadContext.Default.Resolving, AddressOf OnDefaultResolving
    End Sub

    ''' <summary>Owner folders probed for dependency assemblies, in order.
    ''' The production tree is self-contained by owner, with shared runtime
    ''' families under NvCapture/NvAudio/NvGraphics/Runtime. Legacy folders
    ''' remain only as dev-bin fallbacks; retired WebView/WebViewHook and the
    ''' Services owner are not part of the production probe map.</summary>
    Private Function ProbeFolders() As List(Of String)
        Dim folders As New List(Of String)(18)
        ' Active owner layout first. Keep these ahead of legacy candidates so
        ' relocated dependencies cannot accidentally bind to a stale copy.
        folders.Add(P("NVIDIA Capture"))
        folders.Add(P("NVIDIA Container", "CaptureEngine"))
        folders.Add(P("NVIDIA Audio"))
        folders.Add(P("NVIDIA Graphics"))
        folders.Add(P("Runtime"))
        folders.Add(P("Overlay On-Screen Control", "NVIDIA OSC"))
        folders.Add(P("Overlay On-Screen Control", "NVIDIA NodeAPI"))
        folders.Add(P("NVIDIA Launcher", "Cef"))
        folders.Add(P("NVIDIA Container"))
        folders.Add(P("NVIDIA Adapter"))
        ' Legacy layout remains a compatibility fallback for existing installs.
        folders.Add(P("NvCapture"))
        folders.Add(P("NvAudio"))
        folders.Add(P("NvGraphics"))
        folders.Add(P("NvOverlay", "WinForm"))
        folders.Add(P("NvOverlay", "CEF"))
        folders.Add(P("NvBackend"))
        ' Legacy dev source locations; harmless when not present in a staged tree.
        folders.Add(P("ShadowPlay"))
        folders.Add(P("ShadowPlay", "NvCapture"))
        folders.Add(P("NvGallery", "WinForm"))
        ' Root owner — the root app's own outputs live here (Launcher.dll is
        ' a library dependency of the overlay, not just the root exe).
        folders.Add(Dir)
        folders.Add(P("Engine"))
        folders.Add(P("Core"))
        folders.Add(P("Audio"))
        folders.Add(P("Graphics"))
        folders.Add(P("Libraries"))
        folders.Add(P("Runtimes", "win", "lib", "net10.0"))
        folders.Add(ExeDir)
        Return folders
    End Function

    Private Function OnDefaultResolving(context As AssemblyLoadContext,
                                        assemblyName As System.Reflection.AssemblyName) As System.Reflection.Assembly
        If assemblyName Is Nothing Then Return Nothing
        Dim simple As String = assemblyName.Name
        If String.IsNullOrEmpty(simple) Then Return Nothing
        ' Satellite resources are not file-probed here.
        If simple.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) Then Return Nothing
        Dim candidate As String
        For Each folder In ProbeFolders()
            candidate = Path.Combine(folder, simple & ".dll")
            If File.Exists(candidate) Then
                Try
                    Return context.LoadFromAssemblyPath(candidate)
                Catch
                    ' Wrong-architecture or corrupt file: keep probing.
                End Try
            End If
            candidate = Path.Combine(folder, simple & ".exe")
            If File.Exists(candidate) Then
                Try
                    Return context.LoadFromAssemblyPath(candidate)
                Catch
                End Try
            End If
        Next folder
        Return Nothing
    End Function

End Module
