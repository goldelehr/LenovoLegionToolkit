using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.System.Management;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.Controllers;

/// <summary>
/// Emulates the custom fan curve in software on machines where the BIOS refuses to enter the real
/// custom thermal mode (e.g. when the connected power adapter is not recognized as a full power adapter,
/// in which case SmartFanMode reports 255 but ThermalMode stays at Balance and the EC ignores Fan_Set_Table).
/// It periodically reads the CPU temperature and sets per-fan target speeds via SetFeatureValue,
/// which the EC honors regardless of the adapter type. Setting a target of 0 hands control back to the EC.
/// </summary>
public class SoftwareFanCurveController
{
    private const int GOD_MODE_SMART_FAN_MODE = 255;
    private const int PERFORMANCE_SMART_FAN_MODE = 3;
    private const int INTERVAL_MS = 2000;

    /// <summary>
    /// Level 0 in software mode. The EC cannot stop the fans: targets below ~1000 RPM make the
    /// motors stall and restart in a loop, so the lowest level maps to the lowest stable speed.
    /// </summary>
    public const int MINIMUM_STABLE_SPEED = 1100;

    /// <summary>
    /// Last known result of <see cref="DetectHardwareCustomModeBlockedAsync"/>. The BIOS only reveals
    /// whether it blocks custom mode while in Custom or Performance mode, so the answer is cached.
    /// </summary>
    public static bool IsHardwareCustomModeBlockedCached { get; private set; }
    private const int HYSTERESIS = 3;
    private const int EMERGENCY_TEMPERATURE = 90;
    private const int MAX_INVALID_READINGS = 3;

    private readonly Lock _lock = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _hasTargets;

    public bool IsActive => _loop is { IsCompleted: false };

    public SoftwareFanCurveController()
    {
        // Never leave the fans pinned to a fixed target when the process goes away.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseSync();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => ReleaseSync();
    }

    /// <summary>
    /// True when the machine is in Custom mode but the BIOS kept the EC in another thermal mode.
    /// </summary>
    public static async Task<bool> IsHardwareCustomModeBlockedAsync()
    {
        var smartFanMode = await WMI.LenovoGameZoneData.GetSmartFanModeAsync().ConfigureAwait(false);
        var thermalMode = await WMI.LenovoGameZoneData.GetThermalModeAsync().ConfigureAwait(false);
        var blocked = smartFanMode == GOD_MODE_SMART_FAN_MODE && thermalMode != (int)ThermalModeState.GodMode;

        if (smartFanMode == GOD_MODE_SMART_FAN_MODE)
            IsHardwareCustomModeBlockedCached = blocked;

        return blocked;
    }

    /// <summary>
    /// Like <see cref="IsHardwareCustomModeBlockedAsync"/>, but also infers the answer from Performance mode,
    /// which the BIOS gates on the same power adapter check. Falls back to the cached value otherwise.
    /// </summary>
    public static async Task<bool> DetectHardwareCustomModeBlockedAsync()
    {
        try
        {
            var smartFanMode = await WMI.LenovoGameZoneData.GetSmartFanModeAsync().ConfigureAwait(false);
            var thermalMode = await WMI.LenovoGameZoneData.GetThermalModeAsync().ConfigureAwait(false);

            if (smartFanMode == GOD_MODE_SMART_FAN_MODE)
                IsHardwareCustomModeBlockedCached = thermalMode != (int)ThermalModeState.GodMode;
            else if (smartFanMode == PERFORMANCE_SMART_FAN_MODE)
                IsHardwareCustomModeBlockedCached = thermalMode != (int)ThermalModeState.Performance;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to detect whether custom mode is blocked.", ex);
        }

        return IsHardwareCustomModeBlockedCached;
    }

    public async Task StartAsync(FanTable fanTable, FanTableData[] fanTableData)
    {
        var cpu = fanTableData.FirstOrDefault(d => d.Type is FanTableType.CPU);
        var gpu = fanTableData.FirstOrDefault(d => d.Type is FanTableType.GPU);

        if (cpu.FanSpeeds is not { Length: 10 } || cpu.Temps is not { Length: 10 })
        {
            Log.Instance.Trace($"Software fan curve not started, CPU fan table data missing.");
            return;
        }

        var levels = fanTable.GetTable();
        var temps = NormalizeTemps(cpu.Temps);
        var fan1Speeds = cpu.FanSpeeds;
        var fan2Speeds = gpu.FanSpeeds is { Length: 10 } ? gpu.FanSpeeds : cpu.FanSpeeds;

        await StopAsync().ConfigureAwait(false);

        Log.Instance.Trace($"Starting software fan curve. [levels={string.Join(",", levels)}, temps={string.Join(",", temps)}]");

        lock (_lock)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(levels, temps, fan1Speeds, fan2Speeds, token), token);
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_lock)
        {
            _cts?.Cancel();
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            Log.Instance.Trace($"Software fan curve stopped.");
        }

        await ReleaseAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(ushort[] levels, ushort[] temps, ushort[] fan1Speeds, ushort[] fan2Speeds, CancellationToken token)
    {
        var currentStep = -1;
        var invalidReadings = 0;
        int? lastFan1 = null, lastFan2 = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!await IsHardwareCustomModeBlockedAsync().ConfigureAwait(false))
                {
                    Log.Instance.Trace($"Custom mode left or hardware curve available, stopping software fan curve.");
                    break;
                }

                var temperature = await WMI.LenovoOtherMethod.GetFeatureValueAsync(CapabilityID.CpuCurrentTemperature).ConfigureAwait(false);

                if (temperature is <= 0 or > 110)
                {
                    if (++invalidReadings >= MAX_INVALID_READINGS && _hasTargets)
                    {
                        Log.Instance.Trace($"Invalid temperature readings, handing fans back to EC. [temperature={temperature}]");
                        await ReleaseAsync().ConfigureAwait(false);
                        lastFan1 = lastFan2 = null;
                    }

                    await Task.Delay(INTERVAL_MS, token).ConfigureAwait(false);
                    continue;
                }

                invalidReadings = 0;

                int fan1, fan2;
                if (temperature >= EMERGENCY_TEMPERATURE)
                {
                    fan1 = fan1Speeds.Max();
                    fan2 = fan2Speeds.Max();
                    currentStep = temps.Length - 1;
                }
                else
                {
                    currentStep = GetStep(temps, temperature, currentStep);
                    var level = Math.Clamp((int)levels[currentStep], 0, 10);
                    fan1 = level == 0 ? MINIMUM_STABLE_SPEED : fan1Speeds[level - 1];
                    fan2 = level == 0 ? MINIMUM_STABLE_SPEED : fan2Speeds[level - 1];
                }

                if (fan1 != lastFan1 || fan2 != lastFan2)
                {
                    Log.Instance.Trace($"Software fan curve: {temperature}°C -> step {currentStep}, fan1={fan1}, fan2={fan2}");
                    await WMI.LenovoOtherMethod.SetFeatureValueAsync(CapabilityID.CpuCurrentFanSpeed, fan1).ConfigureAwait(false);
                    await WMI.LenovoOtherMethod.SetFeatureValueAsync(CapabilityID.GpuCurrentFanSpeed, fan2).ConfigureAwait(false);
                    _hasTargets = true;
                    lastFan1 = fan1;
                    lastFan2 = fan2;
                }

                await Task.Delay(INTERVAL_MS, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Software fan curve failed.", ex);
        }
        finally
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Highest step whose threshold is reached. Moving down requires the temperature to drop
    /// <see cref="HYSTERESIS"/> degrees below the current step's threshold to avoid oscillation.
    /// </summary>
    private static int GetStep(ushort[] temps, int temperature, int currentStep)
    {
        var step = 0;
        for (var i = 0; i < temps.Length; i++)
        {
            if (temperature >= temps[i])
                step = i;
        }

        if (currentStep > step && temperature > temps[currentStep] - HYSTERESIS)
            return currentStep;

        return step;
    }

    /// <summary>
    /// The BIOS marks unused steps with 127°C. Spread them out above the last real threshold
    /// so that all ten points of the curve are usable in software mode.
    /// </summary>
    public static ushort[] NormalizeTemps(ushort[] temps)
    {
        var result = temps.ToArray();
        var lastValid = Array.FindLastIndex(result, t => t < 100);
        if (lastValid < 1 || lastValid == result.Length - 1)
            return result;

        var spacing = Math.Max(4, result[lastValid] - result[lastValid - 1]);
        for (var i = lastValid + 1; i < result.Length; i++)
            result[i] = (ushort)Math.Min(EMERGENCY_TEMPERATURE, result[i - 1] + spacing);

        return result;
    }

    private async Task ReleaseAsync()
    {
        if (!_hasTargets)
            return;

        try
        {
            await WMI.LenovoOtherMethod.SetFeatureValueAsync(CapabilityID.CpuCurrentFanSpeed, 0).ConfigureAwait(false);
            await WMI.LenovoOtherMethod.SetFeatureValueAsync(CapabilityID.GpuCurrentFanSpeed, 0).ConfigureAwait(false);
            _hasTargets = false;
            Log.Instance.Trace($"Fan targets released to EC.");
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to release fan targets.", ex);
        }
    }

    private void ReleaseSync()
    {
        try
        {
            _cts?.Cancel();
            ReleaseAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch { /* Ignore */ }
    }
}
