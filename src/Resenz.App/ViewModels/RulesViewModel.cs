using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Resenz.Core.Models;
using Resenz.Core.Services;

namespace Resenz.App.ViewModels;

public sealed partial class RuleRowViewModel
{
    public int Index { get; }
    public string Type { get; }
    public string Payload { get; }
    public string Proxy { get; }
    public long? HitCount { get; }

    /// <summary>적중 카운트 표시. 카운트 없으면 표시 "—".</summary>
    public string Size => HitCount?.ToString("N0") ?? "—";

    /// <summary>적중 카운트 존재 여부(Chip 표시용).</summary>
    public bool HasHit => HitCount is not null;

    /// <summary>최근 적중 시각(코어 size는 누적 횟수만 제공하며 시각은 없음. 이 필드는 미래 확장용으로 보존).</summary>
    public bool IsDisabled { get; set; }

    public RuleRowViewModel(RuleItem item)
    {
        Index = item.Index;
        Type = item.Type;
        Payload = item.Payload;
        Proxy = item.Proxy;
        HitCount = item.Size is null ? null : long.TryParse(item.Size, out var n) ? n : null;
    }
}

public sealed partial class RulesViewModel : ObservableObject
{
    private readonly ICoreManager _core;
    private readonly DispatcherQueue _dispatcher;
    private IReadOnlyList<RuleItem> _all = Array.Empty<RuleItem>();
    private readonly EventHandler<CoreStateChanged> _onStateChanged;

    public ObservableCollection<RuleRowViewModel> Items { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "아직 불러오지 않음";

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public RulesViewModel(ICoreManager core, DispatcherQueue dispatcher)
    {
        _core = core;
        _dispatcher = dispatcher;
        _onStateChanged = (_, e) =>
        {
            if (e.State != CoreState.Running) _all = Array.Empty<RuleItem>();
            else _ = AutoReloadAsync();
        };
    }

    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _core.StateChanged += _onStateChanged;
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _core.StateChanged -= _onStateChanged;
    }

    private async Task AutoReloadAsync()
    {
        await Task.Delay(500);
        await LoadAsync(force: true);
    }

    public async Task LoadAsync(bool force = false)
    {
        if (_core.Api is null || _core.State != CoreState.Running)
        {
            StatusText = "코어 미실행";
            return;
        }
        if (_all.Count > 0 && !force) return;

        IsBusy = true;
        try
        {
            _all = await _core.Api.GetRulesAsync().ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                Apply();
                StatusText = $"총 {_all.Count}줄 규칙";
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

    private void Apply()
    {
        var keyword = Filter?.Trim() ?? "";
        var rows = _all
            .Where(r => keyword.Length == 0
                || r.Payload.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || r.Type.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || r.Proxy.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Select(r => new RuleRowViewModel(r))
            .ToList();

        Items.Clear();
        foreach (var row in rows) Items.Add(row);
    }

    /// <summary>임시 비활성화된 규칙(실행 상태에서 제거, 설정 불변. 코어 설정 리로드나 재시작 후 복원).</summary>
    public IReadOnlyList<int> DisabledIndexes { get; private set; } = Array.Empty<int>();

    /// <summary>임시 비활성화 규칙 존재 여부(페이지 헤더 표시"전부 복원").</summary>
    public bool HasDisabled => DisabledIndexes.Count > 0;

    [RelayCommand]
    private async Task ToggleRuleAsync(RuleRowViewModel? row)
    {
        if (row is null || _core.Api is null) return;
        try
        {
            // 규칙 삭제 시 인덱스가 앞당겨지므로 먼저 비활성화한 뒤 전체 다시 가져오기로 인덱스를 새로 맞춤.
            await _core.Api.DeleteRuleAsync(row.Index).ConfigureAwait(false);
            var remaining = new List<int>(DisabledIndexes) { row.Index };
            DisabledIndexes = remaining;
            OnPropertyChanged(nameof(HasDisabled));
            await LoadAsync(force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => StatusText = $"비활성화 실패: {{ex.Message}");
        }
    }

    /// <summary>임시 비활성화 전부 복원: 코어 설정 리로드(비활성화된 규칙이 다시 적용됨).</summary>
    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        if (_core.Api is null || DisabledIndexes.Count == 0) return;
        try
        {
            await _core.ReloadConfigAsync(_core.Paths.RuntimeConfigPath).ConfigureAwait(false);
            DisabledIndexes = Array.Empty<int>();
            OnPropertyChanged(nameof(HasDisabled));
            await LoadAsync(force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => StatusText = $"복원 실패: {{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(force: true);

    partial void OnFilterChanged(string value) => Apply();
}
