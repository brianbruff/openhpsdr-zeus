// SidecarProcess.cs — thin wrapper over System.Diagnostics.Process.
//
// Owns the launch + stdio plumbing for a single zeus-plughost instance.
// The PluginHostManager handles the higher-level lifecycle (restart on
// SIGKILL, backoff, IsRunning state). Here we focus on:
//   - finding + spawning the binary
//   - capturing stderr (and stdout) to the log shim
//   - exposing the Exited event so the manager can react
//   - giving callers the PID for diagnostics
//
// stdin / stdout are reserved for future use; today we redirect them so
// the sidecar doesn't inherit the host console, which avoids "child
// keeps the terminal alive after parent exit" surprises during dev.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zeus.PluginHost.Native;

/// <summary>
/// Process-level wrapper around the spawned sidecar. One instance per
/// running sidecar; not reusable after exit (construct a new one).
/// </summary>
public sealed class SidecarProcess : IDisposable
{
    private readonly Process _process;
    private readonly IPluginHostLog _log;
    private readonly TaskCompletionSource<int> _exitTcs;

    private bool _disposed;

    /// <summary>Raised when the process exits (clean or crashed).</summary>
    public event EventHandler<SidecarExitedEventArgs>? Exited;

    /// <summary>Operating-system process id; -1 once the process has exited.</summary>
    public int ProcessId { get; }

    /// <summary>Resolved absolute path of the sidecar binary that was launched.</summary>
    public string BinaryPath { get; }

    /// <summary>True until the underlying process exits.</summary>
    public bool IsAlive
    {
        get
        {
            if (_disposed) return false;
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>
    /// Awaitable that completes with the process exit code once the
    /// sidecar terminates.
    /// </summary>
    public Task<int> ExitTask => _exitTcs.Task;

    private SidecarProcess(Process process, string binaryPath, IPluginHostLog log)
    {
        _process = process;
        BinaryPath = binaryPath;
        _log = log;
        ProcessId = process.Id;
        _exitTcs = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        process.EnableRaisingEvents = true;
        process.Exited += OnProcessExited;
        process.OutputDataReceived += OnStdout;
        process.ErrorDataReceived += OnStderr;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>
    /// Spawn a new sidecar with optional argv. Throws
    /// <see cref="FileNotFoundException"/> if the binary path does not
    /// resolve, and rethrows any
    /// <see cref="System.ComponentModel.Win32Exception"/> from the OS.
    /// </summary>
    /// <param name="binaryPath">Resolved sidecar binary path.</param>
    /// <param name="log">Optional log sink.</param>
    /// <param name="args">Optional argv passed to the sidecar. Phase 2
    /// callers pass <c>--shm-name</c> + <c>--control-pipe</c> here.</param>
    public static SidecarProcess Launch(
        string binaryPath,
        IPluginHostLog? log = null,
        IReadOnlyList<string>? args = null)
    {
        log ??= NullPluginHostLog.Instance;
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            throw new ArgumentException(
                "binaryPath must be non-empty", nameof(binaryPath));
        }
        if (!File.Exists(binaryPath))
        {
            throw new FileNotFoundException(
                $"sidecar binary not found at {binaryPath}", binaryPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (args != null)
        {
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
        }

        var proc = new Process { StartInfo = psi };
        if (!proc.Start())
        {
            throw new InvalidOperationException(
                $"Process.Start returned false for {binaryPath}");
        }

        log.LogInformation(
            $"SidecarProcess: launched {binaryPath} pid={proc.Id}");
        return new SidecarProcess(proc, binaryPath, log);
    }

    /// <summary>
    /// Send a SIGKILL / TerminateProcess to the sidecar and wait up to
    /// the supplied timeout for it to exit.
    /// </summary>
    public async Task KillAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_disposed) return;
        try
        {
            if (!_process.HasExited)
            {
                _log.LogWarning(
                    $"SidecarProcess: killing pid={_process.Id} ({BinaryPath})");
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process has already exited between HasExited check and Kill.
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            await _process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // We did our best; the manager will treat this as a stuck
            // sidecar and proceed to recreate one.
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int code;
        try { code = _process.ExitCode; }
        catch { code = -1; }

        _log.LogWarning(
            $"SidecarProcess: pid={ProcessId} exited code={code}");
        _exitTcs.TrySetResult(code);
        Exited?.Invoke(this, new SidecarExitedEventArgs(ProcessId, code));
    }

    private void OnStdout(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        _log.LogInformation($"[sidecar pid={ProcessId} stdout] {e.Data}");
    }

    private void OnStderr(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        _log.LogWarning($"[sidecar pid={ProcessId} stderr] {e.Data}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch { /* best effort */ }
            }
        }
        catch { /* HasExited can throw if the handle is already closed */ }
        _process.Exited -= OnProcessExited;
        _process.OutputDataReceived -= OnStdout;
        _process.ErrorDataReceived -= OnStderr;
        _process.Dispose();
    }
}

public sealed class SidecarExitedEventArgs : EventArgs
{
    public int ProcessId { get; }
    public int ExitCode { get; }
    public SidecarExitedEventArgs(int processId, int exitCode)
    {
        ProcessId = processId;
        ExitCode = exitCode;
    }
}
