using System;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Overclocking.Amd;
using LenovoLegionToolkit.Lib.System.Management;
using LenovoLegionToolkit.Lib.Utils;
using ZenStates.Core;
using WMI = LenovoLegionToolkit.Lib.System.Management.WMI;

namespace LenovoLegionToolkit.Lib.Controllers;

/// <summary>
/// Applies the custom mode CPU power limits directly through the SMU, the way RyzenAdj / UXTU do.
/// The BIOS only forwards the custom power limits to the EC when it really entered the custom thermal mode,
/// which it refuses to do when the power adapter is not recognized as a full power adapter. Going through
/// the SMU also allows limits below the BIOS minimum.
/// Only enabled for CPUs where the mailbox messages are known and were verified (RyzenAdj: Strix Point/Halo).
/// </summary>
public class SmuPowerLimitController(AmdOverclockingController amdOverclockingController)
{
    // MP1 mailbox messages on Strix Point / Strix Halo, values in mW.
    private const uint MSG_SET_STAPM_LIMIT = 0x14;
    private const uint MSG_SET_FAST_LIMIT = 0x15;
    private const uint MSG_SET_SLOW_LIMIT = 0x16;

    private const int GOD_MODE_SMART_FAN_MODE = 255;

    // Power table offsets (RyzenAdj layout): 0 = STAPM limit, 2 = fast limit, 4 = slow limit, in W.
    private const int TABLE_STAPM_LIMIT = 0;
    private const int TABLE_FAST_LIMIT = 2;
    private const int TABLE_SLOW_LIMIT = 4;

    /// <summary>Lowest limit offered in the custom mode editor.</summary>
    public const int MINIMUM_WATTS = 5;

    /// <summary>The BIOS/EC may overwrite the limits (e.g. on AC events), so they are checked periodically.</summary>
    private static readonly TimeSpan WATCHDOG_INTERVAL = TimeSpan.FromSeconds(5);

    public readonly record struct Limits(int Stapm, int Slow, int Fast);

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _firmwareLimitsLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _hasApplied;
    private Limits? _firmwareLimits;

    /// <summary>
    /// SMU limits the firmware itself applies in the current mode. The EC translates the BIOS power limits
    /// into different SMU values (e.g. BIOS 70/81 W -> SMU slow/fast 60/71 W), so the BIOS defaults can't be used
    /// as a cap. Read once per process: the BIOS is asked to re-apply its limits first, so values left behind
    /// by a previous (killed) instance are not mistaken for the firmware's.
    /// </summary>
    public async Task<Limits?> GetFirmwareLimitsAsync()
    {
        await _firmwareLimitsLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_firmwareLimits is not null)
                return _firmwareLimits;

            // Only the custom mode's firmware limits are a meaningful cap (Quiet would be far too low).
            if (await WMI.LenovoGameZoneData.GetSmartFanModeAsync().ConfigureAwait(false) != GOD_MODE_SMART_FAN_MODE)
                return null;

            await StopLoopAsync().ConfigureAwait(false);
            await RestoreBiosLimitsAsync().ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            var cpu = amdOverclockingController.GetCpu();
            if (cpu.RefreshPowerTable() != SMU.Status.OK || cpu.powerTable?.Table is not { Length: > TABLE_SLOW_LIMIT } table)
                return null;

            // STAPM is moved around by skin temperature tracking; it never exceeds the slow limit.
            var slow = (int)Math.Floor(table[TABLE_SLOW_LIMIT]);
            var fast = (int)Math.Floor(table[TABLE_FAST_LIMIT]);
            _firmwareLimits = new Limits(slow, slow, fast);

            Log.Instance.Trace($"Firmware SMU limits: {_firmwareLimits} [stapm now={table[TABLE_STAPM_LIMIT]}W]");
            return _firmwareLimits;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to read firmware SMU limits.", ex);
            return null;
        }
        finally
        {
            _firmwareLimitsLock.Release();
        }
    }

    public async Task<bool> IsSupportedAsync()
    {
        try
        {
            await amdOverclockingController.InitializeAsync().ConfigureAwait(false);
            if (!amdOverclockingController.IsSupported())
                return false;

            var codeName = amdOverclockingController.GetCpu().info.codeName;
            return codeName is Cpu.CodeName.StrixHalo or Cpu.CodeName.StrixPoint;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"SMU power limits not supported.", ex);
            return false;
        }
    }

    /// <summary>
    /// Applies the limits (in W) and keeps them applied until <see cref="StopAsync"/> is called.
    /// </summary>
    public async Task ApplyAsync(int stapm, int slow, int fast)
    {
        await StopLoopAsync().ConfigureAwait(false);

        var cpu = amdOverclockingController.GetCpu();

        Log.Instance.Trace($"Applying SMU power limits. [stapm={stapm}W, slow={slow}W, fast={fast}W]");
        SetLimits(cpu, stapm, slow, fast);
        _hasApplied = true;

        lock (_lock)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => WatchdogAsync(cpu, stapm, slow, fast, token), token);
        }
    }

    /// <summary>
    /// Stops enforcing the limits. With <paramref name="restoreBiosLimits"/> the BIOS is asked to apply
    /// its own limits again; not needed when the power mode is about to change anyway.
    /// </summary>
    public async Task StopAsync(bool restoreBiosLimits = false)
    {
        await StopLoopAsync().ConfigureAwait(false);

        if (!_hasApplied)
            return;

        if (restoreBiosLimits)
            await RestoreBiosLimitsAsync().ConfigureAwait(false);
        else
            _hasApplied = false; // A power mode change is about to make the BIOS apply its own limits.
    }

    private async Task RestoreBiosLimitsAsync()
    {
        try
        {
            // Setting the current mode again makes the BIOS push its own limits to the EC/SMU.
            var mode = await WMI.LenovoGameZoneData.GetSmartFanModeAsync().ConfigureAwait(false);
            await WMI.LenovoGameZoneData.SetSmartFanModeAsync(mode).ConfigureAwait(false);
            _hasApplied = false;
            Log.Instance.Trace($"BIOS power limits restored.");
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to restore BIOS power limits.", ex);
        }
    }

    private async Task StopLoopAsync()
    {
        Task? loop;
        lock (_lock)
        {
            _cts?.Cancel();
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (loop is null)
            return;

        try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private static async Task WatchdogAsync(Cpu cpu, int stapm, int slow, int fast, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(WATCHDOG_INTERVAL, token).ConfigureAwait(false);

                if (cpu.RefreshPowerTable() != SMU.Status.OK || cpu.powerTable?.Table is not { Length: > TABLE_SLOW_LIMIT } table)
                    continue;

                // STAPM is adjusted dynamically by the firmware (skin temperature tracking), so only
                // the fast and slow limits tell whether someone else overwrote the values.
                if (Math.Abs(table[TABLE_FAST_LIMIT] - fast) < 1 && Math.Abs(table[TABLE_SLOW_LIMIT] - slow) < 1)
                    continue;

                Log.Instance.Trace($"SMU power limits were overwritten, re-applying. [fast={table[TABLE_FAST_LIMIT]}W, slow={table[TABLE_SLOW_LIMIT]}W]");
                SetLimits(cpu, stapm, slow, fast);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Instance.Trace($"SMU power limit watchdog failed.", ex);
        }
    }

    private static void SetLimits(Cpu cpu, int stapm, int slow, int fast)
    {
        Send(cpu, MSG_SET_STAPM_LIMIT, stapm);
        Send(cpu, MSG_SET_SLOW_LIMIT, slow);
        Send(cpu, MSG_SET_FAST_LIMIT, fast);
    }

    private static void Send(Cpu cpu, uint message, int watts)
    {
        var args = new uint[6];
        args[0] = (uint)Math.Max(MINIMUM_WATTS, watts) * 1000;
        var status = cpu.smu.SendSmuCommand(cpu.smu.Mp1Smu, message, ref args);
        if (status != SMU.Status.OK)
            Log.Instance.Trace($"SMU message 0x{message:X2} failed. [status={status}, watts={watts}]");
    }
}
