using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Pokemon;

public abstract class PokeRoutineExecutor<T>(IConsoleBotManaged<IConsoleConnection, IConsoleConnectionAsync> Config)
    : PokeRoutineExecutorBase(Config)
    where T : PKM, new()
{
    private const ulong dmntID = 0x010000000000000d;
    public BatchTradeTracker<T> BatchTracker => BatchTradeTracker<T>.Instance;
    // Check if either Tesla or dmnt are active if the sanity check for Trainer Data fails, as these are common culprits.
    private const ulong ovlloaderID = 0x420000000007e51a;

    public static void DumpPokemon(string folder, string subfolder, T pk)
    {
        if (!Directory.Exists(folder))
            return;
        var dir = Path.Combine(folder, subfolder);
        Directory.CreateDirectory(dir);
        var fn = Path.Combine(dir, PathUtil.CleanFileName(pk.FileName));
        File.WriteAllBytes(fn, pk.DecryptedPartyData);
        LogUtil.LogInfo("Dump", $"Saved file: {fn}");
    }

    public static void LogSuccessfulTrades(PokeTradeDetail<T> poke, ulong TrainerNID, string TrainerName)
    {
        // All users who traded, tracked by whether it was a targeted trade or distribution.
        if (poke.Type == PokeTradeType.Random)
            PreviousUsersDistribution.TryRegister(TrainerNID, TrainerName);
        else
            PreviousUsers.TryRegister(TrainerNID, TrainerName, poke.Trainer.ID);
    }

    public async Task CheckForRAMShiftingApps(CancellationToken token)
    {
        Log("Trainer data is not valid.");

        bool found = false;
        var msg = "";
        if (await SwitchConnection.IsProgramRunning(ovlloaderID, token).ConfigureAwait(false))
        {
            msg += "Found Tesla Menu";
            found = true;
        }

        if (await SwitchConnection.IsProgramRunning(dmntID, token).ConfigureAwait(false))
        {
            if (found)
                msg += " and ";
            msg += "dmnt (cheat codes?)";
            found = true;
        }
        if (found)
        {
            msg += ".";
            Log(msg);
            Log("Please remove interfering applications and reboot the Switch.");
        }
    }

    public abstract Task<T> ReadBoxPokemon(int box, int slot, CancellationToken token);

    public abstract Task<T> ReadPokemon(ulong offset, CancellationToken token);

    public abstract Task<T> ReadPokemon(ulong offset, int size, CancellationToken token);

    public abstract Task<T> ReadPokemonPointer(IEnumerable<long> jumps, int size, CancellationToken token);

    public async Task<T?> ReadUntilPresent(ulong offset, int waitms, int waitInterval, int size, CancellationToken token)
    {
        int msWaited = 0;
        while (msWaited < waitms)
        {
            var pk = await ReadPokemon(offset, size, token).ConfigureAwait(false);
            if (pk.Species != 0 && pk.ChecksumValid)
                return pk;
            await Task.Delay(waitInterval, token).ConfigureAwait(false);
            msWaited += waitInterval;
        }
        return null;
    }

    public async Task<T?> ReadUntilPresentPointer(IReadOnlyList<long> jumps, int waitms, int waitInterval, int size, CancellationToken token)
    {
        int msWaited = 0;
        while (msWaited < waitms)
        {
            var pk = await ReadPokemonPointer(jumps, size, token).ConfigureAwait(false);
            if (pk.Species != 0 && pk.ChecksumValid)
                return pk;
            await Task.Delay(waitInterval, token).ConfigureAwait(false);
            msWaited += waitInterval;
        }
        return null;
    }

    public async Task<bool> TryReconnect(int attempts, int extraDelay, SwitchProtocol protocol, CancellationToken token)
    {
        // USB can have several reasons for connection loss, some of which is not recoverable (power loss, sleep).
        // Only deal with Wi-Fi for now.
        if (protocol is SwitchProtocol.WiFi)
        {
            // If ReconnectAttempts is set to -1, this should allow it to reconnect (essentially) indefinitely.
            for (int i = 0; i < (uint)attempts; i++)
            {
                LogUtil.LogInfo($"Trying to reconnect... ({i + 1})", Connection.Label);
                Connection.Reset();
                if (Connection.Connected)
                    break;

                await Task.Delay(30_000 + extraDelay, token).ConfigureAwait(false);
            }
        }
        return Connection.Connected;
    }

    public async Task VerifyBotbaseVersion(CancellationToken token)
    {
        var data = await SwitchConnection.GetBotbaseVersion(token).ConfigureAwait(false);
        var version = decimal.TryParse(data, CultureInfo.InvariantCulture, out var v) ? v : 0;
        if (version < BotbaseVersion)
        {
            var protocol = Config.Connection.Protocol;
            var msg = protocol is SwitchProtocol.WiFi ? "sys-botbase" : "usb-botbase";
            msg += $" version is not supported. Expected version {BotbaseVersion} or greater, and current version is {version}. Please download the latest version from: ";
            if (protocol is SwitchProtocol.WiFi)
                msg += "https://github.com/olliz0r/sys-botbase/releases/latest";
            else
                msg += "https://github.com/Koi-3088/usb-botbase/releases/latest";
            throw new Exception(msg);
        }
    }

    // Tesla Menu
    // dmnt used for cheats
    // 确保引入必要命名空间（若未引入，需添加在文件头部）
    // using System;
    // using System.IO;
    // using System.TimeZoneInfo;

    protected PokeTradeResult CheckPartnerReputation(PokeRoutineExecutor<T> bot, PokeTradeDetail<T> poke, ulong TrainerNID, string TrainerName,
            TradeAbuseSettings AbuseSettings, CancellationToken token)
    {
        bool quit = false;
        var user = poke.Trainer;
        var isDistribution = poke.Type == PokeTradeType.Random;
        var list = isDistribution ? PreviousUsersDistribution : PreviousUsers;

        // 新增：封装“首字+***+尾字”的用户名处理逻辑（兼容不同长度用户名）
        string FormatUserName(string originalName)
        {
            // 先清洗空值和首尾空格
            string cleanName = string.IsNullOrEmpty(originalName) ? "" : originalName.Trim();
            if (string.IsNullOrEmpty(cleanName))
                return "未知用户"; // 空值兜底
            if (cleanName.Length == 1)
                return cleanName; // 1个字：直接返回
            if (cleanName.Length == 2)
                return $"{cleanName[0]}*{cleanName[1]}"; // 2个字：首字+*+尾字（如“张*三”）
                                                         // 3个字及以上：首字+***+尾字（如“张***三”“A***e”）
            return $"{cleanName[0]}***{cleanName[cleanName.Length - 1]}";
        }

        // 处理所有需要格式化的用户名
        string formattedTrainerName = FormatUserName(TrainerName);
        string formattedUserTrainerName = FormatUserName(user.TrainerName);
        string formattedPreviousName = string.Empty; // 后续按需赋值

        // Matches to a list of banned NIDs, in case the user ever manages to enter a trade.
        var entry = AbuseSettings.BannedIDs.List.Find(z => z.ID == TrainerNID);
        if (entry != null)
        {
            return PokeTradeResult.SuspiciousActivity;
        }

        // 获取北京时间（Windows系统专用时区ID，提取到外部，避免重复代码）
        TimeZoneInfo beijingTimeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        DateTime nowBeijing = TimeZoneInfo.ConvertTime(DateTime.Now, beijingTimeZone);
        string msg = string.Empty;

        // Check within the trade type (distribution or non-Distribution).
        var previous = list.TryGetPreviousNID(TrainerNID);
        if (previous != null)
        {
            var delta = DateTime.Now - previous.Time; // Time that has passed since last trade.
                                                      // 修改：使用格式化后的用户名
            Log($"Last traded with {formattedUserTrainerName} {delta.TotalMinutes:F1} minutes ago (OT: {formattedTrainerName}).");

            // Allows setting a cooldown for repeat trades. If the same user is encountered within the cooldown period for the same trade type, the user is warned and the trade will be ignored.
            var cd = AbuseSettings.TradeCooldown; // Time they must wait before trading again.

            if (cd != 0 && TimeSpan.FromMinutes(cd) > delta)
            {
                // 场景1：违反规则 - 下次连接时间=北京时间+60分钟（仅1小时惩罚）
                // 修改：使用格式化后的用户名
                DateTime nextConnectTime = nowBeijing.AddMinutes(60);
                msg = $"上次见到你是 {delta.TotalMinutes:F1} 分钟前（OT: {formattedTrainerName}）。\r\n你违反了规则（无视{cd}分钟交易冷却时间）。\r\n下次连接是 北京时间 {nextConnectTime:yyyy-MM-dd HH:mm:ss}\r\n（违规惩罚，需等待60分钟）。";
                // 写入msg2.txt
                File.WriteAllText("msg2.txt", $"{msg}{Environment.NewLine}");

                // 修改：使用格式化后的用户名
                Log($"Found {formattedUserTrainerName} ignoring the {cd} minute trade cooldown. Last encountered {delta.TotalMinutes:F1} minutes ago.");
                return PokeTradeResult.SuspiciousActivity;
            }

            // 场景2：未违反规则（非首次） - 下次连接时间=北京时间+原有CD时长（仅当CD>0时生成消息）
            if (cd != 0)
            {
                // 修改：使用格式化后的用户名
                DateTime nextConnectTime = nowBeijing.AddMinutes(cd);
                msg = $"上次见到你是 {delta.TotalMinutes:F1} 分钟前（OT: {formattedTrainerName}）。\r\n未违反交易规则，下次连接是\r\n北京时间 {nextConnectTime:yyyy-MM-dd HH:mm:ss}\r\n（需等待{cd}分钟冷却）。";
                // 写入msg2.txt
                File.WriteAllText("msg2.txt", $"{msg}{Environment.NewLine}");
            }

            // For distribution trades only, flag users using multiple Discord/Twitch accounts to send to the same in-game player within the TradeAbuseExpiration time limit.
            // This is usually to evade a ban or a trade cooldown.
            if (isDistribution && previous.NetworkID == TrainerNID && previous.RemoteID != user.ID)
            {
                if (delta < TimeSpan.FromMinutes(AbuseSettings.TradeAbuseExpiration))
                {
                    quit = true;
                    // 修改：格式化历史账号用户名
                    formattedPreviousName = FormatUserName(previous.Name);
                    Log($"Found {formattedUserTrainerName} using multiple accounts.\nPreviously traded with {formattedPreviousName} ({previous.RemoteID}) {delta.TotalMinutes:F1} minutes ago on OT: {formattedTrainerName}.");
                }
            }
        }
        else
        {
            // 场景3：首次遇到用户 - 生成专属消息，仅当CD>0时写入（无CD则不生成）
            var cd = AbuseSettings.TradeCooldown;
            if (cd != 0)
            {
                // 修改：使用格式化后的用户名
                DateTime nextConnectTime = nowBeijing.AddMinutes(cd);
                msg = $"这是首次见到你（OT: {formattedTrainerName}）。\r\n未违反交易规则，下次连接是 \r\n北京时间 {nextConnectTime:yyyy-MM-dd HH:mm:ss}\r\n（需等待{cd}分钟冷却）。";
                // 写入msg2.txt
                File.WriteAllText("msg2.txt", $"{msg}{Environment.NewLine}");
            }
        }

        if (quit)
            return PokeTradeResult.SuspiciousActivity;

        return PokeTradeResult.Success;
    }
    protected async Task<(bool, ulong)> ValidatePointerAll(IEnumerable<long> jumps, CancellationToken token)
    {
        var solved = await SwitchConnection.PointerAll(jumps, token).ConfigureAwait(false);
        return (solved != 0, solved);
    }
}
