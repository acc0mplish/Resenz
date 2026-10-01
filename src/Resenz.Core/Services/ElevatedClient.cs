using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Resenz.Core.Services;

public interface IElevatedClient
{
    bool IsElevatedHostRunning { get; }
    /// <summary>마지막 상승 기동 실패 원인(상위에서 사용자 안내용).</summary>
    string? LaunchError { get; }
    Task<ElevatedResponse> StartCoreAsync(string binaryPath, string workingDirectory, string configPath, string? secret, CancellationToken ct = default);
    Task<ElevatedResponse> StopCoreAsync(CancellationToken ct = default);
    Task<ElevatedResponse> GetStatusAsync(CancellationToken ct = default);
    Task<ElevatedResponse> ShutdownAsync(CancellationToken ct = default);
    bool LaunchElevatedHost();
    void KillOrphanHosts();
}

public sealed class ElevatedClient : IElevatedClient
{
    private readonly string _hostPath;
    private readonly object _gate = new();
    private Process? _hostProcess;
    private string? _launchError;

    public string? LaunchError => _launchError;

    public ElevatedClient(string hostPath)
    {
        _hostPath = hostPath;
    }

    public bool IsElevatedHostRunning
    {
        get
        {
            lock (_gate)
            {
                if (_hostProcess is { HasExited: false }) return true;
            }

            // 이 프로세스가 호스트를 띄운 적이 없을 때 명명 파이프로 이미 실행 중인 호스트가 있는지 탐지.
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".", ElevatedProtocol.PipeName, PipeDirection.InOut);
                pipe.Connect(300);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void KillOrphanHosts()
    {
        lock (_gate)
        {
            if (_hostProcess is not null)
            {
                try
                {
                    if (!_hostProcess.HasExited)
                    {
                        _hostProcess.Kill();
                        _hostProcess.WaitForExit(3000);
                    }
                }
                catch
                {
                }
                finally
                {
                    _hostProcess.Dispose();
                    _hostProcess = null;
                }
            }
        }

        // 실행 파일 전체 경로로도 매칭해 이 프로세스가 핸들을 쥐고 있지 않은 남은 호스트를 종료.
        try
        {
            var target = Path.GetFullPath(_hostPath);
            foreach (var process in Process.GetProcessesByName("Resenz.Elevated"))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null &&
                        string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(3000);
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
        }
        catch
        {
        }
    }

    public bool LaunchElevatedHost()
    {
        lock (_gate)
        {
            if (_hostProcess is { HasExited: false }) return true;
            if (!File.Exists(_hostPath)) return false;

            // PowerShell Start-Process -Verb RunAs 우선 사용: UAC가 '알림 안 함'이면 PowerShell이 바로 오류를 내고,
            // 조용히 거부될 때도 명확한 오류를 얻을 수 있어 .NET Process.Start(runas) 실패보다 잡기 쉬움.
            if (TryLaunchViaPowerShell()) return true;

            // .NET runas로 폴백.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _hostPath,
                    // 메인 프로그램 PID를 명시적으로 전달: 권한 상승 프로세스의 부모는 UAC가 바꿔버려 부모 관계로 생존 판단 불가.
                    Arguments = $"--owner {Environment.ProcessId}",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                _hostProcess = Process.Start(psi);
                return _hostProcess is not null;
            }
            catch (Exception ex)
            {
                _launchError = ex.Message;
                return false;
            }
        }
    }

    private bool TryLaunchViaPowerShell()
    {
        try
        {
            var escaped = _hostPath.Replace("'", "''");
            // -Wait PowerShell이 권한 상승 프로세스 생성을 기다리게 함. UAC가 조용히 거부하면 Start-Process가 오류를 냄.
            var command =
                $"try {{ $p = Start-Process -FilePath '{escaped}' " +
                $"-ArgumentList '--owner {Environment.ProcessId}' -Verb RunAs -PassThru -WindowStyle Hidden; " +
                $"if ($p) {{ exit 0 }} else {{ exit 2 }} }} catch {{ Write-Error $_.Exception.Message; exit 1 }}";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            if (!proc.WaitForExit(20000))
            {
                try { proc.Kill(); } catch { }
                _launchError = "상승 기동 시간 초과(UAC 무응답 가능)";
                return false;
            }

            if (proc.ExitCode == 0)
            {
                _launchError = null;
                return true;
            }

            var err = proc.StandardError.ReadToEnd().Trim();
            _launchError = string.IsNullOrEmpty(err)
                ? "상승이 거부되었습니다: UAC가 '알림 안 함'으로 설정되어 있을 수 있습니다. Windows 설정에서 UAC를 최소한 '앱이 변경 시도할 때만 알림'으로 조정하세요."
                : err;
            return false;
        }
        catch (Exception ex)
        {
            _launchError = ex.Message;
            return false;
        }
    }

    public async Task<ElevatedResponse> StartCoreAsync(string binaryPath, string workingDirectory, string configPath, string? secret, CancellationToken ct = default)
        => await SendAsync(new ElevatedRequest(ElevatedProtocol.Start, workingDirectory, configPath, binaryPath, secret), ct).ConfigureAwait(false);

    public async Task<ElevatedResponse> StopCoreAsync(CancellationToken ct = default)
        => await SendAsync(new ElevatedRequest(ElevatedProtocol.Stop), ct).ConfigureAwait(false);

    public async Task<ElevatedResponse> GetStatusAsync(CancellationToken ct = default)
        => await SendAsync(new ElevatedRequest(ElevatedProtocol.Status), ct).ConfigureAwait(false);

    public async Task<ElevatedResponse> ShutdownAsync(CancellationToken ct = default)
        => await SendAsync(new ElevatedRequest(ElevatedProtocol.Shutdown), ct).ConfigureAwait(false);

    private async Task<ElevatedResponse> SendAsync(ElevatedRequest request, CancellationToken ct)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", ElevatedProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);

            var json = JsonSerializer.Serialize(request, ElevatedProtocol.JsonOptions);
            await writer.WriteLineAsync(json).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                return new ElevatedResponse(false, "상승 호스트 무응답");

            return JsonSerializer.Deserialize<ElevatedResponse>(line, ElevatedProtocol.JsonOptions)
                   ?? new ElevatedResponse(false, "상승 호스트 응답 해석 불가");
        }
        catch (OperationCanceledException)
        {
            return new ElevatedResponse(false, "상승 호스트 연결 시간 초과");
        }
        catch (Exception ex)
        {
            return new ElevatedResponse(false, ex.Message);
        }
    }
}
