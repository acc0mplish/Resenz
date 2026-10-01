using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Resenz.Core.Services;

namespace Resenz.App.ViewModels;

public sealed partial class ProfileRowViewModel : ObservableObject
{
    public long Id { get; }
    public string Name { get; }
    public string KindText { get; }
    public string? Source { get; }
    public string UpdatedText { get; }
    public string? SubscriptionUserInfo { get; }
    public bool IsFree { get; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    /// <summary>시간 행 표시 모드: false=갱신 시각(기본), true=만료 시각. 카드 시간 행 클릭으로 전환.</summary>
    [ObservableProperty]
    public partial bool ShowExpire { get; set; }

    /// <summary>카드 시간 행: 만료 시각 또는 갱신 시각, 클릭 전환.</summary>
    public string ToggleTimeText => ShowExpire
        ? (string.IsNullOrEmpty(ExpireText) ? "장기 유효" : ExpireText)
        : $"갱신: {UpdatedText}";

    public void ToggleTime() => ShowExpire = !ShowExpire;

    partial void OnShowExpireChanged(bool value) => OnPropertyChanged(nameof(ToggleTimeText));

    public string SourceText => string.IsNullOrEmpty(Source) ? "로컬" : Source!;

    public string TrafficText => FormatSubscriptionInfo(SubscriptionUserInfo);

    public bool HasTraffic => !string.IsNullOrEmpty(TrafficText);

    public double UsagePercent { get; }

    public bool HasUsageBar { get; }

    public string ExpireText { get; }

    public string UsageText { get; }

    public ProfileRowViewModel(ProfileItem item)
    {
        Id = item.Id;
        Name = item.Name;
        KindText = item.KindText;
        Source = item.Source;
        UpdatedText = item.UpdatedText;
        SubscriptionUserInfo = item.SubscriptionUserInfo;
        IsActive = item.IsActive;
        IsFree = item.IsFree;

        var info = SubscriptionUsage.Parse(item.SubscriptionUserInfo);
        if (info is not null && info.Total > 0)
        {
            UsagePercent = Math.Clamp(info.Used * 100.0 / info.Total, 0, 100);
            HasUsageBar = true;
            UsageText = $"{Format.Bytes(info.Used)} / {Format.Bytes(info.Total)}";
        }
        else if (info is not null)
        {
            UsageText = $"↑ {Format.Bytes(info.Upload)}  ↓ {Format.Bytes(info.Download)}";
        }
        else
        {
            UsageText = "";
        }

        ExpireText = info is null || info.Expire <= 0
            ? (info is null ? "" : "장기 유효")
            : $"만료 {DateTimeOffset.FromUnixTimeSeconds(info.Expire).LocalDateTime:yyyy-MM-dd}";
    }

    private static string FormatSubscriptionInfo(string? raw)
    {
        var info = SubscriptionUsage.Parse(raw);
        if (info is null) return "";
        return info.Total > 0
            ? $"{Format.Bytes(info.Used)} / {Format.Bytes(info.Total)}"
            : $"↑ {Format.Bytes(info.Upload)}  ↓ {Format.Bytes(info.Download)}";
    }
}

public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly ResenzHost _host;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<ProfileRowViewModel> Items { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasItems { get; set; }

    [ObservableProperty]
    public partial string ImportUrl { get; set; } = "";

    [ObservableProperty]
    public partial string ImportName { get; set; } = "";

    /// <summary>구독 주소가 바뀌면 이름을 비워 가져올 때 구독 응답 헤더의 이름을 기본값으로 쓰게 함.</summary>
    partial void OnImportUrlChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ImportName = "";
        }
    }

    [ObservableProperty]
    public partial bool AutoUpdate { get; set; } = true;

    [ObservableProperty]
    public partial bool IsEditorOpen { get; set; }

    [ObservableProperty]
    public partial bool IsEditorBusy { get; set; }

    [ObservableProperty]
    public partial string EditorTitle { get; set; } = "";

    [ObservableProperty]
    public partial string EditorText { get; set; } = "";

    [ObservableProperty]
    public partial string RuntimePreview { get; set; } = "";

    [ObservableProperty]
    public partial int EditorTabIndex { get; set; }

    public bool IsRawTab => EditorTabIndex == 0;

    public bool IsRuntimeTab => EditorTabIndex == 1;

    private ProfileRowViewModel? _editorRow;

    partial void OnEditorTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsRawTab));
        OnPropertyChanged(nameof(IsRuntimeTab));
        if (value == 1) _ = RefreshRuntimePreviewAsync();
    }

    public ProfilesViewModel(ResenzHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            AutoUpdate = await _host.Settings.GetBoolAsync("profile.autoUpdate", true);
            var list = await _host.Profiles.ListAsync();
            _dispatcher.TryEnqueue(() =>
            {
                Items.Clear();
                foreach (var item in list) Items.Add(new ProfileRowViewModel(item));
                HasItems = Items.Count > 0;
                StatusText = Items.Count == 0 ? "아직 설정 파일 없음" : $"총 {Items.Count}개 설정";
            });
        }
        catch (Exception ex)
        {
            StatusText = $"로드 실패: {{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportUrlAsync()
    {
        var url = ImportUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            StatusText = "구독 링크를 입력하세요";
            return;
        }

        IsBusy = true;
        StatusText = "구독 다운로드 중…";
        try
        {
            var item = await _host.Profiles.ImportFromUrlAsync(url, string.IsNullOrWhiteSpace(ImportName) ? null : ImportName);
            StatusText = $"'{item.Name}' 가져옴";
            ImportUrl = "";
            ImportName = "";
            // 가져오기 즉시 적용: runtime 설정 생성 후 코어 핫리로드하여 '활성 상태지만 노드 없음' 상태가 생기지 않게 함.
            await _host.ApplyActiveProfileAsync();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"가져오기 실패: {{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::Resenz.App.App.Main);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".yaml");
            picker.FileTypeFilter.Add(".yml");
            picker.FileTypeFilter.Add(".txt");

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            IsBusy = true;
            StatusText = "파일 가져오는 중…";
            var item = await _host.Profiles.ImportFromFileAsync(file.Path);
            StatusText = $"'{item.Name}' 가져옴";
            // 가져오기 즉시 적용: runtime 설정 생성 후 코어 핫리로드.
            await _host.ApplyActiveProfileAsync();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"가져오기 실패: {{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ActivateAsync(ProfileRowViewModel? row)
    {
        if (row is null) return;
        IsBusy = true;
        try
        {
            await _host.Profiles.SetActiveAsync(row.Id);
            StatusText = $"적용 중 {row.Name}…";
            var applied = await _host.ApplyActiveProfileAsync();
            StatusText = applied ? $"'{row.Name}' 프로필로 전환함" : "전환했으나 설정 적용 실패";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"전환 실패: {{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(ProfileRowViewModel? row)
    {
        if (row is null) return;
        row.IsRefreshing = true;
        IsBusy = true;
        StatusText = $"'{row.Name}' 새로고침 중…";
        try
        {
            var ok = await _host.Profiles.RefreshAsync(row.Id);
            if (ok)
            {
                if (row.IsActive) await _host.ApplyActiveProfileAsync();
                StatusText = $"'{row.Name}' 갱신함";
            }
            else
            {
                StatusText = $"새로고침 실패: 이 설정엔 구독 출처 없음";
            }
            // LoadAsync 목록을 다시 만들어 새로고침 상태가 자연 리셋됨.
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"새로고침 실패: {{ex.Message}";
        }
        finally
        {
            row.IsRefreshing = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ProfileRowViewModel? row)
    {
        if (row is null) return;
        try
        {
            await _host.Profiles.DeleteAsync(row.Id);
            StatusText = $"'{row.Name}' 삭제함";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"삭제 실패: {{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        IsBusy = true;
        var count = 0;
        try
        {
            foreach (var item in Items.Where(i => i.Source is not null && i.KindText == "구독 링크").ToList())
            {
                if (await _host.Profiles.RefreshAsync(item.Id)) count++;
            }
            await _host.ApplyActiveProfileAsync();
            StatusText = $"구독 {count}개 새로고침함";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"새로고침 실패: {{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleAutoUpdateAsync()
    {
        await _host.Settings.SetBoolAsync("profile.autoUpdate", AutoUpdate);
        StatusText = AutoUpdate ? "구독 자동 업데이트 켜짐(매시간)" : "구독 자동 업데이트 꺼짐";
    }

    [RelayCommand]
    private async Task RenameAsync(ProfileRowViewModel? row)
    {
        if (row is null) return;
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "설정 이름 변경",
            PrimaryButtonText = "확인",
            CloseButtonText = "취소",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
            XamlRoot = global::Resenz.App.App.Main?.Content?.XamlRoot,
        };
        var box = new Microsoft.UI.Xaml.Controls.TextBox { Text = row.Name };
        dialog.Content = box;
        var result = await dialog.ShowAsync();
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return;

        var name = box.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        await _host.Profiles.RenameAsync(row.Id, name);
        StatusText = $"이름 변경함: {name}";
        await LoadAsync();
    }

    /// <summary>페이지 내 설정 편집기 열기: 원본 YAML 로드, runtime 미리보기는 해당 탭으로 전환할 때 지연 생성.</summary>
    [RelayCommand]
    private async Task OpenEditorAsync(ProfileRowViewModel? row)
    {
        if (row is null) return;

        var content = await _host.Profiles.ReadContentAsync(row.Id);
        if (content is null)
        {
            StatusText = "설정 내용 읽기 불가";
            return;
        }

        _editorRow = row;
        EditorTitle = $"편집: {{row.Name}";
        EditorText = ToDisplayText(content);
        RuntimePreview = "";
        EditorTabIndex = 0;
        IsEditorOpen = true;
    }

    /// <summary>WinUI TextBox는 \r만 줄바꿈으로 인식: 구독 파일의 LF를 CRLF로 통일해야 정상 표시됨.</summary>
    private static string ToDisplayText(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    /// <summary>저장 시 LF로 되돌려 구독 원본 파일과 일치 유지.</summary>
    private static string ToStorageText(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n");

    [RelayCommand]
    private void CloseEditor()
    {
        IsEditorOpen = false;
        _editorRow = null;
        EditorText = "";
        RuntimePreview = "";
    }

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        if (_editorRow is null) return;
        var row = _editorRow;

        var ok = await _host.Profiles.SaveContentAsync(row.Id, ToStorageText(EditorText));
        if (!ok)
        {
            StatusText = "저장 실패";
            return;
        }

        if (row.IsActive)
        {
            await _host.ApplyActiveProfileAsync();
            StatusText = "저장 후 핫리로드 완료";
        }
        else
        {
            StatusText = "저장됨";
        }

        await LoadAsync();

        if (row.IsActive && EditorTabIndex == 1) await RefreshRuntimePreviewAsync();
    }

    /// <summary>runtime 설정 텍스트 생성: 구독 원본 내용에 전역 주입 항목(포트/DNS/TUN 등)을 겹쳐 씀.</summary>
    private async Task RefreshRuntimePreviewAsync()
    {
        if (_editorRow is null) return;

        IsEditorBusy = true;
        try
        {
            var options = await _host.BuildRuntimeOptionsAsync();
            RuntimePreview = ToDisplayText(MihomoConfigBuilder.BuildRuntimeYaml(EditorText, options));
        }
        catch (Exception ex)
        {
            RuntimePreview = $"runtime 설정 생성 불가: {ex.Message}";
        }
        finally
        {
            IsEditorBusy = false;
        }
    }
}
