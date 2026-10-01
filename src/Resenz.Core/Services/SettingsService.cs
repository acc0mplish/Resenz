using System.Diagnostics;
using Microsoft.Win32;
using Resenz.Data;

namespace Resenz.Core.Services;

public interface IStartupService
{
    bool IsEnabled();
    bool SetEnabled(bool enabled);
}

public sealed class StartupService : IStartupService
{
    private const string TaskName = "Resenz";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Resenz";

    // 메인 프로그램이 관리자 권한으로 실행되어 레지스트리 Run 키로는 상승 실행이 불가하므로 자동 시작은 예약 작업으로 전환
    // (RunLevel=Highest,로그인 시 최고 권한으로 조용히 실행, UAC 팝업 없음).

    public bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
                exe = Path.Combine(AppContext.BaseDirectory, "Resenz.exe");

            if (enabled)
            {
                // 기존 작업을 먼저 삭제한 뒤 생성: 로그인 시 최고 권한으로 실행(/RL HIGHEST), 조용히 시작.
                RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                var create = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --minimized\" " +
                             "/SC ONLOGON /RL HIGHEST /F";
                return RunSchtasks(create);
            }

            return RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        }
        catch
        {
            return false;
        }
    }

    private static bool RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(15000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

public interface ISettingsService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task<bool> GetBoolAsync(string key, bool fallback = false);
    Task SetBoolAsync(string key, bool value);
    Task<int> GetIntAsync(string key, int fallback);
    Task SetIntAsync(string key, int value);
    Task<T> GetEnumAsync<T>(string key, T fallback) where T : struct, Enum;
    Task SetEnumAsync<T>(string key, T value) where T : struct, Enum;
}

public sealed class SettingsService : ISettingsService
{
    private readonly SettingsRepository _repo;

    public SettingsService(ResenzDatabase database)
    {
        _repo = new SettingsRepository(database);
    }

    public Task<string?> GetAsync(string key) => _repo.GetAsync(key);

    public Task SetAsync(string key, string value) => _repo.SetAsync(key, value);

    public async Task<bool> GetBoolAsync(string key, bool fallback = false)
    {
        var raw = await _repo.GetAsync(key).ConfigureAwait(false);
        return raw is null ? fallback : raw == "true";
    }

    public Task SetBoolAsync(string key, bool value) => _repo.SetAsync(key, value ? "true" : "false");

    public async Task<int> GetIntAsync(string key, int fallback)
    {
        var raw = await _repo.GetAsync(key).ConfigureAwait(false);
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    public Task SetIntAsync(string key, int value) => _repo.SetAsync(key, value.ToString());

    public async Task<T> GetEnumAsync<T>(string key, T fallback) where T : struct, Enum
    {
        var raw = await _repo.GetAsync(key).ConfigureAwait(false);
        return Enum.TryParse<T>(raw, ignoreCase: true, out var value) ? value : fallback;
    }

    public Task SetEnumAsync<T>(string key, T value) where T : struct, Enum
        => _repo.SetAsync(key, value.ToString());
}
