using PKHeX.Core;
using PKHeX.Core.Searching;
using SysBot.Base;
using SysBot.Base.Util;
using SysBot.Pokemon.Helpers;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.PokeDataOffsetsPLZA;
using static SysBot.Pokemon.TradeHub.SpecialRequests;

namespace SysBot.Pokemon;

// ReSharper disable once ClassWithVirtualMembersNeverInherited.Global
public class PokeTradeBotPLZA(PokeTradeHub<PA9> Hub, PokeBotState Config) : PokeRoutineExecutor9PLZA(Config), ICountBot, ITradeBot
{
    public readonly TradeAbuseSettings AbuseSettings = Hub.Config.TradeAbuse;

    /// <summary>
    /// Folder to dump received trade data to.
    /// </summary>
    /// <remarks>If null, will skip dumping.</remarks>
    private readonly FolderSettings DumpSetting = Hub.Config.Folder;

    private readonly TradeSettings TradeSettings = Hub.Config.Trade;
    public event Action<int>? TradeProgressChanged;
    private uint DisplaySID;
    private uint DisplayTID;

    private string OT = string.Empty;
    private bool StartFromOverworld = true;
    private ulong? _cachedBoxOffset;
    private ulong TradePartnerStatusOffset;
    private bool _wasConnectedToPartner = false;
    private int _consecutiveConnectionFailures = 0; // Track consecutive online connection failures for soft ban detection

    // 批量交易动态信息字段（用于msg.txt生成，需初始化）修改点
    private int _currentBatchNum = 0; // 当前交易序号（第N只）
    private int _totalBatchCount = 0; // 批量交易总数量
    private string _currentPokemonNameZh = string.Empty; // 当前赠送宝可梦中文名称
    private string _currentHeldItemZh = string.Empty; // 当前宝可梦中文持有物
    private int _currentTradeCode = 0; // 当前交易链接码
    private bool _isBatchTrade = false; // 是否处于批量交易状态（用于区分普通/
    // 新增：共享变量 - 存储当前交易详情和训练师NID（解决未定义错误）
    private PokeTradeDetail<PA9>? _currentTradeDetail; // 对应原来的poke变量
    private ulong _currentTrainerNID; // 对应原来的trainerNID变量（类型为ulong，与代码中一致）
    public event EventHandler<Exception>? ConnectionError;

    public event EventHandler? ConnectionSuccess;

    // Progress bar states
    private enum TradeState
    {
        Idle,               // No trade active
        Starting,           // Command received
        EnteringCode,       // Link code input
        WaitingForPartner,  // Searching
        PartnerFound,       // Partner detected
        Confirming,         // Confirming trade
        Trading,            // Trade animation running
        Completed,          // Trade done successfully
        Failed              // Trade aborted / error
    }

    private TradeState _tradeState = TradeState.Idle;
    private int _lastProgress = -1;

    private void SetTradeState(TradeState newState)
    {
        if (_tradeState == newState)
            return;

        _tradeState = newState;

        // 原有进度计算逻辑（保持不变，无需修改）修改点
        int progress = newState switch
        {
            TradeState.Idle => 0,
            TradeState.Starting => 5,
            TradeState.EnteringCode => 15,
            TradeState.WaitingForPartner => 30,
            TradeState.PartnerFound => 45,
            TradeState.Confirming => 65,
            TradeState.Trading => 85,
            TradeState.Completed => 100,
            TradeState.Failed => 0,
            _ => _lastProgress
        };

        if (progress < _lastProgress && newState != TradeState.Idle)
            return;

        _lastProgress = progress;
        TradeProgressChanged?.Invoke(progress);

        // 新增：调用msg.txt更新方法（核心整合点，原有代码添加这一行即可）
        UpdateMsgTxtByTradeState(newState);
    }
    private void UpdateMsgTxtByTradeState(TradeState currentState)
    {
        // 1. 获取北京时间时区（保留原逻辑，保证时间精准）
        TimeZoneInfo beijingTimeZone;
        try
        {
            beijingTimeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch
        {
            try
            {
                beijingTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
            }
            catch
            {
                beijingTimeZone = TimeZoneInfo.CreateCustomTimeZone("Beijing Time", TimeSpan.FromHours(8), "北京时间", "北京时间");
            }
        }

        // 2. 初始化变量，准备拼接消息（全程做空值保护）
        StringBuilder fullMsgBuilder = new StringBuilder();
        string lastSeenTip = string.Empty;
        string nextMeetTimeStr = string.Empty;
        DateTime currentBeijingTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, beijingTimeZone);

        // 3. 个性化信息拼接（做空值保护，_currentTradeDetail为null时不执行个性化逻辑）
        if (_currentTradeDetail != null) // 此处仅为优化性能，非必须（可替换为三元表达式）
        {
 
            TimeSpan delta = TimeSpan.Zero;
            // 空值保护：使用?.判断_currentTradeDetail是否为null
            var isDistribution = _currentTradeDetail?.Type == PokeTradeType.Random;
            var list = isDistribution ? PreviousUsersDistribution : PreviousUsers;
            // 空值保护：_currentTrainerNID若依赖_currentTradeDetail，需额外判断
            var previous = list?.TryGetPreviousNID(_currentTrainerNID);
            if (previous != null)
            {
                DateTime previousBeijingTime = TimeZoneInfo.ConvertTime(previous.Time, beijingTimeZone);
                delta = currentBeijingTime - previousBeijingTime;
                lastSeenTip = $"上次见到你{delta.TotalMinutes:F1}分钟之前";
            }
            else
            {
                lastSeenTip = "这是第一次见到你哦～";
            }
            var cd = AbuseSettings.TradeCooldown;
            DateTime nextMeetTime = currentBeijingTime.AddMinutes(cd);
            nextMeetTimeStr = nextMeetTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
 
        // 4. 基础状态提示（统一逻辑，无需if/else，自动兼容有无详情场景）
        string baseMsg = currentState switch
        {
            TradeState.Idle => "等待交易...",
            TradeState.Starting => "初始化...",
            // 优化：即使初始为0，后续赋值后刷新也能显示有效码
            TradeState.EnteringCode => _currentTradeCode != 0
                ? $"输入交换码"
                : "输入交换码",
            TradeState.WaitingForPartner => "搜索中",
            // 核心：_currentTradeDetail为null时，仅显示基础文本；非null时显示完整个性化提示
            TradeState.PartnerFound => _currentTradeDetail != null
                ? $"找到伙伴，加载信息...\r\n{lastSeenTip}\r\n下次见到你是北京时间：{nextMeetTimeStr}"
                : "找到伙伴，加载信息...",
            TradeState.Confirming => $"确认交换，验证中.",
            TradeState.Trading => "交易中，请勿中断...",
            TradeState.Completed => _currentTradeDetail != null && _isBatchTrade && _totalBatchCount > 0
                ? $"【批量】第{_currentBatchNum}/{_totalBatchCount}只完成\r\n下次见到你是北京时间：\r\n{nextMeetTimeStr}"
                : "交易完成，返回主界面...",
            TradeState.Failed => _currentTradeDetail != null && _isBatchTrade
                ? "【批量】交换失败，中止流程..."
                : "交易失败，恢复初始状态...",
            _ => "交易进行中..."
        };

        // 5. 批量交易补充信息（做空值保护，自动兼容）
        fullMsgBuilder.Append(baseMsg);
        if (_isBatchTrade && _totalBatchCount > 0 && _currentTradeDetail != null)
        {
            fullMsgBuilder.AppendLine();
            fullMsgBuilder.AppendLine("====");
            fullMsgBuilder.AppendLine($"【{_currentBatchNum}/{_totalBatchCount}】");
            fullMsgBuilder.AppendLine($"赠送精灵: {_currentPokemonNameZh}");
            fullMsgBuilder.AppendLine($"道具: {_currentHeldItemZh}");
        }

        // 6. 统一写入文件（仅一个写入入口，无任何if/else分支）
        string finalMsg = fullMsgBuilder.ToString();
        try
        {
            File.WriteAllText("msg.txt", finalMsg, Encoding.UTF8);
            Log($"成功写入msg.txt，最终内容：\n{finalMsg}");
        }
        catch (IOException ex)
        {
            Log($"更新msg.txt失败：{ex.Message}");
        }
    }
    /// <summary>
    /// 重置批量交易动态字段（避免残留影响后续普通交易）
    /// </summary>
    private void ResetBatchMsgFields()
    {
        _currentBatchNum = 0;
        _totalBatchCount = 0;
        _currentPokemonNameZh = string.Empty;
        _currentHeldItemZh = string.Empty;
        _currentTradeCode = 0;
        _isBatchTrade = false;

        // 新增：重置共享变量，避免残留数据
        _currentTradeDetail = null;
        _currentTrainerNID = 0;
    }

    public ICountSettings Counts => TradeSettings;

    /// <summary>
    /// Tracks failed synchronized starts to attempt to re-sync.
    /// </summary>
    public int FailedBarrier { get; private set; }

    /// <summary>
    /// Synchronized start for multiple bots.
    /// </summary>
    public bool ShouldWaitAtBarrier { get; private set; }

    #region Lifecycle & Main Loop

    public override Task HardStop()
    {
        UpdateBarrier(false);
        return CleanExit(CancellationToken.None);
    }

    public override async Task MainLoop(CancellationToken token)
    {
        try
        {
            // Ensure cache is clean on startup
            _cachedBoxOffset = null;
            _wasConnectedToPartner = false;
            _consecutiveConnectionFailures = 0;

            Hub.Queues.Info.CleanStuckTrades();
            await InitializeHardware(Hub.Config.Trade, token).ConfigureAwait(false);

            Log("Connecting to console...");
            var sav = await IdentifyTrainer(token).ConfigureAwait(false);
            OT = sav.OT;
            DisplaySID = sav.DisplaySID;
            DisplayTID = sav.DisplayTID;
            RecentTrainerCache.SetRecentTrainer(sav);
            OnConnectionSuccess();

            StartFromOverworld = true;

            Log("Initializing bot...");
            if (!await CheckIfOnOverworld(token).ConfigureAwait(false))
            {
                if (!await RecoverToOverworld(token).ConfigureAwait(false))
                {
                    Log("Restarting game...");
                    
                    await RestartGamePLZA(token).ConfigureAwait(false);
                    await Task.Delay(5_000, token).ConfigureAwait(false);

                    if (!await CheckIfOnOverworld(token).ConfigureAwait(false))
                    {
                        Log("Failed to start. Please restart the bot.");
                        throw new Exception("Unable to reach overworld. Bot cannot start trading.");
                    }
                }
            }

            Log("Bot ready. Waiting for trades...");
            await InnerLoop(sav, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            OnConnectionError(e);
            throw;
        }

        Log($"Ending {nameof(PokeTradeBotPLZA)} loop.");
        await HardStop().ConfigureAwait(false);
    }

    public override async Task RebootAndStop(CancellationToken t)
    {
        Hub.Queues.Info.CleanStuckTrades();
        await Task.Delay(2_000, t).ConfigureAwait(false);
        await ReOpenGame(Hub.Config, t).ConfigureAwait(false);
        _cachedBoxOffset = null; // Invalidate box offset cache after reboot
        await HardStop().ConfigureAwait(false);
        await Task.Delay(2_000, t).ConfigureAwait(false);
        if (!t.IsCancellationRequested)
        {
            Log("Restarting the main loop.");
            await MainLoop(t).ConfigureAwait(false);
        }
    }

    #endregion

    #region Enums

    protected enum TradePartnerWaitResult
    {
        Success,
        Timeout,
        KickedToMenu
    }

    protected enum LinkCodeEntryResult
    {
        Success,
        VerificationFailedMismatch
    }

    #endregion

    #region Trade Queue Management

    protected virtual (PokeTradeDetail<PA9>? detail, uint priority) GetTradeData(PokeRoutineType type)
    {
        string botName = Connection.Name;

        // First check the specific type's queue
        if (Hub.Queues.TryDequeue(type, out var detail, out var priority, botName))
        {
            return (detail, priority);
        }

        // If we're doing FlexTrade, also check the Batch queue
        if (type == PokeRoutineType.FlexTrade)
        {
            if (Hub.Queues.TryDequeue(PokeRoutineType.Batch, out detail, out priority, botName))
            {
                return (detail, priority);
            }
        }

        if (Hub.Queues.TryDequeueLedy(out detail))
        {
            return (detail, PokeTradePriorities.TierFree);
        }
        return (null, PokeTradePriorities.TierFree);
    }

    #endregion

    #region Trade Partner Detection

    // Upon connecting, their Nintendo ID will instantly update.
    protected virtual async Task<TradePartnerWaitResult> WaitForTradePartner(CancellationToken token)
    {
        Log("Waiting to connect to user before initializing trade process...");
        SetTradeState(TradeState.WaitingForPartner);

        // Initial delay to let the game populate NID pointer in memory
        await Task.Delay(2_000, token).ConfigureAwait(false);

        int maxWaitMs = Hub.Config.Trade.TradeConfiguration.TradeWaitTime * 1_000;
        int elapsed = 2_000; // Already waited 3 seconds above

        while (elapsed < maxWaitMs)
        {
            // Check if we've entered the trade box - this confirms a partner is connected
            if (!await IsOnMenu(MenuState.InBox, token).ConfigureAwait(false))
            {
                // Check if we got kicked back to overworld/menu
                var menuState = await GetMenuState(token).ConfigureAwait(false);
                if (menuState == MenuState.Overworld || menuState == MenuState.XMenu)
                {
                    Log("Connection interrupted. Restarting...");
                    return TradePartnerWaitResult.KickedToMenu;
                }

                await Task.Delay(100, token).ConfigureAwait(false);
                elapsed += 100;
                continue;
            }

            // We're in the box - wait a moment then validate the status pointer
            await Task.Delay(500, token).ConfigureAwait(false);
            elapsed += 500;


            // Set the offset for trade partner status monitoring
            var (valid, statusOffset) = await ValidatePointerAll(Offsets.TradePartnerStatusPointer, token).ConfigureAwait(false);
            if (!valid)
                continue; // Keep trying until pointer is valid

            Log("Trade partner detected!");

            _wasConnectedToPartner = true;
            TradePartnerStatusOffset = statusOffset;
            return TradePartnerWaitResult.Success;
        }

        Log("Timed out waiting for trade partner.");
        SetTradeState(TradeState.Failed);
        return TradePartnerWaitResult.Timeout;
    }

    #endregion

    #region AutoOT Features

    private static void ApplyTrainerInfo(PA9 pokemon, TradePartnerStatusPLZA partner)
    {
        pokemon.OriginalTrainerGender = (byte)partner.Gender;
        pokemon.TrainerTID7 = (uint)Math.Abs(partner.DisplayTID);
        pokemon.TrainerSID7 = (uint)Math.Abs(partner.DisplaySID);
        pokemon.OriginalTrainerName = partner.OT;
    }

    private async Task<PA9> ApplyAutoOT(PA9 toSend, TradePartnerStatusPLZA tradePartner, SAV9ZA sav, CancellationToken token)
    {
        // Sanity check: if trade partner OT is empty, skip AutoOT
        if (string.IsNullOrWhiteSpace(tradePartner.OT))
        {
            return toSend;
        }

        if (toSend.Version == GameVersion.GO)
        {
            var goClone = toSend.Clone();
            goClone.OriginalTrainerName = tradePartner.OT;

            ClearOTTrash(goClone, tradePartner);

            if (!toSend.ChecksumValid)
                goClone.RefreshChecksum();

            var boxOffset = await GetBoxStartOffset(token).ConfigureAwait(false);
            await SetBoxPokemonAbsolute(boxOffset, goClone, token, sav).ConfigureAwait(false);
            return goClone;
        }

        if (toSend is IHomeTrack pk && pk.HasTracker)
        {
            return toSend;
        }

        if (toSend.Generation != toSend.Format)
        {
            return toSend;
        }

        bool isMysteryGift = toSend.FatefulEncounter;
        var cln = toSend.Clone();

        // Apply trainer info (OT, TID, SID, Gender)
        ApplyTrainerInfo(cln, tradePartner);

        if (!isMysteryGift)
        {
            // Validate language ID - if invalid, default to English (2)
            int language = tradePartner.Language;
            if (language < 1 || language > 12) // Valid language IDs are 1-12
                language = 2; // English
            cln.Language = language;
        }

        ClearOTTrash(cln, tradePartner);

        // Hard-code version to ZA since PLZA only has one game version
        cln.Version = GameVersion.ZA;

        // Set nickname to species name in the Pokemon's language using PKHeX's method
        // This properly handles generation-specific formatting and language-specific names
        if (!toSend.IsNicknamed)
            cln.ClearNickname();

        // Clear handler info - make it look like trade partner is OT and never traded it
        cln.CurrentHandler = 0; // 0 = OT is current handler

        if (toSend.IsShiny)
            cln.PID = (uint)((cln.TID16 ^ cln.SID16 ^ (cln.PID & 0xFFFF) ^ toSend.ShinyXor) << 16) | (cln.PID & 0xFFFF);

        cln.RefreshChecksum();

        var tradeSV = new LegalityAnalysis(cln);

        if (tradeSV.Valid)
        {
            // Don't pass sav - we've already set handler info and don't want UpdateHandler to overwrite it
            var boxOffset = await GetBoxStartOffset(token).ConfigureAwait(false);
            await SetBoxPokemonAbsolute(boxOffset, cln, token, null).ConfigureAwait(false);
            return cln;
        }
        else
        {
            if (toSend.Species != 0)
            {
                var boxOffset = await GetBoxStartOffset(token).ConfigureAwait(false);
                await SetBoxPokemonAbsolute(boxOffset, toSend, token, sav).ConfigureAwait(false);
            }
            return toSend;
        }
    }

    private static void ClearOTTrash(PA9 pokemon, TradePartnerStatusPLZA tradePartner)
    {
        Span<byte> trash = pokemon.OriginalTrainerTrash;
        trash.Clear();
        string name = tradePartner.OT;
        int maxLength = trash.Length / 2;
        int actualLength = Math.Min(name.Length, maxLength);
        for (int i = 0; i < actualLength; i++)
        {
            char value = name[i];
            trash[i * 2] = (byte)value;
            trash[(i * 2) + 1] = (byte)(value >> 8);
        }
        if (actualLength < maxLength)
        {
            trash[actualLength * 2] = 0x00;
            trash[(actualLength * 2) + 1] = 0x00;
        }
    }

    #endregion

    #region Trade Confirmation

    private async Task<PokeTradeResult> ConfirmAndStartTrading(PokeTradeDetail<PA9> detail, uint checksumBeforeTrade, CancellationToken token)
    {
        var boxOffset = await GetBoxStartOffset(token).ConfigureAwait(false);
        var oldEC = await SwitchConnection.ReadBytesAbsoluteAsync(boxOffset, 8, token).ConfigureAwait(false);

        await Click(A, 3_000, token).ConfigureAwait(false);

        bool warningSent = false;
        int maxTime = Hub.Config.Trade.TradeConfiguration.MaxTradeConfirmTime;

        for (int i = 0; i < maxTime; i++)
        {
            // Check if we're still in trade box (partner disconnected if not in InBox menu state)
            if (!await IsOnMenu(MenuState.InBox, token).ConfigureAwait(false))
            {
                Log("No longer in trade box - partner declined and exited during offering stage.");
                SetTradeState(TradeState.Failed);
                detail.SendNotification(this, "Trade partner declined or disconnected.");
                return PokeTradeResult.NoTrainerFound;
            }

            await Click(A, 1_000, token).ConfigureAwait(false);

            // Send warning 10 seconds before timeout
            if (!warningSent && i == maxTime - 10 && maxTime >= 10)
            {
                detail.SendNotification(this, "Hey! Pick a Pokemon to trade or I am leaving!");
                warningSent = true;
            }

            var newEC = await SwitchConnection.ReadBytesAbsoluteAsync(boxOffset, 8, token).ConfigureAwait(false);
            if (!newEC.SequenceEqual(oldEC))
            {
                Log("Trade started!");
                SetTradeState(TradeState.Trading);
                return PokeTradeResult.Success;
            }
        }
        return PokeTradeResult.TrainerTooSlow;
    }

    #endregion

    #region Online Connection & Portal

    private async Task<bool> ConnectAndEnterPortal(CancellationToken token)
    {
        if (!await CheckIfOnOverworld(token).ConfigureAwait(false))
            await RecoverToOverworld(token).ConfigureAwait(false);

        await Click(X, 3_000, token).ConfigureAwait(false); // Load Menu

        await Click(DUP, 1_000, token).ConfigureAwait(false);
        await Click(A, 2_000, token).ConfigureAwait(false);
        await Click(DRIGHT, 1_000, token).ConfigureAwait(false);
        await Click(DRIGHT, 1_000, token).ConfigureAwait(false);
        await Click(A, 1_000, token).ConfigureAwait(false);
        await Click(DRIGHT, 1_000, token).ConfigureAwait(false);

        bool wasAlreadyConnected = await CheckIfConnectedOnline(token).ConfigureAwait(false);

        if (wasAlreadyConnected)
        {
            await Click(A, 1_000, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
            await Task.Delay(1_000, token).ConfigureAwait(false);
            _consecutiveConnectionFailures = 0;
        }
        else
        {
            await Click(A, 1_000, token).ConfigureAwait(false);

            int attempts = 0;
            while (!await CheckIfConnectedOnline(token).ConfigureAwait(false))
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                if (++attempts > 30)
                {
                    _consecutiveConnectionFailures++;
                    Log($"Failed to connect online. Consecutive failures: {_consecutiveConnectionFailures}");

                    if (_consecutiveConnectionFailures >= 3)
                    {
                        Log("Soft ban detected (3 consecutive connection failures). Waiting 30 minutes...");
                        await Task.Delay(30 * 60 * 1000, token).ConfigureAwait(false);
                        Log("30 minute wait complete. Resuming operations.");
                        _consecutiveConnectionFailures = 0;
                    }

                    return false;
                }
            }
            await Task.Delay(8_000 + Hub.Config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
            Log("Connected online.");
            _consecutiveConnectionFailures = 0;

            await Click(A, 1_000, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
            await Task.Delay(3_000, token).ConfigureAwait(false);
        }

        return true;
    }

    #endregion

    #region Trade Queue Processing

    private async Task DoNothing(CancellationToken token)
    {
        Log("Waiting for a user to begin trading...");
        SetTradeState(TradeState.Idle);
        while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.Idle)
            await Task.Delay(1_000, token).ConfigureAwait(false);
    }

    private async Task DoTrades(SAV9ZA sav, CancellationToken token)
    {
        var type = Config.CurrentRoutineType;
        int waitCounter = 0;
        while (!token.IsCancellationRequested && Config.NextRoutineType == type)
        {
            var (detail, priority) = GetTradeData(type);
            if (detail is null)
            {
                await WaitForQueueStep(waitCounter++, token).ConfigureAwait(false);
                continue;
            }
            waitCounter = 0;

            detail.IsProcessing = true;
            Log($"Entering X-Menu and selecting Link Trade...");
            SetTradeState(TradeState.Idle);
            SetTradeState(TradeState.Starting);
            Hub.Config.Stream.StartTrade(this, detail, Hub);
            Hub.Queues.StartTrade(this, detail);

            await PerformTrade(sav, detail, type, priority, token).ConfigureAwait(false);
        }
    }

    #endregion

    #region Navigation and Recovery

    private async Task DisconnectFromTrade(CancellationToken token)
    {
        Log("Disconnecting from trade...");
        SetTradeState(TradeState.Failed);

        // Check if we're still in the trade box (connected) or kicked to menu
        var menuState = await GetMenuState(token).ConfigureAwait(false);

        if (menuState == MenuState.InBox)
        {
            // Still in trade box - press B+A to disconnect
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
        }
        else
        {
            // Already kicked to menu - only press B to navigate back
            await Click(B, 0_500, token).ConfigureAwait(false);
        }
    }

    private async Task ExitTradeToOverworld(bool unexpected, CancellationToken token)
    {
        if (unexpected)
            Log("Unexpected behavior, recovering to overworld.");
        SetTradeState(TradeState.Failed);

        if (await CheckIfOnOverworld(token).ConfigureAwait(false))
        {
            StartFromOverworld = true;
            _wasConnectedToPartner = false; // Reset flag when successfully back to overworld
            return;
        }

        // Use MenuState to determine whether to disconnect or navigate back
        int timeoutSeconds = 30;
        int elapsedExit = 0;

        // If we're in the Box or searching for a Link Trade, we need to use the BAB approach, otherwise we can just mash B.
        var remainMs = 120_000;
        while (await GetMenuState(token).ConfigureAwait(false) >= MenuState.LinkTrade)
        {
            if (remainMs < 0)
            {
                StartFromOverworld = true;
                _wasConnectedToPartner = false; // Reset flag when successfully back to overworld
                return;
            }

            await Click(B, 1_000, token).ConfigureAwait(false);
            if (await GetMenuState(token).ConfigureAwait(false) < MenuState.LinkTrade)
                break;

            var box = await IsOnMenu(MenuState.InBox, token).ConfigureAwait(false);
            await Click(box ? A : B, 1_000, token).ConfigureAwait(false);
            if (await GetMenuState(token).ConfigureAwait(false) < MenuState.LinkTrade)
                break;

            await Click(B, 1_000, token).ConfigureAwait(false);
            if (await GetMenuState(token).ConfigureAwait(false) < MenuState.LinkTrade)
                break;
            remainMs -= 3_000;
        }

        // From here, we should be able to press B to get to overworld.
        while (!await CheckIfOnOverworld(token).ConfigureAwait(false))
            await Click(B, 0_200, token).ConfigureAwait(false);

        Log("Returned to overworld.");
        SetTradeState(TradeState.Failed);
        StartFromOverworld = true;
        _wasConnectedToPartner = false;
    }

    #endregion

    #region Game State & Data Access

    private async Task<TradePartnerStatusPLZA> GetTradePartnerFullInfo(CancellationToken token)
    {
        var baseAddr = await SwitchConnection.PointerAll(Offsets.LinkTradePartnerDataPointer, token).ConfigureAwait(false);
        var nidAddr = baseAddr + TradePartnerNIDShift;
        var tidAddr = baseAddr + TradePartnerTIDShift;

        // Read chunk starting from NID location - includes NID, TID at +0x44, and OT at +0x4C
        var chunk = await SwitchConnection.ReadBytesAbsoluteAsync(nidAddr, 0x69, token).ConfigureAwait(false);
        var nid = BitConverter.ToUInt64(chunk.AsSpan(0, 8));
        var dataIsLoaded = chunk[0x68] != 0;

        var trader_info = new TradePartnerStatusPLZA();

        if (dataIsLoaded)
        {
            var tid = chunk.AsSpan(0x44, 4).ToArray();
            var ot = chunk.AsSpan(0x4C, TradePartnerPLZA.MaxByteLengthStringObject).ToArray();
            tid.CopyTo(trader_info.Data, 0x00);
            ot.CopyTo(trader_info.Data, 0x08);

            // Read gender and language from TID location offset
            var genderLang = await SwitchConnection.ReadBytesAbsoluteAsync(tidAddr, 0x08, token).ConfigureAwait(false);
            trader_info.Data[0x04] = genderLang[0x04]; // Gender at TID base + 0x04
            trader_info.Data[0x05] = genderLang[0x05]; // Language at TID base + 0x05
        }
        else
        {
            // Data not at primary location, use fallback
            var fallbackTidAddr = tidAddr + FallBackTradePartnerDataShift;
            var fallbackChunk = await SwitchConnection.ReadBytesAbsoluteAsync(fallbackTidAddr, 34, token).ConfigureAwait(false);

            var tid = fallbackChunk.AsSpan(0, 4).ToArray();
            var ot = fallbackChunk.AsSpan(0x08, TradePartnerPLZA.MaxByteLengthStringObject).ToArray();
            tid.CopyTo(trader_info.Data, 0x00);
            ot.CopyTo(trader_info.Data, 0x08);

            // Read gender and language from fallback TID location
            var genderLang = await SwitchConnection.ReadBytesAbsoluteAsync(fallbackTidAddr, 0x08, token).ConfigureAwait(false);
            trader_info.Data[0x04] = genderLang[0x04]; // Gender at fallback TID + 0x04
            trader_info.Data[0x05] = genderLang[0x05]; // Language at fallback TID + 0x05
        }

        return trader_info;
    }

    private async Task<ulong> GetBoxStartOffset(CancellationToken token)
    {
        if (_cachedBoxOffset.HasValue)
            return _cachedBoxOffset.Value;

        // Get Box 1 Slot 1 address
        var finalOffset = await ResolvePointer(Offsets.BoxStartPokemonPointer, token).ConfigureAwait(false);
        _cachedBoxOffset = finalOffset;
        return finalOffset;
    }

    private async Task<bool> CheckIfOnOverworld(CancellationToken token)
    {
        return await IsOnMenu(MenuState.Overworld, token).ConfigureAwait(false);
    }

    private async Task<bool> CheckIfConnectedOnline(CancellationToken token)
    {
        // Use the direct main memory offset for faster and more reliable connection checks
        return await IsConnected(token).ConfigureAwait(false);
    }

    #endregion

    #region Trade Result Handling

    private void HandleAbortedTrade(PokeTradeDetail<PA9> detail, PokeRoutineType type, uint priority, PokeTradeResult result)
    {
        // Skip processing if we've already handled the notification (e.g., NoTrainerFound)
        if (result == PokeTradeResult.NoTrainerFound)
            return;

        detail.IsProcessing = false;
        if (result.ShouldAttemptRetry() && detail.Type != PokeTradeType.Random && !detail.IsRetry)
        {
            detail.IsRetry = true;
            Hub.Queues.Enqueue(type, detail, Math.Min(priority, PokeTradePriorities.Tier2));
            detail.SendNotification(this, "Oops! Something happened. I'll requeue you for another attempt.");
        }
        else
        {
            detail.SendNotification(this, $"Oops! Something happened. Canceling the trade: {result}.");
            detail.TradeCanceled(this, result);
        }
    }

    private async Task InnerLoop(SAV9ZA sav, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Config.IterateNextRoutine();
            var task = Config.CurrentRoutineType switch
            {
                PokeRoutineType.Idle => DoNothing(token),
                _ => DoTrades(sav, token),
            };
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (SocketException e)
            {
                if (e.StackTrace != null)
                    Connection.LogError(e.StackTrace);
                var attempts = Hub.Config.Timings.ReconnectAttempts;
                var delay = Hub.Config.Timings.ExtraReconnectDelay;
                var protocol = Config.Connection.Protocol;
                if (!await TryReconnect(attempts, delay, protocol, token).ConfigureAwait(false))
                    return;

                // Invalidate cached pointers after reconnection - game state may have changed
                _cachedBoxOffset = null;
                Log("Reconnected - cached pointers invalidated.");
            }
        }
    }

    #endregion

    #region Events

    private void OnConnectionError(Exception ex)
    {
        ConnectionError?.Invoke(this, ex);
    }

    private void OnConnectionSuccess()
    {
        ConnectionSuccess?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Specialized Trade Types

    private async Task<PokeTradeResult> PerformBatchTrade(SAV9ZA sav, PokeTradeDetail<PA9> poke, CancellationToken token)
    {
        int completedTrades = 0;
        var startingDetail = poke;
        var originalTrainerID = startingDetail.Trainer.ID;

        var tradesToProcess = poke.BatchTrades ?? [poke.TradeData];
        var totalBatchTrades = tradesToProcess.Count;
        List<string> downloadedImagePaths = new List<string>();
        List<string> tempImageFilePaths = new List<string>();

        if (poke.Type == PokeTradeType.Random)
        {
            var allPokemonChineseNames = new List<string>();
            string tipText = "本次交换你会得到";
            allPokemonChineseNames.Add(tipText);

            // 1. 批量下载（每个精灵生成唯一临时图，同时更新固定单图）
            foreach (var batchPokemon in tradesToProcess)
            {
                // 文本记录逻辑
                var zhSpecies = ShowdownTranslator<PA9>.GameStringsZh.Species[batchPokemon.Species];
                allPokemonChineseNames.Add(zhSpecies);

                // 下载并获取临时图路径
                string speciesImageUrl = TradeExtensions<PA9>.PokeImg(batchPokemon, false, false);
                string tempImgPath = await PokemonImageHelper.DownloadAndSavePokemonImageAsync(speciesImageUrl);
                tempImageFilePaths.Add(tempImgPath);
            }

            // 2. 写入msg4.txt
            string msg4FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "msg4.txt");
            try
            {
                var linesToWrite = allPokemonChineseNames.ToArray();
                File.WriteAllLines(msg4FilePath, linesToWrite);
            }
            catch
            {
                // 忽略写入失败的异常，或根据需求自定义处理
            }

            // 3. 拼接所有精灵图片
            if (tempImageFilePaths.Any())
            {
                try
                {
                    string mergedImagePath = await PokemonImageMergeHelper.MergePokemonImagesAsync(
                        tempImageFilePaths,
                        "Horizontal", // 如需纵向，改为 "Vertical"
                        10
                    );
                }
                catch
                {
                    // 忽略拼接失败的异常，或根据需求自定义处理
                }
            }
        }

        // Cache trade partner info after first successful connection
        TradePartnerStatusPLZA? cachedTradePartnerInfo = null;

        void SendCollectedPokemonAndCleanup()
        {
            var allReceived = BatchTracker.GetReceivedPokemon(originalTrainerID);
            if (allReceived.Count > 0)
            {
                poke.SendNotification(this, $"Sending you the {allReceived.Count} Pokémon you traded to me before the interruption.");

                Log($"Returning {allReceived.Count} Pokémon to trainer {originalTrainerID}.");

                // Send each Pokemon directly instead of calling TradeFinished
                for (int j = 0; j < allReceived.Count; j++)
                {
                    var pokemon = allReceived[j];
                    var speciesName = SpeciesName.GetSpeciesName(pokemon.Species, 2);
                    Log($"Returning: {speciesName}");

                    // Send the Pokemon directly to the notifier
                    poke.SendNotification(this, pokemon, $"Pokémon you traded to me: {speciesName}");
                    Thread.Sleep(500);
                }
            }
            else
            {
                Log($"No Pokémon found to return for trainer {originalTrainerID}.");
            }

            BatchTracker.ClearReceivedPokemon(originalTrainerID);
            BatchTracker.ReleaseBatch(originalTrainerID, startingDetail.UniqueTradeID);
            poke.IsProcessing = false;
            Hub.Queues.Info.Remove(new TradeEntry<PA9>(poke, originalTrainerID, PokeRoutineType.Batch, poke.Trainer.TrainerName, poke.UniqueTradeID));
        }

        for (int i = 0; i < totalBatchTrades; i++)
        {
            var currentTradeIndex = i;
            var toSend = tradesToProcess[currentTradeIndex];
            ulong boxOffset;
            // ########### 新增1：当前批次交易开始前，切换为 PartnerFound 状态（表示已找到伙伴，准备当前批次交易）


            await Task.Delay(100, token).ConfigureAwait(false); // 短暂延迟，确保状态更新生效

            // 批量交易动态信息赋值（替换原有直接写入msg.txt的逻辑）修改点
            var currentTradeNum = currentTradeIndex + 1;
            var zhSpecies = ShowdownTranslator<PA9>.GameStringsZh.Species[toSend.Species];
            var zhHeldItem = ShowdownTranslator<PA9>.GameStringsZh.Item[toSend.HeldItem];

            // 1. 更新批量交易全局字段
            _isBatchTrade = true; // 标记为批量交易状态
            _currentBatchNum = currentTradeNum;
            _totalBatchCount = totalBatchTrades;
            _currentPokemonNameZh = zhSpecies;
            _currentHeldItemZh = zhHeldItem;
            // 仅第一只设置链接码（与你原有逻辑一致）
            if (currentTradeIndex == 0)
                _currentTradeCode = poke.Code;

            // 2. 触发状态更新（可选，确保msg.txt即时刷新，也可依赖原有状态变更）
            SetTradeState(_tradeState); // 重新触发当前状态，强制刷新msg.txt

            //string speciesImageUrl = TradeExtensions<PA9>.PokeImg(toSend, false, false);
            //string savedPath = await PokemonImageHelper.DownloadAndSavePokemonImageAsync(speciesImageUrl);

            poke.TradeData = toSend;
            poke.Notifier.UpdateBatchProgress(currentTradeIndex + 1, toSend, poke.UniqueTradeID);

            // For subsequent trades (after first), we've already prepared the Pokemon during the previous trade animation
            // No need to prepare here - just send notification
            if (currentTradeIndex > 0)
            {
                poke.SendNotification(this, $"**Ready!** You can now offer your Pokémon for trade {currentTradeIndex + 1}/{totalBatchTrades}.");
                await Task.Delay(2_000, token).ConfigureAwait(false);
            }
            SetTradeState(TradeState.Confirming); // 新增：确认交易前切换状态
            await Task.Delay(100, token).ConfigureAwait(false);

            // For first trade only - search for partner
            if (currentTradeIndex == 0)
            {
                await Click(A, 0_500, token).ConfigureAwait(false);
                await Click(A, 0_500, token).ConfigureAwait(false);

                WaitAtBarrierIfApplicable(token);
                await Click(A, 1_000, token).ConfigureAwait(false);

                poke.TradeSearching(this);
                var partnerWaitResult = await WaitForTradePartner(token).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                {
                    StartFromOverworld = true;
                    await ExitTradeToOverworld(false, token).ConfigureAwait(false);
                    poke.SendNotification(this, "Canceling the batch trades. The routine has been interrupted.");
                    SendCollectedPokemonAndCleanup();
                    return PokeTradeResult.RoutineCancel;
                }

                if (partnerWaitResult == TradePartnerWaitResult.Timeout)
                {
                    // Partner never showed up - their fault, don't requeue
                    poke.IsProcessing = false;
                    poke.SendNotification(this, "No trading partner found. Canceling the batch trades.");
                    poke.TradeCanceled(this, PokeTradeResult.NoTrainerFound);
                    SendCollectedPokemonAndCleanup();

                    await RecoverToOverworld(token).ConfigureAwait(false);
                    return PokeTradeResult.NoTrainerFound;
                }

                if (partnerWaitResult == TradePartnerWaitResult.KickedToMenu)
                {
                    // Bot got kicked to menu - our fault, trigger requeue
                    Log("Connection error. Retrying...");
                    SetTradeState(TradeState.Failed);
                    SendCollectedPokemonAndCleanup();
                    await RecoverToOverworld(token).ConfigureAwait(false);
                    return PokeTradeResult.RecoverStart;
                }

                Hub.Config.Stream.EndEnterCode(this);

                // Wait until we're in the trade box
                Log("Selecting Pokémon in B1S1 for trade.");


                int boxCheckAttempts = 0;
                while (!await IsOnMenu(MenuState.InBox, token).ConfigureAwait(false))
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    if (++boxCheckAttempts > 30) // 15 seconds max
                    {
                        Log("No trade partner found.");
                        SetTradeState(TradeState.Failed);
                        return PokeTradeResult.NoTrainerFound;
                    }
                }

                // Wait for trade UI and partner data to load
                await Task.Delay(2_000, token).ConfigureAwait(false);

                // Now that data has loaded, read partner info
                var tradePartnerFullInfo = await GetTradePartnerFullInfo(token).ConfigureAwait(false);
                cachedTradePartnerInfo = tradePartnerFullInfo; // Cache for subsequent trades
                var tradePartner = new TradePartnerPLZA(tradePartnerFullInfo);

                var trainerNID = await GetTradePartnerNID(token).ConfigureAwait(false);
                // 新增：为类成员变量赋值（关键步骤，让UpdateMsgTxtByTradeState能访问到）
                this._currentTradeDetail = poke; // 存储当前交易详情（对应原来的poke）
                this._currentTrainerNID = trainerNID; // 存储当前训练师NID（对应原来的trainerNID）

                Log($"[TradePartner] OT: {tradePartner.TrainerName}, TID: {tradePartner.TID7}, SID: {tradePartner.SID7}, Gender: {tradePartnerFullInfo.Gender}, Language: {tradePartnerFullInfo.Language}, NID: {trainerNID}");


                RecordUtil<PokeTradeBotPLZA>.Record($"Initiating\t{trainerNID:X16}\t{tradePartner.TrainerName}\t{poke.Trainer.TrainerName}\t{poke.Trainer.ID}\t{poke.ID}\t{toSend.EncryptionConstant:X8}");

                poke.SendNotification(this, $"Found trade partner: {tradePartner.TrainerName}. **TID**: {tradePartner.TID7} **SID**: {tradePartner.SID7}");

                var tradeCodeStorage = new TradeCodeStorage();
                var existingTradeDetails = tradeCodeStorage.GetTradeDetails(poke.Trainer.ID);

                bool shouldUpdateOT = existingTradeDetails?.OT != tradePartner.TrainerName;
                bool shouldUpdateTID = existingTradeDetails?.TID != int.Parse(tradePartner.TID7);
                bool shouldUpdateSID = existingTradeDetails?.SID != int.Parse(tradePartner.SID7);

                if (shouldUpdateOT || shouldUpdateTID || shouldUpdateSID)
                {
                    string? ot = shouldUpdateOT ? tradePartner.TrainerName : existingTradeDetails?.OT;
                    int? tid = shouldUpdateTID ? int.Parse(tradePartner.TID7) : existingTradeDetails?.TID;
                    int? sid = shouldUpdateSID ? int.Parse(tradePartner.SID7) : existingTradeDetails?.SID;

                    if (ot != null && tid.HasValue && sid.HasValue)
                    {
                        tradeCodeStorage.UpdateTradeDetails(poke.Trainer.ID, ot, tid.Value, sid.Value);
                    }
                }

                var partnerCheck = CheckPartnerReputation(this, poke, trainerNID, tradePartner.TrainerName, AbuseSettings, token);
                if (partnerCheck != PokeTradeResult.Success)
                {
                    poke.SendNotification(this, "Trade partner blocked. Canceling trades.");
                    SendCollectedPokemonAndCleanup();
                    var isDistribution = poke.Type == PokeTradeType.Random;
                    var list = isDistribution ? PreviousUsersDistribution : PreviousUsers;
                    var previous = list.TryGetPreviousNID(trainerNID);
                    var cd = AbuseSettings.TradeCooldown;
                    var delta = previous != null ? DateTime.Now - previous.Time : TimeSpan.Zero;
                    File.WriteAllText("msg.txt", $"请自动退出\r\n上次见到你{delta.TotalMinutes:F1}分钟之前\r\n规则是{cd}分钟连接一次");//修改点 加入违反cd规则的

                    await Click(A, 1_000, token).ConfigureAwait(false);
                    await ExitTradeToOverworld(false, token).ConfigureAwait(false);
                    return partnerCheck;
                }

                poke.SendNotification(this, $"Found trade partner: {tradePartner.TrainerName}. **TID**: {tradePartner.TID7} **SID**: {tradePartner.SID7}");

                // Apply AutoOT for first trade if needed
                if (Hub.Config.Legality.UseTradePartnerInfo && !poke.IgnoreAutoOT)
                {
                    toSend = await ApplyAutoOT(toSend, tradePartnerFullInfo, sav, token).ConfigureAwait(false);
                    poke.TradeData = toSend;
                    // Give game time to refresh trade offer display with AutoOT Pokemon
                    await Task.Delay(3_000, token).ConfigureAwait(false);
                }
            }

            if (currentTradeIndex == 0)
            {
                poke.SendNotification(this, $"Please offer your Pokémon for trade 1/{totalBatchTrades}.");
            }

            var offsetBeforeBatch = await GetBoxStartOffset(token).ConfigureAwait(false);
            var pokemonBeforeBatchTrade = await ReadPokemon(offsetBeforeBatch, BoxFormatSlotSize, token).ConfigureAwait(false);
            var checksumBeforeBatchTrade = pokemonBeforeBatchTrade.Checksum;

            // Read the partner's offered Pokemon BEFORE we start pressing A to confirm
            // For subsequent trades (after first), give users more time to select their Pokemon
            int readTimeout = currentTradeIndex == 0 ? 6_000 : 100_000; // 6s for first trade, 100s for subsequent trades 修改点 因我自己的测试20s不够 所以才加到100s
            var offeredBatch = await ReadUntilPresentPointer(Offsets.LinkTradePartnerPokemonPointer, readTimeout, 0_500, BoxFormatSlotSize, token).ConfigureAwait(false);
            if (offeredBatch == null || offeredBatch.Species == 0 || !offeredBatch.ChecksumValid)
            {
                Log($"Trade {currentTradeIndex + 1} ended because trainer offer was rescinded too quickly.");
                SetTradeState(TradeState.Failed);
                poke.SendNotification(this, $"Trade partner didn't offer a valid Pokémon for trade {currentTradeIndex + 1}. Canceling remaining trades.");
                SendCollectedPokemonAndCleanup();
                await DisconnectFromTrade(token).ConfigureAwait(false);
                await ExitTradeToOverworld(false, token).ConfigureAwait(false);
                return PokeTradeResult.TrainerOfferCanceledQuick;
            }

            // Check if the offered Pokemon will evolve upon trade BEFORE confirming
            if (Hub.Config.Trade.TradeConfiguration.DisallowTradeEvolve && TradeEvolutions.WillTradeEvolve(offeredBatch.Species, offeredBatch.Form, offeredBatch.HeldItem, toSend.Species))
            {
                Log($"Trade {currentTradeIndex + 1} cancelled because trainer offered a Pokémon that would evolve upon trade.");
                SetTradeState(TradeState.Failed);
                poke.SendNotification(this, $"Trade cancelled for trade {currentTradeIndex + 1}. You cannot trade a Pokémon that will evolve. To prevent this, either give your Pokémon an Everstone to hold, or trade a different Pokémon.");
                SendCollectedPokemonAndCleanup();
                await DisconnectFromTrade(token).ConfigureAwait(false);
                await ExitTradeToOverworld(false, token).ConfigureAwait(false);
                return PokeTradeResult.TradeEvolveNotAllowed;
            }

            Log($"Confirming batch trade {currentTradeIndex + 1}/{totalBatchTrades}.");
            SetTradeState(TradeState.Confirming);

            var tradeResult = await ConfirmAndStartTrading(poke, checksumBeforeBatchTrade, token).ConfigureAwait(false);
            if (tradeResult != PokeTradeResult.Success)
            {
                poke.SendNotification(this, $"Trade failed for trade {currentTradeIndex + 1}/{totalBatchTrades}. Canceling remaining trades.");
                SendCollectedPokemonAndCleanup();
                if (tradeResult == PokeTradeResult.TrainerTooSlow)
                {
                    await DisconnectFromTrade(token).ConfigureAwait(false);
                }
                await ExitTradeToOverworld(false, token).ConfigureAwait(false);
                return tradeResult;
            }
            // 批量交易：交易动画启动，切换为Trading状态
            SetTradeState(TradeState.Trading);
            await Task.Delay(100, token).ConfigureAwait(false);
            boxOffset = await GetBoxStartOffset(token).ConfigureAwait(false);
            var received = await ReadPokemon(boxOffset, BoxFormatSlotSize, token).ConfigureAwait(false);
            Log($"Trade {currentTradeIndex + 1} - received {(Species)received.Species}");
            SetTradeState(TradeState.Confirming);

            BatchTracker.AddReceivedPokemon(originalTrainerID, received);
            string speciesImageUrl = TradeExtensions<PA9>.PokeImg(toSend, false, false);
            string savedPath = await PokemonImageHelper.DownloadAndSavePokemonImageAsync(speciesImageUrl);

            // Inject the next Pokemon for the next trade
            if (currentTradeIndex + 1 < totalBatchTrades)
            {
                var nextPokemon = tradesToProcess[currentTradeIndex + 1];

                // Apply AutoOT if needed
                if (Hub.Config.Legality.UseTradePartnerInfo && !poke.IgnoreAutoOT && cachedTradePartnerInfo != null)
                {
                    nextPokemon = await ApplyAutoOT(nextPokemon, cachedTradePartnerInfo, sav, token);
                    tradesToProcess[currentTradeIndex + 1] = nextPokemon;
                }
                else
                {
                    // No AutoOT - inject directly
                    boxOffset = await GetBoxStartOffset(token).ConfigureAwait(false);
                    await SetBoxPokemonAbsolute(boxOffset, nextPokemon, token, sav).ConfigureAwait(false);
                }
                Log($"Next Pokemon ({currentTradeIndex + 2}/{totalBatchTrades}) injected into B1S1 during animation");
 
            }

            if (token.IsCancellationRequested)
            {
                StartFromOverworld = true;
                poke.SendNotification(this, "Canceling batch trades.");
                SetTradeState(TradeState.Failed);
                SendCollectedPokemonAndCleanup();
                await ExitTradeToOverworld(false, token).ConfigureAwait(false);
                return PokeTradeResult.RoutineCancel;
            }

            // Validate that we received a Pokemon during the animation
            if (received == null || received.Species == 0)
            {
                Log($"Trade {currentTradeIndex + 1}/{totalBatchTrades} failed - no Pokemon was received.");
                SetTradeState(TradeState.Failed);
                poke.SendNotification(this, $"Trade {currentTradeIndex + 1}/{totalBatchTrades} was canceled. Canceling remaining trades.");
                SendCollectedPokemonAndCleanup();
                await DisconnectFromTrade(token).ConfigureAwait(false);
                await ExitTradeToOverworld(false, token).ConfigureAwait(false);
                return PokeTradeResult.TrainerTooSlow;
            }

            Log($"Trade {currentTradeIndex + 1}/{totalBatchTrades} complete! Received {(Species)received.Species}.");
            SetTradeState(TradeState.Completed);

            UpdateCountsAndExport(poke, received, toSend);

            // Get the trainer NID and name for logging
            var logTrainerNID = currentTradeIndex == 0 ? await GetTradePartnerNID(token).ConfigureAwait(false) : 0;
            var logPartner = cachedTradePartnerInfo != null ? new TradePartnerPLZA(cachedTradePartnerInfo) : null;
            LogSuccessfulTrades(poke, logTrainerNID, logPartner?.TrainerName ?? "Unknown");

            completedTrades = currentTradeIndex + 1;

            if (completedTrades == totalBatchTrades)
            {
                // Get all collected Pokemon before cleaning anything up
                var allReceived = BatchTracker.GetReceivedPokemon(originalTrainerID);

                // First send notification that trades are complete
                poke.SendNotification(this, "All batch trades completed! Thank you for trading!");

                // Send back all received Pokemon if ReturnPKMs is enabled
                if (Hub.Config.Discord.ReturnPKMs && allReceived.Count > 0)
                {
                    poke.SendNotification(this, $"Here are the {allReceived.Count} Pokémon you traded to me:");

                    // Send each Pokemon directly instead of calling TradeFinished
                    for (int j = 0; j < allReceived.Count; j++)
                    {
                        var pokemon = allReceived[j];
                        var speciesName = SpeciesName.GetSpeciesName(pokemon.Species, 2);

                        // Send the Pokemon directly to the notifier
                        poke.SendNotification(this, pokemon, $"Pokémon you traded to me: {speciesName}");
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                }

                // Now call TradeFinished ONCE for the entire batch with the last received Pokemon
                // This signals that the entire batch trade transaction is complete
                if (allReceived.Count > 0)
                {
                    poke.TradeFinished(this, allReceived[^1]);
                }
                else
                {
                    poke.TradeFinished(this, received);
                }

                // Mark the batch as fully completed and clean up
                Hub.Queues.CompleteTrade(this, startingDetail);
                BatchTracker.ClearReceivedPokemon(originalTrainerID);

                // Exit the trade state
                await ExitTradeToOverworld(false, token).ConfigureAwait(false);
                poke.IsProcessing = false;
                break;
            }

            // Next trade is already prepared - give game a moment to refresh the UI
            if (currentTradeIndex + 1 < totalBatchTrades)
            {
                Log($"Ready for next trade ({currentTradeIndex + 2}/{totalBatchTrades})...");
                SetTradeState(TradeState.PartnerFound);
                await Task.Delay(2_000, token).ConfigureAwait(false);
            }
        }

        ResetBatchMsgFields();

        // Ensure we exit properly even if the loop breaks unexpectedly
        await ExitTradeToOverworld(false, token).ConfigureAwait(false);
        poke.IsProcessing = false;
        return PokeTradeResult.Success;
    }

    #endregion

    #region Core Trade Logic

    private async Task PerformTrade(SAV9ZA sav, PokeTradeDetail<PA9> detail, PokeRoutineType type, uint priority, CancellationToken token)
    {
        PokeTradeResult result;
        try
        {
            // All trades go through PerformLinkCodeTrade which will handle both regular and batch trades
            result = await PerformLinkCodeTrade(sav, detail, token).ConfigureAwait(false);

            if (result != PokeTradeResult.Success)
            {
                if (detail.Type == PokeTradeType.Batch)
                    await HandleAbortedBatchTrade(detail, type, priority, result, token).ConfigureAwait(false);
                else
                    HandleAbortedTrade(detail, type, priority, result);
            }
        }
        catch (SocketException socket)
        {
            Log(socket.Message);
            result = PokeTradeResult.ExceptionConnection;
            if (detail.Type == PokeTradeType.Batch)
                await HandleAbortedBatchTrade(detail, type, priority, result, token).ConfigureAwait(false);
            else
                HandleAbortedTrade(detail, type, priority, result);
            throw;
        }
        catch (Exception e)
        {
            Log(e.Message);
            result = PokeTradeResult.ExceptionInternal;
            if (detail.Type == PokeTradeType.Batch)
                await HandleAbortedBatchTrade(detail, type, priority, result, token).ConfigureAwait(false);
            else
                HandleAbortedTrade(detail, type, priority, result);
        }
    }

 
public static class PokemonImageHelper
{
    /// <summary>
    /// 下载宝可梦图片（生成唯一临时图用于拼接，同时生成固定单图自动覆盖）
    /// </summary>
    /// <param name="speciesImageUrl">图片地址</param>
    /// <returns>唯一临时图片路径（用于拼接）</returns>
    public static async Task<string> DownloadAndSavePokemonImageAsync(string speciesImageUrl)
    {
        // 1. 下载图片数据流
        using var httpClient = new HttpClient();
        using var stream = await httpClient.GetStreamAsync(speciesImageUrl);

        // 2. 加载图片（克隆脱离流依赖）
        Image image = await Task.Run(() =>
        {
            using var tempImage = Image.FromStream(stream);
            return (Image)tempImage.Clone();
        });

        try
        {
            // 3. 构建文件夹路径
            string imagesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedImages");
            Directory.CreateDirectory(imagesFolderPath);

            // 4. 生成唯一临时文件名（用于拼接，避免覆盖）
            string tempUniqueFileName = $"temp_pokemon_{Guid.NewGuid()}.png";
            string tempFilePath = Path.Combine(imagesFolderPath, tempUniqueFileName);

            // 5. 保存唯一临时图（用于拼接所有不同精灵）
            image.Save(tempFilePath, ImageFormat.Png);

            // 6. 同时保存固定单图（覆盖上一个单图）
            string fixedSingleFileName = "single_pokemon.png";
            string fixedSingleFilePath = Path.Combine(imagesFolderPath, fixedSingleFileName);
            image.Save(fixedSingleFilePath, ImageFormat.Png);

            return tempFilePath; // 返回临时图路径，用于拼接
        }
        finally
        {
            // 释放图片资源，避免内存泄漏
            image.Dispose();
        }
    }
}
    public static class PokemonImageMergeHelper
    {
        /// <summary>
        /// 批量图片拼接（固定拼图文件名覆盖，拼接后清理临时单图）
        /// </summary>
        /// <param name="tempImageFilePaths">待拼接的唯一临时图片路径列表</param>
        /// <param name="layoutType">拼接布局：Horizontal（横向）、Vertical（纵向）</param>
        /// <param name="margin">图片之间的间距（像素）</param>
        /// <returns>拼接后的大图本地路径</returns>
        public static async Task<string> MergePokemonImagesAsync(List<string> tempImageFilePaths, string layoutType = "Horizontal", int margin = 10)
        {
            return await Task.Run(() =>
            {
                // 校验参数
                if (tempImageFilePaths == null || !tempImageFilePaths.Any())
                    throw new ArgumentException("待拼接的临时图片路径列表不能为空");

                string imagesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedImages");
                Directory.CreateDirectory(imagesFolderPath);

                List<Image> images = new List<Image>();
                List<Size> imageSizes = new List<Size>();
                try
                {
                    // 【最简核心修复】用MemoryStream加载图片，避免锁定原始文件
                    foreach (var tempPath in tempImageFilePaths)
                    {
                        if (File.Exists(tempPath) && tempPath.Contains("temp_pokemon_"))
                        {
                            using (var fs = File.OpenRead(tempPath))
                            {
                                // 从流加载图片，不锁定原始文件
                                Image img = Image.FromStream(fs);
                                images.Add(img);
                                imageSizes.Add(img.Size);
                            }
                        }
                    }

                    if (images.Count == 0)
                        throw new FileNotFoundException("未找到有效的临时拼接图片");

                    // 计算大图尺寸
                    int bigImageWidth = 0;
                    int bigImageHeight = 0;
                    if (layoutType.Equals("Horizontal", StringComparison.OrdinalIgnoreCase))
                    {
                        bigImageHeight = imageSizes.Max(s => s.Height);
                        bigImageWidth = imageSizes.Sum(s => s.Width) + margin * (images.Count - 1);
                    }
                    else if (layoutType.Equals("Vertical", StringComparison.OrdinalIgnoreCase))
                    {
                        bigImageWidth = imageSizes.Max(s => s.Width);
                        bigImageHeight = imageSizes.Sum(s => s.Height) + margin * (images.Count - 1);
                    }
                    else
                    {
                        throw new NotSupportedException("仅支持Horizontal（横向）和Vertical（纵向）布局");
                    }

                    // 创建大图画布并绘制
                    using Bitmap bigBitmap = new Bitmap(bigImageWidth, bigImageHeight);
                    using Graphics g = Graphics.FromImage(bigBitmap);
                    g.Clear(Color.White);
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                    int currentX = 0;
                    int currentY = 0;
                    for (int i = 0; i < images.Count; i++)
                    {
                        Image img = images[i];
                        Size imgSize = imageSizes[i];

                        if (layoutType.Equals("Horizontal", StringComparison.OrdinalIgnoreCase))
                        {
                            int drawY = (bigImageHeight - imgSize.Height) / 2;
                            g.DrawImage(img, currentX, drawY, imgSize.Width, imgSize.Height);
                            currentX += imgSize.Width + margin;
                        }
                        else
                        {
                            int drawX = (bigImageWidth - imgSize.Width) / 2;
                            g.DrawImage(img, drawX, currentY, imgSize.Width, imgSize.Height);
                            currentY += imgSize.Height + margin;
                        }
                    }

                    // 保存固定拼图（覆盖旧拼图）
                    string fixedMergedFileName = "merged_pokemon.png";
                    string mergedImagePath = Path.Combine(imagesFolderPath, fixedMergedFileName);
                    bigBitmap.Save(mergedImagePath, ImageFormat.Png);

                    return mergedImagePath;
                }
                finally
                {
                    // 1. 释放图片资源
                    foreach (var img in images)
                    {
                        img?.Dispose();
                    }

                    // 2. 清理临时图片（此时文件已无锁定，可正常删除）
                    foreach (var tempPath in tempImageFilePaths)
                    {
                        try
                        {
                            if (File.Exists(tempPath))
                            {
                                File.Delete(tempPath);
                            }
                        }
                        catch
                        {
                            // 保留简易异常忽略，不影响核心流程
                        }
                    }
                }
            });
        }
    }

    private async Task<PokeTradeResult> PerformLinkCodeTrade(SAV9ZA sav, PokeTradeDetail<PA9> poke, CancellationToken token)
    {
        // Check if trade was canceled by user
        if (poke.IsCanceled)
        {
            Log($"Trade for {poke.Trainer.TrainerName} was canceled by user.");
            SetTradeState(TradeState.Failed);
            poke.TradeCanceled(this, PokeTradeResult.UserCanceled);
            return PokeTradeResult.UserCanceled;
        }

        // Update Barrier Settings
        UpdateBarrier(poke.IsSynchronized);
        poke.TradeInitialize(this);
        Hub.Config.Stream.EndEnterCode(this);

        // Handle connection and portal entry FIRST
        if (!await EnsureConnectedAndInPortal(token).ConfigureAwait(false))
        {
            return PokeTradeResult.RecoverStart;
        }

        // Enter Link Trade and code
        var result = await EnterLinkTradeAndCode(poke, poke.Code, token).ConfigureAwait(false);

        if (result == LinkCodeEntryResult.VerificationFailedMismatch)
        {
            // Code didn't match - something went wrong, restart game
            Log("Code verification failed. Restarting game...");
            SetTradeState(TradeState.Failed);
            await RestartGamePLZA(token).ConfigureAwait(false);
            return PokeTradeResult.RecoverStart;
        }

        // Inject Pokemon AFTER code verification succeeds and BEFORE searching
        var toSend = poke.TradeData;
        if (toSend.Species != 0)
        {
            Log("Injected requested Pokémon into B1S1. ");
            SetTradeState(TradeState.EnteringCode);
            var offset = await GetBoxStartOffset(token).ConfigureAwait(false);
            await SetBoxPokemonAbsolute(offset, toSend, token, sav).ConfigureAwait(false);
        }
        if (poke.Type == PokeTradeType.Random)
        {
            string distributeFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "distribute");
            var batchPokemon = LoadPokemonFromDistributeFolder(distributeFolder);

            // 提升变量作用域，解决上下文不存在问题
            List<PA9> randomPokemonList = new List<PA9>();

            // 日志：开始随机抽取流程
            Log($"===== 开始宝可梦随机抽取流程 =====");
            Log($"待抽取宝可梦总数（distribute文件夹）：{batchPokemon.Count}");
            Log($"当前剩余未抽取宝可梦数量：{_remainingPokemon.Count}");

            if (batchPokemon.Count > 0)
            {
                lock (_lockObj)
                {
                    // 初始化/重置剩余抽取列表
                    if (_remainingPokemon.Count == 0)
                    {
                        _remainingPokemon = new List<PA9>(batchPokemon);
                        _drawnUniqueIds.Clear();
                        // 日志：重置抽取池
                        Log($"抽取池已重置，所有宝可梦恢复为未抽取状态，当前抽取池总数：{_remainingPokemon.Count}");
                    }

                    // 随机抽取
                    var random = new Random();
                    int takeCount = Math.Min(5, _remainingPokemon.Count);
                    randomPokemonList = _remainingPokemon
                        .OrderBy(x => random.Next())
                        .Take(takeCount)
                        .ToList();

                    // 日志：本次抽取基本信息
                    Log($"本次计划抽取数量：{takeCount}，实际抽取数量：{randomPokemonList.Count}");

                    // 遍历已抽取宝可梦，记录标识并移除
                    for (int i = 0; i < randomPokemonList.Count; i++)
                    {
                        var selectedPokemon = randomPokemonList[i];
                        string uniqueId = GenerateUniqueIdFromPokemon(selectedPokemon);

                        // 详细日志：每只被抽取宝可梦的信息
                        Log($"【第 {i + 1} 只被抽取宝可梦】");
                        Log($" 物种ID：{selectedPokemon.Species}，唯一标识：{uniqueId}");
 

                        if (!string.IsNullOrWhiteSpace(uniqueId))
                        {
                            bool isAdded = _drawnUniqueIds.Add(uniqueId);
                            // 日志：标识添加结果（防止重复）
                            Log($"  唯一标识是否成功加入已抽取集合：{isAdded}（已抽取标识总数：{_drawnUniqueIds.Count}）");

                            // 移除已抽取宝可梦
                            int removedCount = _remainingPokemon.RemoveAll(p => GenerateUniqueIdFromPokemon(p) == uniqueId);
                            // 日志：移除结果
                            Log($"  从剩余抽取池中移除该宝可梦，移除数量：{removedCount}，当前剩余未抽取数量：{_remainingPokemon.Count}");
                        }
                        else
                        {
                            Log($"  警告：该宝可梦唯一标识生成失败，未加入已抽取集合");
                        }
                    }
                }

                // 日志：抽取完成，赋值BatchTrades
                Log($"本次随机抽取流程完成，已为 BatchTrades 赋值 {randomPokemonList.Count} 只宝可梦");

                // 给 BatchTrades 赋值
                var reflectProperty = poke.GetType().GetProperty("BatchTrades", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (reflectProperty != null && reflectProperty.CanWrite)
                {
                    reflectProperty.SetValue(poke, randomPokemonList);
                    Log($"通过属性反射为 BatchTrades 赋值成功");
                }
                else
                {
                    var reflectField = poke.GetType().GetField("BatchTrades", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (reflectField != null)
                    {
                        reflectField.SetValue(poke, randomPokemonList);
                        Log($"通过字段反射为 BatchTrades 赋值成功");
                    }
                    else
                    {
                        Log($"警告：未找到 BatchTrades 属性或字段，赋值失败");
                    }
                }

                Log($"===== 宝可梦随机抽取流程结束 ====={Environment.NewLine}");
                return await PerformBatchTrade(sav, poke, token).ConfigureAwait(false);
            }
            else
            {
                // 日志：无可用宝可梦文件
                Log($"distribute文件夹中无有效宝可梦文件，将执行非批量交易逻辑");
                Log($"===== 宝可梦随机抽取流程结束 ====={Environment.NewLine}");
                return await PerformNonBatchTrade(sav, poke, token).ConfigureAwait(false);
            }
        }

        StartFromOverworld = false;

        // Route to appropriate trade handling based on trade type
        if (poke.Type == PokeTradeType.Batch)
            return await PerformBatchTrade(sav, poke, token).ConfigureAwait(false);

        return await PerformNonBatchTrade(sav, poke, token).ConfigureAwait(false);
    }

    private async Task<bool> EnsureConnectedAndInPortal(CancellationToken token)
    {
        if (StartFromOverworld)
        {
            if (!await CheckIfOnOverworld(token).ConfigureAwait(false))
            {
                await RecoverToOverworld(token).ConfigureAwait(false);
            }

            if (!await ConnectAndEnterPortal(token).ConfigureAwait(false))
            {
                Log("Connection error. Restarting...");
                SetTradeState(TradeState.Failed);
                await RecoverToOverworld(token).ConfigureAwait(false);
                return false;
            }
        }
        else if (!await CheckIfConnectedOnline(token).ConfigureAwait(false))
        {
            await RecoverToOverworld(token).ConfigureAwait(false);
            if (!await ConnectAndEnterPortal(token).ConfigureAwait(false))
            {
                Log("Connection failed. Restarting...");
                SetTradeState(TradeState.Failed);
                await RecoverToOverworld(token).ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }

    private async Task<LinkCodeEntryResult> EnterLinkTradeAndCode(PokeTradeDetail<PA9> poke, int code, CancellationToken token)
    {
        // Loading code entry
        if (poke.Type != PokeTradeType.Random)
        {
            Hub.Config.Stream.StartEnterCode(this);
        }

        // PLZA saves the previous Link Code after the first trade.
        // If the pointer isn't valid, we haven't traded yet.
        var (valid, _) = await ValidatePointerAll(Offsets.LinkTradeCodePointer, token).ConfigureAwait(false);
        if (!valid)
        {
            // No previous trade, freely enter our code
            if (code != 0)
            {
                Log($"Entering Link Trade code: {code:0000 0000}...");
                SetTradeState(TradeState.EnteringCode);
                await EnterLinkCode(code, Hub.Config, token).ConfigureAwait(false);
            }
        }
        else
        {
            var prevCode = await GetStoredLinkTradeCode(token).ConfigureAwait(false);
            if (prevCode != code)
            {
                // Only clear if the new code is different
                var codeLength = await GetStoredLinkTradeCodeLength(token).ConfigureAwait(false);
                if (codeLength > 0)
                {
                    for (int i = 0; i < codeLength; i++)
                        await Click(B, 0, token).ConfigureAwait(false);
                    await Task.Delay(0_500, token).ConfigureAwait(false);
                }

                if (code != 0)
                {
                    Log($"Entering Link Trade code: {code:0000 0000}...");
                    SetTradeState(TradeState.EnteringCode);
                    await EnterLinkCode(code, Hub.Config, token).ConfigureAwait(false);
                }
            }
            else
            {
                Log($"Using previous Link Trade code: {code:0000 0000}.");
                SetTradeState(TradeState.EnteringCode);
            }
        }

        await Click(PLUS, 2_000, token).ConfigureAwait(false);

        return LinkCodeEntryResult.Success;
    }

    private async Task<PokeTradeResult> PerformNonBatchTrade(SAV9ZA sav, PokeTradeDetail<PA9> poke, CancellationToken token)
    {
        var toSend = poke.TradeData;

        await Click(A, 0_500, token).ConfigureAwait(false);
        await Click(A, 0_500, token).ConfigureAwait(false);

        WaitAtBarrierIfApplicable(token);
        await Click(A, 1_000, token).ConfigureAwait(false);

        poke.TradeSearching(this);
        var partnerWaitResult = await WaitForTradePartner(token).ConfigureAwait(false);

        if (token.IsCancellationRequested)
        {
            StartFromOverworld = true;
            await ExitTradeToOverworld(false, token).ConfigureAwait(false);
            return PokeTradeResult.RoutineCancel;
        }

        if (partnerWaitResult == TradePartnerWaitResult.Timeout)
        {
            // Partner never showed up - their fault, don't requeue
            poke.IsProcessing = false;
            poke.SendNotification(this, "No trading partner found. Canceling the trade.");
            poke.TradeCanceled(this, PokeTradeResult.NoTrainerFound);

            await RecoverToOverworld(token).ConfigureAwait(false);
            return PokeTradeResult.NoTrainerFound;
        }

        if (partnerWaitResult == TradePartnerWaitResult.KickedToMenu)
        {
            // Bot got kicked to menu - our fault, trigger requeue
            Log("Connection error. Retrying...");
            SetTradeState(TradeState.Failed);
            await RecoverToOverworld(token).ConfigureAwait(false);
            return PokeTradeResult.RecoverStart;
        }

        Hub.Config.Stream.EndEnterCode(this);

        // Wait until we're in the trade box
        Log("Selecting Pokémon in B1S1...");
        SetTradeState(TradeState.EnteringCode);
        int boxCheckAttempts = 0;
        while (!await IsOnMenu(MenuState.InBox, token).ConfigureAwait(false))
        {
            await Task.Delay(500, token).ConfigureAwait(false);
            if (++boxCheckAttempts > 30) // 15 seconds max
            {
                Log("No trade partner found.");
                SetTradeState(TradeState.Failed);
                return PokeTradeResult.NoTrainerFound;
            }
        }

        // Wait for trade UI and partner data to load
        await Task.Delay(5_000, token).ConfigureAwait(false);

        // Now that data has loaded, read partner info
        var tradePartnerFullInfo = await GetTradePartnerFullInfo(token).ConfigureAwait(false);
        var tradePartner = new TradePartnerPLZA(tradePartnerFullInfo);

        var trainerNID = await GetTradePartnerNID(token).ConfigureAwait(false);
        // 新增：为类成员变量赋值（关键步骤，让UpdateMsgTxtByTradeState能访问到）
        this._currentTradeDetail = poke; // 存储当前交易详情（对应原来的poke）
        this._currentTrainerNID = trainerNID; // 存储当前训练师NID（对应原来的trainerNID）

        Log($"[TradePartner] OT: {tradePartner.TrainerName}, TID: {tradePartner.TID7}, SID: {tradePartner.SID7}, Gender: {tradePartnerFullInfo.Gender}, Language: {tradePartnerFullInfo.Language}, NID: {trainerNID}");


        RecordUtil<PokeTradeBotPLZA>.Record($"Initiating\t{trainerNID:X16}\t{tradePartner.TrainerName}\t{poke.Trainer.TrainerName}\t{poke.Trainer.ID}\t{poke.ID}\t{toSend.EncryptionConstant:X8}");
        poke.SendNotification(this, $"Found trade partner: {tradePartner.TrainerName}. **TID**: {tradePartner.TID7} **SID**: {tradePartner.SID7} Waiting for a Pokémon...");

        var tradeCodeStorage = new TradeCodeStorage();
        var existingTradeDetails = tradeCodeStorage.GetTradeDetails(poke.Trainer.ID);

        bool shouldUpdateOT = existingTradeDetails?.OT != tradePartner.TrainerName;
        bool shouldUpdateTID = existingTradeDetails?.TID != int.Parse(tradePartner.TID7);
        bool shouldUpdateSID = existingTradeDetails?.SID != int.Parse(tradePartner.SID7);

        if (shouldUpdateOT || shouldUpdateTID || shouldUpdateSID)
        {
            string? ot = shouldUpdateOT ? tradePartner.TrainerName : existingTradeDetails?.OT;
            int? tid = shouldUpdateTID ? int.Parse(tradePartner.TID7) : existingTradeDetails?.TID;
            int? sid = shouldUpdateSID ? int.Parse(tradePartner.SID7) : existingTradeDetails?.SID;

            if (ot != null && tid.HasValue && sid.HasValue)
            {
                tradeCodeStorage.UpdateTradeDetails(poke.Trainer.ID, ot, tid.Value, sid.Value);
            }
        }

        var partnerCheck = CheckPartnerReputation(this, poke, trainerNID, tradePartner.TrainerName, AbuseSettings, token);
        if (partnerCheck != PokeTradeResult.Success)
        {
            await Click(A, 1_000, token).ConfigureAwait(false);
            await ExitTradeToOverworld(false, token).ConfigureAwait(false);
            return partnerCheck;
        }

        // Read the offered Pokemon for Clone/Dump trades
        PA9? offered = null;
        if (poke.Type == PokeTradeType.Clone || poke.Type == PokeTradeType.Dump)
        {
            offered = await ReadUntilPresentPointer(Offsets.LinkTradePartnerPokemonPointer, 3_000, 0_500, BoxFormatSlotSize, token).ConfigureAwait(false);
            if (offered == null || offered.Species == 0)
            {
                poke.SendNotification(this, "Failed to read offered Pokémon. Exiting trade.");
                await ExitTradeToOverworld(true, token).ConfigureAwait(false);
                return PokeTradeResult.TrainerRequestBad;
            }
        }

        if (poke.Type == PokeTradeType.Clone)
        {
            var (result, clone) = await ProcessCloneTradeAsync(poke, sav, offered!, token).ConfigureAwait(false);
            if (result != PokeTradeResult.Success)
            {
                await ExitTradeToOverworld(false, token).ConfigureAwait(false);
                return result;
            }

            // Trade them back their cloned Pokemon
            toSend = clone!;
        }

        if (poke.Type == PokeTradeType.Dump)
        {
            var result = await ProcessDumpTradeAsync(poke, token).ConfigureAwait(false);
            await ExitTradeToOverworld(false, token).ConfigureAwait(false);
            return result;
        }

        if (Hub.Config.Legality.UseTradePartnerInfo && !poke.IgnoreAutoOT)
        {
            toSend = await ApplyAutoOT(toSend, tradePartnerFullInfo, sav, token);
            // Give game time to refresh trade offer display with AutoOT Pokemon
            await Task.Delay(3_000, token).ConfigureAwait(false);
        }

        SpecialTradeType itemReq = SpecialTradeType.None;
        if (poke.Type == PokeTradeType.Seed)
        {
            poke.SendNotification(this, "Seed trades are temporarily unavailable. Please request a specific Pokemon instead.");
            await ExitTradeToOverworld(true, token).ConfigureAwait(false);
            return PokeTradeResult.TrainerRequestBad;
        }

        if (itemReq == SpecialTradeType.WonderCard)
            poke.SendNotification(this, "Distribution success!");
        else if (itemReq != SpecialTradeType.None && itemReq != SpecialTradeType.Shinify)
            poke.SendNotification(this, "Special request successful!");
        else if (itemReq == SpecialTradeType.Shinify)
            poke.SendNotification(this, "Shinify success! Thanks for being part of the community!");

        var offsetBefore = await GetBoxStartOffset(token).ConfigureAwait(false);
        var pokemonBeforeTrade = await ReadPokemon(offsetBefore, BoxFormatSlotSize, token).ConfigureAwait(false);
        var checksumBeforeTrade = pokemonBeforeTrade.Checksum;

        // Read the partner's offered Pokemon BEFORE we start pressing A to confirm
        // This way we can cancel with B+A if they're offering something that will evolve
        if (offered == null) // Only read if we haven't already (Clone/Dump read it earlier)
        {
            offered = await ReadUntilPresentPointer(Offsets.LinkTradePartnerPokemonPointer, 3_000, 0_500, BoxFormatSlotSize, token).ConfigureAwait(false);
        }

        if (offered == null || offered.Species == 0 || !offered.ChecksumValid)
        {
            Log("Trade ended because trainer offer was rescinded too quickly.");
            SetTradeState(TradeState.Failed);
            poke.SendNotification(this, "Trade partner didn't offer a valid Pokémon.");
            await DisconnectFromTrade(token).ConfigureAwait(false);
            await ExitTradeToOverworld(false, token).ConfigureAwait(false);
            return PokeTradeResult.TrainerOfferCanceledQuick;
        }

        // Check if the offered Pokemon will evolve upon trade BEFORE confirming
        if (Hub.Config.Trade.TradeConfiguration.DisallowTradeEvolve && TradeEvolutions.WillTradeEvolve(offered.Species, offered.Form, offered.HeldItem, toSend.Species))
        {
            Log("Trade cancelled because trainer offered a Pokémon that would evolve upon trade.");
            SetTradeState(TradeState.Failed);
            poke.SendNotification(this, "Trade cancelled. You cannot trade a Pokémon that will evolve. To prevent this, either give your Pokémon an Everstone to hold, or trade a different Pokémon.");
            await DisconnectFromTrade(token).ConfigureAwait(false);
            await ExitTradeToOverworld(false, token).ConfigureAwait(false);
            return PokeTradeResult.TradeEvolveNotAllowed;
        }

        Log("Selecting \"Trade it.\" Now waiting for trade animation to begin...");
        SetTradeState(TradeState.Confirming);
        var tradeResult = await ConfirmAndStartTrading(poke, checksumBeforeTrade, token).ConfigureAwait(false);
        if (tradeResult != PokeTradeResult.Success)
        {
            if (tradeResult == PokeTradeResult.TrainerTooSlow)
            {
                await DisconnectFromTrade(token).ConfigureAwait(false);
            }
            await ExitTradeToOverworld(false, token).ConfigureAwait(false);
            return tradeResult;
        }

        if (token.IsCancellationRequested)
        {
            StartFromOverworld = true;
            await ExitTradeToOverworld(false, token).ConfigureAwait(false);
            return PokeTradeResult.RoutineCancel;
        }

        var offset2 = await GetBoxStartOffset(token).ConfigureAwait(false);
        var received = await ReadPokemon(offset2, BoxFormatSlotSize, token).ConfigureAwait(false);
        var checksumAfterTrade = received.Checksum;

        if (checksumBeforeTrade == checksumAfterTrade)
        {
            Log("Trade was canceled.");
            SetTradeState(TradeState.Failed);
            poke.SendNotification(this, "Trade was canceled. Please try again.");
            await DisconnectFromTrade(token).ConfigureAwait(false);
            await ExitTradeToOverworld(false, token).ConfigureAwait(false);
            return PokeTradeResult.TrainerTooSlow;
        }

        Log($"Trade complete! Received {(Species)received.Species}. Now waiting for trade animation to complete...");
        SetTradeState(TradeState.Completed);

        poke.TradeFinished(this, received);
        UpdateCountsAndExport(poke, received, toSend);
        LogSuccessfulTrades(poke, trainerNID, tradePartner.TrainerName);

        await ExitTradeToOverworld(false, token).ConfigureAwait(false);
        return PokeTradeResult.Success;
    }

    private async Task HandleAbortedBatchTrade(PokeTradeDetail<PA9> detail, PokeRoutineType type, uint priority, PokeTradeResult result, CancellationToken token)
    {
        detail.IsProcessing = false;

        // Always remove from UsersInQueue on abort
        Hub.Queues.Info.Remove(new TradeEntry<PA9>(detail, detail.Trainer.ID, type, detail.Trainer.TrainerName, detail.UniqueTradeID));

        if (detail.TotalBatchTrades > 1)
        {
            // Release the batch claim on failure
            BatchTracker.ReleaseBatch(detail.Trainer.ID, detail.UniqueTradeID);

            if (result.ShouldAttemptRetry() && detail.Type != PokeTradeType.Random && !detail.IsRetry)
            {
                detail.IsRetry = true;
                Hub.Queues.Enqueue(type, detail, Math.Min(priority, PokeTradePriorities.Tier2));
                detail.SendNotification(this, "Oops! Something happened during your batch trade. I'll requeue you for another attempt.");
            }
            else
            {
                detail.SendNotification(this, $"Batch trade failed: {result}");
                detail.TradeCanceled(this, result);
                await ExitTradeToOverworld(false, token).ConfigureAwait(false);
            }
        }
        else
        {
            HandleAbortedTrade(detail, type, priority, result);
        }
        ResetBatchMsgFields();
    }

    private async Task<bool> RecoverToOverworld(CancellationToken token)
    {
        if (await CheckIfOnOverworld(token).ConfigureAwait(false))
            return true;

        Log("Recovering...");
        SetTradeState(TradeState.Failed);

        await Click(B, 1_500, token).ConfigureAwait(false);
        if (await CheckIfOnOverworld(token).ConfigureAwait(false))
            return true;

        await Click(A, 1_500, token).ConfigureAwait(false);
        if (await CheckIfOnOverworld(token).ConfigureAwait(false))
            return true;

        var attempts = 0;
        while (!await CheckIfOnOverworld(token).ConfigureAwait(false))
        {
            attempts++;
            if (attempts >= 30)
                break;

            await Click(B, 1_000, token).ConfigureAwait(false);
            if (await CheckIfOnOverworld(token).ConfigureAwait(false))
                break;

            await Click(B, 1_000, token).ConfigureAwait(false);
            if (await CheckIfOnOverworld(token).ConfigureAwait(false))
                break;
        }

        if (!await CheckIfOnOverworld(token).ConfigureAwait(false))
        {
            Log("Restarting game...");
            SetTradeState(TradeState.Failed);

            await RestartGamePLZA(token).ConfigureAwait(false);
        }
        await Task.Delay(1_000, token).ConfigureAwait(false);

        StartFromOverworld = true;
        return true;
    }

    private async Task RestartGamePLZA(CancellationToken token)
    {
        await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
        _cachedBoxOffset = null; // Invalidate box offset cache after restart

        // If we were connected to a partner before restart, prevent soft ban
        if (_wasConnectedToPartner)
        {
            Log("Preventing trade soft ban - connecting with random partner to clear trade state...");

            await PreventTradeSoftBan(token).ConfigureAwait(false);
            _wasConnectedToPartner = false; // Reset the flag after recovery
        }
    }

    /// <summary>
    /// Prevents trade soft ban after restarting during an active trade connection.
    ///
    /// When the bot restarts AFTER successfully connecting to a trade partner (verified via MenuState.InBox),
    /// the game may impose a soft ban if we attempt to trade again without clearing the previous connection state.
    ///
    /// This method connects to a random partner (no code) and immediately disconnects using B+A to signal
    /// to the game servers that the previous trade session has ended, preventing the soft ban.
    /// </summary>
    private async Task PreventTradeSoftBan(CancellationToken token)
    {
        await Task.Delay(5_000, token).ConfigureAwait(false);

        if (!await CheckIfOnOverworld(token).ConfigureAwait(false))
        {
            Log("Not on overworld after restart, attempting recovery...");

            await RecoverToOverworld(token).ConfigureAwait(false);
        }

        Log("Connecting online to prevent trade soft ban...");
        await Click(X, 3_000, token).ConfigureAwait(false);
        await Click(DUP, 1_000, token).ConfigureAwait(false);
        await Click(A, 2_000, token).ConfigureAwait(false);
        await Click(DRIGHT, 1_000, token).ConfigureAwait(false);
        await Click(DRIGHT, 1_000, token).ConfigureAwait(false);
        await Click(A, 1_000, token).ConfigureAwait(false);
        await Click(DRIGHT, 1_000, token).ConfigureAwait(false);
        await Click(A, 1_000, token).ConfigureAwait(false);

        int attempts = 0;
        while (!await CheckIfConnectedOnline(token).ConfigureAwait(false))
        {
            await Task.Delay(1_000, token).ConfigureAwait(false);
            if (++attempts > 30)
            {
                Log("Failed to connect online during soft ban prevention.");
                await RecoverToOverworld(token).ConfigureAwait(false);
                return;
            }
        }
        await Task.Delay(8_000 + Hub.Config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
        Log("Connected online for soft ban prevention.");

        await Click(A, 1_000, token).ConfigureAwait(false);
        await Click(A, 1_000, token).ConfigureAwait(false);
        await Task.Delay(3_000, token).ConfigureAwait(false);

        Log("Connecting with random partner to clear previous trade session...");
        await Click(PLUS, 2_000, token).ConfigureAwait(false);

        Log("Waiting for random partner to connect...");
        await Task.Delay(3_000, token).ConfigureAwait(false);

        int waitAttempts = 0;
        bool connected = false;
        while (waitAttempts < 30 && !connected)
        {
            var nid = await GetTradePartnerNID(token).ConfigureAwait(false);
            if (nid != 0)
            {
                Log("Random partner connected via NID. Disconnecting to complete soft ban prevention...");
                connected = true;
                break;
            }

            if (await IsOnMenu(MenuState.InBox, token).ConfigureAwait(false))
            {
                Log("Random partner connected via TradeBox. Disconnecting to complete soft ban prevention...");
                connected = true;
                break;
            }

            await Task.Delay(1_000, token).ConfigureAwait(false);
            waitAttempts++;
        }

        if (!connected)
        {
            Log("No random partner found within 30s timeout. Soft ban may not be fully prevented. Continuing...");
            await RecoverToOverworld(token).ConfigureAwait(false);
            return;
        }

        Log("Disconnecting from random partner (B to cancel, A to confirm)...");
        await Click(B, 1_000, token).ConfigureAwait(false);
        await Click(A, 1_000, token).ConfigureAwait(false);

        Log("Waiting for partner disconnect confirmation...");
        int disconnectAttempts = 0;
        bool partnerDisconnected = false;
        while (disconnectAttempts < 10 && !partnerDisconnected)
        {
            await Task.Delay(500, token).ConfigureAwait(false);
            var currentNid = await GetTradePartnerNID(token).ConfigureAwait(false);
            if (currentNid == 0)
            {
                Log("Partner disconnected (NID = 0). Exiting to overworld...");
                
                partnerDisconnected = true;
                break;
            }
            disconnectAttempts++;
        }

        if (!partnerDisconnected)
        {
            Log("Partner did not disconnect within timeout. Forcing exit...");
            
        }

        Log("Spamming B to return to overworld...");
        
        for (int i = 0; i < 15; i++)
        {
            await Click(B, 1_000, token).ConfigureAwait(false);

            if (await CheckIfOnOverworld(token).ConfigureAwait(false))
            {
                Log("Soft ban prevention complete. Successfully returned to overworld.");
                
                StartFromOverworld = true;
                return;
            }
        }

        Log("Failed to return to overworld after B spam. Performing full recovery...");
        
        await RecoverToOverworld(token).ConfigureAwait(false);
        StartFromOverworld = true;
    }

    #endregion

    #region Multi-Bot Synchronization

    /// <summary>
    /// Checks if the barrier needs to get updated to consider this bot.
    /// If it should be considered, it adds it to the barrier if it is not already added.
    /// If it should not be considered, it removes it from the barrier if not already removed.
    /// </summary>
    private void UpdateBarrier(bool shouldWait)
    {
        if (ShouldWaitAtBarrier == shouldWait)
            return; // no change required

        ShouldWaitAtBarrier = shouldWait;
        if (shouldWait)
        {
            Hub.BotSync.Barrier.AddParticipant();
            Log($"Joined the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
        }
        else
        {
            Hub.BotSync.Barrier.RemoveParticipant();
            Log($"Left the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
        }
    }

    private void UpdateCountsAndExport(PokeTradeDetail<PA9> poke, PA9 received, PA9 toSend)
    {
        var counts = TradeSettings;
        if (poke.Type == PokeTradeType.Random)
            counts.CountStatsSettings.AddCompletedDistribution();
        else if (poke.Type == PokeTradeType.Clone)
            counts.CountStatsSettings.AddCompletedClones();
        else if (poke.Type == PokeTradeType.FixOT)
            counts.CountStatsSettings.AddCompletedFixOTs();
        else
            counts.CountStatsSettings.AddCompletedTrade();

        if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
        {
            var subfolder = poke.Type.ToString().ToLower();
            var service = poke.Notifier.GetType().ToString().ToLower();
            var tradedFolder = service.Contains("twitch") ? Path.Combine("traded", "twitch") : service.Contains("discord") ? Path.Combine("traded", "discord") : "traded";
            DumpPokemon(DumpSetting.DumpFolder, subfolder, received); // received by bot
            if (poke.Type is PokeTradeType.Specific or PokeTradeType.Clone)
                DumpPokemon(DumpSetting.DumpFolder, tradedFolder, toSend); // sent to partner
        }
    }

    #region Clone & Dump Features

    private async Task<bool> CheckCloneChangedOffer(CancellationToken token)
    {
        // Watch their status to indicate they canceled, then offered a new Pokémon.
        var hovering = await ReadUntilChanged(TradePartnerStatusOffset, [0x2], 25_000, 1_000, true, true, token).ConfigureAwait(false);
        if (!hovering)
        {
            Log("Trade partner did not change their initial offer.");
            SetTradeState(TradeState.Failed);
            return false;
        }
        var offering = await ReadUntilChanged(TradePartnerStatusOffset, [0x3], 25_000, 1_000, true, true, token).ConfigureAwait(false);
        if (!offering)
        {
            return false;
        }
        return true;
    }

    private async Task<(PokeTradeResult Result, PA9? ClonedPokemon)> ProcessCloneTradeAsync(PokeTradeDetail<PA9> poke, SAV9ZA sav, PA9 offered, CancellationToken token)
    {
        if (Hub.Config.Discord.ReturnPKMs)
            poke.SendNotification(this, offered, "Here's what you showed me!");

        var la = new LegalityAnalysis(offered);
        if (!la.Valid)
        {
            Log($"Clone request (from {poke.Trainer.TrainerName}) has detected an invalid Pokémon: {GameInfo.GetStrings("en").Species[offered.Species]}.");
            SetTradeState(TradeState.Failed);
            if (DumpSetting.Dump)
                DumpPokemon(DumpSetting.DumpFolder, "hacked", offered);

            var report = la.Report();
            Log(report);
            poke.SendNotification(this, "This Pokémon is not legal per PKHeX's legality checks. I am forbidden from cloning this. Exiting trade.");
            poke.SendNotification(this, report);

            return (PokeTradeResult.IllegalTrade, null);
        }

        var clone = offered.Clone();
        if (Hub.Config.Legality.ResetHOMETracker)
            clone.Tracker = 0;

        poke.SendNotification(this, $"**Cloned your {GameInfo.GetStrings("en").Species[clone.Species]}!**\nNow press B to cancel your offer and trade me a Pokémon you don't want.");
        Log($"Cloned a {(Species)clone.Species}. Waiting for user to change their Pokémon...");
        SetTradeState(TradeState.Trading);


        if (!await CheckCloneChangedOffer(token).ConfigureAwait(false))
        {
            // They get one more chance.
            poke.SendNotification(this, "**HEY CHANGE IT NOW OR I AM LEAVING!!!**");
            if (!await CheckCloneChangedOffer(token).ConfigureAwait(false))
            {
                Log("Trade partner did not change their Pokémon.");
                SetTradeState(TradeState.Failed);
                return (PokeTradeResult.TrainerTooSlow, null);
            }
        }

        // If we got to here, we can read their offered Pokémon.
        var pk2 = await ReadUntilPresentPointer(Offsets.LinkTradePartnerPokemonPointer, 5_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
        if (pk2 is null || SearchUtil.HashByDetails(pk2) == SearchUtil.HashByDetails(offered))
        {
            Log("Trade partner did not change their Pokémon.");
            SetTradeState(TradeState.Failed);
            return (PokeTradeResult.TrainerTooSlow, null);
        }

        var boxOffset = await GetBoxStartOffset(token).ConfigureAwait(false);
        await SetBoxPokemonAbsolute(boxOffset, clone, token, sav).ConfigureAwait(false);

        return (PokeTradeResult.Success, clone);
    }

    private async Task<PokeTradeResult> ProcessDumpTradeAsync(PokeTradeDetail<PA9> detail, CancellationToken token)
    {
        int ctr = 0;
        var maxDumps = Hub.Config.Trade.TradeConfiguration.MaxDumpsPerTrade;
        var time = TimeSpan.FromSeconds(Hub.Config.Trade.TradeConfiguration.MaxDumpTradeTime);
        var start = DateTime.Now;

        // Tell the user what to do
        detail.SendNotification(this, $"Now showing your Pokémon! You can show me up to {maxDumps} Pokémon. Keep changing Pokémon to dump more!");

        var pkprev = new PA9();
        var warnedAboutTime = false;
        var bctr = 0;

        while (ctr < maxDumps && DateTime.Now - start < time)
        {
            // Check if we're still in the trade box (user disconnected if not)
            if (!await IsOnMenu(MenuState.InBox, token).ConfigureAwait(false))
            {
                Log("Trade partner disconnected (not in trade box).");
                SetTradeState(TradeState.Failed);
                break;
            }

            // Periodic B button press to keep connection alive
            if (bctr++ % 3 == 0)
                await Click(B, 0_100, token).ConfigureAwait(false);

            // Warn user when they're running low on time
            var elapsed = DateTime.Now - start;
            if (!warnedAboutTime && elapsed.TotalSeconds > time.TotalSeconds - 15)
            {
                detail.SendNotification(this, "Only 15 seconds remaining! Show your last Pokémon or press B to exit.");
                warnedAboutTime = true;
            }

            // Wait for the user to show us a Pokemon - needs to be different from the previous one
            var pk = await ReadUntilPresentPointer(Offsets.LinkTradePartnerPokemonPointer, 3_000, 0_050, BoxFormatSlotSize, token).ConfigureAwait(false);
            if (pk == null || pk.Species == 0 || !pk.ChecksumValid)
            {
                await Task.Delay(0_050, token).ConfigureAwait(false);
                continue;
            }

            // Check if this is the same Pokemon as before
            if (SearchUtil.HashByDetails(pk) == SearchUtil.HashByDetails(pkprev))
            {
                Log($"User is showing the same Pokémon as before. Waiting for a different one...");
                await Task.Delay(0_500, token).ConfigureAwait(false);
                continue;
            }

            // Heal and refresh checksum to ensure valid data
            pk.Heal();
            pk.RefreshChecksum();

            // Save the new Pokemon for comparison next round
            pkprev = pk;

            // Dump the Pokemon to file if dumping is enabled
            if (DumpSetting.Dump)
            {
                var subfolder = detail.Type.ToString().ToLower();
                DumpPokemon(DumpSetting.DumpFolder, subfolder, pk);
            }

            var la = new LegalityAnalysis(pk);
            var verbose = $"```{la.Report(true)}```";
            Log($"Shown Pokémon is: {(la.Valid ? "Valid" : "Invalid")}.");
            SetTradeState(TradeState.Trading);
            ctr++;
            var msg = Hub.Config.Trade.TradeConfiguration.DumpTradeLegalityCheck ? verbose : $"File {ctr}";

            // Include trainer data for people requesting with their own trainer data
            var ot = pk.OriginalTrainerName;
            var ot_gender = pk.OriginalTrainerGender == 0 ? "Male" : "Female";
            var tid = pk.GetDisplayTID().ToString(pk.GetTrainerIDFormat().GetTrainerIDFormatStringTID());
            var sid = pk.GetDisplaySID().ToString(pk.GetTrainerIDFormat().GetTrainerIDFormatStringSID());
            msg += $"\n**Trainer Data**\n```OT: {ot}\nOTGender: {ot_gender}\nTID: {tid}\nSID: {sid}```";

            // Extra information for shiny eggs
            var eggstring = pk.IsEgg ? "Egg " : string.Empty;
            msg += pk.IsShiny ? $"\n**This Pokémon {eggstring}is shiny!**" : string.Empty;

            // Send the Pokemon file back to the user via Discord
            detail.SendNotification(this, pk, msg);

            // Tell user their progress
            var remaining = maxDumps - ctr;
            if (remaining > 0)
                detail.SendNotification(this, $"Received! You can show me {remaining} more. Show a different Pokémon to continue, or press B to exit.");
            else
                detail.SendNotification(this, "That's the maximum! Press B to exit the trade.");
        }

        var timeElapsed = DateTime.Now - start;
        Log($"Ended Dump loop after processing {ctr} Pokémon in {timeElapsed.TotalSeconds:F1} seconds.");
        
        if (ctr == 0)
            return PokeTradeResult.TrainerTooSlow;

        TradeSettings.CountStatsSettings.AddCompletedDumps();
        detail.Notifier.SendNotification(this, detail, $"Dumped {ctr} Pokémon.");
        detail.Notifier.TradeFinished(this, detail, pkprev); // Send last dumped Pokemon
        return PokeTradeResult.Success;
    }

    #endregion

    private void WaitAtBarrierIfApplicable(CancellationToken token)
    {
        if (!ShouldWaitAtBarrier)
            return;
        var opt = Hub.Config.Distribution.SynchronizeBots;
        if (opt == BotSyncOption.NoSync)
            return;

        var timeoutAfter = Hub.Config.Distribution.SynchronizeTimeout;
        if (FailedBarrier == 1) // failed last iteration
            timeoutAfter *= 2; // try to re-sync in the event things are too slow.

        var result = Hub.BotSync.Barrier.SignalAndWait(TimeSpan.FromSeconds(timeoutAfter), token);

        if (result)
        {
            FailedBarrier = 0;
            return;
        }

        FailedBarrier++;
        Log($"Barrier sync timed out after {timeoutAfter} seconds. Continuing.");
    }
    #region 辅助方法：读取Distribute文件夹宝可梦
    /// <summary>
    /// 原有方法：修复后确保返回 List<PA9>（原有逻辑不变，仅确认返回类型）
    /// </summary>
    private List<PA9> LoadPokemonFromDistributeFolder(string folderPath)
    {
        var pokemonList = new List<PA9>(); // 确保返回类型为 PA9
        if (!Directory.Exists(folderPath))
        {
            Log($"Distribute文件夹不存在：{folderPath}");
            return pokemonList;
        }

        var extensions = new[] { ".pa9", ".pb7", ".pk8", ".pb8", ".pk7", ".pb7" };
        var files = Directory.GetFiles(folderPath)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var data = File.ReadAllBytes(file);
                var pk = new PA9(data);
                if (pk.Species != 0 && pk.ChecksumValid)
                {
                    pokemonList.Add(pk);
                }
                else
                {
                    Log($"无效的宝可梦文件：{Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                Log($"读取文件失败 {Path.GetFileName(file)}：{ex.Message}");
            }
        }

        return pokemonList;
    }
    #endregion
    private List<PA9> _remainingPokemon = new List<PA9>(); // 替换 YourPokemonType 为 PA9
    private static HashSet<string> _drawnUniqueIds = new HashSet<string>(); // 保留已抽取标识集合（无需修改，仅确认存在）
    private static readonly object _lockObj = new object();
    /// <summary>
    /// 修复：将 Span<byte> 转换为 byte[]，生成宝可梦唯一标识
    /// </summary>
    /// <param name="pokemon">PA9 宝可梦实例</param>
    /// <returns>唯一标识字符串</returns>
    public string GenerateUniqueIdFromPokemon(PA9 pokemon)
    {
        if (pokemon == null)
            throw new ArgumentNullException(nameof(pokemon));

        // 解决 Span<byte> 转 byte[] 问题
        byte[] pokemonBytes = pokemon.Data.ToArray();
        string originalHexStr = string.Join(" ", pokemonBytes.Select(b => b.ToString("X2")));

        var originalBytes = originalHexStr
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var validBytes = originalBytes.Where(b => b != "00").ToList();
        validBytes.Reverse();
        string uniqueId = string.Join("", validBytes);

        // 可选：记录生成唯一标识的辅助日志（调试用，可注释）
        //Log($"宝可梦物种ID：{pokemon.Species}）唯一标识生成完成：{uniqueId}");
        return uniqueId;
    }

    private Task WaitForQueueStep(int waitCounter, CancellationToken token)
    {
        if (waitCounter == 0)
        {
            // Updates the assets.
            Hub.Config.Stream.IdleAssets(this);
            Log("Nothing to check, waiting for new users...");
            SetTradeState(TradeState.Idle);
        }

        return Task.Delay(1_000, token);
    }

    #endregion
}
