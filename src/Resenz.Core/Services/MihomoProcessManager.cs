using System.Diagnostics;
using System.Text.Json;

namespace Resenz.Core.Services;

public sealed record CoreLaunchOptions(
    string BinaryPath,
    string WorkingDirectory,
    string ConfigPath,
    string? Secret);

public interface IMihomoProcessManager : IDisposable
{
    bool IsRunning { get; }
    int? ProcessId { get; }
    event EventHandler<CoreProcessExitedEventArgs>? Exited;

    Task<bool> ValidateConfigAsync(CoreLaunchOptions options, CancellationToken ct = default);
    Task<bool> StartAsync(CoreLaunchOptions options, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<IReadOnlyList<int>> KillOrphansAsync(string binaryPath, CancellationToken ct = default);
}

public sealed record CoreProcessExitedEventArgs(int ExitCode, string? LastError);

public sealed class MihomoProcessManager : IMihomoProcessManager
{
    private readonly object _gate = new();
    private Process? _process;
    private readonly List<string> _recentError = new();

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false } ? _process.Id : null;
            }
        }
    }

    public event EventHandler<CoreProcessExitedEventArgs>? Exited;

    public async Task<bool> ValidateConfigAsync(CoreLaunchOptions options, CancellationToken ct = default)
    {
        if (!File.Exists(options.BinaryPath) || !File.Exists(options.ConfigPath)) return false;

        var psi = new ProcessStartInfo
        {
            FileName = options.BinaryPath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(options.WorkingDirectory);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(options.ConfigPath);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StartAsync(CoreLaunchOptions options, CancellationToken ct = default)
    {
        await StopAsync(ct).ConfigureAwait(false);

        if (!File.Exists(options.BinaryPath))
            throw new FileNotFoundException("mihomo 코어 실행 파일을 찾을 수 없음", options.BinaryPath);

        var psi = new ProcessStartInfo
        {
            FileName = options.BinaryPath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(options.WorkingDirectory);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(options.ConfigPath);
        if (!string.IsNullOrEmpty(options.Secret))
        {
            psi.ArgumentList.Add("-secret");
            psi.ArgumentList.Add(options.Secret);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => CaptureLine(e.Data);
        process.ErrorDataReceived += (_, e) => CaptureLine(e.Data);
        process.Exited += OnProcessExited;

        lock (_gate)
        {
            _process = process;
            _recentError.Clear();
        }

        if (!process.Start()) return false;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process.HasExited == false;
    }

    private void CaptureLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        // 진짜 문제 줄만 남김: mihomo의 일반 level=info/debug 트래픽 로그는 양이 많고 진단 가치가 없어,
        // 전부 오류 알림에 쌓으면 UI가 길고 지저분해짐. 크래시 시 핵심 정보는 대부분 error/fatal/warning.
        if (!LooksLikeError(line)) return;
        lock (_gate)
        {
            _recentError.Add(Clean(line));
            if (_recentError.Count > 10) _recentError.RemoveAt(0);
        }
    }

    /// <summary>mihomo 출력 한 줄이 오류/경고 수준인지 판단(log-level 텍스트와 JSON 두 형식 모두 호환).</summary>
    private static bool LooksLikeError(string line)
    {
        if (line.Contains("level=error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("level=fatal", StringComparison.OrdinalIgnoreCase)
            || line.Contains("level=warning", StringComparison.OrdinalIgnoreCase)
            || line.Contains("level=warn", StringComparison.OrdinalIgnoreCase))
            return true;
        if (line.Contains("\"level\":\"error\"", StringComparison.OrdinalIgnoreCase)
            || line.Contains("\"level\":\"fatal\"", StringComparison.OrdinalIgnoreCase)
            || line.Contains("\"level\":\"warning\"", StringComparison.OrdinalIgnoreCase)
            || line.Contains("\"level\":\"warn\"", StringComparison.OrdinalIgnoreCase))
            return true;
        // level 필드는 없지만 error/fatal 키워드를 포함한 것(초기화 치명 오류 등).
        return !line.Contains("level=info", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("level=debug", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("fatal", StringComparison.OrdinalIgnoreCase)
                || line.Contains("panic", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>로그 한 줄 축약: 타임스탬프와 레벨 접두사를 제거하고 msg 본문만 남겨 화면에서 읽기 쉽게 함.</summary>
    private static string Clean(string line)
    {
        var msgIndex = line.IndexOf("msg=", StringComparison.OrdinalIgnoreCase);
        if (msgIndex >= 0)
        {
            var msg = line[(msgIndex + 4)..].Trim();
            if (msg.Length > 1 && msg[0] == '"')
            {
                var end = msg.IndexOf('"', 1);
                if (end > 0) msg = msg[1..end];
            }
            if (msg.Length > 0) return msg;
        }
        return line;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int exitCode;
        string? lastError;
        lock (_gate)
        {
            exitCode = _process?.ExitCode ?? -1;
            lastError = _recentError.Count > 0 ? string.Join(Environment.NewLine, _recentError.TakeLast(10)) : null;
        }
        Exited?.Invoke(this, new CoreProcessExitedEventArgs(exitCode, lastError));
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        if (process is null) return;

        try
        {
            process.Exited -= OnProcessExited;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public async Task<IReadOnlyList<int>> KillOrphansAsync(string binaryPath, CancellationToken ct = default)
    {
        var killed = new List<int>();
        if (!File.Exists(binaryPath)) return killed;

        var targetPath = Path.GetFullPath(binaryPath);
        var expectedDirectory = Path.GetDirectoryName(targetPath)!;
        var processName = Path.GetFileNameWithoutExtension(targetPath);

        try
        {
            var all = Process.GetProcessesByName(processName);
            var currentPid = Environment.ProcessId;

            foreach (var victim in all)
            {
                try
                {
                    if (victim.Id == currentPid) continue;

                    string? victimPath = null;
                    try { victimPath = victim.MainModule?.FileName; } catch { }
                    if (string.IsNullOrEmpty(victimPath)) continue;

                    // 이 앱 작업 디렉터리 아래의 코어만 정리해 다른 클라이언트(예: clash-party) 프로세스를 오종하지 않게 함.
                    var victimDirectory = Path.GetDirectoryName(Path.GetFullPath(victimPath));
                    if (!string.Equals(victimDirectory, expectedDirectory, StringComparison.OrdinalIgnoreCase))
                        continue;

                    victim.Kill(entireProcessTree: true);
                    await victim.WaitForExitAsync(ct).ConfigureAwait(false);
                    killed.Add(victim.Id);
                }
                catch
                {
                }
                finally
                {
                    victim.Dispose();
                }
            }
        }
        catch
        {
        }
        return killed;
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
    }
}
