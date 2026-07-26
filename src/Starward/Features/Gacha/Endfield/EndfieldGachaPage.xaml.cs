using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Starward.Frameworks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.Gacha.Endfield;

public sealed partial class EndfieldGachaPage : PageBase
{
    private static readonly Dictionary<string, string> CharacterPoolNames = new()
    {
        ["E_CharacterGachaPoolType_Special"] = "特许寻访",
        ["E_CharacterGachaPoolType_Joint"] = "辉光庆典",
        ["E_CharacterGachaPoolType_Standard"] = "基础寻访",
        ["E_CharacterGachaPoolType_Beginner"] = "启程寻访",
    };

    private readonly ILogger<EndfieldGachaPage> _logger = AppConfig.GetLogger<EndfieldGachaPage>();
    private readonly EndfieldGachaService _service = AppConfig.GetService<EndfieldGachaService>();
    private List<EndfieldGachaItem> _allItems = [];
    private CancellationTokenSource? _syncCancellationTokenSource;

    public EndfieldGachaPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<EndfieldGachaAccount> Accounts { get; } = [];

    public ObservableCollection<EndfieldGachaPoolOption> PoolOptions { get; } = [];

    public ObservableCollection<EndfieldGachaItem> DisplayItems { get; } = [];

    public EndfieldGachaAccount? SelectedAccount
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                LoadSelectedAccount();
            }
        }
    }

    public EndfieldGachaPoolOption? SelectedPool
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                UpdateDisplayItems();
            }
        }
    }

    public int SelectedRecordTypeIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                LoadSelectedAccount();
            }
        }
    }

    public bool IsSyncing { get; set => SetProperty(ref field, value); }

    public bool IsEmpty { get; set => SetProperty(ref field, value); } = true;

    public bool IsStatusOpen { get; set => SetProperty(ref field, value); }

    public string StatusText { get; set => SetProperty(ref field, value); } = "";

    public InfoBarSeverity StatusSeverity { get; set => SetProperty(ref field, value); } = InfoBarSeverity.Informational;

    public EndfieldGachaStats Stats { get; set => SetProperty(ref field, value); } = new();

    private string CurrentRecordType => SelectedRecordTypeIndex == 1 ? "weapon" : "char";

    protected override void OnLoaded()
    {
        LoadAccounts();
    }

    protected override void OnUnloaded()
    {
        _syncCancellationTokenSource?.Cancel();
        _syncCancellationTokenSource?.Dispose();
        _syncCancellationTokenSource = null;
    }

    private void LoadAccounts(string? selectedAccountKey = null)
    {
        selectedAccountKey ??= SelectedAccount?.AccountKey;
        List<EndfieldGachaAccount> accounts = _service.GetAccounts();
        Accounts.Clear();
        foreach (EndfieldGachaAccount account in accounts)
        {
            Accounts.Add(account);
        }
        SelectedAccount = Accounts.FirstOrDefault(x => x.AccountKey == selectedAccountKey) ?? Accounts.FirstOrDefault();
        if (SelectedAccount is null)
        {
            _allItems.Clear();
            PoolOptions.Clear();
            DisplayItems.Clear();
            Stats = new();
            IsEmpty = true;
        }
    }

    private void LoadSelectedAccount()
    {
        if (SelectedAccount is null)
        {
            return;
        }
        _allItems = _service.GetItems(SelectedAccount.AccountKey, CurrentRecordType);
        RefreshPoolOptions();
    }

    private void RefreshPoolOptions()
    {
        PoolOptions.Clear();
        PoolOptions.Add(new EndfieldGachaPoolOption { Key = "", Name = "全部卡池" });
        if (CurrentRecordType == "char")
        {
            foreach (string poolType in CharacterPoolNames.Keys.OrderBy(GetCharacterPoolOrder))
            {
                List<EndfieldGachaItem> typeItems = _allItems.Where(x => x.PoolType == poolType).ToList();
                if (typeItems.Count == 0)
                {
                    continue;
                }
                if (poolType == "E_CharacterGachaPoolType_Joint")
                {
                    foreach (IGrouping<string, EndfieldGachaItem> pool in typeItems
                        .GroupBy(x => x.PoolId)
                        .OrderByDescending(x => x.Max(item => ParseTimestamp(item.GachaTime))))
                    {
                        PoolOptions.Add(new EndfieldGachaPoolOption
                        {
                            Key = $"pool:{pool.Key}",
                            Name = pool.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.PoolName))?.PoolName ?? CharacterPoolNames[poolType],
                        });
                    }
                }
                else
                {
                    PoolOptions.Add(new EndfieldGachaPoolOption
                    {
                        Key = $"type:{poolType}",
                        Name = CharacterPoolNames[poolType],
                    });
                }
            }
        }
        else
        {
            IEnumerable<EndfieldGachaPoolOption> pools = _allItems
                .Where(x => !string.IsNullOrWhiteSpace(x.PoolId))
                .GroupBy(x => x.PoolId)
                .OrderByDescending(x => x.Max(item => ParseTimestamp(item.GachaTime)))
                .Select(x => new EndfieldGachaPoolOption
                {
                    Key = x.Key,
                    Name = x.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.PoolName))?.PoolName ?? x.Key,
                });
            foreach (EndfieldGachaPoolOption pool in pools)
            {
                PoolOptions.Add(pool);
            }
        }
        SelectedPool = PoolOptions.FirstOrDefault();
    }

    private void UpdateDisplayItems()
    {
        IEnumerable<EndfieldGachaItem> query = _allItems;
        if (!string.IsNullOrWhiteSpace(SelectedPool?.Key))
        {
            if (CurrentRecordType == "char" && SelectedPool.Key.StartsWith("type:", StringComparison.Ordinal))
            {
                string poolType = SelectedPool.Key[5..];
                query = query.Where(x => x.PoolType == poolType);
            }
            else if (CurrentRecordType == "char" && SelectedPool.Key.StartsWith("pool:", StringComparison.Ordinal))
            {
                string poolId = SelectedPool.Key[5..];
                query = query.Where(x => x.PoolId == poolId);
            }
            else
            {
                query = query.Where(x => x.PoolId == SelectedPool.Key);
            }
        }
        List<EndfieldGachaItem> items = query
            .OrderByDescending(x => ParseTimestamp(x.GachaTime))
            .ThenByDescending(x => x.SeqId.Length)
            .ThenByDescending(x => x.SeqId, StringComparer.Ordinal)
            .ToList();

        int pity;
        if (string.IsNullOrWhiteSpace(SelectedPool?.Key))
        {
            IEnumerable<IGrouping<string, EndfieldGachaItem>> groups = CurrentRecordType == "char"
                ? items.GroupBy(x => x.PoolType == "E_CharacterGachaPoolType_Joint" ? $"joint:{x.PoolId}" : x.PoolType)
                : items.GroupBy(x => x.PoolId);
            foreach (IGrouping<string, EndfieldGachaItem> group in groups)
            {
                CalculatePity(group.ToList());
            }
            pity = -1;
        }
        else
        {
            pity = CalculatePity(items);
        }

        DisplayItems.Clear();
        foreach (EndfieldGachaItem item in items)
        {
            DisplayItems.Add(item);
        }
        Stats = new EndfieldGachaStats
        {
            Total = items.Count,
            Count6 = items.Count(x => x.Rarity == 6),
            Count5 = items.Count(x => x.Rarity == 5),
            Count4 = items.Count(x => x.Rarity == 4),
            PityText = pity < 0 ? "-" : pity.ToString(CultureInfo.CurrentCulture),
        };
        IsEmpty = items.Count == 0;
    }

    private static int CalculatePity(List<EndfieldGachaItem> items)
    {
        int pity = 0;
        foreach (EndfieldGachaItem item in items
            .OrderBy(x => ParseTimestamp(x.GachaTime))
            .ThenBy(x => x.SeqId.Length)
            .ThenBy(x => x.SeqId, StringComparer.Ordinal))
        {
            bool excludesFreePull = item.RecordType == "char" &&
                item.PoolType is "E_CharacterGachaPoolType_Special" or "E_CharacterGachaPoolType_Joint" && item.IsFree;
            if (excludesFreePull)
            {
                item.Pity = 0;
                continue;
            }
            item.Pity = ++pity;
            if (item.Rarity == 6)
            {
                pity = 0;
            }
        }
        return pity;
    }

    private static long ParseTimestamp(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? result : 0;
    }

    private static int GetCharacterPoolOrder(string poolType) => poolType switch
    {
        "E_CharacterGachaPoolType_Special" => 0,
        "E_CharacterGachaPoolType_Joint" => 1,
        "E_CharacterGachaPoolType_Standard" => 2,
        "E_CharacterGachaPoolType_Beginner" => 3,
        _ => 4,
    };

    [RelayCommand]
    private async Task SyncGachaAsync()
    {
        if (IsSyncing)
        {
            return;
        }
        if (SelectedAccount is not null && _service.HasSavedLoginToken(SelectedAccount.AccountKey))
        {
            EndfieldGachaAccount account = SelectedAccount;
            await ExecuteSyncAsync((progress, token) => _service.SyncAccountAsync(account, progress, token));
            return;
        }
        await LoginAccountInternalAsync();
    }

    [RelayCommand]
    private async Task LoginAccountAsync()
    {
        if (!IsSyncing)
        {
            await LoginAccountInternalAsync();
        }
    }

    private async Task LoginAccountInternalAsync()
    {
        try
        {
            var dialog = new EndfieldLoginDialog { XamlRoot = XamlRoot };
            await dialog.ShowAsync();
            if (!string.IsNullOrWhiteSpace(dialog.LoginToken))
            {
                await AddAccountAndSyncAsync(dialog.LoginToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to login Hypergryph account: {ErrorType}", ex.GetType().Name);
            StatusSeverity = InfoBarSeverity.Error;
            StatusText = ex.Message;
            IsStatusOpen = true;
        }
    }

    [RelayCommand]
    private async Task InputLoginTokenAsync()
    {
        if (IsSyncing)
        {
            return;
        }
        var passwordBox = new PasswordBox
        {
            MinWidth = 360,
            PlaceholderText = "登录 Token",
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var dialog = new ContentDialog
        {
            Title = "输入鹰角账号登录 Token",
            Content = passwordBox,
            PrimaryButtonText = "确定",
            SecondaryButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(passwordBox.Password))
        {
            await AddAccountAndSyncAsync(passwordBox.Password);
        }
    }

    private async Task AddAccountAndSyncAsync(string loginToken)
    {
        PrepareSync();
        try
        {
            var progress = new Progress<string>(text => StatusText = text);
            List<EndfieldGachaAccount> accounts = await _service.AddAccountsFromLoginTokenAsync(
                loginToken, progress, _syncCancellationTokenSource!.Token);
            EndfieldGachaAccount account = accounts[0];
            LoadAccounts(account.AccountKey);
            EndfieldGachaSyncResult result = await _service.SyncAccountAsync(
                account, progress, _syncCancellationTokenSource.Token);
            LoadAccounts(result.Account.AccountKey);
            ShowSyncResult(result);
        }
        catch (OperationCanceledException)
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusText = "同步已取消。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to add Endfield account: {ErrorType}", ex.GetType().Name);
            StatusSeverity = InfoBarSeverity.Error;
            StatusText = ex.Message;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private async Task SyncFromGameLogAsync()
    {
        if (!IsSyncing)
        {
            await ExecuteSyncAsync((progress, token) => _service.SyncFromGameLogAsync(progress, token));
        }
    }

    private async Task ExecuteSyncAsync(
        Func<IProgress<string>, CancellationToken, Task<EndfieldGachaSyncResult>> syncOperation)
    {
        PrepareSync();
        try
        {
            var progress = new Progress<string>(text => StatusText = text);
            EndfieldGachaSyncResult result = await syncOperation(progress, _syncCancellationTokenSource!.Token);
            LoadAccounts(result.Account.AccountKey);
            ShowSyncResult(result);
        }
        catch (OperationCanceledException)
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusText = "同步已取消。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to sync Endfield gacha records: {ErrorType}", ex.GetType().Name);
            StatusSeverity = InfoBarSeverity.Error;
            StatusText = ex.Message;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void PrepareSync()
    {
        _syncCancellationTokenSource?.Dispose();
        _syncCancellationTokenSource = new CancellationTokenSource();
        IsSyncing = true;
        IsStatusOpen = true;
        StatusSeverity = InfoBarSeverity.Informational;
        StatusText = "正在准备同步";
    }

    private void ShowSyncResult(EndfieldGachaSyncResult result)
    {
        if (result.FailedPools.Length == 0)
        {
            StatusSeverity = InfoBarSeverity.Success;
            StatusText = $"同步完成：新增 {result.NewCount} 条，共 {result.TotalCount} 条记录。";
        }
        else
        {
            StatusSeverity = InfoBarSeverity.Warning;
            StatusText = $"同步完成，但以下卡池获取失败：{string.Join("、", result.FailedPools)}。请稍后重试。";
        }
    }

    [RelayCommand]
    private void CancelSync()
    {
        _syncCancellationTokenSource?.Cancel();
    }
}
